using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 文章创作六阶段管线（pipeline = studio_v1）。阶段 1–4 失败或无数据只记录 skipped / failed 及原因，不阻断生成；
/// 阶段 6 失败抛出原异常，由 Worker 按现有规则重试或释放积分。重试时跳过已完成的阶段并复用其持久化产物。
/// 引用源正文与联网搜索结果全部作为不可信内容进入生成，永不作为品牌事实。
/// </summary>
public sealed class ArticleStudioPipeline(
    IArticleStudioStore store,
    IAnalyticsQueryStore analytics,
    IArticleSourceReader reader,
    IArticleStructureExtractor structure,
    IArticleWebResearchService webResearch,
    IArticleGenerationProvider provider,
    TimeProvider? clock = null,
    ILogger<ArticleStudioPipeline>? logger = null)
{
    private const int FetchConcurrency = 4;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ILogger log = (ILogger?)logger ?? NullLogger.Instance;

    private sealed record StageOutcome(ArticleStudioStageStatus Status, ArticleStudioStageDetail? Detail = null, string? Output = null)
    {
        public static StageOutcome Skip(string reason, int? items = null)
            => new(ArticleStudioStageStatus.Skipped, new ArticleStudioStageDetail(ItemCount: items, Reason: reason, ReasonLabel: ArticleStudioRules.ReasonLabel(reason)));
        public static StageOutcome Fail(string reason, string errorCode)
            => new(ArticleStudioStageStatus.Failed, new ArticleStudioStageDetail(Reason: reason, ReasonLabel: ArticleStudioRules.ReasonLabel(reason), ErrorCode: errorCode));
    }

    /// <summary>执行（或续跑）六个阶段并返回阶段 6 的生成结果。</summary>
    /// <param name="task">已认领的 studio 任务。</param>
    /// <param name="configuration">deepseek-article 配置。</param>
    /// <param name="evidence">提交时冻结的知识依据。</param>
    /// <param name="cancellationToken">执行令牌（含租约丢失与 15 分钟期限）。</param>
    public async Task<ArticleGenerationResult> RunAsync(ArticleGenerationWorkItem task, ResearchProviderConfiguration configuration,
        IReadOnlyList<KnowledgeEvidenceView> evidence, CancellationToken cancellationToken)
    {
        var snapshot = await store.GetStudioSnapshotAsync(task.TenantId, task.BatchId, cancellationToken);
        var promptId = task.PromptId ?? snapshot?.PromptId;
        var goal = task.OptimizationGoal ?? snapshot?.Goal ?? ArticleOptimizationGoal.Mention;
        var competitors = task.IncludeCompetitors && snapshot is not null ? snapshot.Competitors : [];
        var competitorNames = competitors.Select(competitor => competitor.Name).ToArray();
        var question = QuestionText(task.PromptSnapshot);
        var existing = (await store.ListStagesAsync(task.TenantId, task.TaskId, cancellationToken))
            .GroupBy(stage => stage.Key).ToDictionary(group => group.Key, group => group.Last());

        // Stages 1→2→3 form a chain: once one is (re)executed every later chain stage is executed again.
        var chainRerun = false;

        // 1. citations
        IReadOnlyList<ArticleStudioSourceRecord> citations;
        if (Reusable(existing, ArticleStudioStageKey.Citations))
            citations = await store.ListSourcesAsync(task.TenantId, task.TaskId, ArticleStudioSourceKind.Citation, cancellationToken);
        else
        {
            chainRerun = true;
            var found = new List<ArticleStudioSourceRecord>();
            await ExecuteAsync(task, ArticleStudioStageKey.Citations, async token =>
            {
                if (promptId is not { } id) return StageOutcome.Skip("no_citation_data");
                PromptCitedSourcesView sources;
                try
                {
                    var now = time.GetUtcNow();
                    sources = await analytics.TopCitedUrlsForPromptAsync(task.TenantId, task.SiteId, id, now.AddDays(-30), now,
                        ArticleStudioLimits.MaxCitationSources, token);
                }
                catch (CollectorStoreException exception) when (exception.Code is "ANALYSIS_VERSION_NOT_READY" or "RESOURCE_NOT_FOUND")
                {
                    return StageOutcome.Skip("analysis_not_ready");
                }
                if (sources.Items.Count == 0) return StageOutcome.Skip("no_citation_data", 0);
                found.AddRange(sources.Items.Select((row, index) => new ArticleStudioSourceRecord(ArticleStudioSourceKind.Citation,
                    row.Url, row.Domain, row.Title, row.Engines, row.CitationCount, row.Ownership, row.Ownership == "competitor",
                    ArticleStudioFetchStatus.Pending, null, null, index + 1)));
                EnsureLease(await store.ReplaceSourcesAsync(task, ArticleStudioSourceKind.Citation, found, token));
                return new StageOutcome(ArticleStudioStageStatus.Succeeded, new ArticleStudioStageDetail(ItemCount: found.Count));
            }, "citations_failed", cancellationToken);
            citations = found;
        }

        // 2. fetch
        if (!chainRerun && Reusable(existing, ArticleStudioStageKey.Fetch))
            citations = await store.ListSourcesAsync(task.TenantId, task.TaskId, ArticleStudioSourceKind.Citation, cancellationToken);
        else
        {
            chainRerun = true;
            var input = citations;
            var fetched = input;
            await ExecuteAsync(task, ArticleStudioStageKey.Fetch, async token =>
            {
                if (input.Count == 0) return StageOutcome.Skip("no_sources", 0);
                fetched = await FetchAllAsync(input, token);
                EnsureLease(await store.ReplaceSourcesAsync(task, ArticleStudioSourceKind.Citation, fetched, token));
                var ok = fetched.Count(source => source.FetchStatus == ArticleStudioFetchStatus.Fetched);
                var blocked = fetched.Count(source => source.FetchStatus == ArticleStudioFetchStatus.Blocked);
                var failed = fetched.Count(source => source.FetchStatus == ArticleStudioFetchStatus.Failed);
                var detail = new ArticleStudioStageDetail(ItemCount: fetched.Count, FetchedCount: ok, BlockedCount: blocked, FailedCount: failed);
                return ok == 0
                    ? new StageOutcome(ArticleStudioStageStatus.Failed, detail with
                    {
                        Reason = "all_sources_unreadable", ReasonLabel = ArticleStudioRules.ReasonLabel("all_sources_unreadable"),
                        ErrorCode = "SOURCES_UNREADABLE"
                    })
                    : new StageOutcome(ArticleStudioStageStatus.Succeeded, detail);
            }, "all_sources_unreadable", cancellationToken);
            citations = fetched;
        }
        var readable = citations.Where(source => source.FetchStatus == ArticleStudioFetchStatus.Fetched && !string.IsNullOrWhiteSpace(source.Content))
            .Select(source => new ArticleStudioUntrustedSource(ArticleStudioSourceKind.Citation, source.Url, source.Title, source.Content)).ToArray();

        // 3. structure
        string? insights;
        if (!chainRerun && Reusable(existing, ArticleStudioStageKey.Structure))
            insights = existing[ArticleStudioStageKey.Structure].Output;
        else
        {
            string? extracted = null;
            await ExecuteAsync(task, ArticleStudioStageKey.Structure, async token =>
            {
                if (readable.Length == 0) return StageOutcome.Skip("no_fetched_content", 0);
                extracted = await structure.ExtractAsync(configuration,
                    new ArticleStructureRequest(question, task.Language, readable, competitorNames), token);
                return new StageOutcome(ArticleStudioStageStatus.Succeeded, new ArticleStudioStageDetail(ItemCount: readable.Length), extracted);
            }, "structure_failed", cancellationToken);
            insights = extracted;
        }

        // 4. web_research — independent of the chain; a completed or skipped search is never repeated.
        string? summary;
        IReadOnlyList<ArticleStudioSourceRecord> webSources;
        if (Reusable(existing, ArticleStudioStageKey.WebResearch))
        {
            summary = existing[ArticleStudioStageKey.WebResearch].Output;
            webSources = await store.ListSourcesAsync(task.TenantId, task.TaskId, ArticleStudioSourceKind.WebSearch, cancellationToken);
        }
        else
        {
            string? researched = null;
            var results = new List<ArticleStudioSourceRecord>();
            await ExecuteAsync(task, ArticleStudioStageKey.WebResearch, async token =>
            {
                var result = await webResearch.ResearchAsync(configuration,
                    new ArticleWebResearchRequest(question, task.Language, goal, competitorNames), token);
                if (result.Skipped) return StageOutcome.Skip(result.SkipReason ?? "provider_not_configured");
                results.AddRange(result.Results.Select((item, index) => new ArticleStudioSourceRecord(ArticleStudioSourceKind.WebSearch,
                    item.Url, item.Domain, item.Title, [], 0, null,
                    competitors.Any(competitor => MatchesDomain(item.Domain, competitor.Domains)),
                    ArticleStudioFetchStatus.Skipped, null, null, index + 1)));
                EnsureLease(await store.ReplaceSourcesAsync(task, ArticleStudioSourceKind.WebSearch, results, token));
                researched = result.Summary;
                return new StageOutcome(ArticleStudioStageStatus.Succeeded,
                    new ArticleStudioStageDetail(ItemCount: results.Count, SearchCount: result.SearchCount), researched);
            }, "provider_failed", cancellationToken);
            summary = researched;
            webSources = results;
        }

        // 5. requirements — deterministic assembly, no model call.
        var compose = new ArticleStudioComposeInput(goal, insights,
            competitors.Select(competitor => new ArticleStudioCompetitorInput(competitor.Name,
                webSources.Where(source => MatchesDomain(source.Domain, competitor.Domains)).Select(source => source.Url).Take(5).ToArray())).ToArray(),
            readable.Concat(webSources.Select(source => new ArticleStudioUntrustedSource(ArticleStudioSourceKind.WebSearch, source.Url, source.Title, null))).ToArray(),
            summary);
        if (!Reusable(existing, ArticleStudioStageKey.Requirements))
            await ExecuteAsync(task, ArticleStudioStageKey.Requirements, _ => Task.FromResult(new StageOutcome(ArticleStudioStageStatus.Succeeded,
                new ArticleStudioStageDetail(ItemCount: task.ExtraRequirements?.Length ?? 0))), "compose_failed", cancellationToken);

        // 6. compose — failures propagate to the Worker (retry / release by the existing rules).
        ArticleGenerationResult? generated = null;
        await ExecuteAsync(task, ArticleStudioStageKey.Compose, async token =>
        {
            generated = await provider.GenerateStudioAsync(task, configuration, evidence, compose, token);
            return new StageOutcome(ArticleStudioStageStatus.Succeeded);
        }, "compose_failed", cancellationToken, propagate: true);
        return generated!;
    }

    private static bool Reusable(IReadOnlyDictionary<ArticleStudioStageKey, ArticleStudioStageRecord> existing, ArticleStudioStageKey key)
        => existing.TryGetValue(key, out var stage) && stage.Status is ArticleStudioStageStatus.Succeeded or ArticleStudioStageStatus.Skipped;

    private async Task ExecuteAsync(ArticleGenerationWorkItem task, ArticleStudioStageKey key,
        Func<CancellationToken, Task<StageOutcome>> body, string failureReason, CancellationToken cancellationToken, bool propagate = false)
    {
        var started = time.GetUtcNow();
        // The write is fenced by the task lease.  A Worker that no longer owns the task must stop before it
        // spends another paid model / web-search call; the Worker's failure write is fenced the same way.
        EnsureLease(await store.SaveStageAsync(task, new ArticleStudioStageRecord(key, ArticleStudioStageStatus.Running, started, null, null, null), cancellationToken));
        StageOutcome outcome;
        Exception? failure = null;
        try { outcome = await body(cancellationToken); }
        catch (ArticleStudioLeaseLostException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Deadline or lost lease: record the interruption (fenced by the lease, so a new owner is never
            // overwritten) and let the Worker decide between retry and fencing.
            try
            {
                await store.SaveStageAsync(task, new ArticleStudioStageRecord(key, ArticleStudioStageStatus.Failed, started, time.GetUtcNow(),
                    StageOutcome.Fail(failureReason, "STAGE_INTERRUPTED").Detail, null), CancellationToken.None);
            }
            catch (Exception) { /* best effort; the original cancellation is what matters */ }
            throw;
        }
        catch (Exception exception)
        {
            var code = exception is CollectorStoreException storeException ? storeException.Code
                : exception is HttpRequestException ? "PROVIDER_REQUEST_FAILED"
                : exception is OperationCanceledException ? "STAGE_TIMEOUT" : "STAGE_FAILED";
            log.LogWarning("Article studio stage {Stage} failed for task {TaskId}: {Code}", key, task.TaskId, code);
            outcome = StageOutcome.Fail(failureReason, code);
            failure = exception;
        }
        await store.SaveStageAsync(task, new ArticleStudioStageRecord(key, outcome.Status, started, time.GetUtcNow(), outcome.Detail, outcome.Output),
            CancellationToken.None);
        if (propagate && failure is not null) throw failure;
    }

    private static void EnsureLease(bool owned)
    {
        if (!owned) throw new ArticleStudioLeaseLostException();
    }

    private async Task<IReadOnlyList<ArticleStudioSourceRecord>> FetchAllAsync(IReadOnlyList<ArticleStudioSourceRecord> sources,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(FetchConcurrency);
        var tasks = sources.Select(async source =>
        {
            await gate.WaitAsync(cancellationToken);
            try { return await FetchOneAsync(source, cancellationToken); }
            finally { gate.Release(); }
        }).ToArray();
        return await Task.WhenAll(tasks);
    }

    private async Task<ArticleStudioSourceRecord> FetchOneAsync(ArticleStudioSourceRecord source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        try
        {
            var result = await reader.ReadAsync(source.Url, timeout.Token);
            if (string.Equals(result.ReadStatus, "ready", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(result.BodyMarkdown))
            {
                var body = result.BodyMarkdown.Length <= ArticleStudioLimits.SourceContentMaxLength
                    ? result.BodyMarkdown : result.BodyMarkdown[..ArticleStudioLimits.SourceContentMaxLength];
                return source with
                {
                    FetchStatus = ArticleStudioFetchStatus.Fetched, ErrorCode = null, Content = body,
                    Title = string.IsNullOrWhiteSpace(source.Title) ? result.Title : source.Title
                };
            }
            var code = result.ErrorCode ?? "SOURCE_UNREADABLE";
            return source with
            {
                FetchStatus = code == "SOURCE_BLOCKED" ? ArticleStudioFetchStatus.Blocked : ArticleStudioFetchStatus.Failed,
                ErrorCode = code, Content = null
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return source with { FetchStatus = ArticleStudioFetchStatus.Failed, ErrorCode = "SOURCE_TIMEOUT", Content = null };
        }
        catch (CollectorStoreException exception)
        {
            return source with { FetchStatus = ArticleStudioFetchStatus.Failed, ErrorCode = exception.Code, Content = null };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One unreadable page never fails the whole fetch stage (the reader's SSRF / size guards may throw).
            return source with { FetchStatus = ArticleStudioFetchStatus.Failed, ErrorCode = "SOURCE_UNREADABLE", Content = null };
        }
    }

    private static bool MatchesDomain(string host, IReadOnlyList<string> domains)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var normalized = host.Trim().ToLowerInvariant();
        if (normalized.StartsWith("www.", StringComparison.Ordinal)) normalized = normalized[4..];
        foreach (var domain in domains)
        {
            var candidate = domain.Trim().ToLowerInvariant();
            if (candidate.StartsWith("www.", StringComparison.Ordinal)) candidate = candidate[4..];
            if (candidate.Length == 0) continue;
            if (normalized == candidate || normalized.EndsWith("." + candidate, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string QuestionText(string promptSnapshot)
    {
        if (string.IsNullOrWhiteSpace(promptSnapshot)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(promptSnapshot);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var property in document.RootElement.EnumerateObject())
                    if (property.Name.Equals("content", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        return property.Value.GetString() ?? string.Empty;
        }
        catch (JsonException) { }
        return promptSnapshot.Length <= 2_000 ? promptSnapshot : promptSnapshot[..2_000];
    }
}

/// <summary>阶段或来源写入时发现任务租约已不属于当前 Worker：停止管线，不再调用付费模型或联网搜索。</summary>
public sealed class ArticleStudioLeaseLostException()
    : CollectorStoreException("ARTICLE_GENERATION_LEASE_LOST", "文章生成任务已由其他 Worker 接管。", 409);
