using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>App 渠道每日告警汇报（results.md 8.4）的配置，配置节 AppChannel:DailyReport。</summary>
public sealed class AppDailyReportWorkerOptions
{
    /// <summary>配置节名称。</summary>
    public const string Section = "AppChannel:DailyReport";

    /// <summary>是否注册每日汇报任务；false 时不注册（回滚开关）。默认 true。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>每天生成汇报的本地时刻，格式 HH:mm，默认 09:30。无法解析时按默认值。</summary>
    public string LocalTime { get; set; } = "09:30";

    /// <summary>本地时刻所在的时区（IANA 名称），默认 Asia/Shanghai。无法识别时按默认值。</summary>
    public string TimeZone { get; set; } = AppDailyReportRules.DefaultTimeZone;

    /// <summary>检查是否到期的间隔，默认 1 分钟；不大于 0 时按默认值。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// 每天在固定的本地时刻生成并存档一份 App 渠道告警汇报（results.md 8.4）：没有告警的日子也生成一份，写明无告警。
/// 每次检查时，本地时间已过生成时刻就生成“本地今天”的那一份；同一天已经有了就什么都不做（库里的唯一键保证只有一份），
/// 所以重复运行不产生第二份，Worker 在生成时刻之后才启动也会补上当天的那一份。汇报在控制面生成，采集节点整机断电也会被报告。
/// 发送只占位：交给 <see cref="IDailyReportSender"/>，首期的实现什么都不发，状态记为 not_configured。
/// </summary>
public sealed class AppDailyReportWorker(
    ILogger<AppDailyReportWorker> logger,
    IAppDailyReportStore reports,
    IOptions<AppDailyReportWorkerOptions> options) : BackgroundService
{
    private const string NotMigratedCode = "APP_CHANNEL_SCHEMA_NOT_MIGRATED";
    private DateOnly? done;
    private bool notMigratedLogged;

    /// <summary>执行一次检查：到期且当天还没有记录在本进程里时生成当天的汇报。返回本次的生成结果；没有到期或当天已处理时为空。</summary>
    /// <param name="nowUtc">当前时刻。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<AppDailyReportGeneration?> RunPassAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var (zone, localTime) = Schedule(options.Value);
        if (AppDailyReportRules.DueDate(nowUtc, zone, localTime) is not { } date || date == done) return null;
        try
        {
            var result = await reports.GenerateAsync(date, cancellationToken);
            done = date;
            if (result.Created)
                logger.LogInformation("Generated the App daily report for {ReportDate} (alerts: {HasAlerts}, delivery: {DeliveryStatus}).",
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), result.Report.HasAlerts,
                    AppContractNames.ToStorage(result.Report.DeliveryStatus));
            return result;
        }
        // Before migration 049 there is nothing to report on: say so once, then stay quiet until the schema exists.
        catch (CollectorStoreException exception) when (exception.Code == NotMigratedCode)
        {
            if (!notMigratedLogged) logger.LogInformation("App daily report skipped: migration 049 is not applied.");
            notMigratedLogged = true;
            return null;
        }
    }

    /// <summary>解析配置里的时区和生成时刻；无法识别时退回默认值（Asia/Shanghai、09:30）。</summary>
    /// <param name="value">配置。</param>
    public static (TimeZoneInfo Zone, TimeOnly LocalTime) Schedule(AppDailyReportWorkerOptions value)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(value.TimeZone) ? AppDailyReportRules.DefaultTimeZone : value.TimeZone); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(AppDailyReportRules.DefaultTimeZone);
        }
        var localTime = TimeOnly.TryParseExact(value.LocalTime?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : AppDailyReportRules.DefaultLocalTime;
        return (zone, localTime);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.PollInterval > TimeSpan.Zero ? options.Value.PollInterval : TimeSpan.FromMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                // Retried on the next check; the day is not marked done.
                logger.LogWarning("App daily report pass failed with {ExceptionType}.", exception.GetType().Name);
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}
