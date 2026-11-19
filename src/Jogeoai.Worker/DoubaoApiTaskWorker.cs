using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Domestic;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>领取 doubao_api 任务并同步调用火山方舟；成功即一次事务写入结果、积分与用量，失败按错误码重试、延后或终态。</summary>
public sealed class DoubaoApiTaskWorker(
    ILogger<DoubaoApiTaskWorker> logger,
    ICloroVisibilityStore store,
    IDoubaoAnswerClient client,
    IOptions<DoubaoWorkerOptions> options)
    : SynchronousAnswerTaskWorker<DoubaoWorkerOptions>(logger, store, options, AnswerProviderDefinition.DoubaoApi,
        "Doubao", "doubao", DoubaoErrorCodes.RateLimited, DoubaoErrorCodes.RequestFailed)
{
    /// <inheritdoc />
    protected override Task<CloroAnswerSnapshot> AskAsync(CloroTaskLease task, CancellationToken cancellationToken) =>
        client.AskAsync(new DoubaoAnswerRequest(task.Prompt, task.Locale, task.Country, task.TaskId, task.AnswerMode), cancellationToken);
}
