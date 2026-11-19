using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api.Controllers;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static Jogeoai.Tests.AppCreditSqlTests;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–049、库名含 _test）上的 App 结果入库（implement 6.3，contract.md 9.6，results.md 8.1）：接受时写入的每一列、
/// 带 URL 与不带 URL 的引用、绑定取自租约、结算、分析到达、Run 状态；重复、冲突、过期租约与回显。
/// 未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过，绝不使用业务库 jogeoai。每个用例只用自己新建的租户、节点和绑定，
/// 结束时删除它们写下的全部行并核对整库每张表的行数。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed partial class AppResultIntakeSqlTests
{
    // Same as Program.cs AddJsonOptions: web defaults plus string enums.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static (string, int) Code(CollectorStoreException exception) => (exception.Code, exception.StatusCode);

    private static AppCollectorController Controller(AppCollectorPoolStore store, string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + token;
        return new AppCollectorController(store) { ControllerContext = new() { HttpContext = context } };
    }

    private static async Task<JsonElement> AppBlockAsync(Pool pool, Guid check)
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>((await RowsAsync(pool.Cs,
            "SELECT CAST(result_metadata AS CHAR) FROM visibility_checks WHERE id = UUID_TO_BIN(@id);", ("@id", check.ToString())))[0][0]!);
        // The App block is the only member written by the intake.
        Assert.Equal(["app"], metadata.EnumerateObject().Select(member => member.Name));
        return metadata.GetProperty("app");
    }

    private static async Task<string?[]> CheckAsync(Pool pool, Guid check)
        => Assert.Single(await RowsAsync(pool.Cs, """
            SELECT status, answer, answer_present + 0, capture_mode, evidence_class, CAST(search_queries AS CHAR), query_availability,
                   DATE_FORMAT(captured_at, '%Y-%m-%dT%H:%i:%s.%f'), collector_node_id, collector_region, collector_build_revision,
                   BIN_TO_UUID(collector_task_id), result_hash, BIN_TO_UUID(app_binding_id), egress_country, BIN_TO_UUID(session_profile_id),
                   error_code, provider, engine
            FROM visibility_checks WHERE id = UUID_TO_BIN(@id);
            """, ("@id", check.ToString())));

    [AppChannelMySqlFact]
    public Task AnAcceptedResultWritesTheSampleFromTheLeaseAndOnlyUrlBearingSourcesBecomeCitations() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (node, token) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "入库", 3, "doubao");
        var label = $"doubao-a-{Guid.NewGuid():N}";
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao", label);
        var other = await pool.BindAsync(node, "AI_Apps_4KB_2", "doubao");
        var task = (await store.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task!;
        const string answer = "推荐随手记、鲨鱼记账和钱迹。\n它们都支持多账本。";

        // Untrusted top-level binding/account guesses do not replace the real lease. The reported device must match it.
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var request = JsonSerializer.Deserialize<AppResultRequest>($$"""
            { "nodeId": "{{node}}", "taskId": "{{task.Id}}", "leaseVersion": 1, "idempotencyKey": "intake-key-0001",
              "collectorBuildRevision": "collector-rev-7", "bindingId": "{{other}}", "appBindingId": "{{other}}", "accountLabel": "someone-else",
              "sentAt": "{{now:O}}", "completedAt": "{{now:O}}",
              "answerText": {{JsonSerializer.Serialize(answer)}},
              "citations": [
                { "title": "记账工具横评", "site": "少数派", "url": "https://www.sspai.com/post/1", "list": "cited" },
                { "title": "知乎回答", "site": "知乎", "url": null, "list": "cited" },
                { "title": "公众号文章", "site": "微信", "url": "weixin://article/1", "list": "more" },
                { "title": "钱迹官网", "site": null, "url": "http://QianJi.example.com/a?b=1", "list": "more" },
                { "title": "只有标题" } ],
              "citationsStatus": "read", "citationsIssue": null,
              "searchState": { "webSearch": "on", "deepThinking": "off", "searchQueries": ["记账 App 推荐", "记账工具 对比"] },
              "device": { "deviceId": "AI_Apps_4KB", "androidVersion": "15", "abi": "arm64-v8a" },
              "app": { "package": "com.larus.nova", "version": "15.7.0" },
              "screenshotSha256": "{{Sha.ToUpperInvariant()}}" }
            """, Json)!;
        var response = Assert.IsType<OkObjectResult>((await Controller(store, token).Result(request, default)).Result);
        Assert.Equal("{\"disposition\":\"accepted\",\"sampleId\":\"" + task.CheckId + "\"}", JsonSerializer.Serialize(response.Value, Json));

        // The check: the answer as sent, the App capture mode, the node, and the binding of the lease.
        AppResultCitation[] sources = [new("记账工具横评", "少数派", "https://www.sspai.com/post/1", "cited"), new("知乎回答", "知乎", null, "cited"),
            new("公众号文章", "微信", "weixin://article/1", "more"), new("钱迹官网", null, "http://QianJi.example.com/a?b=1", "more"), new("只有标题", null, null, null)];
        var hash = AppResultRules.PayloadHash(answer, sources);
        // No egress country, no web session profile, no error.
        string?[] expected = ["succeeded", answer, "1", "app", "observed_surface", """["记账 App 推荐", "记账工具 对比"]""", "available", now.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff"),
            node, "cn", "collector-rev-7", task.Id.ToString(), hash, binding.ToString(), null, null, null, "app_collector", "doubao"];
        Assert.Equal(expected.AsEnumerable(), await CheckAsync(pool, task.CheckId));
        var app = await AppBlockAsync(pool, task.CheckId);
        Assert.Equal(["accountLabel", "androidVersion", "appPackage", "appVersion", "citationsIssue", "citationsStatus", "citationsTotal", "citationsWithUrl",
            "deepThinking", "deviceId", "promptRuleVersion", "screenshotSha256", "sourcesWithoutUrl", "sourcesWithoutUrlTruncated", "webSearch"],
            app.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
        // implement 7.2: all three sources without a URL are kept, and the block says so.
        Assert.Equal(JsonValueKind.False, app.GetProperty("sourcesWithoutUrlTruncated").ValueKind);
        // The device and the account are the binding's.
        Assert.Equal(("AI_Apps_4KB", label, "15", "com.larus.nova", "15.7.0", "on", "off", "read", JsonValueKind.Null, 5, 2, Sha, AppPromptRules.RuleVersion),
            (app.GetProperty("deviceId").GetString(), app.GetProperty("accountLabel").GetString(), app.GetProperty("androidVersion").GetString(),
                app.GetProperty("appPackage").GetString(), app.GetProperty("appVersion").GetString(), app.GetProperty("webSearch").GetString(),
                app.GetProperty("deepThinking").GetString(), app.GetProperty("citationsStatus").GetString(), app.GetProperty("citationsIssue").ValueKind,
                app.GetProperty("citationsTotal").GetInt32(), app.GetProperty("citationsWithUrl").GetInt32(), app.GetProperty("screenshotSha256").GetString(),
                app.GetProperty("promptRuleVersion").GetInt32()));
        // Sources without a usable URL: title, site and list only, in the order they were shown.
        Assert.Equal(["知乎回答|知乎|cited", "公众号文章|微信|more", "只有标题||"], app.GetProperty("sourcesWithoutUrl").EnumerateArray().Select(source
            => $"{source.GetProperty("title").GetString()}|{source.GetProperty("site").GetString()}|{source.GetProperty("list").GetString()}"));
        // MySQL stores object members in its own order; every source has exactly these three, null when the App showed none.
        Assert.All(app.GetProperty("sourcesWithoutUrl").EnumerateArray(), source
            => Assert.Equal(["list", "site", "title"], source.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal)));

        // The citation table holds the two sources with a URL and nothing else.
        Assert.Equal(["1|https://www.sspai.com/post/1|sspai.com|记账工具横评", "2|http://qianji.example.com/a?b=1|qianji.example.com|钱迹官网"],
            (await RowsAsync(pool.Cs, "SELECT CONCAT_WS('|', position, normalized_url, source_domain, title) FROM citations WHERE check_id = UUID_TO_BIN(@id) ORDER BY position;",
                ("@id", task.CheckId.ToString()))).Select(row => row[0]!));

        // The raw submission keeps the whole list (URL-less sources too) and the request's own device block.
        Assert.Equal($"accepted|{hash}|5|CN|{node}|1|cn|collector-rev-7|intake-key-0001|AI_Apps_4KB|consumer_app|知乎回答|1|{now:yyyy-MM-dd HH:mm:ss.ffffff}", await pool.RowAsync("""
            SELECT disposition, payload_hash, JSON_LENGTH(citations), egress_country, node_id, lease_version, region, collector_build_revision, idempotency_key,
                   JSON_UNQUOTE(JSON_EXTRACT(provider_metadata, '$.device.deviceId')), JSON_UNQUOTE(JSON_EXTRACT(provider_metadata, '$.channel')),
                   JSON_UNQUOTE(JSON_EXTRACT(citations, '$[1].title')), JSON_TYPE(JSON_EXTRACT(citations, '$[1].url')) = 'NULL',
                   DATE_FORMAT(completed_at, '%Y-%m-%d %H:%i:%s.%f')
            FROM collector_result_submissions WHERE collector_task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString())));
        Assert.Equal(answer, (await RowsAsync(pool.Cs, "SELECT answer_text FROM collector_result_submissions WHERE collector_task_id = UUID_TO_BIN(@id);",
            ("@id", task.Id.ToString())))[0][0]);

        // Task, binding, attempt, credits, analysis arrival, run.
        Assert.Equal("succeeded|1|-|1|0|-|-", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal("1|1", await pool.RowAsync("""
            SELECT (SELECT completed_at IS NOT NULL AND lease_until IS NULL FROM collector_tasks WHERE id = UUID_TO_BIN(@task)),
                   (SELECT last_success_at IS NOT NULL FROM collector_app_bindings WHERE id = UUID_TO_BIN(@binding));
            """, ("@task", task.Id.ToString()), ("@binding", binding.ToString())));
        Assert.Equal("∅", await pool.RowAsync("SELECT last_success_at FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);", ("@id", other.ToString())));
        Assert.Equal($"accepted|{binding}|1|{node}|intake-key-0001|{hash}|∅", await pool.RowAsync("""
            SELECT event_kind, BIN_TO_UUID(app_binding_id), lease_version, node_id, idempotency_key, payload_hash, session_profile_id
            FROM collector_attempts WHERE collector_task_id = UUID_TO_BIN(@id) AND event_kind <> 'leased';
            """, ("@id", task.Id.ToString())));
        Assert.Equal("captured|10.000|1|0", await ItemAsync(pool, task.CheckId));
        Assert.Equal("990.000|0.000|10.000", await AccountAsync(pool, rig.Tenant));
        Assert.Equal($"1|{now:yyyy-MM-dd HH:mm:ss.ffffff}", await pool.RowAsync("""
            SELECT COUNT(*), DATE_FORMAT(MIN(captured_at), '%Y-%m-%d %H:%i:%s.%f') FROM analytics_check_arrivals WHERE check_id = UUID_TO_BIN(@id);
            """, ("@id", task.CheckId.ToString())));
        Assert.Equal("running|3|1|0|1|0", await RunAsync(pool.Cs, cycle.Run));
        // The run's check view (phase 4) reads the sample; the account label is not part of it.
        var view = Assert.Single((await rig.App.GetChecksAsync(rig.Tenant, cycle.Run, null, 10, null, "succeeded")).Items);
        Assert.Equal((task.CheckId, answer, 2, "app"), (view.CheckId, view.Answer!.Text, view.Citations.Count, view.CaptureMode));
        Assert.DoesNotContain(label, JsonSerializer.Serialize(view, Json));

        // The device is free again. No searchState at all: the switches are unknown and the queries stay "not submitted".
        async Task<AppLeasedTask> NextAsync()
        {
            await pool.AgePlatformAsync("doubao");
            await pool.AgeBindingAsync(binding);
            return (await store.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task!;
        }
        var second = await NextAsync();
        Assert.Equal(cycle.Prompts[1], second.PromptId);
        Assert.Equal(new CollectorResultResponse(CollectorResultDisposition.Accepted, second.CheckId), await store.SubmitResultAsync(Result(node, second) with
        {
            WebSearch = AppVisibleState.Unknown, DeepThinking = AppVisibleState.Unknown, SearchQueries = null, ScreenshotSha256 = null,
        }));
        var plain = await CheckAsync(pool, second.CheckId);
        Assert.Equal(("succeeded", null, "unknown", binding.ToString()), (plain[0], plain[5], plain[6], plain[13]));
        var bare = await AppBlockAsync(pool, second.CheckId);
        Assert.Equal(("unknown", "unknown", "absent", 0, 0, 0, JsonValueKind.Null, "9.9.9"), (bare.GetProperty("webSearch").GetString(),
            bare.GetProperty("deepThinking").GetString(), bare.GetProperty("citationsStatus").GetString(), bare.GetProperty("citationsTotal").GetInt32(),
            bare.GetProperty("citationsWithUrl").GetInt32(), bare.GetProperty("sourcesWithoutUrl").GetArrayLength(), bare.GetProperty("screenshotSha256").ValueKind,
            bare.GetProperty("appVersion").GetString()));
        Assert.Equal(0, await pool.CountAsync("SELECT COUNT(*) FROM citations WHERE check_id = UUID_TO_BIN(@id);", ("@id", second.CheckId.ToString())));
        Assert.Equal("running|3|2|0|1|0", await RunAsync(pool.Cs, cycle.Run));

        // An empty list of queries was read: "empty". Then the run is complete.
        var third = await NextAsync();
        await store.SubmitResultAsync(Result(node, third) with { SearchQueries = [] });
        var searched = await CheckAsync(pool, third.CheckId);
        Assert.Equal(("[]", "empty"), (searched[5], searched[6]));
        Assert.Equal("succeeded|3|3|0|1|1", await RunAsync(pool.Cs, cycle.Run));
        Assert.Equal("30.000|0.000|30.000|0.000|captured|10.000|1|1", await ReservationAsync(pool, cycle.Run));
        Assert.Equal("970.000|0.000|30.000", await AccountAsync(pool, rig.Tenant));
    });
}
