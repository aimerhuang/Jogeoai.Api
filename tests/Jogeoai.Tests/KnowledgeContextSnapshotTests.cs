using System.Text.Json;
using System.Text.Json.Nodes;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>
/// 素材预览、生成提交和 Worker 对同一上下文必须得到同一摘要，否则带知识库的生成
/// 会在提交时固定报 KNOWLEDGE_CONTEXT_STALE。
/// </summary>
public sealed class KnowledgeContextSnapshotTests
{
    private static readonly JsonSerializerOptions WorkerJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static KnowledgeContextResponse SampleContext() => new(
        Guid.Parse("8d0b3c5e-6c5e-4c1c-9a3c-2f0f4f0e2a11"),
        [
            new KnowledgeEvidenceView("v:c", Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("33333333-3333-3333-3333-333333333333"),
                "3188DH", "# 3188DH\n- 关联品牌：优联科技", "site", "3188DH.md", "line:1;range:0-8|line:3;range:0-21",
                0.30000000000000004, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), null, "f4d3fb10"),
            new KnowledgeEvidenceView("v:d", Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("44444444-4444-4444-4444-444444444444"),
                "3188DH", "规格参数", "site", "3188DH.md", null, 1, null, null),
        ],
        ["缺少依据的问题"],
        [new KnowledgeConflict("price", ["v:c", "v:d"], "价格不一致")],
        KnowledgeScopeMode.Default,
        42);

    [Fact]
    public void SubmitRereadFromKnowledgeStoreKeepsPreviewHash()
    {
        var context = SampleContext();
        var previewHash = KnowledgeContextSnapshot.Hash(context);

        // knowledge_context_requests.snapshot_json is written and read with default options.
        var stored = JsonSerializer.Serialize(context);
        var reread = JsonSerializer.Deserialize<KnowledgeContextResponse>(stored)!;

        Assert.Equal(previewHash, KnowledgeContextSnapshot.Hash(reread));
    }

    [Fact]
    public void WorkerHashSurvivesMySqlJsonNormalization()
    {
        var context = SampleContext();
        var previewHash = KnowledgeContextSnapshot.Hash(context);
        var snapshot = KnowledgeContextSnapshot.Serialize(context);

        // A MySQL JSON column returns reordered keys with extra whitespace, so the
        // raw text no longer matches the hash input.
        var normalized = Reorder(JsonNode.Parse(snapshot)!).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Assert.NotEqual(snapshot, normalized);

        var worker = JsonSerializer.Deserialize<KnowledgeContextResponse>(normalized, WorkerJsonOptions)!;
        Assert.Equal(previewHash, KnowledgeContextSnapshot.Hash(worker));
    }

    private static JsonNode Reorder(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key.Length).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Reorder(p.Value.DeepClone())))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Reorder(item.DeepClone())).ToArray()),
        _ => node.DeepClone(),
    };
}
