using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>租户内品牌、竞品、Prompt 和采集选择的持久化合同。</summary>
public interface IManagementStore
{
    /// <summary>在单一事务中保存引导内容并完成引导，重复提交不会重复创建。</summary>
    Task<OnboardingCompletionView> CompleteOnboardingAsync(Guid tenantId, Guid siteId,
        OnboardingCompletionRequest request, CancellationToken cancellationToken = default);

    /// <summary>建立品牌或绑定已有 Site。</summary>
    Task<BrandView> CreateBrandAsync(Guid tenantId, BrandWrite request, CancellationToken cancellationToken = default);
    /// <summary>获取品牌。</summary>
    Task<BrandView?> GetBrandAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default);
    /// <summary>列出租户品牌。</summary>
    Task<CursorPage<BrandView>> ListBrandsAsync(Guid tenantId, string? cursor, int limit, CancellationToken cancellationToken = default);
    /// <summary>按版本修改或归档品牌。</summary>
    Task<BrandView> UpdateBrandAsync(Guid tenantId, Guid siteId, BrandPatch request, CancellationToken cancellationToken = default);
    /// <summary>建立竞品。</summary>
    Task<CompetitorView> CreateCompetitorAsync(Guid tenantId, Guid siteId, CompetitorWrite request, CancellationToken cancellationToken = default);
    /// <summary>列出竞品。</summary>
    Task<CursorPage<CompetitorView>> ListCompetitorsAsync(Guid tenantId, Guid siteId, string? cursor, int limit, CancellationToken cancellationToken = default);
    /// <summary>修改或归档竞品。</summary>
    Task<CompetitorView> UpdateCompetitorAsync(Guid tenantId, Guid siteId, Guid competitorId, CompetitorPatch request, CancellationToken cancellationToken = default);
    /// <summary>建立标签。</summary>
    Task<TagView> CreateTagAsync(Guid tenantId, Guid siteId, TagWrite request, CancellationToken cancellationToken = default);
    /// <summary>列出标签。</summary>
    Task<CursorPage<TagView>> ListTagsAsync(Guid tenantId, Guid siteId, string? cursor, int limit, CancellationToken cancellationToken = default);
    /// <summary>修改或归档标签。</summary>
    Task<TagView> UpdateTagAsync(Guid tenantId, Guid siteId, Guid tagId, TagPatch request, CancellationToken cancellationToken = default);
    /// <summary>建立 Prompt。</summary>
    Task<PromptView> CreatePromptAsync(Guid tenantId, Guid siteId, PromptWrite request, CancellationToken cancellationToken = default);
    /// <summary>读取 Prompt。includeAppEligibility 只由提问管理的查询接口传 true：此时已开放 App 渠道的租户会带上 appEligibility；其他调用方得到的视图不含该字段。</summary>
    Task<PromptView?> GetPromptAsync(Guid tenantId, Guid siteId, Guid promptId, CancellationToken cancellationToken = default,
        bool includeAppEligibility = false);
    /// <summary>读取品牌 Prompt 的全量数量摘要。</summary>
    Task<PromptSummaryView> SummarizePromptsAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default);
    /// <summary>分页列出 Prompt。includeAppEligibility 只由提问管理的列表接口传 true：此时已开放 App 渠道的租户的每条提问会带上 appEligibility；其他调用方得到的视图不含该字段。</summary>
    Task<CursorPage<PromptView>> ListPromptsAsync(Guid tenantId, Guid siteId, string? search, Guid? tagId, string? cursor, int limit,
        CancellationToken cancellationToken = default, Guid? groupId = null, bool ungrouped = false, bool includeAppEligibility = false);
    /// <summary>修改 Prompt 并保留历史版本。</summary>
    Task<PromptView> UpdatePromptAsync(Guid tenantId, Guid siteId, Guid promptId, PromptPatch request, CancellationToken cancellationToken = default);
    /// <summary>统计品牌的提问分组数。</summary>
    Task<int> CountPromptGroupsAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default);
    /// <summary>分页列出现有 Prompt 的分组及筛选后的数量。</summary>
    Task<PromptGroupPage> ListPromptGroupsAsync(Guid tenantId, Guid siteId, string? search, Guid? tagId, string? cursor, int limit,
        CancellationToken cancellationToken = default);
    /// <summary>创建 Prompt 分组。</summary>
    Task<PromptGroupView> CreatePromptGroupAsync(Guid tenantId, Guid siteId, PromptGroupWrite request,
        CancellationToken cancellationToken = default);
    /// <summary>按配置版本修改 Prompt 分组名称。</summary>
    Task<PromptGroupView> UpdatePromptGroupAsync(Guid tenantId, Guid siteId, Guid groupId, PromptGroupPatch request,
        CancellationToken cancellationToken = default);
    /// <summary>预览 Prompt 批量导入。</summary>
    Task<PromptImportPreviewView> PreviewImportAsync(Guid tenantId, Guid siteId, IReadOnlyList<PromptImportRow> rows, CancellationToken cancellationToken = default);
    /// <summary>幂等提交 Prompt 批量导入或批量创建；<paramref name="targetKeys"/> 为本次新建提问统一的平台范围，省略为 all。</summary>
    Task<PromptImportResult> ImportAsync(Guid tenantId, Guid siteId, IReadOnlyList<PromptImportRow> rows, string idempotencyKey,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? targetKeys = null);
    /// <summary>
    /// 查询 Provider 目标目录。App 行只在给出 <paramref name="tenantId"/> 且该租户已开放 App 渠道（Collector:AppTenantIds）时返回；
    /// 不给租户时目录不含 App 行。
    /// </summary>
    Task<IReadOnlyList<CollectionTargetView>> GetTargetCatalogAsync(CancellationToken cancellationToken = default, Guid? tenantId = null);
    /// <summary>读取站点启用的目标。</summary>
    Task<SiteTargetsView> GetSiteTargetsAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default);
    /// <summary>
    /// 替换站点目标集合。<paramref name="markIndividual"/> 为 true（公开的品牌目标保存）时，同一事务内把品牌标为单独配置
    /// （不再跟随团队默认监控配置）；内部调用（首次开启监控写入默认目标）保持 false，不改跟随标记。迁移 048 未部署时忽略该标记。
    /// </summary>
    Task<SiteTargetsView> PutSiteTargetsAsync(Guid tenantId, Guid siteId, SiteTargetsWrite request, CancellationToken cancellationToken = default,
        bool markIndividual = false);
    /// <summary>读取团队默认监控配置（从未保存时为虚拟基线，版本 0）及团队内启用品牌的跟随状态；返回值的 CanEdit 恒为 false，由调用方按角色填写。</summary>
    Task<TeamMonitoringConfigView> GetTeamMonitoringConfigAsync(Guid tenantId, CancellationToken cancellationToken = default);
    /// <summary>按版本保存团队默认监控配置，并在同一事务内同步到所有启用且跟随的品牌（各品牌目标配置版本加 1）。</summary>
    Task<TeamMonitoringConfigView> PutTeamMonitoringConfigAsync(Guid tenantId, Guid userId, TeamMonitoringConfigWrite request,
        CancellationToken cancellationToken = default);
    /// <summary>品牌恢复跟随团队默认监控配置：可监控目标立即替换为团队默认配置（物化到该品牌），并标为跟随。</summary>
    Task<SiteTargetsView> FollowTeamDefaultAsync(Guid tenantId, Guid siteId, FollowTeamDefaultRequest request,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// 首次开启监控用：品牌跟随、团队保存过默认配置且品牌没有启用的可监控目标时，返回按该品牌物化的团队默认目标；
    /// 其他情况（含虚拟基线、迁移 048 未部署）返回 null，调用方沿用原有基线逻辑。只读。
    /// </summary>
    Task<IReadOnlyList<SiteTargetWrite>?> MaterializeTeamDefaultAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default);
    /// <summary>影响预估用：校验草稿单元格（与保存相同，但不因超过 20 个报错），并按每个启用且跟随的品牌物化为站点目标。只读。</summary>
    Task<IReadOnlyList<TeamMonitoringBrandTargets>> MaterializeTeamCellsForFollowersAsync(Guid tenantId,
        IReadOnlyList<TeamMonitoringCellWrite> cells, CancellationToken cancellationToken = default);
    /// <summary>固定 Prompt 与目标集合并创建采集规模预览。</summary>
    Task<CollectionPreviewView> CreatePreviewAsync(Guid tenantId, Guid siteId, CollectionSelection selection, CancellationToken cancellationToken = default);
    /// <summary>解析采集选择，验证预览并返回不可变的固定集合。</summary>
    Task<(IReadOnlyList<Guid> PromptIds, IReadOnlyList<CloroVisibilityTarget> Targets, int SampleCount)> ResolveRunAsync(Guid tenantId, Guid siteId,
        CollectionSelection selection, Guid? previewId, CancellationToken cancellationToken = default);
}
