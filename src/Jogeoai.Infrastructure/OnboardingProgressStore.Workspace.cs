using System.Text.Json;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class OnboardingProgressStore
{
    /// <inheritdoc />
    public async Task<OnboardingWorkspaceState> GetWorkspaceAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT completed_at, team_configured_at, BIN_TO_UUID(site_id) FROM tenant_onboarding
            WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OnboardingWorkspaceState(reader.IsDBNull(0), !reader.IsDBNull(1), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)))
            : new OnboardingWorkspaceState(false, true, null);
    }

    /// <inheritdoc />
    public async Task ConfigureTeamAsync(Guid tenantId, Guid userId, string name, CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 200)
            throw new CollectorStoreException("INVALID_TEAM_NAME", "团队名称长度必须为 1–200 个字符。", 400);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = new MySqlCommand("""
            UPDATE tenants t JOIN tenant_members m ON m.tenant_id = t.id
            SET t.name = @name, t.updated_at = UTC_TIMESTAMP(6)
            WHERE t.id = UUID_TO_BIN(@tenant) AND t.status = 'active' AND t.deleted_at IS NULL
              AND m.user_id = UUID_TO_BIN(@user) AND m.role = 'owner' AND m.status = 'active';
            """, connection, transaction);
        update.Parameters.AddWithValue("@name", name);
        update.Parameters.AddWithValue("@tenant", tenantId.ToString());
        update.Parameters.AddWithValue("@user", userId.ToString());
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new CollectorStoreException("TEAM_OWNER_REQUIRED", "只有团队所有者可以确认团队名称。", 403);
        await using var state = new MySqlCommand("""
            INSERT INTO tenant_onboarding (tenant_id, team_configured_at) VALUES (UUID_TO_BIN(@tenant), UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE team_configured_at = COALESCE(team_configured_at, UTC_TIMESTAMP(6));
            """, connection, transaction);
        state.Parameters.AddWithValue("@tenant", tenantId.ToString());
        await state.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OnboardingProgressView> SaveDraftAsync(Guid tenantId, Guid siteId, OnboardingDraftWrite request,
        CancellationToken cancellationToken = default)
    {
        if (request.CurrentStep is not ("select_prompts" or "select_competitors")
            || request.Prompts is null || request.Competitors is null
            || request.Prompts.Count > 5 || request.Competitors.Count > 10
            || (request.CurrentStep == "select_competitors" && request.Prompts.Count == 0)
            || request.Prompts.Any(p => string.IsNullOrWhiteSpace(p.Content) || p.Content.Length > 10000)
            || request.Competitors.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 200))
            throw new CollectorStoreException("INVALID_ONBOARDING_DRAFT", "请检查当前步骤和已选内容，选择 1–5 个问题后继续。", 400);
        var json = JsonSerializer.Serialize(request);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 131072)
            throw new CollectorStoreException("INVALID_ONBOARDING_DRAFT", "引导内容过长。", 400);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = new MySqlCommand("""
            UPDATE onboarding_progress p JOIN sites s ON s.id = p.site_id AND s.tenant_id = p.tenant_id
            SET p.current_step = @step, p.updated_at = UTC_TIMESTAMP(6)
            WHERE p.tenant_id = UUID_TO_BIN(@tenant) AND p.site_id = UUID_TO_BIN(@site) AND p.completed = FALSE
                AND s.status = 'active';
            """, connection, transaction);
        update.Parameters.AddWithValue("@step", request.CurrentStep);
        update.Parameters.AddWithValue("@tenant", tenantId.ToString());
        update.Parameters.AddWithValue("@site", siteId.ToString());
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new CollectorStoreException("ONBOARDING_NOT_EDITABLE", "品牌不存在或引导已完成。", 409);
        await using var save = new MySqlCommand("""
            INSERT INTO onboarding_drafts (tenant_id, site_id, draft_json, updated_at)
            VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @draft, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE draft_json = VALUES(draft_json), updated_at = UTC_TIMESTAMP(6);
            """, connection, transaction);
        save.Parameters.AddWithValue("@tenant", tenantId.ToString());
        save.Parameters.AddWithValue("@site", siteId.ToString());
        save.Parameters.AddWithValue("@draft", json);
        await save.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(tenantId, siteId, cancellationToken))!;
    }
}
