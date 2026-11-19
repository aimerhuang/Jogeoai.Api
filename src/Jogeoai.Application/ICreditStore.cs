using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>租户共享积分账户和不可变账本边界。</summary>
public interface ICreditStore
{
    /// <summary>读取租户积分余额。</summary>
    Task<CreditAccountView> GetAccountAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>分页读取租户积分账本。</summary>
    Task<CreditLedgerPage> ListLedgerAsync(Guid tenantId, Guid? actorUserId, string? cursor, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>按团队时区自然日与操作类型聚合 capture 流水；actorUserId 非空时只统计该成员发起的消费（与账本同一谓词）。</summary>
    Task<CreditUsageView> UsageAsync(Guid tenantId, Guid? actorUserId, CreditUsageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>为生成批次原子预占积分。</summary>
    Task<decimal> ReserveAsync(Guid tenantId, Guid batchId, Guid taskId, decimal amount, string idempotencyKey,
        Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>成功生成后捕获预占积分。</summary>
    Task<bool> CaptureAsync(Guid tenantId, Guid reservationId, Guid articleVersionId,
        CancellationToken cancellationToken = default);

    /// <summary>失败任务释放预占积分。</summary>
    Task<bool> ReleaseAsync(Guid tenantId, Guid reservationId, string reason,
        CancellationToken cancellationToken = default);

    /// <summary>系统管理员为租户幂等入账。</summary>
    Task<CreditLedgerEntryView> GrantAsync(Guid tenantId, decimal amount, string externalReference,
        Guid actorUserId, string reason, CancellationToken cancellationToken = default);
}
