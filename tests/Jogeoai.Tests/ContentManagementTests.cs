using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>内容管理新增边界的定向单元测试。</summary>
public sealed class ContentManagementTests
{
    [Fact]
    public void SourceReaderNormalizesTrackingParametersAndRejectsUserInfo()
    {
        var normalized = ArticleSourceReader.NormalizeAndValidate("https://example.com/a?utm_source=test&x=1#fragment");
        Assert.Equal("https://example.com/a?x=1", normalized);
        var exception = Assert.Throws<CollectorStoreException>(() => ArticleSourceReader.NormalizeAndValidate("https://user:pass@example.com/a"));
        Assert.Equal("INVALID_SOURCE_URL", exception.Code);
    }

    [Fact]
    public void SourceReaderFetchUrlKeepsWwwHost()
    {
        Assert.Equal("https://www.szulink.com.cn/zh/products/x?a=1",
            ArticleSourceReader.NormalizeFetchUrl("https://WWW.szulink.com.cn/zh/products/x?utm_source=t&a=1#top"));
        Assert.Equal("https://example.com/a", ArticleSourceReader.NormalizeFetchUrl("https://example.com/a"));
        Assert.Equal("INVALID_SOURCE_URL",
            Assert.Throws<CollectorStoreException>(() => ArticleSourceReader.NormalizeFetchUrl("ftp://example.com/a")).Code);
    }

    [Fact]
    public async Task SourceReaderFollowsRedirectToExactLocationAndReportsDedupeKey()
    {
        // 裸域名 301 到 www 的站点：跳转目标必须原样抓取，而去重键仍使用去 www 的规范化地址。
        var requested = new List<string>();
        var factory = new RedirectFactory(requested, "http://8.8.8.8/a", "http://8.8.4.4/b");
        var reader = new ArticleSourceReader(factory);
        var result = await reader.ReadAsync("http://8.8.8.8/a");
        Assert.Equal("ready", result.ReadStatus);
        Assert.Equal(["http://8.8.8.8/a", "http://8.8.4.4/b"], requested);
        Assert.Equal(["http://8.8.8.8/a", "http://8.8.4.4/b"], result.RedirectChain);
        Assert.Equal("http://8.8.4.4/b", result.UrlNormalized);
        Assert.Contains("正文", result.BodyMarkdown);
    }

    private sealed class RedirectFactory(List<string> requested, string from, string to) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(requested, from, to));
        private sealed class Handler(List<string> requested, string from, string to) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                requested.Add(request.RequestUri!.AbsoluteUri);
                if (request.RequestUri.AbsoluteUri == from)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri(to) } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("<html><title>t</title><body><p>这是正文内容。</p></body></html>", Encoding.UTF8, "text/html") });
            }
        }
    }

    [Fact]
    public void SourceReaderRejectsPrivateAndMetadataAddresses()
    {
        Assert.False(ArticleSourceReader.IsPublicAddress(IPAddress.Parse("127.0.0.1")));
        Assert.False(ArticleSourceReader.IsPublicAddress(IPAddress.Parse("169.254.169.254")));
        Assert.False(ArticleSourceReader.IsPublicAddress(IPAddress.Parse("192.168.1.10")));
        Assert.True(ArticleSourceReader.IsPublicAddress(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void ArticlePromptSeparatesEvidenceFromUntrustedReferenceAndNotes()
    {
        var task = new ArticleGenerationWorkItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "question snapshot", "target snapshot", null, null, null, "重点", "zh-CN", "answer_guide", "medium", "professional", null, [], null, null, null, false, 1, "test", 100);
        var evidence = new KnowledgeEvidenceView("evidence-1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "资料", "可信事实", "site", "a.md", "line:1", 0.9, null, null);
        var reference = new ArticlePublicationLinkView(Guid.NewGuid(), Guid.NewGuid(), null, "https://example.com", "https://example.com", "hash", "manual", null, ArticlePublicationStatus.Active, null, "ready", DateTimeOffset.UtcNow);
        var prompt = ArticleGenerationProvider.BuildPrompt(task, [evidence], [reference]);
        Assert.Contains("TRUSTED_EVIDENCE", prompt.User, StringComparison.Ordinal);
        Assert.Contains("UNTRUSTED_CONTENT", prompt.User, StringComparison.Ordinal);
        Assert.Contains("MATERIAL_NOTES", prompt.User, StringComparison.Ordinal);
        Assert.Contains("evidence-1", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void ArticleProviderValidationRejectsUnknownEvidenceAndUnsafeMarkdown()
    {
        var task = new ArticleGenerationWorkItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "question", "target", null, null, null, null, "zh-CN", "answer_guide", "medium", "professional", null, [], null, null, null, false, 1, "test", 100);
        var payload = new ArticleGenerationProvider.ArticleGenerationResultPayload("标题", null, "<script>alert(1)</script>", null, null, [], true, ["missing"], [], "披露");
        var exception = Assert.Throws<CollectorStoreException>(() => ArticleGenerationProvider.Validate(payload, task, new HashSet<string> { "known" }, "{}"));
        Assert.Equal("ARTICLE_GENERATION_SCHEMA_INVALID", exception.Code);
    }

    [Fact]
    public async Task ArticleProviderUsesFixedLunaModelAndNeverPlacesCredentialInPrompt()
    {
        var response = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content = JsonSerializer.Serialize(new
                {
                    title = "文章标题", summary = "摘要", bodyMarkdown = new string('答', 1600),
                    seoTitle = "文章标题", seoDescription = "说明", keywords = new[] { "问题" },
                    answeredQuestion = true, evidenceIds = new[] { "known" }, uncertainties = Array.Empty<string>(), disclosure = "AI 辅助生成"
                }) } }
            }
        });
        var factory = new RecordingHttpClientFactory(response);
        var provider = new ArticleGenerationProvider(factory);
        var task = new ArticleGenerationWorkItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "主问题", "target", null, null, null, null, "zh-CN", "answer_guide", "medium", "professional", null, [], null, null, null, false, 1, "test", 100);
        var evidence = new KnowledgeEvidenceView("known", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "资料", "事实", "site", "a.md", "line:1", 0.9, null, null);

        var result = await provider.GenerateAsync(task,
            new ResearchProviderConfiguration(ArticleGenerationProvider.ProviderName, "attacker-model", "https://llm.test/v1/chat/completions", "secret-token", false, 30, 1),
            [evidence], []);

        Assert.Equal("deepseek-flash", result.Model);
        Assert.NotNull(factory.RequestBody);
        using var request = JsonDocument.Parse(factory.RequestBody!);
        Assert.Equal("deepseek-flash", request.RootElement.GetProperty("model").GetString());
        Assert.Equal(16_000, request.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("disabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.DoesNotContain("secret-token", request.RootElement.GetProperty("messages").GetRawText(), StringComparison.Ordinal);
        Assert.Equal("Bearer secret-token", factory.Authorization);
    }

    [Fact]
    public void SourceReaderNamedHandlerDisablesProxyAndAutomaticRedirects()
    {
        using var handler = ArticleSourceReader.CreatePinnedHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
        Assert.False(ArticleSourceReader.IsPublicAddress(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(ArticleSourceReader.IsPublicAddress(IPAddress.Parse("192.0.2.1")));
    }

    [Fact]
    public void ArticleMonitoringCursorCarriesSnapshotTokenAcrossPages()
    {
        var token = new string('a', 64);
        var cursor = new ArticleMonitoringQuery.MonitoringCursor(token, "all", 123,
            Guid.NewGuid(), Guid.NewGuid());

        var encoded = ArticleMonitoringQuery.EncodeCursor(cursor);

        Assert.Equal(token, ArticleMonitoringQuery.ReadSnapshotToken(encoded));
        var decoded = ArticleMonitoringQuery.DecodeCursor(encoded, token, "all");
        Assert.NotNull(decoded);
        Assert.Equal(cursor, decoded);
        Assert.Throws<CollectorStoreException>(() => ArticleMonitoringQuery.DecodeCursor(encoded, new string('b', 64), "all"));

        var versioned = ArticleMonitoringQuery.EncodeCursor(cursor with { UrlVersion = "url-v1" });
        Assert.Equal("url-v1", ArticleMonitoringQuery.DecodeCursor(versioned, token, "all", "url-v1")!.UrlVersion);
        Assert.Throws<CollectorStoreException>(() => ArticleMonitoringQuery.DecodeCursor(versioned, token, "all", "url-v2"));
        Assert.Throws<CollectorStoreException>(() => ArticleMonitoringQuery.DecodeCursor(encoded, token, "all", "url-v1"));

        var scoped = ArticleMonitoringQuery.EncodeCursor(cursor with { UrlVersion = "url-v1", PublicationScope = "scope-a" });
        Assert.Equal("scope-a", ArticleMonitoringQuery.DecodeCursor(scoped, token, "all", "url-v1", "scope-a")!.PublicationScope);
        Assert.Throws<CollectorStoreException>(() => ArticleMonitoringQuery.DecodeCursor(scoped, token, "all", "url-v1", "scope-b"));

        var versionedScope = ArticleMonitoringQuery.EncodeCursor(cursor with
        {
            UrlVersion = "url-v1", PublicationScope = "scope-a", ScopeVersion = "scope-v1"
        });
        Assert.Equal("scope-v1", ArticleMonitoringQuery.DecodeCursor(versionedScope, token, "all", "url-v1", "scope-a", "scope-v1")!.ScopeVersion);
        Assert.Throws<CollectorStoreException>(() => ArticleMonitoringQuery.DecodeCursor(versionedScope, token, "all", "url-v1", "scope-a", "scope-v2"));
    }

    [Fact]
    public void MonitorScopeTransitionsKeepHistoryAndExcludePausedIntervals()
    {
        var enabled = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        var paused = enabled.AddHours(2);
        var resumed = enabled.AddHours(5);

        var first = ContentStore.TransitionMonitorScope(null, ArticleMonitorStatus.Active, enabled);
        var second = ContentStore.TransitionMonitorScope(first.Snapshot, ArticleMonitorStatus.Paused, paused);
        var third = ContentStore.TransitionMonitorScope(second.Snapshot, ArticleMonitorStatus.Active, resumed);

        using var document = JsonDocument.Parse(third.Snapshot);
        var windows = document.RootElement.GetProperty("activeWindows").EnumerateArray().ToArray();
        Assert.Equal(2, windows.Length);
        Assert.Equal("2026-09-29 00:00:00.000000", windows[0].GetProperty("from").GetString());
        Assert.Equal("2026-09-29 02:00:00.000000", windows[0].GetProperty("to").GetString());
        Assert.Equal("2026-09-29 05:00:00.000000", windows[1].GetProperty("from").GetString());
        Assert.True(windows[1].GetProperty("to").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public void ContentMigrationContainsTenantIsolationAndCreditIdempotencyKeys()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "db", "migrations", "014_content_management.sql"));
        Assert.Contains("content_articles", sql, StringComparison.Ordinal);
        Assert.Contains("credit_accounts", sql, StringComparison.Ordinal);
        Assert.Contains("uq_credit_reservations_task", sql, StringComparison.Ordinal);
        Assert.Contains("uq_credit_reservations_tenant_id", sql, StringComparison.Ordinal);
        Assert.Contains("uq_article_generation_batches_tenant_id", sql, StringComparison.Ordinal);
        Assert.Contains("tenant_id, idempotency_key", sql, StringComparison.Ordinal);
        Assert.Contains("fk_content_articles_tenant_site", sql, StringComparison.Ordinal);
        Assert.Contains("fk_content_publication_version", sql, StringComparison.Ordinal);
        Assert.Contains("content_monitor_scopes", sql, StringComparison.Ordinal);
        Assert.Contains("enabled_at", sql, StringComparison.Ordinal);
        Assert.Contains("disabled_at", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, null, 3L, ArticleMonitorStatus.Active, ArticleStage.Archived)]
    [InlineData(true, false, ArticleGenerationTaskStatus.Running, 0L, ArticleMonitorStatus.Inactive, ArticleStage.Archived)]
    [InlineData(false, false, ArticleGenerationTaskStatus.Pending, 0L, ArticleMonitorStatus.Inactive, ArticleStage.Generating)]
    [InlineData(false, false, ArticleGenerationTaskStatus.Running, 0L, ArticleMonitorStatus.Inactive, ArticleStage.Generating)]
    [InlineData(false, false, ArticleGenerationTaskStatus.Failed, 0L, ArticleMonitorStatus.Inactive, ArticleStage.GenerationFailed)]
    [InlineData(false, false, ArticleGenerationTaskStatus.Cancelled, 0L, ArticleMonitorStatus.Inactive, ArticleStage.GenerationFailed)]
    [InlineData(false, false, ArticleGenerationTaskStatus.Stale, 0L, ArticleMonitorStatus.Inactive, ArticleStage.GenerationFailed)]
    [InlineData(false, true, ArticleGenerationTaskStatus.Succeeded, 0L, ArticleMonitorStatus.Inactive, ArticleStage.PendingPublish)]
    [InlineData(false, true, ArticleGenerationTaskStatus.Succeeded, 0L, ArticleMonitorStatus.Active, ArticleStage.PendingPublish)]
    [InlineData(false, true, ArticleGenerationTaskStatus.Succeeded, 1L, ArticleMonitorStatus.Inactive, ArticleStage.Publishing)]
    [InlineData(false, true, ArticleGenerationTaskStatus.Succeeded, 2L, ArticleMonitorStatus.Paused, ArticleStage.Publishing)]
    [InlineData(false, true, ArticleGenerationTaskStatus.Succeeded, 1L, ArticleMonitorStatus.Active, ArticleStage.Tracking)]
    // Import leftovers (no version, no task) are never reported as a failed generation.
    [InlineData(false, false, null, 0L, ArticleMonitorStatus.Inactive, ArticleStage.PendingPublish)]
    [InlineData(false, false, null, 1L, ArticleMonitorStatus.Active, ArticleStage.Tracking)]
    // A current version wins over a later pending task.
    [InlineData(false, true, ArticleGenerationTaskStatus.Pending, 0L, ArticleMonitorStatus.Inactive, ArticleStage.PendingPublish)]
    public void ArticleStageDerivationFollowsPrdOrder(bool archived, bool hasVersion, ArticleGenerationTaskStatus? task,
        long activeLinks, ArticleMonitorStatus monitor, ArticleStage expected)
        => Assert.Equal(expected, ArticleStageRules.Derive(archived, hasVersion, task, activeLinks, monitor));

    [Fact]
    public void ArticleStageKeysMatchJsonNamesAndSqlCaseOrder()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var stages = Enum.GetValues<ArticleStage>();
        Assert.Equal(6, stages.Length);
        Assert.Equal(stages.Select(stage => JsonSerializer.Serialize(stage, options).Trim('"')), ArticleStageRules.Keys);
        foreach (var stage in stages)
        {
            Assert.True(ArticleStageRules.TryParse(ArticleStageRules.Key(stage), out var parsed));
            Assert.Equal(stage, parsed);
        }
        Assert.False(ArticleStageRules.TryParse("PendingPublish", out _));

        // The SQL CASE must evaluate the branches in exactly the Derive order.
        var sql = ContentStore.ArticleStageSql;
        var order = new[] { "'archived'", "'generating'", "'generation_failed'", "'pending_publish'", "'tracking'", "ELSE 'publishing'" }
            .Select(token => sql.IndexOf(token, StringComparison.Ordinal)).ToArray();
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.Order().ToArray(), order);
    }

    [Fact]
    public void ArticlePromptSummaryReadsWebSerializedPromptSnapshot()
    {
        var groupId = Guid.NewGuid();
        var prompt = new PromptView(Guid.NewGuid(), "哪款扫地机器人适合养宠家庭？", 3, "zh-CN", true, [], "unbranded", "manual",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, GroupId: groupId);
        var snapshot = JsonSerializer.Serialize(prompt, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // The list SQL extracts these exact JSON paths from prompt_snapshot.
        using (var document = JsonDocument.Parse(snapshot))
        {
            foreach (var path in new[] { ContentStore.PromptIdJsonPath, ContentStore.PromptContentJsonPath,
                         ContentStore.PromptLocaleJsonPath, ContentStore.PromptGroupIdJsonPath })
                Assert.True(document.RootElement.TryGetProperty(path["$.".Length..], out _), path);
        }

        var summary = ContentStore.ReadPromptSummary(snapshot);
        Assert.Equal(new ArticlePromptSummary(prompt.PromptId, prompt.Content, "zh-CN", groupId), summary);
        var ungrouped = ContentStore.ReadPromptSummary(JsonSerializer.Serialize(prompt with { GroupId = null },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Null(ungrouped!.GroupId);
        Assert.Null(ContentStore.ReadPromptSummary(null));
        Assert.Null(ContentStore.ReadPromptSummary("not json"));
    }

    [Theory]
    [InlineData("https://acme.com/a", false, true)]
    [InlineData("https://www.acme.com/a?utm_source=x", false, true)]
    [InlineData("https://ACME.com/a", false, true)]
    [InlineData("https://acme.cn/news/1", false, true)]
    [InlineData("https://www.acme.cn/news/1", false, true)]
    [InlineData("https://blog.acme.com/post", false, false)]
    [InlineData("https://blog.acme.com/post", true, true)]
    [InlineData("https://www.blog.acme.com/post", true, true)]
    [InlineData("https://notacme.com/a", true, false)]
    [InlineData("https://acme.com.evil.io/a", true, false)]
    [InlineData("https://zhihu.com/p/1", true, false)]
    public void PublicationOwnershipUsesCanonicalBrandDomainsAndSubdomainFlag(string url, bool includeSubdomains, bool expected)
    {
        // Brand domains are canonicalised (one leading www. removed) like the citation ownership rule.
        var brand = Brand("www.acme.com", ["www.acme.cn"], includeSubdomains);
        var normalized = ArticleSourceReader.NormalizeAndValidate(url);
        Assert.Equal(expected, ContentStore.ComputeOwnership(normalized, brand));
    }

    [Fact]
    public void PublicationOwnershipIsUnknownWithoutBrandDomains()
    {
        var normalized = ArticleSourceReader.NormalizeAndValidate("https://acme.com/a");
        Assert.Null(ContentStore.ComputeOwnership(normalized, null));
        Assert.Null(ContentStore.ComputeOwnership(normalized, Brand("", [], true)));
    }

    [Fact]
    public void ReferenceSnapshotRoundTripsWithoutBodiesInSummary()
    {
        var references = new[]
        {
            new ContentStore.ReferenceSnapshotItem("https://example.org/guide", "ready", "范文一", "正文一"),
            new ContentStore.ReferenceSnapshotItem("https://example.net/post", "ready", null, null)
        };
        var json = ContentStore.SerializeReferenceSnapshot(references);
        var restored = ContentStore.DeserializeReferenceSnapshot(json);
        Assert.Equal(references, restored);
        Assert.Empty(ContentStore.DeserializeReferenceSnapshot(null));
        Assert.Empty(ContentStore.DeserializeReferenceSnapshot(""));
        Assert.Empty(ContentStore.DeserializeReferenceSnapshot("{broken"));
    }

    [Fact]
    public void FrozenKnowledgeHitCountMatchesWorkerSnapshotFormat()
    {
        var evidence = new KnowledgeEvidenceView("e1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "资料", "事实", "site", "a.md", "line:1", 0.9, null, null);
        var context = new KnowledgeContextResponse(Guid.NewGuid(), [evidence, evidence with { EvidenceId = "e2" }], [], [],
            KnowledgeScopeMode.Default, 10);
        // Submit stores the context with default (PascalCase) options; the Worker reads it case-insensitively.
        var snapshot = JsonSerializer.Serialize(context);
        Assert.Equal(2, ContentStore.CountFrozenKnowledgeHits(snapshot, withoutRag: false));
        Assert.Equal(0, ContentStore.CountFrozenKnowledgeHits(snapshot, withoutRag: true));
        Assert.Equal(0, ContentStore.CountFrozenKnowledgeHits(null, withoutRag: false));
        Assert.Equal(0, ContentStore.CountFrozenKnowledgeHits("[not an object", withoutRag: false));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("False", false)]
    [InlineData("", false)]
    public void ContentBooleanColumnsAcceptMySqlConnectorBooleanText(string value, bool expected)
        => Assert.Equal(expected, ContentStore.ParseBool(value));

    [Fact]
    public void DomainRankingRegisteredCountsGroupActiveLinksByCanonicalHost()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var links = new (Guid, string, string)[]
        {
            (first, ArticleSourceReader.NormalizeAndValidate("https://www.zhihu.com/p/1"), "active"),
            (first, ArticleSourceReader.NormalizeAndValidate("https://zhihu.com/p/2"), "active"),
            (second, ArticleSourceReader.NormalizeAndValidate("https://ZHIHU.com/p/3"), "active"),
            (second, ArticleSourceReader.NormalizeAndValidate("https://zhihu.com/p/4"), "disabled"),
            (second, ArticleSourceReader.NormalizeAndValidate("https://zhuanlan.zhihu.com/p/5"), "active"),
            (second, ArticleSourceReader.NormalizeAndValidate("https://csdn.net/a"), "disabled")
        };
        var registered = ArticleMonitoringQuery.RegisteredByCanonicalHost(links);
        Assert.Equal((2L, 3L), registered["zhihu.com"]);
        Assert.Equal((1L, 1L), registered["zhuanlan.zhihu.com"]);
        Assert.False(registered.ContainsKey("csdn.net"));
        Assert.False(registered.ContainsKey("www.zhihu.com"));
    }

    [Theory]
    [InlineData("https://WWW.Zhihu.com/question/1?utm_source=feed&a=1#answer")]
    [InlineData("https://www.iesdouyin.com/share/video/7400000000000000000/?region=CN&mid=1&u_code=abc")]
    [InlineData("http://szulink.com.cn/zh/products/x?gclid=t")]
    public void OwnedUrlHashEqualsTheCitationUrlBoardHashForRawAndRowUrls(string rawUrl)
    {
        // The /urls row is built from the url-v2 writer key: citation_events.normalized_url and its UTF-8 SHA-256.
        var fact = CitationDomainProjectionTests.Fact();
        var accumulator = new CitationDomainAccumulator([fact], CitationTestConfig(), DetailedAnalyticsRules.UrlV2);
        accumulator.Add(new CitationDomainEvent(fact.CheckId, DetailedAnalyticsRules.NormalizeUrl(rawUrl, DetailedAnalyticsRules.UrlV2)!, 1));
        var row = Assert.Single(accumulator.Complete().Urls);

        // The frontend marks with the row's url; a raw pasted URL lands on the same hash too.
        var fromRow = ContentStore.OwnedUrlKey(row.Url);
        Assert.Equal(row.UrlHash, fromRow.Hash);
        Assert.Equal(row.Url, fromRow.Normalized);
        Assert.Equal(row.UrlHash, ContentStore.OwnedUrlKey(rawUrl).Hash);
        Assert.Equal(row.UrlHash, CitationDomainAccumulator.UrlHash(fromRow.Normalized));
        // Unmark accepts the row hash in any case and normalizes it to the stored lower-case form.
        Assert.Equal(row.UrlHash, ContentStore.NormalizeOwnedUrlHash(" " + row.UrlHash.ToUpperInvariant() + " "));
    }

    [Fact]
    public async Task OwnedUrlMarkAndUnmarkValidateInputBeforeDatabaseAccess()
    {
        var store = new ContentStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"),
            null!, null!, null!, null!, null!, null!);
        foreach (var url in new[] { "", "   ", "ftp://zhihu.com/a", "https://user:pass@zhihu.com/a", "not a url" })
            Assert.Equal("INVALID_SOURCE_URL", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.MarkOwnedUrlAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), url, null))).Code);
        foreach (var hash in new[] { "", "abc", new string('g', 64), new string('a', 63), new string('a', 65), "https://zhihu.com/a" })
        {
            var exception = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.UnmarkOwnedUrlAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), hash));
            Assert.Equal(("INVALID_URL_HASH", 400), (exception.Code, exception.StatusCode));
        }
    }

    [Fact]
    public void OwnedUrlTitleFallsBackToTheUrlAndIsTrimmedToFiveHundredCharacters()
    {
        Assert.Equal("https://zhihu.com/q/1", ContentStore.OwnedUrlTitle(null, "https://zhihu.com/q/1"));
        Assert.Equal("https://zhihu.com/q/1", ContentStore.OwnedUrlTitle("   ", "https://zhihu.com/q/1"));
        Assert.Equal("管道检测指南", ContentStore.OwnedUrlTitle("  管道检测指南 ", "https://zhihu.com/q/1"));
        Assert.Equal(500, ContentStore.OwnedUrlTitle(new string('标', 600), "https://zhihu.com/q/1").Length);
        // Not fetched: the reference keeps the same read status as an unreadable import, never "pending" (title reader queue).
        Assert.Equal("unreadable", ContentStore.OwnedUrlReadStatus);
    }

    private static SubjectConfiguration CitationTestConfig() => new(Guid.NewGuid(), 1, [], [], true, false);

    private static BrandView Brand(string primary, IReadOnlyList<string> additional, bool includeSubdomains)
        => new(Guid.NewGuid(), "Acme", primary, [], additional, "CN", "zh-CN", "Asia/Shanghai", "active", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IncludeSubdomains: includeSubdomains);

    private sealed class RecordingHttpClientFactory(string body) : IHttpClientFactory
    {
        public string? RequestBody { get; private set; }
        public string? Authorization { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(this, body));

        private sealed class Handler(RecordingHttpClientFactory owner, string body) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                owner.RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                owner.Authorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body),
                    RequestMessage = request,
                };
            }
        }
    }
}
