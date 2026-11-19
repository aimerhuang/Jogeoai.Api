using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>采集完成事务内写入分析到达水位和幂等工作项。</summary>
public static class AnalysisEnqueue
{
    /// <summary>在已有采集事务中记录 Check；不访问 Provider。</summary>
    public static async Task QueueAsync(MySqlConnection connection, MySqlTransaction transaction, Guid checkId,
        CancellationToken cancellationToken)
    {
        await using var state = new MySqlCommand("""
            SELECT BIN_TO_UUID(c.site_id), BIN_TO_UUID(c.tenant_id), BIN_TO_UUID(s.published_release_id)
            FROM visibility_checks c LEFT JOIN site_analysis_state s ON s.site_id = c.site_id
            WHERE c.id = UUID_TO_BIN(@check) FOR UPDATE;
            """, connection, transaction);
        state.Parameters.AddWithValue("@check", checkId.ToString());
        Guid siteId;
        Guid tenantId;
        bool hasState;
        await using (var reader = await state.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("待分析 Check 不存在。");
            siteId = Guid.Parse(reader.GetString(0));
            tenantId = Guid.Parse(reader.GetString(1));
            hasState = !reader.IsDBNull(2);
        }
        if (!hasState) await EnsureSiteStateAsync(connection, transaction, tenantId, siteId, cancellationToken);
        await using var command = new MySqlCommand("""
            INSERT IGNORE INTO analytics_check_arrivals (check_id, tenant_id, site_id, captured_at, arrived_at)
            SELECT c.id, c.tenant_id, c.site_id, c.captured_at, UTC_TIMESTAMP(6)
            FROM visibility_checks c WHERE c.id = UUID_TO_BIN(@check) AND c.status IN ('succeeded','failed','cancelled');
            INSERT IGNORE INTO analysis_check_work (release_id, check_id, tenant_id, site_id, created_at, updated_at)
            SELECT r.id, c.id, c.tenant_id, c.site_id, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
            FROM visibility_checks c JOIN site_analysis_state s ON s.site_id = c.site_id
            JOIN analysis_releases r ON r.site_id = s.site_id
              AND (r.id = s.published_release_id OR r.status = 'building')
            WHERE c.id = UUID_TO_BIN(@check) AND c.status = 'succeeded';
            """, connection, transaction);
        command.Parameters.AddWithValue("@check", checkId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>为迁移后新建的工作区建立首个已发布空分析版本。</summary>
    public static async Task EnsureSiteStateAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid siteId, CancellationToken cancellationToken)
    {
        await using var bootstrap = new MySqlCommand("""
            INSERT IGNORE INTO analysis_releases (id, tenant_id, site_id, version, subject_config_version,
                subject_snapshot, extractor_version, metric_version, detail_version, url_normalization_version,
                status, created_at, published_at)
            SELECT UUID_TO_BIN(UUID()), r.tenant_id, r.site_id, 1, r.version, r.snapshot,
                   @extractor, 'mentions-v1', 'detail-v2', 'url-v2', 'published', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
            FROM subject_config_revisions r JOIN site_brand_settings b ON b.site_id = r.site_id
            WHERE r.tenant_id = UUID_TO_BIN(@tenant) AND r.site_id = UUID_TO_BIN(@site)
              AND r.version = b.subject_config_version;
            INSERT IGNORE INTO site_analysis_state (site_id, tenant_id, published_release_id, updated_at)
            SELECT site_id, tenant_id, id, UTC_TIMESTAMP(6) FROM analysis_releases
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND version = 1;
            """, connection, transaction);
        bootstrap.Parameters.AddWithValue("@tenant", tenantId.ToString());
        bootstrap.Parameters.AddWithValue("@site", siteId.ToString());
        bootstrap.Parameters.AddWithValue("@extractor", MentionMatcher.Version);
        await bootstrap.ExecuteNonQueryAsync(cancellationToken);
    }
}
