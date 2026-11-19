using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.AppAdminSql;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 每日告警汇报的生成与存档（results.md 8.4，collector_app_daily_reports）。汇报内容就是 <see cref="IAppAlertQuery"/> 的结果，
/// 与运营接口的告警视图同一个查询、同一个 JSON 形状；正文由 <see cref="AppDailyReportRules.Render"/> 写成。
/// 每个日期一份：唯一键 report_date 保证重复运行、并发运行都只存下一份。存档之后才交给发送渠道，发送失败不影响存档。
/// </summary>
/// <param name="database">业务数据库。</param>
/// <param name="alerts">告警信息查询。</param>
/// <param name="sender">发送渠道；首期是什么都不发的 <see cref="NotConfiguredDailyReportSender"/>。</param>
public sealed class AppDailyReportStore(MySqlDatabase database, IAppAlertQuery alerts, IDailyReportSender sender) : IAppDailyReportStore
{
    /// <summary>汇报内容的 JSON 形状：与 API 的响应相同（camelCase，枚举写成契约字符串）。</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const int MaxSendErrorLength = 1000;

    /// <inheritdoc />
    public async Task<AppDailyReportGeneration> GenerateAsync(DateOnly reportDate, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        await AppChannelSchema.RequireAsync(connection, null, ct);
        if (await ReadAsync(connection, reportDate, ct) is { } existing) return new AppDailyReportGeneration(false, existing);

        var view = await alerts.GetAlertsAsync(ct);
        var summary = JsonSerializer.Serialize(view, Json);
        if (AppCollectorPoolStore.StoredJsonBytes(summary) > AppDailyReportRules.MaxSummaryBytes)
            throw new CollectorStoreException("APP_DAILY_REPORT_TOO_LARGE", "告警信息超过汇报内容的大小上限，汇报未生成。", 500);
        try
        {
            await ExecuteAsync(connection, null, """
                INSERT INTO collector_app_daily_reports (id, report_date, generated_at, has_alerts, summary, body, delivery_status)
                VALUES (UUID_TO_BIN(@id), @date, UTC_TIMESTAMP(6), @alerts, @summary, @body, @status);
                """, ct, ("@id", Guid.NewGuid().ToString()), ("@date", Day(reportDate)), ("@alerts", view.HasAlerts), ("@summary", summary),
                ("@body", AppDailyReportRules.Render(reportDate, view)),
                ("@status", AppContractNames.ToStorage(AppDailyReportDeliveryStatus.NotConfigured)));
        }
        // Another run stored this date first: that one is the report of the day.
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return new AppDailyReportGeneration(false, (await ReadAsync(connection, reportDate, ct))!);
        }
        var report = (await ReadAsync(connection, reportDate, ct))!;

        // Archived first; sending is a separate, best-effort step (KD-19: the only sender today sends nothing).
        AppDailyReportDelivery delivery;
        try { delivery = await sender.SendAsync(report, ct); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could carry a webhook address or a token.
            delivery = new AppDailyReportDelivery(AppDailyReportDeliveryStatus.Failed, exception.GetType().Name);
        }
        if (delivery.Status == AppDailyReportDeliveryStatus.NotConfigured) return new AppDailyReportGeneration(true, report);
        var error = delivery.Error is { } text && text.Length > MaxSendErrorLength ? text[..MaxSendErrorLength] : delivery.Error;
        await ExecuteAsync(connection, null, """
            UPDATE collector_app_daily_reports
            SET delivery_status = @status, sent_at = CASE WHEN @status = 'sent' THEN UTC_TIMESTAMP(6) ELSE NULL END, send_error = @error
            WHERE report_date = @date;
            """, ct, ("@status", AppContractNames.ToStorage(delivery.Status)), ("@error", error), ("@date", Day(reportDate)));
        return new AppDailyReportGeneration(true, (await ReadAsync(connection, reportDate, ct))!);
    }

    /// <inheritdoc />
    public async Task<CursorPage<AppDailyReportItem>> ListAsync(string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > AppAdminRules.MaxReportPageSize)
            throw new CollectorStoreException("INVALID_FILTER", $"limit 必须在 1–{AppAdminRules.MaxReportPageSize} 之间。", 400);
        var before = DecodeCursor(cursor);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AppChannelSchema.RequireAsync(connection, null, cancellationToken);
        var rows = await QueryRowsAsync(connection, null, $"""
            SELECT BIN_TO_UUID(id), DATE_FORMAT(report_date, '%Y-%m-%d'), {Iso("generated_at")}, has_alerts, delivery_status, {Iso("sent_at")}, send_error
            FROM collector_app_daily_reports
            {(before is null ? "" : "WHERE report_date < @before")}
            ORDER BY report_date DESC
            LIMIT {limit + 1};
            """, cancellationToken, ("@before", before is { } date ? Day(date) : null));
        var items = rows.Take(limit).Select(row => new AppDailyReportItem(Guid.Parse(row[0]), ParseDay(row[1]), ParseIso(row[2]), Flag(row[3]),
            Delivery(row[4]), ParseIsoOrNull(row[5]), Text(row[6]))).ToArray();
        var next = rows.Count > limit ? Convert.ToBase64String(Encoding.UTF8.GetBytes(Day(items[^1].ReportDate))) : null;
        return new CursorPage<AppDailyReportItem>(items, next);
    }

    /// <inheritdoc />
    public async Task<AppDailyReportView?> GetAsync(DateOnly reportDate, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AppChannelSchema.RequireAsync(connection, null, cancellationToken);
        return await ReadAsync(connection, reportDate, cancellationToken);
    }

    private static async Task<AppDailyReportView?> ReadAsync(MySqlConnection connection, DateOnly reportDate, CancellationToken ct)
    {
        var row = await QuerySingleAsync(connection, null, $"""
            SELECT BIN_TO_UUID(id), DATE_FORMAT(report_date, '%Y-%m-%d'), {Iso("generated_at")}, has_alerts, delivery_status, {Iso("sent_at")},
                   send_error, CAST(summary AS CHAR), body
            FROM collector_app_daily_reports WHERE report_date = @date;
            """, ct, ("@date", Day(reportDate)));
        if (row is null) return null;
        var view = JsonSerializer.Deserialize<AppAlertsView>(row[7], Json)
            ?? throw new InvalidOperationException("每日汇报的内容无法读取。");
        return new AppDailyReportView(Guid.Parse(row[0]), ParseDay(row[1]), ParseIso(row[2]), Flag(row[3]), Delivery(row[4]),
            ParseIsoOrNull(row[5]), Text(row[6]), view, row[8]);
    }

    private static AppDailyReportDeliveryStatus Delivery(string value)
        => AppContractNames.TryParse<AppDailyReportDeliveryStatus>(value, out var status) ? status : AppDailyReportDeliveryStatus.NotConfigured;

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDay(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            if (DateOnly.TryParseExact(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                return date;
        }
        catch (FormatException)
        {
        }
        throw new CollectorStoreException("INVALID_CURSOR", "游标无效。", 400);
    }
}

/// <summary>每日汇报的占位发送渠道（KD-19）：什么都不发，不发出任何网络请求，结果记为 not_configured。</summary>
public sealed class NotConfiguredDailyReportSender : IDailyReportSender
{
    /// <inheritdoc />
    public Task<AppDailyReportDelivery> SendAsync(AppDailyReportView report, CancellationToken cancellationToken = default)
        => Task.FromResult(new AppDailyReportDelivery(AppDailyReportDeliveryStatus.NotConfigured));
}
