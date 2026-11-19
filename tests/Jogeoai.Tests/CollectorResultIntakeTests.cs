using System.Reflection;
using System.Text.Json;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>国内采集结果写入的隔离库用例；未设置 JOGEOAI_COLLECTOR_TEST_CONNECTION 时跳过。</summary>
public sealed class CollectorMySqlFactAttribute : FactAttribute
{
    public CollectorMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_COLLECTOR_TEST_CONNECTION")))
            Skip = "需提供已执行 001–027 迁移的独立测试库 JOGEOAI_COLLECTOR_TEST_CONNECTION；不能使用业务库 jogeoai。";
    }
}

/// <summary>Collector 结果接口的引用标题与搜索关键词：控制器映射、校验上限与隔离库写入。</summary>
public sealed class CollectorResultIntakeTests
{
    private const string ServiceToken = "collector-intake-fixture-token";
    private static readonly string[] Urls = ["https://news.example.com/a", "https://www.example.org/b", "https://blog.example.net/c"];

    [Fact]
    public async Task ControllerMapsTitlesByIndexWithOneBasedPositionsAndPassesSearchQueries()
    {
        var f = new ControllerFixture();
        var result = await f.Controller.Result(f.Request(Urls, titles: ["  第一篇 标题 ", null, "   "], queries: [" 管道 摄像机 ", "工业内窥镜"]), default);

        Assert.IsType<OkObjectResult>(result.Result);
        var received = Assert.Single(f.Received);
        Assert.Equal(Urls, received.Citations.Select(c => c.Url));
        Assert.Equal([1, 2, 3], received.Citations.Select(c => c.Position));
        Assert.Equal(new string?[] { "  第一篇 标题 ", null, "   " }, received.Citations.Select(c => c.Title));
        Assert.Equal([" 管道 摄像机 ", "工业内窥镜"], received.SearchQueries!);

        // 与真实 Store 相同的规范化：标题去首尾空白、空白标题为 null，关键词去首尾空白。
        var stored = Assert.Single(f.Normalized);
        Assert.Equal(new string?[] { "第一篇 标题", null, null }, stored.Citations.Select(c => c.Title));
        Assert.Equal(["管道 摄像机", "工业内窥镜"], stored.SearchQueries!);
    }

    [Fact]
    public async Task ControllerWithoutTitlesOrQueriesKeepsTheLegacyShape()
    {
        var f = new ControllerFixture();
        Assert.IsType<OkObjectResult>((await f.Controller.Result(f.Request(Urls), default)).Result);

        var stored = Assert.Single(f.Normalized);
        Assert.All(stored.Citations, citation => Assert.Null(citation.Title));
        Assert.Equal([1, 2, 3], stored.Citations.Select(c => c.Position));
        Assert.Null(stored.SearchQueries);
    }

    [Fact]
    public async Task EmptySearchQueriesStayAnEmptyArrayRatherThanOmitted()
    {
        var f = new ControllerFixture();
        Assert.IsType<OkObjectResult>((await f.Controller.Result(f.Request(Urls, queries: []), default)).Result);
        Assert.Empty(Assert.Single(f.Normalized).SearchQueries!);
    }

    [Fact]
    public void RequestBindsTheCamelCaseWireNamesAndAcceptsLegacyPayloads()
    {
        const string legacy = """
            {"tenantId":"6b1b3c38-6a0e-4f5e-8a53-2f4f8f5d9d11","taskId":"0e7f0c8e-5c9b-4b51-9f0e-9a3e6b1d2c44","nodeId":"node-1",
             "region":"cn","leaseVersion":1,"idempotencyKey":"legacy-key-1","egressCountry":"CN","collectorBuildRevision":"rev",
             "answerText":"回答","citations":["https://news.example.com/a"],"completedAt":"2026-10-21T04:00:00.000Z"}
            """;
        var old = JsonSerializer.Deserialize<CollectorResultRequest>(legacy, JsonSerializerOptions.Web)!;
        Assert.Null(old.CitationTitles);
        Assert.Null(old.SearchQueries);

        var current = JsonSerializer.Deserialize<CollectorResultRequest>(
            legacy.Replace("\"citations\":[\"https://news.example.com/a\"]",
                "\"citations\":[\"https://news.example.com/a\",\"https://www.example.org/b\"],\"citationTitles\":[\"标题\",null],\"searchQueries\":[\"关键词\"]"),
            JsonSerializerOptions.Web)!;
        Assert.Equal(new string?[] { "标题", null }, current.CitationTitles!);
        Assert.Equal(["关键词"], current.SearchQueries!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task TitleCountThatDiffersFromCitationsIsRejectedBeforeTheStore(int titleCount)
    {
        var f = new ControllerFixture();
        var result = await f.Controller.Result(f.Request(Urls, titles: Enumerable.Repeat<string?>("标题", titleCount).ToArray()), default);
        AssertError(result.Result, 400, "INVALID_RESULT");
        Assert.Empty(f.Received);
    }

    [Fact]
    public async Task TitlesWithoutCitationsAreRejected()
    {
        var f = new ControllerFixture();
        var result = await f.Controller.Result(f.Request(null, titles: ["标题"]), default);
        AssertError(result.Result, 400, "INVALID_RESULT");
        Assert.Empty(f.Received);
    }

    [Fact]
    public async Task StoreValidationFailuresSurfaceAs400ThroughTheController()
    {
        var f = new ControllerFixture();
        var result = await f.Controller.Result(f.Request(Urls[..1], titles: [new string('题', 513)]), default);
        AssertError(result.Result, 400, "INVALID_RESULT");
        Assert.Empty(f.Normalized);
    }

    [Fact]
    public void TitlesAreTrimmedBlankBecomesNullAndOnlyOver512CharactersIsRejected()
    {
        var max = new string('题', 512);
        var normalized = DomesticCollectorStore.ValidateResultCommand(Command(
            titles: [max, "  " + new string('a', 512) + "\t", "　 ", null]));
        Assert.Equal(new string?[] { max, new string('a', 512), null, null }, normalized.Citations.Select(c => c.Title));
        Assert.Equal(Enumerable.Range(1, 4), normalized.Citations.Select(c => c.Position));

        AssertInvalid(() => DomesticCollectorStore.ValidateResultCommand(Command(titles: [new string('题', 513)])));
        AssertInvalid(() => DomesticCollectorStore.ValidateResultCommand(Command(titles: [" " + new string('a', 513) + " "])));
    }

    [Fact]
    public void SearchQueryCountLengthAndBlankLimitsAreEnforced()
    {
        // 32 个 500 字中文关键词约 48 KB，未超过 64 KB 列约束，必须被接受。
        var widest = Enumerable.Repeat(new string('词', 500), 32).ToArray();
        Assert.Equal(32, DomesticCollectorStore.ValidateResultCommand(Command(queries: widest)).SearchQueries!.Count);
        Assert.Equal([new string('a', 500)],
            DomesticCollectorStore.ValidateResultCommand(Command(queries: ["  " + new string('a', 500) + "  "])).SearchQueries!);

        AssertInvalid(() => DomesticCollectorStore.ValidateResultCommand(Command(queries: Enumerable.Repeat("关键词", 33).ToArray())));
        AssertInvalid(() => DomesticCollectorStore.ValidateResultCommand(Command(queries: ["关键词", "   "])));
        AssertInvalid(() => DomesticCollectorStore.ValidateResultCommand(Command(queries: [""])));
        AssertInvalid(() => DomesticCollectorStore.ValidateResultCommand(Command(queries: [null!])));
        AssertInvalid(() => DomesticCollectorStore.ValidateResultCommand(Command(queries: [new string('a', 501)])));
    }

    [Fact]
    public void SearchQueriesWhoseStoredJsonExceeds64KilobytesAreRejected()
    {
        // 控制字符在 JSON 中转义为 \u0001（6 字节），32 × 500 个即约 94 KB，超过 ck_visibility_checks_search_size。
        var oversized = Enumerable.Repeat(new string('\u0001', 500), 32).ToArray();
        AssertInvalid(() => DomesticCollectorStore.ValidateResultCommand(Command(queries: oversized)));
    }

    [CollectorMySqlFact]
    public async Task SubmitWritesTitlesAndQueryAvailabilityAndReplaysWithNewFieldsStayDuplicate()
    {
        var f = await SqlFixture.CreateAsync();
        var tasks = await f.LeaseAsync(3);
        var (withQueries, emptyQueries, legacy) = (tasks[0], tasks[1], tasks[2]);

        Assert.Equal(CollectorResultDisposition.Accepted, (await f.Store.SubmitResultAsync(
            f.Command(withQueries, "intake-with-queries", Urls, [" 第一篇 ", null, "  "], [" 管道摄像机 ", "工业内窥镜"]))).Disposition);
        Assert.Equal(CollectorResultDisposition.Accepted, (await f.Store.SubmitResultAsync(
            f.Command(emptyQueries, "intake-empty-queries", Urls[..1], ["只有一篇"], []))).Disposition);
        Assert.Equal(CollectorResultDisposition.Accepted, (await f.Store.SubmitResultAsync(
            f.Command(legacy, "intake-legacy-shape", Urls[..2]))).Disposition);

        await f.AssertCheckAsync(withQueries.CheckId, "available", ["管道摄像机", "工业内窥镜"]);
        Assert.Equal(new (string, string?, int)[] { (Urls[0], "第一篇", 1), (Urls[1], null, 2), (Urls[2], null, 3) },
            await f.CitationsAsync(withQueries.CheckId));
        await f.AssertCheckAsync(emptyQueries.CheckId, "empty", []);
        Assert.Equal(new (string, string?, int)[] { (Urls[0], "只有一篇", 1) }, await f.CitationsAsync(emptyQueries.CheckId));
        await f.AssertCheckAsync(legacy.CheckId, "unknown", null);
        Assert.Equal(new (string, string?, int)[] { (Urls[0], null, 1), (Urls[1], null, 2) }, await f.CitationsAsync(legacy.CheckId));

        // 幂等哈希只含回答与 URL：同一幂等键补充不同标题与关键词仍是 duplicate，且不改写已入库数据。
        var replay = await f.Store.SubmitResultAsync(
            f.Command(withQueries, "intake-with-queries", Urls, ["新标题", "新标题", "新标题"], ["另一个关键词"]));
        Assert.Equal((CollectorResultDisposition.Duplicate, (Guid?)withQueries.CheckId), (replay.Disposition, replay.CheckId));
        await f.AssertCheckAsync(withQueries.CheckId, "available", ["管道摄像机", "工业内窥镜"]);
        Assert.Equal("第一篇", (await f.CitationsAsync(withQueries.CheckId))[0].Title);
    }

    private static CollectorResultCommand Command(IReadOnlyList<string?>? titles = null, IReadOnlyList<string>? queries = null)
    {
        var urls = Enumerable.Range(1, Math.Max(titles?.Count ?? 0, 1)).Select(i => $"https://news.example.com/{i}").ToArray();
        return new(Guid.NewGuid(), Guid.NewGuid(), "intake-node", CollectorRegion.Cn, 1, "intake-validation", "CN", "intake-rev",
            "回答正文", urls.Select((url, index) => new CollectorCitationInput(url, titles?[index], index + 1)).ToArray(),
            DateTimeOffset.UtcNow, null, queries);
    }

    private static void AssertInvalid(Action action)
    {
        var exception = Assert.Throws<CollectorStoreException>(action);
        Assert.Equal(("INVALID_RESULT", 400), (exception.Code, exception.StatusCode));
    }

    private static void AssertError(IActionResult? result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class ControllerFixture
    {
        internal Guid Tenant { get; } = Guid.NewGuid();
        internal List<CollectorResultCommand> Received { get; } = [];
        internal List<CollectorResultCommand> Normalized { get; } = [];
        internal CollectorController Controller { get; }

        internal ControllerFixture()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Collector:ChinaServiceToken"] = ServiceToken,
                ["Collector:ChinaTenantIds:0"] = Tenant.ToString(),
            }).Build();
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = $"Bearer {ServiceToken}";
            var store = DispatchProxy.Create<IVisibilityCollectorStore, StoreProxy>();
            ((StoreProxy)(object)store).Handler = (method, args) =>
            {
                Assert.Equal(nameof(IVisibilityCollectorStore.SubmitResultAsync), method.Name);
                var command = Assert.IsType<CollectorResultCommand>(args[0]);
                Received.Add(command);
                // 应用真实 Store 的校验与规范化，让控制器测试覆盖完整的 400 映射。
                Normalized.Add(DomesticCollectorStore.ValidateResultCommand(command));
                return Task.FromResult(new CollectorResultResponse(CollectorResultDisposition.Accepted, Guid.NewGuid()));
            };
            Controller = new CollectorController(store, configuration,
                new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"))
            { ControllerContext = new() { HttpContext = context } };
        }

        internal CollectorResultRequest Request(IReadOnlyList<string>? citations, IReadOnlyList<string?>? titles = null,
            IReadOnlyList<string>? queries = null) =>
            new(Tenant, Guid.NewGuid(), "intake-node", "cn", 1, "intake-controller", "CN", "intake-rev", "回答正文",
                citations, DateTimeOffset.UtcNow, null, titles, queries);
    }

    public class StoreProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private sealed class SqlFixture
    {
        private readonly string connectionString;
        private readonly ManagementStore management;

        private SqlFixture(string connection)
        {
            connectionString = connection;
            var database = new MySqlDatabase(connection);
            management = new ManagementStore(database);
            Store = new DomesticCollectorStore(database);
        }

        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid Site { get; private set; }
        internal DomesticCollectorStore Store { get; }

        internal static async Task<SqlFixture> CreateAsync()
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_COLLECTOR_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_COLLECTOR_TEST_CONNECTION；SQL 测试不会静默通过。");
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
            Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
            var f = new SqlFixture(value!);
            await f.ExecAsync("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'collector intake fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@tenant", f.Tenant.ToString()));
            f.Site = (await f.management.CreateBrandAsync(f.Tenant, new BrandWrite("Collector Intake", "collector-intake.test", IncludeSubdomains: false, Description: "测试品牌提供设备与服务。"))).SiteId;
            var prompts = new List<Guid>();
            for (var i = 1; i <= 3; i++)
                prompts.Add((await f.management.CreatePromptAsync(f.Tenant, f.Site, new PromptWrite($"管道摄像机怎么选？第 {i} 问", "zh-CN"))).PromptId);
            var run = await f.Store.CreateRunAsync(new CreateVisibilityRunCommand(f.Tenant, f.Site, prompts,
                [new CollectorPlatformSelection("doubao", "doubao.consumer_web")], $"collector-intake-{Guid.NewGuid():N}"));
            Assert.Equal(3, run.TotalChecks);
            return f;
        }

        internal async Task<IReadOnlyList<LeasedCollectorTask>> LeaseAsync(int count)
        {
            var tasks = await Store.LeaseAsync(new CollectorLeaseCommand(Tenant, "intake-node", CollectorRegion.Cn, ["doubao"], count, 300));
            Assert.Equal(count, tasks.Count);
            return tasks;
        }

        internal CollectorResultCommand Command(LeasedCollectorTask task, string key, IReadOnlyList<string> urls,
            IReadOnlyList<string?>? titles = null, IReadOnlyList<string>? queries = null) =>
            new(Tenant, task.Id, "intake-node", CollectorRegion.Cn, task.LeaseVersion, key, "CN", "intake-rev", "豆包回答正文",
                urls.Select((url, index) => new CollectorCitationInput(url, titles?[index], index + 1)).ToArray(),
                DateTimeOffset.UtcNow, null, queries);

        internal async Task AssertCheckAsync(Guid checkId, string queryAvailability, string[]? searchQueries)
        {
            var rows = await QueryAsync("""
                SELECT status, capture_mode, evidence_class, query_availability, search_queries
                FROM visibility_checks WHERE id = UUID_TO_BIN(@check) AND tenant_id = UUID_TO_BIN(@tenant);
                """, ("@check", checkId.ToString()), ("@tenant", Tenant.ToString()));
            var row = Assert.Single(rows);
            Assert.Equal(("succeeded", "scraped", "observed_surface", queryAvailability), (row[0], row[1], row[2], row[3]));
            if (searchQueries is null) Assert.Null(row[4]);
            else Assert.Equal(searchQueries, JsonSerializer.Deserialize<string[]>(row[4]!));
        }

        internal async Task<(string Url, string? Title, int Position)[]> CitationsAsync(Guid checkId)
        {
            var rows = await QueryAsync("""
                SELECT normalized_url, title, CAST(position AS CHAR)
                FROM citations WHERE check_id = UUID_TO_BIN(@check) ORDER BY position, id;
                """, ("@check", checkId.ToString()));
            return rows.Select(row => (row[0]!, row[1], int.Parse(row[2]!))).ToArray();
        }

        private async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        private async Task<List<string?[]>> QueryAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string?[]>();
            while (await reader.ReadAsync())
            {
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                rows.Add(row);
            }
            return rows;
        }
    }
}
