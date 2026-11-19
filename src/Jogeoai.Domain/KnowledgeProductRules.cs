using System.Text;

namespace Jogeoai.Domain;

/// <summary>产品信息的字段校验、识别结果合并、核验门禁与知识库 Markdown 模板。</summary>
public static class KnowledgeProductRules
{
    /// <summary>产品名长度上限。</summary>
    public const int MaxNameLength = 25;
    /// <summary>长文本字段长度上限。</summary>
    public const int MaxTextLength = 1000;
    /// <summary>规格参数项数上限。</summary>
    public const int MaxSpecs = 50;
    /// <summary>规格参数名长度上限。</summary>
    public const int MaxSpecNameLength = 30;
    /// <summary>规格参数值长度上限。</summary>
    public const int MaxSpecValueLength = 200;
    /// <summary>标签数量上限。</summary>
    public const int MaxTags = 10;
    /// <summary>每租户每日（UTC）识别次数上限（PRD D10）。</summary>
    public const int DailyParseLimit = 50;

    /// <summary>可识别、可核验的字段名，顺序即表单顺序。</summary>
    public static readonly IReadOnlyList<string> FieldKeys =
        ["name", "introduction", "advantages", "scenarios", "cases", "specs"];

    /// <summary>规范化并校验写入字段；入库时产品介绍必填。</summary>
    /// <param name="write">原始字段。</param>
    /// <param name="requireIntroduction">是否要求产品介绍非空。</param>
    /// <exception cref="CollectorStoreException">PRODUCT_VALIDATION（400）。</exception>
    public static KnowledgeProductWrite Normalize(KnowledgeProductWrite write, bool requireIntroduction)
    {
        ArgumentNullException.ThrowIfNull(write);
        var name = (write.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > MaxNameLength)
            throw Invalid($"产品名长度必须为 1–{MaxNameLength} 字。");
        var introduction = NormalizeText(write.Introduction, "产品介绍");
        if (requireIntroduction && introduction is null) throw Invalid("产品介绍不能为空。");
        var specs = NormalizeSpecs(write.Specs);
        var tags = NormalizeTags(write.Tags);
        return new KnowledgeProductWrite(name, introduction, NormalizeText(write.Advantages, "优势"),
            NormalizeText(write.Scenarios, "适用场景与人群"), NormalizeText(write.Cases, "客户案例"),
            specs, NormalizeUrl(write.ProductUrl), tags);
    }

    /// <summary>把识别结果合并到当前字段：人工填写的字段保留，空字段或 AI 来源字段被覆盖。</summary>
    /// <param name="current">当前字段。</param>
    /// <param name="currentSources">当前字段来源。</param>
    /// <param name="result">识别输出。</param>
    public static (KnowledgeProductWrite Fields, IReadOnlyDictionary<string, ProductFieldSource> Sources) Merge(
        KnowledgeProductWrite current, IReadOnlyDictionary<string, ProductFieldSource>? currentSources, ProductParseResult result)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(result);
        var sources = new Dictionary<string, ProductFieldSource>(currentSources ?? new Dictionary<string, ProductFieldSource>(), StringComparer.Ordinal);
        string? Text(string key, string? existing, string? proposed)
        {
            var manual = !string.IsNullOrWhiteSpace(existing) && IsManual(sources, key);
            if (manual) { sources[key] = Manual(); return existing; }
            if (result.FieldSources.TryGetValue(key, out var source)) sources[key] = source with { Origin = "ai", Verified = false };
            else if (string.IsNullOrWhiteSpace(proposed)) sources.Remove(key);
            return string.IsNullOrWhiteSpace(proposed) ? (string.IsNullOrWhiteSpace(existing) ? null : existing) : proposed;
        }
        var name = Text("name", current.Name, result.Fields.Name) ?? current.Name;
        var introduction = Text("introduction", current.Introduction, result.Fields.Introduction);
        var advantages = Text("advantages", current.Advantages, result.Fields.Advantages);
        var scenarios = Text("scenarios", current.Scenarios, result.Fields.Scenarios);
        var cases = Text("cases", current.Cases, result.Fields.Cases);
        IReadOnlyList<ProductSpecItem> specs;
        if (current.Specs is { Count: > 0 } && IsManual(sources, "specs"))
        { specs = current.Specs; sources["specs"] = Manual(); }
        else
        {
            specs = result.Fields.Specs is { Count: > 0 } ? result.Fields.Specs : current.Specs ?? [];
            if (result.FieldSources.TryGetValue("specs", out var specSource)) sources["specs"] = specSource with { Origin = "ai", Verified = false };
        }
        // 产品名人工创建时就已存在，识别结果只在名称仍为占位时覆盖。
        var fields = new KnowledgeProductWrite(name.Length > MaxNameLength ? name[..MaxNameLength] : name, introduction, advantages,
            scenarios, cases, specs, current.ProductUrl ?? result.Fields.ProductUrl, current.Tags);
        return (fields, sources);
    }

    /// <summary>以人工编辑覆盖字段：值发生变化的字段标记为 manual 且已核对，其余来源保留。</summary>
    /// <param name="previous">编辑前字段。</param>
    /// <param name="next">编辑后字段。</param>
    /// <param name="sources">编辑前字段来源。</param>
    public static IReadOnlyDictionary<string, ProductFieldSource> ApplyManualEdit(KnowledgeProductWrite? previous,
        KnowledgeProductWrite next, IReadOnlyDictionary<string, ProductFieldSource>? sources)
    {
        ArgumentNullException.ThrowIfNull(next);
        var result = new Dictionary<string, ProductFieldSource>(sources ?? new Dictionary<string, ProductFieldSource>(), StringComparer.Ordinal);
        void Track(string key, string? before, string? after)
        {
            if (string.IsNullOrWhiteSpace(after)) { result.Remove(key); return; }
            if (!string.Equals(before?.Trim(), after.Trim(), StringComparison.Ordinal) || !result.ContainsKey(key))
                result[key] = Manual();
        }
        Track("name", previous?.Name, next.Name);
        Track("introduction", previous?.Introduction, next.Introduction);
        Track("advantages", previous?.Advantages, next.Advantages);
        Track("scenarios", previous?.Scenarios, next.Scenarios);
        Track("cases", previous?.Cases, next.Cases);
        var beforeSpecs = SpecsKey(previous?.Specs);
        var afterSpecs = SpecsKey(next.Specs);
        if (next.Specs is not { Count: > 0 }) result.Remove("specs");
        else if (!string.Equals(beforeSpecs, afterSpecs, StringComparison.Ordinal) || !result.ContainsKey("specs")) result["specs"] = Manual();
        return result;
    }

    /// <summary>核验门禁：所有 origin=ai 且 found 的字段必须出现在已核对列表中。</summary>
    /// <param name="fields">核验后的字段。</param>
    /// <param name="sources">字段来源。</param>
    /// <param name="verifiedFields">用户逐个点击「已核对」的字段名。</param>
    /// <returns>更新 verified 标记后的来源。</returns>
    /// <exception cref="CollectorStoreException">PRODUCT_FIELDS_UNVERIFIED（409）。</exception>
    public static IReadOnlyDictionary<string, ProductFieldSource> Verify(KnowledgeProductWrite fields,
        IReadOnlyDictionary<string, ProductFieldSource>? sources, IReadOnlyList<string>? verifiedFields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var verified = new HashSet<string>((verifiedFields ?? []).Select(item => item.Trim()), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ProductFieldSource>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var (key, source) in sources ?? new Dictionary<string, ProductFieldSource>())
        {
            if (!FieldKeys.Contains(key, StringComparer.Ordinal)) continue;
            var hasValue = HasValue(fields, key);
            if (source.Origin == "ai" && source.Found && hasValue && !source.Verified && !verified.Contains(key))
            { missing.Add(key); continue; }
            result[key] = source with { Verified = source.Verified || verified.Contains(key) || source.Origin == "manual" || !hasValue };
        }
        if (missing.Count > 0)
            throw new CollectorStoreException("PRODUCT_FIELDS_UNVERIFIED", $"以下 AI 生成字段尚未核对：{string.Join("、", missing)}。", 409);
        return result;
    }

    /// <summary>按固定模板把已入库产品渲染为知识库 Markdown。</summary>
    /// <param name="product">产品信息。</param>
    /// <param name="brandName">关联品牌名。</param>
    public static string RenderMarkdown(KnowledgeProductView product, string brandName)
    {
        ArgumentNullException.ThrowIfNull(product);
        var builder = new StringBuilder();
        builder.Append("# ").Append(product.Name.Trim()).Append('\n').Append('\n');
        builder.Append("- 关联品牌：").Append(string.IsNullOrWhiteSpace(brandName) ? "（未填写）" : brandName.Trim()).Append('\n');
        builder.Append("- 产品标识：").Append(product.ProductId.ToString()).Append('\n');
        if (!string.IsNullOrWhiteSpace(product.ProductUrl)) builder.Append("- 产品页面：").Append(product.ProductUrl.Trim()).Append('\n');
        if (product.Tags.Count > 0) builder.Append("- 标签：").Append(string.Join("、", product.Tags)).Append('\n');
        builder.Append('\n');
        Section(builder, "产品介绍", product.Introduction);
        Section(builder, "优势", product.Advantages);
        Section(builder, "适用场景与人群", product.Scenarios);
        Section(builder, "客户案例", product.Cases);
        if (product.Specs.Count > 0)
        {
            builder.Append("## 规格参数\n\n| 参数名 | 参数值 |\n| --- | --- |\n");
            foreach (var spec in product.Specs)
                builder.Append("| ").Append(Cell(spec.Name)).Append(" | ").Append(Cell(spec.Value)).Append(" |\n");
            builder.Append('\n');
        }
        builder.Append("## 来源\n\n");
        builder.Append("- 录入方式：").Append(SourceLabel(product.SourceKind)).Append('\n');
        if (product.SourceDocumentId is not null) builder.Append("- 来源资料：").Append(product.SourceDocumentId.Value.ToString()).Append('\n');
        if (product.ApprovedAt is not null) builder.Append("- 核验入库：").Append(product.ApprovedAt.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")).Append('\n');
        return builder.ToString();
    }

    private static void Section(StringBuilder builder, string title, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        builder.Append("## ").Append(title).Append("\n\n").Append(body.Trim()).Append("\n\n");
    }

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ').Trim();

    private static string SourceLabel(string sourceKind) => sourceKind switch
    {
        "file" => "素材库文件识别",
        "url" => "网址识别",
        "completion" => "AI 智能补全",
        _ => "人工填写"
    };

    private static bool HasValue(KnowledgeProductWrite fields, string key) => key switch
    {
        "name" => !string.IsNullOrWhiteSpace(fields.Name),
        "introduction" => !string.IsNullOrWhiteSpace(fields.Introduction),
        "advantages" => !string.IsNullOrWhiteSpace(fields.Advantages),
        "scenarios" => !string.IsNullOrWhiteSpace(fields.Scenarios),
        "cases" => !string.IsNullOrWhiteSpace(fields.Cases),
        "specs" => fields.Specs is { Count: > 0 },
        _ => false
    };

    private static bool IsManual(IReadOnlyDictionary<string, ProductFieldSource> sources, string key)
        => !sources.TryGetValue(key, out var source) || source.Origin == "manual";

    private static ProductFieldSource Manual() => new("manual", true, true, false, []);

    private static string SpecsKey(IReadOnlyList<ProductSpecItem>? specs)
        => specs is null ? string.Empty : string.Join("\u001e", specs.Select(item => $"{item.Name.Trim()}\u001f{item.Value.Trim()}"));

    private static string? NormalizeText(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > MaxTextLength) throw Invalid($"{label}不能超过 {MaxTextLength} 字。");
        return text;
    }

    private static IReadOnlyList<ProductSpecItem> NormalizeSpecs(IReadOnlyList<ProductSpecItem>? specs)
    {
        if (specs is null) return [];
        var result = new List<ProductSpecItem>();
        foreach (var item in specs)
        {
            if (item is null) continue;
            var name = (item.Name ?? string.Empty).Trim();
            var value = (item.Value ?? string.Empty).Trim();
            if (name.Length == 0 && value.Length == 0) continue;
            if (name.Length is < 1 or > MaxSpecNameLength) throw Invalid($"规格参数名长度必须为 1–{MaxSpecNameLength} 字。");
            if (value.Length is < 1 or > MaxSpecValueLength) throw Invalid($"规格参数值长度必须为 1–{MaxSpecValueLength} 字。");
            result.Add(new ProductSpecItem(name, value));
        }
        if (result.Count > MaxSpecs) throw Invalid($"规格参数最多 {MaxSpecs} 项。");
        return result;
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        var result = (tags ?? []).Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().Normalize(System.Text.NormalizationForm.FormKC)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length > MaxTags) throw Invalid($"标签最多 {MaxTags} 个。");
        if (result.Any(item => item.Length > 128)) throw Invalid("单个标签不能超过 128 个字符。");
        return result;
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw Invalid("产品页面网址必须是公开 HTTP(S) 地址。");
        return text;
    }

    private static CollectorStoreException Invalid(string message) => new("PRODUCT_VALIDATION", message, 400);
}
