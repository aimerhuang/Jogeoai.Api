using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// App 结果入库（implement 6.3，contract.md 9.6，results.md 8.1）。加锁顺序：租约的绑定 → 任务 → 租户的积分账户 →
/// 检测与分析状态 → Run。绑定行先于任务行加锁，与领取、失败上报的顺序一致；绑定取自任务的租约，不采信请求。
/// </summary>
public sealed partial class AppCollectorPoolStore
{
    // chk_visibility_result_metadata_size allows 16384 bytes of the stored JSON text; the rest is headroom.
    private const int ResultMetadataBudgetBytes = 16_000;

    // ck_collector_submissions_citations_size.
    private const int SubmissionCitationsBytes = 262_144;

    // ck_visibility_checks_search_size.
    private const int SearchQueriesBytes = 65_536;

    /// <summary>result_metadata.app.sourcesWithoutUrl 的条数上限（results.md 8.1）。</summary>
    internal const int MaxSourcesWithoutUrl = 100;

    // Non-ASCII text is written as it is, like MySQL's own JSON text, so that byte counts can be estimated before the write.
    private static readonly JsonSerializerOptions Stored = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record LeaseBinding(Guid Id, string NodeId, string DeviceId, string AccountLabel);

    /// <inheritdoc />
    public async Task<CollectorResultResponse> SubmitResultAsync(AppResultCommand command, CancellationToken cancellationToken = default)
    {
        command = AppResultRules.Validate(command);
        var hash = AppResultRules.PayloadHash(command.AnswerText, command.Citations);
        var citations = JsonSerializer.Serialize(command.Citations, Stored);
        if (StoredJsonBytes(citations) > SubmissionCitationsBytes)
            throw new CollectorStoreException(AppResultRules.InvalidCode, "citations 的总大小超过上限。", 400);
        var queries = command.SearchQueries is null ? null : JsonSerializer.Serialize(command.SearchQueries, Stored);
        if (queries is not null && StoredJsonBytes(queries) > SearchQueriesBytes)
            throw new CollectorStoreException(AppResultRules.InvalidCode, "searchQueries 的总大小超过上限。", 400);
        // The binding is locked before the task, so it is read from the task first without a lock; if the lease moved to
        // another binding in between, the transaction starts over (a lease that keeps moving is not the submitter's).
        for (var attempt = 1; ; attempt++)
        {
            var response = await InTransactionAsync(async (c, t, ct)
                => Done(await SubmitAsync(c, t, command, hash, citations, queries, lastAttempt: attempt >= 3, ct)), cancellationToken);
            if (response is not null) return response;
        }
    }

    private static async Task<CollectorResultResponse?> SubmitAsync(MySqlConnection c, MySqlTransaction t, AppResultCommand command, string hash,
        string citations, string? queries, bool lastAttempt, CancellationToken ct)
    {
        var task = ("@task", (object)command.TaskId.ToString());
        var glance = await QuerySingleAsync(c, t, "SELECT BIN_TO_UUID(lease_app_binding_id), channel FROM collector_tasks WHERE id = UUID_TO_BIN(@task);", ct, task);
        // A web task is not an App task: the App route does not say whether it exists.
        if (glance is null || glance[1] != AppChannel) throw new CollectorStoreException("TASK_NOT_FOUND", "采集任务不存在。", 404);
        LeaseBinding? binding = null;
        if (glance[0].Length > 0)
        {
            var owner = await QuerySingleAsync(c, t, """
                SELECT node_id, device_id, account_label FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id) FOR UPDATE;
                """, ct, ("@id", glance[0]));
            if (owner is not null) binding = new LeaseBinding(Guid.Parse(glance[0]), owner[0], owner[1], owner[2]);
        }
        var row = await QuerySingleAsync(c, t, """
            SELECT BIN_TO_UUID(tenant_id), BIN_TO_UUID(run_id), BIN_TO_UUID(check_id), status, lease_owner, lease_version,
                   (lease_until IS NOT NULL AND lease_until > UTC_TIMESTAMP(6)), BIN_TO_UUID(lease_app_binding_id), question_snapshot,
                   COALESCE(prompt_rule_version, 0), region
            FROM collector_tasks WHERE id = UUID_TO_BIN(@task) FOR UPDATE;
            """, ct, task) ?? throw new CollectorStoreException("TASK_NOT_FOUND", "采集任务不存在。", 404);
        var moved = !string.Equals(row[7], glance[0], StringComparison.OrdinalIgnoreCase);
        if (moved && !lastAttempt) return null;
        var (tenantId, runId, checkId) = (Guid.Parse(row[0]), Guid.Parse(row[1]), Guid.Parse(row[2]));

        // The same comparison the collector makes locally: an answer that is the question is not a sample.
        if (AppResultRules.IsEcho(command.AnswerText, row[8]))
            throw new CollectorStoreException(AppResultRules.EchoCode, "回答与提问原文相同（回显），不是有效样本。", 400);

        var key = ("@key", (object)command.IdempotencyKey);
        var prior = await QuerySingleAsync(c, t, """
            SELECT disposition, payload_hash FROM collector_result_submissions
            WHERE collector_task_id = UUID_TO_BIN(@task) AND idempotency_key = @key LIMIT 1;
            """, ct, task, key);
        if (prior is not null)
        {
            if (!string.Equals(prior[1], hash, StringComparison.Ordinal))
                throw new CollectorStoreException(AppResultRules.IdempotencyConflictCode, "该幂等键已用于内容不同的结果。", 409);
            await InsertResultAttemptAsync(c, t, command, row, "duplicate", null, null, ct);
            // The sample exists only if the first submission was accepted.
            return new CollectorResultResponse(CollectorResultDisposition.Duplicate, prior[0] == "accepted" ? checkId : null);
        }

        // The node must hold the current lease: its own node, a binding of that node, the same version, not expired; and
        // the check must still be open (every App writer of a check holds its task lock, so this read is stable).
        var check = await QuerySingleAsync(c, t, """
            SELECT status, COALESCE(OCTET_LENGTH(result_metadata), 0) FROM visibility_checks
            WHERE id = UUID_TO_BIN(@check) AND tenant_id = UUID_TO_BIN(@tenant);
            """, ct, ("@check", checkId.ToString()), ("@tenant", tenantId.ToString()));
        var live = !moved && binding is not null && string.Equals(binding.NodeId, command.NodeId, StringComparison.Ordinal)
            && string.Equals(binding.DeviceId, command.DeviceId, StringComparison.Ordinal)
            && (row[3] is "leased" or "collecting") && string.Equals(row[4], command.NodeId, StringComparison.Ordinal)
            && Int(row[5]) == command.LeaseVersion && Flag(row[6]) && check is not null && (check[0] is "pending" or "running");

        await ExecuteAsync(c, t, """
            INSERT INTO collector_result_submissions
                (id, tenant_id, collector_task_id, lease_version, node_id, region, idempotency_key, payload_hash, disposition,
                 answer_text, citations, egress_country, collector_build_revision, provider_metadata, completed_at, received_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@task), @version, @node, @region, @key, @hash, @disposition,
                 @answer, @citations, 'CN', @revision, @metadata, @completed, UTC_TIMESTAMP(6));
            """, ct, task, key, ("@id", Guid.NewGuid().ToString()), ("@tenant", tenantId.ToString()), ("@version", command.LeaseVersion),
            ("@node", command.NodeId), ("@region", row[10]), ("@hash", hash), ("@disposition", live ? "accepted" : "stale"),
            ("@answer", command.AnswerText), ("@citations", citations), ("@revision", command.CollectorBuildRevision),
            ("@metadata", JsonSerializer.Serialize(new
            {
                channel = AppChannel, sentAt = command.SentAt.UtcDateTime, citationsStatus = AppContractNames.ToStorage(command.CitationsStatus),
                citationsIssue = command.CitationsIssue,
                searchState = new
                {
                    webSearch = AppContractNames.ToStorage(command.WebSearch), deepThinking = AppContractNames.ToStorage(command.DeepThinking),
                    searchQueries = command.SearchQueries,
                },
                device = new { deviceId = command.DeviceId, androidVersion = command.AndroidVersion, abi = command.Abi },
                app = new { package = command.AppPackage, version = command.AppVersion },
                screenshotSha256 = command.ScreenshotSha256,
            }, Stored)), ("@completed", command.CompletedAt.UtcDateTime));
        if (!live)
        {
            await InsertResultAttemptAsync(c, t, command, row, "stale", hash, null, ct);
            return new CollectorResultResponse(CollectorResultDisposition.Stale, null);
        }

        // Credits first (the account row), then the check, the analysis state and the run. A check without a held
        // reservation (it should not exist) is still a valid sample: it is recorded and nothing is charged.
        var lease = binding!;
        if (await RecordSendAsync(c, t, new AppSentCommand(command.NodeId, lease.Id, command.TaskId, command.LeaseVersion, command.SentAt), true, ct) == "stale")
            throw new CollectorStoreException("ATTEMPT_NOT_FOUND", "无法核实设备尝试，必须先处理旧租约。", 409);
        if (!await AppCreditReservations.CaptureAsync(c, t, tenantId, checkId, ct))
            await InsertEventAsync(c, t, new AppEvent("result_without_reservation", NodeActor(command.NodeId), command.NodeId, lease.Id,
                TaskId: command.TaskId, Detail: new { checkId, leaseVersion = command.LeaseVersion }), ct);

        var (withUrl, metadata) = AppMetadata(command, lease, Int(row[9]), ResultMetadataBudgetBytes - Int(check![1]));
        // result_metadata keeps whatever else is stored on the row; only its "app" member is written.
        await ExecuteAsync(c, t, """
            UPDATE visibility_checks
            SET status = 'succeeded', answer = @answer, answer_present = TRUE, capture_mode = @capture, evidence_class = @evidence,
                search_queries = COALESCE(CAST(@queries AS JSON), search_queries),
                query_availability = COALESCE(@availability, query_availability),
                collector_task_id = UUID_TO_BIN(@task), collector_node_id = @node, collector_region = @region,
                collector_build_revision = @revision, captured_at = @completed, result_hash = @hash,
                app_binding_id = UUID_TO_BIN(@binding),
                result_metadata = JSON_SET(COALESCE(result_metadata, JSON_OBJECT()), '$.app', CAST(@app AS JSON)),
                error_code = NULL, error_message = NULL, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@check) AND tenant_id = UUID_TO_BIN(@tenant);
            """, ct, task, ("@answer", command.AnswerText), ("@capture", AppChannelDefinition.CaptureMode),
            ("@evidence", AppChannelDefinition.EvidenceClass), ("@queries", queries),
            ("@availability", command.SearchQueries is null ? null : command.SearchQueries.Count > 0 ? "available" : "empty"),
            ("@node", command.NodeId), ("@region", row[10]), ("@revision", command.CollectorBuildRevision),
            ("@completed", command.CompletedAt.UtcDateTime), ("@hash", hash), ("@binding", lease.Id.ToString()), ("@app", metadata),
            ("@check", checkId.ToString()), ("@tenant", tenantId.ToString()));
        // Only sources with a usable URL enter the citation table, numbered in the order they were shown (as the web intake does).
        for (var i = 0; i < withUrl.Count; i++)
            await ExecuteAsync(c, t, """
                INSERT INTO citations (id, check_id, normalized_url, title, source_domain, position, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@check), @url, @title, @domain, @position, UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE title = COALESCE(VALUES(title), title);
                """, ct, ("@id", Guid.NewGuid().ToString()), ("@check", checkId.ToString()), ("@url", withUrl[i].Url),
                ("@title", withUrl[i].Title), ("@domain", withUrl[i].Domain), ("@position", i + 1));
        await ExecuteAsync(c, t, """
            UPDATE collector_tasks SET status = 'succeeded', lease_owner = NULL, lease_until = NULL,
                completed_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@task);
            UPDATE collector_app_bindings SET last_success_at = UTC_TIMESTAMP(6), last_seen_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@binding);
            """, ct, task, ("@binding", lease.Id.ToString()));
        await InsertResultAttemptAsync(c, t, command, row, "accepted", hash, lease.Id, ct);
        await AnalysisEnqueue.QueueAsync(c, t, checkId, ct);
        await AppCollectionStore.UpdateRunStatusAsync(c, t, runId, ct);
        return new CollectorResultResponse(CollectorResultDisposition.Accepted, checkId);
    }

    /// <summary>
    /// 把引用分成两部分并生成 result_metadata 的 app 块（results.md 8.1）：URL 可用（与网页结果入库相同的规范化规则）的进入引用表；
    /// 其余的只保留标题、站点名和列表名，放进 sourcesWithoutUrl。该列表最多 <see cref="MaxSourcesWithoutUrl"/> 条，
    /// 并且整个块不超过 <paramref name="budgetBytes"/>（列上有 16384 字节的约束）：放不下时从末尾去掉，
    /// citationsTotal 与 citationsWithUrl 仍是完整的计数，完整的引用列表在 collector_result_submissions 里。
    /// 块里的 sourcesWithoutUrlTruncated 明确写出这份列表是否被截断（保留的条数少于没有 URL 的引用总数时为 true），
    /// 读取方不必再从条数差推断。
    /// </summary>
    internal static (List<(string Url, string Domain, string Title)> WithUrl, string Metadata) AppMetadata(AppResultCommand command,
        string deviceId, string accountLabel, int promptRuleVersion, int budgetBytes)
    {
        var withUrl = new List<(string Url, string Domain, string Title)>();
        var without = new List<object>();
        foreach (var citation in command.Citations)
        {
            if (citation.Url is { } url && NormalizeCitationUrl(url) is { } normalized) withUrl.Add((normalized.Url, normalized.Domain, citation.Title));
            else if (without.Count < MaxSourcesWithoutUrl) without.Add(new { title = citation.Title, site = citation.Site, list = citation.List });
        }
        // Every source that is not a citation, whether or not it is kept below.
        var withoutTotal = command.Citations.Count - withUrl.Count;
        while (true)
        {
            var json = JsonSerializer.Serialize(new
            {
                deviceId, androidVersion = command.AndroidVersion, appPackage = command.AppPackage, appVersion = command.AppVersion, accountLabel,
                webSearch = AppContractNames.ToStorage(command.WebSearch), deepThinking = AppContractNames.ToStorage(command.DeepThinking),
                citationsStatus = AppContractNames.ToStorage(command.CitationsStatus), citationsIssue = command.CitationsIssue,
                citationsTotal = command.Citations.Count, citationsWithUrl = withUrl.Count, sourcesWithoutUrl = without,
                // The member name is AppAnswerMetadataCodec.TruncatedMember, which the answer detail reads.
                sourcesWithoutUrlTruncated = without.Count < withoutTotal,
                screenshotSha256 = command.ScreenshotSha256, promptRuleVersion,
            }, Stored);
            if (without.Count == 0 || StoredJsonBytes(json) + "{\"app\": }".Length <= budgetBytes) return (withUrl, json);
            without.RemoveAt(without.Count - 1);
        }
    }

    private static (List<(string Url, string Domain, string Title)> WithUrl, string Metadata) AppMetadata(AppResultCommand command,
        LeaseBinding binding, int promptRuleVersion, int budgetBytes)
        => AppMetadata(command, binding.DeviceId, binding.AccountLabel, promptRuleVersion, budgetBytes);

    /// <summary>
    /// JSON 文本写进 MySQL 的 JSON 列之后的字节数上界：MySQL 在每个逗号和冒号后面多写一个空格，非 ASCII 字符按 UTF-8 原样输出。
    /// 字符串内部的逗号和冒号也被算进去，所以只会高估。
    /// </summary>
    internal static int StoredJsonBytes(string json) => Encoding.UTF8.GetByteCount(json) + json.Count(character => character is ',' or ':');

    /// <summary>
    /// 与网页结果入库（DomesticCollectorStore.NormalizeUrl）相同的规则：只接受不带用户信息的 http / https 绝对地址，
    /// 主机必须是带点的域名（不是 IP、不是本机或内网名称），整个地址不超过 750 个字符；域名去掉 www. 前缀。
    /// </summary>
    internal static (string Url, string Domain)? NormalizeCitationUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (host.Contains(':') || !host.Contains('.', StringComparison.Ordinal) || System.Net.IPAddress.TryParse(host, out _)) return null;
        if (host is "localhost" or "0.0.0.0" || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal)
            || host.EndsWith(".intranet", StringComparison.Ordinal) || host.EndsWith(".invalid", StringComparison.Ordinal)) return null;
        if (uri.AbsoluteUri.Length > 750) return null;
        return (uri.AbsoluteUri, host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host);
    }

    // The attempt carries the binding only when the result was accepted: a stale or repeated submission names no binding.
    private static Task<int> InsertResultAttemptAsync(MySqlConnection c, MySqlTransaction t, AppResultCommand command, string[] taskRow,
        string eventKind, string? hash, Guid? bindingId, CancellationToken ct)
        => ExecuteAsync(c, t, """
            INSERT INTO collector_attempts (id, tenant_id, collector_task_id, lease_version, node_id, region, event_kind, idempotency_key,
                payload_hash, app_binding_id, created_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@task), @version, @node, @region, @event, @key, @hash,
                UUID_TO_BIN(@binding), UTC_TIMESTAMP(6));
            """, ct, ("@id", Guid.NewGuid().ToString()), ("@tenant", taskRow[0]), ("@task", command.TaskId.ToString()),
            ("@version", command.LeaseVersion), ("@node", command.NodeId), ("@region", taskRow[10]), ("@event", eventKind),
            ("@key", command.IdempotencyKey), ("@hash", hash), ("@binding", bindingId?.ToString()));
}
