using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>文章创作第 1 步准备与生成提交：自动补齐旧向导字段，复用现有素材预览、知识冻结与预占提交路径。</summary>
public sealed class ArticleStudioService(
    IManagementStore management,
    ICreditStore credits,
    IResearchStore research,
    IArticleMaterialSelectionService materials,
    IContentStore content,
    IArticleStudioStore studio,
    IAnalyticsQueryStore analytics,
    IKnowledgeRetrievalService knowledgeRetrieval,
    IArticleProductResolver? products = null,
    TimeProvider? clock = null) : IArticleStudioService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<ArticleStudioPrepareView> PrepareAsync(Guid tenantId, Guid siteId, Guid userId, bool canWrite,
        Guid? promptId, CancellationToken cancellationToken = default)
    {
        _ = await management.GetBrandAsync(tenantId, siteId, cancellationToken)
            ?? throw new CollectorStoreException("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
        var prompts = (await management.ListPromptsAsync(tenantId, siteId, null, null, null, 100, cancellationToken)).Items
            .Where(prompt => prompt.Enabled && prompt.ArchivedAt is null).ToArray();
        var groups = (await management.ListPromptGroupsAsync(tenantId, siteId, null, null, null, 100, cancellationToken)).Items
            .ToDictionary(group => group.GroupId, group => group.Name);
        var options = prompts.Select(prompt => new ArticleStudioPromptOption(prompt.PromptId, prompt.Content, prompt.BrandedStatus,
            prompt.Locale, prompt.GroupId, prompt.GroupId is { } group && groups.TryGetValue(group, out var name) ? name : null)).ToArray();
        var account = await credits.GetAccountAsync(tenantId, cancellationToken);
        var providerConfigured = (await research.ListProviderSettingsAsync(cancellationToken)).Any(item => item.Enabled
            && item.CredentialConfigured && string.Equals(item.Provider, ArticleGenerationProvider.ProviderName, StringComparison.OrdinalIgnoreCase));
        ArticleStudioSelectedPromptView? selected = null;
        if (promptId is { } id)
        {
            var prompt = await ResolvePromptAsync(tenantId, siteId, id, cancellationToken);
            var language = ArticleStudioRules.LanguageFor(prompt.Locale);
            var now = time.GetUtcNow();
            var performance = await ReadPerformanceAsync(tenantId, siteId, prompt.PromptId, now, cancellationToken);
            ArticleStudioKnowledgeView? knowledge = null;
            if (canWrite)
            {
                var preview = await AutoMaterialPreviewAsync(tenantId, siteId, userId, prompt.PromptId, language, cancellationToken);
                knowledge = new ArticleStudioKnowledgeView(preview.MaterialPreviewId, preview.WithoutRag ? 0 : preview.HitCount,
                    preview.WithoutRag, preview.ExpiresAt);
            }
            var engineCount = 0;
            try
            {
                engineCount = (await analytics.TopCitedUrlsForPromptAsync(tenantId, siteId, prompt.PromptId, now.AddDays(-30), now, 1,
                    cancellationToken)).EngineCount;
            }
            catch (CollectorStoreException) { engineCount = 0; }
            var existing = await studio.GetLatestStudioArticleAsync(tenantId, siteId, prompt.PromptId, cancellationToken);
            selected = new ArticleStudioSelectedPromptView(prompt.PromptId, prompt.Content, prompt.BrandedStatus, language,
                performance.Goal, performance.Basis, performance.BrandMentions, performance.EligibleChecks, knowledge,
                performance.Competitors, engineCount, existing);
        }
        return new ArticleStudioPrepareView(options, selected, ArticleGenerationPricing.UnitPrice, account.AvailableCredits,
            providerConfigured, ArticleStudioLimits.RequirementsMaxLength);
    }

    /// <inheritdoc />
    public async Task<ArticleStudioGenerationAccepted> SubmitAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleStudioGenerationRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new CollectorStoreException("IDEMPOTENCY_KEY_REQUIRED", "请提供 Idempotency-Key。", 400);
        if (idempotencyKey.Length > 191) throw new CollectorStoreException("INVALID_IDEMPOTENCY_KEY", "幂等键无效。", 400);
        if (request.PromptId == Guid.Empty) throw new CollectorStoreException("INVALID_PROMPT_SELECTION", "请选择一个监测问题。", 400);
        if (request.Requirements?.Length > ArticleStudioLimits.RequirementsMaxLength)
            throw new CollectorStoreException("INVALID_GENERATION_REQUEST", "自定义写作要求不能超过 5000 字符。", 400);
        if (request.ArticleId == Guid.Empty || request.MaterialPreviewId == Guid.Empty)
            throw new CollectorStoreException("INVALID_GENERATION_REQUEST", "文章或素材预览标识无效。", 400);
        var requirements = string.IsNullOrWhiteSpace(request.Requirements) ? null : request.Requirements;
        var requestHash = RequestHash(siteId, request with { Requirements = requirements });

        // Replays are resolved before any mutable input (prompt, preview, analytics) is read again.
        var accepted = await studio.FindStudioSubmissionAsync(tenantId, siteId, idempotencyKey, cancellationToken);
        if (accepted is { } replay)
        {
            if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
                throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "幂等键已用于不同生成请求。", 409);
            return replay.Accepted;
        }

        var prompt = await ResolvePromptAsync(tenantId, siteId, request.PromptId, cancellationToken);
        var language = ArticleStudioRules.LanguageFor(prompt.Locale);
        var providerConfiguration = await research.GetProviderConfigurationAsync(ArticleGenerationProvider.ProviderName, cancellationToken);
        if (providerConfiguration is null || string.IsNullOrWhiteSpace(providerConfiguration.Credential))
            throw new CollectorStoreException("ARTICLE_PROVIDER_UNAVAILABLE", "文章生成 Provider 未配置。", 503);

        var material = await ResolveMaterialAsync(tenantId, siteId, userId, prompt.PromptId, language, request.MaterialPreviewId, cancellationToken);
        KnowledgeContextResponse? frozenContext = null;
        string? knowledgeSnapshot = null;
        if (!material.WithoutRag)
        {
            if (material.ContextId is null)
                throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材上下文不存在。", 409);
            frozenContext = await knowledgeRetrieval.GetContextAsync(tenantId, material.ContextId.Value, cancellationToken)
                ?? throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材上下文不存在。", 409);
            if (!string.Equals(KnowledgeContextSnapshot.Hash(frozenContext), material.ContextSnapshotHash, StringComparison.OrdinalIgnoreCase))
                throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材快照校验失败。", 409);
            var brand = await management.GetBrandAsync(tenantId, siteId, cancellationToken);
            ArticleProductRef? product = null;
            if (Guid.TryParse(material.Selection.ProductKey, out var productId) && products is not null)
                product = await products.ResolveAsync(tenantId, siteId, productId, cancellationToken)
                    ?? throw new CollectorStoreException("PRODUCT_NOT_FOUND", "指定产品不存在或已删除。", 404);
            var selectionJson = JsonSerializer.Serialize(new { material.Selection, material.WithoutRag, material.MaterialNotes, material.PromptIds, material.Language, material.Topic }, JsonOptions);
            knowledgeSnapshot = ArticleWritingMaterialsSnapshot.Attach(selectionJson,
                ArticleWritingMaterialsSnapshot.Freeze(tenantId, siteId, brand, product, frozenContext));
        }

        var performance = await ReadPerformanceAsync(tenantId, siteId, prompt.PromptId, time.GetUtcNow(), cancellationToken);
        var frozenCompetitors = Array.Empty<ArticleStudioFrozenCompetitor>();
        if (request.IncludeCompetitors && performance.Competitors.Available)
        {
            var configured = await ListActiveCompetitorsAsync(tenantId, siteId, cancellationToken);
            frozenCompetitors = performance.Competitors.Items.Select(item => new ArticleStudioFrozenCompetitor(item.SubjectId, item.Name,
                item.Mentions, configured.FirstOrDefault(competitor => competitor.CompetitorId == item.SubjectId)?.Domains ?? [])).ToArray();
        }
        // 文章工作台不提供 App 口径：冻结的目标快照不含已启用的 App 目标（否则它们会挤占前几个名额）。
        var targets = (await management.GetSiteTargetsAsync(tenantId, siteId, cancellationToken)).Items
            .Where(target => target.Enabled && target.TargetId is not null && !MonitoringProviders.IsApp(target.Provider))
            .Take(ArticleStudioLimits.MaxTargets).ToArray();
        var submission = new ArticleStudioSubmission(requestHash, prompt, JsonSerializer.Serialize(targets, JsonOptions), language,
            ArticleStudioRules.StrategyFor(performance.Goal), "medium", "professional", requirements, material.WithoutRag,
            material.WithoutRag ? null : material.ContextId, material.WithoutRag ? null : material.ContextSnapshotHash,
            frozenContext is null ? null : KnowledgeContextSnapshot.Serialize(frozenContext), knowledgeSnapshot,
            performance.Goal, performance.Basis, frozenCompetitors.Length > 0, frozenCompetitors, request.ArticleId,
            ArticleGenerationPricing.UnitPrice);
        return await studio.SubmitStudioGenerationAsync(tenantId, siteId, userId, submission, idempotencyKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ArticleStudioGenerationView?> GetGenerationAsync(Guid tenantId, Guid siteId, Guid taskId,
        CancellationToken cancellationToken = default)
        => studio.GetStudioGenerationAsync(tenantId, siteId, taskId, cancellationToken);

    /// <summary>原始请求摘要：站点、提问、素材预览、竞品开关、写作要求与重新生成文章。</summary>
    internal static string RequestHash(Guid siteId, ArticleStudioGenerationRequest request)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            SiteId = siteId, request.PromptId, request.MaterialPreviewId, request.IncludeCompetitors,
            request.Requirements, request.ArticleId
        }, JsonOptions)))).ToLowerInvariant();

    private async Task<PromptView> ResolvePromptAsync(Guid tenantId, Guid siteId, Guid promptId, CancellationToken cancellationToken)
    {
        var prompt = await management.GetPromptAsync(tenantId, siteId, promptId, cancellationToken);
        if (prompt is null || !prompt.Enabled || prompt.ArchivedAt is not null)
            throw new CollectorStoreException("PROMPT_NOT_FOUND", "监测问题不存在、已停用或已归档。", 404);
        return prompt;
    }

    /// <summary>自动范围素材预览；0 命中不抛 422，改为保存不使用知识库的预览（沿用现有披露规则）。</summary>
    private async Task<ArticleMaterialPreviewView> AutoMaterialPreviewAsync(Guid tenantId, Guid siteId, Guid userId, Guid promptId,
        string language, CancellationToken cancellationToken)
    {
        try
        {
            return await materials.PreviewAsync(tenantId, siteId, userId, new ArticleMaterialPreviewRequest([promptId], language), cancellationToken);
        }
        catch (CollectorStoreException exception) when (exception.Code == "KNOWLEDGE_CONTEXT_EMPTY")
        {
            return await materials.PreviewAsync(tenantId, siteId, userId,
                new ArticleMaterialPreviewRequest([promptId], language, WithoutRag: true), cancellationToken);
        }
    }

    private async Task<ArticleMaterialPreviewView> ResolveMaterialAsync(Guid tenantId, Guid siteId, Guid userId, Guid promptId,
        string language, Guid? materialPreviewId, CancellationToken cancellationToken)
    {
        if (materialPreviewId is { } id)
        {
            ArticleMaterialPreviewView? preview;
            try { preview = await content.GetMaterialPreviewAsync(tenantId, siteId, id, cancellationToken); }
            catch (CollectorStoreException exception) when (exception.Code == "ARTICLE_PREVIEW_EXPIRED")
            {
                // The 15-minute preview from step 1 expired while the user was writing requirements: re-run the same
                // automatic retrieval instead of failing the submission.
                return await AutoMaterialPreviewAsync(tenantId, siteId, userId, promptId, language, cancellationToken);
            }
            if (preview is null)
                throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材预览不存在。", 409);
            if (preview.PromptIds is not { Count: 1 } || preview.PromptIds[0] != promptId
                || preview.Language is not null && !string.Equals(preview.Language, language, StringComparison.OrdinalIgnoreCase))
                throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材与所选问题不一致，请重新进入创作准备。", 409);
            return preview;
        }
        return await AutoMaterialPreviewAsync(tenantId, siteId, userId, promptId, language, cancellationToken);
    }

    private async Task<IReadOnlyList<CompetitorView>> ListActiveCompetitorsAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken)
        => (await management.ListCompetitorsAsync(tenantId, siteId, null, 100, cancellationToken)).Items
            .Where(competitor => string.Equals(competitor.Status, "active", StringComparison.OrdinalIgnoreCase)).ToArray();

    private sealed record Performance(ArticleOptimizationGoal Goal, ArticleStudioGoalBasis Basis, long BrandMentions,
        long EligibleChecks, ArticleStudioCompetitorsView Competitors);

    /// <summary>近 7 天全来源表现：按来源分组的主体提及次数相加，推导优化目标并筛选前 8 名已配置竞品。</summary>
    private async Task<Performance> ReadPerformanceAsync(Guid tenantId, Guid siteId, Guid promptId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var configured = (await ListActiveCompetitorsAsync(tenantId, siteId, cancellationToken))
            .ToDictionary(competitor => competitor.CompetitorId, competitor => competitor.Name);
        long eligible = 0, brandMentions = 0;
        var mentions = new Dictionary<Guid, long>();
        var brandIds = new HashSet<Guid>();
        try
        {
            // LeaderboardAsync rejects multi-source populations, so read the share-of-voice source groups
            // (one projection per provider|captureMode|evidenceClass) and add the per-subject counts.
            var view = await analytics.ShareOfVoiceAsync(tenantId, siteId,
                AnalyticsQueryStore.AllSourcesRequest(now.AddDays(-7), now, [promptId]), null, 100, cancellationToken);
            foreach (var group in (view.SourceGroups ?? new Dictionary<string, VoiceProjection>()).Values)
            {
                eligible += group.Metrics.EligibleChecks;
                foreach (var entry in group.Entries)
                {
                    if (entry.IsBrand) { brandIds.Add(entry.SubjectId); brandMentions += entry.Mentions; continue; }
                    mentions[entry.SubjectId] = mentions.GetValueOrDefault(entry.SubjectId) + entry.Mentions;
                }
            }
        }
        catch (CollectorStoreException)
        {
            // No published analysis release yet (or analytics unavailable): treat as "no data".
            eligible = 0; brandMentions = 0; mentions.Clear();
        }
        var topCompetitor = mentions.Where(pair => configured.ContainsKey(pair.Key)).Select(pair => pair.Value).DefaultIfEmpty(0).Max();
        var (goal, basis) = ArticleStudioRules.DeriveGoal(eligible, brandMentions, topCompetitor);
        return new Performance(goal, basis, brandMentions, eligible, ArticleStudioRules.SelectCompetitors(configured, mentions));
    }
}
