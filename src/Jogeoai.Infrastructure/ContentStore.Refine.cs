using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 智能微调计费（迁移 041）：article_refine_operations 独立预占，账本 reserve / capture / release 通过 refine_operation_id 关联。
/// 锁顺序：微调操作 → 积分账户 → 文章（与生成结算的 任务 → 预占 → 账户 → 文章 同向）。
/// </summary>
public sealed partial class ContentStore
{
    /// <inheritdoc />
    public async Task<ArticleRefineOperationRecord?> FindRefineAsync(Guid tenantId, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await ArticleEditorSchema.RequireAsync(connection, cancellationToken);
        var row = await RowAsync(connection, null, """
            SELECT BIN_TO_UUID(id),BIN_TO_UUID(site_id),BIN_TO_UUID(article_id),request_hash,status,error_code,
                   (status='held' AND created_at<DATE_SUB(UTC_TIMESTAMP(6),INTERVAL @abandon SECOND))
            FROM article_refine_operations WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@key", idempotencyKey),
            ("@abandon", (int)ArticleRefineRules.AbandonAfter.TotalSeconds));
        if (row is null) return null;
        var operationId = Guid.Parse(row[0]);
        if (ParseBool(row[6]))
        {
            await ReleaseRefineAsync(tenantId, operationId, ArticleEditorErrorCodes.RefineAbandoned, cancellationToken);
            return new ArticleRefineOperationRecord(operationId, Guid.Parse(row[1]), Guid.Parse(row[2]), row[3],
                CreditReservationStatus.Released, ArticleEditorErrorCodes.RefineAbandoned);
        }
        return new ArticleRefineOperationRecord(operationId, Guid.Parse(row[1]), Guid.Parse(row[2]), row[3],
            ParseRefineStatus(row[4]), Null(row[5]));
    }

    /// <inheritdoc />
    public async Task<ArticleRefineHold> ReserveRefineAsync(ArticleRefineReservation reservation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        var tenant = reservation.TenantId.ToString();
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await ArticleEditorSchema.RequireAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Holds left behind by a crashed request are released before the balance check (operation locks first).
        // The candidate scan is a plain read: a locking range scan on (tenant_id,status,created_at) would take next-key/gap
        // locks that block a concurrent reservation's INSERT while it waits for this one's account lock (deadlock).
        // Each candidate is then locked by primary key and re-checked inside ReleaseRefineInTransactionAsync.
        var abandoned = await RowsAsync(connection, transaction, """
            SELECT BIN_TO_UUID(id) FROM article_refine_operations
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND status='held' AND created_at<DATE_SUB(UTC_TIMESTAMP(6),INTERVAL @abandon SECOND)
            ORDER BY id LIMIT 20;
            """, cancellationToken, ("@tenant", tenant), ("@abandon", (int)ArticleRefineRules.AbandonAfter.TotalSeconds));
        foreach (var stale in abandoned)
            await ReleaseRefineInTransactionAsync(connection, transaction, reservation.TenantId, Guid.Parse(stale[0]),
                ArticleEditorErrorCodes.RefineAbandoned, cancellationToken);

        await ExecAsync(connection, transaction, "INSERT IGNORE INTO credit_accounts (tenant_id,updated_at) VALUES (UUID_TO_BIN(@tenant),UTC_TIMESTAMP(6));",
            cancellationToken, ("@tenant", tenant));
        var account = await RowAsync(connection, transaction, "SELECT available_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", tenant));
        var existing = await RowAsync(connection, transaction,
            "SELECT 1 FROM article_refine_operations WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key;",
            cancellationToken, ("@tenant", tenant), ("@key", reservation.IdempotencyKey));
        if (existing is not null)
            throw new CollectorStoreException(ArticleEditorErrorCodes.RefineInProgress, "该幂等键的智能微调正在进行，请稍后重试。", 409);

        var article = await RowAsync(connection, transaction, """
            SELECT a.status,BIN_TO_UUID(a.current_version_id),COALESCE(v.edit_revision,0),v.title,v.body_markdown,v.language
            FROM content_articles a
            LEFT JOIN content_article_versions v ON v.tenant_id=a.tenant_id AND v.id=a.current_version_id
            WHERE a.tenant_id=UUID_TO_BIN(@tenant) AND a.site_id=UUID_TO_BIN(@site) AND a.id=UUID_TO_BIN(@article)
            FOR UPDATE;
            """, cancellationToken, ("@tenant", tenant), ("@site", reservation.SiteId.ToString()), ("@article", reservation.ArticleId.ToString()));
        if (article is null) throw NotFound();
        if (article[0].Equals("archived", StringComparison.OrdinalIgnoreCase))
            throw new CollectorStoreException("ARTICLE_ARCHIVED", "文章已归档。", 409);
        if (ParseGuid(article[1]) != reservation.BaseVersionId
            || int.Parse(article[2], CultureInfo.InvariantCulture) != reservation.ExpectedRevision)
            throw new CollectorStoreException(ArticleEditorErrorCodes.VersionConflict, "文章已被其他操作更新，请刷新后再微调。", 409);

        var title = article[3];
        var body = article[4];
        var selectionStart = -1;
        if (reservation.Scope == ArticleRefineScope.Whole)
        {
            if (body.Length > ArticleEditorLimits.WholeRefineMaxLength)
                throw new CollectorStoreException(ArticleEditorErrorCodes.RefineInputTooLong, "整体微调的正文不能超过 20000 字符，请改用框选段落微调。", 422);
        }
        else
        {
            body = ArticleRefineRules.NormalizeNewlines(body);
            selectionStart = ArticleRefineRules.FindUnique(body, reservation.SelectionMarkdown ?? string.Empty);
            if (selectionStart < 0)
                throw new CollectorStoreException(ArticleEditorErrorCodes.SelectionNotFound,
                    selectionStart == -2 ? "选中内容在正文中出现多次，请扩大选区。" : "选中内容在当前正文中不存在，请保存后重新选择。", 422);
        }
        if (account is null || decimal.Parse(account[0], CultureInfo.InvariantCulture) < reservation.Credits)
            throw new CollectorStoreException("CREDIT_INSUFFICIENT", "积分余额不足。", 402);

        var operationId = Guid.NewGuid();
        try
        {
            await ExecAsync(connection, transaction, """
                INSERT INTO article_refine_operations (id,tenant_id,site_id,article_id,base_version_id,base_edit_revision,scope,status,credits,idempotency_key,request_hash,requested_by,created_at)
                VALUES (UUID_TO_BIN(@op),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),UUID_TO_BIN(@base),@revision,@scope,'held',@credits,@key,@hash,UUID_TO_BIN(@user),UTC_TIMESTAMP(6));
                UPDATE credit_accounts SET available_credits=available_credits-@credits,reserved_credits=reserved_credits+@credits,version=version+1,updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant);
                INSERT INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,refine_operation_id,actor_user_id,external_reference,reason,created_at)
                SELECT UUID_TO_BIN(UUID()),tenant_id,'reserve',-@credits,available_credits,UUID_TO_BIN(@op),UUID_TO_BIN(@user),CONCAT('refine-reserve:',@op),'智能微调预占',UTC_TIMESTAMP(6)
                FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
                """, cancellationToken, ("@op", operationId.ToString()), ("@tenant", tenant), ("@site", reservation.SiteId.ToString()),
                ("@article", reservation.ArticleId.ToString()), ("@base", reservation.BaseVersionId.ToString()), ("@revision", reservation.ExpectedRevision),
                ("@scope", reservation.Scope == ArticleRefineScope.Whole ? "whole" : "selection"), ("@credits", reservation.Credits),
                ("@key", reservation.IdempotencyKey), ("@hash", reservation.RequestHash), ("@user", reservation.UserId.ToString()));
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new CollectorStoreException(ArticleEditorErrorCodes.RefineInProgress, "该幂等键的智能微调正在进行，请稍后重试。", 409);
        }
        await transaction.CommitAsync(cancellationToken);
        return new ArticleRefineHold(operationId, title, body, article[5], selectionStart);
    }

    /// <inheritdoc />
    public async Task<ArticleRefineResult> CompleteRefineAsync(Guid tenantId, Guid operationId, string title, string bodyMarkdown,
        CancellationToken cancellationToken = default)
    {
        var tenant = tenantId.ToString();
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var operation = await RowAsync(connection, transaction, """
            SELECT status,credits,BIN_TO_UUID(site_id),BIN_TO_UUID(article_id),BIN_TO_UUID(base_version_id),base_edit_revision,BIN_TO_UUID(requested_by),error_code
            FROM article_refine_operations WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@op) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenant), ("@op", operationId.ToString()))
            ?? throw new CollectorStoreException("RESOURCE_NOT_FOUND", "智能微调记录不存在。", 404);
        if (operation[0] == "captured")
        {
            await transaction.CommitAsync(cancellationToken);
            return (await GetRefineResultAsync(tenantId, operationId, cancellationToken))!;
        }
        if (operation[0] != "held")
        {
            await transaction.CommitAsync(cancellationToken);
            var code = Null(operation[7]) ?? ArticleEditorErrorCodes.RefineAbandoned;
            throw new CollectorStoreException(code, "智能微调已释放，未扣费。", ArticleRefineRules.StatusFor(code));
        }
        var credits = decimal.Parse(operation[1], CultureInfo.InvariantCulture);
        var account = await RowAsync(connection, transaction, "SELECT reserved_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", tenant));
        if (account is null || decimal.Parse(account[0], CultureInfo.InvariantCulture) < credits)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new CollectorStoreException("CREDIT_RECONCILIATION_REQUIRED", "积分账户与预占流水不一致，请稍后重试。", 503);
        }
        var article = await RowAsync(connection, transaction, """
            SELECT a.status,BIN_TO_UUID(a.current_version_id),COALESCE(v.edit_revision,0)
            FROM content_articles a LEFT JOIN content_article_versions v ON v.tenant_id=a.tenant_id AND v.id=a.current_version_id
            WHERE a.tenant_id=UUID_TO_BIN(@tenant) AND a.site_id=UUID_TO_BIN(@site) AND a.id=UUID_TO_BIN(@article) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenant), ("@site", operation[2]), ("@article", operation[3]));
        string? conflict = article is null ? "RESOURCE_NOT_FOUND"
            : article[0].Equals("archived", StringComparison.OrdinalIgnoreCase) ? "ARTICLE_ARCHIVED"
            : !string.Equals(article[1], operation[4], StringComparison.OrdinalIgnoreCase) || article[2] != operation[5]
                ? ArticleEditorErrorCodes.VersionConflict : null;
        if (conflict is not null)
        {
            // The article moved on while the model was running: never overwrite it silently; release instead.
            await ReleaseRefineInTransactionAsync(connection, transaction, tenantId, operationId, conflict, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new CollectorStoreException(conflict, "微调期间文章已被修改，结果未保存，积分已释放。", conflict == "RESOURCE_NOT_FOUND" ? 404 : 409);
        }

        var versionId = Guid.NewGuid();
        var next = int.Parse((await RowAsync(connection, transaction,
            "SELECT COALESCE(MAX(version_no),0)+1 FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND article_id=UUID_TO_BIN(@article);",
            cancellationToken, ("@tenant", tenant), ("@article", operation[3])))![0], CultureInfo.InvariantCulture);
        await ExecAsync(connection, transaction, """
            INSERT INTO content_article_versions (id,tenant_id,site_id,article_id,version_no,version_kind,edit_revision,title,summary,body_markdown,seo_title,seo_description,keywords_json,content_length,language,provider,model,created_by,created_at)
            SELECT UUID_TO_BIN(@version),tenant_id,site_id,article_id,@number,'refined',0,@title,summary,@body,seo_title,seo_description,keywords_json,CHAR_LENGTH(@body),language,@provider,@model,UUID_TO_BIN(@user),UTC_TIMESTAMP(6)
            FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND article_id=UUID_TO_BIN(@article) AND id=UUID_TO_BIN(@base);
            UPDATE content_articles SET current_version_id=UUID_TO_BIN(@version),title=@title,updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@article);
            UPDATE article_refine_operations SET status='captured',result_version_id=UUID_TO_BIN(@version),settled_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@op) AND status='held';
            UPDATE credit_accounts SET reserved_credits=reserved_credits-@credits,lifetime_captured=lifetime_captured+@credits,version=version+1,updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant);
            INSERT IGNORE INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,refine_operation_id,actor_user_id,external_reference,reason,created_at)
            SELECT UUID_TO_BIN(UUID()),tenant_id,'capture',0,available_credits,UUID_TO_BIN(@op),UUID_TO_BIN(@user),CONCAT('refine-capture:',@op),'智能微调结算',UTC_TIMESTAMP(6)
            FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@version", versionId.ToString()), ("@number", next), ("@title", title), ("@body", bodyMarkdown),
            ("@provider", ArticleGenerationProvider.ProviderName), ("@model", ArticleGenerationProvider.ModelName), ("@user", operation[6]),
            ("@tenant", tenant), ("@article", operation[3]), ("@base", operation[4]), ("@op", operationId.ToString()), ("@credits", credits));
        await transaction.CommitAsync(cancellationToken);
        var result = (await GetRefineResultAsync(tenantId, operationId, cancellationToken))!;
        return result with { Replayed = false };
    }

    /// <inheritdoc />
    public async Task ReleaseRefineAsync(Guid tenantId, Guid operationId, string errorCode, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ReleaseRefineInTransactionAsync(connection, transaction, tenantId, operationId, errorCode, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ArticleRefineResult?> GetRefineResultAsync(Guid tenantId, Guid operationId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, null, """
            SELECT BIN_TO_UUID(o.article_id),o.scope,BIN_TO_UUID(v.id),v.version_no,v.edit_revision,v.title,v.body_markdown,v.content_length,o.credits,v.created_at
            FROM article_refine_operations o
            JOIN content_article_versions v ON v.tenant_id=o.tenant_id AND v.article_id=o.article_id AND v.id=o.result_version_id
            WHERE o.tenant_id=UUID_TO_BIN(@tenant) AND o.id=UUID_TO_BIN(@op) AND o.status='captured';
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@op", operationId.ToString()));
        if (row is null) return null;
        return new ArticleRefineResult(operationId, Guid.Parse(row[0]), row[1] == "whole" ? ArticleRefineScope.Whole : ArticleRefineScope.Selection,
            Guid.Parse(row[2]), int.Parse(row[3], CultureInfo.InvariantCulture), ArticleVersionKind.Refined, int.Parse(row[4], CultureInfo.InvariantCulture),
            row[5], row[6], int.Parse(row[7], CultureInfo.InvariantCulture), decimal.Parse(row[8], CultureInfo.InvariantCulture), true, ParseDate(row[9]));
    }

    private static async Task ReleaseRefineInTransactionAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        Guid operationId, string errorCode, CancellationToken cancellationToken)
    {
        var tenant = tenantId.ToString();
        var operation = await RowAsync(connection, transaction,
            "SELECT status,credits FROM article_refine_operations WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@op) FOR UPDATE;",
            cancellationToken, ("@tenant", tenant), ("@op", operationId.ToString()));
        if (operation is null || operation[0] != "held") return;
        var credits = decimal.Parse(operation[1], CultureInfo.InvariantCulture);
        var account = await RowAsync(connection, transaction, "SELECT reserved_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", tenant));
        if (account is null || decimal.Parse(account[0], CultureInfo.InvariantCulture) < credits)
            throw new CollectorStoreException("CREDIT_RECONCILIATION_REQUIRED", "积分账户与预占流水不一致，请稍后重试。", 503);
        var code = string.IsNullOrWhiteSpace(errorCode) ? "REFINE_FAILED" : errorCode.Length > 64 ? errorCode[..64] : errorCode;
        await ExecAsync(connection, transaction, """
            UPDATE article_refine_operations SET status='released',error_code=@code,settled_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@op) AND status='held';
            UPDATE credit_accounts SET available_credits=available_credits+@credits,reserved_credits=reserved_credits-@credits,version=version+1,updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant);
            INSERT IGNORE INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,refine_operation_id,external_reference,reason,created_at)
            SELECT UUID_TO_BIN(UUID()),tenant_id,'release',@credits,available_credits,UUID_TO_BIN(@op),CONCAT('refine-release:',@op),@reason,UTC_TIMESTAMP(6)
            FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@code", code), ("@tenant", tenant), ("@op", operationId.ToString()), ("@credits", credits),
            ("@reason", $"智能微调释放：{code}"));
    }

    private static CreditReservationStatus ParseRefineStatus(string value) => value switch
    {
        "captured" => CreditReservationStatus.Captured,
        "released" => CreditReservationStatus.Released,
        _ => CreditReservationStatus.Held,
    };
}
