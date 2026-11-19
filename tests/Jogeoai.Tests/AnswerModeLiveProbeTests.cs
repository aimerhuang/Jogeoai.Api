using System.Diagnostics;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Providers.Domestic;
using Microsoft.Extensions.DependencyInjection;

namespace Jogeoai.Tests;

// 阶段 0 回答模式实测（Trellis 11-09-brand-model-modes）：仅在设置只读配置连接和报告文件后运行。
// 读取 answer_provider_settings 的已保存配置，只在内存中覆盖 thinking / reasoningEffort，不写库、不改全局设置，
// 每个组合调用一次真实 API（不重试）。报告只写长度、计数和响应块类型，不写凭据或推理正文。
public sealed class AnswerModeLiveProbeTests
{
    private const string Prompt = "工业管道内窥镜国内有哪些非常耐用的产品";

    private static readonly ProbeCase[] Cases =
    [
        new("deepseek_api", "fast", "disabled", null),
        new("deepseek_api", "deep", "enabled", null),
        new("packy_qwen", "deep", "enabled", null),
        new("packy_glm", "fast", "disabled", "low"),
        new("packy_glm", "deep", "enabled", "high"),
        new("packy_kimi", "deep", "enabled", null),
        new("doubao_api", "fast", "disabled", null),
        new("packy_yuanbao", "fast", "disabled", null),
    ];

    [AnswerModeLiveProbeFact]
    public async Task ProbeAnswerModes()
    {
        var connection = Environment.GetEnvironmentVariable("JOGEOAI_ANSWER_MODE_PROBE_CONNECTION")!;
        var report = Environment.GetEnvironmentVariable("JOGEOAI_ANSWER_MODE_PROBE_REPORT")!;
        var only = Environment.GetEnvironmentVariable("JOGEOAI_ANSWER_MODE_PROBE_ONLY");
        var stored = new AnswerProviderSettingsStore(new MySqlDatabase(connection));
        var results = new List<object>();
        foreach (var probe in Cases)
        {
            if (!string.IsNullOrWhiteSpace(only) && !only.Split(',').Contains($"{probe.Provider}:{probe.Mode}")) continue;
            var overriding = new OverridingSettingsStore(stored, probe);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAnswerProviderSettingsStore>(overriding);
            services.AddDeepSeekProvider().AddDoubaoProvider().AddPackyProvider();
            await using var container = services.BuildServiceProvider();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var snapshot = probe.Provider switch
                {
                    "deepseek_api" => await container.GetRequiredService<IDeepSeekAnswerClient>().AskAsync(new DeepSeekAnswerRequest(Prompt)),
                    "doubao_api" => await container.GetRequiredService<IDoubaoAnswerClient>().AskAsync(new DoubaoAnswerRequest(Prompt)),
                    _ => await container.GetRequiredService<IPackyAnswerClient>().AskAsync(new PackyAnswerRequest(probe.Provider, Prompt)),
                };
                var types = new Dictionary<string, int>(StringComparer.Ordinal);
                CountTypes(snapshot.RawOutput, types);
                results.Add(new
                {
                    probe.Provider, probe.Mode, Sent = new { overriding.Effective?.Thinking, overriding.Effective?.ReasoningEffort, overriding.Effective?.RequestMode },
                    Ok = true, ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
                    snapshot.ModelVersion, AnswerLength = snapshot.AnswerText.Length,
                    AnswerHead = snapshot.AnswerText[..Math.Min(120, snapshot.AnswerText.Length)],
                    Citations = snapshot.Citations.Count, WebQueries = snapshot.WebQueries.Count, snapshot.QueryAvailability,
                    ReasoningBlocks = types.Where(t => t.Key.Contains("thinking") || t.Key.Contains("reasoning")).ToDictionary(),
                    BlockTypes = types,
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var code = exception.GetType().GetProperty("Code")?.GetValue(exception) as string;
                results.Add(new
                {
                    probe.Provider, probe.Mode, Sent = new { overriding.Effective?.Thinking, overriding.Effective?.ReasoningEffort, overriding.Effective?.RequestMode },
                    Ok = false, ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
                    ErrorType = exception.GetType().Name, ErrorCode = code, Message = exception.Message,
                });
            }
        }
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Assert.NotEmpty(results);
    }

    private static void CountTypes(JsonElement element, Dictionary<string, int> types)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("type") && property.Value.ValueKind == JsonValueKind.String)
                        types[property.Value.GetString()!] = types.GetValueOrDefault(property.Value.GetString()!) + 1;
                    CountTypes(property.Value, types);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CountTypes(item, types);
                break;
        }
    }

    private sealed record ProbeCase(string Provider, string Mode, string? Thinking, string? ReasoningEffort);

    private sealed class OverridingSettingsStore(IAnswerProviderSettingsStore inner, ProbeCase probe) : IAnswerProviderSettingsStore
    {
        public AnswerProviderOptions? Effective { get; private set; }

        public Task<AnswerProviderSettingsView?> GetAsync(string provider, CancellationToken cancellationToken = default) =>
            inner.GetAsync(provider, cancellationToken);

        public async Task<AnswerProviderConfiguration?> GetConfigurationAsync(string provider, CancellationToken cancellationToken = default)
        {
            var configuration = await inner.GetConfigurationAsync(provider, cancellationToken);
            if (configuration is null || provider != probe.Provider) return configuration;
            Effective = configuration.Options with { Thinking = probe.Thinking, ReasoningEffort = probe.ReasoningEffort };
            return configuration with { Options = Effective };
        }

        public Task<AnswerProviderSettingsView> SaveAsync(string provider, SaveAnswerProviderSettingsRequest request,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("实测不写入设置。");
    }

    private sealed class AnswerModeLiveProbeFactAttribute : FactAttribute
    {
        public AnswerModeLiveProbeFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_ANSWER_MODE_PROBE_CONNECTION"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_ANSWER_MODE_PROBE_REPORT")))
                Skip = "未启用回答模式真实实测";
        }
    }
}
