using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Domestic;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>领取 deepseek_api 任务并同步调用 DeepSeek Anthropic 兼容接口；成功即一次事务写入结果、积分与用量，失败按错误码重试、延后或终态。</summary>
public sealed class DeepSeekApiTaskWorker(
    ILogger<DeepSeekApiTaskWorker> logger,
    ICloroVisibilityStore store,
    IDeepSeekAnswerClient client,
    IOptions<DeepSeekWorkerOptions> options)
    : SynchronousAnswerTaskWorker<DeepSeekWorkerOptions>(logger, store, options, AnswerProviderDefinition.DeepSeekApi,
        "DeepSeek", "deepseek", DeepSeekErrorCodes.RateLimited, DeepSeekErrorCodes.RequestFailed)
{
    /// <inheritdoc />
    protected override Task<CloroAnswerSnapshot> AskAsync(CloroTaskLease task, CancellationToken cancellationToken) =>
        client.AskAsync(new DeepSeekAnswerRequest(task.Prompt, task.Locale, task.Country, task.TaskId, task.AnswerMode), cancellationToken);
}
