using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>团队级跨品牌汇总查询。</summary>
public interface ITeamSummaryQuery
{
    /// <summary>汇总租户全部启用品牌（或指定品牌）的指标、计数与启用目标；单个品牌分析失败不影响其他品牌。</summary>
    Task<TeamSummaryView> SummaryAsync(Guid tenantId, Guid userId, TeamSummaryRequest request,
        CancellationToken cancellationToken = default);
}
