using System.Globalization;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 套餐上限守卫。在调用方已持有的事务（通常已 <c>SELECT ... FROM tenants ... FOR UPDATE</c>）内读取生效套餐上限并计数，
/// <c>current + additional &gt; limit</c> 时抛 <see cref="PlanLimitExceededException"/>。上限为 NULL 表示不限；
/// 付费套餐已到期但尚未被 Worker 降级时按免费体验版上限计算；套餐表缺失（1146）抛 503，不静默放行。
/// </summary>
internal static class SubscriptionLimitGuard
{
    internal const string SchemaNotMigratedCode = "SUBSCRIPTION_SCHEMA_NOT_MIGRATED";

    internal static CollectorStoreException SchemaNotMigrated() =>
        new(SchemaNotMigratedCode, "套餐数据库迁移尚未执行，请先部署迁移 024。", 503);

    internal static bool IsMissingSchema(MySqlException exception) => exception.Number == 1146;

    /// <summary>品牌：租户内 <c>sites.status='active'</c> 的数量。</summary>
    internal static Task EnsureBrandAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        int additional, CancellationToken cancellationToken)
        => EnsureAsync(connection, transaction, tenantId, PlanLimitKind.Brands, "max_brands",
            "SELECT COUNT(*) FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant) AND status = 'active';",
            [("@tenant", tenantId.ToString())], additional, cancellationToken);

    /// <summary>成员：租户内 <c>tenant_members.status IN ('active','invited')</c> 的数量（含所有者）。</summary>
    internal static Task EnsureMembersAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        int additional, CancellationToken cancellationToken)
        => EnsureAsync(connection, transaction, tenantId, PlanLimitKind.Members, "max_members",
            "SELECT COUNT(*) FROM tenant_members WHERE tenant_id = UUID_TO_BIN(@tenant) AND status IN ('active','invited');",
            [("@tenant", tenantId.ToString())], additional, cancellationToken);

    /// <summary>监控提问：租户内启用、未归档且所属品牌 active 的 <c>prompts</c> 数量。</summary>
    internal static Task EnsurePromptsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        int additional, CancellationToken cancellationToken)
        => EnsureAsync(connection, transaction, tenantId, PlanLimitKind.Prompts, "max_prompts", PromptCountSql,
            [("@tenant", tenantId.ToString())], additional, cancellationToken);

    /// <summary>只读预估用：返回租户生效套餐的提问上限（NULL 为不限）与当前计数，口径与 <see cref="EnsurePromptsAsync"/> 相同。</summary>
    internal static async Task<(int? Limit, int Current)> ReadPromptUsageAsync(MySqlConnection connection, MySqlTransaction? transaction,
        Guid tenantId, CancellationToken cancellationToken)
    {
        var (_, _, limit) = await ReadLimitAsync(connection, transaction, tenantId, "max_prompts", cancellationToken);
        var row = await ReadAsync(connection, transaction, PromptCountSql, cancellationToken, ("@tenant", tenantId.ToString()));
        return (limit, Convert.ToInt32(row![0], CultureInfo.InvariantCulture));
    }

    private const string PromptCountSql = """
        SELECT COUNT(*) FROM prompts p JOIN sites s ON s.id = p.site_id
        WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.status = 'active' AND p.enabled = TRUE AND p.archived_at IS NULL;
        """;

    /// <summary>竞品：指定品牌下 <c>competitors.status='active'</c> 的数量。</summary>
    internal static Task EnsureCompetitorsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        Guid siteId, int additional, CancellationToken cancellationToken)
        => EnsureAsync(connection, transaction, tenantId, PlanLimitKind.Competitors, "max_competitors_per_brand",
            "SELECT COUNT(*) FROM competitors WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND status = 'active';",
            [("@tenant", tenantId.ToString()), ("@site", siteId.ToString())], additional, cancellationToken);

    /// <summary>读取租户生效套餐的指定上限列；没有订阅行时按免费体验版计算。</summary>
    internal static async Task<(string PlanCode, string PlanName, int? Limit)> ReadLimitAsync(MySqlConnection connection,
        MySqlTransaction? transaction, Guid tenantId, string limitColumn, CancellationToken cancellationToken)
    {
        // limitColumn 只来自本类的固定字符串，不接受外部输入。
        var sql = $"""
            SELECT p.code, p.name, p.{limitColumn}
            FROM tenant_subscriptions s
            JOIN subscription_plans p ON p.code = IF(s.expires_at IS NOT NULL AND s.expires_at <= UTC_TIMESTAMP(6), 'free_trial', s.plan_code)
            WHERE s.tenant_id = UUID_TO_BIN(@tenant);
            """;
        try
        {
            var row = await ReadAsync(connection, transaction, sql, cancellationToken, ("@tenant", tenantId.ToString()));
            row ??= await ReadAsync(connection, transaction,
                $"SELECT code, name, {limitColumn} FROM subscription_plans WHERE code = 'free_trial';", cancellationToken);
            if (row is null) return ("free_trial", "免费体验版", null);
            var limit = row[2] is null or DBNull ? (int?)null : Convert.ToInt32(row[2], CultureInfo.InvariantCulture);
            return ((string)row[0]!, (string)row[1]!, limit);
        }
        catch (MySqlException exception) when (IsMissingSchema(exception))
        {
            throw SchemaNotMigrated();
        }
    }

    private static async Task EnsureAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        PlanLimitKind kind, string limitColumn, string countSql, (string Name, object? Value)[] countParameters, int additional,
        CancellationToken cancellationToken)
    {
        if (additional <= 0) return;
        var (planCode, planName, limit) = await ReadLimitAsync(connection, transaction, tenantId, limitColumn, cancellationToken);
        if (limit is null) return;
        var row = await ReadAsync(connection, transaction, countSql, cancellationToken, countParameters);
        var current = Convert.ToInt32(row![0], CultureInfo.InvariantCulture);
        if (current + additional > limit.Value)
            throw new PlanLimitExceededException(planCode, planName, kind, limit.Value, current);
    }

    private static async Task<object?[]?> ReadAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var row = new object?[reader.FieldCount];
        for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }
}
