using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;

namespace Jogeoai.Application;

/// <summary>API 回答 Provider（Cloro、豆包、DeepSeek）共享的 Visibility Run、任务和证据的应用层存储契约；类名沿用 Cloro。</summary>
public interface ICloroVisibilityStore
{
    /// <summary>事务创建 Run、Check、Provider Task 和 Outbox。</summary>
    Task<VisibilityRunCreated> CreateRunAsync(CreateCloroVisibilityRunCommand command, CancellationToken cancellationToken = default);
    /// <summary>按幂等键查询已存在的 Run，并校验其规范化请求与预期一致。</summary>
    Task<Guid?> GetExistingRunByKeyAsync(CreateCloroVisibilityRunCommand expected, CancellationToken cancellationToken = default);
    /// <summary>按租户查询 Run 概要。</summary>
    Task<VisibilityRunSummary?> GetRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken = default);
    /// <summary>按租户读取 Run 的 Provider，用于稳定选择查询契约。</summary>
    Task<string?> GetRunProviderAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken = default);
    /// <summary>按绑定租户、运行及筛选条件的稳定游标查询 Check。</summary>
    Task<CursorPage<CloroVisibilityCheckView>> GetChecksAsync(Guid tenantId, Guid runId, string? cursor, int limit, string? engine, string? status, CancellationToken cancellationToken = default);
    /// <summary>按租户查询单条 Check。</summary>
    Task<CloroVisibilityCheckView?> GetCheckAsync(Guid tenantId, Guid checkId, CancellationToken cancellationToken = default);
    /// <summary>按 Provider 领取待处理任务并递增租约版本；Cloro 要求 022 key 池迁移已部署，其他 Provider 不要求。</summary>
    Task<IReadOnlyList<CloroTaskLease>> LeaseTasksAsync(string workerId, int limit = 1, int leaseSeconds = 90,
        string provider = "cloro", CancellationToken cancellationToken = default);
    /// <summary>外部提交前持久化 submitting，避免未知提交被重复执行。</summary>
    Task MarkSubmittingAsync(CloroTaskLease task, string workerId, CancellationToken cancellationToken = default);
    /// <summary>保存外部任务 ID 并释放租约，下一次只允许 Poll。</summary>
    Task RecordSubmissionAsync(CloroTaskLease task, string workerId, CloroSubmittedTask submitted, CancellationToken cancellationToken = default);
    /// <summary>保存安全轮询摘要并释放租约，等待退避后再次领取。</summary>
    Task RecordPollAsync(CloroTaskLease task, string workerId, CloroTaskStatusSnapshot snapshot, TimeSpan delay, CancellationToken cancellationToken = default);
    /// <summary>事务保存回答、引用、用量和任务/运行终态。</summary>
    Task CompleteTaskAsync(CloroTaskLease task, string workerId, CloroAnswerSnapshot answer, int durationMs, CancellationToken cancellationToken = default);
    /// <summary>同步调用型 Provider（如豆包）在一次事务内完成：积分捕获、用量流水、外部标识、结果列、引用、任务终态、运行聚合与分析登记。</summary>
    /// <param name="task">当前租约。</param>
    /// <param name="workerId">持有租约的 Worker。</param>
    /// <param name="answer">规范化回答；其 Provider 必须已注册。</param>
    /// <param name="durationMs">外部调用耗时（毫秒）。</param>
    /// <param name="externalId">Provider 响应标识（如方舟响应 id），写入 external_task_id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task CompleteSynchronousTaskAsync(CloroTaskLease task, string workerId, CloroAnswerSnapshot answer, long durationMs,
        string externalId, CancellationToken cancellationToken = default);
    /// <summary>事务记录安全错误并更新检查及运行聚合；retryAfter 为空时下次尝试固定在 10 秒后。</summary>
    Task FailTaskAsync(CloroTaskLease task, string workerId, string errorCode, bool retryable, TimeSpan? retryAfter = null,
        CancellationToken cancellationToken = default);
    /// <summary>尚未提交的任务因 key 池不可用而延后：置为可重试并保持用户积分预留；仅积分耗尽时记录首次耗尽时间，用于最长等待。</summary>
    Task DeferTaskAsync(CloroTaskLease task, string workerId, string errorCode, DateTimeOffset nextAttemptAt,
        bool creditsExhausted, CancellationToken cancellationToken = default);
    /// <summary>排队后因积分不足失败的任务换账号重提：同一事务写入带旧外部 ID 的审计事件并回到 pending；超过上限返回 false。</summary>
    Task<bool> ResetForResubmissionAsync(CloroTaskLease task, string workerId, int maxResubmits, CancellationToken cancellationToken = default);
    /// <summary>按 Provider 回收过期租约；Cloro 的未知提交不会重新提交，同步调用型 Provider 回到 pending。</summary>
    Task<int> RecoverExpiredTasksAsync(string provider = "cloro", CancellationToken cancellationToken = default);
    /// <summary>将指定运行中有外部任务标识的本地超时失败重新入队；仅供显式修复，不在常规启动时调用。</summary>
    Task<int> RecoverTimedOutTasksAsync(Guid runId, CancellationToken cancellationToken = default);
}
