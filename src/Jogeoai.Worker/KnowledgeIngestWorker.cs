using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>领取并处理知识库资料摄取任务。</summary>
public sealed class KnowledgeIngestWorker(
    IKnowledgeStore store,
    IObjectStorage objectStorage,
    IDocumentTextExtractor extractor,
    IEmbeddingProvider embedding,
    IVectorIndex vectorIndex,
    IConfiguration configuration,
    IOptions<KnowledgeExtractionLimits> extractionLimits,
    ILogger<KnowledgeIngestWorker> logger) : BackgroundService
{
    /// <summary>摄取租约秒数的配置键。</summary>
    public const string LeaseSecondsKey = "Knowledge:Ingest:LeaseSeconds";
    /// <summary>未配置时的摄取租约秒数。</summary>
    public const int DefaultLeaseSeconds = 300;
    /// <summary>允许配置的最短摄取租约秒数。</summary>
    public const int MinLeaseSeconds = 30;
    /// <summary>允许配置的最长摄取租约秒数。</summary>
    public const int MaxLeaseSeconds = 3600;

    private readonly KnowledgeChunker chunker = new();
    private readonly string workerId = $"knowledge-{Environment.MachineName}-{Guid.NewGuid():N}";
    // 摄取没有心跳：一次「解析 + 分块 + 向量化 + 发布」必须在这一个租约内写回结果。
    private readonly TimeSpan lease = ResolveLease(configuration);

    /// <summary>读取摄取租约时长：未配置时为 300 秒；不是 30–3600 之间的整数时抛出异常，Worker 启动失败而不是静默改用其他值。</summary>
    /// <param name="configuration">应用配置。</param>
    /// <returns>每次领取摄取任务时建立的租约时长。</returns>
    public static TimeSpan ResolveLease(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[LeaseSecondsKey];
        if (string.IsNullOrWhiteSpace(raw)) return TimeSpan.FromSeconds(DefaultLeaseSeconds);
        if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < MinLeaseSeconds or > MaxLeaseSeconds)
            throw new InvalidOperationException(
                $"{LeaseSecondsKey} must be an integer between {MinLeaseSeconds} and {MaxLeaseSeconds}.");
        return TimeSpan.FromSeconds(seconds);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parseTimeoutSeconds = extractionLimits.Value.ParseTimeoutSeconds;
        if (lease.TotalSeconds <= parseTimeoutSeconds)
            // 解析超时不短于租约时，慢文件来不及在租约内记为 KNOWLEDGE_PARSE_TIMEOUT，只能靠重领上限在 3 次后失败。
            logger.LogWarning("Knowledge ingest lease ({LeaseSeconds}s) is not longer than the parse timeout ({ParseTimeoutSeconds}s); slow files will fail by the reclaim cap instead of KNOWLEDGE_PARSE_TIMEOUT.",
                (int)lease.TotalSeconds, parseTimeoutSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await store.ClaimIngestJobAsync(workerId, lease, stoppingToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                // 任务级异常已尽量写入受控错误；循环异常只记录任务 worker 标识。
                logger.LogError(exception, "Knowledge ingest worker iteration failed. WorkerId={WorkerId}", workerId);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(Jogeoai.Domain.KnowledgeIngestJobLease job, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await objectStorage.OpenReadAsync(job.ObjectKey, cancellationToken)
                ?? throw new KnowledgeExtractionException("KNOWLEDGE_OBJECT_MISSING", "源文件对象不存在。", true);
            var extracted = await extractor.ExtractAsync(stream, job.Extension, null, cancellationToken);
            await store.SaveExtractedDocumentAsync(job, extracted, cancellationToken);
            var chunks = chunker.Chunk(job.VersionId, extracted);
            if (chunks.Count > 0)
            {
                var vectors = await embedding.EmbedAsync(chunks.Select(item => item.Text).ToArray(), cancellationToken);
                if (vectors.Count != chunks.Count)
                    throw new InvalidOperationException("Embedding 返回数量与片段数量不一致。");
                await vectorIndex.UpsertAsync(job.TenantId, job.VersionId, chunks, vectors, cancellationToken);
            }
            // 图片版本没有片段；PublishIndexedVersionAsync 只对 content_kind=image 允许 0 片段发布。
            await store.PublishIndexedVersionAsync(job, chunks, cancellationToken);
        }
        catch (KnowledgeExtractionException exception)
        {
            await store.FailIngestJobAsync(job, exception.Code, exception.Message, exception.Retryable, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            await store.FailIngestJobAsync(job, "KNOWLEDGE_INDEX_FAILED", "资料索引服务暂时不可用。", true, cancellationToken);
            logger.LogWarning(exception, "Knowledge ingest task failed. JobId={JobId} TenantId={TenantId}", job.JobId, job.TenantId);
        }
    }
}
