using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class KnowledgeRetrievalLexicalTests
{
    private const string ProcurementQuestion = "市政和工业管道检测需要采购推杆式内窥镜或CCTV管道机器人，分别推荐哪些厂家和型号？";

    [Fact]
    public void ChineseQuestionBecomesAdjacentBigramsAroundAlphanumericWords()
    {
        var terms = KnowledgeLexicalTerms.Extract(ProcurementQuestion);

        Assert.Equal("市政", terms[0]);
        foreach (var expected in new[] { "管道", "检测", "内窥", "窥镜", "cctv", "机器", "器人", "厂家", "型号" })
            Assert.Contains(expected, terms);
        Assert.Single(terms, term => term == "管道");
        Assert.DoesNotContain(terms, term => term.Contains('，') || term.Contains('？'));
        // Bigrams never span a word run or punctuation.
        Assert.DoesNotContain("或c", terms);
        Assert.DoesNotContain("人分", terms);
        Assert.All(terms.Where(term => term != "cctv"), term => Assert.Equal(2, term.Length));
    }

    [Fact]
    public void LoneCjkCharacterIsDroppedUnlessItIsTheWholeQuery()
    {
        Assert.Equal(["钉子"], KnowledgeLexicalTerms.Extract("锤 和 钉子"));
        Assert.Equal(["锤"], KnowledgeLexicalTerms.Extract("锤"));
        Assert.Equal(["锤"], KnowledgeLexicalTerms.Extract(" 锤？"));
        Assert.Equal(["dvr"], KnowledgeLexicalTerms.Extract("锤 DVR"));
        Assert.Empty(KnowledgeLexicalTerms.Extract("，。！ "));
        Assert.Empty(KnowledgeLexicalTerms.Extract(null));
    }

    [Fact]
    public void AlphanumericTermsAreLowercasedWholeWordsSplitOnPunctuation()
    {
        Assert.Equal(["model", "x", "3188dh", "c23h", "voltage"], KnowledgeLexicalTerms.Extract("Model-X 3188DH, C23H; voltage"));
        // Previous whitespace-separated queries keep their terms.
        Assert.Equal(["model", "x", "specification"], KnowledgeLexicalTerms.Extract("model X  Specification"));
        Assert.Equal(["dvr"], KnowledgeLexicalTerms.Extract("DVR dvr"));
        Assert.Equal(["カメ", "メラ", "카메", "메라"], KnowledgeLexicalTerms.Extract("カメラ 카메라"));
    }

    [Fact]
    public void TermsAreCappedAtSixtyFour()
    {
        var query = string.Concat(Enumerable.Range(0, 100).Select(i => (char)(0x4E00 + i)));

        var terms = KnowledgeLexicalTerms.Extract(query);

        Assert.Equal(KnowledgeLexicalTerms.MaxTerms, terms.Count);
        Assert.Equal(64, terms.Count);
        Assert.Equal(query[..2], terms[0]);
        Assert.Equal(query.Substring(63, 2), terms[^1]);
    }

    [Fact]
    public void MinMatchesRequiresTwoDistinctTermsForMultiTermQueries()
    {
        Assert.Equal(0, KnowledgeLexicalTerms.MinMatches([]));
        Assert.Equal(1, KnowledgeLexicalTerms.MinMatches(["3188dh"]));
        Assert.Equal(2, KnowledgeLexicalTerms.MinMatches(["管道", "检测"]));
        Assert.Equal(2, KnowledgeLexicalTerms.MinMatches(KnowledgeLexicalTerms.Extract(ProcurementQuestion)));
    }

    [Fact]
    public void ScoreKeepsPreviousFormulaAndCountsDistinctMatches()
    {
        var terms = KnowledgeLexicalTerms.Extract(ProcurementQuestion);
        var minMatches = KnowledgeLexicalTerms.MinMatches(terms);

        var relevant = KnowledgeLexicalTerms.Score("本产品用于管道检测，配备 CCTV 摄像头。", terms);
        var weak = KnowledgeLexicalTerms.Score("公司简介：我们的管理团队。", terms);
        var single = KnowledgeLexicalTerms.Score("管道", ["管道", "厂家"]);

        Assert.True(relevant.Matches >= minMatches);
        Assert.True(weak.Matches < minMatches);
        Assert.Equal(1, single.Matches);
        Assert.Equal(1.1, single.Score, 6);
        var repeated = KnowledgeLexicalTerms.Score("管道检测 管道检测 Model", ["管道", "检测", "model", "厂家"]);
        Assert.Equal(3, repeated.Matches);
        Assert.Equal(1.2 + 1.2 + 1.1, repeated.Score, 6);
    }

    [Fact]
    public void AssemblerCoversEveryPinnedDocumentBeforeRelevanceFills()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var pinnedA = Chunks(a, 3, "资料A"); var pinnedB = Chunks(b, 3, "资料B"); var pinnedC = Chunks(c, 3, "资料C");
        var pinned = pinnedA.Concat(pinnedB).Concat(pinnedC).ToList();
        // Only document A matches the question; its second chunk is the best hit.
        var relevant = new[] { pinnedA[1].Evidence with { Score = 3 }, pinnedA[0].Evidence with { Score = 2 }, pinnedA[2].Evidence with { Score = 1 } };

        var result = KnowledgeContextAssembler.Assemble([relevant], pinned, 3, 100_000);

        Assert.Equal(3, result.Count);
        Assert.Equal([a, b, c], result.Select(item => item.DocumentId));
        Assert.Equal(pinnedA[1].Evidence.EvidenceId, result[0].EvidenceId);
        Assert.Equal(3, result[0].Score);
        Assert.Equal(pinnedB[0].Evidence.EvidenceId, result[1].EvidenceId);
        Assert.Equal(pinnedC[0].Evidence.EvidenceId, result[2].EvidenceId);
    }

    [Fact]
    public void AssemblerFallsBackToLowestOrdinalAndRoundRobinsTheFill()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var chunksA = Chunks(a, 3, "A"); var chunksB = Chunks(b, 2, "B");
        // Load order does not have to be ordinal order inside a document.
        var pinned = new[] { chunksA[2], chunksA[0], chunksB[1], chunksA[1], chunksB[0] };

        var result = KnowledgeContextAssembler.Assemble([Array.Empty<KnowledgeEvidenceView>()], pinned, 10, 100_000);

        Assert.Equal(new[] { chunksA[0], chunksB[0], chunksA[1], chunksB[1], chunksA[2] }.Select(item => item.Evidence.EvidenceId),
            result.Select(item => item.EvidenceId));
        Assert.All(result, item => Assert.Equal(0, item.Score));
    }

    [Fact]
    public void AssemblerRespectsChunkAndCharacterBudgets()
    {
        var pinned = Enumerable.Range(0, 5).SelectMany(_ => Chunks(Guid.NewGuid(), 4, new string('x', 400))).ToList();
        var relevant = pinned.Take(6).Select(chunk => chunk.Evidence with { Score = 1 }).ToArray();

        var byCount = KnowledgeContextAssembler.Assemble([relevant], pinned, 3, 100_000);
        var byCharacters = KnowledgeContextAssembler.Assemble([relevant], pinned, 100, 1000);

        Assert.Equal(3, byCount.Count);
        Assert.Equal(3, byCount.Select(item => item.DocumentId).Distinct().Count());
        Assert.Equal(2, byCharacters.Count);
        Assert.True(byCharacters.Sum(item => item.Text.Length) <= 1000);
    }

    [Fact]
    public void AssemblerDeduplicatesAcrossQueriesAndPinnedChunks()
    {
        var document = Guid.NewGuid();
        var chunks = Chunks(document, 2, "片段");
        var hit = chunks[0].Evidence with { Score = 2 };

        var result = KnowledgeContextAssembler.Assemble([[hit], [hit, chunks[1].Evidence with { Score = 1 }]], chunks, 10, 100_000);

        Assert.Equal(2, result.Count);
        Assert.Equal(result.Count, result.Select(item => item.EvidenceId).Distinct().Count());
        Assert.Equal(2, result[0].Score);
    }

    [Fact]
    public void AssemblerSkipsOversizedChunkInFillRound()
    {
        var document = Guid.NewGuid();
        var pinned = new[]
        {
            Pinned(document, 0, new string('a', 300)), Pinned(document, 1, new string('b', 900)), Pinned(document, 2, new string('c', 300))
        };

        var result = KnowledgeContextAssembler.Assemble([Array.Empty<KnowledgeEvidenceView>()], pinned, 10, 1000);

        Assert.Equal([pinned[0].Evidence.EvidenceId, pinned[2].Evidence.EvidenceId], result.Select(item => item.EvidenceId));
    }

    [Fact]
    public void AssemblerCoverageFallsBackToAFittingChunkWhenTheBestHitIsTooLong()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var chunksA = new[] { Pinned(a, 0, new string('a', 300)), Pinned(a, 1, new string('l', 2000)) };
        var chunksB = new[] { Pinned(b, 0, new string('b', 600)) };
        // Document A's only relevant hit exceeds the whole character budget; with a
        // two-chunk budget the fill round would be too late to cover A.
        var relevant = new[] { chunksA[1].Evidence with { Score = 5 } };
        var chunksC = new[] { Pinned(Guid.NewGuid(), 0, new string('c', 100)) };

        var result = KnowledgeContextAssembler.Assemble([relevant], [.. chunksA, .. chunksB, .. chunksC], 2, 1000);

        Assert.Equal([chunksA[0].Evidence.EvidenceId, chunksB[0].Evidence.EvidenceId], result.Select(item => item.EvidenceId));
    }

    [Fact]
    public void AssemblerWithoutPinnedChunksMatchesThePreviousLoop()
    {
        var random = new Random(20261022);
        for (var round = 0; round < 200; round++)
        {
            var pool = Enumerable.Range(0, 12).Select(_ => Evidence(Guid.NewGuid(), new string('x', random.Next(100, 900)), random.NextDouble())).ToList();
            var queries = Enumerable.Range(0, random.Next(1, 4))
                .Select(_ => (IReadOnlyList<KnowledgeEvidenceView>)pool.OrderBy(_ => random.Next()).Take(random.Next(0, 8))
                    .OrderByDescending(item => item.Score).ToList())
                .ToList();
            var maxChunks = random.Next(1, 10); var maxCharacters = random.Next(1000, 4000);

            var expected = PreviousLoop(queries, maxChunks, maxCharacters);
            var actual = KnowledgeContextAssembler.Assemble(queries, [], maxChunks, maxCharacters);

            Assert.Equal(expected.Select(item => item.EvidenceId), actual.Select(item => item.EvidenceId));
        }
    }

    // The CreateContextAsync loop before pinned retrieval, kept verbatim for comparison.
    private static List<KnowledgeEvidenceView> PreviousLoop(IReadOnlyList<IReadOnlyList<KnowledgeEvidenceView>> queries,
        int maxChunks, int maxCharacters)
    {
        var all = new List<KnowledgeEvidenceView>();
        foreach (var found in queries)
            foreach (var item in found.Where(item => all.All(existing => existing.EvidenceId != item.EvidenceId)))
            {
                if (all.Count >= maxChunks) break;
                if (all.Sum(existing => existing.Text.Length) + item.Text.Length > maxCharacters) break;
                all.Add(item);
            }
        return all;
    }

    private static List<KnowledgePinnedChunk> Chunks(Guid documentId, int count, string text)
        => Enumerable.Range(0, count).Select(ordinal => Pinned(documentId, ordinal, $"{text}-{ordinal}")).ToList();

    private static KnowledgePinnedChunk Pinned(Guid documentId, long ordinal, string text)
        => new(Evidence(documentId, text, 0), ordinal);

    private static KnowledgeEvidenceView Evidence(Guid documentId, string text, double score)
    {
        var (versionId, chunkId) = (Guid.NewGuid(), Guid.NewGuid());
        return new KnowledgeEvidenceView($"{versionId}:{chunkId}", documentId, versionId, chunkId, "资料", text, "site",
            "a.md", null, score, null, null);
    }
}
