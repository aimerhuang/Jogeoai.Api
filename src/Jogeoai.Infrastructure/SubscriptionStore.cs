using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 套餐目录、租户订阅、卡密与周期额度的 MySQL 实现。订阅是租户的单行状态（原地更新 + 事件表）；
/// 周期清零 / 发放、开通发放都在同一事务内直接写 <c>credit_accounts</c> / <c>credit_ledger</c>，
/// 账本引用确定且唯一（<c>plan-activate:{订阅}:{事件}</c>、<c>plan-period:{订阅}:{开通时间}:{周期}</c>、<c>plan-expire:{订阅}:{开通时间}:{周期}</c>），重复执行不重复入账。
/// 锁顺序：<c>plan_codes</c> → <c>tenant_subscriptions</c> → <c>credit_accounts</c>。
/// </summary>
public sealed class SubscriptionStore(MySqlDatabase database, IOptions<SubscriptionOptions> options) : ISubscriptionStore
{
    private const string FreeTrial = "free_trial";
    private const int MaxMonths = 36;
    private const int MaxBatchQuantity = 500;
    private const int RecentEventCount = 50;

    private const string PlanColumns = """
        code, name, tagline, sort_order, recommended, max_brands, max_members, max_prompts, max_competitors_per_brand,
        signup_credits, monthly_credits, daily_monitoring_limit, engine_count,
        feature_domain_analysis, feature_prompt_recommendation, feature_article_recommendation, feature_geo_agent, enabled
        """;

    private const string SubscriptionColumns = """
        BIN_TO_UUID(id), BIN_TO_UUID(tenant_id), plan_code, source, started_at, expires_at, period_index,
        period_started_at, period_ends_at, period_quota, period_captured_baseline, version
        """;

    private const string CodeColumns = """
        BIN_TO_UUID(id), BIN_TO_UUID(batch_id), plan_code, months, code_hint, status, expires_at,
        BIN_TO_UUID(redeemed_tenant_id), BIN_TO_UUID(redeemed_by), redeemed_at, revoked_at, created_at
        """;

    private readonly RedeemFailureLimiter limiter = new(TimeSpan.FromHours(1));

    private sealed record PlanRow(string Code, string Name, string Tagline, int SortOrder, bool Recommended, int? MaxBrands,
        int? MaxMembers, int? MaxPrompts, int? MaxCompetitors, decimal SignupCredits, decimal MonthlyCredits, int DailyMonitoringLimit,
        int EngineCount, bool DomainAnalysis, bool PromptRecommendation, bool ArticleRecommendation, bool GeoAgent, bool Enabled)
    {
        public SubscriptionPlanView ToView(bool isCurrent) => new(Code, Name, Tagline, SortOrder, Recommended,
            new PlanLimitsView(MaxBrands, MaxMembers, MaxPrompts, MaxCompetitors), SignupCredits, MonthlyCredits, DailyMonitoringLimit,
            EngineCount, new PlanFeaturesView(DomainAnalysis, PromptRecommendation, ArticleRecommendation, GeoAgent), Enabled, isCurrent);
    }

    private sealed record SubscriptionRow(Guid Id, Guid TenantId, string PlanCode, string Source, DateTime StartedAt, DateTime? ExpiresAt,
        int PeriodIndex, DateTime? PeriodStartedAt, DateTime? PeriodEndsAt, decimal PeriodQuota, decimal Baseline, long Version)
    {
        public bool IsExpired(DateTime now) => ExpiresAt is { } expires && expires <= now;
        public string EffectivePlanCode(DateTime now) => IsExpired(now) ? FreeTrial : PlanCode;
    }

    private sealed record AccountRow(decimal Available, decimal Reserved, decimal LifetimeGranted, decimal LifetimeCaptured);

    private sealed record CodeCursor(long CreatedTicks, Guid Id);

    /// <summary>以 anchor 为锚点加 months 个自然月；目标月没有对应日期时取月末，时间部分保持不变。</summary>
    /// <param name="anchor">锚点时间。</param>
    /// <param name="months">月数，可为负。</param>
    public static DateTime AddMonthsClamped(DateTime anchor, int months)
    {
        var totalMonths = anchor.Year * 12 + (anchor.Month - 1) + months;
        var year = Math.DivRem(totalMonths, 12, out var monthIndex);
        var month = monthIndex + 1;
        var day = Math.Min(anchor.Day, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day, 0, 0, 0, anchor.Kind).Add(anchor.TimeOfDay);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubscriptionPlanView>> ListPlansAsync(Guid? tenantId, CancellationToken cancellationToken = default)
        => Translate(async () =>
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var plans = await ReadPlansAsync(connection, null, cancellationToken);
            string? current = null;
            if (tenantId is { } tenant)
            {
                var row = await Row(connection, null, """
                    SELECT IF(expires_at IS NOT NULL AND expires_at <= UTC_TIMESTAMP(6), 'free_trial', plan_code)
                    FROM tenant_subscriptions WHERE tenant_id = UUID_TO_BIN(@tenant);
                    """, cancellationToken, ("@tenant", tenant.ToString()));
                current = row is null ? null : S(row[0]);
            }
            return (IReadOnlyList<SubscriptionPlanView>)plans.Select(plan => plan.ToView(plan.Code == current)).ToArray();
        });

    /// <inheritdoc />
    public Task<TenantSubscriptionView> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => Translate(async () =>
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var view = await BuildViewAsync(connection, transaction, tenantId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return view;
        });

    /// <inheritdoc />
    public async Task<TenantSubscriptionView> RedeemAsync(Guid tenantId, Guid userId, string code, string clientIp,
        CancellationToken cancellationToken = default)
    {
        var tenantKey = "tenant:" + tenantId.ToString("N");
        var ipKey = "ip:" + (string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp.Trim());
        var now = DateTimeOffset.UtcNow;
        var maxFailures = Math.Max(1, options.Value.RedeemFailuresPerHour);
        if (limiter.IsLimited(tenantKey, maxFailures, now) || limiter.IsLimited(ipKey, maxFailures, now))
            throw new CollectorStoreException("PLAN_REDEEM_RATE_LIMITED", "卡密错误次数过多，请一小时后再试。", 429);
        try
        {
            if (!PlanCodeFormat.TryNormalize(code, out var normalized)) throw CodeInvalid();
            var sha = PlanCodeFormat.Hash(normalized);
            await Translate(async () =>
            {
                await using var connection = database.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                var utcNow = DateTime.UtcNow;
                var codeRow = await Row(connection, transaction, """
                    SELECT BIN_TO_UUID(id), plan_code, months, status, expires_at FROM plan_codes WHERE code_sha256 = @sha FOR UPDATE;
                    """, cancellationToken, ("@sha", sha));
                if (codeRow is null) throw CodeInvalid();
                var status = S(codeRow[3]);
                if (status == "redeemed") throw new CollectorStoreException("PLAN_CODE_USED", "该卡密已被使用。", 409);
                if (status == "revoked") throw new CollectorStoreException("PLAN_CODE_REVOKED", "该卡密已作废。", 409);
                if (NT(codeRow[4]) is { } codeExpiry && codeExpiry <= utcNow)
                    throw new CollectorStoreException("PLAN_CODE_EXPIRED", "该卡密已过期。", 409);
                var codeId = G(codeRow[0]);
                var subscription = await ReadSubscriptionAsync(connection, transaction, tenantId, true, cancellationToken)
                    ?? throw SubscriptionMissing();
                var account = await LockAccountAsync(connection, transaction, tenantId, cancellationToken);
                var target = await ReadPlanAsync(connection, transaction, S(codeRow[1]), cancellationToken);
                if (target is null || !target.Enabled) throw PlanNotFound();
                await ApplyPlanChangeAsync(connection, transaction, subscription, account, target, I(codeRow[2]), null, false,
                    SubscriptionEventType.Redeem, userId, codeId, null, utcNow, cancellationToken);
                await Exec(connection, transaction, """
                    UPDATE plan_codes SET status = 'redeemed', redeemed_tenant_id = UUID_TO_BIN(@tenant), redeemed_by = UUID_TO_BIN(@user),
                        redeemed_at = @now WHERE id = UUID_TO_BIN(@id) AND status = 'unused';
                    """, cancellationToken, ("@tenant", tenantId.ToString()), ("@user", userId.ToString()), ("@now", utcNow),
                    ("@id", codeId.ToString()));
                await transaction.CommitAsync(cancellationToken);
                return true;
            });
        }
        catch (CollectorStoreException exception) when (exception.Code is "PLAN_CODE_INVALID" or "PLAN_CODE_USED" or "PLAN_CODE_REVOKED" or "PLAN_CODE_EXPIRED")
        {
            limiter.RecordFailure(tenantKey, now);
            limiter.RecordFailure(ipKey, now);
            throw;
        }
        return await GetAsync(tenantId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TenantSubscriptionView> AssignAsync(Guid tenantId, Guid actorUserId, AssignSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        var planCode = NormalizePlanCode(request.PlanCode);
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length is < 1 or > 500) throw new CollectorStoreException("INVALID_REASON", "请填写 1–500 字符的指派原因。", 400);
        if (request.Months is not null && request.ExpiresAt is not null)
            throw new CollectorStoreException("INVALID_ASSIGNMENT", "months 与 expiresAt 只能提供一个。", 400);
        if (request.Months is { } months && months is < 1 or > MaxMonths)
            throw new CollectorStoreException("INVALID_ASSIGNMENT", $"months 必须在 1–{MaxMonths}。", 400);
        if (request.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            throw new CollectorStoreException("INVALID_ASSIGNMENT", "expiresAt 必须晚于当前时间。", 400);
        if (planCode != FreeTrial && request.Months is null && request.ExpiresAt is null)
            throw new CollectorStoreException("INVALID_ASSIGNMENT", "付费套餐必须提供 months 或 expiresAt。", 400);
        await Translate(async () =>
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var utcNow = DateTime.UtcNow;
            var subscription = await ReadSubscriptionAsync(connection, transaction, tenantId, true, cancellationToken)
                ?? throw SubscriptionMissing();
            var target = await ReadPlanAsync(connection, transaction, planCode, cancellationToken);
            if (target is null || !target.Enabled) throw PlanNotFound();
            var account = await LockAccountAsync(connection, transaction, tenantId, cancellationToken);
            await ApplyPlanChangeAsync(connection, transaction, subscription, account, target, request.Months ?? 0,
                request.ExpiresAt?.UtcDateTime, true, SubscriptionEventType.AdminAssign, actorUserId, null, reason, utcNow, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });
        return await GetAsync(tenantId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AdminTenantSubscriptionView> GetForAdminAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => Translate(async () =>
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var view = await BuildViewAsync(connection, transaction, tenantId, cancellationToken);
            var rows = await Rows(connection, transaction, """
                SELECT BIN_TO_UUID(id), event_type, from_plan_code, to_plan_code, BIN_TO_UUID(actor_user_id), BIN_TO_UUID(plan_code_id),
                       period_index, credits_expired, credits_granted, reason, created_at
                FROM subscription_events WHERE tenant_id = UUID_TO_BIN(@tenant) ORDER BY created_at DESC, id DESC LIMIT @limit;
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@limit", RecentEventCount));
            await transaction.CommitAsync(cancellationToken);
            var events = rows.Select(row => new SubscriptionEventView(G(row[0]), ParseEventType(S(row[1])), NS(row[2]), NS(row[3]),
                NG(row[4]), NG(row[5]), row[6] is null ? null : I(row[6]), ND(row[7]), ND(row[8]), NS(row[9]), Offset(T(row[10])))).ToArray();
            return new AdminTenantSubscriptionView(view, events);
        });

    /// <inheritdoc />
    public Task<SubscriptionPlanView> UpdatePlanAsync(string code, SubscriptionPlanUpdate update, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var planCode = NormalizePlanCode(code);
        var name = update.Name?.Trim();
        if (name is not null && name.Length is < 1 or > 64) throw new CollectorStoreException("INVALID_PLAN", "套餐名称长度必须为 1–64。", 400);
        var tagline = update.Tagline?.Trim();
        if (tagline is { Length: > 500 }) throw new CollectorStoreException("INVALID_PLAN", "定位文案最多 500 字符。", 400);
        foreach (var credits in new[] { update.SignupCredits, update.MonthlyCredits })
            if (credits is < 0 or > 100_000_000) throw new CollectorStoreException("INVALID_PLAN", "积分额度必须在 0–100,000,000。", 400);
        if (update.Limits is { } limits)
            foreach (var limit in new[] { limits.MaxBrands, limits.MaxMembers, limits.MaxPrompts, limits.MaxCompetitorsPerBrand })
                if (limit is < 0 or > 1_000_000) throw new CollectorStoreException("INVALID_PLAN", "上限必须为 null（不限）或 0–1,000,000。", 400);
        if (update.DailyMonitoringLimit is < 0 or > 1000 || update.EngineCount is < 0 or > 100)
            throw new CollectorStoreException("INVALID_PLAN", "展示属性超出范围。", 400);
        return Translate(async () =>
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var current = await ReadPlanAsync(connection, transaction, planCode, cancellationToken, true) ?? throw PlanNotFound();
            var next = current with
            {
                Name = name ?? current.Name,
                Tagline = tagline ?? current.Tagline,
                Recommended = update.Recommended ?? current.Recommended,
                Enabled = update.Enabled ?? current.Enabled,
                MaxBrands = update.Limits is null ? current.MaxBrands : update.Limits.MaxBrands,
                MaxMembers = update.Limits is null ? current.MaxMembers : update.Limits.MaxMembers,
                MaxPrompts = update.Limits is null ? current.MaxPrompts : update.Limits.MaxPrompts,
                MaxCompetitors = update.Limits is null ? current.MaxCompetitors : update.Limits.MaxCompetitorsPerBrand,
                SignupCredits = update.SignupCredits ?? current.SignupCredits,
                MonthlyCredits = update.MonthlyCredits ?? current.MonthlyCredits,
                DailyMonitoringLimit = update.DailyMonitoringLimit ?? current.DailyMonitoringLimit,
                EngineCount = update.EngineCount ?? current.EngineCount,
                DomainAnalysis = update.Features?.DomainAnalysis ?? current.DomainAnalysis,
                PromptRecommendation = update.Features?.PromptRecommendation ?? current.PromptRecommendation,
                ArticleRecommendation = update.Features?.ArticleRecommendation ?? current.ArticleRecommendation,
                GeoAgent = update.Features?.GeoAgent ?? current.GeoAgent,
            };
            if (next.Code == FreeTrial && !next.Enabled)
                throw new CollectorStoreException("INVALID_PLAN", "免费体验版不能停用。", 400);
            await Exec(connection, transaction, """
                UPDATE subscription_plans SET name = @name, tagline = @tagline, recommended = @recommended, enabled = @enabled,
                    max_brands = @brands, max_members = @members, max_prompts = @prompts, max_competitors_per_brand = @competitors,
                    signup_credits = @signup, monthly_credits = @monthly, daily_monitoring_limit = @daily, engine_count = @engines,
                    feature_domain_analysis = @domain, feature_prompt_recommendation = @prompt_rec,
                    feature_article_recommendation = @article_rec, feature_geo_agent = @geo, updated_at = UTC_TIMESTAMP(6)
                WHERE code = @code;
                """, cancellationToken, ("@name", next.Name), ("@tagline", next.Tagline), ("@recommended", next.Recommended),
                ("@enabled", next.Enabled), ("@brands", next.MaxBrands), ("@members", next.MaxMembers), ("@prompts", next.MaxPrompts),
                ("@competitors", next.MaxCompetitors), ("@signup", next.SignupCredits), ("@monthly", next.MonthlyCredits),
                ("@daily", next.DailyMonitoringLimit), ("@engines", next.EngineCount), ("@domain", next.DomainAnalysis),
                ("@prompt_rec", next.PromptRecommendation), ("@article_rec", next.ArticleRecommendation), ("@geo", next.GeoAgent),
                ("@code", planCode));
            await transaction.CommitAsync(cancellationToken);
            _ = actorUserId; // 目录级修改没有租户上下文，不写租户事件；修改时间由 updated_at 记录。
            return next.ToView(false);
        });
    }

    /// <inheritdoc />
    public Task<PlanCodeBatchResult> CreateCodeBatchAsync(PlanCodeBatchRequest request, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var planCode = NormalizePlanCode(request.PlanCode);
        if (planCode == FreeTrial) throw new CollectorStoreException("INVALID_PLAN_CODE_BATCH", "免费体验版不能生成卡密。", 400);
        if (request.Months is < 1 or > MaxMonths) throw new CollectorStoreException("INVALID_PLAN_CODE_BATCH", $"months 必须在 1–{MaxMonths}。", 400);
        if (request.Quantity is < 1 or > MaxBatchQuantity) throw new CollectorStoreException("INVALID_PLAN_CODE_BATCH", $"quantity 必须在 1–{MaxBatchQuantity}。", 400);
        if (request.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            throw new CollectorStoreException("INVALID_PLAN_CODE_BATCH", "expiresAt 必须晚于当前时间。", 400);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 200 }) throw new CollectorStoreException("INVALID_PLAN_CODE_BATCH", "备注最多 200 字符。", 400);
        return Translate(async () =>
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var plan = await ReadPlanAsync(connection, transaction, planCode, cancellationToken);
            if (plan is null || !plan.Enabled) throw PlanNotFound();
            var batchId = Guid.NewGuid();
            var createdAt = DateTime.UtcNow;
            await Exec(connection, transaction, """
                INSERT INTO plan_code_batches (id, plan_code, months, quantity, expires_at, note, created_by, created_at)
                VALUES (UUID_TO_BIN(@id), @plan, @months, @quantity, @expires, @note, UUID_TO_BIN(@actor), @now);
                """, cancellationToken, ("@id", batchId.ToString()), ("@plan", planCode), ("@months", request.Months),
                ("@quantity", request.Quantity), ("@expires", request.ExpiresAt?.UtcDateTime), ("@note", note),
                ("@actor", actorUserId.ToString()), ("@now", createdAt));
            var codes = new List<string>(request.Quantity);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (codes.Count < request.Quantity)
            {
                var plaintext = PlanCodeFormat.Generate();
                var sha = PlanCodeFormat.Hash(plaintext);
                if (!seen.Add(sha)) continue;
                try
                {
                    await Exec(connection, transaction, """
                        INSERT INTO plan_codes (id, batch_id, plan_code, months, code_sha256, code_hint, status, expires_at, created_at)
                        VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@batch), @plan, @months, @sha, @hint, 'unused', @expires, @now);
                        """, cancellationToken, ("@id", Guid.NewGuid().ToString()), ("@batch", batchId.ToString()), ("@plan", planCode),
                        ("@months", request.Months), ("@sha", sha), ("@hint", PlanCodeFormat.Hint(plaintext)),
                        ("@expires", request.ExpiresAt?.UtcDateTime), ("@now", createdAt));
                }
                catch (MySqlException exception) when (exception.Number == 1062)
                {
                    // 80 bit 随机碰撞几乎不可能；若与历史卡密重复则重新生成这一张。
                    continue;
                }
                codes.Add(plaintext);
            }
            await transaction.CommitAsync(cancellationToken);
            return new PlanCodeBatchResult(new PlanCodeBatchView(batchId, planCode, request.Months, request.Quantity,
                request.ExpiresAt?.ToUniversalTime(), note, actorUserId, Offset(createdAt)), codes);
        });
    }

    /// <inheritdoc />
    public Task<PlanCodePage> ListCodesAsync(Guid? batchId, string? status, string? planCode, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new CollectorStoreException("INVALID_LIMIT", "limit 必须在 1–100。", 400);
        var statusFilter = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (statusFilter is not (null or "unused" or "redeemed" or "revoked" or "expired"))
            throw new CollectorStoreException("INVALID_STATUS", "status 必须为 unused、redeemed、revoked 或 expired。", 400);
        var plan = string.IsNullOrWhiteSpace(planCode) ? null : NormalizePlanCode(planCode);
        var position = ParseCodeCursor(cursor);
        return Translate(async () =>
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var sql = new StringBuilder($"SELECT {CodeColumns} FROM plan_codes WHERE 1 = 1");
            var parameters = new List<(string, object?)> { ("@limit", limit + 1), ("@now", DateTime.UtcNow) };
            if (batchId is { } batch) { sql.Append(" AND batch_id = UUID_TO_BIN(@batch)"); parameters.Add(("@batch", batch.ToString())); }
            if (plan is not null) { sql.Append(" AND plan_code = @plan"); parameters.Add(("@plan", plan)); }
            switch (statusFilter)
            {
                case "expired": sql.Append(" AND status = 'unused' AND expires_at IS NOT NULL AND expires_at <= @now"); break;
                case "unused": sql.Append(" AND status = 'unused' AND (expires_at IS NULL OR expires_at > @now)"); break;
                case not null: sql.Append(" AND status = @status"); parameters.Add(("@status", statusFilter)); break;
            }
            if (position is not null)
            {
                sql.Append(" AND (created_at > @after OR (created_at = @after AND id > UUID_TO_BIN(@after_id)))");
                parameters.Add(("@after", new DateTime(position.CreatedTicks, DateTimeKind.Utc)));
                parameters.Add(("@after_id", position.Id.ToString()));
            }
            sql.Append(" ORDER BY created_at, id LIMIT @limit;");
            var rows = await Rows(connection, null, sql.ToString(), cancellationToken, parameters.ToArray());
            var now = DateTime.UtcNow;
            var items = rows.Take(limit).Select(row => ToCodeView(row, now)).ToArray();
            var next = rows.Count > limit && items.Length > 0
                ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new CodeCursor(T(rows[limit - 1][11]).Ticks, items[^1].CodeId)))
                : null;
            return new PlanCodePage(items, next);
        });
    }

    /// <inheritdoc />
    public Task<PlanCodeView> RevokeCodeAsync(Guid codeId, Guid actorUserId, CancellationToken cancellationToken = default)
        => Translate(async () =>
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var row = await Row(connection, transaction, $"SELECT {CodeColumns} FROM plan_codes WHERE id = UUID_TO_BIN(@id) FOR UPDATE;",
                cancellationToken, ("@id", codeId.ToString()))
                ?? throw new CollectorStoreException("RESOURCE_NOT_FOUND", "卡密不存在。", 404);
            var status = S(row[5]);
            if (status == "redeemed") throw new CollectorStoreException("PLAN_CODE_USED", "该卡密已被使用，不能作废。", 409);
            if (status == "unused")
            {
                await Exec(connection, transaction, "UPDATE plan_codes SET status = 'revoked', revoked_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id) AND status = 'unused';",
                    cancellationToken, ("@id", codeId.ToString()));
                row = await Row(connection, transaction, $"SELECT {CodeColumns} FROM plan_codes WHERE id = UUID_TO_BIN(@id);",
                    cancellationToken, ("@id", codeId.ToString()));
            }
            await transaction.CommitAsync(cancellationToken);
            _ = actorUserId;
            return ToCodeView(row!, DateTime.UtcNow);
        });

    /// <inheritdoc />
    public Task<int> RolloverDuePeriodsAsync(DateTime utcNow, int batchSize, CancellationToken cancellationToken = default)
        => Translate(async () =>
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var ids = await Rows(connection, null, """
                SELECT BIN_TO_UUID(id) FROM tenant_subscriptions
                WHERE period_index > 0 AND period_ends_at IS NOT NULL AND period_ends_at <= @now
                ORDER BY period_ends_at, id LIMIT @limit;
                """, cancellationToken, ("@now", utcNow), ("@limit", Math.Clamp(batchSize, 1, 10_000)));
            var processed = 0;
            foreach (var row in ids)
            {
                var id = G(row[0]);
                var advanced = false;
                // 长期停机时同一订阅可能欠多个周期；循环直到追平，每个周期单独一个事务。
                for (var step = 0; step < 240 && await RolloverOneAsync(connection, id, utcNow, cancellationToken); step++)
                    advanced = true;
                if (advanced) processed++;
            }
            return processed;
        });

    /// <inheritdoc />
    public Task<int> DowngradeExpiredAsync(DateTime utcNow, int batchSize, CancellationToken cancellationToken = default)
        => Translate(async () =>
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var ids = await Rows(connection, null, """
                SELECT BIN_TO_UUID(id) FROM tenant_subscriptions
                WHERE expires_at IS NOT NULL AND expires_at <= @now AND plan_code <> 'free_trial'
                ORDER BY expires_at, id LIMIT @limit;
                """, cancellationToken, ("@now", utcNow), ("@limit", Math.Clamp(batchSize, 1, 10_000)));
            var processed = 0;
            foreach (var row in ids)
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                var subscription = await ReadSubscriptionByIdAsync(connection, transaction, G(row[0]), cancellationToken);
                if (subscription is null || subscription.PlanCode == FreeTrial || !subscription.IsExpired(utcNow))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    continue;
                }
                await DowngradeRowAsync(connection, transaction, subscription, null, utcNow, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                processed++;
            }
            return processed;
        });

    private async Task<bool> RolloverOneAsync(MySqlConnection connection, Guid subscriptionId, DateTime utcNow, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var subscription = await ReadSubscriptionByIdAsync(connection, transaction, subscriptionId, cancellationToken);
        if (subscription is null || subscription.PeriodIndex == 0 || subscription.PeriodEndsAt is not { } periodEnd || periodEnd > utcNow)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        var account = await LockAccountAsync(connection, transaction, subscription.TenantId, cancellationToken);
        var expired = await ExpireAvailableAsync(connection, transaction, subscription, account, cancellationToken);
        if (subscription.IsExpired(utcNow))
        {
            await DowngradeRowAsync(connection, transaction, subscription, expired, utcNow, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        var plan = await ReadPlanAsync(connection, transaction, subscription.PlanCode, cancellationToken);
        var quota = plan?.MonthlyCredits ?? 0;
        var newIndex = subscription.PeriodIndex + 1;
        var newEnd = AddMonthsClamped(subscription.StartedAt, newIndex);
        if (newEnd <= periodEnd) newEnd = AddMonthsClamped(periodEnd, 1);
        await Exec(connection, transaction, """
            UPDATE tenant_subscriptions SET period_index = @index, period_started_at = @start, period_ends_at = @end,
                period_quota = @quota, period_captured_baseline = @baseline, version = version + 1, updated_at = @now
            WHERE id = UUID_TO_BIN(@id);
            """, cancellationToken, ("@index", newIndex), ("@start", periodEnd), ("@end", newEnd), ("@quota", quota),
            ("@baseline", account.LifetimeCaptured), ("@now", utcNow), ("@id", subscription.Id.ToString()));
        var granted = quota > 0
            ? await GrantAsync(connection, transaction, subscription.TenantId, quota, $"plan-period:{subscription.Id:N}:{CycleKey(subscription)}:{newIndex}", null,
                $"{plan!.Name} 第 {newIndex} 周期额度", cancellationToken)
            : 0;
        await InsertEventAsync(connection, transaction, Guid.NewGuid(), subscription.TenantId, subscription.Id,
            SubscriptionEventType.PeriodRollover, subscription.PlanCode, subscription.PlanCode, null, null, newIndex, expired, granted, null,
            utcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task ApplyPlanChangeAsync(MySqlConnection connection, MySqlTransaction transaction, SubscriptionRow subscription,
        AccountRow account, PlanRow target, int months, DateTime? explicitExpiresAt, bool allowDowngrade, SubscriptionEventType eventType,
        Guid? actorUserId, Guid? planCodeId, string? reason, DateTime now, CancellationToken cancellationToken)
    {
        var currentCode = subscription.EffectivePlanCode(now);
        var currentPlan = await ReadPlanAsync(connection, transaction, currentCode, cancellationToken);
        var currentSort = currentPlan?.SortOrder ?? -1;
        var eventId = Guid.NewGuid();
        var sourceName = eventType == SubscriptionEventType.Redeem ? "redeem" : "admin";
        if (target.Code == currentCode)
        {
            if (target.Code == FreeTrial)
            {
                // 「同为免费体验版」可能是行上仍是已到期、尚未被 Worker 降级的付费编码（按免费体验版生效）。
                // 必须把 plan_code 真正落到 free_trial 并清空周期；若只把 expires_at 置空，旧付费编码会永久生效。
                await Exec(connection, transaction, """
                    UPDATE tenant_subscriptions SET plan_code = 'free_trial', source = @source,
                        started_at = IF(plan_code = 'free_trial', started_at, @now), expires_at = NULL,
                        period_index = 0, period_started_at = NULL, period_ends_at = NULL, period_quota = 0,
                        period_captured_baseline = (SELECT COALESCE(ca.lifetime_captured, 0) FROM credit_accounts ca WHERE ca.tenant_id = tenant_subscriptions.tenant_id),
                        version = version + 1, updated_at = @now
                    WHERE id = UUID_TO_BIN(@id);
                    """, cancellationToken, ("@source", sourceName), ("@now", now), ("@id", subscription.Id.ToString()));
                await InsertEventAsync(connection, transaction, eventId, subscription.TenantId, subscription.Id, eventType, currentCode, target.Code,
                    actorUserId, planCodeId, null, null, null, reason, now, cancellationToken);
                return;
            }
            // 同套餐：顺延到期时间，不改周期、不发放。
            var newExpiry = explicitExpiresAt ?? AddMonthsClamped(subscription.ExpiresAt is { } current && current > now ? current : now, months);
            await Exec(connection, transaction, """
                UPDATE tenant_subscriptions SET expires_at = @expires, source = @source, version = version + 1, updated_at = @now
                WHERE id = UUID_TO_BIN(@id);
                """, cancellationToken, ("@expires", newExpiry), ("@source", sourceName), ("@now", now), ("@id", subscription.Id.ToString()));
            await InsertEventAsync(connection, transaction, eventId, subscription.TenantId, subscription.Id, eventType, currentCode, target.Code,
                actorUserId, planCodeId, subscription.PeriodIndex == 0 ? null : subscription.PeriodIndex, null, null, reason, now, cancellationToken);
            return;
        }
        if (target.SortOrder > currentSort)
        {
            // 升级：切换套餐、以现在为锚开启第 1 周期并立即发放额度；剩余可用积分不清零（父任务 FR-10）。
            DateTime? newExpiry = target.Code == FreeTrial ? null : explicitExpiresAt ?? AddMonthsClamped(now, months);
            var quota = target.MonthlyCredits;
            var hasPeriod = target.Code != FreeTrial;
            await Exec(connection, transaction, """
                UPDATE tenant_subscriptions SET plan_code = @plan, source = @source, started_at = @now, expires_at = @expires,
                    period_index = @index, period_started_at = @period_start, period_ends_at = @period_end, period_quota = @quota,
                    period_captured_baseline = @baseline, version = version + 1, updated_at = @now
                WHERE id = UUID_TO_BIN(@id);
                """, cancellationToken, ("@plan", target.Code), ("@source", sourceName), ("@now", now), ("@expires", newExpiry),
                ("@index", hasPeriod ? 1 : 0), ("@period_start", hasPeriod ? now : null), ("@period_end", hasPeriod ? AddMonthsClamped(now, 1) : null),
                ("@quota", hasPeriod ? quota : 0), ("@baseline", hasPeriod ? account.LifetimeCaptured : 0), ("@id", subscription.Id.ToString()));
            var granted = hasPeriod && quota > 0
                ? await GrantAsync(connection, transaction, subscription.TenantId, quota, $"plan-activate:{subscription.Id:N}:{eventId:N}",
                    actorUserId, $"开通 {target.Name} 发放第 1 周期额度", cancellationToken)
                : 0;
            await InsertEventAsync(connection, transaction, eventId, subscription.TenantId, subscription.Id, eventType, currentCode, target.Code,
                actorUserId, planCodeId, hasPeriod ? 1 : null, null, granted, reason, now, cancellationToken);
            return;
        }
        if (!allowDowngrade)
            throw new CollectorStoreException("PLAN_DOWNGRADE_NOT_ALLOWED", "卡密套餐低于当前有效的付费套餐，不能降级。", 409);
        // 管理员降级：立即生效，不清零、不发放；降到免费体验版时清空周期，降到更低付费套餐时保留当前周期与锚点。
        if (target.Code == FreeTrial)
            await Exec(connection, transaction, """
                UPDATE tenant_subscriptions SET plan_code = @plan, source = @source, started_at = @now, expires_at = NULL,
                    period_index = 0, period_started_at = NULL, period_ends_at = NULL, period_quota = 0,
                        period_captured_baseline = (SELECT COALESCE(ca.lifetime_captured, 0) FROM credit_accounts ca WHERE ca.tenant_id = tenant_subscriptions.tenant_id),
                    version = version + 1, updated_at = @now
                WHERE id = UUID_TO_BIN(@id);
                """, cancellationToken, ("@plan", target.Code), ("@source", sourceName), ("@now", now), ("@id", subscription.Id.ToString()));
        else
            await Exec(connection, transaction, """
                UPDATE tenant_subscriptions SET plan_code = @plan, source = @source, expires_at = @expires,
                    version = version + 1, updated_at = @now
                WHERE id = UUID_TO_BIN(@id);
                """, cancellationToken, ("@plan", target.Code), ("@source", sourceName),
                ("@expires", explicitExpiresAt ?? AddMonthsClamped(now, months)), ("@now", now), ("@id", subscription.Id.ToString()));
        await InsertEventAsync(connection, transaction, eventId, subscription.TenantId, subscription.Id, eventType, currentCode, target.Code,
            actorUserId, planCodeId, null, null, null, reason, now, cancellationToken);
    }

    private static async Task DowngradeRowAsync(MySqlConnection connection, MySqlTransaction transaction, SubscriptionRow subscription,
        decimal? creditsExpired, DateTime now, CancellationToken cancellationToken)
    {
        await Exec(connection, transaction, """
            UPDATE tenant_subscriptions SET plan_code = 'free_trial', source = 'expiry', started_at = @now, expires_at = NULL,
                period_index = 0, period_started_at = NULL, period_ends_at = NULL, period_quota = 0,
                        period_captured_baseline = (SELECT COALESCE(ca.lifetime_captured, 0) FROM credit_accounts ca WHERE ca.tenant_id = tenant_subscriptions.tenant_id),
                version = version + 1, updated_at = @now
            WHERE id = UUID_TO_BIN(@id);
            """, cancellationToken, ("@now", now), ("@id", subscription.Id.ToString()));
        await InsertEventAsync(connection, transaction, Guid.NewGuid(), subscription.TenantId, subscription.Id,
            SubscriptionEventType.ExpireDowngrade, subscription.PlanCode, FreeTrial, null, null,
            subscription.PeriodIndex == 0 ? null : subscription.PeriodIndex, creditsExpired, null, "付费套餐到期，自动降为免费体验版", now,
            cancellationToken);
    }

    /// <summary>
    /// 开通周期（cycle）标识：每次升级 / 切换套餐都把 started_at 置为当前时间并把 period_index 重置为 1，
    /// 因此账本引用必须带上 started_at，否则第二次开通的 period 2 / expire 1 会与上一轮的引用相同而被幂等检查跳过（不发放、不清零）。
    /// </summary>
    private static string CycleKey(SubscriptionRow subscription) =>
        subscription.StartedAt.ToString("yyyyMMddHHmmssffffff", CultureInfo.InvariantCulture);

    /// <summary>周期结束清零可用积分（预占不动）；引用 plan-expire:{subscriptionId}:{cycle}:{periodIndex} 保证幂等。返回清零金额。</summary>
    private static async Task<decimal> ExpireAvailableAsync(MySqlConnection connection, MySqlTransaction transaction,
        SubscriptionRow subscription, AccountRow account, CancellationToken cancellationToken)
    {
        var reference = $"plan-expire:{subscription.Id:N}:{CycleKey(subscription)}:{subscription.PeriodIndex}";
        if (account.Available <= 0 || await LedgerExistsAsync(connection, transaction, subscription.TenantId, reference, cancellationToken))
            return 0;
        await Exec(connection, transaction, """
            UPDATE credit_accounts SET available_credits = 0, version = version + 1, updated_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant);
            INSERT IGNORE INTO credit_ledger (id, tenant_id, entry_type, amount, balance_after, external_reference, reason, created_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), 'expire', @amount, 0, @reference, @reason, UTC_TIMESTAMP(6));
            """, cancellationToken, ("@tenant", subscription.TenantId.ToString()), ("@id", Guid.NewGuid().ToString()),
            ("@amount", -account.Available), ("@reference", reference), ("@reason", $"第 {subscription.PeriodIndex} 周期结束，清零可用积分"));
        return account.Available;
    }

    /// <summary>在已锁定的账户行上发放积分；引用已存在时不重复入账，返回实际发放金额。</summary>
    private static async Task<decimal> GrantAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, decimal amount,
        string reference, Guid? actorUserId, string reason, CancellationToken cancellationToken)
    {
        if (amount <= 0 || await LedgerExistsAsync(connection, transaction, tenantId, reference, cancellationToken)) return 0;
        await Exec(connection, transaction, """
            UPDATE credit_accounts SET available_credits = available_credits + @amount, lifetime_granted = lifetime_granted + @amount,
                version = version + 1, updated_at = UTC_TIMESTAMP(6) WHERE tenant_id = UUID_TO_BIN(@tenant);
            INSERT IGNORE INTO credit_ledger (id, tenant_id, entry_type, amount, balance_after, actor_user_id, external_reference, reason, created_at)
            SELECT UUID_TO_BIN(@id), tenant_id, 'grant', @amount, available_credits, UUID_TO_BIN(@actor), @reference, @reason, UTC_TIMESTAMP(6)
            FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@amount", amount), ("@tenant", tenantId.ToString()), ("@id", Guid.NewGuid().ToString()),
            ("@actor", actorUserId?.ToString()), ("@reference", reference), ("@reason", reason));
        return amount;
    }

    private static async Task<bool> LedgerExistsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, string reference,
        CancellationToken cancellationToken)
        => await Row(connection, transaction, "SELECT 1 FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant) AND external_reference = @reference;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@reference", reference)) is not null;

    private static async Task<AccountRow> LockAccountAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        CancellationToken cancellationToken)
    {
        await Exec(connection, transaction, "INSERT IGNORE INTO credit_accounts (tenant_id, updated_at) VALUES (UUID_TO_BIN(@tenant), UTC_TIMESTAMP(6));",
            cancellationToken, ("@tenant", tenantId.ToString()));
        var row = await Row(connection, transaction, """
            SELECT available_credits, reserved_credits, lifetime_granted, lifetime_captured FROM credit_accounts
            WHERE tenant_id = UUID_TO_BIN(@tenant) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        return row is null ? new AccountRow(0, 0, 0, 0) : new AccountRow(D(row[0]), D(row[1]), D(row[2]), D(row[3]));
    }

    private static async Task InsertEventAsync(MySqlConnection connection, MySqlTransaction transaction, Guid eventId, Guid tenantId,
        Guid subscriptionId, SubscriptionEventType type, string? fromPlan, string? toPlan, Guid? actorUserId, Guid? planCodeId, int? periodIndex,
        decimal? creditsExpired, decimal? creditsGranted, string? reason, DateTime now, CancellationToken cancellationToken)
        => await Exec(connection, transaction, """
            INSERT INTO subscription_events (id, tenant_id, subscription_id, event_type, from_plan_code, to_plan_code, actor_user_id, plan_code_id,
                period_index, credits_expired, credits_granted, reason, created_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@subscription), @type, @from, @to, UUID_TO_BIN(@actor), UUID_TO_BIN(@code),
                @period, @expired, @granted, @reason, @now);
            """, cancellationToken, ("@id", eventId.ToString()), ("@tenant", tenantId.ToString()), ("@subscription", subscriptionId.ToString()),
            ("@type", EventTypeName(type)), ("@from", fromPlan), ("@to", toPlan), ("@actor", actorUserId?.ToString()),
            ("@code", planCodeId?.ToString()), ("@period", periodIndex), ("@expired", creditsExpired), ("@granted", creditsGranted),
            ("@reason", reason is { Length: > 500 } ? reason[..500] : reason), ("@now", now));

    private async Task<TenantSubscriptionView> BuildViewAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var subscription = await ReadSubscriptionAsync(connection, transaction, tenantId, false, cancellationToken) ?? throw SubscriptionMissing();
        var effectiveCode = subscription.EffectivePlanCode(now);
        var plan = await ReadPlanAsync(connection, transaction, effectiveCode, cancellationToken) ?? throw PlanNotFound();
        var accountRow = await Row(connection, transaction, """
            SELECT available_credits, reserved_credits, lifetime_granted, lifetime_captured FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        var account = accountRow is null ? new AccountRow(0, 0, 0, 0) : new AccountRow(D(accountRow[0]), D(accountRow[1]), D(accountRow[2]), D(accountRow[3]));
        var counts = await Row(connection, transaction, """
            SELECT (SELECT COUNT(*) FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant) AND status = 'active'),
                   (SELECT COUNT(*) FROM tenant_members WHERE tenant_id = UUID_TO_BIN(@tenant) AND status IN ('active','invited')),
                   (SELECT COUNT(*) FROM prompts p JOIN sites s ON s.id = p.site_id
                    WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.status = 'active' AND p.enabled = TRUE AND p.archived_at IS NULL);
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        var competitorRows = await Rows(connection, transaction, """
            SELECT BIN_TO_UUID(s.id), s.display_name,
                   (SELECT COUNT(*) FROM competitors c WHERE c.tenant_id = s.tenant_id AND c.site_id = s.id AND c.status = 'active')
            FROM sites s WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.status = 'active' ORDER BY s.created_at, s.id;
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        var competitors = competitorRows.Select(row => new BrandCompetitorUsage(G(row[0]), S(row[1]), I(row[2]), plan.MaxCompetitors)).ToArray();
        var usage = new PlanUsageView(new UsageItem(I(counts![0]), plan.MaxBrands), new UsageItem(I(counts[1]), plan.MaxMembers),
            new UsageItem(I(counts[2]), plan.MaxPrompts), competitors, plan.SignupCredits, Math.Max(0, account.LifetimeCaptured - subscription.Baseline), account.Available, account.Reserved);
        var expired = subscription.IsExpired(now);
        SubscriptionPeriodView? period = !expired && effectiveCode != FreeTrial && subscription.PeriodIndex > 0
            && subscription.PeriodStartedAt is { } periodStart && subscription.PeriodEndsAt is { } periodEnd
            ? new SubscriptionPeriodView(subscription.PeriodIndex, Offset(periodStart), Offset(periodEnd), subscription.PeriodQuota,
                Math.Max(0, account.LifetimeCaptured - subscription.Baseline), account.Available, account.Reserved)
            : null;
        return new TenantSubscriptionView(subscription.Id, tenantId, plan.ToView(true), ParseSource(subscription.Source),
            Offset(subscription.StartedAt), expired ? null : subscription.ExpiresAt is { } expiresAt ? Offset(expiresAt) : null, period, usage);
    }

    private static async Task<SubscriptionRow?> ReadSubscriptionAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId,
        bool forUpdate, CancellationToken cancellationToken)
    {
        var row = await Row(connection, transaction,
            $"SELECT {SubscriptionColumns} FROM tenant_subscriptions WHERE tenant_id = UUID_TO_BIN(@tenant){(forUpdate ? " FOR UPDATE" : "")};",
            cancellationToken, ("@tenant", tenantId.ToString()));
        return row is null ? null : ToSubscription(row);
    }

    private static async Task<SubscriptionRow?> ReadSubscriptionByIdAsync(MySqlConnection connection, MySqlTransaction transaction, Guid id,
        CancellationToken cancellationToken)
    {
        var row = await Row(connection, transaction, $"SELECT {SubscriptionColumns} FROM tenant_subscriptions WHERE id = UUID_TO_BIN(@id) FOR UPDATE;",
            cancellationToken, ("@id", id.ToString()));
        return row is null ? null : ToSubscription(row);
    }

    private static SubscriptionRow ToSubscription(object?[] row) => new(G(row[0]), G(row[1]), S(row[2]), S(row[3]), T(row[4]), NT(row[5]),
        I(row[6]), NT(row[7]), NT(row[8]), D(row[9]), D(row[10]), Convert.ToInt64(row[11], CultureInfo.InvariantCulture));

    private static async Task<List<PlanRow>> ReadPlansAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken cancellationToken)
        => (await Rows(connection, transaction, $"SELECT {PlanColumns} FROM subscription_plans ORDER BY sort_order, code;", cancellationToken))
            .Select(ToPlan).ToList();

    private static async Task<PlanRow?> ReadPlanAsync(MySqlConnection connection, MySqlTransaction? transaction, string code,
        CancellationToken cancellationToken, bool forUpdate = false)
    {
        var row = await Row(connection, transaction, $"SELECT {PlanColumns} FROM subscription_plans WHERE code = @code{(forUpdate ? " FOR UPDATE" : "")};",
            cancellationToken, ("@code", code));
        return row is null ? null : ToPlan(row);
    }

    private static PlanRow ToPlan(object?[] row) => new(S(row[0]), S(row[1]), S(row[2]), I(row[3]), B(row[4]), NI(row[5]), NI(row[6]), NI(row[7]),
        NI(row[8]), D(row[9]), D(row[10]), I(row[11]), I(row[12]), B(row[13]), B(row[14]), B(row[15]), B(row[16]), B(row[17]));

    private static PlanCodeView ToCodeView(object?[] row, DateTime now)
    {
        var status = S(row[5]) switch
        {
            "redeemed" => PlanCodeStatus.Redeemed,
            "revoked" => PlanCodeStatus.Revoked,
            _ => NT(row[6]) is { } expires && expires <= now ? PlanCodeStatus.Expired : PlanCodeStatus.Unused,
        };
        return new PlanCodeView(G(row[0]), G(row[1]), S(row[2]), I(row[3]), S(row[4]), status, NT(row[6]) is { } e ? Offset(e) : null,
            NG(row[7]), NG(row[8]), NT(row[9]) is { } r ? Offset(r) : null, NT(row[10]) is { } v ? Offset(v) : null, Offset(T(row[11])));
    }

    private static CodeCursor? ParseCodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            if (cursor.Length > 1024) throw new FormatException();
            var parsed = JsonSerializer.Deserialize<CodeCursor>(Convert.FromBase64String(cursor));
            if (parsed is null || parsed.CreatedTicks < 0 || parsed.CreatedTicks > DateTime.MaxValue.Ticks || parsed.Id == Guid.Empty)
                throw new FormatException();
            return parsed;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new CollectorStoreException("INVALID_CURSOR", "卡密列表游标无效。", 400);
        }
    }

    private static string NormalizePlanCode(string? value)
    {
        var code = value?.Trim().ToLowerInvariant() ?? "";
        if (code.Length is < 1 or > 32 || code.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')))
            throw new CollectorStoreException("PLAN_NOT_FOUND", "套餐不存在。", 404);
        return code;
    }

    private static CollectorStoreException CodeInvalid() => new("PLAN_CODE_INVALID", "卡密无效。", 404);
    private static CollectorStoreException PlanNotFound() => new("PLAN_NOT_FOUND", "套餐不存在或已停用。", 404);
    private static CollectorStoreException SubscriptionMissing() =>
        new("SUBSCRIPTION_NOT_FOUND", "租户尚无订阅记录；请确认迁移 024 已执行并完成回填。", 404);

    private static string EventTypeName(SubscriptionEventType type) => type switch
    {
        SubscriptionEventType.Registration => "registration",
        SubscriptionEventType.Migration => "migration",
        SubscriptionEventType.Redeem => "redeem",
        SubscriptionEventType.AdminAssign => "admin_assign",
        SubscriptionEventType.ExpireDowngrade => "expire_downgrade",
        SubscriptionEventType.PeriodRollover => "period_rollover",
        SubscriptionEventType.PlanUpdated => "plan_updated",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static SubscriptionEventType ParseEventType(string value) => value switch
    {
        "registration" => SubscriptionEventType.Registration,
        "migration" => SubscriptionEventType.Migration,
        "redeem" => SubscriptionEventType.Redeem,
        "admin_assign" => SubscriptionEventType.AdminAssign,
        "expire_downgrade" => SubscriptionEventType.ExpireDowngrade,
        "period_rollover" => SubscriptionEventType.PeriodRollover,
        _ => SubscriptionEventType.PlanUpdated,
    };

    private static SubscriptionSource ParseSource(string value) => value switch
    {
        "registration" => SubscriptionSource.Registration,
        "redeem" => SubscriptionSource.Redeem,
        "admin" => SubscriptionSource.Admin,
        "expiry" => SubscriptionSource.Expiry,
        _ => SubscriptionSource.Migration,
    };

    private static async Task<T> Translate<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (MySqlException exception) when (SubscriptionLimitGuard.IsMissingSchema(exception))
        {
            throw SubscriptionLimitGuard.SchemaNotMigrated();
        }
    }

    private static string S(object? value) => value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    private static string? NS(object? value) => value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    private static int I(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static int? NI(object? value) => value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static decimal D(object? value) => value is null or DBNull ? 0 : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    private static decimal? ND(object? value) => value is null or DBNull ? null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    private static bool B(object? value) => value switch
    {
        bool flag => flag,
        null or DBNull => false,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
    };
    private static DateTime T(object? value) => DateTime.SpecifyKind((DateTime)value!, DateTimeKind.Utc);
    private static DateTime? NT(object? value) => value is null or DBNull ? null : T(value);
    private static Guid G(object? value) => Guid.Parse(S(value));
    private static Guid? NG(object? value) => value is null or DBNull ? null : Guid.Parse(S(value));
    private static DateTimeOffset Offset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static async Task<int> Exec(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<object?[]>> Rows(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result.Add(row);
        }
        return result;
    }

    private static async Task<object?[]?> Row(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        => (await Rows(connection, transaction, sql, cancellationToken, parameters)).FirstOrDefault();
}

/// <summary>卡密格式：JG-XXXX-XXXX-XXXX-XXXX，16 位去除易混淆字符（I / O / 0 / 1）的 base32 大写，80 bit 加密随机。</summary>
public static class PlanCodeFormat
{
    /// <summary>卡密字符表（32 个字符，不含 I、O、0、1）。</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>固定前缀。</summary>
    public const string Prefix = "JG";

    private const int BodyLength = 16;

    /// <summary>生成一张新的卡密明文。</summary>
    public static string Generate()
    {
        var body = new char[BodyLength];
        for (var i = 0; i < body.Length; i++) body[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return Format(new string(body));
    }

    /// <summary>规范化用户输入：去空白与连字符、转大写、前缀可省略；返回标准格式。</summary>
    /// <param name="input">用户输入。</param>
    /// <param name="normalized">标准格式的卡密。</param>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input) || input.Length > 64) return false;
        var compact = new string(input.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').ToArray()).ToUpperInvariant();
        if (compact.StartsWith(Prefix, StringComparison.Ordinal) && compact.Length == Prefix.Length + BodyLength)
            compact = compact[Prefix.Length..];
        if (compact.Length != BodyLength || compact.Any(c => !Alphabet.Contains(c))) return false;
        normalized = Format(compact);
        return true;
    }

    /// <summary>标准格式卡密的 SHA-256（小写十六进制）。</summary>
    /// <param name="normalizedCode">标准格式卡密。</param>
    public static string Hash(string normalizedCode) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode))).ToLowerInvariant();

    /// <summary>卡密末 4 位。</summary>
    /// <param name="normalizedCode">标准格式卡密。</param>
    public static string Hint(string normalizedCode) => normalizedCode[^4..];

    private static string Format(string body) => $"{Prefix}-{body[..4]}-{body[4..8]}-{body[8..12]}-{body[12..16]}";
}

/// <summary>进程内滑动窗口失败计数；只记录失败，用于卡密兑换按租户与 IP 限流。</summary>
internal sealed class RedeemFailureLimiter(TimeSpan window)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> failures = new(StringComparer.Ordinal);

    /// <summary>窗口内失败次数是否已达到上限。</summary>
    public bool IsLimited(string key, int maxFailures, DateTimeOffset now)
    {
        if (!failures.TryGetValue(key, out var queue)) return false;
        lock (queue)
        {
            Trim(queue, now);
            return queue.Count >= maxFailures;
        }
    }

    /// <summary>记录一次失败。</summary>
    public void RecordFailure(string key, DateTimeOffset now)
    {
        var queue = failures.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            Trim(queue, now);
            queue.Enqueue(now);
        }
        if (failures.Count > 10_000)
            foreach (var (otherKey, other) in failures)
            {
                lock (other) Trim(other, now);
                if (other.Count == 0) failures.TryRemove(otherKey, out _);
            }
    }

    private void Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && queue.Peek() <= now - window) queue.Dequeue();
    }
}
