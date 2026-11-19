using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>团队默认监控配置的纯函数部分（不访问数据库）：单元格校验与错误码、默认模式折叠、签名、物化的地区规则、JSON 形态。</summary>
public sealed class TeamMonitoringConfigTests
{
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static CollectorStoreException Rejects(IReadOnlyList<TeamMonitoringCellWrite>? cells, bool enforce = true)
        => Assert.Throws<CollectorStoreException>(() => ManagementStore.ValidateTeamCells(cells, enforce));

    [Fact]
    public void DefaultModesFoldToEmptyAndNonDefaultModesAreKept()
    {
        var cells = ManagementStore.ValidateTeamCells([
            new("cloro", "ChatGPT"), new(" DeepSeek_API ", "deepseek", AnswerMode.Deep, MonitoringFrequency.TwiceDaily),
            new("deepseek_api", "deepseek", AnswerMode.Fast), new("doubao_api", "doubao", AnswerMode.Deep), new("doubao_api", "doubao", AnswerMode.Fast)]);
        Assert.Equal(("cloro", "chatgpt", (string?)null, AnswerMode.Fast, MonitoringFrequency.Daily),
            (cells[0].Provider, cells[0].Engine, cells[0].StoredMode, cells[0].Mode, cells[0].Frequency));
        Assert.Equal(("deepseek_api", "deep", AnswerMode.Deep, MonitoringFrequency.TwiceDaily),
            (cells[1].Provider, cells[1].StoredMode, cells[1].Mode, cells[1].Frequency));
        Assert.Equal(((string?)null, AnswerMode.Fast), (cells[2].StoredMode, cells[2].Mode));
        // Doubao defaults to deep: deep folds, fast is stored.
        Assert.Equal(((string?)null, AnswerMode.Deep), (cells[3].StoredMode, cells[3].Mode));
        Assert.Equal(("fast", AnswerMode.Fast), (cells[4].StoredMode, cells[4].Mode));

        Assert.Equal("cloro|chatgpt||daily", cells[0].Signature);
        Assert.Equal("deepseek_api|deepseek|deep|twice_daily", cells[1].Signature);
        Assert.Equal("doubao_api|doubao|fast|daily", TeamMonitoringRules.Signature("doubao_api", "doubao", "fast", MonitoringFrequency.Daily));
    }

    [Fact]
    public void ErrorMatrixUsesTheSiteTargetCodes()
    {
        Assert.Equal(("MONITORING_TARGETS_REQUIRED", 400), Code(Rejects(null)));
        Assert.Equal(("MONITORING_TARGETS_REQUIRED", 400), Code(Rejects([])));
        Assert.Equal(("MONITORING_TARGETS_REQUIRED", 400), Code(Rejects([], enforce: false)));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(Rejects([new("domestic_collector", "doubao")])));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(Rejects([new("unknown", "x")])));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(Rejects([new("cloro", "deepseek")])));
        Assert.Equal(("ANSWER_MODE_UNSUPPORTED", 400), Code(Rejects([new("cloro", "chatgpt", AnswerMode.Deep)])));
        Assert.Equal(("ANSWER_MODE_UNSUPPORTED", 400), Code(Rejects([new("packy_yuanbao", "tencent_yuanbao", AnswerMode.Fast)])));
        Assert.Equal(("FREQUENCY_INVALID", 400), Code(Rejects([new("cloro", "chatgpt", null, (MonitoringFrequency)9)])));
        // Omitted and explicit default mode are the same cell; another frequency does not make it distinct.
        Assert.Equal(("DUPLICATE_TARGET", 400), Code(Rejects([new("deepseek_api", "deepseek"), new("deepseek_api", "deepseek", AnswerMode.Fast, MonitoringFrequency.Weekly)])));
        // Fast and deep of one engine are two cells.
        Assert.Equal(2, ManagementStore.ValidateTeamCells([new("deepseek_api", "deepseek"), new("deepseek_api", "deepseek", AnswerMode.Deep)]).Length);

        // More than 20 cells: 400 when saving; the estimate validates the cells themselves (21 cells always repeat one).
        var many = Enumerable.Range(0, 21).Select(_ => new TeamMonitoringCellWrite("cloro", "chatgpt")).ToArray();
        Assert.Equal(("MONITORING_TOO_LARGE", 400), Code(Rejects(many)));
        Assert.Equal(("DUPLICATE_TARGET", 400), Code(Rejects(many, enforce: false)));
        Assert.Equal(("INVALID_TARGETS", 400), Code(Rejects(Enumerable.Range(0, 31).Select(_ => new TeamMonitoringCellWrite("cloro", "chatgpt")).ToArray(), enforce: false)));
    }

    [Fact]
    public void EveryCatalogCellIsAcceptedAndTheWholeCatalogFitsTheLimit()
    {
        var all = AnswerProviderDefinition.All.SelectMany(d => d.Engines.SelectMany(e => d.SupportedAnswerModes.Select(m => new TeamMonitoringCellWrite(d.Provider, e, m))))
            .ToArray();
        Assert.InRange(all.Length, 1, Jogeoai.Domain.MonitoringMatrix.MaxTargets);
        Assert.Equal(all.Length, ManagementStore.ValidateTeamCells(all).Length);
    }

    private static readonly IReadOnlyList<CollectionTargetView> Catalog =
    [
        new(null, "cloro", "chatgpt", "chatgpt", "scraped", "observed_surface", "US", "en-US", true, "available", "supported", "conditional", "conditional"),
        new(null, "cloro", "gemini", "gemini", "scraped", "observed_surface", "US", "en-US", true, "available", "supported", "conditional", "conditional"),
    ];

    [Fact]
    public void MaterializationKeepsBrandRegionsAndFallsBackToCatalogAndProviderDefaults()
    {
        var cells = ManagementStore.ValidateTeamCells([
            new("cloro", "chatgpt", null, MonitoringFrequency.Weekly), new("cloro", "gemini"),
            new("deepseek_api", "deepseek", AnswerMode.Deep), new("packy_kimi", "kimi")]);
        var existing = new ManagementStore.ExistingTargetRow[]
        {
            // Disabled US row and enabled CN row: the enabled row wins, then country / locale order.
            new("cloro", "chatgpt", "US", "zh-CN", false),
            new("cloro", "chatgpt", "JP", "ja-JP", true),
            new("cloro", "chatgpt", "CN", "zh-CN", true),
            new("cloro", "perplexity", "GB", "en-GB", true),
        };
        var targets = ManagementStore.MaterializeCells(cells, existing, "zh-CN", Catalog);
        Assert.Equal(4, targets.Length);
        Assert.Equal(new SiteTargetWrite("cloro", "chatgpt", "scraped", "CN", "zh-CN", true, AnswerMode.Fast, MonitoringFrequency.Weekly), targets[0]);
        // No existing gemini row: catalog country + brand locale (PromptMonitoringDefaults.Target rule).
        Assert.Equal(new SiteTargetWrite("cloro", "gemini", "scraped", "US", "zh-CN", true, AnswerMode.Fast), targets[1]);
        // Pinned providers always use their fixed surface.
        Assert.Equal(new SiteTargetWrite("deepseek_api", "deepseek", "api", "CN", "zh-CN", true, AnswerMode.Deep), targets[2]);
        Assert.Equal(new SiteTargetWrite("packy_kimi", "kimi", "api", "CN", "zh-CN", true, AnswerMode.Fast), targets[3]);

        // Only disabled rows: the first by country / locale. Every materialized target passes the site-target validation.
        targets = ManagementStore.MaterializeCells(cells[..1], [new("cloro", "chatgpt", "US", "zh-CN", false), new("cloro", "chatgpt", "DE", "de-DE", false)], "en-US", Catalog);
        Assert.Equal(("DE", "de-DE"), (targets[0].Country, targets[0].Locale));
        var normalized = ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1,
            ManagementStore.MaterializeCells(cells, existing, "zh-CN", Catalog), SiteTargetsScope.Monitoring));
        // The default mode folds to NULL, so a default-mode cell keeps the legacy key.
        Assert.Equal(CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "chatgpt", "zh-CN", "CN")), normalized[0].Key);
        Assert.Equal("deep", normalized[2].StoredMode);
    }

    [Fact]
    public void BaselineIsTheFirstMonitoringTarget()
    {
        var baseline = Assert.Single(ManagementStore.ValidateTeamCells([TeamMonitoringRules.Baseline]));
        Assert.Equal("cloro|chatgpt||daily", baseline.Signature);
        // Without any existing row the baseline materializes exactly as PromptMonitoringDefaults.Target does.
        var target = Assert.Single(ManagementStore.MaterializeCells([baseline], [], "zh-CN", Catalog));
        var legacy = PromptMonitoringDefaults.Target(Catalog, "zh-CN");
        Assert.Equal(ManagementStore.NormalizeTarget(legacy).Key, ManagementStore.NormalizeTarget(target).Key);
    }

    [Fact]
    public void CatalogOrderFollowsTheProviderRegistry()
    {
        var ordered = new[] { ("packy_kimi", "kimi", AnswerMode.Fast), ("cloro", "gemini", AnswerMode.Fast), ("deepseek_api", "deepseek", AnswerMode.Deep),
                ("deepseek_api", "deepseek", AnswerMode.Fast), ("cloro", "chatgpt", AnswerMode.Fast) }
            .OrderBy(c => TeamMonitoringRules.CatalogOrder(c.Item1, c.Item2, c.Item3)).Select(c => $"{c.Item2}:{c.Item3}").ToArray();
        Assert.Equal(["chatgpt:Fast", "gemini:Fast", "deepseek:Fast", "deepseek:Deep", "kimi:Fast"], ordered);
    }

    [Fact]
    public void WireShapesUseCamelCaseAndStorageEnumNames()
    {
        var write = JsonSerializer.Deserialize<TeamMonitoringConfigWrite>(
            """{"expectedVersion":0,"cells":[{"provider":"cloro","engine":"chatgpt"},{"provider":"deepseek_api","engine":"deepseek","answerMode":"deep","frequency":"twice_daily"}]}""", Api)!;
        Assert.Equal(0u, write.ExpectedVersion);
        Assert.Equal((null, MonitoringFrequency.Daily), (write.Cells[0].AnswerMode, write.Cells[0].Frequency));
        Assert.Equal((AnswerMode.Deep, MonitoringFrequency.TwiceDaily), (write.Cells[1].AnswerMode, write.Cells[1].Frequency));

        var view = new TeamMonitoringConfigView(3, true, [new("cloro", "chatgpt", "chatgpt", AnswerMode.Fast, MonitoringFrequency.EveryOtherDay)],
            [new(Guid.Empty, "优联", false, true, 12, [])]);
        var json = JsonSerializer.Serialize(view, Api);
        Assert.Contains("\"configVersion\":3", json);
        Assert.Contains("\"canEdit\":true", json);
        Assert.Contains("\"answerMode\":\"fast\"", json);
        Assert.Contains("\"frequency\":\"every_other_day\"", json);
        Assert.Contains("\"followsTeamDefault\":false", json);
        Assert.Contains("\"monitoringStarted\":true", json);
        Assert.Contains("\"targetConfigVersion\":12", json);
        Assert.Contains("\"followsTeamDefault\":true", JsonSerializer.Serialize(new SiteTargetsView(1, [], true), Api));
        Assert.Contains("\"followsTeamDefault\":false", JsonSerializer.Serialize(new SiteTargetsView(1, []), Api));
    }

    private static (string, int) Code(CollectorStoreException exception) => (exception.Code, exception.StatusCode);
}
