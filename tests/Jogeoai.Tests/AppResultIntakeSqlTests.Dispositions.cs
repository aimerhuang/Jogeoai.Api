using Jogeoai.Domain;
using static Jogeoai.Tests.AppCreditSqlTests;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;

namespace Jogeoai.Tests;

/// <summary>结果提交的各种处理结果（contract.md 9.6）：校验、回显、过期租约、重复、冲突；以及元数据列的容量和没有预留的检测。</summary>
public sealed partial class AppResultIntakeSqlTests
{
    [AppChannelMySqlFact]
    public Task StaleDuplicateConflictingAndEchoSubmissionsNeverWriteASecondSample() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (nodeA, _) = await pool.NodeAsync("mac");
        var (nodeB, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "处理结果", 1, "doubao");
        var (a, b) = (await pool.BindAsync(nodeA, "AVD_1", "doubao"), await pool.BindAsync(nodeB, "AVD_B", "doubao"));
        var task = (await store.LeaseAsync(new AppLeaseCommand(nodeA, a, 120))).Task!;
        AppResultCitation withUrl = new("横评", "少数派", "https://sspai.com/post/1", "cited"), withoutUrl = new("知乎回答", "知乎", null, null);
        Task<CollectorResultResponse> SubmitAsync(AppResultCommand command) => store.SubmitResultAsync(command);
        async Task<(string, int)> RefusedAsync(AppResultCommand command) => Code(await Assert.ThrowsAsync<CollectorStoreException>(() => SubmitAsync(command)));
        // submissions by disposition | attempts other than leases by kind | check status | answer set | citations | item
        Task<string> StateAsync() => pool.RowAsync("""
            SELECT (SELECT COALESCE(GROUP_CONCAT(CONCAT(disposition, ':', n) ORDER BY disposition), '-') FROM
                       (SELECT disposition, COUNT(*) n FROM collector_result_submissions WHERE collector_task_id = UUID_TO_BIN(@task) GROUP BY disposition) s),
                   (SELECT COALESCE(GROUP_CONCAT(CONCAT(event_kind, ':', n) ORDER BY event_kind), '-') FROM
                       (SELECT event_kind, COUNT(*) n FROM collector_attempts WHERE collector_task_id = UUID_TO_BIN(@task) AND event_kind <> 'leased' GROUP BY event_kind) x),
                   c.status, c.answer IS NOT NULL, (SELECT COUNT(*) FROM citations WHERE check_id = c.id),
                   (SELECT status FROM monitoring_credit_items WHERE check_id = c.id)
            FROM visibility_checks c WHERE c.id = UUID_TO_BIN(@check);
            """, ("@task", task.Id.ToString()), ("@check", task.CheckId.ToString()));
        var valid = Result(nodeA, task, "key-accepted", "推荐随手记。", withUrl, withoutUrl) with { DeviceId = "AVD_1" };

        // Refused before anything is stored: validation, the payload limits, an echo of the question, an unknown task.
        Assert.Equal((AppResultRules.InvalidCode, 400), await RefusedAsync(valid with { AnswerText = "  " }));
        Assert.Equal((AppResultRules.InvalidCode, 400), await RefusedAsync(valid with { Citations = [] }));
        Assert.Equal((AppResultRules.InvalidCode, 400), await RefusedAsync(valid with
        {
            Citations = Enumerable.Range(0, 100).Select(i => new AppResultCitation(new string('题', 512), null, "https://a.example/" + new string('u', 2030), null)).ToArray(),
        }));
        Assert.Equal("处理结果：第 1 条提问，哪个记账工具好用？", task.QuestionText);
        foreach (var echo in new[] { task.QuestionText, $"  {task.QuestionText}\n", "处理结果：第 1 条提问，\n哪个 记账工具　好用？" })
            Assert.Equal((AppResultRules.EchoCode, 400), await RefusedAsync(valid with { AnswerText = echo }));
        Assert.Equal(("TASK_NOT_FOUND", 404), await RefusedAsync(valid with { TaskId = Guid.NewGuid() }));
        Assert.Equal("-|-|pending|0|0|held", await StateAsync());
        Assert.Equal($"leased|1|{nodeA}|1|0|-|-", await TaskAsync(pool.Cs, task.Id));

        // Stale: another node, another lease version, another device, a lease binding of another node, an expired lease. Each is kept as a
        // submission and an attempt; the check, the citations and the reservation are untouched and no sample is named.
        var stale = new CollectorResultResponse(CollectorResultDisposition.Stale, null);
        Assert.Equal(stale, await SubmitAsync(valid with { NodeId = nodeB, IdempotencyKey = "key-other-node" }));
        Assert.Equal(stale, await SubmitAsync(valid with { LeaseVersion = 2, IdempotencyKey = "key-version" }));
        Assert.Equal(stale, await SubmitAsync(valid with { DeviceId = "AVD_B", IdempotencyKey = "key-other-device" }));
        await pool.ExecAsync("UPDATE collector_tasks SET lease_app_binding_id = UUID_TO_BIN(@b) WHERE id = UUID_TO_BIN(@id);", ("@b", b.ToString()), ("@id", task.Id.ToString()));
        Assert.Equal(stale, await SubmitAsync(valid with { IdempotencyKey = "key-foreign-binding" }));
        await pool.ExecAsync("UPDATE collector_tasks SET lease_app_binding_id = UUID_TO_BIN(@a) WHERE id = UUID_TO_BIN(@id);", ("@a", a.ToString()), ("@id", task.Id.ToString()));
        await ExpireLeaseAsync(pool, task.Id);
        Assert.Equal(stale, await SubmitAsync(valid with { IdempotencyKey = "key-expired" }));
        Assert.Equal("stale:5|stale:5|pending|0|0|held", await StateAsync());
        Assert.Equal($"{nodeB}|2|1", await pool.RowAsync("""
            SELECT (SELECT node_id FROM collector_result_submissions WHERE collector_task_id = UUID_TO_BIN(@id) AND idempotency_key = 'key-other-node'),
                   (SELECT lease_version FROM collector_result_submissions WHERE collector_task_id = UUID_TO_BIN(@id) AND idempotency_key = 'key-version'),
                   (SELECT COUNT(*) = SUM(app_binding_id IS NULL) FROM collector_attempts WHERE collector_task_id = UUID_TO_BIN(@id) AND event_kind = 'stale');
            """, ("@id", task.Id.ToString())));
        // The same stale submission again is a duplicate without a sample; other content under its key is a conflict.
        Assert.Equal(new CollectorResultResponse(CollectorResultDisposition.Duplicate, null), await SubmitAsync(valid with { LeaseVersion = 2, IdempotencyKey = "key-version" }));
        Assert.Equal((AppResultRules.IdempotencyConflictCode, 409), await RefusedAsync(valid with { IdempotencyKey = "key-version", AnswerText = "别的回答。" }));

        // Accepted while the lease is current.
        await pool.ExecAsync("UPDATE collector_tasks SET lease_until = UTC_TIMESTAMP(6) + INTERVAL 60 SECOND WHERE id = UUID_TO_BIN(@id);", ("@id", task.Id.ToString()));
        Assert.Equal(new CollectorResultResponse(CollectorResultDisposition.Accepted, task.CheckId), await SubmitAsync(valid));
        Assert.Equal("accepted:1,stale:5|accepted:1,duplicate:1,stale:5|succeeded|1|1|captured", await StateAsync());
        var ledger = await LedgerAsync(pool, task.CheckId);

        // Duplicate: the same key and the same content, whatever the order of the sources and whether a missing member is
        // null or an empty string. It names the sample and writes nothing but an attempt.
        var duplicate = new CollectorResultResponse(CollectorResultDisposition.Duplicate, task.CheckId);
        Assert.Equal(duplicate, await SubmitAsync(valid));
        Assert.Equal(duplicate, await SubmitAsync(valid with { Citations = [withoutUrl with { Url = "", List = " " }, withUrl with { Title = " 横评 " }] }));
        // Members outside the hash (device, search state, times) do not make a retry a conflict.
        Assert.Equal(duplicate, await SubmitAsync(valid with { AppVersion = "10.0.0", SearchQueries = null, CompletedAt = valid.CompletedAt.AddSeconds(5) }));
        // Conflict: the same key with another answer or another source.
        Assert.Equal((AppResultRules.IdempotencyConflictCode, 409), await RefusedAsync(valid with { AnswerText = "推荐鲨鱼记账。" }));
        Assert.Equal((AppResultRules.IdempotencyConflictCode, 409), await RefusedAsync(valid with { Citations = [withUrl, withoutUrl with { Title = "另一个回答" }] }));
        Assert.Equal((AppResultRules.IdempotencyConflictCode, 409), await RefusedAsync(valid with { Citations = [withUrl], }));
        // A new key after the task succeeded is stale: the sample is not overwritten.
        Assert.Equal(stale, await SubmitAsync(valid with { IdempotencyKey = "key-late", AnswerText = "迟到的另一条回答。" }));
        Assert.Equal("accepted:1,stale:6|accepted:1,duplicate:4,stale:6|succeeded|1|1|captured", await StateAsync());
        Assert.Equal("推荐随手记。|9.9.9", await pool.RowAsync("""
            SELECT answer, JSON_UNQUOTE(JSON_EXTRACT(result_metadata, '$.app.appVersion')) FROM visibility_checks WHERE id = UUID_TO_BIN(@id);
            """, ("@id", task.CheckId.ToString())));
        // Charged once: one reserve and one capture row, 10 credits.
        Assert.Equal(ledger, await LedgerAsync(pool, task.CheckId));
        Assert.Equal(2, ledger.Length);
        Assert.Equal("990.000|0.000|10.000|1|1|2", await AccountAsync(pool, rig.Tenant) + "|" + await CreditRowsAsync(pool, rig.Tenant));
        Assert.Equal("succeeded|1|1|0|1|1", await RunAsync(pool.Cs, cycle.Run));
        Assert.Equal(1, await pool.CountAsync("SELECT COUNT(*) FROM analytics_check_arrivals WHERE check_id = UUID_TO_BIN(@id);", ("@id", task.CheckId.ToString())));
    });

    [AppChannelMySqlFact]
    public Task ALongListOfSourcesFitsTheMetadataColumnAndACheckWithoutAReservationIsStillASample() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "容量", 2, "doubao");
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao");

        // 100 sources without a URL, each title 512 Chinese characters: about 160 KB against a 16 KB column.
        var task = (await store.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task!;
        var sources = Enumerable.Range(0, 100).Select(i => new AppResultCitation($"{i:000}" + new string('题', 509), "站点名称", null, "cited")).ToArray();
        Assert.Equal(CollectorResultDisposition.Accepted, (await store.SubmitResultAsync(Result(node, task, "key-long-list", "回答。", sources))).Disposition);
        var stored = (await pool.RowAsync("""
            SELECT OCTET_LENGTH(c.result_metadata) <= 16384, JSON_LENGTH(c.result_metadata, '$.app.sourcesWithoutUrl'),
                   JSON_EXTRACT(c.result_metadata, '$.app.citationsTotal'), JSON_EXTRACT(c.result_metadata, '$.app.citationsWithUrl'),
                   LEFT(JSON_UNQUOTE(JSON_EXTRACT(c.result_metadata, '$.app.sourcesWithoutUrl[0].title')), 3),
                   (SELECT COUNT(*) FROM citations WHERE check_id = c.id),
                   (SELECT JSON_LENGTH(s.citations) FROM collector_result_submissions s WHERE s.collector_task_id = UUID_TO_BIN(@task))
            FROM visibility_checks c WHERE c.id = UUID_TO_BIN(@check);
            """, ("@task", task.Id.ToString()), ("@check", task.CheckId.ToString()))).Split('|');
        // The list is cut from the end to fit; the counts stay complete and the raw submission keeps all 100.
        Assert.Equal(("1", "100", "0", "000", "0", "100"), (stored[0], stored[2], stored[3], stored[4], stored[5], stored[6]));
        Assert.InRange(int.Parse(stored[1]), 5, 15);

        // A check whose reservation is not held any more (it should not happen): the sample is accepted, nothing is charged
        // and the case is recorded as an event.
        await pool.AgePlatformAsync("doubao");
        await pool.AgeBindingAsync(binding);
        var second = (await store.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task!;
        await pool.ExecAsync("UPDATE monitoring_credit_items SET status = 'released', released_at = UTC_TIMESTAMP(6) WHERE check_id = UUID_TO_BIN(@id);",
            ("@id", second.CheckId.ToString()));
        var account = await AccountAsync(pool, rig.Tenant);
        Assert.Equal(new CollectorResultResponse(CollectorResultDisposition.Accepted, second.CheckId), await store.SubmitResultAsync(Result(node, second, "key-unreserved")));
        Assert.Equal("succeeded|released", await pool.RowAsync("""
            SELECT c.status, (SELECT status FROM monitoring_credit_items WHERE check_id = c.id) FROM visibility_checks c WHERE c.id = UUID_TO_BIN(@id);
            """, ("@id", second.CheckId.ToString())));
        Assert.Equal(account, await AccountAsync(pool, rig.Tenant));
        Assert.Single(await LedgerAsync(pool, second.CheckId));
        var recorded = await pool.LastEventAsync("result_without_reservation", binding);
        Assert.Equal((second.CheckId.ToString(), 1), (recorded.GetProperty("checkId").GetString(), recorded.GetProperty("leaseVersion").GetInt32()));
        Assert.Equal(second.Id.ToString(), await pool.RowAsync(
            "SELECT BIN_TO_UUID(task_id) FROM collector_app_events WHERE event_kind = 'result_without_reservation' AND binding_id = UUID_TO_BIN(@id);",
            ("@id", binding.ToString())));
        Assert.Equal("succeeded|2|2|0|1|1", await RunAsync(pool.Cs, cycle.Run));
    });
}
