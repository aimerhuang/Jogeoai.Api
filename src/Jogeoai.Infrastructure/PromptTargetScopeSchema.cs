using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>042 按提问平台范围的结构探测。未迁移时旧读写照常，只有带平台范围的写入需要 <see cref="RequireAsync"/>。</summary>
internal static class PromptTargetScopeSchema
{
    internal const string NotMigratedCode = "PROMPT_TARGET_SCHEMA_NOT_MIGRATED";

    // Do not catch arbitrary SQL errors as missing schema; probe information_schema explicitly.
    internal static async Task<bool> IsMigratedAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new MySqlCommand("""
            SELECT (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
                      AND table_name = 'prompts' AND column_name IN ('target_scope', 'creation_source'))
                 + (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()
                      AND table_name = 'prompt_target_scopes');
            """, connection, transaction);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 3;
    }

    /// <summary>带平台范围的写入前调用；未执行 042 时返回 503，不做任何写入。</summary>
    internal static async Task RequireAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        if (!await IsMigratedAsync(connection, transaction, ct))
            throw new CollectorStoreException(NotMigratedCode, "按提问保存 AI 平台范围所需的迁移 042 尚未部署。", 503);
    }
}
