using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>文章编辑器（迁移 041）：自动保存草稿。只改标题与正文，不改标签、SEO 与语言。</summary>
public sealed partial class ContentStore : IArticleEditorStore
{
    /// <summary>
    /// 当前版本「已被发布」：有发布链接（含已停用）显式引用该版本，或有未指定版本的链接登记于该版本成为当前版本之后。
    /// 版本只在创建时成为当前版本，因此后者表示登记链接时发布的就是这个版本。被发布的版本不再原地覆盖。
    /// </summary>
    private const string CurrentVersionPublishedSql =
        "EXISTS(SELECT 1 FROM content_publication_links p WHERE p.tenant_id=a.tenant_id AND p.article_id=a.id"
        + " AND (p.article_version_id=a.current_version_id OR (p.article_version_id IS NULL AND p.created_at>=v.created_at)))";

    /// <summary>草稿请求的长度与安全校验（与 PATCH 共用编辑侧安全规则）。</summary>
    /// <param name="request">草稿请求。</param>
    /// <param name="baseBody">当前版本正文（正文未变时跳过正文检查，已有图片地址可保留）；未知时为空。</param>
    internal static void ValidateDraft(ArticleDraftRequest request, string? baseBody = null)
    {
        ValidateDraftShape(request);
        ArticleEditSafety.Validate(request.Title, request.BodyMarkdown, baseBody);
    }

    private static void ValidateDraftShape(ArticleDraftRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > ArticleEditorLimits.TitleMaxLength)
            throw new CollectorStoreException("INVALID_ARTICLE", "文章标题不能为空且不能超过 200 字。", 400);
        if (string.IsNullOrWhiteSpace(request.BodyMarkdown) || request.BodyMarkdown.Length > ArticleEditorLimits.BodyMaxLength)
            throw new CollectorStoreException("INVALID_ARTICLE", "文章正文为空或超过 100000 字符。", 400);
        if (request.ExpectedRevision < 0 || request.BaseVersionId == Guid.Empty)
            throw new CollectorStoreException("INVALID_ARTICLE", "基础版本或修订号无效。", 400);
    }

    /// <inheritdoc />
    public async Task<ArticleDraftResult> SaveDraftAsync(Guid tenantId, Guid siteId, Guid articleId, Guid userId,
        ArticleDraftRequest request, CancellationToken cancellationToken = default)
    {
        ValidateDraftShape(request);
        var title = request.Title.Trim();
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await ArticleEditorSchema.RequireAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Lock order: article (and its current version row) only; generation, refine and PATCH take the article lock
        // after any credit/task locks, so this path cannot form a cycle with them.
        var row = await RowAsync(connection, transaction, $"""
            SELECT a.status,BIN_TO_UUID(a.current_version_id),a.language,COALESCE(v.version_kind,''),COALESCE(v.edit_revision,0),
                   {CurrentVersionPublishedSql},v.body_markdown
            FROM content_articles a
            LEFT JOIN content_article_versions v ON v.tenant_id=a.tenant_id AND v.id=a.current_version_id
            WHERE a.tenant_id=UUID_TO_BIN(@tenant) AND a.site_id=UUID_TO_BIN(@site) AND a.id=UUID_TO_BIN(@article)
            FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        if (row is null) throw NotFound();
        if (row[0].Equals("archived", StringComparison.OrdinalIgnoreCase))
            throw new CollectorStoreException("ARTICLE_ARCHIVED", "文章已归档。", 409);
        var currentVersionId = ParseGuid(row[1]);
        var currentRevision = int.Parse(row[4], CultureInfo.InvariantCulture);
        if (currentVersionId != request.BaseVersionId || (currentVersionId is not null && currentRevision != request.ExpectedRevision))
            throw new CollectorStoreException(ArticleEditorErrorCodes.VersionConflict, "文章已被其他操作更新，请刷新后再编辑。", 409);
        // Safety is judged against the version being edited: unchanged legacy bodies and their existing images stay saveable.
        ArticleEditSafety.Validate(title, request.BodyMarkdown, currentVersionId is null ? null : row[6]);

        var overwrite = currentVersionId is not null && row[3] == "manual_edit" && !ParseBool(row[5]);
        Guid versionId;
        bool created;
        if (overwrite)
        {
            versionId = currentVersionId!.Value;
            created = false;
            var updated = await ExecAsync(connection, transaction, """
                UPDATE content_article_versions SET title=@title,body_markdown=@body,content_length=CHAR_LENGTH(@body),edit_revision=edit_revision+1
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND article_id=UUID_TO_BIN(@article) AND id=UUID_TO_BIN(@version) AND edit_revision=@revision;
                """, cancellationToken, ("@title", title), ("@body", request.BodyMarkdown), ("@tenant", tenantId.ToString()),
                ("@article", articleId.ToString()), ("@version", versionId.ToString()), ("@revision", request.ExpectedRevision));
            if (updated != 1)
                throw new CollectorStoreException(ArticleEditorErrorCodes.VersionConflict, "文章已被其他操作更新，请刷新后再编辑。", 409);
        }
        else
        {
            versionId = Guid.NewGuid();
            created = true;
            var next = int.Parse((await RowAsync(connection, transaction,
                "SELECT COALESCE(MAX(version_no),0)+1 FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND article_id=UUID_TO_BIN(@article);",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@article", articleId.ToString())))![0], CultureInfo.InvariantCulture);
            if (currentVersionId is null)
                await ExecAsync(connection, transaction, """
                    INSERT INTO content_article_versions (id,tenant_id,site_id,article_id,version_no,version_kind,edit_revision,title,body_markdown,content_length,language,created_by,created_at)
                    VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),@number,'manual_edit',0,@title,@body,CHAR_LENGTH(@body),@language,UUID_TO_BIN(@user),UTC_TIMESTAMP(6));
                    """, cancellationToken, ("@id", versionId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                    ("@article", articleId.ToString()), ("@number", next), ("@title", title), ("@body", request.BodyMarkdown),
                    ("@language", row[2]), ("@user", userId.ToString()));
            else
                // Summary, SEO fields, keywords and language carry over from the version being edited.
                await ExecAsync(connection, transaction, """
                    INSERT INTO content_article_versions (id,tenant_id,site_id,article_id,version_no,version_kind,edit_revision,title,summary,body_markdown,seo_title,seo_description,keywords_json,content_length,language,created_by,created_at)
                    SELECT UUID_TO_BIN(@id),tenant_id,site_id,article_id,@number,'manual_edit',0,@title,summary,@body,seo_title,seo_description,keywords_json,CHAR_LENGTH(@body),language,UUID_TO_BIN(@user),UTC_TIMESTAMP(6)
                    FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND article_id=UUID_TO_BIN(@article) AND id=UUID_TO_BIN(@base);
                    """, cancellationToken, ("@id", versionId.ToString()), ("@tenant", tenantId.ToString()), ("@article", articleId.ToString()),
                    ("@number", next), ("@title", title), ("@body", request.BodyMarkdown), ("@user", userId.ToString()),
                    ("@base", currentVersionId.Value.ToString()));
        }
        await ExecAsync(connection, transaction, """
            UPDATE content_articles SET current_version_id=UUID_TO_BIN(@version),title=@title,updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@article);
            """, cancellationToken, ("@version", versionId.ToString()), ("@title", title), ("@tenant", tenantId.ToString()),
            ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        var saved = (await RowAsync(connection, transaction, """
            SELECT version_no,edit_revision,title,content_length,UTC_TIMESTAMP(6) FROM content_article_versions
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@version);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@version", versionId.ToString())))!;
        await transaction.CommitAsync(cancellationToken);
        return new ArticleDraftResult(articleId, versionId, int.Parse(saved[0], CultureInfo.InvariantCulture), ArticleVersionKind.ManualEdit,
            int.Parse(saved[1], CultureInfo.InvariantCulture), saved[2], int.Parse(saved[3], CultureInfo.InvariantCulture), created,
            ParseDate(saved[4]));
    }
}
