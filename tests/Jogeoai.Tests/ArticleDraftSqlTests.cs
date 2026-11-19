using System.Globalization;
using System.Reflection;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 草稿自动保存的 SQL 回归（AC-B2）：只用显式隔离、名称含 _test 且已执行 001–041 的测试库（JOGEOAI_CONTENT_TEST_CONNECTION）；
/// 未提供连接时跳过。人工夹具保留在隔离库供审计，清理库需另行确认。
/// </summary>
[Collection("Knowledge isolated SQL")]
public sealed class ArticleDraftSqlTests
{
    [ContentMySqlFact]
    public async Task RepeatedSavesOverwriteOneManualDraftUntilPublishedAndKeepTags()
    {
        var f = await EditorSqlFixture.CreateAsync();
        var (article, generated) = await f.ArticleAsync("# 原文\n\n生成的正文。");
        var tag = await f.TagArticleAsync(article);

        var first = await f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User, new ArticleDraftRequest("改后标题", "第一次编辑", generated, 0));
        Assert.True(first.CreatedNewVersion);
        Assert.Equal((2, 0, ArticleVersionKind.ManualEdit), (first.VersionNo, first.EditRevision, first.VersionKind));

        var second = await f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User, new ArticleDraftRequest("改后标题", "第二次编辑", first.VersionId, 0));
        var third = await f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User, new ArticleDraftRequest("最终标题", "第三次编辑", first.VersionId, 1));
        Assert.Equal((first.VersionId, false, 1), (second.VersionId, second.CreatedNewVersion, second.EditRevision));
        Assert.Equal((first.VersionId, false, 2), (third.VersionId, third.CreatedNewVersion, third.EditRevision));
        Assert.Equal(1, await f.CountAsync("SELECT COUNT(*) FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND version_kind='manual_edit';"));

        var detail = (await f.Store.GetArticleAsync(f.Tenant, f.Site, article))!;
        Assert.Equal((first.VersionId, 2, "第三次编辑", 5), (detail.CurrentVersion!.VersionId, detail.CurrentVersion.EditRevision, detail.CurrentVersion.BodyMarkdown, detail.CurrentVersion.ContentLength));
        Assert.Equal("最终标题", detail.Article.Title);
        Assert.Equal([tag], detail.Article.Tags);
        Assert.Equal(2, detail.Versions.Count);

        // Stale revision or stale base version conflicts; nothing is written.
        foreach (var stale in new[] { new ArticleDraftRequest("x", "y", first.VersionId, 1), new ArticleDraftRequest("x", "y", generated, 0) })
        {
            var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User, stale));
            Assert.Equal((ArticleEditorErrorCodes.VersionConflict, 409), (conflict.Code, conflict.StatusCode));
        }

        // A link that references the draft freezes it: the next save branches into a new manual version.
        await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, article, f.User,
            new ArticlePublicationLinkWrite($"https://published.example/{Guid.NewGuid():N}", ArticleVersionId: first.VersionId, OwnershipMatch: false));
        var branched = await f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User, new ArticleDraftRequest("最终标题", "发布后编辑", first.VersionId, 2));
        Assert.True(branched.CreatedNewVersion);
        Assert.Equal((3, 0), (branched.VersionNo, branched.EditRevision));
        var stillDraft = await f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User, new ArticleDraftRequest("最终标题", "继续编辑", branched.VersionId, 0));
        Assert.Equal((branched.VersionId, 1), (stillDraft.VersionId, stillDraft.EditRevision));
        Assert.Equal("第三次编辑", await f.TextAsync("SELECT body_markdown FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);", ("@id", first.VersionId.ToString())));

        // A link registered without a version after the draft became current also counts as publishing it.
        await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, article, f.User,
            new ArticlePublicationLinkWrite($"https://published.example/{Guid.NewGuid():N}", OwnershipMatch: false));
        var afterUnversioned = await f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User, new ArticleDraftRequest("最终标题", "再次编辑", branched.VersionId, 1));
        Assert.True(afterUnversioned.CreatedNewVersion);
        Assert.Equal(4, afterUnversioned.VersionNo);
        Assert.Equal([tag], (await f.Store.GetArticleAsync(f.Tenant, f.Site, article))!.Article.Tags);
    }

    [ContentMySqlFact]
    public async Task UnsafeContentArchivedArticlesAndPatchSafetyAreEnforced()
    {
        var f = await EditorSqlFixture.CreateAsync();
        var (article, generated) = await f.ArticleAsync("正文");
        var unsafeDraft = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User,
            new ArticleDraftRequest("标题", "<iframe src=\"https://x.example\"></iframe>", generated, 0)));
        Assert.Equal((ArticleEditorErrorCodes.ContentUnsafe, 400), (unsafeDraft.Code, unsafeDraft.StatusCode));
        var unsafePatch = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.EditArticleAsync(f.Tenant, f.Site, article, f.User,
            new ArticleEditRequest("标题", "![x](http://insecure.example/a.png)")));
        Assert.Equal((ArticleEditorErrorCodes.ImageSourceInvalid, 400), (unsafePatch.Code, unsafePatch.StatusCode));
        Assert.Equal(1, await f.CountAsync("SELECT COUNT(*) FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant);"));

        // PATCH keeps creating a new manual version per save, with code fences allowed.
        var patched = await f.Store.EditArticleAsync(f.Tenant, f.Site, article, f.User, new ArticleEditRequest("标题", "正文\n\n```\ncode\n```"));
        Assert.Equal((2, ArticleVersionKind.ManualEdit, 0), (patched.CurrentVersion!.VersionNo, patched.CurrentVersion.VersionKind, patched.CurrentVersion.EditRevision));

        await f.Store.SetArchivedAsync(f.Tenant, f.Site, article, f.User, true);
        var archived = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User,
            new ArticleDraftRequest("标题", "正文", patched.CurrentVersion.VersionId, 0)));
        Assert.Equal(("ARTICLE_ARCHIVED", 409), (archived.Code, archived.StatusCode));
        var missing = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.SaveDraftAsync(f.Tenant, f.Site, Guid.NewGuid(), f.User,
            new ArticleDraftRequest("标题", "正文", null, 0)));
        Assert.Equal(404, missing.StatusCode);
    }

    [ContentMySqlFact]
    public async Task PatchKeepsLegacyBodiesSaveableAndHonoursExpectedRevision()
    {
        var f = await EditorSqlFixture.CreateAsync();
        const string legacy = "导入正文\n\n![旧图](http://legacy.example/a.png)";
        var (article, generated) = await f.ArticleAsync(legacy);
        // Unchanged legacy body with an http image: metadata/title-only PATCH and an edit that keeps the image both save.
        var same = await f.Store.EditArticleAsync(f.Tenant, f.Site, article, f.User, new ArticleEditRequest("新标题", legacy));
        var draft = await f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User,
            new ArticleDraftRequest("新标题", legacy + "\n\n补充一段", same.CurrentVersion!.VersionId, 0));
        Assert.False(draft.CreatedNewVersion);
        var overwritten = await f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User,
            new ArticleDraftRequest("新标题", legacy + "\n\n再补充", draft.VersionId, draft.EditRevision));
        Assert.Equal(2, overwritten.EditRevision);

        // A stale PATCH (old revision, same version number) can no longer overwrite the autosaved draft.
        var stale = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.EditArticleAsync(f.Tenant, f.Site, article, f.User,
            new ArticleEditRequest("旧标题", legacy, ExpectedVersionNo: overwritten.VersionNo, ExpectedRevision: 1)));
        Assert.Equal((ArticleEditorErrorCodes.VersionConflict, 409), (stale.Code, stale.StatusCode));
        var fresh = await f.Store.EditArticleAsync(f.Tenant, f.Site, article, f.User,
            new ArticleEditRequest("新标题", legacy + "\n\n再补充", ExpectedVersionNo: overwritten.VersionNo, ExpectedRevision: 2));
        Assert.Equal(overwritten.VersionNo + 1, fresh.CurrentVersion!.VersionNo);
        Assert.NotEqual(generated, fresh.CurrentVersion.VersionId);
    }
}

/// <summary>编辑器 SQL 测试共用夹具：合成租户、成员、1000 积分账户、品牌与一篇带生成版本的文章。</summary>
internal sealed class EditorSqlFixture
{
    private readonly ManagementStore management;
    internal string ConnectionString { get; }
    internal MySqlDatabase Database { get; }
    internal ContentStore Store { get; }
    internal Guid Tenant { get; } = Guid.NewGuid();
    internal Guid User { get; } = Guid.NewGuid();
    internal Guid Site { get; private set; }

    private EditorSqlFixture(string connection)
    {
        ConnectionString = connection;
        Database = new MySqlDatabase(connection);
        management = new ManagementStore(Database);
        var knowledge = new KnowledgeStore(Database, null!, null!, null!);
        Store = new ContentStore(Database, management, new CreditStore(Database), DispatchProxy.Create<IResearchStore, StudioProxy>(),
            null!, knowledge, knowledge, new ArticleProductResolver(new BrandKnowledgeItemStore(Database), new KnowledgeProductStore(Database, knowledge, management)));
    }

    internal static async Task<EditorSqlFixture> CreateAsync()
    {
        var value = Environment.GetEnvironmentVariable("JOGEOAI_CONTENT_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(value));
        var builder = new MySqlConnectionStringBuilder(value!);
        Assert.Contains("_test", builder.Database, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
        builder.AllowUserVariables = false;
        builder.GuidFormat = MySqlGuidFormat.Default;
        var f = new EditorSqlFixture(builder.ConnectionString);
        Assert.True(await f.CountAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name IN ('article_refine_operations','content_images');") == 2,
            "测试库尚未执行 041_article_editor.sql。");
        ArticleEditorSchema.Reset();
        await f.ExecAsync("""
            INSERT INTO tenants (id,name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@tenant),'article editor artificial fixture','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO auth_users (id,email,password_hash,status,created_at,updated_at) VALUES (UUID_TO_BIN(@user),@email,'not-a-login','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO credit_accounts (tenant_id,available_credits,updated_at) VALUES (UUID_TO_BIN(@tenant),1000,UTC_TIMESTAMP(6));
            """, ("@tenant", f.Tenant.ToString()), ("@user", f.User.ToString()), ("@email", $"editor-{f.User:N}@example.test"));
        f.Site = (await f.management.CreateBrandAsync(f.Tenant, new BrandWrite("合成编辑品牌", $"editor-{f.Tenant:N}.test", Description: "合成品牌介绍"))).SiteId;
        return f;
    }

    /// <summary>插入一篇当前版本为 generated（version 1）的文章。</summary>
    internal async Task<(Guid Article, Guid Version)> ArticleAsync(string body)
    {
        var article = Guid.NewGuid();
        var version = Guid.NewGuid();
        await ExecAsync("""
            INSERT INTO content_articles (id,tenant_id,site_id,created_by,source_kind,status,title,language,monitor_status,created_at,updated_at)
            VALUES (UUID_TO_BIN(@article),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),'ai_generated','active','合成文章','zh-CN','inactive',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO content_article_versions (id,tenant_id,site_id,article_id,version_no,version_kind,title,summary,body_markdown,seo_title,keywords_json,content_length,language,created_by,created_at)
            VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),1,'generated','合成文章','合成摘要',@body,'合成 SEO 标题',JSON_ARRAY('选型'),CHAR_LENGTH(@body),'zh-CN',UUID_TO_BIN(@user),DATE_SUB(UTC_TIMESTAMP(6),INTERVAL 1 SECOND));
            UPDATE content_articles SET current_version_id=UUID_TO_BIN(@version) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@article);
            """, ("@article", article.ToString()), ("@version", version.ToString()), ("@tenant", Tenant.ToString()),
            ("@site", Site.ToString()), ("@user", User.ToString()), ("@body", body));
        return (article, version);
    }

    /// <summary>给文章挂一个标签并返回标签名。</summary>
    internal async Task<string> TagArticleAsync(Guid article)
    {
        var tag = await management.CreateTagAsync(Tenant, Site, new TagWrite($"标签{Guid.NewGuid():N}"[..10]));
        await ExecAsync("INSERT INTO content_article_tags (tenant_id,site_id,article_id,tag_id,created_at) VALUES (UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),UUID_TO_BIN(@tag),UTC_TIMESTAMP(6));",
            ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@article", article.ToString()), ("@tag", tag.TagId.ToString()));
        return tag.Name;
    }

    internal async Task<string[]> AccountAsync()
        => await RowAsync("SELECT CAST(available_credits AS SIGNED),CAST(reserved_credits AS SIGNED),CAST(lifetime_captured AS SIGNED) FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);");

    internal async Task<int> CountAsync(string sql, params (string Name, object? Value)[] parameters)
        => int.Parse((await RowAsync(sql, parameters))[0], CultureInfo.InvariantCulture);

    internal async Task<string> TextAsync(string sql, params (string Name, object? Value)[] parameters) => (await RowAsync(sql, parameters))[0];

    internal async Task<string[]> RowAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        if (sql.Contains("@tenant", StringComparison.Ordinal)) command.Parameters.AddWithValue("@tenant", Tenant.ToString());
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "").ToArray();
    }

    internal async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
