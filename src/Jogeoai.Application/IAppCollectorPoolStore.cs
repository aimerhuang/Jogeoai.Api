using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>
/// App 全局账号池的控制面（依赖迁移 049）：节点身份、绑定登记、领取、续租、失败上报，以及运营对节点和绑定的操作。
/// 池不属于任何租户：任何绑定都可以领取任何已开放租户在该平台上的任务。领取时按次预留积分，结果被接受时结算。
/// </summary>
public interface IAppCollectorPoolStore
{
    /// <summary>记录实际发题时间；返回 accepted、duplicate 或 stale。</summary>
    Task<string> ReportSentAsync(AppSentCommand command, CancellationToken cancellationToken = default);
    /// <summary>按令牌查找节点。令牌只以 SHA-256 的形式比较；未知的令牌返回空值，被停用的节点照常返回并带状态。</summary>
    /// <param name="token">请求头里的节点令牌明文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppNodeIdentity?> AuthenticateNodeAsync(string? token, CancellationToken cancellationToken = default);

    /// <summary>登记一个采集节点并签发令牌。令牌明文只在返回值里出现这一次；节点已存在时返回 409 APP_NODE_EXISTS。</summary>
    /// <param name="nodeId">节点标识，1–128 个字符。</param>
    /// <param name="operatorUserId">执行操作的运营人员，写入审计事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppNodeCredential> RegisterNodeAsync(string nodeId, Guid? operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>更换节点的令牌，旧令牌立即失效。令牌明文只在返回值里出现这一次；节点不存在时返回 404 APP_NODE_NOT_FOUND。</summary>
    /// <param name="nodeId">节点标识。</param>
    /// <param name="operatorUserId">执行操作的运营人员，写入审计事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppNodeCredential> RotateNodeTokenAsync(string nodeId, Guid? operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>启用或停用节点。停用后它的全部请求被拒绝，其他节点不受影响；节点不存在时返回 404 APP_NODE_NOT_FOUND。</summary>
    /// <param name="nodeId">节点标识。</param>
    /// <param name="status">新的状态。</param>
    /// <param name="operatorUserId">执行操作的运营人员，写入审计事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SetNodeStatusAsync(string nodeId, AppNodeStatus status, Guid? operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 登记或更新节点上报的绑定，按（节点、设备、平台）识别。账号代号已属于另一个绑定的那一条被拒绝并写审计事件，其余照常处理。
    /// 新登记的绑定一律是 stopped / disabled_by_operator 且未批准（迁移 052），上报的健康状态不能让它生效，要由运营调用恢复接口批准；
    /// 未批准的绑定之后的上报也保持这个状态。节点已登记的不同设备数达到它的上限时，新设备的绑定被拒绝（device_limit_exceeded），不入库。
    /// 已批准的绑定：上报 ok 只解除设备离线、版本不一致、契约未审阅、契约变更这几种停止；其余停止原因要由运营恢复。没有出现在上报里的绑定保持不变。
    /// </summary>
    /// <param name="command">上报内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppBindingReportResult> ReportBindingsAsync(AppBindingReportCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// 运营恢复一个已停止的绑定（任何停止原因）；冷却不受影响。恢复同时就是批准：还没有批准过的绑定在这里记下批准时间，
    /// 已批准的绑定再次停用、恢复时批准时间不变。绑定已是 active 且已批准时返回 false；不存在时返回 404 APP_BINDING_NOT_FOUND。
    /// </summary>
    /// <param name="bindingId">绑定标识。</param>
    /// <param name="operatorUserId">执行操作的运营人员，写入审计事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> ResumeBindingAsync(Guid bindingId, Guid? operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>运营解除平台的暂停（含验证熔断）；熔断的计数从这一刻重新开始。平台没有暂停时返回 false。</summary>
    /// <param name="platformCode">平台代码。</param>
    /// <param name="operatorUserId">执行操作的运营人员，写入审计事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> ResumePlatformAsync(string platformCode, Guid? operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 为一个绑定领取至多一条 App 任务。按固定顺序评估：绑定归属、停止与冷却、同设备已有租约、设备的发题冷却、
    /// 平台暂停与入口停用、选任务。没有领到时返回原因和建议的重试间隔。
    /// 选任务时为这一个检测预留一次的积分（已有预留的不重复预留）；租户的可用积分不够一次检测的任务不领取并记下原因。
    /// 同一台设备（节点 + 虚机）尝试过的任务不再派给它。上一次租约已过期、既没有结果也没有失败上报的任务视同那一次尝试失败：
    /// 记一次失败后由本次领取的设备以新的租约版本接手，没有次数上限。
    /// 还没有被运营批准的绑定按已停止处理（binding_stopped），不管它的状态列是什么；它的请求不写入任何内容：
    /// 不刷新它的最近出现时间，不写事件，也不会给等待中的任务写下“绑定已停止”的原因。
    /// 绑定不存在或不属于该节点时返回 403 APP_BINDING_FORBIDDEN 并写审计事件。
    /// </summary>
    /// <param name="command">领取请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppLeaseResult> LeaseAsync(AppLeaseCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// 续租：节点、任务、租约版本都匹配且租约未过期时延长租约并把任务置为采集中；否则返回 409 STALE_LEASE。
    /// 新的截止时间取“现在 + 延长秒数”“领取时刻 + 总时长上限（AppChannel:MaxLeaseTotalSeconds，默认 1800 秒）”“任务的截止时点”三者中最早的一个；
    /// 领取已超过总时长上限，或任务已过截止时点时，同样返回 409 STALE_LEASE，租约不再延长。
    /// </summary>
    /// <param name="command">续租请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新的租约截止时间。</returns>
    Task<DateTimeOffset> HeartbeatAsync(AppHeartbeatCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// 处理一次分类失败：绑定停止或冷却、平台暂停、任务重新排队或进入终态。
    /// 还没有被运营批准的绑定的上报不生效：不写审计事件，不改绑定、任务和平台，不计入验证熔断；响应与“租约已不是当前租约”的上报相同。
    /// 入口只在该平台所有已批准、没有被运营停用的绑定都因 App 拒绝模拟器而停止时才置为不可用；验证熔断只统计已批准的绑定。
    /// 任务的去向不取决于发送状态，也没有失败次数上限：失败只结束这一次尝试，任务回到队列等另一台设备（需要登录时绑定停止，任务同样回到队列）。
    /// 只有需要人工验证、App 拒绝模拟器和频率受限使任务进入终态：对应的检测立即置为未采集，它预留的积分在同一事务里释放。
    /// </summary>
    /// <param name="command">失败上报。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppFailureResult> ReportFailureAsync(AppFailureCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// 接收一条 App 采集结果。节点必须持有任务的当前租约（节点、租约版本一致，租约未过期），否则只保留审计记录并返回 stale。
    /// 同一任务同一幂等键：内容哈希相同返回 duplicate，不同返回 409 RESULT_IDEMPOTENCY_CONFLICT。回答与提问原文相同返回 400 ECHO_ANSWER。
    /// 接受时在一个事务里写入回答、带 URL 的引用、app 元数据，任务置为成功，结算该检测的预留，并入分析队列。
    /// 样本所属的绑定取自任务的租约，不采信请求。
    /// </summary>
    /// <param name="command">采集结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CollectorResultResponse> SubmitResultAsync(AppResultCommand command, CancellationToken cancellationToken = default);
}
