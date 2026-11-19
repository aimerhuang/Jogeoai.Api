using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

public sealed class ManagementSqlTests
{
    [Stage1MySqlFact]
    public async Task ArchivedSubjectsKeepTermsAndReserveOwnershipWithoutEnteringNewRevisions()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE1_TEST_CONNECTION")!;
        Assert.Equal("jogeoai_stage1_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
        var tenantId = Guid.NewGuid();
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var insert = new MySqlCommand("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'archived-terms-test', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, connection);
            insert.Parameters.AddWithValue("@tenant", tenantId.ToString());
            await insert.ExecuteNonQueryAsync();
        }

        var management = new ManagementStore(new MySqlDatabase(connectionString));
        var suffix = Guid.NewGuid().ToString("N");
        var brandDomain = $"{suffix}.example.test";
        var extraDomain = $"extra-{suffix}.example.test";
        var brand = await management.CreateBrandAsync(tenantId, new BrandWrite("Dormant Brand", brandDomain,
            Aliases: ["Dormant Brand Alias"], AdditionalDomains: [extraDomain], Status: "archived", Description: "测试品牌提供设备与服务。"));
        Assert.Equal("archived", brand.Status);
        Assert.Equal(["Dormant Brand Alias"], brand.Aliases);
        Assert.Equal([extraDomain], brand.AdditionalDomains);
        using (var initial = JsonDocument.Parse(await RevisionAsync(connectionString, brand.SiteId, brand.SubjectConfigVersion)))
        {
            Assert.Equal("archived", initial.RootElement.GetProperty("brandStatus").GetString());
            Assert.Empty(initial.RootElement.GetProperty("terms").EnumerateArray());
        }

        brand = await management.UpdateBrandAsync(tenantId, brand.SiteId,
            new BrandPatch(Status: "active", ExpectedVersion: brand.SubjectConfigVersion));
        Assert.Equal(["Dormant Brand Alias"], brand.Aliases);
        Assert.Equal([extraDomain], brand.AdditionalDomains);
        using (var active = JsonDocument.Parse(await RevisionAsync(connectionString, brand.SiteId, brand.SubjectConfigVersion)))
            Assert.Contains(active.RootElement.GetProperty("terms").EnumerateArray(),
                term => term.GetProperty("key").GetString() == "dormant brand alias");

        var rivalDomain = $"rival-{suffix}.example.test";
        var competitor = await management.CreateCompetitorAsync(tenantId, brand.SiteId,
            new CompetitorWrite("Rival Brand", Aliases: ["Reserved Rival Alias"], Domains: [rivalDomain]));
        Assert.Equal(["Reserved Rival Alias"], competitor.Aliases);
        brand = (await management.GetBrandAsync(tenantId, brand.SiteId))!;
        var activeVersion = brand.SubjectConfigVersion;
        var activeSnapshot = await RevisionAsync(connectionString, brand.SiteId, activeVersion);

        competitor = await management.UpdateCompetitorAsync(tenantId, brand.SiteId, competitor.CompetitorId,
            new CompetitorPatch(Status: "archived", ExpectedVersion: activeVersion));
        Assert.Equal("archived", competitor.Status);
        Assert.Equal(["Reserved Rival Alias"], competitor.Aliases);
        Assert.Equal([rivalDomain], competitor.Domains);
        Assert.Equal(activeSnapshot, await RevisionAsync(connectionString, brand.SiteId, activeVersion));
        brand = (await management.GetBrandAsync(tenantId, brand.SiteId))!;
        using (var archivedCompetitorRevision = JsonDocument.Parse(await RevisionAsync(connectionString, brand.SiteId, brand.SubjectConfigVersion)))
        {
            var terms = archivedCompetitorRevision.RootElement.GetProperty("terms").EnumerateArray().ToArray();
            Assert.DoesNotContain(terms, term => term.GetProperty("subjectId").GetString() == competitor.CompetitorId.ToString());
            Assert.Contains(terms, term => term.GetProperty("key").GetString() == "dormant brand alias");
        }
        var aliasConflict = await Assert.ThrowsAsync<CollectorStoreException>(() => management.CreateCompetitorAsync(
            tenantId, brand.SiteId, new CompetitorWrite("Other", Aliases: ["Reserved Rival Alias"])));
        Assert.Equal("SUBJECT_TERM_CONFLICT", aliasConflict.Code);
        var domainConflict = await Assert.ThrowsAsync<CollectorStoreException>(() => management.CreateCompetitorAsync(
            tenantId, brand.SiteId, new CompetitorWrite("Other", Domains: [$"sub.{rivalDomain}"])));
        Assert.Equal("SUBJECT_TERM_CONFLICT", domainConflict.Code);

        competitor = await management.UpdateCompetitorAsync(tenantId, brand.SiteId, competitor.CompetitorId,
            new CompetitorPatch(Status: "active", ExpectedVersion: brand.SubjectConfigVersion));
        Assert.Equal(["Reserved Rival Alias"], competitor.Aliases);
        brand = (await management.GetBrandAsync(tenantId, brand.SiteId))!;
        using (var restored = JsonDocument.Parse(await RevisionAsync(connectionString, brand.SiteId, brand.SubjectConfigVersion)))
            Assert.Contains(restored.RootElement.GetProperty("terms").EnumerateArray(),
                term => term.GetProperty("subjectId").GetString() == competitor.CompetitorId.ToString());

        var bornArchived = await management.CreateCompetitorAsync(tenantId, brand.SiteId,
            new CompetitorWrite("Born Archived", Aliases: ["Born Archived Alias"],
                Domains: [$"born-{suffix}.example.test"], Status: "archived"));
        Assert.Equal(["Born Archived Alias"], bornArchived.Aliases);
        Assert.Single(bornArchived.Domains);
        brand = (await management.GetBrandAsync(tenantId, brand.SiteId))!;
        using (var newRevision = JsonDocument.Parse(await RevisionAsync(connectionString, brand.SiteId, brand.SubjectConfigVersion)))
            Assert.DoesNotContain(newRevision.RootElement.GetProperty("terms").EnumerateArray(),
                term => term.GetProperty("subjectId").GetString() == bornArchived.CompetitorId.ToString());

        brand = await management.UpdateBrandAsync(tenantId, brand.SiteId,
            new BrandPatch(Status: "archived", ExpectedVersion: brand.SubjectConfigVersion));
        Assert.Equal(["Dormant Brand Alias"], brand.Aliases);
        Assert.Equal([extraDomain], brand.AdditionalDomains);
        Assert.Contains((await management.ListCompetitorsAsync(tenantId, brand.SiteId, null, 50)).Items,
            item => item.CompetitorId == competitor.CompetitorId && item.Aliases.SequenceEqual(["Reserved Rival Alias"]));
        using (var inactiveSite = JsonDocument.Parse(await RevisionAsync(connectionString, brand.SiteId, brand.SubjectConfigVersion)))
        {
            Assert.Equal("archived", inactiveSite.RootElement.GetProperty("brandStatus").GetString());
            Assert.Empty(inactiveSite.RootElement.GetProperty("terms").EnumerateArray());
        }
        brand = await management.UpdateBrandAsync(tenantId, brand.SiteId,
            new BrandPatch(Status: "active", ExpectedVersion: brand.SubjectConfigVersion));
        Assert.Equal(["Dormant Brand Alias"], brand.Aliases);
        var brandAliasConflict = await Assert.ThrowsAsync<CollectorStoreException>(() => management.CreateCompetitorAsync(
            tenantId, brand.SiteId, new CompetitorWrite("Other", Aliases: ["Dormant Brand Alias"])));
        Assert.Equal("SUBJECT_TERM_CONFLICT", brandAliasConflict.Code);
    }

    /// <summary>B3：备注 / 描述读写回；只改展示字段不递增 subject_config_version，全字段补丁递增。需要隔离库已执行 026。</summary>
    [Stage1MySqlFact]
    public async Task RemarkAndDescriptionRoundTripAndDisplayOnlyPatchKeepsTheSubjectConfigVersion()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE1_TEST_CONNECTION")!;
        Assert.Equal("jogeoai_stage1_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
        var tenantId = Guid.NewGuid();
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var insert = new MySqlCommand("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'remark-test', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, connection);
            insert.Parameters.AddWithValue("@tenant", tenantId.ToString());
            await insert.ExecuteNonQueryAsync();
        }

        var management = new ManagementStore(new MySqlDatabase(connectionString));
        var brand = await management.CreateBrandAsync(tenantId, new BrandWrite("Remark brand", $"{Guid.NewGuid():N}.test",
            Remark: "  内部备注  ", Description: "品牌描述"));
        Assert.Equal("内部备注", brand.Remark);
        Assert.Equal("品牌描述", brand.Description);
        var version = brand.SubjectConfigVersion;

        // 只改备注：版本不变，描述保留。
        var patched = await management.UpdateBrandAsync(tenantId, brand.SiteId, new BrandPatch(Remark: "新备注", ExpectedVersion: version));
        Assert.Equal(version, patched.SubjectConfigVersion);
        Assert.Equal("新备注", patched.Remark);
        Assert.Equal("品牌描述", patched.Description);
        // 空串清空描述，仍不递增版本。
        patched = await management.UpdateBrandAsync(tenantId, brand.SiteId, new BrandPatch(Description: "", ExpectedVersion: version));
        Assert.Equal(version, patched.SubjectConfigVersion);
        Assert.Null(patched.Description);
        Assert.Equal("新备注", patched.Remark);
        // 展示字段补丁同样校验 expectedVersion。
        var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => management.UpdateBrandAsync(tenantId, brand.SiteId,
            new BrandPatch(Remark: "过期", ExpectedVersion: version + 1)));
        Assert.Equal("CONFIG_VERSION_CONFLICT", conflict.Code);
        var tooLong = await Assert.ThrowsAsync<CollectorStoreException>(() => management.UpdateBrandAsync(tenantId, brand.SiteId,
            new BrandPatch(Remark: new string('长', 201), ExpectedVersion: version)));
        Assert.Equal(("VALIDATION_FAILED", 400), (tooLong.Code, tooLong.StatusCode));

        // 全字段补丁：版本递增，备注同时更新，快照不含备注。
        var full = await management.UpdateBrandAsync(tenantId, brand.SiteId,
            new BrandPatch(Name: "Renamed brand", Remark: "同时改", ExpectedVersion: version));
        Assert.Equal(version + 1, full.SubjectConfigVersion);
        Assert.Equal(("Renamed brand", "同时改"), (full.Name, full.Remark));
        using (var revision = JsonDocument.Parse(await RevisionAsync(connectionString, brand.SiteId, full.SubjectConfigVersion)))
        {
            Assert.False(revision.RootElement.TryGetProperty("remark", out _));
            Assert.False(revision.RootElement.TryGetProperty("description", out _));
        }
        var listed = (await management.ListBrandsAsync(tenantId, null, 10)).Items.Single(item => item.SiteId == brand.SiteId);
        Assert.Equal("同时改", listed.Remark);
    }

    /// <summary>026 迁移只加两列、幂等过程、无 DROP 表或列。</summary>
    [Fact]
    public void Migration026AddsRemarkAndDescriptionIdempotently()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations/026_site_remark_description.sql"));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        var sql = File.ReadAllText(path);
        Assert.Contains("CREATE PROCEDURE jogeoai_026_site_remark_description()", sql, StringComparison.Ordinal);
        Assert.Contains("CALL jogeoai_026_site_remark_description();", sql, StringComparison.Ordinal);
        Assert.Contains("DROP PROCEDURE jogeoai_026_site_remark_description;", sql, StringComparison.Ordinal);
        Assert.Contains("column_name = 'remark'", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE sites ADD COLUMN remark VARCHAR(200) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("column_name = 'description'", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE sites ADD COLUMN description VARCHAR(2000) NULL", sql, StringComparison.Ordinal);
        Assert.Equal(2, sql.Split("IF NOT EXISTS (SELECT 1 FROM information_schema.columns").Length - 1);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE site_brand_settings", sql, StringComparison.Ordinal);
        Assert.Equal(2, sql.Split("ALTER TABLE sites ADD COLUMN").Length - 1);
    }

    private static async Task<string> RevisionAsync(string connectionString, Guid siteId, uint version)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var query = new MySqlCommand("""
            SELECT snapshot FROM subject_config_revisions
            WHERE site_id = UUID_TO_BIN(@site) AND version = @version;
            """, connection);
        query.Parameters.AddWithValue("@site", siteId.ToString());
        query.Parameters.AddWithValue("@version", version);
        return (string)(await query.ExecuteScalarAsync())!;
    }

    [Stage1MySqlFact]
    public async Task TagRenameKeepsPromptVersionSnapshotAndArchivedSiteRejectsTagWrites()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE1_TEST_CONNECTION")!;
        var builder = new MySqlConnectionStringBuilder(connectionString);
        Assert.Equal("jogeoai_stage1_20260926", builder.Database);
        var tenantId = Guid.NewGuid();
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var insert = new MySqlCommand("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'management-test', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, connection);
            insert.Parameters.AddWithValue("@tenant", tenantId.ToString());
            await insert.ExecuteNonQueryAsync();
        }

        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var brand = await management.CreateBrandAsync(tenantId,
            new BrandWrite("Snapshot brand", $"{Guid.NewGuid():N}.test", Description: "测试品牌提供设备与服务。"));
        var tag = await management.CreateTagAsync(tenantId, brand.SiteId, new TagWrite("Original"));
        var prompt = await management.CreatePromptAsync(tenantId, brand.SiteId,
            new PromptWrite("Which product is best?", TagIds: [tag.TagId]));
        await management.UpdateTagAsync(tenantId, brand.SiteId, tag.TagId,
            new TagPatch(Name: "Renamed", ExpectedVersion: tag.Version));

        var runs = new CloroVisibilityStore(database);
        var run = await runs.CreateRunAsync(new CreateCloroVisibilityRunCommand(tenantId, brand.SiteId,
            [prompt.PromptId], [new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US")],
            $"test-{Guid.NewGuid():N}", "management-test"));
        var check = Assert.Single((await runs.GetChecksAsync(tenantId, run.RunId, null, 50, null, null)).Items);
        Assert.Equal(["Original"], check.PromptTagsSnapshot);

        await management.UpdateBrandAsync(tenantId, brand.SiteId,
            new BrandPatch(Status: "archived", ExpectedVersion: brand.SubjectConfigVersion));
        var createError = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            management.CreateTagAsync(tenantId, brand.SiteId, new TagWrite("Later")));
        Assert.Equal(404, createError.StatusCode);
        var updateError = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            management.UpdateTagAsync(tenantId, brand.SiteId, tag.TagId,
                new TagPatch(Name: "Later", ExpectedVersion: tag.Version + 1)));
        Assert.Equal(404, updateError.StatusCode);
    }

    [Stage1MySqlFact]
    public async Task PromptGroupsPersistAssignmentAndKeepUnassignedAndFilteredCountsStable()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE1_TEST_CONNECTION")!;
        Assert.Equal("jogeoai_stage1_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
        var tenantId = Guid.NewGuid();
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var insert = new MySqlCommand("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'prompt-groups-test', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, connection);
            insert.Parameters.AddWithValue("@tenant", tenantId.ToString());
            await insert.ExecuteNonQueryAsync();
        }

        var management = new ManagementStore(new MySqlDatabase(connectionString));
        var brand = await management.CreateBrandAsync(tenantId,
            new BrandWrite("Prompt group brand", $"{Guid.NewGuid():N}.test", Description: "测试品牌提供设备与服务。"));
        var otherBrand = await management.CreateBrandAsync(tenantId,
            new BrandWrite("Other prompt group brand", $"{Guid.NewGuid():N}.test", Description: "测试品牌提供设备与服务。"));
        var ungrouped = await management.CreatePromptAsync(tenantId, brand.SiteId,
            new PromptWrite("Ungrouped question", "en-US"));
        var group = await management.CreatePromptGroupAsync(tenantId, brand.SiteId,
            new PromptGroupWrite("  Product questions  "));
        var emptyGroup = await management.CreatePromptGroupAsync(tenantId, brand.SiteId,
            new PromptGroupWrite("Empty questions"));
        var grouped = await management.CreatePromptAsync(tenantId, brand.SiteId,
            new PromptWrite("Product question", GroupId: group.GroupId));

        var page = await management.ListPromptGroupsAsync(tenantId, brand.SiteId, null, null, null, 1);
        Assert.Single(page.Items);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(1, page.Ungrouped.PromptCount);
        var next = await management.ListPromptGroupsAsync(tenantId, brand.SiteId, null, null, page.NextCursor, 10);
        var returned = page.Items.Concat(next.Items).ToArray();
        Assert.Contains(returned, item => item.GroupId == group.GroupId && item.PromptCount == 1 && item.EnabledCount == 1);
        Assert.Contains(returned, item => item.GroupId == emptyGroup.GroupId && item.PromptCount == 0 && item.EnabledCount == 0);
        var filteredGroups = await management.ListPromptGroupsAsync(tenantId, brand.SiteId, "Product", null, null, 10);
        Assert.Equal(2, filteredGroups.Items.Count);
        Assert.Equal(1, filteredGroups.Items.Single(item => item.GroupId == group.GroupId).PromptCount);
        Assert.Equal(0, filteredGroups.Items.Single(item => item.GroupId == emptyGroup.GroupId).PromptCount);
        Assert.Equal(0, filteredGroups.Ungrouped.PromptCount);

        var assigned = await management.UpdatePromptAsync(tenantId, brand.SiteId, ungrouped.PromptId,
            new PromptPatch(ExpectedVersion: checked((int)ungrouped.ConfigVersion), GroupId: group.GroupId));
        Assert.Equal(group.GroupId, assigned.GroupId);
        Assert.Equal(ungrouped.Version, assigned.Version);
        var cleared = await management.UpdatePromptAsync(tenantId, brand.SiteId, ungrouped.PromptId,
            new PromptPatch(ExpectedVersion: checked((int)assigned.ConfigVersion), ClearGroup: true));
        Assert.Null(cleared.GroupId);
        Assert.Equal(assigned.Version, cleared.Version);

        var groupedPage = await management.ListPromptsAsync(tenantId, brand.SiteId, null, null, null, 10,
            groupId: group.GroupId);
        Assert.Single(groupedPage.Items);
        Assert.Equal(grouped.PromptId, groupedPage.Items[0].PromptId);
        var ungroupedPage = await management.ListPromptsAsync(tenantId, brand.SiteId, null, null, null, 10,
            ungrouped: true);
        Assert.Single(ungroupedPage.Items);
        Assert.Equal(ungrouped.PromptId, ungroupedPage.Items[0].PromptId);

        var renamed = await management.UpdatePromptGroupAsync(tenantId, brand.SiteId, group.GroupId,
            new PromptGroupPatch("Renamed questions", group.Version));
        Assert.Equal("Renamed questions", renamed.Name);
        Assert.Equal(1, renamed.PromptCount);
        Assert.Equal(1, renamed.EnabledCount);

        var foreignGroup = await management.CreatePromptGroupAsync(tenantId, otherBrand.SiteId,
            new PromptGroupWrite("Foreign group"));
        var foreignError = await Assert.ThrowsAsync<CollectorStoreException>(() => management.UpdatePromptAsync(
            tenantId, brand.SiteId, grouped.PromptId,
            new PromptPatch(ExpectedVersion: checked((int)grouped.ConfigVersion), GroupId: foreignGroup.GroupId)));
        Assert.Equal("RESOURCE_NOT_FOUND", foreignError.Code);

        var invalid = await Assert.ThrowsAsync<CollectorStoreException>(() => management.UpdatePromptAsync(
            tenantId, brand.SiteId, grouped.PromptId,
            new PromptPatch(ExpectedVersion: checked((int)grouped.ConfigVersion), GroupId: group.GroupId, ClearGroup: true)));
        Assert.Equal("INVALID_GROUP", invalid.Code);
    }
}
