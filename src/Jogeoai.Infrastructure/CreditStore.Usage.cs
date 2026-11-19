using System.Globalization;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class CreditStore
{
    // 成员范围与 ListLedgerAsync 同一谓词：本人发起的流水，或本人请求的文章批次所属预占。监控结算没有 actor，成员看不到。
    private const string ActorScopeSql = " AND (l.actor_user_id=UUID_TO_BIN(@actor) OR EXISTS (SELECT 1 FROM credit_reservations r JOIN article_generation_batches b ON b.tenant_id=r.tenant_id AND b.id=r.batch_id WHERE r.tenant_id=l.tenant_id AND r.id=l.reservation_id AND b.requested_by=UUID_TO_BIN(@actor)))";

    // capture 行 amount 恒为 0；真实金额来自文章预占（credit_reservations.amount）、监控计费项（monitoring_credit_items.amount）、
    // 文章链接监控轮次（article_monitoring_rounds.captured_credits，迁移 027）或智能微调操作（article_refine_operations.credits，迁移 041）。
    // 最后一列是监控检测的 Provider（由它推导渠道）；不关联监控检测的流水为空。
    private const string UsageSelectSql = """
        SELECT l.created_at, (l.article_version_id IS NOT NULL OR r.id IS NOT NULL), (l.monitoring_check_id IS NOT NULL),
               COALESCE(r.amount, i.amount, m.captured_credits, 0), (l.article_monitoring_round_id IS NOT NULL), 0,
               vc.provider
        FROM credit_ledger l
        LEFT JOIN credit_reservations r ON r.tenant_id = l.tenant_id AND r.id = l.reservation_id
        LEFT JOIN monitoring_credit_items i ON i.tenant_id = l.tenant_id AND i.check_id = l.monitoring_check_id
        LEFT JOIN article_monitoring_rounds m ON m.tenant_id = l.tenant_id AND m.id = l.article_monitoring_round_id
        LEFT JOIN visibility_checks vc ON vc.tenant_id = l.tenant_id AND vc.id = l.monitoring_check_id
        WHERE l.tenant_id = UUID_TO_BIN(@tenant) AND l.entry_type = 'capture'
        """;

    // 041 之后的同口径查询：多关联一张微调操作表；未执行 041 的库沿用上面的查询（refine_operation_id 列不存在）。
    private const string UsageSelectWithRefineSql = """
        SELECT l.created_at, (l.article_version_id IS NOT NULL OR r.id IS NOT NULL), (l.monitoring_check_id IS NOT NULL),
               COALESCE(r.amount, i.amount, m.captured_credits, o.credits, 0), (l.article_monitoring_round_id IS NOT NULL),
               (l.refine_operation_id IS NOT NULL), vc.provider
        FROM credit_ledger l
        LEFT JOIN credit_reservations r ON r.tenant_id = l.tenant_id AND r.id = l.reservation_id
        LEFT JOIN monitoring_credit_items i ON i.tenant_id = l.tenant_id AND i.check_id = l.monitoring_check_id
        LEFT JOIN article_monitoring_rounds m ON m.tenant_id = l.tenant_id AND m.id = l.article_monitoring_round_id
        LEFT JOIN article_refine_operations o ON o.tenant_id = l.tenant_id AND o.id = l.refine_operation_id
        LEFT JOIN visibility_checks vc ON vc.tenant_id = l.tenant_id AND vc.id = l.monitoring_check_id
        WHERE l.tenant_id = UUID_TO_BIN(@tenant) AND l.entry_type = 'capture'
        """;

    /// <inheritdoc />
    public async Task<CreditUsageView> UsageAsync(Guid tenantId, Guid? actorUserId, CreditUsageRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var zoneId = request.TimeZone;
        TimeZoneInfo zone;
        if (zoneId is not null) zone = CollectionScheduleCalendar.ParseTimeZone(zoneId);
        else (zoneId, zone) = await DefaultZoneAsync(connection, tenantId, cancellationToken);
        var to = request.To ?? CreditUsageAggregator.LocalDate(DateTimeOffset.UtcNow, zone);
        var from = request.From ?? to.AddDays(-29);
        CreditUsageAggregator.ValidateRange(from, to);
        var (fromUtc, toUtc) = CreditUsageAggregator.UtcWindow(from, to, zone);
        var scope = actorUserId is null ? "" : ActorScopeSql;
        var select = await ArticleEditorSchema.ExistsAsync(connection, cancellationToken) ? UsageSelectWithRefineSql : UsageSelectSql;
        var rows = await RowsAsync(connection, null,
            select + " AND l.created_at >= @from AND l.created_at < @to" + scope + " ORDER BY l.created_at, l.id;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@actor", actorUserId?.ToString()),
            ("@from", fromUtc.UtcDateTime), ("@to", toUtc.UtcDateTime));
        var latest = await RowAsync(connection, null,
            select + scope + " ORDER BY l.created_at DESC, l.id DESC LIMIT 1;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@actor", actorUserId?.ToString()));
        return CreditUsageAggregator.Build(from, to, zoneId, zone, rows.Select(ToUsageRecord).ToArray(),
            latest is null ? null : ToUsageRecord(latest));
    }

    private static CreditUsageRecord ToUsageRecord(string[] row)
        => new(ParseDate(row[0]), Flag(row[1]), Flag(row[2]), decimal.Parse(row[3], CultureInfo.InvariantCulture), Flag(row[4]),
            row.Length > 5 && Flag(row[5]), row.Length > 6 && row[6].Length > 0 ? row[6] : null);

    private static bool Flag(string value) => value is "1" or "True" or "true";

    /// <summary>默认时区：最早创建的启用品牌的 site_brand_settings.time_zone；无品牌或时区无法解析时为 Asia/Shanghai。</summary>
    private static async Task<(string Id, TimeZoneInfo Zone)> DefaultZoneAsync(MySqlConnection connection, Guid tenantId,
        CancellationToken cancellationToken)
    {
        var row = await RowAsync(connection, null, """
            SELECT b.time_zone FROM sites s JOIN site_brand_settings b ON b.site_id = s.id AND b.tenant_id = s.tenant_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.status = 'active' ORDER BY s.created_at, s.id LIMIT 1;
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        var candidate = row is null || string.IsNullOrWhiteSpace(row[0]) ? TeamSummaryProjector.DefaultTimeZone : row[0];
        try { return (candidate, TimeZoneInfo.FindSystemTimeZoneById(candidate)); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        { return (TeamSummaryProjector.DefaultTimeZone, TimeZoneInfo.FindSystemTimeZoneById(TeamSummaryProjector.DefaultTimeZone)); }
    }
}
