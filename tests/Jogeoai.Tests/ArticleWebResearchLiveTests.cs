using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jogeoai.Tests;

// 仅在明确设置只读配置连接和输出文件后运行：读取 deepseek-article 设置行，对真实 DeepSeek web_search 做一次调用（不重试），
// 报告只写摘要长度、来源与搜索次数，不写凭据。普通定向测试不访问真实模型。
public sealed class ArticleWebResearchLiveTests
{
    [ArticleWebResearchLiveFact]
    public async Task DeepSeekArticleKeyReturnsSearchSourcesAndSummary()
    {
        var connection = Environment.GetEnvironmentVariable("JOGEOAI_ARTICLE_WEB_RESEARCH_LIVE_CONNECTION")!;
        var report = Environment.GetEnvironmentVariable("JOGEOAI_ARTICLE_WEB_RESEARCH_REPORT")!;
        var store = new ResearchStore(new MySqlDatabase(connection));
        var config = await store.GetProviderConfigurationAsync(ArticleGenerationProvider.ProviderName);
        Assert.NotNull(config);
        Assert.True(config!.WebSearchEnabled, "deepseek-article 未开启 web_search_enabled。");

        var services = new ServiceCollection();
        services.AddHttpClient(ArticleWebResearchService.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        using var container = services.BuildServiceProvider();
        var service = new ArticleWebResearchService(container.GetRequiredService<IHttpClientFactory>());
        var request = new ArticleWebResearchRequest("工业管道内窥镜国内有哪些非常耐用的产品", "zh-CN",
            ArticleOptimizationGoal.Ranking, []);

        var started = DateTimeOffset.UtcNow;
        var result = await service.ResearchAsync(config, request);
        var elapsed = DateTimeOffset.UtcNow - started;
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            result.Skipped,
            result.SkipReason,
            result.SearchCount,
            SummaryLength = result.Summary?.Length ?? 0,
            SummaryHead = result.Summary is null ? null : result.Summary[..Math.Min(300, result.Summary.Length)],
            Sources = result.Results,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        Assert.False(result.Skipped, $"联网搜索被跳过：{result.SkipReason}");
        Assert.True(result.SearchCount >= 1);
        Assert.NotEmpty(result.Results);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }

    private sealed class ArticleWebResearchLiveFactAttribute : FactAttribute
    {
        public ArticleWebResearchLiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_ARTICLE_WEB_RESEARCH_LIVE_CONNECTION"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_ARTICLE_WEB_RESEARCH_REPORT")))
                Skip = "未启用文章创作联网搜索的真实验证";
        }
    }
}
