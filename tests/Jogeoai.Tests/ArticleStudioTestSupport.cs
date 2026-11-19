using System.Reflection;
using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>文章创作测试共用的接口替身与数据构造；不连接数据库、不发 HTTP。</summary>
internal static class StudioFakes
{
    internal static T Of<T>(Func<string, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, StudioProxy>();
        ((StudioProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    internal static T Unused<T>() where T : class
        => Of<T>((method, _) => throw new InvalidOperationException($"Unexpected call {typeof(T).Name}.{method}"));

    internal static PromptView Prompt(Guid id, string content = "合成品类如何选型？", string locale = "zh-CN", Guid? groupId = null)
        => new(id, content, 1, locale, true, [], "unbranded", "auto", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 1, groupId);

    internal static CoreMetrics Metrics(long eligible)
        => new(1, eligible, 1, eligible, eligible, 0, 0, 0, 0, 0, 0, MetricRatio.Of(0, eligible), MetricRatio.Of(0, 0),
            MetricRatio.Of(0, eligible), MetricRatio.Of(0, eligible), 0, null);

    internal static VoiceProjection Group(long eligible, params (Guid Id, string Name, bool Brand, long Mentions)[] entries)
        => new(Metrics(eligible), entries.Select((entry, index) => new SubjectShareEntry(entry.Id, entry.Name, entry.Brand,
            entry.Mentions, MetricRatio.Of(entry.Mentions, 10), 1, index + 1)).ToArray());

    internal static ShareOfVoiceView Sov(params VoiceProjection[] groups)
        => new(null!, Metrics(0), [], [], MetricRatio.Of(0, 0), [], Metrics(0),
            groups.Select((group, index) => (Key: $"source-{index}", group)).ToDictionary(item => item.Key, item => item.group));

    internal static ArticleGenerationWorkItem WorkItem(string? requirements = null, bool withoutRag = true,
        string pipeline = ArticleStudioLimits.StudioPipeline, Guid? promptId = null, bool includeCompetitors = false)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "{\"promptId\":\"" + (promptId ?? Guid.NewGuid()) + "\",\"content\":\"合成品类如何选型？\"}", "[]", null, null, null, null,
            "zh-CN", "answer_guide", "medium", "professional", null, [], requirements, null, null, withoutRag, 1,
            $"worker-{Guid.NewGuid():N}", 100m, pipeline, includeCompetitors, ArticleOptimizationGoal.Ranking, promptId ?? Guid.NewGuid());

    internal static ArticleGenerationResult Result()
        => new("合成标题", null, new string('字', 1600), null, null, [], true, [], ["待核验"], "AI 辅助生成",
            "deepseek-article", "deepseek-flash", "article-writing-v2", "hash", 10, 10);
}

/// <summary>把接口调用转给委托的代理。</summary>
public class StudioProxy : DispatchProxy
{
    /// <summary>调用处理器：方法名与参数。</summary>
    public Func<string, object?[]?, object?> Handler { get; set; } = (_, _) => throw new InvalidOperationException();

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!.Name, args);
}

/// <summary>每次读取前进 250 毫秒的可控时钟。</summary>
internal sealed class SteppingClock(DateTimeOffset start, TimeSpan? step = null) : TimeProvider
{
    private DateTimeOffset now = start;
    private readonly TimeSpan increment = step ?? TimeSpan.FromMilliseconds(250);

    public override DateTimeOffset GetUtcNow()
    {
        var current = now;
        now = now.Add(increment);
        return current;
    }
}
