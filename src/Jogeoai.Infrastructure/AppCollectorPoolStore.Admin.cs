using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 运营的写操作（implement 8.3）：绑定的运营停用与限速、平台的暂停与限速、恢复被判定为不可用的 App 入口、提问排除的审计事件。
/// 与领取、失败上报共用事务方法（READ COMMITTED、死锁重试）和加锁顺序：绑定的操作只锁这一个绑定行，平台的操作先锁平台行再改入口，
/// 都不锁任务。每次实际发生变化都在同一事务里追加一条 actor 为 operator 的审计事件；没有变化时不写。
/// </summary>
public sealed partial class AppCollectorPoolStore : IAppCollectorAdminStore
{
    /// <summary>运营设置的限速取值超出范围（400）。</summary>
    internal const string InvalidLimitsCode = "INVALID_APP_LIMITS";

    /// <summary>App 入口不是因为 App 拒绝模拟器而停用，运营接口不恢复它（409）。</summary>
    internal const string EntryNotRestorableCode = "APP_ENTRY_NOT_RESTORABLE";

    /// <summary>运营要把节点的设备上限调到低于它已登记的不同设备数（409）。</summary>
    internal const string MaxDevicesBelowRegisteredCode = "APP_NODE_MAX_DEVICES_BELOW_REGISTERED";

    private static readonly string OperatorStop = AppContractNames.ToStorage(AppBindingStopReason.DisabledByOperator);

    /// <inheritdoc />
    public Task<bool> SetNodeMaxDevicesAsync(string nodeId, int maxDevices, Guid? operatorUserId, CancellationToken cancellationToken = default)
    {
        nodeId = RequireNodeId(nodeId);
        if (!AppAdminRules.MaxDevicesValid(maxDevices))
            throw new CollectorStoreException(InvalidLimitsCode, $"maxDevices 必须在 1–{AppPoolRules.MaxDevicesPerNodeLimit} 之间。", 400);
        return InTransactionAsync(async (c, t, ct) =>
        {
            await AppChannelSchema.RequireBindingApprovalAsync(c, t, ct);
            // The same node row lock as a bindings report: a report cannot register a new device between the count and the update.
            var row = await QuerySingleAsync(c, t, "SELECT max_devices FROM collector_app_nodes WHERE node_id = @node FOR UPDATE;", ct,
                    ("@node", nodeId))
                ?? throw new CollectorStoreException("APP_NODE_NOT_FOUND", "采集节点不存在。", 404);
            var from = Int(row[0]);
            if (from == maxDevices) return Done(false);
            var registered = Int((await QuerySingleAsync(c, t,
                "SELECT COUNT(DISTINCT device_id) FROM collector_app_bindings WHERE node_id = @node;", ct, ("@node", nodeId)))![0]);
            if (maxDevices < registered)
                throw new CollectorStoreException(MaxDevicesBelowRegisteredCode,
                    $"该节点已登记 {registered} 台设备，设备上限不能低于这个数。", 409);
            await ExecuteAsync(c, t, "UPDATE collector_app_nodes SET max_devices = @max, updated_at = UTC_TIMESTAMP(6) WHERE node_id = @node;",
                ct, ("@max", maxDevices), ("@node", nodeId));
            await InsertEventAsync(c, t, new AppEvent("node_max_devices_changed", Operator(operatorUserId), nodeId,
                Detail: new { from, to = maxDevices, registeredDevices = registered }), ct);
            return Done(true);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DisableBindingAsync(Guid bindingId, Guid? operatorUserId, CancellationToken cancellationToken = default)
        => InTransactionAsync(async (c, t, ct) =>
        {
            // The statement below needs no 052 column, but the route reads the binding back with them: without 052 the read
            // would answer 503 after this write had been committed. Refused here instead, before anything is written.
            await AppChannelSchema.RequireBindingApprovalAsync(c, t, ct);
            var row = await LockBindingForOperatorAsync(c, t, bindingId, ct);
            if (row[3] == OperatorStop) return Done(false);
            // The previous stop reason (if any) is replaced; it stays in the event. An earlier stop keeps its stopped_at.
            await ExecuteAsync(c, t, """
                UPDATE collector_app_bindings
                SET status = 'stopped', stop_reason = @reason, stopped_at = COALESCE(stopped_at, UTC_TIMESTAMP(6)), updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@id);
                """, ct, ("@reason", OperatorStop), ("@id", bindingId.ToString()));
            await InsertEventAsync(c, t, new AppEvent("binding_stopped", Operator(operatorUserId), row[0], bindingId, row[1],
                Detail: new { from = row[3].Length == 0 ? null : row[3], to = OperatorStop, source = "operator" }), ct);
            return Done(true);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> SetBindingLimitsAsync(Guid bindingId, int minIntervalSeconds, int? dailyQuota, Guid? operatorUserId,
        CancellationToken cancellationToken = default)
    {
        if (!AppAdminRules.BindingLimitsValid(minIntervalSeconds, dailyQuota))
            throw new CollectorStoreException(InvalidLimitsCode,
                $"minIntervalSeconds 必须在 {AppAdminRules.MinBindingIntervalSeconds}–{AppAdminRules.MaxBindingIntervalSeconds} 之间，" +
                $"dailyQuota 为空（不设上限）或在 1–{AppAdminRules.MaxBindingDailyQuota} 之间。", 400);
        throw RetiredLimits();
    }

    private static CollectorStoreException RetiredLimits() => new("APP_LEGACY_LIMITS_RETIRED",
        $"绑定/平台限速参数已退役。派单以设备实际发送后 {AppPoolRules.DeviceCooldownSeconds} 秒为准。", 409);

    // node_id | platform_code | status | stop_reason; the binding row is the only lock.
    private static async Task<string[]> LockBindingForOperatorAsync(MySqlConnection c, MySqlTransaction t, Guid bindingId, CancellationToken ct)
        => await QuerySingleAsync(c, t, """
            SELECT node_id, platform_code, status, COALESCE(stop_reason, '')
            FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id) FOR UPDATE;
            """, ct, ("@id", bindingId.ToString()))
           ?? throw new CollectorStoreException("APP_BINDING_NOT_FOUND", "绑定不存在。", 404);

    /// <inheritdoc />
    public Task<bool> PausePlatformAsync(string platformCode, Guid? operatorUserId, CancellationToken cancellationToken = default)
    {
        var platform = AppAdminSql.RequirePlatform(platformCode);
        return InTransactionAsync(async (c, t, ct) =>
        {
            // A platform that is already paused (by an operator or by the verification breaker) keeps its reason.
            if ((await LockPlatformAsync(c, t, platform, ct)).Paused) return Done(false);
            await ExecuteAsync(c, t, """
                UPDATE collector_app_platform_states SET paused_at = UTC_TIMESTAMP(6), pause_reason = @reason, updated_at = UTC_TIMESTAMP(6)
                WHERE platform_code = @platform;
                """, ct, ("@reason", AppAdminRules.OperatorPauseReason), ("@platform", platform));
            await InsertEventAsync(c, t, new AppEvent("platform_paused", Operator(operatorUserId), PlatformCode: platform,
                Detail: new { reason = AppAdminRules.OperatorPauseReason }), ct);
            return Done(true);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> SetPlatformLimitsAsync(string platformCode, int minIntervalSeconds, int? dailyCap, Guid? operatorUserId,
        CancellationToken cancellationToken = default)
    {
        _ = AppAdminSql.RequirePlatform(platformCode);
        if (!AppAdminRules.PlatformLimitsValid(minIntervalSeconds, dailyCap))
            throw new CollectorStoreException(InvalidLimitsCode,
                $"minIntervalSeconds 必须在 {AppAdminRules.MinPlatformIntervalSeconds}–{AppAdminRules.MaxPlatformIntervalSeconds} 之间，" +
                $"dailyCap 为空（不设上限）或在 1–{AppAdminRules.MaxPlatformDailyCap} 之间。", 400);
        throw RetiredLimits();
    }

    /// <inheritdoc />
    public Task<bool> RestorePlatformEntryAsync(string platformCode, Guid? operatorUserId, CancellationToken cancellationToken = default)
    {
        var platform = AppAdminSql.RequirePlatform(platformCode);
        return InTransactionAsync(async (c, t, ct) =>
        {
            // Platform row first, as the failure path does before it disables the entry.
            await LockPlatformAsync(c, t, platform, ct);
            var entries = await QueryRowsAsync(c, t, $"""
                SELECT BIN_TO_UUID(id), enabled, COALESCE(unavailable_reason, '') FROM collector_platform_entries
                WHERE platform_code = @platform AND channel = '{AppChannel}' AND region = 'cn' ORDER BY version FOR UPDATE;
                """, ct, ("@platform", platform));
            if (entries.Any(entry => Flag(entry[1]))) return Done(false);
            // Only the verdict of the failure path (emulator_rejected) is undone here. An entry disabled for another reason — the
            // seed of a platform whose App content cannot be read, or a manual change without a reason — is a product decision.
            var rejected = entries.Where(entry => entry[2] == AppUnavailableReasons.EmulatorRejected).ToList();
            if (rejected.Count == 0)
                throw new CollectorStoreException(EntryNotRestorableCode,
                    "该平台的 App 入口不是因为 App 拒绝模拟器而停用，不能在这里恢复。", 409);
            await ExecuteAsync(c, t, $"""
                UPDATE collector_platform_entries SET enabled = TRUE, unavailable_reason = NULL
                WHERE id IN ({string.Join(", ", rejected.Select((_, i) => $"UUID_TO_BIN(@entry{i})"))});
                """, ct, [.. rejected.Select((entry, i) => (object)($"@entry{i}", entry[0]))]);
            await InsertEventAsync(c, t, new AppEvent("platform_entry_restored", Operator(operatorUserId), PlatformCode: platform,
                Detail: new { from = AppUnavailableReasons.EmulatorRejected }), ct);
            return Done(true);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task RecordPromptExclusionEventAsync(Guid tenantId, AppPromptExclusionView exclusion, bool revoked, Guid? operatorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exclusion);
        // Identifiers only: neither the prompt text nor the free-text reason is copied into the event.
        return InTransactionAsync(async (c, t, ct) => Done(await InsertEventAsync(c, t, new AppEvent(
            revoked ? "prompt_exclusion_revoked" : "prompt_excluded", Operator(operatorUserId),
            Detail: new { tenantId, siteId = exclusion.SiteId, promptId = exclusion.PromptId, exclusionId = exclusion.Id }), ct)), cancellationToken);
    }
}
