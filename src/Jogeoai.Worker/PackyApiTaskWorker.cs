using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Domestic;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>
/// PackyAPI 任务 Worker 的运行参数；四个 Provider 共用配置节 <c>Packy:Worker</c>（默认 BatchSize 2、MaxConcurrency 2、LeaseSeconds 600，
/// 每个 Provider 各自一份并发上限）。<c>Packy:Worker:Enabled=false</c> 时四个 Worker 都不注册，用于回滚。
/// </summary>
public sealed class PackyWorkerOptions : ApiAnswerWorkerOptions
{
    /// <summary>创建默认参数；租约默认 600 秒，覆盖千问完整回答的 540 秒超时（实测单次 2–4 分钟以上，2026-10-29）。</summary>
    public PackyWorkerOptions() => LeaseSeconds = 600;
}

/// <summary>领取一个 PackyAPI Provider 的任务并同步调用 PackyAPI；成功即一次事务写入结果、积分与用量，失败按错误码重试、延后或终态。</summary>
public abstract class PackyApiTaskWorker(
    ILogger logger,
    ICloroVisibilityStore store,
    IPackyAnswerClient client,
    IOptions<PackyWorkerOptions> options,
    AnswerProviderDefinition definition,
    string name)
    : SynchronousAnswerTaskWorker<PackyWorkerOptions>(logger, store, options, definition, name, definition.Provider.Replace('_', '-'),
        PackyErrorCodes.RateLimited, PackyErrorCodes.RequestFailed)
{
    /// <inheritdoc />
    protected override Task<CloroAnswerSnapshot> AskAsync(CloroTaskLease task, CancellationToken cancellationToken) =>
        client.AskAsync(new PackyAnswerRequest(definition.Provider, task.Prompt, task.Locale, task.Country, task.TaskId,
            task.AnswerMode), cancellationToken);
}

/// <summary>领取 packy_qwen（千问）任务。</summary>
public sealed class PackyQwenTaskWorker(ILogger<PackyQwenTaskWorker> logger, ICloroVisibilityStore store, IPackyAnswerClient client,
    IOptions<PackyWorkerOptions> options)
    : PackyApiTaskWorker(logger, store, client, options, AnswerProviderDefinition.PackyQwen, "PackyQwen");

/// <summary>领取 packy_glm（智谱清言）任务。</summary>
public sealed class PackyGlmTaskWorker(ILogger<PackyGlmTaskWorker> logger, ICloroVisibilityStore store, IPackyAnswerClient client,
    IOptions<PackyWorkerOptions> options)
    : PackyApiTaskWorker(logger, store, client, options, AnswerProviderDefinition.PackyGlm, "PackyGlm");

/// <summary>领取 packy_kimi（Kimi）任务。</summary>
public sealed class PackyKimiTaskWorker(ILogger<PackyKimiTaskWorker> logger, ICloroVisibilityStore store, IPackyAnswerClient client,
    IOptions<PackyWorkerOptions> options)
    : PackyApiTaskWorker(logger, store, client, options, AnswerProviderDefinition.PackyKimi, "PackyKimi");

/// <summary>领取 packy_yuanbao（元宝，混元 hy4）任务。</summary>
public sealed class PackyYuanbaoTaskWorker(ILogger<PackyYuanbaoTaskWorker> logger, ICloroVisibilityStore store, IPackyAnswerClient client,
    IOptions<PackyWorkerOptions> options)
    : PackyApiTaskWorker(logger, store, client, options, AnswerProviderDefinition.PackyYuanbao, "PackyYuanbao");
