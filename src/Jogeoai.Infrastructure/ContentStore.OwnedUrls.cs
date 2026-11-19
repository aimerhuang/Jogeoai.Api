using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>引用文章榜的「自有」标记：复用发布链接登记，不新增表。</summary>
public sealed partial class ContentStore
{
    /// <summary>标记新建导入文章时的读取状态：未抓取网页，与导入不可读链接的取值相同。</summary>
    internal const string OwnedUrlReadStatus = "unreadable";

    private const int OwnedUrlTitleMaxLength = 500;

    /// <summary>发布链接的规范化 URL 与摘要；与引用文章榜 urlHash（url-v2 写入端摘要）相同。</summary>
    /// <param name="url">原始或已规范化的公开 URL。</param>
    internal static (string Normalized, string Hash) OwnedUrlKey(string url)
    {
        var normalized = ArticleSourceReader.NormalizeAndValidate(url);
        return (normalized, Hash(normalized));
    }

    /// <summary>新建导入文章的标题：去首尾空白、最多五百字符；为空时使用规范化 URL。</summary>
    internal static string OwnedUrlTitle(string? title, string normalized)
    {
        var value = string.IsNullOrWhiteSpace(title) ? normalized : title.Trim();
        return value.Length > OwnedUrlTitleMaxLength ? value[..OwnedUrlTitleMaxLength] : value;
    }

    /// <summary>移除接口的 URL 摘要：六十四位十六进制，统一为小写；否则 400 INVALID_URL_HASH。</summary>
    internal static string NormalizeOwnedUrlHash(string? urlHash)
    {
        var value = urlHash?.Trim().ToLowerInvariant();
        if (value is null || value.Length != 64 || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new CollectorStoreException("INVALID_URL_HASH", "urlHash 必须是六十四位十六进制 URL 摘要。", 400);
        return value;
    }

    /// <inheritdoc />
    public async Task<OwnedUrlMarkResult> MarkOwnedUrlAsync(Guid tenantId, Guid siteId, Guid userId, string url,
        string? title, CancellationToken cancellationToken = default)
    {
        var (normalized, hash) = OwnedUrlKey(url);
        var articleTitle = OwnedUrlTitle(title, normalized);
        // Same server-side official-site ownership as manually registered and imported links; read
        // before the transaction so no row lock is held while loading the brand.
        var ownership = ComputeOwnership(normalized, await management.GetBrandAsync(tenantId, siteId, cancellationToken));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await MarkOwnedUrlOnceAsync(tenantId, siteId, userId, url.Trim(), normalized, hash, articleTitle,
                    ownership, cancellationToken);
            }
            // A concurrent mark of the same new URL loses the unique reference key (or a gap-lock deadlock);
            // the retry then sees the winner's active link and returns unchanged.
            catch (MySqlException exception) when (attempt == 1
                && exception.ErrorCode is MySqlErrorCode.DuplicateKeyEntry or MySqlErrorCode.LockDeadlock)
            {
            }
        }
    }

    private async Task<OwnedUrlMarkResult> MarkOwnedUrlOnceAsync(Guid tenantId, Guid siteId, Guid userId,
        string original, string normalized, string hash, string title, bool? ownership, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await AssertSiteAsync(connection, tenantId, siteId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        (string Name, object? Value)[] scope =
            [("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@hash", hash)];
        // Active first, then the most recently disabled; at most one link per reference per site (uq_content_publication_url).
        var links = await RowsAsync(connection, transaction, """
            SELECT BIN_TO_UUID(p.id),BIN_TO_UUID(p.article_id),p.status
            FROM content_publication_links p
            JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id
            WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site) AND r.url_hash=@hash
            ORDER BY p.status='active' DESC, COALESCE(p.disabled_at,p.created_at) DESC, p.id
            FOR UPDATE;
            """, cancellationToken, scope);
        var active = links.FirstOrDefault(link => link[2].Equals("active", StringComparison.OrdinalIgnoreCase));
        if (active is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(hash, normalized, true, Guid.Parse(active[1]), OwnedUrlMarkOutcome.Unchanged);
        }
        if (links.FirstOrDefault() is { } disabled)
        {
            // The unique (tenant, site, reference) key allows only one link row, so it is reactivated in
            // place like AddPublicationLinkAsync does; an explicit earlier ownership value is kept.
            await ExecAsync(connection, transaction, """
                UPDATE content_articles SET status='active',archived_at=NULL,updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@article) AND status='archived';
                UPDATE content_publication_links SET status='active',disabled_at=NULL,ownership_match=COALESCE(ownership_match,@ownership)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@link);
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@article", disabled[1]), ("@link", disabled[0]), ("@ownership", ownership));
            await transaction.CommitAsync(cancellationToken);
            return new(hash, normalized, true, Guid.Parse(disabled[1]), OwnedUrlMarkOutcome.Reactivated);
        }
        // A publication reference without a link (never written by current code) is reused instead of
        // violating uq_content_references_site_kind_hash.
        var reference = await RowAsync(connection, transaction,
            "SELECT BIN_TO_UUID(id) FROM content_references WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND kind='publication' AND url_hash=@hash FOR UPDATE;",
            cancellationToken, scope);
        var articleId = Guid.NewGuid();
        await InsertImportedArticleAsync(connection, transaction, new ImportedArticleInsert(tenantId, siteId, userId, articleId,
            title, null, original, normalized, hash, OwnedUrlReadStatus, null, ownership,
            reference is null ? null : Guid.Parse(reference[0])), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(hash, normalized, true, articleId, OwnedUrlMarkOutcome.Created);
    }

    /// <inheritdoc />
    public async Task<OwnedUrlMarkResult> UnmarkOwnedUrlAsync(Guid tenantId, Guid siteId, Guid userId, string urlHash,
        CancellationToken cancellationToken = default)
    {
        var hash = NormalizeOwnedUrlHash(urlHash);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await AssertSiteAsync(connection, tenantId, siteId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        (string Name, object? Value)[] scope =
            [("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@hash", hash)];
        var links = await RowsAsync(connection, transaction, """
            SELECT BIN_TO_UUID(p.id),BIN_TO_UUID(p.article_id),r.url_normalized
            FROM content_publication_links p
            JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id
            WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site) AND r.url_hash=@hash AND p.status='active'
            ORDER BY p.created_at, p.id
            FOR UPDATE;
            """, cancellationToken, scope);
        if (links.Count == 0)
        {
            var known = await RowAsync(connection, transaction,
                "SELECT url_normalized FROM content_references WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND kind='publication' AND url_hash=@hash LIMIT 1;",
                cancellationToken, scope);
            await transaction.CommitAsync(cancellationToken);
            return new(hash, known?[0], false, null, OwnedUrlMarkOutcome.Unchanged);
        }
        foreach (var link in links)
            await ExecAsync(connection, transaction,
                "UPDATE content_publication_links SET status='disabled',disabled_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@link) AND status='active';",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@link", link[0]));
        // Imported articles left without any active link are archived with the same columns as the archive
        // endpoint; generated and discovered articles only lose the link.
        foreach (var articleId in links.Select(link => link[1]).Distinct(StringComparer.Ordinal))
            await ExecAsync(connection, transaction, """
                UPDATE content_articles a
                SET a.status='archived',a.archived_at=COALESCE(a.archived_at,UTC_TIMESTAMP(6)),a.updated_at=UTC_TIMESTAMP(6)
                WHERE a.tenant_id=UUID_TO_BIN(@tenant) AND a.site_id=UUID_TO_BIN(@site) AND a.id=UUID_TO_BIN(@article)
                  AND a.status='active' AND a.source_kind='published_import'
                  AND NOT EXISTS (SELECT 1 FROM content_publication_links o
                                  WHERE o.tenant_id=a.tenant_id AND o.site_id=a.site_id AND o.article_id=a.id AND o.status='active');
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId));
        await transaction.CommitAsync(cancellationToken);
        return new(hash, links[0][2], false, Guid.Parse(links[0][1]), OwnedUrlMarkOutcome.Disabled);
    }
}
