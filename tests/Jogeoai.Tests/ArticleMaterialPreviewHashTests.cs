using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>素材预览摘要必须基于落库后重新读取的上下文计算，与提交 / Worker 的校验口径一致。</summary>
public sealed class ArticleMaterialPreviewHashTests
{
    [Fact]
    public async Task PreviewHashMatchesTheReloadedSnapshotWhenMySqlRewritesScores()
    {
        var tenant = Guid.NewGuid();
        var site = Guid.NewGuid();
        var contextId = Guid.NewGuid();
        // MySQL JSON 列把 7.6000000000000005 读回为 7.6（2026-10-30 本机 8.4 实测）。
        KnowledgeContextResponse Context(double score) => new(contextId,
            [new KnowledgeEvidenceView("ev-1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "资料", "正文", "brand",
                "a.md", null, score, null, null)], [], [], KnowledgeScopeMode.Default, 2);
        var created = Context(2.5 + 2.5 + 2.6000000000000005);
        var reloaded = Context(7.6);
        Assert.NotEqual(KnowledgeContextSnapshot.Hash(created), KnowledgeContextSnapshot.Hash(reloaded));

        var retrieval = StudioFakes.Of<IKnowledgeRetrievalService>((method, _) => method switch
        {
            nameof(IKnowledgeRetrievalService.CreateContextAsync) => Task.FromResult(created),
            nameof(IKnowledgeRetrievalService.GetContextAsync) => Task.FromResult<KnowledgeContextResponse?>(reloaded),
            _ => throw new InvalidOperationException(method),
        });
        var knowledge = StudioFakes.Of<IKnowledgeStore>((method, _) => method switch
        {
            nameof(IKnowledgeStore.ListFoldersAsync) => Task.FromResult<IReadOnlyList<KnowledgeFolderView>>([]),
            nameof(IKnowledgeStore.ListDocumentsAsync) => Task.FromResult(new CursorPage<KnowledgeDocumentView>([], null)),
            _ => throw new InvalidOperationException(method),
        });
        var content = StudioFakes.Of<IContentStore>((method, args) => method == nameof(IContentStore.SaveMaterialPreviewAsync)
            ? Task.FromResult((ArticleMaterialPreviewView)args![3]!)
            : throw new InvalidOperationException(method));
        var service = new ArticleMaterialSelectionService(knowledge, retrieval, content);

        var preview = await service.PreviewAsync(tenant, site, Guid.NewGuid(),
            new ArticleMaterialPreviewRequest([Guid.NewGuid()], "zh-CN"));

        Assert.Equal(KnowledgeContextSnapshot.Hash(reloaded), preview.ContextSnapshotHash);
        Assert.Equal(7.6, preview.Hits.Single().Score);
    }
}
