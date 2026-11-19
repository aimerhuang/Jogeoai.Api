using System.Globalization;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>App 运营视图（implement 8.3 / 8.4）共用的 SQL 片段与解析。</summary>
internal static class AppAdminSql
{
    /// <summary>
    /// 每个节点的绑定汇总（别名 b）：绑定数、active 绑定数、绑定最近一次出现的时间。与节点表按 node_id 左连接。
    /// </summary>
    internal const string NodeBindingsJoin = """
        LEFT JOIN (SELECT node_id, COUNT(*) AS total, COALESCE(SUM(status = 'active'), 0) AS active, MAX(last_seen_at) AS seen
                   FROM collector_app_bindings GROUP BY node_id) b ON b.node_id = n.node_id
        """;

    /// <summary>
    /// 节点最近一次出现的时间：节点自己的 last_seen_at（只在绑定上报时刷新）与它任一绑定的 last_seen_at（领取、失败上报、结果提交也会刷新）中较晚的一个。
    /// 只看节点列会把一个只领取、暂时没有上报绑定的节点误报为失联。
    /// </summary>
    internal const string NodeLastActivity =
        "(CASE WHEN n.last_seen_at IS NULL THEN b.seen WHEN b.seen IS NULL OR b.seen < n.last_seen_at THEN n.last_seen_at ELSE b.seen END)";

    /// <summary>ISO 8601 UTC，毫秒精度，在 SQL 里生成：共用的行读取器把 DATETIME 转成只到秒的字符串。</summary>
    internal static string Iso(string column)
        => $"CASE WHEN {column} IS NULL THEN NULL ELSE CONCAT(DATE_FORMAT({column}, '%Y-%m-%dT%H:%i:%s.'), LPAD(FLOOR(MICROSECOND({column}) / 1000), 3, '0'), 'Z') END";

    internal static DateTimeOffset ParseIso(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    internal static DateTimeOffset? ParseIsoOrNull(string value) => value.Length == 0 ? null : ParseIso(value);

    internal static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    internal static int? IntOrNull(string value) => value.Length == 0 ? null : Int(value);

    internal static long Long(string value) => value.Length == 0 ? 0 : long.Parse(value, CultureInfo.InvariantCulture);

    internal static bool Flag(string value) => value is "1" or "True" or "true";

    internal static string? Text(string value) => value.Length == 0 ? null : value;

    /// <summary>平台在 <see cref="AppChannelDefinition.Platforms"/> 里的位置；未知平台排在最后。</summary>
    internal static int PlatformOrder(string code)
    {
        for (var i = 0; i < AppChannelDefinition.Engines.Count; i++)
            if (string.Equals(AppChannelDefinition.Engines[i], code, StringComparison.Ordinal)) return i;
        return int.MaxValue;
    }

    /// <summary>运营接口的 platformCode 参数：必须是 App 渠道已知的平台，返回规范的小写代码；否则 400 INVALID_PLATFORM。</summary>
    internal static string RequirePlatform(string? platformCode)
        => AppChannelDefinition.TryGetPlatform(platformCode)?.Code
           ?? throw new CollectorStoreException("INVALID_PLATFORM", "platformCode 不是 App 渠道的平台。", 400);
}
