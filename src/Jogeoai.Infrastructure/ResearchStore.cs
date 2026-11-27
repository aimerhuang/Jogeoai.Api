using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>品牌研究任务及全局研究 Provider 的 MySQL 实现。</summary>
/// <param name="database">MySQL 连接工厂。</param>
/// <param name="knowledgeItems">品牌知识条目存储；提供时入队会把条目冻结为 knowledge: 证据，未提供时不读取条目。</param>
/// <param name="quota">入队额度（<c>Research:Quota</c>）；未提供时使用默认值（每站点 1 个进行中任务、每租户滚动 24 小时 20 次）。</param>
public sealed class ResearchStore(MySqlDatabase database, IBrandKnowledgeItemStore? knowledgeItems = null,
    IOptions<ResearchQuotaOptions>? quota = null) : IResearchStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly ResearchQuotaOptions DefaultQuota = new();

    /// <summary>
    /// 入队事务的隔离级别：READ COMMITTED。事务内每条普通读都使用语句开始时的最新已提交数据，
    /// 所以“取得站点行锁之后再读进行中任务”一定能看到上一个持锁请求已提交的任务，不依赖读视图在事务里何时建立。
    /// 不要改回默认的 REPEATABLE READ：那样只有在“站点行的锁定读是事务第一条语句、之前没有任何普通读”时才成立。
    /// </summary>
    internal const IsolationLevel EnqueueIsolation = IsolationLevel.ReadCommitted;

    /// <summary>
    /// Worker 领取语句：可领取的任务按“最近最少被服务的租户优先”排序，只锁定候选任务行并跳过已被其他 Worker 锁住的行。
    /// 租户的排序值是它最近一次被服务的时间（该租户任务 started_at 的最大值）；从未被服务过的租户没有这个时间，用任务自己的创建时间代替，
    /// 所以新租户排在“上次服务早于它入队时间”的租户之后，而不是排在所有人之前（否则每注册一个新租户就能插队一次）。
    /// </summary>
    internal const string ClaimSql = """
        SELECT BIN_TO_UUID(j.id), BIN_TO_UUID(j.tenant_id), BIN_TO_UUID(j.site_id), BIN_TO_UUID(j.user_id),
               j.request_json, j.attempt_count, j.result_json
        FROM brand_research_jobs j
        WHERE (j.status = 'pending' AND j.next_attempt_at <= UTC_TIMESTAMP(6))
           OR (j.status = 'running' AND j.lease_until < UTC_TIMESTAMP(6))
        ORDER BY COALESCE((SELECT MAX(s.started_at) FROM brand_research_jobs s WHERE s.tenant_id = j.tenant_id), j.created_at),
                 j.created_at, j.id
        LIMIT 1 FOR UPDATE OF j SKIP LOCKED;
        """;

    public async Task<BrandResearchAccepted> EnqueueAsync(Guid tenantId, Guid siteId, Guid userId,
        StartBrandResearchRequest request, CancellationToken cancellationToken = default)
    {
        // 条目在入队时冻结进初始上下文：之后的编辑只影响下一次研究；无官网但有条目时可以不填业务介绍。
        var knowledgeEvidence = knowledgeItems is null ? []
            : ResearchContextRules.KnowledgeEvidence(await knowledgeItems.ListForResearchAsync(tenantId, siteId, cancellationToken));
        var normalized = Normalize(request, knowledgeEvidence.Count > 0);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(EnqueueIsolation, cancellationToken);
        var site = await Row(connection, transaction,
            "SELECT host, display_name FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@site) AND status = 'active' FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        if (site is null) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "工作区不存在。", 404);

        var inputKey = string.IsNullOrWhiteSpace(normalized.RefreshKey)
            ? $"research:{siteId:N}:{Guid.NewGuid():N}"
            : normalized.RefreshKey.Trim();
        if (inputKey.Length is < 1 or > 180) throw new ArgumentException("refreshKey 长度必须为 1–180。", nameof(request));
        var key = $"{ResearchContext.CurrentStrategy}:{ResearchContextRules.Hash(inputKey)}";
        var payload = JsonSerializer.Serialize(normalized, JsonOptions);
        var existing = await Row(connection, transaction,
            "SELECT BIN_TO_UUID(id), BIN_TO_UUID(site_id), status, created_at, request_json FROM brand_research_jobs WHERE tenant_id = UUID_TO_BIN(@tenant) AND idempotency_key = @key LIMIT 1;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@key", key));
        if (existing is not null)
        {
            if (!string.Equals(existing[1], siteId.ToString(), StringComparison.OrdinalIgnoreCase)
                || !JsonNode.DeepEquals(JsonNode.Parse(existing[4]), JsonNode.Parse(payload)))
                throw new CollectorStoreException("RESEARCH_IDEMPOTENCY_CONFLICT", "refreshKey 已用于不同的研究请求。", 409);
            await transaction.CommitAsync(cancellationToken);
            return new BrandResearchAccepted(Guid.Parse(existing[0]), ParseStatus(existing[2]), Utc(existing[3]));
        }

        // 入队额度：排在幂等重放之后（重放不计入、也不会被拦截）、插入之前，与插入同一事务并持有上面的站点行锁。
        // 两条都是普通（不加锁）读，不给 brand_research_jobs 加任何锁，所以不会与 Worker 领取或租约更新互相等待，
        // 也不会像 REPEATABLE READ 下的锁定读那样在 ix_brand_research_jobs_site_created 上留下间隙锁。
        // 事务是 READ COMMITTED（见 EnqueueIsolation）：每条语句读到的都是语句开始时已提交的数据。
        // 同一站点的并发请求在站点行锁上排队，前一个请求先提交再释放锁，后到的请求取得锁之后执行的这条读一定能看到它的任务，只会有一个通过。
        var limits = quota?.Value ?? DefaultQuota;
        var active = await Rows(connection, transaction, """
            SELECT BIN_TO_UUID(id) FROM brand_research_jobs
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND status IN ('pending','running')
            ORDER BY created_at DESC, id DESC
            LIMIT @limit;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@limit", limits.MaxActivePerSite));
        if (active.Count >= limits.MaxActivePerSite) throw new ResearchAlreadyActiveException(Guid.Parse(active[0][0]));
        // 滚动 24 小时而不是自然日；已取消和失败的任务同样计入（可能已经消耗了 Provider 调用）。
        // 不加租户级锁：同一租户不同站点的并发请求各持各的站点行锁，极端情况下会略微超过上限（最多“并发站点数 − 1”次）。
        var recent = await Row(connection, transaction, """
            SELECT COUNT(*) FROM brand_research_jobs
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND created_at > UTC_TIMESTAMP(6) - INTERVAL 24 HOUR;
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        if (long.Parse(recent![0], CultureInfo.InvariantCulture) >= limits.DailyPerTenant)
            throw new CollectorStoreException("RESEARCH_DAILY_LIMIT", "今天的品牌研究次数已用完，请明天再试。", 429);

        StoredResult? previous = null;
        if (normalized.ReuseJobId is { } previousId)
        {
            var prior = await Row(connection, transaction,
                "SELECT result_json FROM brand_research_jobs WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@id);",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", previousId.ToString()));
            if (prior is null) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "用于复用的研究任务不存在。", 404);
            previous = DeserializeResult(prior[0]);
        }
        var initial = ResearchContextRules.Create(normalized, previous?.Research, previous?.Suggestion, knowledgeEvidence);
        var id = Guid.NewGuid();
        await Exec(connection, transaction, """
            INSERT INTO brand_research_jobs
                (id, tenant_id, site_id, user_id, status, attempt_count, next_attempt_at, request_json,
                 idempotency_key, result_json, requested_at, created_at, updated_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@user), 'pending', 0,
                    UTC_TIMESTAMP(6), @request, @key, @result, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, cancellationToken, ("@id", id.ToString()), ("@tenant", tenantId.ToString()),
            ("@site", siteId.ToString()), ("@user", userId.ToString()), ("@request", payload), ("@key", key),
            ("@result", JsonSerializer.Serialize(new StoredResult(initial, null), JsonOptions)));
        await transaction.CommitAsync(cancellationToken);
        return new BrandResearchAccepted(id, BrandResearchJobStatus.Pending, DateTimeOffset.UtcNow);
    }

    public async Task<BrandResearchJobView?> GetAsync(Guid tenantId, Guid siteId, Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var row = await Row(connection, null, """
            SELECT status, result_json, error_code, error_message, provider, model, created_at, completed_at
            FROM brand_research_jobs
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@id);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", jobId.ToString()));
        if (row is null) return null;
        var result = DeserializeResult(row[1]);
        return new BrandResearchJobView(jobId, ParseStatus(row[0]), result?.Suggestion, Null(row[2]), Null(row[3]),
            Null(row[4]), Null(row[5]), Utc(row[6]), NullableUtc(row[7]), result?.Research);
    }

    public async Task<bool> CancelAsync(Guid tenantId, Guid siteId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var count = await Exec(connection, null, """
            UPDATE brand_research_jobs SET status = 'cancelled', lease_owner = NULL, lease_until = NULL,
                updated_at = UTC_TIMESTAMP(6), cancelled_at = UTC_TIMESTAMP(6), completed_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@id)
              AND status IN ('pending','running');
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", jobId.ToString()));
        return count > 0;
    }

    public async Task<ResearchWorkItem?> LeaseAsync(string workerId, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 租户间公平：最近最少被服务的租户优先，按租户最近一次开始时间升序；从未被领取过的租户（MAX(started_at) 为 NULL）
        // 以任务自己的 created_at 参与排序，不享有优先。同一租户内仍按创建顺序。
        // started_at 与 created_at 都是数据库的 UTC_TIMESTAMP(6)，可以直接比较；started_at 由下面的 UPDATE 在任务首次被领取时写入，
        // 重试或租约过期后的再次领取不刷新它（一个任务最多 3 次尝试，期间该租户的排序值不后移）。
        // 子查询属于嵌套查询块，外层的锁定子句不作用于它（普通快照读）；FOR UPDATE OF j 只锁被扫描的候选任务行。
        var row = await Row(connection, transaction, ClaimSql, cancellationToken);
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var leaseUntil = DateTime.UtcNow.Add(leaseDuration);
        var leaseOwner = $"{workerId[..Math.Min(workerId.Length, 80)]}:{Guid.NewGuid():N}";
        var attempt = int.Parse(row[5], CultureInfo.InvariantCulture) + 1;
        await Exec(connection, transaction, """
            UPDATE brand_research_jobs SET status = 'running', attempt_count = @attempt,
                lease_owner = @owner, lease_until = @until, started_at = COALESCE(started_at, UTC_TIMESTAMP(6)),
                error_code = NULL, error_message = NULL, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id);
            """, cancellationToken, ("@attempt", attempt), ("@owner", leaseOwner), ("@until", leaseUntil), ("@id", row[0]));
        await transaction.CommitAsync(cancellationToken);
        var request = JsonSerializer.Deserialize<StartBrandResearchRequest>(row[4], JsonOptions)
            ?? throw new InvalidOperationException("Stored research request is invalid.");
        return new ResearchWorkItem(Guid.Parse(row[0]), Guid.Parse(row[1]), Guid.Parse(row[2]), Guid.Parse(row[3]), request, attempt, leaseOwner, DeserializeResult(row[6])?.Research);
    }

    /// <inheritdoc />
    public async Task<bool> RenewLeaseAsync(Guid jobId, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return await Exec(connection, null, """
            UPDATE brand_research_jobs SET lease_until = @until, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id) AND lease_owner = @owner AND status = 'running' AND lease_until > UTC_TIMESTAMP(6);
            """, cancellationToken, ("@until", DateTime.UtcNow.Add(leaseDuration)), ("@id", jobId.ToString()), ("@owner", leaseOwner)) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> SaveProgressAsync(Guid jobId, string leaseOwner, ResearchContext context, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return await Exec(connection, null, """
            UPDATE brand_research_jobs SET result_json = @result, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id) AND lease_owner = @owner AND status = 'running' AND lease_until > UTC_TIMESTAMP(6);
            """, cancellationToken, ("@result", JsonSerializer.Serialize(new StoredResult(context, null), JsonOptions)),
            ("@id", jobId.ToString()), ("@owner", leaseOwner)) > 0;
    }

    public async Task CompleteAsync(Guid jobId, string leaseOwner, ResearchProviderResult result,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new StoredResult(result.Research, result.Suggestion), JsonOptions);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await Exec(connection, null, """
            UPDATE brand_research_jobs SET status = 'done', provider = @provider, model = @model,
                result_json = @result, error_code = NULL, error_message = NULL, lease_owner = NULL, lease_until = NULL,
                completed_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id) AND lease_owner = @owner AND status = 'running' AND lease_until > UTC_TIMESTAMP(6);
            """, cancellationToken, ("@provider", result.Provider), ("@model", result.Model),
            ("@result", payload), ("@id", jobId.ToString()), ("@owner", leaseOwner));
    }

    public async Task FailAsync(Guid jobId, string leaseOwner, string code, string message, bool retryable,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var next = retryable ? DateTime.UtcNow.AddMinutes(1) : DateTime.UtcNow;
        await Exec(connection, null, """
            UPDATE brand_research_jobs SET status = @status, error_code = @code, error_message = @message,
                lease_owner = NULL, lease_until = NULL, next_attempt_at = @next,
                completed_at = IF(@status = 'failed', UTC_TIMESTAMP(6), NULL), updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id) AND lease_owner = @owner AND status = 'running' AND lease_until > UTC_TIMESTAMP(6);
            """, cancellationToken, ("@status", retryable ? "pending" : "failed"), ("@code", code),
            ("@message", message.Length > 1000 ? message[..1000] : message), ("@next", next),
            ("@id", jobId.ToString()), ("@owner", leaseOwner));
    }

    public async Task<IReadOnlyList<ResearchProviderSettingsView>> ListProviderSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var rows = await Rows(connection, null, """
            SELECT provider, model, api_url, enabled, priority, web_search_enabled, timeout_seconds,
                   credential, credential_hint, updated_at
            FROM research_provider_settings ORDER BY priority, provider;
            """, cancellationToken);
        return rows.Select(row => new ResearchProviderSettingsView(row[0], row[1], row[2], Bool(row[3]), Int(row[4]),
            Bool(row[5]), Int(row[6]), !string.IsNullOrWhiteSpace(row[7]), Null(row[8]), Utc(row[9]))).ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> IsSystemAdministratorAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            var row = await Row(connection, null, """
                SELECT is_system_admin
                FROM auth_users
                WHERE id = UUID_TO_BIN(@user) AND status = 'active';
                """, cancellationToken, ("@user", userId.ToString()));
            return row is not null && Bool(row[0]);
        }
        catch (MySqlException exception) when (exception.Number is 1054 or 1146)
        {
            throw new CollectorStoreException("AUTH_SYSTEM_ADMIN_SCHEMA_NOT_MIGRATED",
                "系统管理员权限迁移尚未执行。", 503);
        }
    }

    public async Task<ResearchProviderConfiguration?> GetProviderConfigurationAsync(string provider,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var row = await Row(connection, null, """
            SELECT provider, model, api_url, credential, enabled, priority, web_search_enabled, timeout_seconds
            FROM research_provider_settings WHERE provider = @provider AND enabled = TRUE AND credential IS NOT NULL;
            """, cancellationToken, ("@provider", provider));
        if (row is null) return null;
        return new ResearchProviderConfiguration(row[0], row[1], row[2], row[3], Bool(row[6]), Int(row[7]), Int(row[5]));
    }

    public async Task SaveProviderSettingsAsync(string provider, SaveResearchProviderSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedProvider = provider?.Trim().ToLowerInvariant() ?? "";
        var model = request.Model?.Trim() ?? "";
        if (normalizedProvider.Length is < 1 or > 64 || normalizedProvider.Any(char.IsControl))
            throw new ArgumentException("provider is invalid.");
        if (model.Length is < 1 or > 128 || model.Any(char.IsControl))
            throw new ArgumentException("model is invalid.");
        if (!Uri.TryCreate(request.ApiUrl, UriKind.Absolute, out var apiUrl) || apiUrl.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(apiUrl.Host) || !string.IsNullOrEmpty(apiUrl.UserInfo)
            || !string.IsNullOrEmpty(apiUrl.Query) || !string.IsNullOrEmpty(apiUrl.Fragment)
            || apiUrl.ToString().Length > 2048)
            throw new ArgumentException("apiUrl must be an absolute HTTP or HTTPS URL.");
        if (request.Priority < 0 || request.TimeoutSeconds is < 5 or > 600) throw new ArgumentException("Provider limits are invalid.");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var credential = string.IsNullOrWhiteSpace(request.Credential) ? null : request.Credential.Trim();
        var hint = string.IsNullOrWhiteSpace(request.Credential) ? null : Mask(request.Credential.Trim());
        await Exec(connection, null, """
            INSERT INTO research_provider_settings
                (provider, model, api_url, credential, credential_hint, enabled, priority,
                 web_search_enabled, timeout_seconds, created_at, updated_at)
            VALUES (@provider, @model, @url, @credential, @hint, @enabled, @priority, @search, @timeout,
                    UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE model = VALUES(model), api_url = VALUES(api_url),
                credential = COALESCE(VALUES(credential), credential),
                credential_hint = COALESCE(VALUES(credential_hint), credential_hint), enabled = VALUES(enabled),
                priority = VALUES(priority), web_search_enabled = VALUES(web_search_enabled),
                timeout_seconds = VALUES(timeout_seconds), updated_at = UTC_TIMESTAMP(6);
            """, cancellationToken, ("@provider", normalizedProvider), ("@model", model),
            ("@url", apiUrl.ToString().TrimEnd('/')), ("@credential", credential), ("@hint", hint),
            ("@enabled", request.Enabled), ("@priority", request.Priority), ("@search", request.WebSearchEnabled),
            ("@timeout", request.TimeoutSeconds));
    }

    private static StartBrandResearchRequest NormalizeRequest(StartBrandResearchRequest request) => Normalize(request, hasKnowledge: false);

    private static StartBrandResearchRequest Normalize(StartBrandResearchRequest request, bool hasKnowledge)
    {
        string? website = null;
        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            if (!Uri.TryCreate(request.Website.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !IsPublicWebsiteHost(uri.DnsSafeHost))
                throw new ArgumentException("官网必须是公开 HTTP(S) 地址。");
            website = BrandWebsiteCollector.NormalizeFetchUrl(uri.ToString());
        }
        else if (string.IsNullOrWhiteSpace(request.BusinessDescription) && !hasKnowledge)
            throw new ArgumentException("无官网时请提供品牌业务介绍，或先在品牌信息中补充知识。");
        if (request.MaxPrompts is < 1 or > 30 || request.MaxCompetitors is < 0 or > 10)
            throw new ArgumentException("research limits are invalid.");
        if (request.BrandName?.Length > 200 || request.BusinessDescription?.Length > 4000
            || request.ExcludedQuestions?.Count > 300 || request.ExcludedQuestions?.Sum(p => p?.Length ?? 0) > 60000 || request.ExcludedQuestions?.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 10000) == true
            || request.Feedback?.Count > 50 || request.Feedback?.Sum(f => f?.Prompt?.Length ?? 0) > 10000 || request.Feedback?.Any(f => f is null || string.IsNullOrWhiteSpace(f.Prompt) || f.Prompt.Length > 10000
                || f.Reason is not ("off_topic" or "generic" or "duplicate" or "more_like_this")) == true
            || string.IsNullOrWhiteSpace(request.Market) || request.Market.Length > 50
            || string.IsNullOrWhiteSpace(request.Language) || request.Language.Length > 35)
            throw new ArgumentException("业务资料、反馈或地区语言设置不符合要求。");
        return request with
        {
            Website = website,
            BusinessDescription = string.IsNullOrWhiteSpace(request.BusinessDescription) ? null : request.BusinessDescription.Trim(),
            Market = request.Market.Trim(), Language = request.Language.Trim(),
            BrandName = string.IsNullOrWhiteSpace(request.BrandName) ? null : request.BrandName.Trim(),
            RefreshKey = string.IsNullOrWhiteSpace(request.RefreshKey) ? null : request.RefreshKey.Trim()
        };
    }

    private static bool IsPublicWebsiteHost(string value)
    {
        var host = value.TrimEnd('.').ToLowerInvariant();
        if (host is "localhost" or "0.0.0.0" || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal)
            || host.EndsWith(".intranet", StringComparison.Ordinal)
            || host.EndsWith(".invalid", StringComparison.Ordinal)) return false;
        if (!IPAddress.TryParse(host, out var address)) return true;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return false;
        var bytes = address.GetAddressBytes();
        return bytes.Length != 4 || bytes[0] is not (0 or 10 or 127)
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168);
    }

    private sealed record StoredResult(ResearchContext? Research, BrandResearchSuggestion? Suggestion);
    private static StoredResult? DeserializeResult(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        using var document = JsonDocument.Parse(value);
        return document.RootElement.TryGetProperty("research", out _)
            ? JsonSerializer.Deserialize<StoredResult>(value, JsonOptions)
            : new StoredResult(null, JsonSerializer.Deserialize<BrandResearchSuggestion>(value, JsonOptions));
    }
    private static BrandResearchJobStatus ParseStatus(string value) => Enum.TryParse<BrandResearchJobStatus>(value, true, out var result) ? result : BrandResearchJobStatus.Failed;
    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);
    private static bool Bool(string value) => value is "1" or "true" or "True";
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static DateTimeOffset? NullableUtc(string value) => string.IsNullOrWhiteSpace(value) ? null : Utc(value);
    private static string Mask(string value) => value.Length <= 4 ? "****" : $"****{value[^4..]}";

    private static async Task<int> Exec(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string[]>> Rows(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<string[]?> Row(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        => (await Rows(connection, transaction, sql, cancellationToken, parameters)).FirstOrDefault();
}
