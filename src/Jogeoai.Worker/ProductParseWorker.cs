using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Worker;

/// <summary>领取产品信息识别任务：读取来源 → 调用 Luna 多模态识别 → 写入待核验草稿。</summary>
public sealed class ProductParseWorker(
    IKnowledgeProductStore store,
    IKnowledgeProductSourceReader reader,
    IProductParseProvider provider,
    IResearchStore research,
    ILogger<ProductParseWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string workerId = $"product-parse-{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await store.ClaimParseJobAsync(workerId, Lease, stoppingToken);
                if (job is null) { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // 任务级错误已写入任务表；这里只记录循环异常类型与 worker 标识，不记录资料正文。
                logger.LogError(exception, "Product parse worker iteration failed. WorkerId={WorkerId}", workerId);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(ProductParseJobLease job, CancellationToken stoppingToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var leaseLost = false;
        var heartbeat = MaintainLeaseAsync(job, execution, () => leaseLost = true, stoppingToken);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await store.MarkStepAsync(job, "read_source", null, 0, execution.Token);
            var input = await reader.ReadAsync(job, execution.Token);
            var summary = JsonSerializer.Serialize(new
            {
                kind = input.Kind.ToString().ToLowerInvariant(),
                textCharacters = input.SourceText?.Length ?? 0,
                imageCount = input.Images.Count,
                imageBytes = input.Images.Sum(image => (long)image.Bytes.Length),
                brandEvidenceCount = input.BrandEvidence.Count,
                filledFields = new[]
                {
                    input.Current.Introduction, input.Current.Advantages, input.Current.Scenarios, input.Current.Cases
                }.Count(value => !string.IsNullOrWhiteSpace(value)) + (input.Current.Specs is { Count: > 0 } ? 1 : 0),
                truncated = input.Truncated,
            }, JsonOptions);
            await store.MarkStepAsync(job, "model", summary, input.Images.Count, execution.Token);

            var configuration = await research.GetProviderConfigurationAsync("openai-compatible", execution.Token)
                ?? await research.GetProviderConfigurationAsync("openai-api", execution.Token);
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.Credential))
                throw new ProductParseException("PRODUCT_PARSE_PROVIDER_UNAVAILABLE", "识别 Provider 未配置。", false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.TimeoutSeconds <= 0 ? 300 : configuration.TimeoutSeconds, 1, 300)));
            ProductParseResult result;
            try
            {
                result = await provider.ParseAsync(input, configuration, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !execution.IsCancellationRequested)
            {
                throw new ProductParseException("PRODUCT_PARSE_RETRYABLE", "模型识别超时，请稍后重试。", true);
            }
            await store.MarkStepAsync(job, "finalize", null, input.Images.Count, execution.Token);
            await store.CompleteParseAsync(job, result, stoppingToken);
            logger.LogInformation("Product parse job {JobId} completed. Tenant={TenantId} Kind={Kind} Model={Model} Images={ImageCount} TextChars={TextChars} DurationMs={DurationMs}",
                job.JobId, job.TenantId, input.Kind, result.ModelName, input.Images.Count, input.SourceText?.Length ?? 0, started.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (leaseLost)
        {
            logger.LogWarning("Product parse job {JobId} lease was fenced", job.JobId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (ProductParseException exception)
        {
            await TryFailAsync(job, exception.Code, exception.Message, exception.Retryable, stoppingToken);
        }
        catch (CollectorStoreException exception) when (exception.Code == "PRODUCT_PARSE_STALE_LEASE")
        {
            logger.LogWarning("Product parse job {JobId} lease was fenced", job.JobId);
        }
        catch (CollectorStoreException exception)
        {
            await TryFailAsync(job, exception.Code, exception.Message, exception.StatusCode >= 500, stoppingToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Product parse provider request failed for job {JobId}: {Type}", job.JobId, exception.GetType().Name);
            await TryFailAsync(job, "PRODUCT_PARSE_RETRYABLE", "识别服务暂时不可用，请稍后重试。", true, stoppingToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Product parse job {JobId} failed: {Type}", job.JobId, exception.GetType().Name);
            await TryFailAsync(job, "PRODUCT_PARSE_FAILED", "识别失败，请稍后重试。", false, stoppingToken);
        }
        finally
        {
            execution.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogWarning(exception, "Product parse heartbeat stopped for job {JobId}", job.JobId); }
        }
    }

    private async Task MaintainLeaseAsync(ProductParseJobLease job, CancellationTokenSource execution, Action markLeaseLost,
        CancellationToken stoppingToken)
    {
        try
        {
            while (!execution.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, execution.Token);
                if (execution.IsCancellationRequested) break;
                if (!await store.HeartbeatAsync(job, Lease, stoppingToken))
                {
                    markLeaseLost();
                    execution.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || execution.IsCancellationRequested) { }
        catch (Exception)
        {
            // 心跳失败按租约丢失处理，避免另一 Worker 领取后本 Worker 仍写入结果。
            markLeaseLost();
            execution.Cancel();
        }
    }

    private async Task TryFailAsync(ProductParseJobLease job, string code, string message, bool retryable, CancellationToken cancellationToken)
    {
        try { await store.FailParseAsync(job, code, message, retryable, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogError(exception, "Failed to persist product parse failure for job {JobId}", job.JobId); }
    }
}
