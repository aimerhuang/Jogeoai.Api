using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class ManagementStore
{
    /// <inheritdoc />
    public async Task<OnboardingCompletionView> CompleteOnboardingAsync(Guid tenantId, Guid siteId,
        OnboardingCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var prompts = request.Prompts ?? [];
        var competitors = request.Competitors ?? [];
        if (prompts.Count is < 1 or > 5)
            throw Error("PROMPT_SELECTION_LIMIT", "请选择 1–5 个监控问题。", 400);
        if (competitors.Count > 10) throw Error("COMPETITOR_SELECTION_LIMIT", "最多选择 10 个竞品。", 400);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 与品牌创建保持一致的锁顺序：租户 -> 团队引导 -> 品牌 -> 品牌引导。租户级锁让套餐上限校验与写入串行化。
        await LockTenant(connection, transaction, tenantId, cancellationToken);
        var setup = await Row(connection, transaction,
            "SELECT team_configured_at, BIN_TO_UUID(site_id) FROM tenant_onboarding WHERE tenant_id = UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()));
        if (setup is not null && string.IsNullOrEmpty(setup[0])) throw Error("TEAM_REQUIRED", "请先确认团队名称。", 400);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        EnsureBrandReadyForOnboarding(site);
        var state = await Row(connection, transaction, """
            SELECT completed FROM onboarding_progress WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        if (state is null) throw Error("RESOURCE_NOT_FOUND", "品牌引导不存在。", 404);
        var promptsCreated = 0;
        var competitorsCreated = 0;
        if (!Bool(state[0]))
        {
            // 保留原有 1–5 提问 / ≤10 竞品校验；套餐上限按本次一次性写入的数量校验。
            await SubscriptionLimitGuard.EnsureCompetitorsAsync(connection, transaction, tenantId, siteId, competitors.Count, cancellationToken);
            await SubscriptionLimitGuard.EnsurePromptsAsync(connection, transaction, tenantId, prompts.Count(prompt => prompt.Enabled), cancellationToken);
            foreach (var competitor in competitors)
            {
                var name = competitor.Name?.Trim() ?? "";
                if (name.Length is < 1 or > 200) throw Error("INVALID_NAME", "竞品名称长度必须为 1–200。", 400);
                var terms = CompetitorTerms(name, competitor.Aliases ?? [], competitor.Domains ?? []);
                var id = Guid.NewGuid();
                await Exec(connection, transaction, """
                    INSERT INTO competitors (id, tenant_id, site_id, name, status, created_at, updated_at)
                    VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @name, 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, cancellationToken, ("@id", id.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@name", name));
                await ReplaceTerms(connection, transaction, tenantId, siteId, "competitor", id, terms, cancellationToken);
                competitorsCreated++;
            }
            if (competitorsCreated > 0) await BumpRevision(connection, transaction, tenantId, siteId, cancellationToken);
            foreach (var prompt in prompts)
            {
                await InsertPrompt(connection, transaction, tenantId, siteId, prompt, cancellationToken);
                promptsCreated++;
            }
            await Exec(connection, transaction, """
                UPDATE onboarding_progress SET current_step = 'complete', completed = TRUE, updated_at = UTC_TIMESTAMP(6)
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);
                UPDATE tenant_onboarding SET completed_at = COALESCE(completed_at, UTC_TIMESTAMP(6))
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        }
        await transaction.CommitAsync(cancellationToken);
        return new OnboardingCompletionView((await GetBrandAsync(tenantId, siteId, cancellationToken))!,
            competitorsCreated, promptsCreated, (await new OnboardingProgressStore(database).GetAsync(tenantId, siteId, cancellationToken))!);
    }

    private static void EnsureBrandReadyForOnboarding(string[] site)
    {
        // 官网允许为空；新建时已校验介绍，旧品牌完成引导不新增历史资料必填要求。
        if (site[2] != "active" || string.IsNullOrWhiteSpace(site[0]))
            throw Error("BRAND_REQUIRED", "请先填写有效品牌名称并启用品牌。", 400);
    }
}
