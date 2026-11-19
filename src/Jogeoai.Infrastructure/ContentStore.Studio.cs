using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>文章创作（迁移 040）：提交、阶段与来源持久化、轮询视图。</summary>
public sealed partial class ContentStore : IArticleStudioStore
{
    private static readonly JsonSerializerOptions StudioJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>批次 request_snapshot 中的文章创作快照包装：原始请求摘要 + 冻结输入。</summary>
    private sealed record StudioBatchSnapshot(string RequestHash, ArticleStudioSnapshot Studio, string? Requirements = null);

    /// <summary>studio 批次的 request_hash：幂等键与原始请求摘要组合，保证不同幂等键的相同请求可以重复生成。</summary>
    internal static string StudioBatchHash(string idempotencyKey, string requestHash)
        => Hash($"article-studio:{idempotencyKey}:{requestHash}");

    /// <inheritdoc />
    public async Task<(ArticleStudioGenerationAccepted Accepted, string RequestHash)?> FindStudioSubmissionAsync(Guid tenantId,
        Guid siteId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ValidateStudioKey(idempotencyKey);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, null,
            "SELECT BIN_TO_UUID(id),BIN_TO_UUID(site_id) FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key LIMIT 1;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@key", idempotencyKey));
        if (row is null) return null;
        if (!Guid.TryParse(row[1], out var batchSite) || batchSite != siteId)
            throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "幂等键已用于其他生成请求。", 409);
        return await ReadStudioAcceptedAsync(connection, null, tenantId, siteId, Guid.Parse(row[0]), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ArticleStudioGenerationAccepted> SubmitStudioGenerationAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleStudioSubmission submission, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ValidateStudioKey(idempotencyKey);
        if (submission.Requirements?.Length > ArticleStudioLimits.RequirementsMaxLength)
            throw new CollectorStoreException("INVALID_GENERATION_REQUEST", "自定义写作要求不能超过 5000 字符。", 400);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await ArticleStudioSchema.RequireAsync(connection, cancellationToken);
        await AssertSiteAsync(connection, tenantId, siteId, cancellationToken);
        var storedHash = StudioBatchHash(idempotencyKey, submission.RequestHash);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SubmitStudioInTransactionAsync(connection, tenantId, siteId, userId, submission,
                    idempotencyKey, storedHash, cancellationToken);
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                if (exception.Message.Contains("uq_article_generation_tasks_studio_active", StringComparison.Ordinal))
                    throw new CollectorStoreException("ARTICLE_GENERATION_IN_PROGRESS", "该文章已有正在进行的生成任务，请等待完成后再重新生成。", 409);
                // A concurrent request with the same key won the batch insert: resolve it as a replay once.
                if (attempt == 0 && exception.Message.Contains("uq_article_generation_batches_idempotency", StringComparison.Ordinal))
                    continue;
                throw;
            }
        }
    }

    private async Task<ArticleStudioGenerationAccepted> SubmitStudioInTransactionAsync(MySqlConnection connection,
        Guid tenantId, Guid siteId, Guid userId, ArticleStudioSubmission submission, string idempotencyKey,
        string storedHash, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await RowAsync(connection, transaction,
            "SELECT BIN_TO_UUID(id),request_hash,BIN_TO_UUID(site_id) FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@key", idempotencyKey));
        if (existing is not null)
        {
            if (!string.Equals(existing[1], storedHash, StringComparison.Ordinal) || existing[2] != siteId.ToString())
                throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "幂等键已用于不同生成请求。", 409);
            var replay = await ReadStudioAcceptedAsync(connection, transaction, tenantId, siteId, Guid.Parse(existing[0]), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return replay.Accepted;
        }

        // Lock order matches completion (account before article) to avoid a deadlock with a finishing task.
        await ExecAsync(connection, transaction, "INSERT IGNORE INTO credit_accounts (tenant_id,updated_at) VALUES (UUID_TO_BIN(@tenant),UTC_TIMESTAMP(6));", cancellationToken, ("@tenant", tenantId.ToString()));
        var account = await RowAsync(connection, transaction, "SELECT available_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;", cancellationToken, ("@tenant", tenantId.ToString()));
        if (account is null || decimal.Parse(account[0], CultureInfo.InvariantCulture) < submission.UnitPrice)
            throw new CollectorStoreException("CREDIT_INSUFFICIENT", "积分余额不足。", 402);

        var articleId = Guid.NewGuid();
        var reuseArticle = false;
        var regeneratedAsNew = false;
        if (submission.ArticleId is { } requested)
        {
            var article = await RowAsync(connection, transaction,
                "SELECT status FROM content_articles WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@article) FOR UPDATE;",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", requested.ToString()));
            if (article is null) throw NotFound();
            if (article[0] == "archived")
                throw new CollectorStoreException("ARTICLE_ARCHIVED", "文章已归档，请先恢复后再重新生成。", 409);
            var running = await RowAsync(connection, transaction,
                "SELECT 1 FROM article_generation_tasks WHERE tenant_id=UUID_TO_BIN(@tenant) AND article_id=UUID_TO_BIN(@article) AND status IN ('pending','running') LIMIT 1;",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@article", requested.ToString()));
            if (running is not null)
                throw new CollectorStoreException("ARTICLE_GENERATION_IN_PROGRESS", "该文章已有正在进行的生成任务，请等待完成后再重新生成。", 409);
            var links = await RowAsync(connection, transaction,
                "SELECT COUNT(*) FROM content_publication_links WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND article_id=UUID_TO_BIN(@article) AND status='active';",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", requested.ToString()));
            if (links is not null && long.Parse(links[0], CultureInfo.InvariantCulture) > 0) regeneratedAsNew = true;
            else { articleId = requested; reuseArticle = true; }
        }

        var batchId = Guid.NewGuid(); var taskId = Guid.NewGuid(); var reservationId = Guid.NewGuid();
        var snapshot = new ArticleStudioSnapshot(ArticleStudioLimits.StudioPipeline, submission.Prompt.PromptId, submission.Goal,
            submission.GoalBasis, submission.IncludeCompetitors, submission.Competitors, submission.ArticleId, regeneratedAsNew,
            submission.WithoutRag);
        var snapshotJson = JsonSerializer.Serialize(new StudioBatchSnapshot(submission.RequestHash, snapshot, submission.Requirements), StudioJson);
        await ExecAsync(connection, transaction, """
            INSERT INTO article_generation_batches (id,tenant_id,site_id,requested_by,idempotency_key,request_hash,status,total_tasks,reserved_credits,request_snapshot,preview_expires_at,created_at)
            VALUES (UUID_TO_BIN(@batch),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),@key,@hash,'queued',1,@total,@snapshot,DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 15 MINUTE),UTC_TIMESTAMP(6));
            UPDATE credit_accounts SET available_credits=available_credits-@total,reserved_credits=reserved_credits+@total,version=version+1,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant);
            INSERT INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,actor_user_id,external_reference,reason,created_at)
            SELECT UUID_TO_BIN(UUID()),tenant_id,'reserve',-@total,available_credits,UUID_TO_BIN(@user),CONCAT('batch-reserve:',@batch),'文章生成预占',UTC_TIMESTAMP(6) FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@batch", batchId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
            ("@user", userId.ToString()), ("@key", idempotencyKey), ("@hash", storedHash), ("@total", submission.UnitPrice), ("@snapshot", snapshotJson));
        if (!reuseArticle)
        {
            var content = submission.Prompt.Content.Trim();
            var title = content.Length == 0 ? $"待生成文章 · {submission.Prompt.PromptId:N}" : content[..Math.Min(200, content.Length)];
            await ExecAsync(connection, transaction, "INSERT INTO content_articles (id,tenant_id,site_id,created_by,source_kind,status,title,language,monitor_status,created_at,updated_at) VALUES (UUID_TO_BIN(@article),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),'ai_generated','active',@title,@language,'inactive',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)); INSERT INTO content_monitor_scopes (id,tenant_id,site_id,article_id,mode,filter_snapshot,version,created_at,updated_at) VALUES (UUID_TO_BIN(UUID()),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),'all_brand_observations',JSON_OBJECT('activeWindows',JSON_ARRAY()),1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));",
                cancellationToken, ("@article", articleId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@user", userId.ToString()), ("@title", title), ("@language", submission.Language));
        }
        await ExecAsync(connection, transaction, "INSERT INTO article_generation_tasks (id,tenant_id,site_id,batch_id,article_id,prompt_id,prompt_snapshot,target_snapshot,knowledge_selection_snapshot,context_snapshot,reference_snapshot,material_notes,context_id,context_snapshot_hash,language,strategy_key,length_profile,style,audience,keywords_json,extra_requirements,without_rag,pipeline,include_competitors,optimization_goal,status,next_attempt_at,reservation_id,created_at,updated_at) VALUES (UUID_TO_BIN(@task),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@batch),UUID_TO_BIN(@article),UUID_TO_BIN(@prompt),@prompt_snapshot,@target_snapshot,@knowledge,@context_snapshot,NULL,NULL,UUID_TO_BIN(@context),@context_hash,@language,@strategy,@length,@style,NULL,@keywords,@extra,@without_rag,'studio_v1',@competitors,@goal,'pending',UTC_TIMESTAMP(6),UUID_TO_BIN(@reservation),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)); INSERT INTO credit_reservations (id,tenant_id,batch_id,task_id,amount,status,idempotency_key,created_at) VALUES (UUID_TO_BIN(@reservation),UUID_TO_BIN(@tenant),UUID_TO_BIN(@batch),UUID_TO_BIN(@task),@amount,'held',CONCAT('task:',@task),UTC_TIMESTAMP(6));",
            cancellationToken, ("@task", taskId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@batch", batchId.ToString()),
            ("@article", articleId.ToString()), ("@prompt", submission.Prompt.PromptId.ToString()), ("@prompt_snapshot", JsonSerializer.Serialize(submission.Prompt, JsonOptions)),
            ("@target_snapshot", submission.TargetSnapshotJson), ("@knowledge", submission.KnowledgeSelectionSnapshot), ("@context_snapshot", submission.ContextSnapshot),
            ("@context", submission.ContextId?.ToString()), ("@context_hash", submission.ContextHash), ("@language", submission.Language), ("@strategy", submission.Strategy),
            ("@length", submission.LengthProfile), ("@style", submission.Style), ("@keywords", "[]"), ("@extra", submission.Requirements),
            ("@without_rag", submission.WithoutRag), ("@competitors", submission.IncludeCompetitors), ("@goal", ArticleStudioRules.GoalValue(submission.Goal)),
            ("@reservation", reservationId.ToString()), ("@amount", submission.UnitPrice));
        for (var index = 0; index < ArticleStudioRules.Stages.Count; index++)
            await ExecAsync(connection, transaction,
                "INSERT INTO article_generation_task_stages (tenant_id,task_id,stage_key,position,status,updated_at) VALUES (UUID_TO_BIN(@tenant),UUID_TO_BIN(@task),@key,@position,'pending',UTC_TIMESTAMP(6));",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@task", taskId.ToString()),
                ("@key", ArticleStudioRules.StageValue(ArticleStudioRules.Stages[index])), ("@position", index + 1));
        await transaction.CommitAsync(cancellationToken);
        return new ArticleStudioGenerationAccepted(batchId, taskId, articleId, submission.UnitPrice, regeneratedAsNew,
            submission.Goal, submission.IncludeCompetitors, submission.WithoutRag, StudioPollPath(siteId, taskId));
    }

    private async Task<(ArticleStudioGenerationAccepted Accepted, string RequestHash)> ReadStudioAcceptedAsync(
        MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId, Guid siteId, Guid batchId,
        CancellationToken cancellationToken)
    {
        var row = await RowAsync(connection, transaction,
            "SELECT b.request_snapshot,BIN_TO_UUID(t.id),BIN_TO_UUID(t.article_id),COALESCE(r.amount,b.reserved_credits) FROM article_generation_batches b JOIN article_generation_tasks t ON t.tenant_id=b.tenant_id AND t.batch_id=b.id LEFT JOIN credit_reservations r ON r.tenant_id=t.tenant_id AND r.id=t.reservation_id WHERE b.tenant_id=UUID_TO_BIN(@tenant) AND b.site_id=UUID_TO_BIN(@site) AND b.id=UUID_TO_BIN(@batch) ORDER BY t.created_at LIMIT 1;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@batch", batchId.ToString()));
        var snapshot = row is null ? null : ReadStudioBatchSnapshot(row[0]);
        // A legacy wizard batch with the same key, or a damaged snapshot, can never be a replay of this request.
        if (row is null || snapshot is null)
            throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "幂等键已用于不同生成请求。", 409);
        var taskId = Guid.Parse(row[1]);
        return (new ArticleStudioGenerationAccepted(batchId, taskId, Guid.Parse(row[2]),
            decimal.Parse(row[3], CultureInfo.InvariantCulture), snapshot.Studio.RegeneratedAsNew, snapshot.Studio.Goal,
            snapshot.Studio.IncludeCompetitors, snapshot.Studio.WithoutRag, StudioPollPath(siteId, taskId)), snapshot.RequestHash);
    }

    private static StudioBatchSnapshot? ReadStudioBatchSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<StudioBatchSnapshot>(json, StudioJson);
            return snapshot?.Studio is { Pipeline: ArticleStudioLimits.StudioPipeline } && !string.IsNullOrEmpty(snapshot.RequestHash) ? snapshot : null;
        }
        catch (JsonException) { return null; }
    }

    private static string StudioPollPath(Guid siteId, Guid taskId) => $"/api/v1/sites/{siteId}/article-studio/generations/{taskId}";

    private static void ValidateStudioKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new CollectorStoreException("IDEMPOTENCY_KEY_REQUIRED", "请提供 Idempotency-Key。", 400);
        if (idempotencyKey.Length > 191) throw new CollectorStoreException("INVALID_IDEMPOTENCY_KEY", "幂等键无效。", 400);
    }

    /// <inheritdoc />
    public async Task<ArticleStudioExistingArticleView?> GetLatestStudioArticleAsync(Guid tenantId, Guid siteId, Guid promptId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        if (!await ArticleStudioSchema.ExistsAsync(connection, cancellationToken)) return null;
        var row = await RowAsync(connection, null, """
            SELECT BIN_TO_UUID(a.id),a.title,a.status,BIN_TO_UUID(t.id),t.status,
                   (SELECT COUNT(*) FROM content_publication_links l WHERE l.tenant_id=a.tenant_id AND l.site_id=a.site_id AND l.article_id=a.id AND l.status='active')
            FROM article_generation_tasks t
            JOIN content_articles a ON a.tenant_id=t.tenant_id AND a.site_id=t.site_id AND a.id=t.article_id
            WHERE t.tenant_id=UUID_TO_BIN(@tenant) AND t.site_id=UUID_TO_BIN(@site) AND t.prompt_id=UUID_TO_BIN(@prompt)
              AND t.pipeline='studio_v1' AND a.status='active'
            ORDER BY t.created_at DESC, t.id DESC LIMIT 1;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@prompt", promptId.ToString()));
        if (row is null) return null;
        return new ArticleStudioExistingArticleView(Guid.Parse(row[0]), row[1], ArticleStatus.Active,
            long.Parse(row[5], CultureInfo.InvariantCulture) > 0, Guid.Parse(row[3]), ParseTaskStatus(row[4]));
    }

    /// <inheritdoc />
    public async Task<ArticleStudioGenerationView?> GetStudioGenerationAsync(Guid tenantId, Guid siteId, Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await ArticleStudioSchema.RequireAsync(connection, cancellationToken);
        var task = await RowAsync(connection, null, """
            SELECT BIN_TO_UUID(t.id),BIN_TO_UUID(t.batch_id),BIN_TO_UUID(t.article_id),BIN_TO_UUID(t.prompt_id),t.status,t.optimization_goal,
                   t.include_competitors,t.attempt_count,t.last_error_code,t.last_error_message,COALESCE(r.status,'held'),
                   (SELECT BIN_TO_UUID(v.id) FROM content_article_versions v WHERE v.tenant_id=t.tenant_id AND v.article_id=t.article_id AND v.generation_batch_id=t.batch_id ORDER BY v.version_no DESC LIMIT 1),
                   t.created_at,t.updated_at
            FROM article_generation_tasks t
            LEFT JOIN credit_reservations r ON r.tenant_id=t.tenant_id AND r.id=t.reservation_id
            WHERE t.tenant_id=UUID_TO_BIN(@tenant) AND t.site_id=UUID_TO_BIN(@site) AND t.id=UUID_TO_BIN(@task) AND t.pipeline='studio_v1';
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@task", taskId.ToString()));
        if (task is null) return null;
        var stages = await ListStagesAsync(connection, tenantId, taskId, cancellationToken);
        var stageViews = ArticleStudioRules.Stages.Select((key, index) =>
        {
            var stage = stages.FirstOrDefault(item => item.Key == key);
            long? duration = stage?.StartedAt is { } started && stage.FinishedAt is { } finished
                ? (long)Math.Max(0, (finished - started).TotalMilliseconds) : null;
            return new ArticleStudioStageView(key, index + 1, stage?.Status ?? ArticleStudioStageStatus.Pending,
                stage?.StartedAt, stage?.FinishedAt, duration, stage?.Detail);
        }).ToArray();
        var sourceRows = await RowsAsync(connection, null, """
            SELECT BIN_TO_UUID(id),source_kind,normalized_url,domain,title,engines_json,citation_count,is_competitor,fetch_status,error_code,position
            FROM article_generation_task_sources WHERE tenant_id=UUID_TO_BIN(@tenant) AND task_id=UUID_TO_BIN(@task)
            ORDER BY source_kind='web_search', position, id;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@task", taskId.ToString()));
        var sources = sourceRows.Select(row =>
        {
            var fetch = ArticleStudioRules.ParseFetch(row[8]);
            return new ArticleStudioSourceView(Guid.Parse(row[0]),
                row[1] == "web_search" ? ArticleStudioSourceKind.WebSearch : ArticleStudioSourceKind.Citation,
                row[2], row[3], Null(row[4]), DeserializeStrings(row[5]), int.Parse(row[6], CultureInfo.InvariantCulture),
                ParseBool(row[7]), fetch, Null(row[9]), ArticleStudioRules.FetchErrorLabel(Null(row[9])),
                int.Parse(row[10], CultureInfo.InvariantCulture));
        }).ToArray();
        var citations = sources.Where(source => source.Kind == ArticleStudioSourceKind.Citation).ToArray();
        return new ArticleStudioGenerationView(Guid.Parse(task[0]), Guid.Parse(task[1]), Guid.Parse(task[2]), ParseGuid(task[3]),
            ParseTaskStatus(task[4]), ArticleStudioRules.ParseGoal(Null(task[5])), ParseBool(task[6]),
            int.Parse(task[7], CultureInfo.InvariantCulture), stageViews, sources,
            citations.Count(source => source.FetchStatus == ArticleStudioFetchStatus.Fetched), citations.Length,
            ParseGuid(task[11]), Null(task[8]), Null(task[9]),
            Enum.TryParse<CreditReservationStatus>(task[10], true, out var reservation) ? reservation : CreditReservationStatus.Held,
            ParseDate(task[12]), ParseDate(task[13]));
    }

    /// <inheritdoc />
    public async Task<ArticleStudioSnapshot?> GetStudioSnapshotAsync(Guid tenantId, Guid batchId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, null,
            "SELECT request_snapshot FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@batch);",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@batch", batchId.ToString()));
        return row is null ? null : ReadStudioBatchSnapshot(row[0])?.Studio;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArticleStudioStageRecord>> ListStagesAsync(Guid tenantId, Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        return await ListStagesAsync(connection, tenantId, taskId, cancellationToken);
    }

    private static async Task<IReadOnlyList<ArticleStudioStageRecord>> ListStagesAsync(MySqlConnection connection, Guid tenantId,
        Guid taskId, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, null,
            "SELECT stage_key,status,DATE_FORMAT(started_at,'%Y-%m-%dT%H:%i:%s.%f'),DATE_FORMAT(finished_at,'%Y-%m-%dT%H:%i:%s.%f'),detail_json,output_text FROM article_generation_task_stages WHERE tenant_id=UUID_TO_BIN(@tenant) AND task_id=UUID_TO_BIN(@task) ORDER BY position;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@task", taskId.ToString()));
        var stages = new List<ArticleStudioStageRecord>();
        foreach (var row in rows)
        {
            if (ArticleStudioRules.ParseStage(row[0]) is not { } key) continue;
            ArticleStudioStageDetail? detail = null;
            if (!string.IsNullOrWhiteSpace(row[4]))
            {
                try { detail = JsonSerializer.Deserialize<ArticleStudioStageDetail>(row[4], StudioJson); }
                catch (JsonException) { detail = null; }
            }
            stages.Add(new ArticleStudioStageRecord(key, ArticleStudioRules.ParseStatus(row[1]),
                string.IsNullOrWhiteSpace(row[2]) ? null : ParseDate(row[2]),
                string.IsNullOrWhiteSpace(row[3]) ? null : ParseDate(row[3]), detail, Null(row[5])));
        }
        return stages;
    }

    /// <inheritdoc />
    public async Task<bool> SaveStageAsync(ArticleGenerationWorkItem workItem, ArticleStudioStageRecord stage,
        CancellationToken cancellationToken = default)
    {
        var detail = stage.Detail is null ? null : JsonSerializer.Serialize(stage.Detail, StudioJson);
        var output = stage.Output is null ? null : stage.Output.Length <= 100_000 ? stage.Output : stage.Output[..100_000];
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Fenced by the task lease: a Worker that lost its lease cannot overwrite the new owner's progress.
        var owner = await RowAsync(connection, transaction,
            "SELECT 1 FROM article_generation_tasks WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@task) AND status='running' AND lease_owner=@owner FOR SHARE;",
            cancellationToken, ("@tenant", workItem.TenantId.ToString()), ("@task", workItem.TaskId.ToString()), ("@owner", workItem.LeaseOwner));
        if (owner is null) { await transaction.RollbackAsync(cancellationToken); return false; }
        await ExecAsync(connection, transaction, """
            INSERT INTO article_generation_task_stages (tenant_id,task_id,stage_key,position,status,started_at,finished_at,detail_json,output_text,updated_at)
            VALUES (UUID_TO_BIN(@tenant),UUID_TO_BIN(@task),@key,@position,@status,@started,@finished,@detail,@output,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE status=VALUES(status),started_at=VALUES(started_at),finished_at=VALUES(finished_at),
                detail_json=VALUES(detail_json),output_text=VALUES(output_text),updated_at=VALUES(updated_at);
            """, cancellationToken, ("@key", ArticleStudioRules.StageValue(stage.Key)),
            ("@position", ArticleStudioRules.Stages.ToList().IndexOf(stage.Key) + 1), ("@status", ArticleStudioRules.StatusValue(stage.Status)),
            ("@started", stage.StartedAt?.UtcDateTime), ("@finished", stage.FinishedAt?.UtcDateTime), ("@detail", detail), ("@output", output),
            ("@tenant", workItem.TenantId.ToString()), ("@task", workItem.TaskId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArticleStudioSourceRecord>> ListSourcesAsync(Guid tenantId, Guid taskId,
        ArticleStudioSourceKind kind, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var rows = await RowsAsync(connection, null, """
            SELECT normalized_url,domain,title,engines_json,citation_count,ownership,is_competitor,fetch_status,error_code,content_text,position
            FROM article_generation_task_sources WHERE tenant_id=UUID_TO_BIN(@tenant) AND task_id=UUID_TO_BIN(@task) AND source_kind=@kind
            ORDER BY position, id;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@task", taskId.ToString()), ("@kind", StudioSourceKindValue(kind)));
        return rows.Select(row => new ArticleStudioSourceRecord(kind, row[0], row[1], Null(row[2]), DeserializeStrings(row[3]),
            int.Parse(row[4], CultureInfo.InvariantCulture), Null(row[5]), ParseBool(row[6]), ArticleStudioRules.ParseFetch(row[7]),
            Null(row[8]), Null(row[9]), int.Parse(row[10], CultureInfo.InvariantCulture))).ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> ReplaceSourcesAsync(ArticleGenerationWorkItem workItem, ArticleStudioSourceKind kind,
        IReadOnlyList<ArticleStudioSourceRecord> sources, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var owner = await RowAsync(connection, transaction,
            "SELECT 1 FROM article_generation_tasks WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@task) AND status='running' AND lease_owner=@owner FOR SHARE;",
            cancellationToken, ("@tenant", workItem.TenantId.ToString()), ("@task", workItem.TaskId.ToString()), ("@owner", workItem.LeaseOwner));
        if (owner is null) { await transaction.RollbackAsync(cancellationToken); return false; }
        await ExecAsync(connection, transaction,
            "DELETE FROM article_generation_task_sources WHERE tenant_id=UUID_TO_BIN(@tenant) AND task_id=UUID_TO_BIN(@task) AND source_kind=@kind;",
            cancellationToken, ("@tenant", workItem.TenantId.ToString()), ("@task", workItem.TaskId.ToString()), ("@kind", StudioSourceKindValue(kind)));
        foreach (var source in sources)
        {
            var content = source.Content is null ? null
                : source.Content.Length <= ArticleStudioLimits.SourceContentMaxLength ? source.Content : source.Content[..ArticleStudioLimits.SourceContentMaxLength];
            await ExecAsync(connection, transaction, """
                INSERT INTO article_generation_task_sources (id,tenant_id,task_id,source_kind,normalized_url,domain,title,engines_json,citation_count,ownership,is_competitor,fetch_status,error_code,content_text,position,created_at)
                VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@task),@kind,@url,@domain,@title,@engines,@count,@ownership,@competitor,@fetch,@error,@content,@position,UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", Guid.NewGuid().ToString()), ("@tenant", workItem.TenantId.ToString()), ("@task", workItem.TaskId.ToString()),
                ("@kind", StudioSourceKindValue(kind)), ("@url", Bound(source.Url, 2048)), ("@domain", Bound(source.Domain, 255)),
                ("@title", source.Title is null ? null : Bound(source.Title, 500)),
                ("@engines", JsonSerializer.Serialize(source.Engines.Take(20).Select(engine => Bound(engine, 64)).ToArray())),
                ("@count", Math.Max(0, source.CitationCount)), ("@ownership", source.Ownership is null ? null : Bound(source.Ownership, 32)),
                ("@competitor", source.IsCompetitor), ("@fetch", ArticleStudioRules.FetchValue(source.FetchStatus)),
                ("@error", source.ErrorCode is null ? null : Bound(source.ErrorCode, 64)), ("@content", content), ("@position", Math.Max(1, source.Position)));
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static string StudioSourceKindValue(ArticleStudioSourceKind kind) => kind == ArticleStudioSourceKind.WebSearch ? "web_search" : "citation";
    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];
}
