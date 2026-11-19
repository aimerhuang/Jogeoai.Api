using Jogeoai.Domain;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 文章生成不提供 App 口径（7.6，ContentStore.Generation 提交时的目标核对）：带着 App 目标标识的生成预览在提交时按
/// “目标平台配置已变化”拒绝（409 SELECTION_STALE），不写批次、不预占积分。
/// </summary>
public sealed partial class AppAnalyticsSqlTests
{
    [AppChannelMySqlFact]
    public Task AGenerationPreviewCarryingAnAppTargetIsRefusedAsStaleAtSubmission() => PoolAsync(async (pool, seedTenant) =>
    {
        var (w, _) = await SeedWorldAsync(pool, await seedTenant());
        var (t, s) = (w.Tenant, w.Site);
        var (user, owner) = await SeedUsersAsync(w.Cs, t);
        try
        {
            var targets = (await w.Rig.Store.GetSiteTargetsAsync(t, s)).Items;
            var app = targets.Single(target => target.Provider == AppProvider && target.Engine == "doubao" && target.Enabled).TargetId!.Value;
            var chatGpt = targets.Single(target => target.Provider == "cloro" && target.Engine == "chatgpt" && target.Enabled).TargetId!.Value;

            async Task<ArticleGenerationSubmitRequest> PreviewAsync(params Guid[] targetIds)
            {
                var request = new ArticleGenerationPreviewRequest([w.Prompts[0]], targetIds, WithoutRag: true);
                var preview = new ArticleGenerationPreviewView(Guid.NewGuid(), $"{Guid.NewGuid():N}{Guid.NewGuid():N}", request.PromptIds, request.TargetIds,
                    1, ArticleGenerationPricing.UnitPrice, ArticleGenerationPricing.UnitPrice, 1000, DateTimeOffset.UtcNow.AddMinutes(15), [], [], null, true);
                var saved = await w.Content.SaveGenerationPreviewAsync(t, s, user, request, preview);
                return new ArticleGenerationSubmitRequest(saved.PreviewId, saved.RequestHash);
            }

            foreach (var selection in new[] { new[] { app }, [chatGpt, app] })
            {
                var stale = await Assert.ThrowsAsync<CollectorStoreException>(async ()
                    => await w.Content.SubmitGenerationAsync(t, s, user, await PreviewAsync(selection), $"gen-{Guid.NewGuid():N}"));
                Assert.Equal(("SELECTION_STALE", 409), Code(stale));
            }
            // The same preview with the API target only gets past the target check: the next step reads the article provider's
            // configuration, which this fixture's research store does not offer.
            var next = await Assert.ThrowsAsync<InvalidOperationException>(async ()
                => await w.Content.SubmitGenerationAsync(t, s, user, await PreviewAsync(chatGpt), $"gen-{Guid.NewGuid():N}"));
            Assert.Equal("GetProviderConfigurationAsync", next.Message);
            // Nothing was submitted or reserved.
            Assert.Equal(0, await CountAsync(w.Cs, "SELECT COUNT(*) FROM article_generation_batches WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", t.ToString())));
            Assert.Equal(0, await CountAsync(w.Cs, "SELECT COUNT(*) FROM credit_reservations WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", t.ToString())));
        }
        finally { await DeleteUsersAsync(w.Cs, user, owner); }
    });
}
