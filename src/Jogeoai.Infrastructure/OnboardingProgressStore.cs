using System.Globalization;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>品牌引导进度的 MySQL 实现。</summary>
public sealed partial class OnboardingProgressStore(MySqlDatabase database) : IOnboardingProgressStore
{
    public async Task<OnboardingProgressView> EnsureAsync(Guid tenantId, Guid siteId, string currentStep = "select_prompts",
        CancellationToken cancellationToken = default)
    {
        if (currentStep is not ("create_team" or "create_brand" or "select_prompts" or "select_competitors" or "complete"))
            throw new ArgumentException("currentStep is invalid.");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var site = new MySqlCommand("SELECT status FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@site);", connection))
        {
            site.Parameters.AddWithValue("@tenant", tenantId.ToString());
            site.Parameters.AddWithValue("@site", siteId.ToString());
            var status = await site.ExecuteScalarAsync(cancellationToken);
            if (status is null || Convert.ToString(status, CultureInfo.InvariantCulture) != "active")
                throw new CollectorStoreException("RESOURCE_NOT_FOUND", "品牌不存在。", 404);
        }
        await using var command = new MySqlCommand("""
            INSERT INTO onboarding_progress (tenant_id, site_id, current_step, completed, selected_prompt_limit, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @step, FALSE, 5, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE current_step = IF(completed, current_step, VALUES(current_step)), updated_at = UTC_TIMESTAMP(6);
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@step", currentStep);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await GetAsync(tenantId, siteId, cancellationToken))!;
    }

    public async Task<OnboardingProgressView?> GetAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT p.current_step, p.completed, p.selected_prompt_limit, p.updated_at, d.draft_json
            FROM onboarding_progress p LEFT JOIN onboarding_drafts d ON d.tenant_id = p.tenant_id AND d.site_id = p.site_id
            WHERE p.tenant_id = UUID_TO_BIN(@tenant) AND p.site_id = UUID_TO_BIN(@site);
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var updated = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
        return new OnboardingProgressView(siteId, reader.GetString(0), reader.GetBoolean(1), reader.GetInt32(2), updated,
            reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<OnboardingDraftWrite>(reader.GetString(4)));
    }

    public async Task<OnboardingProgressView> CompleteAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE onboarding_progress SET current_step = 'complete', completed = TRUE, updated_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await GetAsync(tenantId, siteId, cancellationToken))
            ?? throw new CollectorStoreException("RESOURCE_NOT_FOUND", "品牌引导不存在。", 404);
    }
}
