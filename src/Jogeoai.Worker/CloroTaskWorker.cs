using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>从数据库领取并恢复 Cloro Submit/Poll 任务。</summary>
public sealed class CloroTaskWorker(
    ILogger<CloroTaskWorker> logger,
    ICloroVisibilityStore store,
    ICloroTaskClient client,
    IOptions<CloroAdapterOptions> options,
    ICloroKeyPool keyPool,
    CloroKeyCreditsRefresher creditsRefresher,
    IOptions<CloroKeyPoolOptions> poolOptions) : BackgroundService
{
    private readonly string workerId = $"cloro-{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var recovered = await store.RecoverExpiredTasksAsync(AnswerProviderDefinition.Cloro.Provider, stoppingToken);
                if (recovered > 0) logger.LogInformation("Recovered {Count} expired Cloro task leases.", recovered);
                var tasks = await store.LeaseTasksAsync(workerId, 10, 90, AnswerProviderDefinition.Cloro.Provider, stoppingToken);
                foreach (var task in tasks) await ProcessAsync(task, stoppingToken);
                if (tasks.Count == 0) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning("Cloro task loop failed with {ExceptionType}.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(Jogeoai.Domain.CloroTaskLease task, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(task.ExternalTaskId))
            {
                await store.MarkSubmittingAsync(task, workerId, cancellationToken);
                var submitted = await client.SubmitAsync(new CloroTaskRequest(task.Engine, task.Prompt, task.Locale, task.Country, task.TaskId), cancellationToken);
                await store.RecordSubmissionAsync(task, workerId, submitted, cancellationToken);
                return;
            }

            var snapshot = await client.GetAsync(task.ExternalTaskId, task.CloroKeyId, cancellationToken);
            if (snapshot.Status is "FAILED" or "ERROR")
            {
                if (await TryResubmitForCreditsAsync(task, snapshot, cancellationToken)) return;
                await store.FailTaskAsync(task, workerId, snapshot.ErrorCode ?? "CLORO_TASK_FAILED", false, cancellationToken: cancellationToken);
                return;
            }
            if (snapshot.Status is "COMPLETED" or "SUCCESS")
            {
                var answer = client.Normalize(snapshot, task.Engine);
                var taskStartedAt = task.SubmittedAt ?? task.StartedAt ?? task.CreatedAt;
                await store.CompleteTaskAsync(task, workerId, answer,
                    (int)Math.Clamp((DateTimeOffset.UtcNow - taskStartedAt).TotalMilliseconds, 0, int.MaxValue), cancellationToken);
                return;
            }

            // Always perform one final provider check before applying a local
            // deadline. The worker may have been stopped while Cloro continued
            // the external task, so a completed result must still be accepted
            // after the local deadline has elapsed.
            if (GetTimeoutCode(task) is { } timeoutCode)
            {
                await store.FailTaskAsync(task, workerId, timeoutCode, false, cancellationToken: cancellationToken);
                return;
            }
            await store.RecordPollAsync(task, workerId, snapshot, TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (CloroProviderException exception) when (string.IsNullOrWhiteSpace(task.ExternalTaskId)
            && exception.Code is "CLORO_KEY_POOL_EXHAUSTED" or "CLORO_KEY_POOL_BUSY" or "CLORO_KEY_POOL_UNAVAILABLE")
        {
            await DeferOrFailAsync(task, exception, cancellationToken);
        }
        catch (CloroProviderException exception)
        {
            var timeoutCode = exception.Retryable && !string.IsNullOrWhiteSpace(task.ExternalTaskId)
                ? GetTimeoutCode(task) : null;
            await store.FailTaskAsync(task, workerId, timeoutCode ?? exception.Code,
                timeoutCode is null && exception.Retryable && !exception.SubmissionUnknown, cancellationToken: cancellationToken);
        }
        catch (HttpRequestException)
        {
            var code = string.IsNullOrWhiteSpace(task.ExternalTaskId) ? "CLORO_SUBMIT_FAILED" : "CLORO_POLL_FAILED";
            var timeoutCode = string.IsNullOrWhiteSpace(task.ExternalTaskId) ? null : GetTimeoutCode(task);
            await store.FailTaskAsync(task, workerId, timeoutCode ?? code, timeoutCode is null, cancellationToken: cancellationToken);
        }
        catch (CollectorStoreException exception) when (exception.Code == "STALE_LEASE")
        {
            // Another worker recovered or completed this task; its lease fenced this worker.
            logger.LogDebug("Cloro task lease was fenced for task {TaskId}.", task.TaskId);
        }
        catch (CollectorStoreException)
        {
            // Database failures must leave the transaction-owned task recoverable; do not mark it as a Provider failure.
            throw;
        }
        catch (System.Data.Common.DbException)
        {
            // Preserve the durable stage: with an external ID, lease recovery resumes polling.
            // A submitting task without an ID instead recovers as submission_unknown, never resubmits.
            throw;
        }
        catch (InvalidOperationException exception)
        {
            var code = exception.Message.Contains("configured", StringComparison.OrdinalIgnoreCase) ? "CLORO_NOT_CONFIGURED" : "INVALID_PROVIDER_RESPONSE";
            await store.FailTaskAsync(task, workerId, code, false, cancellationToken: cancellationToken);
        }
        catch (Exception _) when (_ is not OperationCanceledException)
        {
            await store.FailTaskAsync(task, workerId, "CLORO_SUBMISSION_UNKNOWN", false, cancellationToken: cancellationToken);
        }
    }

    // The pool was empty before any request was sent: wait (keeping the user reservation). Only running out
    // of credits counts toward the maximum wait, after which FailTaskAsync releases the reservation; a busy
    // (429 cooldown) or unreadable pool just waits for the next attempt.
    private async Task DeferOrFailAsync(CloroTaskLease task, CloroProviderException exception, CancellationToken cancellationToken)
    {
        var pool = poolOptions.Value;
        var now = DateTimeOffset.UtcNow;
        var exhausted = exception.Code == "CLORO_KEY_POOL_EXHAUSTED";
        var since = task.PoolExhaustedSince ?? now;
        var maxWait = pool.PoolExhaustedMaxWait > TimeSpan.Zero ? pool.PoolExhaustedMaxWait : TimeSpan.FromHours(2);
        if (exhausted && now - since >= maxWait)
        {
            logger.LogWarning("Cloro task {TaskId} waited {Minutes} minutes for the key pool and is failed.",
                task.TaskId, (int)(now - since).TotalMinutes);
            await store.FailTaskAsync(task, workerId, exception.Code, false, cancellationToken: cancellationToken);
            return;
        }
        var interval = pool.PoolExhaustedRetryInterval > TimeSpan.Zero ? pool.PoolExhaustedRetryInterval : TimeSpan.FromMinutes(10);
        var next = now + interval;
        if (exception.NextAttemptAt is { } recovery && recovery < next) next = recovery;
        if (exhausted && since + maxWait < next) next = since + maxWait;
        if (next < now.AddSeconds(5)) next = now.AddSeconds(5);
        await store.DeferTaskAsync(task, workerId, exception.Code, next, exhausted, cancellationToken);
    }

    // R-04: a queued task that FAILED because its account ran out of credits is resubmitted with another
    // account. Cloro does not bill FAILED tasks, so this cannot double-charge; unconfirmed causes stay terminal.
    private async Task<bool> TryResubmitForCreditsAsync(CloroTaskLease task, CloroTaskStatusSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var pool = poolOptions.Value;
        if (task.CreditResubmitCount >= pool.MaxCreditResubmitsPerTask) return false;
        var key = await keyPool.GetAsync(task.CloroKeyId, cancellationToken);
        if (key is null) return false;
        if (snapshot.ProviderFailureCode == "INSUFFICIENT_CREDITS")
        {
            await keyPool.MarkRejectedAsync(key.KeyId, task.TaskId, CloroKeyRejection.InsufficientCredits,
                "INSUFFICIENT_CREDITS", cancellationToken);
        }
        else
        {
            // No reason in the response: confirm with a fresh balance; refresh writes the exhausted state itself.
            var credits = await creditsRefresher.RefreshAsync(key, cancellationToken);
            if (credits?.Remaining is not { } remaining || remaining > pool.LowBalanceThreshold) return false;
        }
        var reset = await store.ResetForResubmissionAsync(task, workerId, pool.MaxCreditResubmitsPerTask, cancellationToken);
        if (reset)
            logger.LogInformation("Cloro task {TaskId} failed for insufficient credits on account {AccountLabel}; resubmitting.",
                task.TaskId, key.AccountLabel);
        return reset;
    }

    private string? GetTimeoutCode(CloroTaskLease task)
    {
        var now = DateTimeOffset.UtcNow;
        var totalTimeout = options.Value.TotalTimeout > TimeSpan.Zero ? options.Value.TotalTimeout : TimeSpan.FromHours(1);
        var generationTimeout = options.Value.GenerationTimeout > TimeSpan.Zero ? options.Value.GenerationTimeout : TimeSpan.FromMinutes(10);
        if (task.SubmittedAt is { } submittedAt && now - submittedAt >= totalTimeout) return "CLORO_POLL_TIMEOUT";
        if (task.StartedAt is { } startedAt && now - startedAt >= generationTimeout) return "CLORO_GENERATION_TIMEOUT";
        return null;
    }
}
