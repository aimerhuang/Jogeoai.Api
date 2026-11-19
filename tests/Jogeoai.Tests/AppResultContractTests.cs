using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api.Controllers;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using static Jogeoai.Tests.AppCollectorContractTests;

namespace Jogeoai.Tests;

/// <summary>
/// App 结果提交的契约（不连数据库，contract.md 9.6、results.md 8.1）：请求与响应的 JSON 字段、控制器与纯规则的校验和上限、
/// 回显判定、幂等哈希的规范化、result_metadata 的 app 块与“带 URL / 不带 URL”的划分。
/// </summary>
public sealed class AppResultContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Sent = new(2026, 11, 17, 1, 10, 0, TimeSpan.Zero);
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A request that passes every check: one source with a URL and one without.</summary>
    internal static AppResultRequest Request(string? nodeId) => new(nodeId, Id.ToString(), 2, "result-key-0001", "rev-1", Sent, Sent.AddSeconds(42),
        "推荐三款记账工具。", [new("记账工具横评", "少数派", "https://sspai.com/post/1", "cited"), new("知乎回答", "知乎")], "read", null,
        new("on", "off", ["记账 工具 推荐"]), new("AI_Apps_4KB", "15", "arm64-v8a"), new("com.larus.nova", "15.7.0"), Sha);

    private static AppResultCommand Command() => new("mac-1", Id, 2, "result-key-0001", "rev-1", Sent, Sent.AddSeconds(42), "推荐三款记账工具。",
        [new("记账工具横评", "少数派", "https://sspai.com/post/1", "cited"), new("知乎回答", "知乎", null, null)], AppCitationsStatus.Read, null,
        AppVisibleState.On, AppVisibleState.Off, ["记账 工具 推荐"], "AI_Apps_4KB", "15", "arm64-v8a", "com.larus.nova", "15.7.0", Sha);

    private static void AssertInvalid(AppResultCommand command)
    {
        var exception = Assert.Throws<CollectorStoreException>(() => AppResultRules.Validate(command));
        Assert.Equal((AppResultRules.InvalidCode, 400), (exception.Code, exception.StatusCode));
    }

    [Fact]
    public void TheRequestBindsFromTheContractFieldNamesAndTheResponseWritesSampleIdAsNull()
    {
        var request = JsonSerializer.Deserialize<AppResultRequest>($$"""
            { "nodeId": "mac-1", "taskId": "{{Id}}", "leaseVersion": 1, "idempotencyKey": "result-key-0001",
              "collectorBuildRevision": "rev-1", "sentAt": "2026-11-17T01:10:00.000Z", "completedAt": "2026-11-17T01:10:42.500Z",
              "answerText": "回答",
              "citations": [ { "title": "标题", "site": null, "url": null, "list": "cited" }, { "title": "T2", "site": "S", "url": "https://a.example/x" } ],
              "citationsStatus": "read", "citationsIssue": null,
              "searchState": { "webSearch": "on", "deepThinking": "off", "searchQueries": ["词"] },
              "device": { "deviceId": "AI_Apps_4KB", "androidVersion": "15", "abi": "arm64-v8a" },
              "app": { "package": "com.larus.nova", "version": "15.7.0" },
              "screenshotSha256": "{{Sha}}" }
            """, Json)!;
        Assert.Equal(("mac-1", Id.ToString(), 1, "result-key-0001", "rev-1", "回答", "read", null, Sha),
            (request.NodeId, request.TaskId, request.LeaseVersion, request.IdempotencyKey, request.CollectorBuildRevision, request.AnswerText,
                request.CitationsStatus, request.CitationsIssue, request.ScreenshotSha256));
        Assert.Equal((Sent, TimeSpan.Zero, Sent.AddSeconds(42.5)), (request.SentAt, request.SentAt!.Value.Offset, request.CompletedAt));
        Assert.Equal([new AppResultCitationRequest("标题", null, null, "cited"), new AppResultCitationRequest("T2", "S", "https://a.example/x")], request.Citations);
        Assert.Equal(("on", "off", "词"), (request.SearchState!.WebSearch, request.SearchState.DeepThinking, Assert.Single(request.SearchState.SearchQueries!)));
        Assert.Equal(new AppResultDeviceRequest("AI_Apps_4KB", "15", "arm64-v8a"), request.Device);
        Assert.Equal(new AppResultAppRequest("com.larus.nova", "15.7.0"), request.App);
        // Members the collector may leave out bind as null.
        var minimal = JsonSerializer.Deserialize<AppResultRequest>("""{"nodeId":"mac-1","citationsStatus":"absent"}""", Json)!;
        Assert.True(minimal is { Citations: null, SearchState: null, Device: null, App: null, SentAt: null, CompletedAt: null, ScreenshotSha256: null, TaskId: null });

        Assert.Equal("{\"disposition\":\"accepted\",\"sampleId\":\"" + Id + "\"}",
            JsonSerializer.Serialize(new AppResultResponse(CollectorResultDisposition.Accepted, Id), Json));
        Assert.Equal("""{"disposition":"stale","sampleId":null}""", JsonSerializer.Serialize(new AppResultResponse(CollectorResultDisposition.Stale, null), Json));
        Assert.Equal("""{"disposition":"duplicate","sampleId":null}""", JsonSerializer.Serialize(new AppResultResponse(CollectorResultDisposition.Duplicate, null), Json));
        Assert.Equal(["read", "absent", "unreadable", "not_declared"], AppContractNames.All<AppCitationsStatus>());
        Assert.Equal(["on", "off", "unknown"], AppContractNames.All<AppVisibleState>());
    }

    [Fact]
    public async Task TheControllerChecksTheShapeAndPassesTheTokensNodeToTheStore()
    {
        var f = new Fixture();
        var valid = Request(f.Node);
        async Task Refused(AppResultRequest request) => AssertError((await f.Controller.Result(request, default)).Result, 400, AppResultRules.InvalidCode);
        await Refused(valid with { TaskId = "not-a-uuid" });
        await Refused(valid with { LeaseVersion = 0 });
        await Refused(valid with { SentAt = null });
        await Refused(valid with { CompletedAt = null });
        // Exact wire names only.
        foreach (var status in new[] { "READ", " read", "partial", "", null }) await Refused(valid with { CitationsStatus = status });
        await Refused(valid with { SearchState = new("ON", "off") });
        await Refused(valid with { SearchState = new("on", null) });
        await Refused(valid with { Device = null });
        await Refused(valid with { App = null });
        await Refused(valid with { Citations = [null!] });
        Assert.DoesNotContain(f.Calls, call => call.Method != "AuthenticateNodeAsync");

        var accepted = Assert.IsType<OkObjectResult>((await f.Controller.Result(valid, default)).Result);
        Assert.Equal("{\"disposition\":\"accepted\",\"sampleId\":\"" + Id + "\"}", JsonSerializer.Serialize(accepted.Value, Json));
        var command = f.Last<AppResultCommand>();
        Assert.Equal(("mac-1", Id, 2, "result-key-0001", "rev-1", Sent, Sent.AddSeconds(42), "推荐三款记账工具。"), (command.NodeId, command.TaskId,
            command.LeaseVersion, command.IdempotencyKey, command.CollectorBuildRevision, command.SentAt, command.CompletedAt, command.AnswerText));
        Assert.Equal([new AppResultCitation("记账工具横评", "少数派", "https://sspai.com/post/1", "cited"), new AppResultCitation("知乎回答", "知乎", null, null)],
            command.Citations);
        Assert.Equal((AppCitationsStatus.Read, null, AppVisibleState.On, AppVisibleState.Off, "记账 工具 推荐"),
            (command.CitationsStatus, command.CitationsIssue, command.WebSearch, command.DeepThinking, Assert.Single(command.SearchQueries!)));
        Assert.Equal(("AI_Apps_4KB", "15", "arm64-v8a", "com.larus.nova", "15.7.0", Sha),
            (command.DeviceId, command.AndroidVersion, command.Abi, command.AppPackage, command.AppVersion, command.ScreenshotSha256));

        // No searchState: both switches unknown, the queries not submitted. No citations member: an empty list.
        Assert.IsType<OkObjectResult>((await f.Controller.Result(valid with { SearchState = null, Citations = null, CitationsStatus = "absent" }, default)).Result);
        command = f.Last<AppResultCommand>();
        Assert.Equal((AppVisibleState.Unknown, AppVisibleState.Unknown, null, 0), (command.WebSearch, command.DeepThinking, command.SearchQueries, command.Citations.Count));

        f.Submitted = new(CollectorResultDisposition.Stale, null);
        Assert.Equal("""{"disposition":"stale","sampleId":null}""",
            JsonSerializer.Serialize(Assert.IsType<OkObjectResult>((await f.Controller.Result(valid, default)).Result).Value, Json));
        foreach (var (code, status) in new[] { (AppResultRules.IdempotencyConflictCode, 409), (AppResultRules.EchoCode, 400), ("TASK_NOT_FOUND", 404),
            (AppResultRules.InvalidCode, 400), ("APP_CHANNEL_SCHEMA_NOT_MIGRATED", 503) })
        {
            f.StoreError = new CollectorStoreException(code, "m", status);
            AssertError((await f.Controller.Result(valid, default)).Result, status, code);
        }
    }

    [Fact]
    public void ValidationEnforcesEveryLimitAndNormalizesTheCommand()
    {
        var valid = Command();
        Assert.Equal(valid.Citations, AppResultRules.Validate(valid).Citations);
        AssertInvalid(valid with { TaskId = Guid.Empty });
        AssertInvalid(valid with { LeaseVersion = 0 });
        AssertInvalid(valid with { NodeId = " " });
        foreach (var key in new[] { "1234567", new string('k', 192), null }) AssertInvalid(valid with { IdempotencyKey = key! });
        Assert.Equal(8, AppResultRules.Validate(valid with { IdempotencyKey = "12345678" }).IdempotencyKey.Length);
        foreach (var revision in new[] { "", "  ", new string('r', 129) }) AssertInvalid(valid with { CollectorBuildRevision = revision });
        AssertInvalid(valid with { SentAt = Sent.ToOffset(TimeSpan.FromHours(8)) });
        AssertInvalid(valid with { CompletedAt = Sent.ToOffset(TimeSpan.FromHours(8)) });

        // Answer: not blank, at most 1 MiB of UTF-8.
        foreach (var answer in new[] { "", " \n\t", null }) AssertInvalid(valid with { AnswerText = answer! });
        Assert.Equal(1_048_576, AppResultRules.MaxAnswerBytes);
        AppResultRules.Validate(valid with { AnswerText = new string('a', 1_048_576) });
        AssertInvalid(valid with { AnswerText = new string('a', 1_048_577) });
        AssertInvalid(valid with { AnswerText = new string('答', 349_526) }); // 1 048 578 bytes

        // Citations: 100 entries, title 1–512, site 255, url 2048, list label.
        AppResultCitation[] Many(int count) => Enumerable.Range(0, count).Select(i => new AppResultCitation($"T{i}", null, null, null)).ToArray();
        Assert.Equal(100, AppResultRules.Validate(valid with { Citations = Many(100) }).Citations.Count);
        AssertInvalid(valid with { Citations = Many(101) });
        AssertInvalid(valid with { Citations = null! });
        AssertInvalid(valid with { Citations = [null!] });
        AppResultCommand One(AppResultCitation citation) => valid with { Citations = [citation] };
        AppResultRules.Validate(One(new(new string('题', 512), new string('s', 255), "https://a.example/" + new string('u', 2030), "list_2")));
        foreach (var title in new[] { "", "   ", null, new string('题', 513) }) AssertInvalid(One(new(title!, null, null, null)));
        AssertInvalid(One(new("T", new string('s', 256), null, null)));
        AssertInvalid(One(new("T", null, "https://a.example/" + new string('u', 2031), null)));
        foreach (var list in new[] { "Cited", "1st", "a-b", new string('l', 21) }) AssertInvalid(One(new("T", null, null, list)));

        // citationsStatus read ⇔ citations non-empty; citationsIssue ⇔ unreadable.
        AssertInvalid(valid with { Citations = [] });
        foreach (var status in new[] { AppCitationsStatus.Absent, AppCitationsStatus.NotDeclared })
        {
            AssertInvalid(valid with { CitationsStatus = status });
            AppResultRules.Validate(valid with { CitationsStatus = status, Citations = [] });
            AssertInvalid(valid with { CitationsStatus = status, Citations = [], CitationsIssue = "driver_timeout" });
        }
        AssertInvalid(valid with { CitationsIssue = "driver_timeout" });
        AssertInvalid(valid with { CitationsStatus = AppCitationsStatus.Unreadable, Citations = [] });
        AssertInvalid(valid with { CitationsStatus = AppCitationsStatus.Unreadable, CitationsIssue = "driver_timeout" });
        AssertInvalid(valid with { CitationsStatus = AppCitationsStatus.Unreadable, Citations = [], CitationsIssue = "Bad Issue" });
        Assert.Equal("driver_timeout", AppResultRules.Validate(valid with { CitationsStatus = AppCitationsStatus.Unreadable, Citations = [],
            CitationsIssue = " driver_timeout " }).CitationsIssue);

        // Search queries: at most 32, each 1–500 characters after trimming; null means "not submitted".
        Assert.Null(AppResultRules.Validate(valid with { SearchQueries = null }).SearchQueries);
        Assert.Empty(AppResultRules.Validate(valid with { SearchQueries = [] }).SearchQueries!);
        Assert.Equal(32, AppResultRules.Validate(valid with { SearchQueries = Enumerable.Repeat(new string('词', 500), 32).ToArray() }).SearchQueries!.Count);
        AssertInvalid(valid with { SearchQueries = Enumerable.Repeat("词", 33).ToArray() });
        foreach (var query in new[] { "", "  ", null, new string('词', 501) }) AssertInvalid(valid with { SearchQueries = [query!] });

        // Device, App, screenshot hash.
        foreach (var device in new[] { "", new string('d', 129) }) AssertInvalid(valid with { DeviceId = device });
        foreach (var android in new[] { "", new string('1', 33) }) AssertInvalid(valid with { AndroidVersion = android });
        AssertInvalid(valid with { Abi = new string('a', 33) });
        foreach (var package in new[] { "", new string('p', 256) }) AssertInvalid(valid with { AppPackage = package });
        foreach (var version in new[] { "", new string('v', 65) }) AssertInvalid(valid with { AppVersion = version });
        foreach (var hash in new[] { "abc", Sha + "0", Sha[..63] + "g" }) AssertInvalid(valid with { ScreenshotSha256 = hash });

        // Normalization: trimmed, blank optional members become null, the hash is lower case.
        var normalized = AppResultRules.Validate(valid with
        {
            Citations = [new("  标题 ", " ", "", "  "), new(" T2 ", " 站点 ", " https://a.example/x ", " cited ")],
            SearchQueries = ["  词 "], Abi = " ", ScreenshotSha256 = Sha.ToUpperInvariant(), DeviceId = " AVD ", AppVersion = " 1.0 ",
        });
        Assert.Equal([new AppResultCitation("标题", null, null, null), new AppResultCitation("T2", "站点", "https://a.example/x", "cited")], normalized.Citations);
        Assert.Equal(("词", null, Sha, "AVD", "1.0"), (Assert.Single(normalized.SearchQueries!), normalized.Abi, normalized.ScreenshotSha256,
            normalized.DeviceId, normalized.AppVersion));
        Assert.Null(AppResultRules.Validate(valid with { ScreenshotSha256 = null }).ScreenshotSha256);
        Assert.Equal((100, 512, 255, 2048, 32, 500), (AppResultRules.MaxCitations, AppResultRules.MaxTitleLength, AppResultRules.MaxSiteLength,
            AppResultRules.MaxUrlLength, AppResultRules.MaxSearchQueries, AppResultRules.MaxSearchQueryLength));
    }

    [Theory]
    [InlineData("哪个记账工具好用？", "哪个记账工具好用？", true)]
    [InlineData("  哪个记账工具好用？\n", "哪个记账工具好用？", true)]
    [InlineData("哪个 记账\t工具　好用？", "哪个记账工具 好用？", true)]
    [InlineData("which app is best", "whichappisbest", true)]
    [InlineData("哪个记账工具好用", "哪个记账工具好用？", false)]
    [InlineData("Which app is best", "which app is best", false)]
    [InlineData("哪个记账工具好用？推荐随手记。", "哪个记账工具好用？", false)]
    [InlineData("   ", " ", false)]
    public void AnAnswerThatIsTheQuestionIgnoringWhitespaceIsAnEcho(string answer, string question, bool echo)
        => Assert.Equal(echo, AppResultRules.IsEcho(answer, question));

    [Fact]
    public void ThePayloadHashCoversTheAnswerAndTheCanonicalCitations()
    {
        AppResultCitation a = new("标题一", "站点", "https://a.example/1", "cited"), b = new("标题二", null, null, null), c = new("标题三", "站点", null, "more");
        var hash = AppResultRules.PayloadHash("回答", [a, b, c]);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        // The order of the citations does not matter.
        Assert.Equal(hash, AppResultRules.PayloadHash("回答", [c, a, b]));
        Assert.Equal(hash, AppResultRules.PayloadHash("回答", [b, c, a]));
        // A missing member, null, an empty string and blanks are the same; surrounding blanks are ignored.
        Assert.Equal(hash, AppResultRules.PayloadHash("回答", [a, new("标题二", "", " ", ""), new(" 标题三 ", " 站点", "  ", "more ")]));
        var bound = JsonSerializer.Deserialize<AppResultCitationRequest[]>("""
            [{"title":"标题一","site":"站点","url":"https://a.example/1","list":"cited"},{"title":"标题二"},{"title":"标题三","site":"站点","url":null,"list":"more"}]
            """, Json)!;
        Assert.Equal(hash, AppResultRules.PayloadHash("回答", bound.Select(x => new AppResultCitation(x.Title!, x.Site, x.Url, x.List)).ToArray()));
        // Any difference in the answer or in a citation changes it; a value does not move between members.
        Assert.NotEqual(hash, AppResultRules.PayloadHash("回答 ", [a, b, c]));
        Assert.NotEqual(hash, AppResultRules.PayloadHash("回答", [a, b]));
        Assert.NotEqual(hash, AppResultRules.PayloadHash("回答", [a, b, c, c]));
        Assert.NotEqual(hash, AppResultRules.PayloadHash("回答", [a, b, c with { List = "cited" }]));
        Assert.NotEqual(hash, AppResultRules.PayloadHash("回答", [a with { Url = "https://a.example/2" }, b, c]));
        Assert.NotEqual(AppResultRules.PayloadHash("回答", [new("T", "S", null, null)]), AppResultRules.PayloadHash("回答", [new("T", null, "S", null)]));
        Assert.NotEqual(AppResultRules.PayloadHash("回答", []), AppResultRules.PayloadHash("回答", [b]));
        // Pinned: a change of the canonical form would turn a retried submission into a conflict.
        Assert.Equal(PinnedHash, hash);
    }

    // SHA-256 of {"answerText":…,"citations":[{"list":…,"title":…,"site":…,"url":…},…]} with the citations sorted (null first)
    // and non-ASCII text written as \uXXXX; computed independently of the implementation.
    private const string PinnedHash = "21ff2f17852428b79839cf7a01f9c089800b74e57812b4fa3a97a0f96b9a03ea";

    [Fact]
    public void TheAppBlockHasTheDocumentedMembersAndOnlyUsableUrlsCountAsUrls()
    {
        var command = AppResultRules.Validate(Command() with
        {
            Citations =
            [
                new("带链接", "少数派", "https://www.sspai.com/post/1?x=1", "cited"), new("无链接", "知乎", null, "cited"), new("只有标题", null, null, null),
                new("不是网页地址", "微信", "weixin://article/1", "more"), new("本机地址", null, "http://localhost/x", null),
                new("第二个链接", null, "http://News.Example.com/a", "more"),
            ],
        });
        var (withUrl, json) = AppCollectorPoolStore.AppMetadata(command, "AI_Apps_4KB", "doubao-a", 1, 16_000);
        Assert.Equal(new[] { ("https://www.sspai.com/post/1?x=1", "sspai.com", "带链接"), ("http://news.example.com/a", "news.example.com", "第二个链接") }, withUrl);
        var app = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal(["deviceId", "androidVersion", "appPackage", "appVersion", "accountLabel", "webSearch", "deepThinking", "citationsStatus",
            "citationsIssue", "citationsTotal", "citationsWithUrl", "sourcesWithoutUrl", "sourcesWithoutUrlTruncated", "screenshotSha256", "promptRuleVersion"],
            app.EnumerateObject().Select(member => member.Name));
        // implement 7.2: the block says explicitly whether the list of URL-less sources was cut; here all four are kept.
        Assert.Equal(JsonValueKind.False, app.GetProperty(AppAnswerMetadataCodec.TruncatedMember).ValueKind);
        Assert.Equal("sourcesWithoutUrlTruncated", AppAnswerMetadataCodec.TruncatedMember);
        Assert.Equal(("AI_Apps_4KB", "15", "com.larus.nova", "15.7.0", "doubao-a", "on", "off", "read", JsonValueKind.Null, 6, 2, Sha, 1),
            (app.GetProperty("deviceId").GetString(), app.GetProperty("androidVersion").GetString(), app.GetProperty("appPackage").GetString(),
                app.GetProperty("appVersion").GetString(), app.GetProperty("accountLabel").GetString(), app.GetProperty("webSearch").GetString(),
                app.GetProperty("deepThinking").GetString(), app.GetProperty("citationsStatus").GetString(), app.GetProperty("citationsIssue").ValueKind,
                app.GetProperty("citationsTotal").GetInt32(), app.GetProperty("citationsWithUrl").GetInt32(), app.GetProperty("screenshotSha256").GetString(),
                app.GetProperty("promptRuleVersion").GetInt32()));
        // Title, site and list only: an unusable URL is not kept, and nothing is guessed.
        Assert.Equal("""[{"title":"无链接","site":"知乎","list":"cited"},{"title":"只有标题","site":null,"list":null},"""
            + """{"title":"不是网页地址","site":"微信","list":"more"},{"title":"本机地址","site":null,"list":null}]""",
            app.GetProperty("sourcesWithoutUrl").GetRawText());

        // No sources at all, no screenshot, an unreadable list.
        var empty = JsonSerializer.Deserialize<JsonElement>(AppCollectorPoolStore.AppMetadata(AppResultRules.Validate(Command() with
        {
            Citations = [], CitationsStatus = AppCitationsStatus.Unreadable, CitationsIssue = "driver_timeout", ScreenshotSha256 = null,
            WebSearch = AppVisibleState.Unknown,
        }), "AVD", "kimi-a", 3, 16_000).Metadata);
        Assert.Equal(("unreadable", "driver_timeout", 0, 0, 0, JsonValueKind.Null, "unknown", 3), (empty.GetProperty("citationsStatus").GetString(),
            empty.GetProperty("citationsIssue").GetString(), empty.GetProperty("citationsTotal").GetInt32(), empty.GetProperty("citationsWithUrl").GetInt32(),
            empty.GetProperty("sourcesWithoutUrl").GetArrayLength(), empty.GetProperty("screenshotSha256").ValueKind,
            empty.GetProperty("webSearch").GetString(), empty.GetProperty("promptRuleVersion").GetInt32()));
    }

    [Fact]
    public void SourcesWithoutUrlAreCutFromTheEndToFitTheColumnAndTheCountsStayComplete()
    {
        // 100 sources with 512-character Chinese titles are about 160 KB; the column allows 16 384 bytes.
        var command = AppResultRules.Validate(Command() with
        {
            Citations = Enumerable.Range(0, 100).Select(i => new AppResultCitation($"{i:000}" + new string('题', 509), "站点名称", null, "cited")).ToArray(),
        });
        var (withUrl, json) = AppCollectorPoolStore.AppMetadata(command, "AI_Apps_4KB", "doubao-a", 1, 16_000);
        Assert.Empty(withUrl);
        Assert.InRange(AppCollectorPoolStore.StoredJsonBytes(json), 12_000, 16_000 - 9);
        var app = JsonSerializer.Deserialize<JsonElement>(json);
        var kept = app.GetProperty("sourcesWithoutUrl").EnumerateArray().Select(source => source.GetProperty("title").GetString()![..3]).ToArray();
        Assert.InRange(kept.Length, 5, 15);
        Assert.Equal(Enumerable.Range(0, kept.Length).Select(i => $"{i:000}"), kept);
        Assert.Equal((100, 0), (app.GetProperty("citationsTotal").GetInt32(), app.GetProperty("citationsWithUrl").GetInt32()));
        // implement 7.2: the cut is marked in the block itself.
        Assert.True(app.GetProperty("sourcesWithoutUrlTruncated").GetBoolean());
        // Short titles: all 100 fit. A smaller budget (other content already on the row) keeps fewer.
        var small = AppResultRules.Validate(Command() with { Citations = Enumerable.Range(0, 100).Select(i => new AppResultCitation($"来源 {i}", "站点", null, null)).ToArray() });
        var whole = JsonSerializer.Deserialize<JsonElement>(AppCollectorPoolStore.AppMetadata(small, "AVD", "a", 1, 16_000).Metadata);
        Assert.Equal(100, whole.GetProperty("sourcesWithoutUrl").GetArrayLength());
        Assert.False(whole.GetProperty("sourcesWithoutUrlTruncated").GetBoolean());
        var tight = JsonSerializer.Deserialize<JsonElement>(AppCollectorPoolStore.AppMetadata(small, "AVD", "a", 1, 2_000).Metadata);
        Assert.InRange(tight.GetProperty("sourcesWithoutUrl").GetArrayLength(), 10, 40);
        Assert.True(tight.GetProperty("sourcesWithoutUrlTruncated").GetBoolean());
        // A source with a URL is a citation, not a missing source: one URL-less source of two, kept, is not a cut.
        var mixed = AppResultRules.Validate(Command() with
        {
            Citations = [new AppResultCitation("带链接", null, "https://a.example.com/1", null), new AppResultCitation("无链接", null, null, null)],
        });
        Assert.False(JsonSerializer.Deserialize<JsonElement>(AppCollectorPoolStore.AppMetadata(mixed, "AVD", "a", 1, 16_000).Metadata)
            .GetProperty("sourcesWithoutUrlTruncated").GetBoolean());
        Assert.Equal(100, AppCollectorPoolStore.MaxSourcesWithoutUrl);
        // The estimate counts MySQL's blank after every comma and colon and never escapes non-ASCII text.
        Assert.Equal(Encoding.UTF8.GetByteCount("""{"a": "题", "b": [1, 2]}"""), AppCollectorPoolStore.StoredJsonBytes("""{"a":"题","b":[1,2]}"""));
    }

    // The App intake cannot call the web intake's private rule; this keeps the copy identical.
    [Theory]
    [InlineData("https://www.sspai.com/post/1?x=1#top")]
    [InlineData("http://News.Example.com/a")]
    [InlineData("https://example.com.")]
    [InlineData("HTTPS://EXAMPLE.COM/路径")]
    [InlineData("weixin://article/1")]
    [InlineData("ftp://example.com/x")]
    [InlineData("https://user:pw@example.com/x")]
    [InlineData("http://localhost/x")]
    [InlineData("http://printer.local/x")]
    [InlineData("http://a.localhost/x")]
    [InlineData("http://svc.internal/x")]
    [InlineData("http://svc.intranet/x")]
    [InlineData("http://x.invalid/")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://intranet/x")]
    [InlineData("example.com/abc")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public void TheUrlRuleIsTheWebIntakesRule(string value)
    {
        var web = typeof(DomesticCollectorStore).GetMethod("NormalizeUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(((string, string)?)web.Invoke(null, [value]), AppCollectorPoolStore.NormalizeCitationUrl(value));
        var tooLong = "https://example.com/" + new string('a', 731);
        Assert.Equal(((string, string)?)web.Invoke(null, [tooLong]), AppCollectorPoolStore.NormalizeCitationUrl(tooLong));
        Assert.Null(AppCollectorPoolStore.NormalizeCitationUrl(tooLong));
        Assert.NotNull(AppCollectorPoolStore.NormalizeCitationUrl(tooLong[..750]));
    }
}
