using Jogeoai.Application;
using Jogeoai.Infrastructure;

namespace Jogeoai.Worker;

/// <summary>领取并处理知识库资料摄取任务。</summary>
public sealed class KnowledgeIngestWorker(
    IKnowledgeStore store,
    IObjectStorage objectStorage,
    IDocumentTextExtractor extractor,
    IEmbeddingProvider embedding,
    IVectorIndex vectorIndex,
    ILogger<KnowledgeIngestWorker> logger) : BackgroundService
{
    private readonly KnowledgeChunker chunker = new();
    private readonly string workerId = $"knowledge-{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await store.ClaimIngestJobAsync(workerId, TimeSpan.FromMinutes(2), stoppingToken);
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
