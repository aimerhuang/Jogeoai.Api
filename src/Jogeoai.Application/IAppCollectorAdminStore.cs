using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>
/// App 全局账号池的运营读写（implement 8.3，/api/v1/admin/collector-app/）。只供系统管理员接口调用，授权在 Controller 里完成。
/// 写操作沿用账号池的加锁顺序（绑定 → 平台 → 任务；这里的写操作都不锁任务）、READ COMMITTED 与死锁重试，
/// 每次实际发生变化都在同一事务里向 collector_app_events 追加一条事件（actor 为 operator:&lt;用户&gt;）；没有变化时不写事件。
/// 节点登记、令牌签发与轮换、节点启停、绑定恢复和平台恢复在 <see cref="IAppCollectorPoolStore"/> 上。迁移 049 未部署时返回 503 APP_CHANNEL_SCHEMA_NOT_MIGRATED；
/// 节点与绑定的读取、绑定的停用、节点设备上限的设置还需要迁移 052，未部署时同样返回 503，不做任何写入。
/// </summary>
public interface IAppCollectorAdminStore
{
    /// <summary>列出全部节点（按节点标识排序），含最近出现时间、是否失联与绑定数。不含令牌或令牌哈希。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<AppNodeView>> ListNodesAsync(CancellationToken cancellationToken = default);

    /// <summary>读取一个节点；不存在时返回 404 APP_NODE_NOT_FOUND。</summary>
    /// <param name="nodeId">节点标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppNodeView> GetNodeAsync(string nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置节点可登记的不同设备数上限（迁移 052）。取值必须在 1–64 之间，否则返回 400 INVALID_APP_LIMITS；
    /// 低于该节点已登记的不同设备数时返回 409 APP_NODE_MAX_DEVICES_BELOW_REGISTERED，不做修改（不允许出现“已登记但超限”）。
    /// 与现值相同时返回 false，不写事件。节点不存在时返回 404 APP_NODE_NOT_FOUND。
    /// 与绑定上报用同一把节点行锁，调低上限和同时到达的上报不会互相越过。
    /// </summary>
    /// <param name="nodeId">节点标识。</param>
    /// <param name="maxDevices">新的设备上限（1–64）。</param>
    /// <param name="operatorUserId">执行操作的运营人员，写入审计事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> SetNodeMaxDevicesAsync(string nodeId, int maxDevices, Guid? operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>列出绑定（按平台、节点、设备排序）；为空的条件不筛选。</summary>
    /// <param name="nodeId">只看该节点的绑定。</param>
    /// <param name="platformCode">只看该平台的绑定；不是 App 平台时返回 400 INVALID_PLATFORM。</param>
    /// <param name="status">只看该状态的绑定。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<AppBindingAdminView>> ListBindingsAsync(string? nodeId, string? platformCode, AppBindingStatus? status,
        CancellationToken cancellationToken = default);

    /// <summary>读取一个绑定；不存在时返回 404 APP_BINDING_NOT_FOUND。</summary>
    /// <param name="bindingId">绑定标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppBindingAdminView> GetBindingAsync(Guid bindingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 运营停用绑定：状态 stopped、原因 disabled_by_operator（覆盖原来的停止原因，原来的原因记在事件里；已停止的保留原来的停止时间）。
    /// 之后采集器上报 ok 或任何停止原因都不会解除它，只能由运营恢复。正在执行的租约不收回。已是运营停用时返回 false，不写事件。
    /// 绑定不存在时返回 404 APP_BINDING_NOT_FOUND。待批准的绑定本来就是这个状态和原因，返回 false；
    /// 状态是 active 但从未批准的绑定（旧版本在迁移 052 之后登记的）在这里变为待批准。迁移 052 未部署时返回 503，不做修改。
    /// </summary>
    /// <param name="bindingId">绑定标识。</param>
    /// <param name="operatorUserId">执行操作的运营人员，写入审计事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> DisableBindingAsync(Guid bindingId, Guid? operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置绑定的领取间隔与每日（最近 24 小时）领取上限，下一次领取起生效。取值超出 <see cref="AppAdminRules"/> 的范围时返回 400 INVALID_APP_LIMITS；
    /// 与现值相同时返回 false，不写事件。绑定不存在时返回 404 APP_BINDING_NOT_FOUND。
    /// </summary>
    /// <param name="bindingId">绑定标识。</param>
    /// <param name="minIntervalSeconds">领取间隔秒数。</param>
    /// <param name="dailyQuota">最近 24 小时的领取上限；为空表示不设上限。</param>
    /// <param name="operatorUserId">执行操作的运营人员，写入审计事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> SetBindingLimitsAsync(Guid bindingId, int minIntervalSeconds, int? dailyQuota, Guid? operatorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>列出每个已知 App 平台（按 <see cref="AppChannelDefinition.Platforms"/> 的顺序）的闸门与入口状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<AppPlatformStateView>> ListPlatformsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 运营暂停平台（pause_reason = operator_paused）：之后该平台的领取返回 platform_paused。已暂停（含验证熔断）时返回 false，
    /// 保留原来的暂停原因，不写事件。不是 App 平台时返回 400 INVALID_PLATFORM。恢复用 <see cref="IAppCollectorPoolStore.ResumePlatformAsync"/>。
    /// </summary>
    /// <param name="platformCode">平台代码。</param>
    /// <param name="operatorUserId">执行操作的运营人员，写入审计事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> PausePlatformAsync(string platformCode, Guid? operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置平台合计的领取间隔与 Asia/Shanghai 自然日的领取上限，下一次领取起生效。取值超出 <see cref="AppAdminRules"/> 的范围时返回
    /// 400 INVALID_APP_LIMITS；与现值相同时返回 false，不写事件。不是 App 平台时返回 400 INVALID_PLATFORM。
    /// </summary>
    /// <param name="platformCode">平台代码。</param>
    /// <param name="minIntervalSeconds">领取间隔秒数。</param>
    /// <param name="dailyCap">每日领取上限；为空表示不设上限。</param>
    /// <param name="operatorUserId">执行操作的运营人员，写入审计事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> SetPlatformLimitsAsync(string platformCode, int minIntervalSeconds, int? dailyCap, Guid? operatorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 恢复被判定为不可用（原因 APP_EMULATOR_REJECTED）的 App 入口：入口重新启用、清除原因。恢复绑定不会自动恢复入口（contract 9.5），
    /// 入口恢复也不会恢复任何绑定。入口本来可用时返回 false，不写事件；入口因其他原因停用（如文心一言的 APP_CONTENT_UNREADABLE，或没有注明原因）
    /// 时返回 409 APP_ENTRY_NOT_RESTORABLE，不做修改。不是 App 平台时返回 400 INVALID_PLATFORM。
    /// </summary>
    /// <param name="platformCode">平台代码。</param>
    /// <param name="operatorUserId">执行操作的运营人员，写入审计事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> RestorePlatformEntryAsync(string platformCode, Guid? operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>每个已知 App 平台（按平台顺序）当前的待领取数与进行中数。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<AppPlatformQueueView>> GetQueueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按任务标识读取一条 App 采集任务的尝试历史：任务当前的状态与最终结果，以及每一次租约（按租约版本升序）的节点、设备、绑定与账号代号、
    /// 发送状态与发题时间、失败码，以及哪一次的结果被接受。只读，不加锁；不含提问原文或回答内容。
    /// 任务不存在或不是 App 渠道的任务时返回 404 APP_TASK_NOT_FOUND；迁移 051 未部署时返回 503 APP_CHANNEL_SCHEMA_NOT_MIGRATED。
    /// </summary>
    /// <param name="taskId">采集任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppTaskAttemptsView> GetTaskAttemptsAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按条件分页读取审计事件，按发生时间、事件标识倒序（新的在前），游标稳定：翻页期间新写入的事件不会让已读过的事件重复出现。
    /// 条件不合法时返回 400 INVALID_FILTER，游标不合法时返回 400 INVALID_CURSOR。
    /// </summary>
    /// <param name="query">筛选条件。</param>
    /// <param name="cursor">上一页返回的不透明游标；第一页为空。</param>
    /// <param name="limit">每页条数（1–200）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CursorPage<AppEventView>> ListEventsAsync(AppEventQuery query, string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// 为运营代表租户做的提问排除或撤销追加一条审计事件（prompt_excluded / prompt_exclusion_revoked）。
    /// 排除本身由 <see cref="IAppCollectionStore.ExcludePromptAsync"/> / <see cref="IAppCollectionStore.RevokePromptExclusionAsync"/> 写入，
    /// 它们在自己的事务里提交，所以这条事件是在排除之后单独写入的。事件只含标识，不含提问原文或排除原因。
    /// </summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="exclusion">排除记录。</param>
    /// <param name="revoked">true 表示撤销，false 表示排除。</param>
    /// <param name="operatorUserId">执行操作的运营人员。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RecordPromptExclusionEventAsync(Guid tenantId, AppPromptExclusionView exclusion, bool revoked, Guid? operatorUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// App 渠道当前的告警信息（results.md 8.4 的六项，加上连续发题前失败的绑定）。运营接口的告警视图与每日汇报都调用它，所以两者的内容同源。只读，不加锁。
/// </summary>
public interface IAppAlertQuery
{
    /// <summary>
    /// 读取当前的告警信息：失联节点、停止的绑定、暂停或不可用的平台、过去 24 小时的未采集数与领取 / 成功 / 失败次数、各平台本周的容量占用、
    /// 最近连续在发题前失败的绑定（只提示，不改变任何状态）、待运营批准的绑定（不计入停止的绑定）。
    /// 迁移 049 未部署时返回 503 APP_CHANNEL_SCHEMA_NOT_MIGRATED；迁移 052 未部署时照常返回，待批准的绑定为空数组。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppAlertsView> GetAlertsAsync(CancellationToken cancellationToken = default);
}

/// <summary>每日告警汇报（results.md 8.4）的生成与存档：每个本地日期一份，没有告警也有一份。</summary>
public interface IAppDailyReportStore
{
    /// <summary>
    /// 生成某一天的汇报：这一天已经有了就返回已有的那一份（不重新生成、不再发送）；否则读取当前的告警信息，写成正文，存档后交给
    /// <see cref="IDailyReportSender"/>。同一天并发生成时只有一份被存下。迁移 049 未部署时返回 503 APP_CHANNEL_SCHEMA_NOT_MIGRATED。
    /// </summary>
    /// <param name="reportDate">汇报日期（本地日期）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppDailyReportGeneration> GenerateAsync(DateOnly reportDate, CancellationToken cancellationToken = default);

    /// <summary>按汇报日期倒序分页列出历史汇报（不含内容）。游标不合法时返回 400 INVALID_CURSOR。</summary>
    /// <param name="cursor">上一页返回的不透明游标；第一页为空。</param>
    /// <param name="limit">每页条数（1–100）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CursorPage<AppDailyReportItem>> ListAsync(string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>读取某一天的汇报；没有时返回空值。</summary>
    /// <param name="reportDate">汇报日期。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppDailyReportView?> GetAsync(DateOnly reportDate, CancellationToken cancellationToken = default);
}

/// <summary>
/// 每日汇报的发送渠道（总 PRD KD-19）。首期只有一个什么都不发的实现，结果记为 not_configured；
/// 以后接入群机器人时实现这个接口并加配置，不改汇报的生成。实现不得把汇报内容以外的东西发出去，也不得记录机器人地址或令牌。
/// </summary>
public interface IDailyReportSender
{
    /// <summary>发送一份已经存档的汇报，返回发送状态。抛出异常时按发送失败记录，不影响存档。</summary>
    /// <param name="report">汇报。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppDailyReportDelivery> SendAsync(AppDailyReportView report, CancellationToken cancellationToken = default);
}
