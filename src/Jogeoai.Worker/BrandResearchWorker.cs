using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Worker;

/// <summary>执行可恢复的官网研究；续期租约，取消任务时停止网络及模型调用。</summary>
public sealed class BrandResearchWorker(
    IResearchStore store,
    IEnumerable<IResearchProvider> providers,
    IHttpClientFactory clients,
    ILogger<BrandResearchWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = $"research-{Guid.NewGuid():N}";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await store.LeaseAsync(workerId, LeaseDuration, stoppingToken);
                if (job is null) { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogWarning("品牌研究轮询失败，将稍后重试。");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(ResearchWorkItem job, CancellationToken stoppingToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        budget.CancelAfter(TimeSpan.FromMinutes(8));
        var token = budget.Token;
        var lostLease = false;
        var context = job.Research ?? ResearchContextRules.Create(job.Request);
        async Task CheckLease()
        {
            if (!await store.RenewLeaseAsync(job.JobId, job.LeaseOwner, LeaseDuration, token))
            { lostLease = true; await budget.CancelAsync(); token.ThrowIfCancellationRequested(); }
        }
        async Task Save(ResearchContext progress, CancellationToken ct)
        {
            context = progress;
            if (!await store.SaveProgressAsync(job.JobId, job.LeaseOwner, context, ct))
            { lostLease = true; await budget.CancelAsync(); token.ThrowIfCancellationRequested(); }
        }
        async Task Heartbeat()
        {
            try
            {
                while (!token.IsCancellationRequested)
                { await Task.Delay(TimeSpan.FromSeconds(20), token); await CheckLease(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception) { lostLease = true; await budget.CancelAsync(); }
        }
        Task heartbeat = Task.CompletedTask;
        try
        {
            await CheckLease();
            heartbeat = Heartbeat();
            if (job.AttemptCount > 3)
            {
                await store.FailAsync(job.JobId, job.LeaseOwner, "RESEARCH_RETRY_LIMIT", "研究任务多次中断，请重新生成。", false, token);
                return;
            }
            if (context.Profile is null && !string.IsNullOrWhiteSpace(job.Request.Website)
                && !context.Evidence.Any(e => e.Url is not null))
            {
                await Save(context with { Phase = "collecting" }, token);
                var evidence = await new BrandWebsiteCollector(clients).CollectAsync(job.Request.Website, job.Request.Language, token);
                // 官网证据与品牌知识条目、用户补充并列；只替换网页证据。
                context = context with { Evidence = [.. evidence, .. context.Evidence.Where(e => e.Id == "user" || ResearchContextRules.IsKnowledgeEvidence(e))] };
                await Save(context, token);
            }
            if (context.Evidence.Count == 0)
                throw new ResearchNeedsInputException("官网暂时无法读取或没有足够业务资料，请补充一句主营业务和重点产品。");
            var configs = await store.ListProviderSettingsAsync(token);
            foreach (var metadata in configs.Where(x => x.Enabled && x.CredentialConfigured).OrderBy(x => x.Priority))
            {
                var config = await store.GetProviderConfigurationAsync(metadata.Provider, token);
                var provider = providers.FirstOrDefault(x => x.Provider.Equals(metadata.Provider, StringComparison.OrdinalIgnoreCase)
                    || x.Provider == "openai-compatible" && metadata.Provider is "openai-api" or "openrouter");
                if (config is null || provider is null) continue;
                try
                {
                    var result = await provider.RunAsync(config, job.Request, context, Save, token);
                    await CheckLease();
                    await store.CompleteAsync(job.JobId, job.LeaseOwner, result, token);
                    return;
                }
                catch (Exception exception) when (!token.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException or JsonException)
                { logger.LogWarning("品牌研究 Provider {Provider} 本次调用不可用。", metadata.Provider); }
            }
            await store.FailAsync(job.JobId, job.LeaseOwner, "RESEARCH_PROVIDER_UNAVAILABLE", "AI 研究暂时不可用，请稍后重试或手动添加。", job.AttemptCount < 3, token);
        }
        catch (ResearchNeedsInputException exception)
        {
            await Save(context with { Phase = "needs_input", Profile = null }, token);
            await store.FailAsync(job.JobId, job.LeaseOwner, "RESEARCH_NEEDS_INPUT", exception.Message, false, token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || lostLease) { }
        catch (Exception exception)
        {
            if (!lostLease && !stoppingToken.IsCancellationRequested)
                await store.FailAsync(job.JobId, job.LeaseOwner,
                    exception is InvalidOperationException ? "RESEARCH_QUALITY_INSUFFICIENT" : "RESEARCH_FAILED",
                    exception is InvalidOperationException ? "符合行业和证据要求的提问不足，请补充业务信息或重试。" : "研究耗时过长或暂时失败，请稍后重试。",
                    exception is not InvalidOperationException && job.AttemptCount < 3, stoppingToken);
        }
        finally { await budget.CancelAsync(); await heartbeat; }
    }
}
