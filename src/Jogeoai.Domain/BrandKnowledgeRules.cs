using System.Globalization;
using System.Text;

namespace Jogeoai.Domain;

/// <summary>品牌知识条目的维度元数据、标题规范化、长度校验、知识库 Markdown 模板、网址识别候选标准化与查重。</summary>
public static class BrandKnowledgeRules
{
    /// <summary>标题长度上限（字符数，按 Unicode 码点计，与 MySQL CHAR_LENGTH 一致）。</summary>
    public const int MaxTitleLength = 100;
    /// <summary>内容长度上限（字符数，按 Unicode 码点计）。</summary>
    public const int MaxContentLength = 2000;
    /// <summary>原文依据片段长度上限。</summary>
    public const int MaxExcerptLength = 1000;
    /// <summary>每品牌条目上限（所有维度合计，PRD D07）。</summary>
    public const int MaxItemsPerSite = 50;
    /// <summary>单次网址识别最多保留的候选数。</summary>
    public const int MaxCandidates = 30;
    /// <summary>原文依据去除空白后的最少字符数；过短的片段几乎必然是正文子串，无法证明出处。</summary>
    public const int MinEvidenceLength = 4;
    /// <summary>正文字符二元组 Jaccard 相似度达到该值即视为可能重复。</summary>
    public const double DuplicateSimilarityThreshold = 0.8;
    /// <summary>每租户每日（UTC）识别次数上限，与产品识别共用额度（PRD D08）。</summary>
    public const int DailyExtractLimit = KnowledgeProductRules.DailyParseLimit;
    /// <summary>识别候选在保存或放弃前的保留时长。</summary>
    public static readonly TimeSpan CandidateLifetime = TimeSpan.FromHours(24);
    /// <summary>条目检索投影资料在知识库中的来源类型（knowledge_documents.source_kind）。</summary>
    public const string ItemDocumentSourceKind = "item";
    /// <summary>通用资料接口试图改写条目检索投影资料时的错误码（409）。</summary>
    public const string DocumentManagedByItemCode = "KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM";

    /// <summary>
    /// 条目是品牌事实的唯一编辑入口，其检索投影资料（source_kind=item）只由同步 Worker 写入：
    /// 通用资料接口的修改、停用、重试与上传新版本命中这类资料时抛出 409 KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM。
    /// </summary>
    /// <param name="sourceKind">资料的 source_kind。</param>
    /// <exception cref="CollectorStoreException">资料是条目检索投影时。</exception>
    public static void AssertDocumentNotItemManaged(string? sourceKind)
    {
        if (string.Equals(sourceKind, ItemDocumentSourceKind, StringComparison.Ordinal))
            throw new CollectorStoreException(DocumentManagedByItemCode,
                "该资料由品牌知识库条目自动维护，不能在这里修改、停用、重试或上传新版本；请到品牌知识库编辑或删除对应条目。", 409);
    }

    /// <summary>6 个维度的元数据，按固定顺序排列（亦即品牌研究读取条目的优先级）。</summary>
    public static readonly IReadOnlyList<BrandKnowledgeDimensionInfo> Dimensions =
    [
        new(BrandKnowledgeDimension.BrandIntro, "brand_intro", "品牌介绍", 1, "公司定位、成立背景、资质荣誉"),
        new(BrandKnowledgeDimension.SellingPoint, "selling_point", "核心卖点", 2, "用户最该记住的 1 个卖点，一条写一个"),
        new(BrandKnowledgeDimension.Audience, "audience", "目标人群", 3, "谁在用、什么场景用"),
        new(BrandKnowledgeDimension.Differentiator, "differentiator", "差异化优势", 4, "与竞品相比的不同点，尽量给出可验证事实"),
        new(BrandKnowledgeDimension.ProductService, "product_service", "产品/服务", 5, "一个产品或服务一条，写清型号、参数、适用范围"),
        new(BrandKnowledgeDimension.Faq, "faq", "常见问题", 6, "标题写问题，内容写回答")
    ];

    /// <summary>读取维度元数据。</summary>
    /// <param name="dimension">维度。</param>
    /// <exception cref="ArgumentOutOfRangeException">维度未定义。</exception>
    public static BrandKnowledgeDimensionInfo Describe(BrandKnowledgeDimension dimension)
        => Dimensions.FirstOrDefault(item => item.Dimension == dimension)
           ?? throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "未定义的品牌知识维度。");

    /// <summary>维度的小写蛇形键（数据库 dimension 列与接口 JSON 值）。</summary>
    /// <param name="dimension">维度。</param>
    public static string ToKey(BrandKnowledgeDimension dimension) => Describe(dimension).Key;

    /// <summary>解析维度：接受小写蛇形键（不区分大小写，连字符视同下划线）或中文名。</summary>
    /// <param name="value">待解析的值。</param>
    /// <param name="dimension">解析出的维度。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryParseDimension(string? value, out BrandKnowledgeDimension dimension)
    {
        dimension = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        var key = text.Replace('-', '_').ToLowerInvariant();
        foreach (var item in Dimensions)
        {
            if (item.Key == key || string.Equals(item.Label, text, StringComparison.Ordinal))
            {
                dimension = item.Dimension;
                return true;
            }
        }
        return false;
    }

    /// <summary>规范化标题：去除首尾空白，内部连续空白（含换行、全角空格）折叠为一个半角空格。不改变大小写与全半角。</summary>
    /// <param name="title">原始标题。</param>
    public static string NormalizeTitle(string? title) => CollapseWhitespace(title);

    /// <summary>计算重名判断键 title_key：NFKC（全角转半角）、Trim、折叠空白、ToLowerInvariant。</summary>
    /// <param name="title">标题（规范化前后均可）。</param>
    /// <remarks>043 迁移在 SQL 中只能做到 Trim、折叠空白与 LOWER，缺少 NFKC；含全角 ASCII 的迁移标题会在迁移报告中列出。</remarks>
    public static string TitleKey(string? title)
    {
        var text = (title ?? string.Empty).Normalize(NormalizationForm.FormKC);
        return CollapseWhitespace(text).ToLowerInvariant();
    }

    /// <summary>规范化内容：统一换行为 \n 并去除首尾空白，保留内部格式。</summary>
    /// <param name="content">原始内容。</param>
    public static string NormalizeContent(string? content)
        => (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    /// <summary>按 Unicode 码点统计字符数，与 MySQL CHAR_LENGTH 一致。</summary>
    /// <param name="text">文本。</param>
    public static int CharacterCount(string? text) => string.IsNullOrEmpty(text) ? 0 : text.EnumerateRunes().Count();

    /// <summary>规范化并校验新增条目。</summary>
    /// <param name="write">原始请求。</param>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_ITEM_VALIDATION（400）。</exception>
    public static BrandKnowledgeItemWrite NormalizeWrite(BrandKnowledgeItemWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var (title, content) = NormalizeFields(write.Dimension, write.Title, write.Content);
        return new BrandKnowledgeItemWrite(write.Dimension, title, content);
    }

    /// <summary>规范化并校验编辑请求。</summary>
    /// <param name="update">原始请求。</param>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_ITEM_VALIDATION（400）。</exception>
    public static BrandKnowledgeItemUpdate NormalizeUpdate(BrandKnowledgeItemUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.ExpectedVersion < 1) throw Invalid("内容版本无效，请刷新后重试。");
        var (title, content) = NormalizeFields(update.Dimension, update.Title, update.Content);
        return new BrandKnowledgeItemUpdate(update.Dimension, title, content, update.ExpectedVersion);
    }

    /// <summary>补齐 6 个维度的计数，缺失的维度计 0。</summary>
    /// <param name="counts">已有计数。</param>
    public static IReadOnlyDictionary<BrandKnowledgeDimension, int> CompleteCounts(IReadOnlyDictionary<BrandKnowledgeDimension, int>? counts)
        => Dimensions.ToDictionary(item => item.Dimension,
            item => counts is not null && counts.TryGetValue(item.Dimension, out var count) ? count : 0);

    /// <summary>按固定模板把条目渲染为知识库 Markdown（检索投影文档内容）。</summary>
    /// <param name="dimension">维度。</param>
    /// <param name="title">标题。</param>
    /// <param name="content">内容。</param>
    /// <param name="sourceUrl">来源网址；为空时不输出来源行。</param>
    public static string RenderMarkdown(BrandKnowledgeDimension dimension, string title, string content, string? sourceUrl)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(NormalizeTitle(title)).Append("\n\n");
        builder.Append("维度：").Append(Describe(dimension).Label).Append("\n\n");
        builder.Append(NormalizeContent(content)).Append('\n');
        if (!string.IsNullOrWhiteSpace(sourceUrl)) builder.Append('\n').Append("来源：").Append(sourceUrl.Trim()).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// 标准化模型输出的候选：维度须可解析且在限定范围内；标题 1–100 字、内容 1–2000 字（超出截断并标记）；
    /// 原文依据去除空白后必须是网页正文的子串，否则整条丢弃（防编造）；同批内同维度 title_key 重复只保留第一条；
    /// 品牌介绍是一段综合介绍，同批只保留第一条；最多 30 条；
    /// 候选序号为任务内序号；与已有条目可能重复的写入 <see cref="KnowledgeExtractCandidate.DuplicateOfItemId"/>。
    /// </summary>
    /// <param name="raw">模型输出的原始候选，按输出顺序。</param>
    /// <param name="pageText">送入模型的网页正文。</param>
    /// <param name="allowedDimensions">限定维度；为空表示全部维度。</param>
    /// <param name="existingItems">品牌当前未删除的条目，用于查重。</param>
    public static KnowledgeExtractNormalizeResult NormalizeCandidates(IReadOnlyList<KnowledgeExtractRawCandidate>? raw, string? pageText,
        IReadOnlyCollection<BrandKnowledgeDimension>? allowedDimensions, IReadOnlyList<BrandKnowledgeItemView>? existingItems)
    {
        var source = raw ?? [];
        var page = CompactForMatch(pageText);
        var allowed = allowedDimensions is { Count: > 0 } ? allowedDimensions.ToHashSet() : null;
        var existing = existingItems ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasBrandIntro = false;
        var result = new List<KnowledgeExtractCandidate>();
        var dropped = 0;
        var truncated = false;
        foreach (var candidate in source)
        {
            if (candidate is null || result.Count >= MaxCandidates
                || !TryParseDimension(candidate.Dimension, out var dimension) || (allowed is not null && !allowed.Contains(dimension)))
            { dropped++; continue; }

            var title = NormalizeTitle(candidate.Title);
            var content = NormalizeContent(candidate.Content);
            if (title.Length == 0 || content.Length == 0) { dropped++; continue; }
            if (CharacterCount(title) > MaxTitleLength) { title = TruncateRunes(title, MaxTitleLength).TrimEnd(); truncated = true; }
            if (CharacterCount(content) > MaxContentLength) { content = TruncateRunes(content, MaxContentLength).TrimEnd(); truncated = true; }

            var evidence = CollapseWhitespace(candidate.Evidence);
            var compactEvidence = CompactForMatch(evidence);
            if (compactEvidence.Length < MinEvidenceLength || !page.Contains(compactEvidence, StringComparison.Ordinal))
            { dropped++; continue; }

            if (!seen.Add(ToKey(dimension) + ":" + TitleKey(title))) { dropped++; continue; }
            if (dimension == BrandKnowledgeDimension.BrandIntro)
            {
                if (hasBrandIntro) { dropped++; continue; }
                hasBrandIntro = true;
            }

            result.Add(new KnowledgeExtractCandidate((result.Count + 1).ToString(CultureInfo.InvariantCulture), dimension, title, content,
                TruncateRunes(evidence, MaxExcerptLength), FindDuplicate(dimension, title, content, existing)));
        }
        return new KnowledgeExtractNormalizeResult(result, dropped, truncated);
    }

    /// <summary>
    /// 在已有条目中查找可能重复的条目：先找同维度 title_key 相同的，再找任意维度 title_key 相同的，
    /// 最后找正文字符二元组 Jaccard 相似度 ≥ 0.8 中最相似的。
    /// </summary>
    /// <param name="dimension">候选维度。</param>
    /// <param name="title">候选标题。</param>
    /// <param name="content">候选内容。</param>
    /// <param name="existingItems">已有条目。</param>
    /// <returns>重复条目标识；没有则为空。</returns>
    public static Guid? FindDuplicate(BrandKnowledgeDimension dimension, string title, string content,
        IReadOnlyList<BrandKnowledgeItemView> existingItems)
    {
        ArgumentNullException.ThrowIfNull(existingItems);
        if (existingItems.Count == 0) return null;
        var key = TitleKey(title);
        var sameDimension = existingItems.FirstOrDefault(item => item.Dimension == dimension && TitleKey(item.Title) == key);
        if (sameDimension is not null) return sameDimension.ItemId;
        var sameTitle = existingItems.FirstOrDefault(item => TitleKey(item.Title) == key);
        if (sameTitle is not null) return sameTitle.ItemId;

        var bigrams = Bigrams(content);
        Guid? best = null;
        var bestScore = 0d;
        foreach (var item in existingItems)
        {
            var score = Jaccard(bigrams, Bigrams(item.Content));
            if (score >= DuplicateSimilarityThreshold && score > bestScore) { best = item.ItemId; bestScore = score; }
        }
        return best;
    }

    /// <summary>两段正文的字符二元组 Jaccard 相似度（NFKC、去空白、小写后计算；两者都为空时为 0）。</summary>
    /// <param name="left">正文一。</param>
    /// <param name="right">正文二。</param>
    public static double ContentSimilarity(string? left, string? right) => Jaccard(Bigrams(left), Bigrams(right));

    private static (string Title, string Content) NormalizeFields(BrandKnowledgeDimension dimension, string? rawTitle, string? rawContent)
    {
        if (!Enum.IsDefined(dimension)) throw Invalid("请选择有效的维度。");
        var title = NormalizeTitle(rawTitle);
        if (CharacterCount(title) is < 1 or > MaxTitleLength) throw Invalid($"标题长度必须为 1–{MaxTitleLength} 字。");
        var content = NormalizeContent(rawContent);
        if (CharacterCount(content) is < 1 or > MaxContentLength) throw Invalid($"内容长度必须为 1–{MaxContentLength} 字。");
        return (title, content);
    }

    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch)) { pendingSpace = builder.Length > 0; continue; }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>匹配用的紧凑形式：NFKC、去除全部空白、小写。网页换行与模型输出的空格差异不影响子串判断。</summary>
    private static string CompactForMatch(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            if (!char.IsWhiteSpace(ch)) builder.Append(char.ToLowerInvariant(ch));
        return builder.ToString();
    }

    private static HashSet<string> Bigrams(string? value)
    {
        var text = CompactForMatch(value);
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (text.Length == 1) set.Add(text);
        for (var index = 0; index + 1 < text.Length; index++) set.Add(text.Substring(index, 2));
        return set;
    }

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return 0;
        var intersection = left.Count(right.Contains);
        return (double)intersection / (left.Count + right.Count - intersection);
    }

    private static string TruncateRunes(string value, int maxRunes)
    {
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ >= maxRunes) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static CollectorStoreException Invalid(string message) => new("KNOWLEDGE_ITEM_VALIDATION", message, 400);
}
