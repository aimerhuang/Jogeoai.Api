using System.Collections.Concurrent;
using System.Reflection;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Tests.Mcp;

/// <summary>按方法名分派的 Store 桩；未登记的方法直接失败，便于断言「没有调用」。</summary>
public class McpStoreProxy : DispatchProxy
{
    /// <summary>方法名 → 处理函数。</summary>
    public Dictionary<string, Func<object?[], object?>> Handlers { get; } = [];

    /// <summary>调用记录（方法名与参数）。</summary>
    public ConcurrentQueue<(string Method, object?[] Args)> Calls { get; } = new();

    /// <summary>创建桩。</summary>
    public static (T Store, McpStoreProxy Proxy) Create<T>() where T : class
    {
        var store = DispatchProxy.Create<T, McpStoreProxy>();
        return (store, (McpStoreProxy)(object)store);
    }

    /// <summary>某方法的调用次数。</summary>
    public int Count(string method) => Calls.Count(call => call.Method == method);

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        Calls.Enqueue((method.Name, args ?? []));
        if (!Handlers.TryGetValue(method.Name, out var handler))
            throw new InvalidOperationException($"Unexpected store call: {method.DeclaringType?.Name}.{method.Name}");
        return handler(args ?? []);
    }
}

/// <summary>内存令牌存储：按哈希登记令牌，可模拟缺表（503）。</summary>
public sealed class FakeMcpTokenStore : IMcpTokenStore
{
    private readonly ConcurrentDictionary<string, McpTokenPrincipal> active = new();

    /// <summary>为真时所有方法抛出 503 MCP_SCHEMA_NOT_MIGRATED。</summary>
    public bool SchemaMissing { get; set; }

    /// <summary>TouchLastUsedAsync 调用次数。</summary>
    public int Touches;

    /// <summary>登记一个有效令牌并返回明文。</summary>
    public (string Token, McpTokenPrincipal Principal) Issue(Guid tenantId, IReadOnlyList<Guid>? siteIds = null)
    {
        var token = McpTokenCodec.Generate();
        var principal = new McpTokenPrincipal(Guid.NewGuid(), tenantId, McpTokenCodec.Hash(token), siteIds,
            DateTimeOffset.UtcNow.AddDays(30), null);
        active[principal.TokenHash] = principal;
        return (token, principal);
    }

    /// <summary>模拟吊销或过期：Store 的查询条件排除这类令牌，因此查不到。</summary>
    public void Deactivate(string token) => active.TryRemove(McpTokenCodec.Hash(token), out _);

    /// <inheritdoc />
    public Task<McpTokenView> CreateAsync(McpTokenCreate request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<IReadOnlyList<McpTokenView>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<McpTokenView?> RevokeAsync(Guid tenantId, Guid tokenId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<McpTokenPrincipal?> FindActiveByHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        if (SchemaMissing) throw new CollectorStoreException("MCP_SCHEMA_NOT_MIGRATED", "MCP 访问令牌所需迁移 044 尚未部署。", 503);
        return Task.FromResult(active.TryGetValue(tokenHash, out var principal) ? principal : null);
    }

    /// <inheritdoc />
    public Task TouchLastUsedAsync(Guid tokenId, DateTimeOffset usedAt, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Touches);
        return Task.CompletedTask;
    }
}

/// <summary>收集日志（格式化文本与结构化字段），用于断言审计内容。</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    /// <summary>已记录的日志。</summary>
    public ConcurrentQueue<(string Category, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = new();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>某类别下的日志。</summary>
    public CapturingLogger<T> For<T>() => new(this);

    /// <summary>泛型记录器，供直接构造被测类型使用。</summary>
    public sealed class CapturingLogger<T>(CapturingLoggerProvider owner) : ILogger<T>
    {
        private readonly CapturingLogger inner = new(owner, typeof(T).FullName!);

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    /// <summary>非泛型记录器。</summary>
    public sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            // 在记录时复制结构化字段：框架日志的 state 会延迟读取已释放的 HttpContext。
            => owner.Entries.Enqueue((category, formatter(state, exception),
                state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToArray() : []));
    }
}

/// <summary>构造分析视图的测试数据。</summary>
public static class McpFixtures
{
    /// <summary>品牌视图。</summary>
    public static BrandView Brand(Guid siteId, string timeZone = "Asia/Shanghai")
        => new(siteId, "柯丽尔", "keliere.example", [], [], "CN", "zh-CN", timeZone, "active", 3,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>分析元数据。</summary>
    public static AnalyticsMeta Meta(string snapshotToken = "snapshot-token-0123456789abcdef")
    {
        var from = new DateTimeOffset(2026, 10, 27, 16, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 3, 16, 0, 0, TimeSpan.Zero);
        var filter = new AnalyticsFilter(from, to, "Asia/Shanghai", ["cloro"], [], [], [], ["scraped"], ["observed_surface"],
            [], [], [], "any", null, "observed", [], null, [], []);
        return new AnalyticsMeta(snapshotToken, filter, "v1", 1, 3, false, to, to.AddMinutes(15), to, to, 10, 10, 0, 0, 0);
    }

    /// <summary>核心指标。</summary>
    public static CoreMetrics Metrics(long eligible, long mentions)
        => new(1, eligible, 1, eligible, eligible, 0, 0, 0, 0, 0, 0, MetricRatio.Of(mentions, eligible),
            MetricRatio.Of(mentions, eligible * 2), MetricRatio.Of(0, eligible), MetricRatio.Of(0, eligible), mentions * 2, null);

    /// <summary>单条回答指标。</summary>
    public static CheckBrandMetrics BrandMetrics()
        => new(new RankingMetric(2, 1, 1, null, "check-subject-first-offset-v1"),
            new ExposureScore(65, "exposure-v2", .4, .3, .2, .1, MetricRatio.Of(1, 1), MetricMean.Of(.5, 1),
                MetricRatio.Of(1, 2), MetricRatio.Of(0, 1), null));

    /// <summary>回答详情。</summary>
    public static MonitoringAnswerDetailView Detail(Guid siteId, Guid checkId, string answer, int citations = 2)
        => new(siteId, checkId, Guid.NewGuid(), Guid.NewGuid(), "推荐一款吹风机", "domestic_collector", "doubao", "doubao-1.5",
            "CN", "zh-CN", "succeeded", true, new VisibilityAnswer(answer), ["吹风机 推荐", "高速吹风机"], "available",
            Enumerable.Range(1, citations).Reverse()
                .Select(position => new AnalysisCitationView($"https://news.example/{position}", "news.example", $"标题{position}", position))
                .ToArray(),
            DateTimeOffset.UtcNow, 1200, null, null, null, true, "mentioned", null, null, false, BrandMetrics());

    /// <summary>列表记录。</summary>
    public static MonitoringRecordView Record(Guid checkId)
        => new(checkId, DateTimeOffset.UtcNow, "豆包", "domestic_collector", "doubao", "doubao-1.5", false, "CN", "zh-CN",
            Guid.NewGuid().ToString(), "推荐一款吹风机", "摘要……", true, true, true, "visible", "succeeded", "completed", 1200,
            0, 2, "available", "/details", "mentioned", null, BrandMetrics());
}
