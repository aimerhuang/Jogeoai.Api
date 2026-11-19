using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>绑定登记（implement 5.3）：登记与更新、账号代号全局唯一、健康状态的转换、运营恢复。</summary>
public sealed partial class AppLeaseSqlTests
{
    private static AppBindingReport Report(string device, string platform, string label, string health = "ok", string? installed = "1.0")
        => new(device, platform, label, $"pkg.{platform}", "1.0", installed, "15", health);

    // Upsert by (node, device, platform); limits come from the control plane; an account label can live in one binding only;
    // a rejected binding does not stop the rest of the report.
    [AppChannelMySqlFact]
    public Task ABindingReportUpsertsByDeviceAndPlatformAndRejectsADuplicateAccountWithAnAuditEvent() => PoolAsync(async (pool, _) =>
    {
        var store = pool.Store();
        var (first, _) = await pool.NodeAsync("mac");
        var (second, _) = await pool.NodeAsync("mac");
        var suffix = Guid.NewGuid().ToString("N");
        var (doubaoA, kimiA) = ($"doubao-a-{suffix}", $"kimi-a-{suffix}");

        var registered = await store.ReportBindingsAsync(new AppBindingReportCommand(first, "rev-1",
            [Report("AI_Apps_4KB", "doubao", doubaoA), Report("AI_Apps_4KB", "kimi", kimiA)]));
        Assert.Empty(registered.Rejected);
        Assert.Equal(["AI_Apps_4KB|doubao", "AI_Apps_4KB|kimi"], registered.Bindings.Select(binding => $"{binding.DeviceId}|{binding.PlatformCode}"));
        Assert.All(registered.Bindings, binding => Assert.Equal((AppBindingStatus.Active, null, null, 100, null),
            (binding.Status, binding.StopReason, binding.CooldownUntil, binding.MinIntervalSeconds, binding.DailyQuota)));
        var (doubao, kimi) = (registered.Bindings[0].BindingId, registered.Bindings[1].BindingId);
        Assert.Equal("rev-1|1", await pool.RowAsync("SELECT collector_build_revision, last_seen_at IS NOT NULL FROM collector_app_nodes WHERE node_id = @node;",
            ("@node", first)));
        Assert.Equal(["binding_registered"], await pool.EventsAsync(doubao));

        // An explicit safety cooldown remains visible. Old per-binding quota and interval are not advertised as dispatch limits.
        await pool.ExecAsync("""
            UPDATE collector_app_bindings SET min_interval_seconds = 120, daily_quota = 5, cooldown_until = UTC_TIMESTAMP(6) + INTERVAL 1 HOUR
            WHERE id = UUID_TO_BIN(@id);
            """, ("@id", doubao.ToString()));
        var kimiBefore = await pool.RowAsync("SELECT updated_at, last_seen_at FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);", ("@id", kimi.ToString()));
        var again = Assert.Single((await store.ReportBindingsAsync(new AppBindingReportCommand(first, "rev-2",
            [Report("AI_Apps_4KB", "doubao", doubaoA, installed: "1.1")]))).Bindings);
        Assert.Equal((doubao, 100, (int?)null, AppBindingStatus.Active), (again.BindingId, again.MinIntervalSeconds, again.DailyQuota, again.Status));
        Assert.InRange(again.CooldownUntil!.Value, DateTimeOffset.UtcNow.AddMinutes(58), DateTimeOffset.UtcNow.AddMinutes(62));
        Assert.Equal("1.1|rev-2", await pool.RowAsync("""
            SELECT b.installed_app_version, n.collector_build_revision FROM collector_app_bindings b JOIN collector_app_nodes n ON n.node_id = b.node_id
            WHERE b.id = UUID_TO_BIN(@id);
            """, ("@id", doubao.ToString())));
        Assert.Equal(kimiBefore, await pool.RowAsync("SELECT updated_at, last_seen_at FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);", ("@id", kimi.ToString())));
        Assert.Equal(2, await pool.CountAsync("SELECT COUNT(*) FROM collector_app_bindings WHERE node_id = @node;", ("@node", first)));

        // Another node reports: the same account, an unknown platform, an operator-only health value, an unknown health value,
        // an empty device — each rejected on its own — and one valid binding, which is registered.
        var mixed = await store.ReportBindingsAsync(new AppBindingReportCommand(second, "rev-1",
        [
            Report("AVD_B", "doubao", doubaoA), Report("AVD_B", "chatgpt", "chatgpt-a"), Report("AVD_B", "qwen", $"qwen-x-{suffix}", "disabled_by_operator"),
            Report("AVD_B", "deepseek", $"deepseek-x-{suffix}", "broken"), Report(" ", "kimi", $"kimi-x-{suffix}"), Report("AVD_B", "tencent_yuanbao", $"yuanbao-a-{suffix}"),
        ]));
        Assert.Equal(["AVD_B|doubao|APP_ACCOUNT_ALREADY_BOUND", "AVD_B|chatgpt|INVALID_PLATFORM", "AVD_B|qwen|INVALID_BINDING_HEALTH",
            "AVD_B|deepseek|INVALID_BINDING_HEALTH", " |kimi|INVALID_BINDING"], mixed.Rejected.Select(item => $"{item.DeviceId}|{item.PlatformCode}|{item.Code}"));
        var yuanbao = Assert.Single(mixed.Bindings);
        Assert.Equal("tencent_yuanbao", yuanbao.PlatformCode);
        Assert.Equal(1, await pool.CountAsync("SELECT COUNT(*) FROM collector_app_bindings WHERE node_id = @node;", ("@node", second)));
        // The audit event points at the binding that owns the account and names both nodes; the owner is untouched.
        var rejection = await pool.LastEventAsync("binding_rejected", doubao);
        Assert.Equal(("account_already_bound", "AVD_B", first), (rejection.GetProperty("reason").GetString(), rejection.GetProperty("deviceId").GetString(),
            rejection.GetProperty("ownerNodeId").GetString()));
        Assert.Equal(second, await pool.RowAsync("SELECT node_id FROM collector_app_events WHERE event_kind = 'binding_rejected' AND binding_id = UUID_TO_BIN(@id);",
            ("@id", doubao.ToString())));
        Assert.Equal($"{first}|{doubaoA}", await pool.RowAsync("SELECT node_id, account_label FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);", ("@id", doubao.ToString())));
        // The same node moving the account to another device is the same conflict; a new label on the same slot is a change.
        Assert.Equal("APP_ACCOUNT_ALREADY_BOUND", Assert.Single((await store.ReportBindingsAsync(new AppBindingReportCommand(first, "rev-2",
            [Report("AI_Apps_4KB_2", "doubao", doubaoA)]))).Rejected).Code);
        Assert.Equal(doubao, Assert.Single((await store.ReportBindingsAsync(new AppBindingReportCommand(first, "rev-2",
            [Report("AI_Apps_4KB", "doubao", $"doubao-b-{suffix}")]))).Bindings).BindingId);
        Assert.Equal("binding_account_changed", (await pool.EventsAsync(doubao))[^1]);

        // Request-level limits.
        foreach (var bad in new[] { new AppBindingReportCommand(first, " ", []), new AppBindingReportCommand(first, new string('r', 129), []),
            new AppBindingReportCommand(first, "rev", Enumerable.Repeat(Report("d", "doubao", "l"), AppPoolRules.MaxReportedBindings + 1).ToArray()) })
            Assert.Equal(("INVALID_BINDING_REPORT", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => store.ReportBindingsAsync(bad))));
    });

    // health: a stop reason stops the binding; ok clears only the stops the collector's own check found; the rest need an operator.
    [AppChannelMySqlFact]
    public Task ReportedHealthStopsABindingAndOkClearsOnlyTheSelfCheckReasons() => PoolAsync(async (pool, _) =>
    {
        var store = pool.Store();
        var (node, _) = await pool.NodeAsync("mac");
        var label = $"doubao-{Guid.NewGuid():N}";
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao", label);
        async Task<AppBindingState> HealthAsync(string health)
            => Assert.Single((await store.ReportBindingsAsync(new AppBindingReportCommand(node, "rev", [Report("AI_Apps_4KB", "doubao", label, health)]))).Bindings);

        // A fresh online report clears all legacy self-check stops, including contract_changed.
        foreach (var reason in new[] { AppBindingStopReason.DeviceOffline, AppBindingStopReason.VersionMismatch, AppBindingStopReason.ContractUnverified, AppBindingStopReason.ContractChanged })
        {
            var stopped = await HealthAsync(AppContractNames.ToStorage(reason));
            Assert.Equal((AppBindingStatus.Stopped, (AppBindingStopReason?)reason), (stopped.Status, stopped.StopReason));
            Assert.Equal($"stopped|{AppContractNames.ToStorage(reason)}|1|0", await pool.BindingAsync(binding));
            Assert.Equal((AppBindingStatus.Active, null), ((await HealthAsync("ok")).Status, (await HealthAsync("ok")).StopReason));
            Assert.Equal("active|-|0|0", await pool.BindingAsync(binding));
        }
        // Safety stops still require operator intervention.
        foreach (var reason in new[] { AppBindingStopReason.LoginRequired, AppBindingStopReason.ChallengeRequired, AppBindingStopReason.EmulatorRejected })
        {
            var name = AppContractNames.ToStorage(reason);
            Assert.Equal(reason, (await HealthAsync(name)).StopReason);
            var stoppedAt = await pool.RowAsync("SELECT stopped_at FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);", ("@id", binding.ToString()));
            Assert.Equal(reason, (await HealthAsync("ok")).StopReason);
            Assert.Equal(reason, (await HealthAsync("device_offline")).StopReason);
            // stopped_at is the moment the binding stopped, not the moment of the last report.
            Assert.Equal(stoppedAt, await pool.RowAsync("SELECT stopped_at FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);", ("@id", binding.ToString())));
            Assert.True(await store.ResumeBindingAsync(binding, Guid.NewGuid()));
            Assert.False(await store.ResumeBindingAsync(binding, null));
            Assert.Equal("active|-|0|0", await pool.BindingAsync(binding));
        }
        // An operator stop is kept whatever the collector reports.
        await pool.ExecAsync("UPDATE collector_app_bindings SET status = 'stopped', stop_reason = 'disabled_by_operator', stopped_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id);",
            ("@id", binding.ToString()));
        foreach (var health in new[] { "ok", "login_required", "device_offline" })
            Assert.Equal(AppBindingStopReason.DisabledByOperator, (await HealthAsync(health)).StopReason);
        Assert.Equal(("APP_BINDING_NOT_FOUND", 404), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => store.ResumeBindingAsync(Guid.NewGuid(), null))));

        // One event per state change, none for a report that changed nothing.
        var events = await pool.EventsAsync(binding);
        Assert.Equal(("binding_registered", 7, 7), (events[0], events.Count(kind => kind == "binding_stopped"), events.Count(kind => kind == "binding_resumed")));
        Assert.Equal(15, events.Length);
        // A new binding can be registered already stopped.
        var born = Assert.Single((await store.ReportBindingsAsync(new AppBindingReportCommand(node, "rev",
            [Report("AI_Apps_4KB", "kimi", $"kimi-{Guid.NewGuid():N}", "contract_unverified")]))).Bindings);
        Assert.Equal("stopped|contract_unverified|1|0", await pool.BindingAsync(born.BindingId));
    });
}
