using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>套餐目录、租户订阅、卡密与周期额度的存储接口。</summary>
public interface ISubscriptionStore
{
    /// <summary>读取套餐目录（按排序）；提供租户时标记当前生效套餐。</summary>
    /// <param name="tenantId">用于标记 isCurrent 的租户；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<SubscriptionPlanView>> ListPlansAsync(Guid? tenantId, CancellationToken cancellationToken = default);

    /// <summary>读取租户当前订阅、周期额度和各上限用量。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantSubscriptionView> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>兑换卡密；同套餐顺延、更高套餐立即切换并发放额度、更低套餐拒绝。失败按租户与 IP 限流。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="userId">操作用户。</param>
    /// <param name="code">卡密明文。</param>
    /// <param name="clientIp">客户端 IP，用于失败限流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantSubscriptionView> RedeemAsync(Guid tenantId, Guid userId, string code, string clientIp, CancellationToken cancellationToken = default);

    /// <summary>系统管理员指派套餐；允许降级（立即生效，不清零、不发放）。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="actorUserId">操作管理员。</param>
    /// <param name="request">目标套餐、时长或到期时间、原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TenantSubscriptionView> AssignAsync(Guid tenantId, Guid actorUserId, AssignSubscriptionRequest request, CancellationToken cancellationToken = default);

    /// <summary>系统管理员读取租户订阅详情与最近事件。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AdminTenantSubscriptionView> GetForAdminAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>系统管理员修改套餐目录；编码不可新增或删除。</summary>
    /// <param name="code">套餐编码。</param>
    /// <param name="update">需要修改的字段。</param>
    /// <param name="actorUserId">操作管理员。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<SubscriptionPlanView> UpdatePlanAsync(string code, SubscriptionPlanUpdate update, Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>批量生成卡密；明文只在返回值中出现一次。</summary>
    /// <param name="request">套餐、时长、数量、截止时间、备注。</param>
    /// <param name="actorUserId">操作管理员。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PlanCodeBatchResult> CreateCodeBatchAsync(PlanCodeBatchRequest request, Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>分页查询卡密。</summary>
    /// <param name="batchId">按批次过滤。</param>
    /// <param name="status">按状态过滤（unused / redeemed / revoked / expired）。</param>
    /// <param name="planCode">按套餐过滤。</param>
    /// <param name="cursor">上页游标。</param>
    /// <param name="limit">每页数量，1–100。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PlanCodePage> ListCodesAsync(Guid? batchId, string? status, string? planCode, string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>作废未使用的卡密。</summary>
    /// <param name="codeId">卡密标识。</param>
    /// <param name="actorUserId">操作管理员。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PlanCodeView> RevokeCodeAsync(Guid codeId, Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Worker：处理到期周期（清零可用积分、发放下一周期额度，或在付费到期时降级），返回处理的订阅数。</summary>
    /// <param name="utcNow">当前 UTC 时间。</param>
    /// <param name="batchSize">单轮最多处理的订阅数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<int> RolloverDuePeriodsAsync(DateTime utcNow, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>Worker：把已到期的付费订阅降为免费体验版，返回处理的订阅数。</summary>
    /// <param name="utcNow">当前 UTC 时间。</param>
    /// <param name="batchSize">单轮最多处理的订阅数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<int> DowngradeExpiredAsync(DateTime utcNow, int batchSize, CancellationToken cancellationToken = default);
}
