using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jogeoai.Domain;

/// <summary>版本化提及识别所用的主体。</summary>
public sealed record AnalysisSubject(Guid Id, string Type, string Name);

/// <summary>某主体在不可变配置中的匹配词。</summary>
public sealed record AnalysisTerm(Guid SubjectId, string SubjectType, string Kind, string Key);

/// <summary>只从 subject_config_revisions 的不可变 JSON 读取的主体配置。</summary>
/// <param name="BrandId">品牌标识。</param>
/// <param name="Version">主体配置版本。</param>
/// <param name="Subjects">参与匹配的品牌和竞品。</param>
/// <param name="Terms">参与匹配的名称、别名和域名。</param>
/// <param name="IsComplete">快照是否包含可用于分析的完整字段。</param>
/// <param name="IncludeSubdomains">是否将配置域名的子域名视为命中。</param>
public sealed record SubjectConfiguration(Guid BrandId, int Version, IReadOnlyList<AnalysisSubject> Subjects,
    IReadOnlyList<AnalysisTerm> Terms, bool IsComplete, bool IncludeSubdomains = true)
{
    /// <summary>读取并校验不可变修订；旧版缺 terms 或 brandStatus 时保留为 unknown。</summary>
    public static SubjectConfiguration Parse(Guid siteId, int version, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("brandStatus", out var brandStatus)
            || !root.TryGetProperty("terms", out var terms) || terms.ValueKind != JsonValueKind.Array)
            return new SubjectConfiguration(siteId, version, [], [], false);
        var brandId = root.TryGetProperty("brandId", out var brandIdElement)
            && Guid.TryParse(brandIdElement.GetString(), out var parsedBrandId) ? parsedBrandId : siteId;
        var subjects = new List<AnalysisSubject>();
        if (brandStatus.GetString() == "active")
            subjects.Add(new AnalysisSubject(brandId, "brand", root.GetProperty("name").GetString() ?? ""));
        if (root.TryGetProperty("competitors", out var competitors) && competitors.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in competitors.EnumerateArray())
                if (entry.GetProperty("status").GetString() == "active"
                    && Guid.TryParse(entry.GetProperty("id").GetString(), out var competitorId))
                    subjects.Add(new AnalysisSubject(competitorId, "competitor", entry.GetProperty("name").GetString() ?? ""));
        }
        var ids = subjects.Select(s => s.Id).ToHashSet();
        var matches = new List<AnalysisTerm>();
        foreach (var term in terms.EnumerateArray())
        {
            if (!Guid.TryParse(term.GetProperty("subjectId").GetString(), out var id) || !ids.Contains(id)) continue;
            var key = term.GetProperty("key").GetString();
            if (!string.IsNullOrWhiteSpace(key))
                matches.Add(new AnalysisTerm(id, term.GetProperty("subjectType").GetString() ?? "",
                    term.GetProperty("termType").GetString() ?? "alias", key));
        }
        // Snapshots written before the onboarding setting existed retain the
        // historical subdomain behavior. New snapshots always persist the
        // explicit boolean, including false.
        var includeSubdomains = !root.TryGetProperty("includeSubdomains", out var includeSubdomainsElement)
            || includeSubdomainsElement.ValueKind != JsonValueKind.False && includeSubdomainsElement.ValueKind != JsonValueKind.True
            || includeSubdomainsElement.GetBoolean();
        return new SubjectConfiguration(brandId, version, subjects, matches, true, includeSubdomains);
    }
}

/// <summary>回答原文中的一处主体提及；偏移是原始 UTF-16 左闭右开下标。</summary>
public sealed record MentionMatch(Guid SubjectId, string SubjectType, string MatchedText, string Snippet,
    string Basis, int OffsetStart, int OffsetEnd);

/// <summary>提及识别器，只读取回答正文；引用 URL 不参与正文识别。</summary>
public static partial class MentionMatcher
{
    /// <summary>
    /// 命中规则及偏移映射版本。mention-v2：拉丁字母别名只受拉丁单词字符的边界约束（汉字相邻可命中）；
    /// 正文域名比较前去掉一个前导 www.；与汉字连写的域名取其中的 ASCII 片段作为补充候选。
    /// </summary>
    public const string Version = "mention-v2";

    private sealed record Candidate(AnalysisTerm Term, int Start, int End);

    [GeneratedRegex(@"(?<![\p{L}\p{N}_-])(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?\.)+[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?(?![\p{L}\p{N}_-])",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DomainPattern();

    /// <summary>返回互不重叠的原文命中；同 Check 同主体可有多处证据。</summary>
    public static IReadOnlyList<MentionMatch> Match(string answer, SubjectConfiguration configuration)
    {
        if (!configuration.IsComplete || string.IsNullOrEmpty(answer)) return [];
        var (normalized, starts, ends) = NormalizeWithOffsets(answer);
        var candidates = new List<Candidate>();
        foreach (var term in configuration.Terms)
        {
            if (term.Kind.Contains("domain", StringComparison.Ordinal)) continue;
            var needle = term.Key.Normalize(NormalizationForm.FormC).ToUpperInvariant();
            if (needle.Length == 0) continue;
            for (var offset = 0; offset < normalized.Length;)
            {
                var found = normalized.IndexOf(needle, offset, StringComparison.Ordinal);
                if (found < 0) break;
                var after = found + needle.Length;
                if ((!NeedsBoundary(needle[0]) || found == 0 || !IsLatinWord(normalized[found - 1]))
                    && (!NeedsBoundary(needle[^1]) || after == normalized.Length || !IsLatinWord(normalized[after])))
                    candidates.Add(new Candidate(term, starts[found], ends[after - 1]));
                offset = found + 1;
            }
        }
        var domainTerms = configuration.Terms.Where(t => t.Kind.Contains("domain", StringComparison.Ordinal)).ToArray();
        if (domainTerms.Length > 0)
        {
            foreach (Match match in DomainPattern().Matches(answer))
            {
                // 正则的标签字符含 \p{L}，"访问www.a.test" 或 "www.a.test获取" 会把汉字并进主机名；
                // 原主机名仍按 IDN 比较，另把被非拉丁字符隔开的 ASCII 片段作为补充候选。
                // 同一词项已由 ASCII 片段命中时不再保留混合主机名的整段候选（开启子域名时 "优联www.b.test"
                // 的 IDN 形式也以 ".b.test" 结尾），以免更长的整段吞掉相邻的汉字别名。
                var segmentTerms = new HashSet<AnalysisTerm>();
                if (match.Value.Any(c => c > '\u007f'))
                    foreach (var (start, length) in AsciiHostSegments(match.Value))
                        foreach (var term in MatchingDomainTerms(domainTerms, match.Value.Substring(start, length),
                                     configuration.IncludeSubdomains))
                        {
                            candidates.Add(new Candidate(term, match.Index + start, match.Index + start + length));
                            segmentTerms.Add(term);
                        }
                foreach (var term in MatchingDomainTerms(domainTerms, match.Value, configuration.IncludeSubdomains))
                    if (!segmentTerms.Contains(term))
                        candidates.Add(new Candidate(term, match.Index, match.Index + match.Length));
            }
        }
        var accepted = new List<Candidate>();
        foreach (var candidate in candidates.OrderByDescending(c => c.End - c.Start)
                     .ThenBy(c => c.Start).ThenBy(c => c.Term.SubjectId))
        {
            if (accepted.Any(other => candidate.Start < other.End && other.Start < candidate.End)) continue;
            accepted.Add(candidate);
        }
        return accepted.OrderBy(c => c.Start).Select(c =>
        {
            var snippetStart = Math.Max(0, c.Start - 40);
            var snippetEnd = Math.Min(answer.Length, c.End + 40);
            return new MentionMatch(c.Term.SubjectId, c.Term.SubjectType,
                answer[c.Start..c.End], answer[snippetStart..snippetEnd], c.Term.Kind, c.Start, c.End);
        }).ToArray();
    }

    private static IEnumerable<AnalysisTerm> MatchingDomainTerms(IReadOnlyList<AnalysisTerm> domainTerms,
        string text, bool includeSubdomains)
    {
        var host = text.TrimEnd('.').ToLowerInvariant();
        try { host = new IdnMapping().GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException) { return []; }
        return domainTerms.Where(term => DomainMatches(host, term.Key, includeSubdomains)).ToArray();
    }

    /// <summary>
    /// 主机名中与非 ASCII 字符直接连写（同一标签内、中间没有点）的 ASCII 片段（须含点且首尾为字母或数字）。
    /// 以点与非 ASCII 标签分隔的片段（如 IDN 子域名 例子.a.test 中的 a.test）属于真实主机名的一部分，不作为候选，
    /// 以免绕过子域名开关；与拉丁扩展字母相邻的片段也不作为候选，保持拉丁单词边界。
    /// </summary>
    private static IEnumerable<(int Start, int Length)> AsciiHostSegments(string host)
    {
        for (var i = 0; i < host.Length;)
        {
            if (host[i] > '\u007f') { i++; continue; }
            var start = i;
            while (i < host.Length && host[i] <= '\u007f') i++;
            var end = i;
            if (start > 0 && (IsLatinWord(host[start - 1]) || host[start] == '.')
                || end < host.Length && (IsLatinWord(host[end]) || host[end - 1] == '.')) continue;
            // 正则匹配首尾不会是点，上面又排除了以点与非 ASCII 标签相接的片段，因此片段首尾无需再去点。
            if (end - start < 3 || !char.IsAsciiLetterOrDigit(host[start]) || !char.IsAsciiLetterOrDigit(host[end - 1])
                || host.IndexOf('.', start, end - start) < 0) continue;
            yield return (start, end - start);
        }
    }

    /// <summary>拉丁单词字符：ASCII 字母和数字、下划线、U+00C0–U+024F 的字母；汉字等其他文字不构成拉丁词边界。</summary>
    private static bool IsLatinWord(char value) => char.IsAsciiLetterOrDigit(value) || value == '_'
        || value is >= '\u00c0' and <= '\u024f' && char.IsLetter(value);
    private static bool NeedsBoundary(char value) => value is >= 'A' and <= 'Z' or >= '0' and <= '9'
        || value is >= '\u00c0' and <= '\u024f' && char.IsLetter(value);

    /// <summary>主机名与配置域名各去掉一个前导 www. 后比较（与引用侧 normalized_url 一致）；其他子域名受开关控制。</summary>
    private static bool DomainMatches(string host, string configuredDomain, bool includeSubdomains)
    {
        host = StripWww(host);
        configuredDomain = StripWww(configuredDomain);
        return host == configuredDomain
            || includeSubdomains && host.EndsWith('.' + configuredDomain, StringComparison.Ordinal);
    }

    private static string StripWww(string host)
        => host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4 ? host[4..] : host;

    private static (string Text, int[] Starts, int[] Ends) NormalizeWithOffsets(string answer)
    {
        var positions = StringInfo.ParseCombiningCharacters(answer);
        var text = new StringBuilder(answer.Length);
        var starts = new List<int>(answer.Length);
        var ends = new List<int>(answer.Length);
        for (var i = 0; i < positions.Length; i++)
        {
            var start = positions[i];
            var end = i + 1 < positions.Length ? positions[i + 1] : answer.Length;
            var normalized = answer[start..end].Normalize(NormalizationForm.FormC).ToUpperInvariant();
            text.Append(normalized);
            foreach (var _ in normalized)
            {
                starts.Add(start);
                ends.Add(end);
            }
        }
        return (text.ToString(), starts.ToArray(), ends.ToArray());
    }
}
