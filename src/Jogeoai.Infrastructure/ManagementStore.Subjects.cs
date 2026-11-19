using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class ManagementStore
{
    private sealed record Term(string Kind, string Namespace, string Key, string Display);

    private static IReadOnlyList<Term> BrandTerms(string name, string? domain, IReadOnlyList<string> aliases, IReadOnlyList<string> additional)
    {
        var terms = new List<Term> { new("name", "text", NormalizeText(name), name.Trim()) };
        if (!string.IsNullOrWhiteSpace(domain))
            terms.Add(new("primary_domain", "domain", NormalizeDomain(domain), NormalizeDomain(domain)));
        terms.AddRange(aliases.Select(value => new Term("alias", "text", NormalizeText(value), value.Trim())));
        terms.AddRange(additional.Select(value => new Term("additional_domain", "domain", NormalizeDomain(value), NormalizeDomain(value))));
        return terms.DistinctBy(t => (t.Namespace, t.Key)).ToArray();
    }

    private static IReadOnlyList<Term> CompetitorTerms(string name, IReadOnlyList<string> aliases, IReadOnlyList<string> domains)
    {
        var terms = new List<Term> { new("name", "text", NormalizeText(name), name.Trim()) };
        terms.AddRange(aliases.Select(value => new Term("alias", "text", NormalizeText(value), value.Trim())));
        terms.AddRange(domains.Select(value => new Term("domain", "domain", NormalizeDomain(value), NormalizeDomain(value))));
        return terms.DistinctBy(t => (t.Namespace, t.Key)).ToArray();
    }

    private static bool DomainsOverlap(string a, string b) => a == b || a.EndsWith('.' + b, StringComparison.Ordinal)
        || b.EndsWith('.' + a, StringComparison.Ordinal);

    private static async Task ReplaceTerms(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid siteId,
        string subjectType, Guid subjectId, IReadOnlyList<Term> terms, CancellationToken cancellationToken)
    {
        var others = await Rows(connection, transaction, """
            SELECT subject_type, BIN_TO_UUID(subject_id), namespace_key, normalized_key FROM subject_terms
            WHERE site_id = UUID_TO_BIN(@site) AND NOT (subject_type = @type AND subject_id = UUID_TO_BIN(@subject)) FOR UPDATE;
            """, cancellationToken, ("@site", siteId.ToString()), ("@type", subjectType), ("@subject", subjectId.ToString()));
        foreach (var term in terms)
        foreach (var other in others)
        {
            if (term.Namespace != other[2]) continue;
            if (term.Namespace == "text" ? term.Key == other[3] : DomainsOverlap(term.Key, other[3]))
                throw Error("SUBJECT_TERM_CONFLICT", $"{term.Namespace} 值已归属其他主体。", 409);
        }
        await Exec(connection, transaction, "DELETE FROM subject_terms WHERE site_id = UUID_TO_BIN(@site) AND subject_type = @type AND subject_id = UUID_TO_BIN(@subject);",
            cancellationToken, ("@site", siteId.ToString()), ("@type", subjectType), ("@subject", subjectId.ToString()));
        foreach (var term in terms)
        {
            await Exec(connection, transaction, """
                INSERT INTO subject_terms (id, tenant_id, site_id, subject_type, subject_id, term_type, namespace_key,
                    normalized_key, display_value, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @type, UUID_TO_BIN(@subject),
                    @kind, @namespace, @key, @display, UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", Guid.NewGuid().ToString()), ("@tenant", tenantId.ToString()),
                ("@site", siteId.ToString()), ("@type", subjectType), ("@subject", subjectId.ToString()),
                ("@kind", term.Kind), ("@namespace", term.Namespace), ("@key", term.Key), ("@display", term.Display));
        }
    }

    private static async Task<(string[] Aliases, string[] Domains)> ReadTerms(MySqlConnection connection, MySqlTransaction? transaction,
        Guid siteId, string type, Guid subjectId, CancellationToken cancellationToken)
    {
        var terms = await Rows(connection, transaction, """
            SELECT term_type, display_value FROM subject_terms
            WHERE site_id = UUID_TO_BIN(@site) AND subject_type = @type AND subject_id = UUID_TO_BIN(@subject)
            ORDER BY term_type, normalized_key;
            """, cancellationToken, ("@site", siteId.ToString()), ("@type", type), ("@subject", subjectId.ToString()));
        return (terms.Where(t => t[0] == "alias").Select(t => t[1]).ToArray(),
            terms.Where(t => t[0] is "additional_domain" or "domain").Select(t => t[1]).ToArray());
    }

    public async Task<BrandView> CreateBrandAsync(Guid tenantId, BrandWrite request, CancellationToken cancellationToken = default)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 200) throw Error("INVALID_NAME", "品牌名称长度必须为 1–200。", 400);
        var host = NormalizeOptionalDomain(request.PrimaryDomain);
        var country = ValidateCountry(request.DefaultCountry);
        var locale = ValidateLocale(request.DefaultLocale);
        var zone = ValidateTimeZone(request.TimeZone);
        var status = ValidateStatus(request.Status);
        var terms = BrandTerms(name, host, request.Aliases ?? [], request.AdditionalDomains ?? []);
        var siteId = request.SiteId ?? Guid.NewGuid();
        var reportTitle = string.IsNullOrWhiteSpace(request.ReportTitle) ? name : request.ReportTitle.Trim();
        var remark = ValidateDisplayText(request.Remark, 200, "remark");
        var description = ValidateDisplayText(request.Description, 2000, "description");
        if (request.SiteId is null && string.IsNullOrWhiteSpace(description))
            throw Error("VALIDATION_FAILED", "请填写品牌介绍（1–2000 字）。", 400);
        if (request.CreationRequestId == Guid.Empty)
            throw Error("VALIDATION_FAILED", "creationRequestId 必须是非空 UUID。", 400);
        var requestHash = Hash(System.Text.Json.JsonSerializer.Serialize(new
        {
            name, host, country, locale, zone, status, reportTitle,
            includeSubdomains = request.IncludeSubdomains ?? false,
            remark = string.IsNullOrEmpty(remark) ? null : remark, description,
            terms = terms.OrderBy(t => t.Kind, StringComparer.Ordinal).ThenBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => new { t.Kind, t.Namespace, t.Key, t.Display })
        }));
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 锁顺序：tenants -> tenant_onboarding -> sites。租户级锁让套餐品牌上限校验与写入串行化（设计 §5.2）。
        var tenant = await LockTenant(connection, transaction, tenantId, cancellationToken);
        if (tenant is null || tenant[0] != "active") throw Error("RESOURCE_NOT_FOUND", "租户不存在。", 404);
        // 在所有可变前置条件和名额检查之前重放，最后一个可用名额上的网络重试也能成功。
        if (request.SiteId is null && request.CreationRequestId is { } creationRequestId)
        {
            var replay = await Row(connection, transaction, """
                SELECT BIN_TO_UUID(id), creation_request_hash FROM sites
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND creation_request_id = UUID_TO_BIN(@request) FOR UPDATE;
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@request", creationRequestId.ToString()));
            if (replay is not null)
            {
                if (!string.Equals(replay[1], requestHash, StringComparison.Ordinal))
                    throw Error("BRAND_CREATE_IDEMPOTENCY_CONFLICT", "该创建请求标识已用于不同的品牌资料，请重新提交。", 409);
                await transaction.CommitAsync(cancellationToken);
                return (await GetBrandAsync(tenantId, Guid.Parse(replay[0]), cancellationToken))!;
            }
        }
        var setup = await Row(connection, transaction,
            "SELECT team_configured_at, completed_at FROM tenant_onboarding WHERE tenant_id = UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()));
        if (setup is not null && string.IsNullOrEmpty(setup[0]) && string.IsNullOrEmpty(setup[1]))
            throw Error("TEAM_REQUIRED", "请先确认团队名称。", 400);
        if (request.SiteId is null)
        {
            await SubscriptionLimitGuard.EnsureBrandAsync(connection, transaction, tenantId, status == "active" ? 1 : 0, cancellationToken);
            try
            {
                await Exec(connection, transaction, """
                    INSERT INTO sites (id, tenant_id, host, display_name, status, report_title, include_subdomains, remark, description,
                        creation_request_id, creation_request_hash, created_at, updated_at)
                    VALUES (UUID_TO_BIN(@site), UUID_TO_BIN(@tenant), @host, @name, @status, @report_title, @include_subdomains,
                            @remark, @description, UUID_TO_BIN(@request), @request_hash, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, cancellationToken, ("@site", siteId.ToString()), ("@tenant", tenantId.ToString()),
                    ("@host", host), ("@name", name), ("@status", status), ("@report_title", reportTitle),
                    ("@include_subdomains", request.IncludeSubdomains ?? false), ("@remark", remark), ("@description", description),
                    ("@request", request.CreationRequestId?.ToString()), ("@request_hash", request.CreationRequestId is null ? null : requestHash));
            }
            catch (MySqlException exception) when (exception.Number == 1062)
            { throw Error("PRIMARY_DOMAIN_CONFLICT", "该主域名已归属本租户的其他品牌。", 409); }
            await Exec(connection, transaction, """
                INSERT INTO site_brand_settings (site_id, tenant_id, default_country, default_locale, time_zone,
                    subject_config_version, created_at, updated_at)
                VALUES (UUID_TO_BIN(@site), UUID_TO_BIN(@tenant), @country, @locale, @zone, 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@site", siteId.ToString()), ("@tenant", tenantId.ToString()),
                ("@country", country), ("@locale", locale), ("@zone", zone));
            // Archived subjects retain their displayed terms and reserve their
            // ownership. Revision snapshots decide which terms are matchable.
            await ReplaceTerms(connection, transaction, tenantId, siteId, "brand", siteId, terms, cancellationToken);
            await Exec(connection, transaction, """
                INSERT INTO subject_config_revisions (site_id, tenant_id, version, snapshot, created_at)
                VALUES (UUID_TO_BIN(@site), UUID_TO_BIN(@tenant), 1, @snapshot, UTC_TIMESTAMP(6));
                """, cancellationToken, ("@site", siteId.ToString()), ("@tenant", tenantId.ToString()),
                ("@snapshot", System.Text.Json.JsonSerializer.Serialize(new { brandId = siteId, name, primaryDomain = host,
                    reportTitle, includeSubdomains = request.IncludeSubdomains ?? false,
                    brandStatus = status, competitors = Array.Empty<object>(),
                    terms = terms.Where(_ => status == "active").Select(t => new
                    { subjectType = "brand", subjectId = siteId, termType = t.Kind, key = t.Key }) })));
        }
        else
        {
            var existing = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
            if (existing[2] != "active") throw Error("RESOURCE_NOT_FOUND", "已有 Site 已归档。", 404);
            if (request.ExpectedVersion is null || request.ExpectedVersion.Value != uint.Parse(existing[6]))
                throw Error("CONFIG_VERSION_CONFLICT", "品牌配置版本已变化。", 409);
            await UpdateBrandRows(connection, transaction, tenantId, siteId, name, host, country, locale, zone, status,
                request.ReportTitle, request.IncludeSubdomains, remark, description, terms, cancellationToken);
        }
        await Exec(connection, transaction, """
            INSERT INTO onboarding_progress (tenant_id, site_id, current_step, completed, selected_prompt_limit, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'select_prompts', FALSE, 5, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE site_id = VALUES(site_id);
            UPDATE tenant_onboarding SET site_id = COALESCE(site_id, UUID_TO_BIN(@site))
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND completed_at IS NULL;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return (await GetBrandAsync(tenantId, siteId, cancellationToken))!;
    }

    private static async Task UpdateBrandRows(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid siteId,
        string name, string? host, string country, string locale, string zone, string status, string? reportTitle,
        bool? includeSubdomains, string? remark, string? description, IReadOnlyList<Term> terms,
        CancellationToken cancellationToken)
    {
        try
        {
            await Exec(connection, transaction, """
                UPDATE sites SET display_name = @name, host = @host, status = @status,
                    report_title = COALESCE(@report_title, report_title),
                    include_subdomains = COALESCE(@include_subdomains, include_subdomains),
                    remark = COALESCE(@remark, remark), description = COALESCE(@description, description),
                    updated_at = UTC_TIMESTAMP(6)
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@site);
                """, cancellationToken, ("@name", name), ("@host", host), ("@status", status),
                ("@report_title", string.IsNullOrWhiteSpace(reportTitle) ? null : reportTitle.Trim()),
                ("@include_subdomains", includeSubdomains), ("@remark", remark), ("@description", description),
                ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        { throw Error("PRIMARY_DOMAIN_CONFLICT", "该主域名已归属本租户的其他品牌。", 409); }
        await Exec(connection, transaction, """
            UPDATE site_brand_settings SET default_country = @country, default_locale = @locale, time_zone = @zone,
                updated_at = UTC_TIMESTAMP(6) WHERE site_id = UUID_TO_BIN(@site);
            """, cancellationToken, ("@country", country), ("@locale", locale), ("@zone", zone), ("@site", siteId.ToString()));
        await ReplaceTerms(connection, transaction, tenantId, siteId, "brand", siteId, terms, cancellationToken);
        if (status == "archived")
            await Exec(connection, transaction, """
                UPDATE collection_schedules SET enabled = FALSE, next_slot_at = NULL,
                    version = version + 1, updated_at = UTC_TIMESTAMP(6)
                WHERE site_id = UUID_TO_BIN(@site) AND enabled = TRUE;
                """,
                cancellationToken, ("@site", siteId.ToString()));
        await BumpRevision(connection, transaction, tenantId, siteId, cancellationToken);
    }

    public async Task<BrandView> UpdateBrandAsync(Guid tenantId, Guid siteId, BrandPatch request, CancellationToken cancellationToken = default)
    {
        if (request.ExpectedVersion is null) throw Error("CONFIG_VERSION_REQUIRED", "请提供 expectedVersion。", 400);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 归档 -> 启用会重新占用品牌名额，先取租户级锁再锁品牌。
        await LockTenant(connection, transaction, tenantId, cancellationToken);
        var current = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (request.ExpectedVersion.Value != uint.Parse(current[6])) throw Error("CONFIG_VERSION_CONFLICT", "品牌配置版本已变化。", 409);
        var remark = ValidateDisplayText(request.Remark, 200, "remark");
        var description = ValidateDisplayText(request.Description, 2000, "description");
        if (request.IsDisplayOnly)
        {
            // 备注、描述、报告标题只是展示字段：不进入主体配置快照，也不递增 subject_config_version。
            await UpdateDisplayFieldsAsync(connection, transaction, tenantId, siteId, request.ReportTitle, remark, description, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetBrandAsync(tenantId, siteId, cancellationToken))!;
        }
        var oldTerms = await ReadTerms(connection, transaction, siteId, "brand", siteId, cancellationToken);
        var name = request.Name?.Trim() ?? current[0];
        if (name.Length is < 1 or > 200) throw Error("INVALID_NAME", "品牌名称长度必须为 1–200。", 400);
        var host = NormalizeOptionalDomain(request.PrimaryDomain ?? current[1]);
        var aliases = request.Aliases ?? oldTerms.Aliases;
        var domains = request.AdditionalDomains ?? oldTerms.Domains;
        var country = ValidateCountry(request.DefaultCountry ?? current[3]);
        var locale = ValidateLocale(request.DefaultLocale ?? current[4]);
        var zone = ValidateTimeZone(request.TimeZone ?? current[5]);
        var status = ValidateStatus(request.Status ?? current[2]);
        if (status == "active" && current[2] == "archived")
            await SubscriptionLimitGuard.EnsureBrandAsync(connection, transaction, tenantId, 1, cancellationToken);
        await UpdateBrandRows(connection, transaction, tenantId, siteId, name, host, country, locale, zone, status,
            request.ReportTitle, request.IncludeSubdomains, remark, description, BrandTerms(name, host, aliases, domains), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetBrandAsync(tenantId, siteId, cancellationToken))!;
    }

    /// <summary>只更新 sites 的展示字段；空串清空，null 保留，不触发修订快照。</summary>
    private static Task UpdateDisplayFieldsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid siteId,
        string? reportTitle, string? remark, string? description, CancellationToken cancellationToken)
        => Exec(connection, transaction, """
            UPDATE sites SET report_title = COALESCE(@report_title, report_title),
                remark = COALESCE(@remark, remark), description = COALESCE(@description, description),
                updated_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@site);
            """, cancellationToken, ("@report_title", string.IsNullOrWhiteSpace(reportTitle) ? null : reportTitle.Trim()),
            ("@remark", remark), ("@description", description), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));

    /// <summary>展示文本：null 表示保留，空白归一为空串（清空），超长返回 400 VALIDATION_FAILED。</summary>
    private static string? ValidateDisplayText(string? value, int maxLength, string field)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength) throw Error("VALIDATION_FAILED", $"{field} 长度不能超过 {maxLength}。", 400);
        return trimmed;
    }

    public async Task<BrandView?> GetBrandAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var row = await Row(connection, null, """
            SELECT s.display_name, s.host, s.status, b.default_country, b.default_locale, b.time_zone,
                   b.subject_config_version, s.created_at, s.updated_at, b.target_config_version,
                   s.report_title, s.include_subdomains, s.remark, s.description
            FROM sites s JOIN site_brand_settings b ON b.site_id = s.id AND b.tenant_id = s.tenant_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.id = UUID_TO_BIN(@site);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        if (row is null) return null;
        var terms = await ReadTerms(connection, null, siteId, "brand", siteId, cancellationToken);
        return new BrandView(siteId, row[0], NormalizeOptionalDomain(row[1]), terms.Aliases, terms.Domains, row[3], row[4], row[5], row[2],
            uint.Parse(row[6]), Utc(row[7]), Utc(row[8]), uint.Parse(row[9]), row[10], Bool(row[11]),
            string.IsNullOrEmpty(row[12]) ? null : row[12], string.IsNullOrEmpty(row[13]) ? null : row[13]);
    }

    public async Task<CursorPage<BrandView>> ListBrandsAsync(Guid tenantId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var (ids, next) = await ListIdsAsync(connection, "brands", tenantId, null, cursor, limit, cancellationToken);
        var result = new List<BrandView>();
        foreach (var id in ids)
        {
            var brand = await GetBrandAsync(tenantId, id, cancellationToken);
            if (brand is not null) result.Add(brand);
        }
        return new CursorPage<BrandView>(result, next);
    }

    public async Task<CompetitorView> CreateCompetitorAsync(Guid tenantId, Guid siteId, CompetitorWrite request, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 200) throw Error("INVALID_NAME", "竞品名称长度必须为 1–200。", 400);
        var status = ValidateStatus(request.Status);
        var terms = CompetitorTerms(name, request.Aliases ?? [], request.Domains ?? []);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
        // 竞品按品牌计数，品牌行锁足以串行化本品牌的竞品写入。
        if (status == "active")
            await SubscriptionLimitGuard.EnsureCompetitorsAsync(connection, transaction, tenantId, siteId, 1, cancellationToken);
        await Exec(connection, transaction, """
            INSERT INTO competitors (id, tenant_id, site_id, name, status, archived_at, created_at, updated_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @name, @status,
                    IF(@status = 'archived', UTC_TIMESTAMP(6), NULL), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, cancellationToken, ("@id", id.ToString()), ("@tenant", tenantId.ToString()),
            ("@site", siteId.ToString()), ("@name", name), ("@status", status));
        await ReplaceTerms(connection, transaction, tenantId, siteId, "competitor", id, terms, cancellationToken);
        await BumpRevision(connection, transaction, tenantId, siteId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadCompetitor(tenantId, siteId, id, cancellationToken))!;
    }

    private async Task<CompetitorView?> ReadCompetitor(Guid tenantId, Guid siteId, Guid competitorId, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var row = await Row(connection, null, """
            SELECT c.name, c.status, c.created_at, c.updated_at FROM competitors c
            JOIN sites s ON s.id = c.site_id AND s.tenant_id = c.tenant_id
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site) AND c.id = UUID_TO_BIN(@id);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", competitorId.ToString()));
        if (row is null) return null;
        var terms = await ReadTerms(connection, null, siteId, "competitor", competitorId, cancellationToken);
        return new CompetitorView(competitorId, row[0], terms.Aliases, terms.Domains, row[1], Utc(row[2]), Utc(row[3]));
    }

    public async Task<CursorPage<CompetitorView>> ListCompetitorsAsync(Guid tenantId, Guid siteId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSiteExists(connection, null, tenantId, siteId, cancellationToken);
        var (ids, next) = await ListIdsAsync(connection, "competitors", tenantId, siteId, cursor, limit, cancellationToken);
        var result = new List<CompetitorView>();
        foreach (var id in ids) result.Add((await ReadCompetitor(tenantId, siteId, id, cancellationToken))!);
        return new CursorPage<CompetitorView>(result, next);
    }

    public async Task<CompetitorView> UpdateCompetitorAsync(Guid tenantId, Guid siteId, Guid competitorId, CompetitorPatch request,
        CancellationToken cancellationToken = default)
    {
        if (request.ExpectedVersion is null) throw Error("CONFIG_VERSION_REQUIRED", "请提供 expectedVersion。", 400);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (request.ExpectedVersion.Value != uint.Parse(site[6])) throw Error("CONFIG_VERSION_CONFLICT", "主体配置版本已变化。", 409);
        var row = await Row(connection, transaction, "SELECT name, status FROM competitors WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@id) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", competitorId.ToString()));
        if (row is null) throw Error("RESOURCE_NOT_FOUND", "竞品不存在。", 404);
        var oldTerms = await ReadTerms(connection, transaction, siteId, "competitor", competitorId, cancellationToken);
        var name = request.Name?.Trim() ?? row[0];
        if (name.Length is < 1 or > 200) throw Error("INVALID_NAME", "竞品名称长度必须为 1–200。", 400);
        var status = ValidateStatus(request.Status ?? row[1]);
        if (status == "active" && row[1] == "archived")
            await SubscriptionLimitGuard.EnsureCompetitorsAsync(connection, transaction, tenantId, siteId, 1, cancellationToken);
        var terms = CompetitorTerms(name, request.Aliases ?? oldTerms.Aliases, request.Domains ?? oldTerms.Domains);
        await Exec(connection, transaction, """
            UPDATE competitors SET name = @name, status = @status,
                archived_at = IF(@status = 'archived', COALESCE(archived_at, UTC_TIMESTAMP(6)), NULL),
                updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id);
            """, cancellationToken, ("@name", name), ("@status", status), ("@id", competitorId.ToString()));
        await ReplaceTerms(connection, transaction, tenantId, siteId, "competitor", competitorId, terms, cancellationToken);
        await BumpRevision(connection, transaction, tenantId, siteId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadCompetitor(tenantId, siteId, competitorId, cancellationToken))!;
    }
}
