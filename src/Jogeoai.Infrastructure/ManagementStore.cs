using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>管理平面的租户范围 MySQL 实现。</summary>
/// <param name="database">业务数据库。</param>
/// <param name="providerSettings">可选的 API 回答 Provider 设置店；提供时目录中的豆包与 DeepSeek API 可用性由各自设置行决定，否则为不可用。</param>
/// <param name="logger">可选日志；站点目标保存后凌晨监控计划同步失败时记录警告。</param>
/// <param name="appChannel">可选的 App 渠道开放范围（Collector:AppTenantIds）；不提供时 App 渠道不对任何租户开放。</param>
public sealed partial class ManagementStore(MySqlDatabase database, IAnswerProviderSettingsStore? providerSettings = null,
    ILogger<ManagementStore>? logger = null, IOptions<AppChannelOptions>? appChannel = null) : IManagementStore
{
    // design 6.1: closed for everyone unless the tenant is listed in Collector:AppTenantIds.
    private bool AppOpen(Guid tenantId) => appChannel?.Value.IsOpen(tenantId) == true;

    private sealed record ManagementCursor(DateTimeOffset SnapshotAt, DateTimeOffset LastAt, Guid LastId, string Scope);
    private static readonly IReadOnlyList<string> CloroEngines = AnswerProviderDefinition.Cloro.Engines;
    private static readonly IdnMapping Idn = new();

    private static CollectorStoreException Error(string code, string message, int status) => new(code, message, status);
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static DateTimeOffset? NullableUtc(string value) => string.IsNullOrEmpty(value) ? null : Utc(value);
    private static bool Bool(string value) => value is "1" or "True" or "true";
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string NormalizeText(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        normalized = string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (normalized.Length is < 1 or > 253) throw Error("INVALID_TERM", "名称或别名长度必须为 1–253。", 400);
        return normalized;
    }
    private static string NormalizeDomain(string value)
    {
        value = value.Trim().TrimEnd('.');
        if (value.Contains("://", StringComparison.Ordinal) || value.Contains('/') || value.Contains(':'))
            throw Error("INVALID_DOMAIN", "请提供域名，不包含协议、路径或端口。", 400);
        try
        {
            var normalized = Idn.GetAscii(value).ToLowerInvariant();
            if (normalized.Length is < 1 or > 253 || normalized.Split('.').Any(label => label.Length is < 1 or > 63)
                || !normalized.Contains('.') || normalized.Split('.').Any(label => label.StartsWith('-') || label.EndsWith('-')))
                throw new ArgumentException();
            return normalized;
        }
        catch (ArgumentException) { throw Error("INVALID_DOMAIN", "域名无效。", 400); }
    }
    private static string? NormalizeOptionalDomain(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : NormalizeDomain(value);
    private static string ValidateLocale(string locale)
    {
        if (locale.Length is < 2 or > 16) throw Error("INVALID_LOCALE", "locale 无效。", 400);
        try { return CultureInfo.GetCultureInfo(locale).Name; }
        catch (CultureNotFoundException) { throw Error("INVALID_LOCALE", "locale 无效。", 400); }
    }
    private static string ValidateCountry(string country)
    {
        country = country.Trim().ToUpperInvariant();
        try { if (country.Length != 2 || new RegionInfo(country).TwoLetterISORegionName != country) throw new ArgumentException(); }
        catch (ArgumentException) { throw Error("INVALID_COUNTRY", "country 必须为两位 ISO 国家代码。", 400); }
        return country;
    }
    private static string ValidateTimeZone(string zone)
    {
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(zone); }
        catch (TimeZoneNotFoundException) { throw Error("INVALID_TIME_ZONE", "IANA 时区无效。", 400); }
        catch (InvalidTimeZoneException) { throw Error("INVALID_TIME_ZONE", "IANA 时区无效。", 400); }
        return zone;
    }
    private static string ValidateStatus(string status)
    {
        if (status is not ("active" or "archived")) throw Error("INVALID_STATUS", "状态必须为 active 或 archived。", 400);
        return status;
    }
    private static IReadOnlyList<string> NormalizeValues(IReadOnlyList<string>? values, Func<string, string> normalize)
        => (values ?? []).Select(normalize).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static async Task<int> Exec(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
    private static async Task<List<string[]>> Rows(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            result.Add(row);
        }
        return result;
    }
    private static async Task<string[]?> Row(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        => (await Rows(connection, transaction, sql, cancellationToken, parameters)).FirstOrDefault();

    private static async Task<(IReadOnlyList<Guid> Ids, string? NextCursor)> ListIdsAsync(MySqlConnection connection,
        string entity, Guid tenantId, Guid? siteId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw Error("INVALID_LIMIT", "limit 必须在 1–100。", 400);
        var scope = Hash(JsonSerializer.Serialize(new { entity, tenantId, siteId }));
        ManagementCursor? position = null;
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length > 2048) throw new FormatException();
                position = JsonSerializer.Deserialize<ManagementCursor>(Convert.FromBase64String(cursor));
                if (position is null || position.Scope != scope || position.LastId == Guid.Empty
                    || position.LastAt > position.SnapshotAt || position.SnapshotAt > DateTimeOffset.UtcNow.AddMinutes(1))
                    throw new FormatException();
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            { throw Error("INVALID_CURSOR", "游标与当前列表不匹配。", 400); }
        }
        var snapshotAt = position?.SnapshotAt ?? DateTimeOffset.UtcNow;
        var table = entity switch
        {
            "brands" => "sites",
            "competitors" => "competitors",
            "tags" => "tags",
            _ => throw new ArgumentOutOfRangeException(nameof(entity))
        };
        var sql = $"SELECT BIN_TO_UUID(id), created_at FROM {table} WHERE tenant_id = UUID_TO_BIN(@tenant)";
        if (siteId is not null) sql += " AND site_id = UUID_TO_BIN(@site)";
        sql += " AND created_at <= @snapshot";
        if (position is not null) sql += " AND (created_at > @last OR (created_at = @last AND id > UUID_TO_BIN(@lastId)))";
        sql += " ORDER BY created_at, id LIMIT @limit;";
        var parameters = new List<(string, object?)> { ("@tenant", tenantId.ToString()), ("@snapshot", snapshotAt.UtcDateTime), ("@limit", limit + 1) };
        if (siteId is not null) parameters.Add(("@site", siteId.Value.ToString()));
        if (position is not null)
        {
            parameters.Add(("@last", position.LastAt.UtcDateTime));
            parameters.Add(("@lastId", position.LastId.ToString()));
        }
        var rows = await Rows(connection, null, sql, cancellationToken, parameters.ToArray());
        var next = rows.Count > limit
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new ManagementCursor(snapshotAt,
                Utc(rows[limit - 1][1]), Guid.Parse(rows[limit - 1][0]), scope)))
            : null;
        return (rows.Take(limit).Select(row => Guid.Parse(row[0])).ToArray(), next);
    }

    private static async Task<string[]> LockSite(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid siteId,
        CancellationToken cancellationToken)
    {
        var row = await Row(connection, transaction, """
            SELECT s.display_name, s.host, s.status, b.default_country, b.default_locale, b.time_zone,
                   b.subject_config_version, s.created_at, s.updated_at, b.target_config_version,
                   s.report_title, s.include_subdomains, s.remark, s.description
            FROM sites s JOIN site_brand_settings b ON b.site_id = s.id AND b.tenant_id = s.tenant_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.id = UUID_TO_BIN(@site) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        return row ?? throw Error("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
    }

    /// <summary>租户级串行化锁：套餐上限校验与写入在同一事务内，锁顺序 tenants -> tenant_onboarding -> sites -> onboarding_progress。</summary>
    private static Task<string[]?> LockTenant(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        CancellationToken cancellationToken)
        => Row(connection, transaction, "SELECT status FROM tenants WHERE id = UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()));

    private static async Task EnsureSiteExists(MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId, Guid siteId,
        CancellationToken cancellationToken)
    {
        var row = await Row(connection, transaction, "SELECT status FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@site);",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        if (row is null) throw Error("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
    }

    private static async Task BumpRevision(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid siteId,
        CancellationToken cancellationToken)
    {
        await Exec(connection, transaction, "UPDATE site_brand_settings SET subject_config_version = subject_config_version + 1, updated_at = UTC_TIMESTAMP(6) WHERE site_id = UUID_TO_BIN(@site);",
            cancellationToken, ("@site", siteId.ToString()));
        var version = await Row(connection, transaction, "SELECT subject_config_version FROM site_brand_settings WHERE site_id = UUID_TO_BIN(@site);",
            cancellationToken, ("@site", siteId.ToString()));
        var site = await Row(connection, transaction, "SELECT display_name, host, status, report_title, include_subdomains FROM sites WHERE id = UUID_TO_BIN(@site);",
            cancellationToken, ("@site", siteId.ToString()));
        var subjects = await Rows(connection, transaction, """
            SELECT subject_type, BIN_TO_UUID(subject_id), term_type, normalized_key FROM subject_terms
            WHERE site_id = UUID_TO_BIN(@site) ORDER BY subject_type, subject_id, namespace_key, normalized_key;
            """, cancellationToken, ("@site", siteId.ToString()));
        var competitors = await Rows(connection, transaction, "SELECT BIN_TO_UUID(id), name, status FROM competitors WHERE site_id = UUID_TO_BIN(@site) ORDER BY id;",
            cancellationToken, ("@site", siteId.ToString()));
        var activeCompetitors = competitors.Where(r => r[2] == "active").Select(r => r[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = JsonSerializer.Serialize(new { brandId = siteId, name = site![0], primaryDomain = NormalizeOptionalDomain(site[1]),
            reportTitle = site[3], includeSubdomains = Bool(site[4]), brandStatus = site[2],
            competitors = competitors.Select(r => new { id = r[0], name = r[1], status = r[2] }),
            terms = subjects.Where(r => site[2] == "active" && (r[0] == "brand"
                    || r[0] == "competitor" && activeCompetitors.Contains(r[1])))
                .Select(r => new { subjectType = r[0], subjectId = r[1], termType = r[2], key = r[3] }) });
        await Exec(connection, transaction, """
            INSERT INTO subject_config_revisions (site_id, tenant_id, version, snapshot, created_at)
            VALUES (UUID_TO_BIN(@site), UUID_TO_BIN(@tenant), @version, @snapshot, UTC_TIMESTAMP(6));
            """, cancellationToken, ("@site", siteId.ToString()), ("@tenant", tenantId.ToString()),
            ("@version", int.Parse(version![0])), ("@snapshot", snapshot));
    }
}
