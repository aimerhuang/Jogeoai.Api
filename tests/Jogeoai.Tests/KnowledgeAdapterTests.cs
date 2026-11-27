using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Api.Controllers;
using Jogeoai.Worker;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

public sealed class KnowledgeAdapterTests
{
    [Fact]
    public void ScopeModeAcceptsDocumentedBrandOnlyJsonValue()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());

        var request = JsonSerializer.Deserialize<KnowledgeSearchRequest>(
            "{\"Query\":\"产品型号\",\"ScopeMode\":\"brand_only\"}", options);

        Assert.Equal(KnowledgeScopeMode.BrandOnly, request!.ScopeMode);
    }

    [Fact]
    public void UploadRoutesAcceptTheAdvertisedHundredMegabyteFile()
    {
        foreach (var action in new[] { nameof(KnowledgeController.UploadTenant), nameof(KnowledgeController.UploadSite) })
        {
            var method = typeof(KnowledgeController).GetMethod(action)!;
            var limit = Assert.Single(method.CustomAttributes, item => item.AttributeType == typeof(RequestSizeLimitAttribute));
            Assert.True((long)limit.ConstructorArguments[0].Value! > 100L * 1024 * 1024);
        }
    }

    [Fact]
    public void DocumentViewSerializesLatestPendingVersionBesideCurrentReadyVersion()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new KnowledgeVersionView(Guid.NewGuid(), 1, "old.txt", ".txt", "text/plain", 5,
            new string('a', 64), "ready", null, null, 5, null, now, now);
        var latest = current with { VersionId = Guid.NewGuid(), VersionNumber = 2, OriginalFileName = "new.txt",
            Status = "pending", CompletedAt = null };
        var document = new KnowledgeDocumentView(Guid.NewGuid(), Guid.NewGuid(), null, "tenant", null,
            "Product", "ready", "und", "general", null, [], null, null, current.VersionId, current,
            now, now, 2, latest);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(document,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Equal(current.VersionId.ToString(), json.RootElement.GetProperty("currentVersion").GetProperty("versionId").GetString());
        Assert.Equal(latest.VersionId.ToString(), json.RootElement.GetProperty("latestVersion").GetProperty("versionId").GetString());
        Assert.Equal("pending", json.RootElement.GetProperty("latestVersion").GetProperty("status").GetString());
    }

    [Fact]
    public void ConflictCandidatesRequireSameExplicitProductAcrossDistinctSources()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var first = new KnowledgeEvidenceView("first", documentId, versionId, Guid.NewGuid(), "A", "型号 X 电压 5V",
            "site", "a.txt", "line:1", 0.9, null, null, "产品 X");
        var sameSource = first with { EvidenceId = "second-chunk", ChunkId = Guid.NewGuid(), Text = "型号 X 电压 12V" };
        var otherSource = first with { EvidenceId = "other", DocumentId = Guid.NewGuid(), VersionId = Guid.NewGuid(),
            ProductKey = "  产品 x  " };
        var noProduct = first with { EvidenceId = "unscoped", DocumentId = Guid.NewGuid(), VersionId = Guid.NewGuid(), ProductKey = null };

        Assert.Empty(KnowledgeConflictDetector.FindCandidates([first, sameSource, noProduct]));
        var candidate = Assert.Single(KnowledgeConflictDetector.FindCandidates([first, sameSource, otherSource, noProduct]));
        Assert.Equal("产品 x", candidate.Key);
        Assert.Contains("other", candidate.EvidenceIds);
        Assert.DoesNotContain("unscoped", candidate.EvidenceIds);
        Assert.Contains("可能存在冲突", candidate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FolderMoveRejectsDescendantAndAcceptsUnrelatedParent()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        var grandchild = Guid.NewGuid();
        var other = Guid.NewGuid();
        var parents = new Dictionary<Guid, Guid?>
        {
            [root] = null, [child] = root, [grandchild] = child, [other] = null
        };
        Task<Guid?> ReadParent(Guid id, CancellationToken _) => Task.FromResult(parents.GetValueOrDefault(id));

        Assert.True(await KnowledgeFolderHierarchy.WouldCreateCycleAsync(root, grandchild, ReadParent));
        Assert.True(await KnowledgeFolderHierarchy.WouldCreateCycleAsync(root, root, ReadParent));
        Assert.False(await KnowledgeFolderHierarchy.WouldCreateCycleAsync(child, other, ReadParent));
    }

    [Fact]
    public void FolderMoveToRootIsExplicitAndRejectsConflictingParent()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var rootMove = JsonSerializer.Deserialize<KnowledgeFolderUpdate>("{\"moveToRoot\":true}", options)!;
        KnowledgeStore.ValidateFolderMove(rootMove);
        Assert.True(rootMove.MoveToRoot);
        Assert.Null(rootMove.ParentId);

        var conflicting = rootMove with { ParentId = Guid.NewGuid() };
        var error = Assert.Throws<CollectorStoreException>(() => KnowledgeStore.ValidateFolderMove(conflicting));
        Assert.Equal("INVALID_FOLDER_MOVE", error.Code);
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void SearchRankingRetainsLateRelevantEvidenceWithBoundedMemory()
    {
        var candidates = new List<KnowledgeEvidenceView>();
        for (var i = 0; i < 1500; i++)
        {
            var item = new KnowledgeEvidenceView($"e{i}", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "Product", "unrelated", "tenant", "manual.txt", "line:1", 0.1, null, null);
            KnowledgeStore.KeepBestEvidence(candidates, item, 8);
        }
        var relevant = new KnowledgeEvidenceView("late-relevant", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Product", "model X specification", "tenant", "spec.txt", "line:1", 3, null, null);
        KnowledgeStore.KeepBestEvidence(candidates, relevant, 8);

        Assert.Equal(8, candidates.Count);
        Assert.Contains(candidates, item => item.EvidenceId == "late-relevant");
    }

    [Fact]
    public void DocumentListFiltersNormalizeAndRejectInvalidScopeOrStatus()
    {
        var siteId = Guid.NewGuid();
        var normalized = KnowledgeStore.NormalizeDocumentListFilter(siteId,
            new KnowledgeDocumentListFilter(KnowledgeScopeMode.BrandOnly, " READY ", "  产品  ", "  manual "));

        Assert.Equal(KnowledgeScopeMode.BrandOnly, normalized.ScopeMode);
        Assert.Equal("ready", normalized.Status);
        Assert.Equal("产品", normalized.Tag);
        Assert.Equal("manual", normalized.DocumentType);
        Assert.Equal("INVALID_KNOWLEDGE_SCOPE", Assert.Throws<CollectorStoreException>(() =>
            KnowledgeStore.NormalizeDocumentListFilter(null,
                new KnowledgeDocumentListFilter(KnowledgeScopeMode.BrandOnly))).Code);
        Assert.Equal("INVALID_KNOWLEDGE_STATUS", Assert.Throws<CollectorStoreException>(() =>
            KnowledgeStore.NormalizeDocumentListFilter(siteId,
                new KnowledgeDocumentListFilter(Status: "deleted"))).Code);
    }

    [Fact]
    public void DocumentCursorRejectsChangedTenantSiteOrFilters()
    {
        var tenant = Guid.NewGuid();
        var site = Guid.NewGuid();
        var filter = KnowledgeStore.NormalizeDocumentListFilter(site,
            new KnowledgeDocumentListFilter(KnowledgeScopeMode.BrandOnly, "ready", "product", "manual"));
        var hash = KnowledgeStore.ComputeListCursorContextHash(tenant, site, filter);
        var documentId = Guid.NewGuid();
        var cursor = KnowledgeStore.EncodeCursor(DateTime.UtcNow, documentId, hash);

        Assert.Equal(documentId, KnowledgeStore.ParseCursor(cursor, hash).Id);
        foreach (var changedHash in new[]
        {
            KnowledgeStore.ComputeListCursorContextHash(Guid.NewGuid(), site, filter),
            KnowledgeStore.ComputeListCursorContextHash(tenant, Guid.NewGuid(), filter),
            KnowledgeStore.ComputeListCursorContextHash(tenant, site, filter with { Status = "failed" }),
            KnowledgeStore.ComputeListCursorContextHash(tenant, site, filter with { Tag = "other" }),
            KnowledgeStore.ComputeListCursorContextHash(tenant, site, filter with { DocumentType = "other" }),
            KnowledgeStore.ComputeListCursorContextHash(tenant, site, filter with { ScopeMode = KnowledgeScopeMode.Default })
        })
            Assert.Equal("INVALID_CURSOR", Assert.Throws<CollectorStoreException>(() =>
                KnowledgeStore.ParseCursor(cursor, changedHash)).Code);
    }

    [Fact]
    public async Task ChunkerSplitsLongParagraphsPreservesRangesAndStableFinalIds()
    {
        var extractor = new BasicDocumentTextExtractor();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Product\n" + new string('a', 160)));
        var document = await extractor.ExtractAsync(stream, ".md", "text/markdown");
        var chunker = new KnowledgeChunker();
        var first = chunker.Chunk(Guid.Parse("11111111-1111-4111-8111-111111111111"), document, 40, 5);
        var second = chunker.Chunk(Guid.Parse("11111111-1111-4111-8111-111111111111"), document, 40, 5);

        Assert.True(first.Count > 3);
        Assert.All(first, item => Assert.InRange(item.Text.Length, 1, 40));
        Assert.All(first, item => Assert.Contains("range:", item.Location, StringComparison.Ordinal));
        Assert.Equal(first.Select(item => item.ChunkId), second.Select(item => item.ChunkId));
    }

    [Fact]
    public async Task DocxExtractorReadsHeadingsAndParagraphsWithoutExecutingDtd()
    {
        await using var stream = new MemoryStream(CreateDocx("Heading", "Product facts"));
        var result = await new BasicDocumentTextExtractor().ExtractAsync(stream, ".docx", null);

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("Heading", result.Segments[0].HeadingPath);
        Assert.Equal("Product facts", result.Segments[1].Text);
    }

    [Fact]
    public async Task DocxExtractorRejectsExternalRelationshipsRegardlessOfXmlAttributeFormatting()
    {
        await using var stream = new MemoryStream(CreateDocxWithExternalRelationship());

        var exception = await Assert.ThrowsAsync<KnowledgeExtractionException>(() =>
            new BasicDocumentTextExtractor().ExtractAsync(stream, ".docx", null));

        Assert.Equal("KNOWLEDGE_PARSE_FAILED", exception.Code);
    }

    [Fact]
    public void ExtractionLimitsDefaultToTheDesignedBudgetsAndRejectOutOfRangeValues()
    {
        var defaults = new KnowledgeExtractionLimits();
        Assert.Equal(1000, defaults.MaxPdfPages);
        Assert.Equal(10_000_000, defaults.MaxCharacters);
        Assert.Equal(40_000_000, defaults.MaxImagePixels);
        Assert.Equal(90, defaults.ParseTimeoutSeconds);
        Assert.True(defaults.IsValid());

        foreach (var invalid in new[]
        {
            new KnowledgeExtractionLimits { MaxPdfPages = 0 },
            new KnowledgeExtractionLimits { MaxCharacters = 0 },
            new KnowledgeExtractionLimits { MaxImagePixels = 0 },
            new KnowledgeExtractionLimits { ParseTimeoutSeconds = 0 },
            new KnowledgeExtractionLimits { ParseTimeoutSeconds = 3601 }
        })
        {
            Assert.False(invalid.IsValid());
            Assert.Throws<ArgumentOutOfRangeException>(() => new BasicDocumentTextExtractor(invalid));
        }
    }

    [Fact]
    public async Task KnowledgeServicesBindExtractionLimitsFromConfigurationAndRejectInvalidValues()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Knowledge:Extraction:MaxPdfPages"] = "2",
            ["Knowledge:Extraction:ParseTimeoutSeconds"] = "30"
        }).Build();
        await using var provider = new ServiceCollection().AddKnowledgeServices(configuration).BuildServiceProvider();

        var limits = provider.GetRequiredService<IOptions<KnowledgeExtractionLimits>>().Value;
        Assert.Equal(2, limits.MaxPdfPages);
        Assert.Equal(30, limits.ParseTimeoutSeconds);
        Assert.Equal(10_000_000, limits.MaxCharacters);
        Assert.Equal(40_000_000, limits.MaxImagePixels);

        // 注入的解析器使用绑定后的上限，而不是默认值。
        var extractor = provider.GetRequiredService<IDocumentTextExtractor>();
        await using var stream = new MemoryStream(BuildTextPdf(3));
        var exception = await Assert.ThrowsAsync<KnowledgeExtractionException>(() => extractor.ExtractAsync(stream, ".pdf", null));
        Assert.Equal("KNOWLEDGE_PDF_TOO_MANY_PAGES", exception.Code);

        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Knowledge:Extraction:MaxPdfPages"] = "0"
        }).Build();
        await using var invalidProvider = new ServiceCollection().AddKnowledgeServices(invalid).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => invalidProvider.GetRequiredService<IOptions<KnowledgeExtractionLimits>>().Value);
    }

    [Fact]
    public async Task KnowledgeServicesStartupValidationPassesWithoutTheSectionAndFailsOnBadValues()
    {
        // IStartupValidator 是 ValidateOnStart 注册的、宿主（API 与 Worker）启动时执行的校验入口；这里不启动宿主，直接调用它。
        static ServiceProvider Build(params (string Key, string Value)[] values) => new ServiceCollection()
            .AddKnowledgeServices(new ConfigurationBuilder()
                .AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value)).Build())
            .BuildServiceProvider();

        // 配置节缺失：使用默认值，启动校验通过，解析器可以解析出来。
        await using var absent = Build();
        absent.GetRequiredService<IStartupValidator>().Validate();
        Assert.IsType<BasicDocumentTextExtractor>(absent.GetRequiredService<IDocumentTextExtractor>());

        // 超出允许范围：启动校验失败。
        await using var outOfRange = Build(("Knowledge:Extraction:ParseTimeoutSeconds", "0"));
        Assert.Throws<OptionsValidationException>(() => outOfRange.GetRequiredService<IStartupValidator>().Validate());

        // 不是数字：绑定阶段就失败，同样发生在启动校验里，不会带着默认值继续运行。
        await using var notANumber = Build(("Knowledge:Extraction:MaxImagePixels", "many"));
        Assert.ThrowsAny<Exception>(() => notANumber.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void IngestLeaseDefaultsToFiveMinutesAndRejectsOutOfRangeConfiguration()
    {
        static IConfiguration Configuration(string? value) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Knowledge:Ingest:LeaseSeconds"] = value }).Build();

        Assert.Equal(TimeSpan.FromSeconds(300), KnowledgeIngestWorker.ResolveLease(new ConfigurationBuilder().Build()));
        Assert.Equal(TimeSpan.FromSeconds(300), KnowledgeIngestWorker.ResolveLease(Configuration(" ")));
        Assert.Equal(TimeSpan.FromSeconds(120), KnowledgeIngestWorker.ResolveLease(Configuration("120")));
        Assert.Equal(TimeSpan.FromSeconds(30), KnowledgeIngestWorker.ResolveLease(Configuration("30")));
        Assert.Equal(TimeSpan.FromSeconds(3600), KnowledgeIngestWorker.ResolveLease(Configuration("3600")));
        foreach (var invalid in new[] { "29", "3601", "0", "-5", "abc", "1.5" })
            Assert.Throws<InvalidOperationException>(() => KnowledgeIngestWorker.ResolveLease(Configuration(invalid)));
    }

    [Fact]
    public async Task PdfWithinDefaultLimitsStillExtractsTextPerPage()
    {
        await using var stream = new MemoryStream(BuildTextPdf(3));

        var result = await new BasicDocumentTextExtractor().ExtractAsync(stream, ".pdf", "application/pdf");

        Assert.Equal("text", result.ContentKind);
        Assert.Equal(3, result.PageCount);
        Assert.Equal(900, result.CharacterCount);
        Assert.Contains(result.Segments, item => item.Location!.StartsWith("page:1;", StringComparison.Ordinal));
        Assert.Contains(result.Segments, item => item.Location!.StartsWith("page:3;", StringComparison.Ordinal));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task PdfOverPageLimitIsRejectedBeforeAnyPageIsParsed()
    {
        var pdf = BuildTextPdf(3);
        var visited = new List<int>();
        var extractor = new BasicDocumentTextExtractor(new KnowledgeExtractionLimits { MaxPdfPages = 2 })
        {
            BeforePdfPage = page => { lock (visited) visited.Add(page); }
        };
        await using var stream = new MemoryStream(pdf);

        var exception = await Assert.ThrowsAsync<KnowledgeExtractionException>(() => extractor.ExtractAsync(stream, ".pdf", null));

        Assert.Equal("KNOWLEDGE_PDF_TOO_MANY_PAGES", exception.Code);
        Assert.False(exception.Retryable);
        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
        Assert.Empty(visited);

        // 页数恰好等于上限时照常解析。
        var atLimit = new BasicDocumentTextExtractor(new KnowledgeExtractionLimits { MaxPdfPages = 3 });
        await using var accepted = new MemoryStream(pdf);
        Assert.Equal(3, (await atLimit.ExtractAsync(accepted, ".pdf", null)).PageCount);
    }

    [Fact]
    public async Task PdfStopsParsingAsSoonAsCumulativeCharactersExceedTheLimit()
    {
        // 每页 300 字符、共 40 页；上限 450：第 1 页累计 300 未超，第 2 页累计 600 超限，之后的 38 页不应被解析。
        var pdf = BuildTextPdf(40);
        var visited = new List<int>();
        var extractor = new BasicDocumentTextExtractor(new KnowledgeExtractionLimits { MaxCharacters = 450 })
        {
            BeforePdfPage = page => { lock (visited) visited.Add(page); }
        };
        await using var stream = new MemoryStream(pdf);

        var exception = await Assert.ThrowsAsync<KnowledgeExtractionException>(() => extractor.ExtractAsync(stream, ".pdf", null));

        Assert.Equal("KNOWLEDGE_CONTENT_TOO_LARGE", exception.Code);
        Assert.False(exception.Retryable);
        Assert.Equal([1, 2], visited);

        // 累计恰好等于上限时不算超限。
        var exact = new BasicDocumentTextExtractor(new KnowledgeExtractionLimits { MaxCharacters = 900 });
        await using var accepted = new MemoryStream(BuildTextPdf(3));
        Assert.Equal(900, (await exact.ExtractAsync(accepted, ".pdf", null)).CharacterCount);
    }

    [Fact]
    public async Task PdfParseTimeoutReturnsTimeoutCodeWithoutBlockingLaterExtractionsAndParsesNoFurtherPage()
    {
        // 预热：进程内第一次打开 PDF 较慢，先解析一次，让下面的超时落在「卡在第 1 页」而不是加载阶段。
        await using (var warmup = new MemoryStream(BuildTextPdf(1)))
            await new BasicDocumentTextExtractor().ExtractAsync(warmup, ".pdf", null);
        using var release = new ManualResetEventSlim(false);
        var visited = new List<int>();
        var limits = new KnowledgeExtractionLimits();
        var extractor = new BasicDocumentTextExtractor(limits)
        {
            ParseTimeoutOverride = TimeSpan.FromMilliseconds(500),
            // 第 1 页之前卡住解析线程，模拟一个在超时内解析不完的文件。
            BeforePdfPage = page => { lock (visited) visited.Add(page); if (page == 1) release.Wait(TimeSpan.FromSeconds(30)); }
        };
        await using var stream = new MemoryStream(BuildTextPdf(3));
        var started = System.Diagnostics.Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<KnowledgeExtractionException>(() => extractor.ExtractAsync(stream, ".pdf", null));

        Assert.Equal("KNOWLEDGE_PARSE_TIMEOUT", exception.Code);
        Assert.False(exception.Retryable);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"超时应在设定时间附近返回，实际 {started.Elapsed}。");

        // 被放弃的解析线程仍卡着时，后续解析不受影响。
        await using var later = new MemoryStream(Encoding.UTF8.GetBytes("# Product\nfacts"));
        Assert.Equal(2, (await new BasicDocumentTextExtractor(limits).ExtractAsync(later, ".md", null)).Segments.Count);

        // 放开后，被放弃的解析在下一页之前看到取消并停止：第 2、3 页不会被解析。
        // （极慢的机器上超时可能先于第 1 页触发，此时一页都不解析，同样满足。）
        release.Set();
        await Task.Delay(300);
        lock (visited) Assert.DoesNotContain(visited, page => page > 1);
    }

    [Fact]
    public async Task CallerCancellationDuringPdfParsePropagatesAsCancellationNotAsParseFailureOrTimeout()
    {
        using var shutdown = new CancellationTokenSource();
        var visited = new List<int>();
        var extractor = new BasicDocumentTextExtractor
        {
            // 解析第 1 页之前模拟宿主关机。
            BeforePdfPage = page => { lock (visited) visited.Add(page); if (page == 1) shutdown.Cancel(); }
        };
        await using var stream = new MemoryStream(BuildTextPdf(3));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.ExtractAsync(stream, ".pdf", null, shutdown.Token));

        Assert.True(shutdown.IsCancellationRequested);
        Assert.IsNotType<KnowledgeExtractionException>(exception.InnerException);
        await Task.Delay(300);
        lock (visited) Assert.Equal([1], visited);
    }

    [Fact]
    public async Task DocxWithinDefaultLimitsAndAlreadyCancelledCallsBehaveAsBefore()
    {
        var extractor = new BasicDocumentTextExtractor(new KnowledgeExtractionLimits());
        await using var stream = new MemoryStream(CreateDocx("Heading", "Product facts"));
        Assert.Equal(2, (await extractor.ExtractAsync(stream, ".docx", null)).Segments.Count);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var again = new MemoryStream(CreateDocx("Heading", "Product facts"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(again, ".docx", null, cancelled.Token));
    }

    [Fact]
    public async Task EmbeddingAdapterBatchesAndRestoresResponseOrder()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{\"data\":[{\"index\":1,\"embedding\":[2,3,4]},{\"index\":0,\"embedding\":[1,2,3]}]}"));
        var provider = new OpenAiCompatibleEmbeddingProvider(new HttpClient(handler), "http://embedding.test/v1/embeddings", "test-model", 3, "secret", 2);

        var vectors = await provider.EmbedAsync(["one", "two"]);

        Assert.Equal(2, vectors.Count);
        Assert.Equal([1f, 2f, 3f], vectors[0]);
        Assert.Equal([2f, 3f, 4f], vectors[1]);
        Assert.Contains("Bearer secret", handler.AuthorizationValues);
        Assert.Contains("openai-", provider.Profile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QdrantAdapterUsesTenantAndVisibleVersionFilters()
    {
        var chunkId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var versionId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var handler = new QdrantHandler(chunkId);
        var index = new QdrantVectorIndex(new HttpClient(handler), "http://qdrant.test", "profile-1", 3);

        await index.UpsertAsync(Guid.Parse("44444444-4444-4444-8444-444444444444"), versionId,
            [new KnowledgeChunk(chunkId, 0, "text", null, "page:1")], [[1, 0, 0]]);
        var result = await index.SearchAsync(Guid.Parse("44444444-4444-4444-8444-444444444444"), new HashSet<Guid> { versionId }, [1, 0, 0], 5);

        Assert.Single(result);
        Assert.Equal(chunkId, result[0].ChunkId);
        Assert.Contains(handler.RequestBodies, body => body.Contains("tenantId", StringComparison.Ordinal)
            && body.Contains(versionId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task QdrantConfiguredCollectionTracksActiveEmbeddingProfile()
    {
        var handler = new QdrantHandler(Guid.NewGuid());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Knowledge:Qdrant:Url"] = "http://qdrant.test",
            ["Knowledge:Qdrant:Profile"] = "stale-profile",
            ["Knowledge:Qdrant:Dimensions"] = "99"
        }).Build();
        var embedding = new OpenAiCompatibleEmbeddingProvider(new HttpClient(new RecordingHandler(_ => JsonResponse("{}"))),
            "http://embedding.test/v1/embeddings", "active-model", 3);
        var index = new QdrantVectorIndex(new HttpClient(handler), configuration, embedding);

        await index.UpsertAsync(Guid.NewGuid(), Guid.NewGuid(),
            [new KnowledgeChunk(Guid.NewGuid(), 0, "text", null, "line:1")], [[1f, 0f, 0f]]);

        Assert.Contains(handler.RequestUrls, url => url.Contains(embedding.Profile, StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestUrls, url => url.Contains("stale-profile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalVectorIndexSharesWorkerWritesWithSeparateApiInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "jogeoai-vector-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Knowledge:VectorRoot"] = root }).Build();
            var embedding = new DeterministicEmbeddingProvider();
            var workerIndex = new LocalVectorIndex(configuration, embedding);
            var apiIndex = new LocalVectorIndex(configuration, embedding);
            var tenant = Guid.NewGuid();
            var version = Guid.NewGuid();
            var chunk = new KnowledgeChunk(Guid.NewGuid(), 0, "product facts", null, "line:1");
            await workerIndex.UpsertAsync(tenant, version, [chunk], [[1f, 0f]]);

            var visible = await apiIndex.SearchAsync(tenant, new HashSet<Guid> { version }, [1f, 0f], 5);
            var otherTenant = await apiIndex.SearchAsync(Guid.NewGuid(), new HashSet<Guid> { version }, [1f, 0f], 5);
            Assert.Equal(chunk.ChunkId, Assert.Single(visible).ChunkId);
            Assert.Empty(otherTenant);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>每页 3 行 × 100 个字母（共 300 字符）的多页文字 PDF，由测试代码生成。</summary>
    private static byte[] BuildTextPdf(int pages)
        => BasicDocumentTextExtractorImageTests.BuildPdf(
            Enumerable.Range(0, pages).Select(BasicDocumentTextExtractorImageTests.TextPage).ToArray());

    private static byte[] CreateDocx(string heading, string paragraph)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write($"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>{heading}</w:t></w:r></w:p><w:p><w:r><w:t>{paragraph}</w:t></w:r></w:p></w:body></w:document>");
        }
        return output.ToArray();
    }

    private static byte[] CreateDocxWithExternalRelationship()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var document = archive.CreateEntry("word/document.xml");
            using (var writer = new StreamWriter(document.Open(), Encoding.UTF8))
                writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>safe text</w:t></w:r></w:p></w:body></w:document>");

            var relationships = archive.CreateEntry("word/_rels/document.xml.rels");
            using (var writer = new StreamWriter(relationships.Open(), Encoding.UTF8))
                writer.Write("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"https://example.test\" TargetMode = \"External\" /></Relationships>");
        }
        return output.ToArray();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        public List<string> AuthorizationValues { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization is not null) AuthorizationValues.Add(request.Headers.Authorization.ToString());
            return Task.FromResult(callback(request));
        }
    }

    private sealed class QdrantHandler(Guid chunkId) : HttpMessageHandler
    {
        private int collectionGets;
        public List<string> RequestBodies { get; } = [];
        public List<string> RequestUrls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            RequestUrls.Add(request.RequestUri!.ToString());
            if (request.Method == HttpMethod.Get && ++collectionGets == 1) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Get) return JsonResponse("{\"result\":{\"config\":{\"params\":{\"vectors\":{\"size\":3}}}}}");
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
                return JsonResponse($"{{\"result\":[{{\"id\":\"{chunkId}\",\"score\":0.9,\"payload\":{{\"chunkId\":\"{chunkId}\"}}}}]}}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }
}
