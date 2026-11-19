using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>品牌引导进度的持久化合同。</summary>
public interface IOnboardingProgressStore
{
    /// <summary>读取租户注册引导状态；已有租户不强制重新引导。</summary>
    Task<OnboardingWorkspaceState> GetWorkspaceAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>由团队所有者确认团队名称。</summary>
    Task ConfigureTeamAsync(Guid tenantId, Guid userId, string name, CancellationToken cancellationToken = default);

    /// <summary>保存选择步骤和草稿，已完成的引导不可回退。</summary>
    Task<OnboardingProgressView> SaveDraftAsync(Guid tenantId, Guid siteId, OnboardingDraftWrite request,
        CancellationToken cancellationToken = default);

    /// <summary>建立或读取品牌的引导进度。</summary>
    Task<OnboardingProgressView> EnsureAsync(Guid tenantId, Guid siteId, string currentStep = "select_prompts",
        CancellationToken cancellationToken = default);

    /// <summary>读取品牌引导进度。</summary>
    Task<OnboardingProgressView?> GetAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default);

    /// <summary>标记品牌引导完成。</summary>
    Task<OnboardingProgressView> CompleteAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default);
}
