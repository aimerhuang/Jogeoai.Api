using System.Security.Cryptography;
using System.Text;

namespace Jogeoai.Domain;

/// <summary>分类器返回并解析后的原始结论（尚未校验），不对外序列化。</summary>
/// <param name="State">模型给出的状态字符串。</param>
/// <param name="Confidence">模型给出的置信度。</param>
/// <param name="RationaleCode">模型给出的依据码。</param>
/// <param name="EvidenceQuote">模型给出的判定依据原文片段。</param>
/// <param name="Terms">模型给出的印象词候选。</param>
public sealed record ReputationClassifierOutput(string? State, double? Confidence, string? RationaleCode,
    string? EvidenceQuote, IReadOnlyList<ReputationTermCandidate> Terms);

/// <summary>模型给出的一个印象词候选，不对外序列化。</summary>
/// <param name="Text">短语原文。</param>
/// <param name="Polarity">极性字符串。</param>
public sealed record ReputationTermCandidate(string? Text, string? Polarity);

/// <summary>情感判定输入；品牌名称与别名来自分析版本冻结的主体配置。</summary>
/// <param name="BrandName">本品牌名称。</param>
/// <param name="BrandAliases">本品牌别名（不含域名）。</param>
/// <param name="ExcludedTerms">不能作为印象词的名称：品牌名 / 别名 / 域名、竞品名称与别名。</param>
/// <param name="Answer">完整回答原文；偏移与短语校验都基于该文本。</param>
/// <param name="MentionSnippets">现有提及分析给出的本品牌命中片段（可选上下文）。</param>
/// <param name="ConfidenceThreshold">置信度阈值，低于该值记 unknown + LOW_CONFIDENCE。</param>
public sealed record ReputationClassificationInput(string BrandName, IReadOnlyList<string> BrandAliases,
    IReadOnlyList<string> ExcludedTerms, string Answer, IReadOnlyList<string> MentionSnippets,
    double ConfidenceThreshold);

/// <summary>经过校验的情感判定结果；状态只可能是正面、中性、负面或未知（分类器永不返回未关联）。</summary>
/// <param name="State">判定状态。</param>
/// <param name="Confidence">置信度（0–1）；输出非法时为空。</param>
/// <param name="RationaleCode">依据码或未知原因码。</param>
/// <param name="EvidenceStart">依据片段起始 UTF-16 偏移（含）。</param>
/// <param name="EvidenceEnd">依据片段结束 UTF-16 偏移（不含）。</param>
/// <param name="Terms">通过原文与过滤规则校验的印象词；未知状态不保留印象词。</param>
/// <param name="ClassifierVersion">判定口径版本。</param>
/// <param name="ModelVersion">实际调用的模型。</param>
/// <param name="ModelQuote">判定为 unknown 时保留的模型原始依据片段（截断至 <see cref="ReputationRules.MaxModelQuoteLength"/>），用于排查；已判定时为空。</param>
public sealed record ReputationClassification(ReputationSentimentState State, double? Confidence,
    string RationaleCode, int? EvidenceStart, int? EvidenceEnd, IReadOnlyList<ReputationFactTerm> Terms,
    string ClassifierVersion, string? ModelVersion, string? ModelQuote = null);

/// <summary>口碑口径的纯函数：健康度、等级、显示占比、状态归并、分类结果校验与印象词过滤。全页只有这一套实现。</summary>
public static class ReputationRules
{
    /// <summary>健康度口径版本。</summary>
    public const string HealthVersion = "reputation-health-v1";
    /// <summary>情感判定口径版本（提示词、校验规则与停用词表）；停用词或校验规则变化时必须升级。</summary>
    public const string ClassifierVersion = "reputation-sentiment-v1";
    /// <summary>最低样本数。</summary>
    public const int MinSampleSize = 3;
    /// <summary>部分覆盖阈值。</summary>
    public const double PartialCoverageThreshold = 0.8;
    /// <summary>每种主要极性（正 / 中 / 负）返回的印象词数量。</summary>
    public const int TopTermsPerPolarity = 10;
    /// <summary>每条回答最多保留的印象词数。</summary>
    public const int MaxTermsPerAnswer = 30;
    /// <summary>印象词最大长度（字符）。</summary>
    public const int MaxTermLength = 32;
    /// <summary>判定依据片段最大长度（字符）。</summary>
    public const int MaxEvidenceLength = 400;

    /// <summary>未关联（短路，不调用模型）。</summary>
    public const string NotMentionedCode = "NOT_MENTIONED";
    /// <summary>置信度低于阈值。</summary>
    public const string LowConfidence = "LOW_CONFIDENCE";
    /// <summary>输出非法（结构、状态或置信度无效）。</summary>
    public const string InvalidOutput = "INVALID_OUTPUT";
    /// <summary>判定依据片段缺失或无法在回答原文中找到。</summary>
    public const string EvidenceNotFound = "EVIDENCE_NOT_FOUND";
    /// <summary>unknown 结果保留的模型依据片段最大字符数。</summary>
    public const int MaxModelQuoteLength = 400;
    /// <summary>格式容错匹配时，去掉格式字符后的依据片段至少保留的字符数，避免过短片段误命中。</summary>
    public const int MinTolerantEvidenceLength = 6;
    /// <summary>分类器认为未提及本品牌，与现有提及分析不一致。</summary>
    public const string MentionDisagree = "MENTION_DISAGREE";
    /// <summary>分类器自身给出 unknown。</summary>
    public const string ClassifierUnknown = "CLASSIFIER_UNKNOWN";
    /// <summary>模型依据码不在允许集合内时的归并码。</summary>
    public const string OtherRationale = "OTHER";

    /// <summary>模型可返回的依据码。</summary>
    public static readonly IReadOnlySet<string> ModelRationaleCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "PRAISE", "RECOMMENDATION", "FAVORABLE_COMPARISON", "FACTUAL_DESCRIPTION", "MIXED_VIEW",
        "CRITICISM", "RISK_WARNING", "UNFAVORABLE_COMPARISON", "INSUFFICIENT_CONTEXT"
    };

    /// <summary>健康度等级阈值，由高到低。</summary>
    public static readonly IReadOnlyList<ReputationLevelThreshold> LevelThresholds =
    [
        new(ReputationHealthLevel.Excellent, 80), new(ReputationHealthLevel.Good, 65),
        new(ReputationHealthLevel.Fair, 50), new(ReputationHealthLevel.Risk, 35),
        new(ReputationHealthLevel.HighRisk, 0)
    ];

    // 停用词与回答模板固定语；属于 reputation-sentiment-v1，修改须升级 ClassifierVersion。
    private static readonly HashSet<string> StopTerms = new(StringComparer.Ordinal)
    {
        "的", "了", "是", "和", "与", "及", "或", "在", "我们", "您", "你", "他们", "它", "这", "那", "这个", "那个",
        "一些", "一个", "可以", "可能", "非常", "比较", "以及", "等", "等等", "总之", "综上", "综上所述", "总的来说",
        "以下", "以下是", "如下", "首先", "其次", "最后", "另外", "此外", "同时", "需要注意", "值得注意", "建议",
        "推荐", "根据", "相关", "方面", "情况", "产品", "品牌", "公司", "服务", "用户", "选择", "参考", "希望对你有帮助",
        "希望对您有帮助", "仅供参考", "如有需要", "具体情况", "实际情况", "官方网站", "官网",
        "the", "a", "an", "and", "or", "of", "to", "in", "is", "are", "for", "with", "on", "it", "this", "that",
        "brand", "product", "company", "service", "overall", "however", "also", "very", "recommend", "official website"
    };

    // 平台名称（引擎显示名、厂商与常见别称）；口碑印象词不得是平台名。
    private static readonly string[] PlatformTerms =
    [
        "豆包", "doubao", "千问", "通义", "通义千问", "qwen", "deepseek", "深度求索", "kimi", "月之暗面", "智谱", "清言",
        "智谱清言", "glm", "chatgpt", "openai", "gpt", "gemini", "copilot", "perplexity", "google", "谷歌", "百度",
        "文心", "文心一言", "元宝", "腾讯元宝", "ai overview", "claude", "grok", "cloro"
    ];

    /// <summary>按三态计数计算任一维度的口碑分数；所有区块都必须调用本函数，禁止另设 0–1 分值。</summary>
    public static ReputationScore Score(long positive, long neutral, long negative,
        long unknown = 0, long pending = 0, long failed = 0)
    {
        var classified = positive + neutral + negative;
        var mentioned = classified + unknown + pending + failed;
        double? health = classified == 0 ? null : (positive * 100d + neutral * 50d) / classified;
        var coverage = MetricRatio.Of(classified, mentioned);
        return new ReputationScore(positive, neutral, negative, classified,
            MetricRatio.Of(positive, classified), MetricRatio.Of(neutral, classified), MetricRatio.Of(negative, classified),
            DisplayPercents(positive, neutral, negative), health, Level(health),
            classified > 0 && classified < MinSampleSize, unknown, pending, failed, mentioned, coverage,
            coverage.Value is { } value && value < PartialCoverageThreshold,
            mentioned == 0 ? "NO_MENTIONED_ANSWERS" : classified == 0 ? "NO_CLASSIFIED_SAMPLE" : null);
    }

    /// <summary>由一组事实的状态计数得到口碑分数。</summary>
    public static ReputationScore Score(IEnumerable<ReputationFact> facts)
    {
        long positive = 0, neutral = 0, negative = 0, unknown = 0, pending = 0, failed = 0;
        foreach (var fact in facts)
        {
            switch (fact.State)
            {
                case ReputationSentimentState.Positive: positive++; break;
                case ReputationSentimentState.Neutral: neutral++; break;
                case ReputationSentimentState.Negative: negative++; break;
                case ReputationSentimentState.Unknown: unknown++; break;
                case ReputationSentimentState.Pending: pending++; break;
                case ReputationSentimentState.Failed: failed++; break;
            }
        }
        return Score(positive, neutral, negative, unknown, pending, failed);
    }

    /// <summary>健康度等级：按四舍五入（远离零）取整后的健康度与 80 / 65 / 50 / 35 比较，使等级与整数显示一致。</summary>
    public static ReputationHealthLevel? Level(double? health)
    {
        if (health is null) return null;
        var rounded = Math.Round(health.Value, 0, MidpointRounding.AwayFromZero);
        return LevelThresholds.First(t => rounded >= t.MinHealth).Level;
    }

    /// <summary>三项显示占比：各自四舍五入（远离零）到 1 位小数，和不等于 100.0 时差额计入数量最大的一项（同数按正、中、负顺序取第一个）。</summary>
    public static ReputationDisplayPercents? DisplayPercents(long positive, long neutral, long negative)
    {
        var total = positive + neutral + negative;
        if (total == 0) return null;
        long[] counts = [positive, neutral, negative];
        var tenths = counts.Select(c => (long)Math.Round(c * 1000m / total, 0, MidpointRounding.AwayFromZero)).ToArray();
        var diff = 1000 - tenths.Sum();
        if (diff != 0)
        {
            var largest = 0;
            for (var i = 1; i < counts.Length; i++) if (counts[i] > counts[largest]) largest = i;
            tenths[largest] += diff;
        }
        return new ReputationDisplayPercents(tenths[0] / 10d, tenths[1] / 10d, tenths[2] / 10d);
    }

    /// <summary>
    /// 单条回答的唯一状态归并：无效回答 → not_applicable；有效但现有提及分析未提及本品牌 → not_mentioned（不依赖情感事实）；
    /// 已提及时取快照内可见的判定事实，事实为 not_mentioned（与提及分析冲突）时按 unknown；没有事实时按工作项是否重试耗尽区分 failed / pending。
    /// </summary>
    public static ReputationSentimentState ResolveState(bool eligible, bool brandMentioned,
        ReputationSentimentState? stored, bool workFailed)
    {
        if (!eligible) return ReputationSentimentState.NotApplicable;
        if (!brandMentioned) return ReputationSentimentState.NotMentioned;
        return stored switch
        {
            ReputationSentimentState.Positive or ReputationSentimentState.Neutral
                or ReputationSentimentState.Negative or ReputationSentimentState.Unknown => stored.Value,
            ReputationSentimentState.NotMentioned => ReputationSentimentState.Unknown,
            _ => workFailed ? ReputationSentimentState.Failed : ReputationSentimentState.Pending
        };
    }

    /// <summary>是否为已判定（正 / 中 / 负）状态。</summary>
    public static bool IsClassified(ReputationSentimentState state)
        => state is ReputationSentimentState.Positive or ReputationSentimentState.Neutral or ReputationSentimentState.Negative;

    /// <summary>状态的 API / 数据库字符串。</summary>
    public static string StateName(ReputationSentimentState state) => state switch
    {
        ReputationSentimentState.Positive => "positive",
        ReputationSentimentState.Neutral => "neutral",
        ReputationSentimentState.Negative => "negative",
        ReputationSentimentState.Unknown => "unknown",
        ReputationSentimentState.NotMentioned => "not_mentioned",
        ReputationSentimentState.Pending => "pending",
        ReputationSentimentState.Failed => "failed",
        _ => "not_applicable"
    };

    /// <summary>解析状态字符串（忽略大小写与首尾空白）；不支持时返回空。</summary>
    public static ReputationSentimentState? ParseState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "positive" => ReputationSentimentState.Positive,
        "neutral" => ReputationSentimentState.Neutral,
        "negative" => ReputationSentimentState.Negative,
        "unknown" => ReputationSentimentState.Unknown,
        "not_mentioned" => ReputationSentimentState.NotMentioned,
        "pending" => ReputationSentimentState.Pending,
        "failed" => ReputationSentimentState.Failed,
        "not_applicable" => ReputationSentimentState.NotApplicable,
        _ => null
    };

    /// <summary>极性的 API / 数据库字符串。</summary>
    public static string PolarityName(ReputationTermPolarity polarity) => polarity switch
    {
        ReputationTermPolarity.Positive => "positive",
        ReputationTermPolarity.Negative => "negative",
        _ => "neutral"
    };

    /// <summary>解析极性字符串；不支持时返回空。</summary>
    public static ReputationTermPolarity? ParsePolarity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "positive" => ReputationTermPolarity.Positive,
        "neutral" => ReputationTermPolarity.Neutral,
        "negative" => ReputationTermPolarity.Negative,
        _ => null
    };

    /// <summary>印象词规范化词键：NFKC、去首尾空白、合并连续空白、小写；回答列表 term 筛选使用同一函数。</summary>
    public static string NormalizeTerm(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        var builder = new StringBuilder(normalized.Length);
        var space = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character)) { space = true; continue; }
            if (space && builder.Length > 0) builder.Append(' ');
            space = false;
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    /// <summary>词键的 SHA-256 十六进制摘要。</summary>
    public static string TermHash(string termKey) => Sha256(termKey);

    /// <summary>
    /// 判定输入摘要：口径版本 + 品牌名 + 排序后的别名 + 排除词 + 置信度阈值 + 回答原文；相同摘要的历史判定可以复用，避免重复调用模型。
    /// 排除词（竞品、平台名等）或阈值变化会改变印象词过滤与 LOW_CONFIDENCE 结论，因此必须进入摘要，不能复用旧结论。
    /// </summary>
    public static string InputHash(string brandName, IEnumerable<string> aliases, string answer,
        IEnumerable<string>? excludedTerms = null, double? confidenceThreshold = null)
        => Sha256(string.Join('\n', ClassifierVersion, brandName, NormalizedSet(aliases), NormalizedSet(excludedTerms ?? []),
            confidenceThreshold?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "", answer));

    private static string NormalizedSet(IEnumerable<string> values)
        => string.Join('\u001f', values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

    /// <summary>
    /// 校验分类器输出：未关联 → unknown + MENTION_DISAGREE；状态或置信度非法 → unknown + INVALID_OUTPUT；分类器自身 unknown → CLASSIFIER_UNKNOWN；
    /// 置信度低于阈值 → LOW_CONFIDENCE；依据片段缺失或不在原文 → EVIDENCE_NOT_FOUND。通过时只保留能在原文找到、且不是品牌名 / 别名 / 平台名 / 停用词的印象词。
    /// </summary>
    public static ReputationClassification Evaluate(ReputationClassifierOutput? output,
        ReputationClassificationInput input, string? modelVersion)
    {
        var modelQuote = BoundQuote(output?.EvidenceQuote);
        ReputationClassification Unknown(string code, double? confidence = null)
            => new(ReputationSentimentState.Unknown, confidence, code, null, null, [], ClassifierVersion, modelVersion, modelQuote);
        if (output is null) return Unknown(InvalidOutput);
        var state = output.State?.Trim().ToLowerInvariant();
        if (state == "not_mentioned") return Unknown(MentionDisagree, ValidConfidence(output.Confidence));
        if (state is not ("positive" or "neutral" or "negative" or "unknown")) return Unknown(InvalidOutput);
        if (output.Confidence is not { } confidence || double.IsNaN(confidence) || confidence is < 0 or > 1)
            return Unknown(InvalidOutput);
        if (state == "unknown") return Unknown(ClassifierUnknown, confidence);
        if (confidence < input.ConfidenceThreshold) return Unknown(LowConfidence, confidence);
        var quote = output.EvidenceQuote?.Trim();
        if (string.IsNullOrEmpty(quote) || quote.Length > MaxEvidenceLength) return Unknown(EvidenceNotFound, confidence);
        if (FindEvidence(input.Answer, quote) is not var (start, end)) return Unknown(EvidenceNotFound, confidence);
        var rationale = output.RationaleCode?.Trim().ToUpperInvariant();
        rationale = rationale is not null && ModelRationaleCodes.Contains(rationale) ? rationale : OtherRationale;
        var parsed = ParseState(state)!.Value;
        return new ReputationClassification(parsed, confidence, rationale, start, end,
            ValidateTerms(output.Terms, input), ClassifierVersion, modelVersion);
    }

    private static string? BoundQuote(string? quote)
    {
        var trimmed = quote?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        var cleaned = new string(trimmed.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
        if (cleaned.Length <= MaxModelQuoteLength) return cleaned;
        var cut = char.IsHighSurrogate(cleaned[MaxModelQuoteLength - 1]) ? MaxModelQuoteLength - 1 : MaxModelQuoteLength;
        return cleaned[..cut];
    }

    /// <summary>
    /// 在回答原文中定位依据片段，返回 UTF-16 左闭右开偏移：先精确匹配（区分、再忽略大小写）；
    /// 仍找不到时做格式容错匹配：两侧都忽略 Markdown 加粗 / 行内代码标记（* 与 `）、表格分隔符（|）和全部空白，
    /// 命中后映射回原文中第一个与最后一个匹配字符的位置。容错匹配要求去掉格式后至少 <see cref="MinTolerantEvidenceLength"/> 个字符。
    /// </summary>
    public static (int Start, int End)? FindEvidence(string answer, string quote)
    {
        if (string.IsNullOrEmpty(answer) || string.IsNullOrEmpty(quote)) return null;
        var exact = answer.IndexOf(quote, StringComparison.Ordinal);
        if (exact < 0) exact = answer.IndexOf(quote, StringComparison.OrdinalIgnoreCase);
        if (exact >= 0) return (exact, exact + quote.Length);
        var (foldedQuote, _) = Fold(quote);
        if (foldedQuote.Length < MinTolerantEvidenceLength) return null;
        var (foldedAnswer, map) = Fold(answer);
        var index = foldedAnswer.IndexOf(foldedQuote, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var end = map[index + foldedQuote.Length - 1] + 1;
        // 末字符是高代理项时把低代理项一并包含，偏移不切开代理对。
        if (char.IsHighSurrogate(answer[end - 1]) && end < answer.Length) end++;
        return (map[index], end);
    }

    private static bool IsFormatting(char character) => char.IsWhiteSpace(character) || character is '*' or '|' or '`';

    // 去掉格式字符后的文本，以及每个保留字符在原文中的位置。
    private static (string Folded, int[] Map) Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (IsFormatting(text[i])) continue;
            builder.Append(text[i]);
            map.Add(i);
        }
        return (builder.ToString(), map.ToArray());
    }

    private static double? ValidConfidence(double? value)
        => value is { } v && !double.IsNaN(v) && v is >= 0 and <= 1 ? v : null;

    /// <summary>印象词过滤：去重后最多 30 个；必须在原文中出现（忽略大小写），出现次数由原文实际计数，不采信模型计数。</summary>
    public static IReadOnlyList<ReputationFactTerm> ValidateTerms(IReadOnlyList<ReputationTermCandidate>? candidates,
        ReputationClassificationInput input)
    {
        if (candidates is null || candidates.Count == 0) return [];
        var excluded = input.ExcludedTerms.Append(input.BrandName).Concat(input.BrandAliases).Concat(PlatformTerms)
            .Select(NormalizeTerm).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var result = new List<ReputationFactTerm>();
        var seen = new HashSet<(string, ReputationTermPolarity)>();
        foreach (var candidate in candidates)
        {
            if (result.Count >= MaxTermsPerAnswer) break;
            var text = candidate.Text?.Trim();
            var polarity = ParsePolarity(candidate.Polarity);
            if (polarity is null || string.IsNullOrEmpty(text) || text.Length > MaxTermLength) continue;
            if (text.Any(char.IsControl) || !text.Any(char.IsLetter)) continue;
            var key = NormalizeTerm(text);
            if (key.Length < 2 && !key.Any(IsCjk)) continue;
            if (StopTerms.Contains(key)) continue;
            // 品牌名 / 别名 / 域名 / 竞品 / 平台名本身或包含它们的短语都不是对本品牌的描述。
            if (excluded.Any(name => key == name || name.Length >= 2 && ContainsName(key, name))) continue;
            var occurrences = CountOccurrences(input.Answer, text);
            if (occurrences == 0) continue;
            if (!seen.Add((key, polarity.Value))) continue;
            result.Add(new ReputationFactTerm(key, text, polarity.Value, occurrences));
        }
        return result;
    }

    /// <summary>不重叠、忽略大小写的出现次数；原文中找不到时按依据片段同样的格式容错规则（忽略 * ` | 与空白）再计一次。</summary>
    public static int CountOccurrences(string text, string phrase)
    {
        if (string.IsNullOrEmpty(phrase)) return 0;
        var count = CountExact(text, phrase);
        if (count > 0) return count;
        var (foldedPhrase, _) = Fold(phrase);
        return foldedPhrase.Length == 0 ? 0 : CountExact(Fold(text).Folded, foldedPhrase);
    }

    private static int CountExact(string text, string phrase)
    {
        var count = 0;
        for (var index = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase); index >= 0;
             index = text.IndexOf(phrase, index + phrase.Length, StringComparison.OrdinalIgnoreCase))
            count++;
        return count;
    }

    private static bool IsCjk(char character) => character is >= '一' and <= '鿿';

    /// <summary>
    /// 词键是否包含排除名称：名称边缘是 ASCII 字母或数字时，该侧要求 ASCII 词边界（"arc" 不命中 "research"、"box" 不命中 "toolbox"）；
    /// 中文等边缘按子串命中（"小米" 命中 "小米手机"，"acme" 命中 "acme的售后"）。
    /// </summary>
    internal static bool ContainsName(string key, string name)
    {
        var checkStart = char.IsAsciiLetterOrDigit(name[0]);
        var checkEnd = char.IsAsciiLetterOrDigit(name[^1]);
        for (var index = key.IndexOf(name, StringComparison.Ordinal); index >= 0;
             index = key.IndexOf(name, index + 1, StringComparison.Ordinal))
        {
            var end = index + name.Length;
            if ((!checkStart || index == 0 || !char.IsAsciiLetterOrDigit(key[index - 1]))
                && (!checkEnd || end == key.Length || !char.IsAsciiLetterOrDigit(key[end])))
                return true;
        }
        return false;
    }

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>印象词主要极性：贡献回答数最多；同数比出现次数；仍相同时负面 → 中性 → 正面（风险优先）。</summary>
    public static ReputationTermPolarity DominantPolarity(long positiveAnswers, long neutralAnswers, long negativeAnswers,
        long positiveOccurrences, long neutralOccurrences, long negativeOccurrences)
    {
        var candidates = new[]
        {
            (Polarity: ReputationTermPolarity.Negative, Answers: negativeAnswers, Occurrences: negativeOccurrences),
            (Polarity: ReputationTermPolarity.Neutral, Answers: neutralAnswers, Occurrences: neutralOccurrences),
            (Polarity: ReputationTermPolarity.Positive, Answers: positiveAnswers, Occurrences: positiveOccurrences)
        };
        return candidates.OrderByDescending(c => c.Answers).ThenByDescending(c => c.Occurrences).First().Polarity;
    }
}
