using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 绑定登记（implement 5.3）：按（节点、设备、平台）登记或更新；账号代号全局唯一；健康状态的转换；运营恢复。
/// 迁移 052 起：新绑定以“被运营停用、未批准”登记，运营恢复即批准；每个节点可登记的不同设备数有上限。
/// 加锁顺序：节点行 → 绑定行。节点行锁让同一个节点的并发上报（以及运营调整设备上限）逐个进行，设备数不会被并发突破；
/// 领取、失败上报、结果提交从不锁节点行，所以与“绑定 → 平台 → 任务”的顺序不冲突。
/// </summary>
public sealed partial class AppCollectorPoolStore
{
    /// <summary>账号代号已属于另一个绑定。</summary>
    internal const string AccountAlreadyBoundCode = "APP_ACCOUNT_ALREADY_BOUND";

    private sealed record ValidBinding(string DeviceId, string PlatformCode, string AccountLabel, string AppPackage, string? Reviewed,
        string? Installed, string? Android, AppBindingStopReason? Health);

    /// <inheritdoc />
    public async Task<AppBindingReportResult> ReportBindingsAsync(AppBindingReportCommand command, CancellationToken cancellationToken = default)
    {
        var revision = command.CollectorBuildRevision?.Trim();
        if (revision is not { Length: >= 1 and <= 128 } || command.Bindings is null || command.Bindings.Count > AppPoolRules.MaxReportedBindings
            || command.Bindings.Any(binding => binding is null))
            throw new CollectorStoreException("INVALID_BINDING_REPORT",
                $"collectorBuildRevision 必须为 1–128 个字符，bindings 最多 {AppPoolRules.MaxReportedBindings} 条。", 400);
        await InTransactionAsync(async (c, t, ct) =>
        {
            // Before anything is written: without 052 the report is refused as a whole (503).
            await AppChannelSchema.RequireBindingApprovalAsync(c, t, ct);
            return Done(await ExecuteAsync(c, t, """
                UPDATE collector_app_nodes
                SET last_seen_at = UTC_TIMESTAMP(6), collector_build_revision = @revision, updated_at = UTC_TIMESTAMP(6)
                WHERE node_id = @node;
                """, ct, ("@revision", revision), ("@node", command.NodeId)));
        }, cancellationToken);

        // One transaction per binding: a rejected binding does not undo the others, and no transaction ever holds the
        // rows of two bindings (a lease locks every binding of one device). Each of them takes the node row first.
        var accepted = new List<AppBindingState>();
        var rejected = new List<AppBindingRejection>();
        foreach (var reported in command.Bindings)
        {
            if (Validate(reported, out var binding) is { } invalid) { rejected.Add(invalid); continue; }
            var (state, rejection) = await InTransactionAsync(async (c, t, ct) => Done(await UpsertBindingAsync(c, t, command.NodeId, binding!, ct)),
                cancellationToken);
            if (state is not null) accepted.Add(state);
            else rejected.Add(rejection!);
        }
        return new AppBindingReportResult(accepted, rejected);
    }

    private static AppBindingRejection? Validate(AppBindingReport reported, out ValidBinding? binding)
    {
        binding = null;
        AppBindingRejection Bad(string code, string message) => new(reported.DeviceId, reported.PlatformCode, code, message);
        static bool Within(string? value, int max) => value is null || value.Trim().Length <= max;
        static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var (device, label, package) = (reported.DeviceId?.Trim(), reported.AccountLabel?.Trim(), reported.AppPackage?.Trim());
        if (device is not { Length: >= 1 and <= 128 } || label is not { Length: >= 1 and <= 128 } || package is not { Length: >= 1 and <= 255 }
            || !Within(reported.ReviewedAppVersion, 64) || !Within(reported.InstalledAppVersion, 64) || !Within(reported.AndroidVersion, 32))
            return Bad("INVALID_BINDING", "deviceId、accountLabel 必须为 1–128 个字符，appPackage 为 1–255 个字符，版本号最多 64 个字符，androidVersion 最多 32 个字符。");
        var platform = AppChannelDefinition.TryGetPlatform(reported.PlatformCode?.Trim() ?? "");
        if (platform is null) return Bad("INVALID_PLATFORM", "platformCode 不是 App 渠道的平台。");
        AppBindingStopReason? health = null;
        var value = reported.Health?.Trim();
        if (!string.Equals(value, AppContractNames.HealthOk, StringComparison.OrdinalIgnoreCase))
        {
            if (!AppContractNames.TryParse<AppBindingStopReason>(value, out var reason) || reason == AppBindingStopReason.DisabledByOperator)
                return Bad("INVALID_BINDING_HEALTH", "health 必须是 ok，或除 disabled_by_operator 之外的停止原因。");
            health = reason;
        }
        binding = new ValidBinding(device, platform.Code, label, package, Optional(reported.ReviewedAppVersion),
            Optional(reported.InstalledAppVersion), Optional(reported.AndroidVersion), health);
        return null;
    }

    private static async Task<(AppBindingState? State, AppBindingRejection? Rejection)> UpsertBindingAsync(MySqlConnection c, MySqlTransaction t,
        string nodeId, ValidBinding binding, CancellationToken ct)
    {
        var actor = NodeActor(nodeId);
        // RF2. The node row is locked first and held to the end of this transaction: reports of one node (and the operator's
        // change of the cap) run one after another, so the device count read below cannot be passed by a concurrent report.
        var maxDevices = Int((await QuerySingleAsync(c, t, "SELECT max_devices FROM collector_app_nodes WHERE node_id = @node FOR UPDATE;", ct,
            ("@node", nodeId)) ?? throw new CollectorStoreException("APP_NODE_NOT_FOUND", "采集节点不存在。", 404))[0]);
        var existing = await QuerySingleAsync(c, t, """
            SELECT BIN_TO_UUID(id), account_label, stop_reason, approved_at IS NOT NULL FROM collector_app_bindings
            WHERE node_id = @node AND device_id = @device AND platform_code = @platform FOR UPDATE;
            """, ct, ("@node", nodeId), ("@device", binding.DeviceId), ("@platform", binding.PlatformCode));
        Guid? existingId = existing is null ? null : Guid.Parse(existing[0]);
        var approved = existing is not null && Flag(existing[3]);

        if (existing is null)
        {
            // Devices are compared by the column's collation, as the unique key (node, device, platform) does. An over-cap
            // binding is refused before the account check: nothing is stored and no event is written for it.
            var devices = (await QuerySingleAsync(c, t, """
                SELECT COUNT(DISTINCT device_id), COALESCE(MAX(device_id = @device), 0) FROM collector_app_bindings WHERE node_id = @node;
                """, ct, ("@node", nodeId), ("@device", binding.DeviceId)))!;
            if (!AppPoolRules.DeviceWithinLimit(Flag(devices[1]), Int(devices[0]), maxDevices))
                return (null, new AppBindingRejection(binding.DeviceId, binding.PlatformCode, AppPoolRules.DeviceLimitExceededCode,
                    $"该节点可登记的设备数已达上限（{maxDevices} 台）；需要接入更多设备时，请系统管理员调高该节点的设备上限。"));
        }

        async Task<AppBindingRejection?> AccountTakenAsync()
        {
            var owner = await QuerySingleAsync(c, t, """
                SELECT BIN_TO_UUID(id), node_id FROM collector_app_bindings WHERE platform_code = @platform AND account_label = @label;
                """, ct, ("@platform", binding.PlatformCode), ("@label", binding.AccountLabel));
            if (owner is null || Guid.Parse(owner[0]) == existingId) return null;
            await InsertEventAsync(c, t, new AppEvent("binding_rejected", actor, nodeId, Guid.Parse(owner[0]), binding.PlatformCode,
                Detail: new { reason = "account_already_bound", deviceId = binding.DeviceId, accountLabel = binding.AccountLabel, ownerNodeId = owner[1] }), ct);
            return new AppBindingRejection(binding.DeviceId, binding.PlatformCode, AccountAlreadyBoundCode,
                "该账号代号已属于另一个绑定；同一个账号只能出现在一个绑定里。");
        }

        if ((existing is null || !string.Equals(existing[1], binding.AccountLabel, StringComparison.Ordinal))
            && await AccountTakenAsync() is { } taken) return (null, taken);

        AppBindingStopReason? current = existing is not null && AppContractNames.TryParse<AppBindingStopReason>(existing[2], out var held) ? held : null;
        // RF1. A new binding, and a binding the operator has not approved yet, is always stopped / disabled_by_operator:
        // nothing the node reports makes it active. Only the operator's resume (ResumeBindingAsync) approves it.
        var next = AppPoolRules.StopReasonAfterReport(existing is not null, approved, current, binding.Health);
        var reason = next is { } value ? AppContractNames.ToStorage(value) : null;
        var id = existingId ?? Guid.NewGuid();
        object[] columns =
        [
            ("@id", id.ToString()), ("@label", binding.AccountLabel), ("@package", binding.AppPackage), ("@reviewed", binding.Reviewed),
            ("@installed", binding.Installed), ("@android", binding.Android), ("@status", next is null ? "active" : "stopped"), ("@reason", reason),
        ];
        if (existing is null)
        {
            try
            {
                await ExecuteAsync(c, t, """
                    INSERT INTO collector_app_bindings (id, node_id, device_id, platform_code, account_label, app_package,
                        reviewed_app_version, installed_app_version, android_version, status, stop_reason, stopped_at, approved_at,
                        last_seen_at, created_at, updated_at)
                    VALUES (UUID_TO_BIN(@id), @node, @device, @platform, @label, @package, @reviewed, @installed, @android, @status, @reason,
                        CASE WHEN @reason IS NULL THEN NULL ELSE UTC_TIMESTAMP(6) END, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, ct, [.. columns, ("@node", nodeId), ("@device", binding.DeviceId), ("@platform", binding.PlatformCode)]);
            }
            // A concurrent report took the account label or the same device slot between the checks and the insert.
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                return (null, await AccountTakenAsync() ?? new AppBindingRejection(binding.DeviceId, binding.PlatformCode,
                    "BINDING_REPORT_CONFLICT", "同一绑定正在被并发上报，请重试。"));
            }
            await InsertEventAsync(c, t, new AppEvent("binding_registered", actor, nodeId, id, binding.PlatformCode,
                Detail: new { deviceId = binding.DeviceId, accountLabel = binding.AccountLabel, stopReason = reason, approvalPending = true }), ct);
        }
        else
        {
            await ExecuteAsync(c, t, """
                UPDATE collector_app_bindings
                SET account_label = @label, app_package = @package, reviewed_app_version = @reviewed, installed_app_version = @installed,
                    android_version = @android, status = @status, stop_reason = @reason,
                    stopped_at = CASE WHEN @reason IS NULL THEN NULL ELSE COALESCE(stopped_at, UTC_TIMESTAMP(6)) END,
                    last_seen_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@id);
                """, ct, columns);
            if (!string.Equals(existing[1], binding.AccountLabel, StringComparison.Ordinal))
                await InsertEventAsync(c, t, new AppEvent("binding_account_changed", actor, nodeId, id, binding.PlatformCode,
                    Detail: new { from = existing[1], to = binding.AccountLabel }), ct);
            if (next != current)
                await InsertEventAsync(c, t, new AppEvent(next is null ? "binding_resumed" : "binding_stopped", actor, nodeId, id,
                    binding.PlatformCode, Detail: new
                    {
                        from = existing[2].Length == 0 ? null : existing[2], to = reason,
                        // A binding registered by an older build after 052 (active, never approved) is put back to pending here.
                        source = approved ? "health_report" : "approval_required",
                    }), ct);
        }
        return (await ReadBindingStateAsync(c, t, id, binding.DeviceId, binding.PlatformCode, ct), null);
    }

    private static async Task<AppBindingState> ReadBindingStateAsync(MySqlConnection c, MySqlTransaction t, Guid id, string deviceId,
        string platformCode, CancellationToken ct)
    {
        var row = await QuerySingleAsync(c, t, $"""
            SELECT status, stop_reason, {Iso("CASE WHEN cooldown_until > UTC_TIMESTAMP(6) THEN cooldown_until ELSE NULL END")}
            FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);
            """, ct, ("@id", id.ToString())) ?? throw new InvalidOperationException("绑定不存在。");
        return new AppBindingState(deviceId, platformCode, id, row[0] == "active" ? AppBindingStatus.Active : AppBindingStatus.Stopped,
            AppContractNames.TryParse<AppBindingStopReason>(row[1], out var reason) ? reason : null, ParseIsoOrNull(row[2]),
            AppPoolRules.DeviceCooldownSeconds, null);
    }

    /// <inheritdoc />
    public Task<bool> ResumeBindingAsync(Guid bindingId, Guid? operatorUserId, CancellationToken cancellationToken = default)
        => InTransactionAsync(async (c, t, ct) =>
        {
            await AppChannelSchema.RequireBindingApprovalAsync(c, t, ct);
            var row = await QuerySingleAsync(c, t, """
                SELECT node_id, platform_code, status, stop_reason, approved_at IS NOT NULL FROM collector_app_bindings
                WHERE id = UUID_TO_BIN(@id) FOR UPDATE;
                """, ct, ("@id", bindingId.ToString()))
                ?? throw new CollectorStoreException("APP_BINDING_NOT_FOUND", "绑定不存在。", 404);
            var approved = Flag(row[4]);
            if (row[2] == "active" && approved) return Done(false);
            // Resuming is approving: the first resume stamps approved_at, later ones keep it.
            await ExecuteAsync(c, t, """
                UPDATE collector_app_bindings
                SET status = 'active', stop_reason = NULL, stopped_at = NULL, approved_at = COALESCE(approved_at, UTC_TIMESTAMP(6)),
                    updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@id);
                """, ct, ("@id", bindingId.ToString()));
            await InsertEventAsync(c, t, new AppEvent("binding_resumed", Operator(operatorUserId), row[0], bindingId, row[1],
                Detail: new { from = row[3].Length == 0 ? null : row[3], to = (string?)null, source = "operator", approved = !approved }), ct);
            return Done(true);
        }, cancellationToken);
}
