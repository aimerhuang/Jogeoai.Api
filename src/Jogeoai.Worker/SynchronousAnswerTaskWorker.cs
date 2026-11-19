using System.Diagnostics;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Domestic;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>
/// 同步 API 回答 Provider 的任务循环：按 Provider 领取 provider_tasks，同步调用一次外部接口；成功即一次事务写入结果、积分与用量，
/// 失败按 <see cref="AnswerProviderException"/> 的错误码重试、延后或终态。子类只提供 Provider、调用委托、限流码与通用失败码。
/// </summary>
/// <typeparam name="TOptions">运行参数类型。</typeparam>
public abstract class SynchronousAnswerTaskWorker<TOptions> : BackgroundService where TOptions : ApiAnswerWorkerOptions
{
    private readonly ILogger logger;
    private readonly ICloroVisibilityStore store;
    private readonly IOptions<TOptions> options;
    private readonly string provider;
    private readonly string name;
    private readonly string rateLimitedCode;
    private readonly string requestFailedCode;
    private readonly string workerId;

    /// <summary>创建任务循环。</summary>
    /// <param name="logger">日志；只记录任务标识、错误码与耗时，不记录正文、问题或凭据。</param>
    /// <param name="store">共享的 provider_tasks / visibility_checks 存储。</param>
    /// <param name="options">运行参数。</param>
    /// <param name="definition">Provider 定义（决定领取哪个 provider 的任务）。</param>
    /// <param name="name">日志中使用的 Provider 名称，如 Doubao。</param>
    /// <param name="workerIdPrefix">Worker 标识前缀，如 doubao。</param>
    /// <param name="rateLimitedCode">限流错误码，按退避序列重试。</param>
    /// <param name="requestFailedCode">未预期异常时记录的可重试错误码。</param>
    protected SynchronousAnswerTaskWorker(ILogger logger, ICloroVisibilityStore store, IOptions<TOptions> options,
        AnswerProviderDefinition definition, string name, string workerIdPrefix, string rateLimitedCode, string requestFailedCode)
    {
        this.logger = logger;
        this.store = store;
        this.options = options;
        provider = definition.Provider;
        this.name = name;
        this.rateLimitedCode = rateLimitedCode;
        this.requestFailedCode = requestFailedCode;
        workerId = $"{workerIdPrefix}-{Environment.MachineName}-{Guid.NewGuid():N}";
    }

    /// <summary>向外部 Provider 提交租到的任务并返回规范化回答；失败时抛出 <see cref="AnswerProviderException"/>。</summary>
    /// <param name="task">已租到的任务（含问题、区域语言、国家与任务标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    protected abstract Task<CloroAnswerSnapshot> AskAsync(CloroTaskLease task, CancellationToken cancellationToken);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var gate = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrency));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var recovered = await store.RecoverExpiredTasksAsync(provider, stoppingToken);
                if (recovered > 0) logger.LogInformation("Recovered {Count} expired {Provider} task leases.", recovered, name);
                var tasks = await store.LeaseTasksAsync(workerId, Math.Clamp(settings.BatchSize, 1, 10),
                    Math.Clamp(settings.LeaseSeconds, 10, 900), provider, stoppingToken);
                await Task.WhenAll(tasks.Select(task => ProcessGatedAsync(gate, task, stoppingToken)));
                if (tasks.Count == 0)
                    await Task.Delay(settings.IdleDelay > TimeSpan.Zero ? settings.IdleDelay : TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning("{Provider} task loop failed with {ExceptionType}.", name, exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessGatedAsync(SemaphoreSlim gate, CloroTaskLease task, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await ProcessAsync(task, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task ProcessAsync(CloroTaskLease task, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await store.MarkSubmittingAsync(task, workerId, cancellationToken);
            var answer = await AskAsync(task, cancellationToken);
            await store.CompleteSynchronousTaskAsync(task, workerId, answer, stopwatch.ElapsedMilliseconds,
                ExternalId(task, answer.RawOutput), cancellationToken);
            logger.LogInformation("{Provider} task {TaskId} completed in {DurationMs} ms.", name, task.TaskId, stopwatch.ElapsedMilliseconds);
        }
        catch (AnswerProviderException exception) when (exception.DeferMinutes is { } minutes)
        {
            await DeferOrFailAsync(task, exception.Code, minutes, cancellationToken);
        }
        catch (AnswerProviderException exception)
        {
            await FailAsync(task, exception.Code, exception.Retryable, stopwatch.ElapsedMilliseconds, cancellationToken);
        }
        catch (CollectorStoreException exception) when (exception.Code == "STALE_LEASE")
        {
            // Another worker recovered this task, or the lease expired during the call; its fence makes this attempt a no-op.
            logger.LogDebug("{Provider} task lease was fenced for task {TaskId}.", name, task.TaskId);
        }
        catch (CollectorStoreException)
        {
            // Database failures leave the leased task recoverable; never translate them into a Provider failure.
            throw;
        }
        catch (System.Data.Common.DbException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("{Provider} task {TaskId} failed with {ExceptionType}.", name, task.TaskId, exception.GetType().Name);
            await FailAsync(task, requestFailedCode, true, stopwatch.ElapsedMilliseconds, cancellationToken);
        }
    }

    // The attempt count already includes the current lease; retrying stops once the per-code limit is reached.
    private async Task FailAsync(CloroTaskLease task, string code, bool retryable, long durationMs, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        TimeSpan? retryAfter = null;
        if (retryable)
        {
            if (code == rateLimitedCode)
            {
                retryable = task.AttemptCount < Math.Max(1, settings.RateLimitMaxAttempts);
                var backoff = settings.RateLimitBackoff is { Length: > 0 } sequence ? sequence : [TimeSpan.FromSeconds(30)];
                retryAfter = backoff[Math.Clamp(task.AttemptCount - 1, 0, backoff.Length - 1)];
            }
            else retryable = task.AttemptCount < Math.Max(1, settings.MaxRetryAttempts);
        }
        logger.LogWarning("{Provider} task {TaskId} failed with {Code} after {DurationMs} ms (retryable: {Retryable}).",
            name, task.TaskId, code, durationMs, retryable);
        await store.FailTaskAsync(task, workerId, code, retryable, retryAfter, cancellationToken);
    }

    // Quota, overdue or insufficient balance: keep the user reservation and wait (DeferMinutes per attempt) up to MaxDeferWait,
    // then fail terminally so FailTaskAsync releases the reservation. Mirrors the Cloro exhausted-pool rule.
    private async Task DeferOrFailAsync(CloroTaskLease task, string code, int deferMinutes, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = DateTimeOffset.UtcNow;
        var since = task.PoolExhaustedSince ?? now;
        var maxWait = settings.MaxDeferWait > TimeSpan.Zero ? settings.MaxDeferWait : TimeSpan.FromHours(2);
        if (now - since >= maxWait)
        {
            logger.LogWarning("{Provider} task {TaskId} waited {Minutes} minutes for quota and is failed.", name, task.TaskId, (int)(now - since).TotalMinutes);
            await store.FailTaskAsync(task, workerId, code, false, cancellationToken: cancellationToken);
            return;
        }
        var next = now.AddMinutes(Math.Max(1, deferMinutes));
        if (since + maxWait < next) next = since + maxWait;
        if (next < now.AddSeconds(5)) next = now.AddSeconds(5);
        logger.LogWarning("{Provider} task {TaskId} deferred with {Code} until {NextAttemptAt}.", name, task.TaskId, code, next);
        await store.DeferTaskAsync(task, workerId, code, next, creditsExhausted: true, cancellationToken);
    }

    // The provider response id is the external reference; without one the local task id keeps the (provider, external_task_id) key unique.
    private static string ExternalId(CloroTaskLease task, JsonElement rawOutput)
    {
        if (rawOutput.ValueKind == JsonValueKind.Object && rawOutput.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 255 } value && !string.IsNullOrWhiteSpace(value))
            return value;
        return $"local:{task.TaskId:N}";
    }
}
