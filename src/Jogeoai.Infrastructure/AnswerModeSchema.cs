using System.Collections.Concurrent;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 047 回答模式与采集频率的结构探测。未迁移时：读取把目标视为默认模式 + daily、检测模式为空（历史）；
/// 只有需要新列的写入（非默认模式、非 daily 频率、带非默认模式目标的 Run）返回 503 <see cref="NotMigratedCode"/>。
/// </summary>
internal static class AnswerModeSchema
{
    internal const string NotMigratedCode = "ANSWER_MODE_SCHEMA_NOT_MIGRATED";

    // Migrations are forward-only while a process runs (047 rollback refuses once mode/frequency data exists and requires the
    // old build), so only a positive probe is cached; an unmigrated database is re-probed until 047 is applied.
    private static readonly ConcurrentDictionary<string, bool> Migrated = new(StringComparer.Ordinal);

    // Do not catch arbitrary SQL errors as missing schema; probe information_schema explicitly.
    internal static async Task<bool> IsMigratedAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        var key = $"{connection.DataSource}/{connection.Database}";
        if (Migrated.ContainsKey(key)) return true;
        await using var command = new MySqlCommand("""
            SELECT (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
                      AND table_name = 'site_targets' AND column_name IN ('answer_mode', 'frequency', 'frequency_anchor'))
                 + (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
                      AND table_name = 'visibility_checks' AND column_name = 'answer_mode');
            """, connection, transaction);
        var migrated = Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 4;
        if (migrated) Migrated.TryAdd(key, true);
        return migrated;
    }

    /// <summary>需要 047 新列的写入前调用；未执行 047 时返回 503，不做任何写入。</summary>
    internal static async Task RequireAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        if (!await IsMigratedAsync(connection, transaction, ct)) throw Missing();
    }

    internal static CollectorStoreException Missing()
        => new(NotMigratedCode, "回答模式与采集频率所需的迁移 047 尚未部署。", 503);

    /// <summary>把检测或目标上存储的模式字符串解析为公开枚举；空值（历史）或无法识别时为空。</summary>
    internal static AnswerMode? ParseStored(string? value) => AnswerModeRules.TryParse(value, out var mode) ? mode : null;

    /// <summary>把检测上存储的模式解析为非空的存储值；空值（历史）或不受支持时取 Provider 默认模式。</summary>
    internal static string ResolveStorage(AnswerProviderDefinition definition, string? value)
        => AnswerModeRules.ToStorage(AnswerModeRules.TryResolve(definition, value, out var mode) ? mode : definition.DefaultAnswerMode);
}
