using System.Text.Json;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 读取回答元数据（visibility_checks.result_metadata）里的 app 块（results.md 8.1、8.3）。写入在
/// <see cref="AppCollectorPoolStore.AppMetadata(AppResultCommand, string, string, int, int)"/>；这里不返回账号代号。
/// </summary>
internal static class AppAnswerMetadataCodec
{
    /// <summary>app 块里表示“没有 URL 的引用来源被截断”的成员名。</summary>
    internal const string TruncatedMember = "sourcesWithoutUrlTruncated";

    /// <summary>
    /// 解析 app 块；不是 App 回答、没有该块或内容无法解析时两项都为空。
    /// 早于截断标记写入的回答没有 <see cref="TruncatedMember"/>，此时按条数差判断。
    /// </summary>
    internal static (AppAnswerView? App, IReadOnlyList<AppSourceWithoutUrlView>? SourcesWithoutUrl) Decode(string? resultMetadata)
    {
        if (string.IsNullOrWhiteSpace(resultMetadata)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(resultMetadata);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("app", out var app) || app.ValueKind != JsonValueKind.Object)
                return (null, null);
            var sources = new List<AppSourceWithoutUrlView>();
            if (app.TryGetProperty("sourcesWithoutUrl", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object)
                        sources.Add(new AppSourceWithoutUrlView(Text(item, "title"), Text(item, "site"), Text(item, "list")));
            var total = Number(app, "citationsTotal");
            var withUrl = Number(app, "citationsWithUrl");
            var truncated = app.TryGetProperty(TruncatedMember, out var marker) && marker.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? marker.GetBoolean()
                : total - withUrl > sources.Count;
            return (new AppAnswerView(Text(app, "deviceId"), Text(app, "androidVersion"), Text(app, "appPackage"), Text(app, "appVersion"),
                State(app, "webSearch"), State(app, "deepThinking"),
                AppContractNames.TryParse<AppCitationsStatus>(Text(app, "citationsStatus"), out var status) ? status : AppCitationsStatus.NotDeclared,
                Text(app, "citationsIssue"), total, withUrl, truncated, Text(app, "screenshotSha256"), Number(app, "promptRuleVersion")), sources);
        }
        catch (JsonException) { return (null, null); }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static AppVisibleState State(JsonElement element, string name)
        => AppContractNames.TryParse<AppVisibleState>(Text(element, name), out var state) ? state : AppVisibleState.Unknown;
}
