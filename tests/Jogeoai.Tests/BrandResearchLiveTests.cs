using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jogeoai.Tests;

// 仅在明确设置只读配置连接和输出文件后运行，普通定向测试不访问真实模型。
public sealed class BrandResearchLiveTests
{
    [ResearchLiveFact]
    public async Task UlinktekWebsiteProducesReviewedIndustryQuestions()
    {
        var connection = Environment.GetEnvironmentVariable("JOGEOAI_RESEARCH_LIVE_CONNECTION")!;
        var report = Environment.GetEnvironmentVariable("JOGEOAI_RESEARCH_REPORT")!;
        var store = new ResearchStore(new MySqlDatabase(connection));
        var settings = (await store.ListProviderSettingsAsync()).First(s => s.Enabled && s.CredentialConfigured);
        var config = (await store.GetProviderConfigurationAsync(settings.Provider))!;
        var services = new ServiceCollection();
        services.AddHttpClient("research", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient("research-web", client => client.Timeout = TimeSpan.FromSeconds(35)).ConfigurePrimaryHttpMessageHandler(ArticleSourceReader.CreatePinnedHandler);
        using var container = services.BuildServiceProvider();
        var factory = container.GetRequiredService<IHttpClientFactory>();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var request = new StartBrandResearchRequest("https://www.szulink.com.cn/", "优联科技（ulinktek）", 10);
        var evidence = await new BrandWebsiteCollector(factory).CollectAsync(request.Website!, request.Language, budget.Token);
        Assert.NotEmpty(evidence);
        var context = ResearchContextRules.Create(request) with { Evidence = evidence };
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(context), budget.Token);
        ResearchProviderResult? result = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                result = await new OpenAiCompatibleResearchProvider(factory).RunAsync(config, request, context,
                    async (c, ct) => { context = c; await File.WriteAllTextAsync(report, JsonSerializer.Serialize(c), ct); }, budget.Token);
                break;
            }
            catch (Exception exception) when (!budget.IsCancellationRequested && attempt < 2 && exception is HttpRequestException or TaskCanceledException)
            {
                // 模拟 Worker 的有界重试，使用已保存画像；真实测试不写入业务任务表。
            }
        }
        Assert.NotNull(result);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(result), budget.Token);
        Assert.Equal(10, result.Suggestion.Prompts.Count);
        Assert.Contains("管道", result.Research!.Profile!.Industry.Value);
        Assert.All(result.Suggestion.Prompts, p => { Assert.NotEmpty(p.EvidenceIds!); Assert.NotEmpty(p.Reason!); });
        Assert.DoesNotContain(result.Suggestion.Prompts, p => p.Prompt.Contains("信息化系统") || p.Prompt.Contains("软件开发"));
    }
    private sealed class ResearchLiveFactAttribute : FactAttribute
    {
        public ResearchLiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_RESEARCH_LIVE_CONNECTION"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_RESEARCH_REPORT"))) Skip = "未启用真实官网及模型的只读验证";
        }
    }
}
