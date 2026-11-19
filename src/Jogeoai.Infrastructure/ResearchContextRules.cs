using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>研究上下文的版本、作用域和有界复用规则。</summary>
public static class ResearchContextRules
{
    /// <summary>规范化问题，用于排除重复推荐。</summary>
    public static string QuestionKey(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"\s+", " ")
        .Trim().TrimEnd('?', '!', '.', '。').Trim().ToUpperInvariant();

    /// <summary>生成内容指纹。</summary>
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>品牌知识条目证据编号前缀；后接条目 UUID 的 N 格式。</summary>
    public const string KnowledgeEvidencePrefix = "knowledge:";

    /// <summary>证据是否来自品牌知识库条目。</summary>
    /// <param name="evidence">研究证据。</param>
    public static bool IsKnowledgeEvidence(ResearchEvidence evidence) =>
        evidence.Id.StartsWith(KnowledgeEvidencePrefix, StringComparison.Ordinal);

    /// <summary>
    /// 把品牌研究读取的条目转为研究证据：编号 <c>knowledge:&lt;条目 N 格式 UUID&gt;</c>，无网址，
    /// 标题「品牌知识·维度·标题」，摘要为「标题 + 换行 + 正文」，指纹为摘要的 SHA256，读取时间取条目最后修改时间。
    /// 条目内容属于用户资料，进入模型时按数据而非指令处理。
    /// </summary>
    /// <param name="items">已按研究预算裁剪的条目。</param>
    public static IReadOnlyList<ResearchEvidence> KnowledgeEvidence(IEnumerable<BrandKnowledgeResearchItem> items) =>
        items.Select(item =>
        {
            var excerpt = $"{item.Title}\n{item.Content}";
            return new ResearchEvidence($"{KnowledgeEvidencePrefix}{item.ItemId:N}", null,
                $"品牌知识·{BrandKnowledgeRules.Describe(item.Dimension).Label}·{item.Title}", excerpt, Hash(excerpt), item.UpdatedAt);
        }).ToArray();

    /// <summary>
    /// 建立新任务上下文；来源只接受服务端读取的同品牌任务。上一轮的用户补充与品牌知识证据都会被本次的替换；
    /// 品牌知识证据（编号、标题或指纹）与上一轮不同时不复用画像。
    /// </summary>
    /// <param name="request">已规范化的研究请求。</param>
    /// <param name="previous">同品牌上一轮研究上下文。</param>
    /// <param name="previousSuggestion">同品牌上一轮推荐结果，用于排除重复问题。</param>
    /// <param name="knowledgeEvidence">本次入队时读取的品牌知识证据，编号必须以 <see cref="KnowledgeEvidencePrefix"/> 开头。</param>
    public static ResearchContext Create(StartBrandResearchRequest request, ResearchContext? previous = null,
        BrandResearchSuggestion? previousSuggestion = null, IReadOnlyList<ResearchEvidence>? knowledgeEvidence = null)
    {
        var knowledge = knowledgeEvidence ?? [];
        if (knowledge.Any(e => !IsKnowledgeEvidence(e)))
            throw new ArgumentException("品牌知识证据编号必须以 knowledge: 开头。", nameof(knowledgeEvidence));
        var sameScope = previous is not null && previous.StrategyVersion == ResearchContext.CurrentStrategy
            && previous.Website == request.Website && previous.BrandName == request.BrandName
            && previous.Market == request.Market && previous.Language == request.Language;
        var description = request.BusinessDescription ?? (sameScope ? previous!.BusinessDescription : null);
        var freshEvidence = sameScope && previous!.Evidence.All(e => e.Url is null || e.FetchedAt > DateTimeOffset.UtcNow.AddDays(-1));
        var knowledgeUnchanged = sameScope && SameKnowledge(previous!.Evidence.Where(IsKnowledgeEvidence), knowledge);
        var profile = freshEvidence && knowledgeUnchanged && description == previous!.BusinessDescription ? previous.Profile : null;
        var evidence = freshEvidence ? previous!.Evidence.Where(e => e.Id != "user" && !IsKnowledgeEvidence(e)).ToArray() : [];
        evidence = [.. evidence, .. knowledge];
        if (!string.IsNullOrWhiteSpace(description))
            evidence = [.. evidence, new ResearchEvidence("user", null, "用户补充的业务信息", description, Hash(description), DateTimeOffset.UtcNow)];
        var exclusions = (sameScope ? previous!.ExcludedQuestions : [])
            .Concat(previousSuggestion?.Prompts.Select(p => p.Prompt) ?? [])
            .Concat(request.ExcludedQuestions ?? [])
            .DistinctBy(QuestionKey).Where(p => p.Length <= 1000).TakeLast(100).ToArray();
        var feedback = (sameScope ? previous!.Feedback : []).Concat(request.Feedback ?? [])
            .DistinctBy(f => (QuestionKey(f.Prompt), f.Reason)).TakeLast(50).ToArray();
        while (exclusions.Sum(p => p.Length) > 20000) exclusions = exclusions.Skip(1).ToArray();
        while (feedback.Sum(f => f.Prompt.Length) > 10000) feedback = feedback.Skip(1).ToArray();
        return new(ResearchContext.CurrentStrategy, profile is null ? "collecting" : "generating",
            request.Website, request.BrandName, request.Market, request.Language, description, profile,
            evidence, exclusions, feedback);
    }

    /// <summary>对证据及用户修正计算画像版本。</summary>
    public static string ProfileVersion(ResearchContext context) => Hash(JsonSerializer.Serialize(new
    {
        context.StrategyVersion, context.Website, context.BrandName, context.Market, context.Language,
        context.BusinessDescription, Evidence = context.Evidence.Select(e => new { e.Id, e.ContentHash }),
    }));

    private static bool SameKnowledge(IEnumerable<ResearchEvidence> previous, IReadOnlyList<ResearchEvidence> current)
    {
        var before = previous.Select(e => (e.Id, e.Title, e.ContentHash)).ToHashSet();
        var after = current.Select(e => (e.Id, e.Title, e.ContentHash)).ToHashSet();
        return before.SetEquals(after);
    }
}
