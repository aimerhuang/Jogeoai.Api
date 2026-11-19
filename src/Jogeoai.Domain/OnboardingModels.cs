namespace Jogeoai.Domain;

/// <summary>当前登录用户的品牌引导摘要。</summary>
/// <param name="TenantId">当前请求解析出的租户标识。</param>
/// <param name="Tenant">当前用户在该租户中的成员关系；开发环境兼容桥请求可能为空。</param>
/// <param name="Brand">当前租户的首个有效品牌；尚未创建时为空。</param>
/// <param name="Progress">当前品牌的引导进度；尚未创建品牌时为空。</param>
/// <param name="TeamConfigured">是否已确认团队名称。</param>
/// <param name="Required">是否必须先完成注册引导。</param>
public sealed record OnboardingSummaryView(Guid TenantId, TenantMembershipView? Tenant, BrandView? Brand,
    OnboardingProgressView? Progress, bool TeamConfigured = true, bool Required = false);

/// <summary>品牌引导进度。</summary>
/// <param name="SiteId">品牌站点标识。</param>
/// <param name="CurrentStep">当前步骤。</param>
/// <param name="Completed">是否已完成。</param>
/// <param name="SelectedPromptLimit">允许选择的问题数量上限。</param>
/// <param name="UpdatedAt">最近更新时间。</param>
/// <param name="Draft">尚未完成的用户选择与研究任务，供刷新恢复。</param>
public sealed record OnboardingProgressView(Guid SiteId, string CurrentStep, bool Completed,
    int SelectedPromptLimit, DateTimeOffset UpdatedAt, OnboardingDraftWrite? Draft = null);

/// <summary>确认注册时预建的团队名称，不重复建立租户。</summary>
/// <param name="Name">团队名称，1–200 个字符。</param>
public sealed record OnboardingTeamWrite(string Name);

/// <summary>注册引导的租户级状态。</summary>
/// <param name="Required">是否尚需完成引导。</param>
/// <param name="TeamConfigured">团队名称是否已确认。</param>
/// <param name="SiteId">本次引导绑定的品牌。</param>
public sealed record OnboardingWorkspaceState(bool Required, bool TeamConfigured, Guid? SiteId);

/// <summary>保存品牌引导草稿，不创建正式问题或竞品。</summary>
/// <param name="CurrentStep">当前步骤，只允许 select_prompts 或 select_competitors。</param>
/// <param name="Prompts">已选择的问题，最多 5 个。</param>
/// <param name="Competitors">已选择的竞品，最多 10 个。</param>
/// <param name="ResearchJobId">最近一次研究任务标识。</param>
public sealed record OnboardingDraftWrite(string CurrentStep, IReadOnlyList<PromptWrite> Prompts,
    IReadOnlyList<CompetitorWrite> Competitors, Guid? ResearchJobId = null);

/// <summary>提交引导完成内容的请求。</summary>
/// <param name="Competitors">已选择的竞品，可以为空。</param>
/// <param name="Prompts">已选择或自定义的监控问题，必须选择 1–5 个。</param>
public sealed record OnboardingCompletionRequest(IReadOnlyList<CompetitorWrite>? Competitors, IReadOnlyList<PromptWrite>? Prompts);

/// <summary>引导完成后的摘要。</summary>
/// <param name="Brand">当前品牌。</param>
/// <param name="CompetitorsCreated">本次建立的竞品数。</param>
/// <param name="PromptsCreated">本次建立的监控问题数。</param>
/// <param name="Progress">完成后的引导进度。</param>
public sealed record OnboardingCompletionView(BrandView Brand, int CompetitorsCreated, int PromptsCreated, OnboardingProgressView Progress);
