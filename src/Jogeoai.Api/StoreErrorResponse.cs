using Jogeoai.Domain;

namespace Jogeoai.Api;

/// <summary>
/// 把稳定业务异常映射为 <c>{ error: { code, message[, details] } }</c>；套餐上限错误和“已有进行中的研究任务”附带结构化 details。
/// </summary>
internal static class StoreErrorResponse
{
    /// <summary>
    /// 构造错误响应体；<see cref="PlanLimitExceededException"/> 附加 planCode / limit / limitValue / currentValue，
    /// <see cref="ResearchAlreadyActiveException"/> 附加 jobId（现有研究任务）。
    /// </summary>
    internal static object Body(CollectorStoreException exception) => exception switch
    {
        PlanLimitExceededException limit => new
        {
            error = new
            {
                code = exception.Code,
                message = exception.Message,
                details = new { planCode = limit.PlanCode, limit = limit.Limit, limitValue = limit.LimitValue, currentValue = limit.CurrentValue },
            },
        },
        ResearchAlreadyActiveException active => new
        {
            error = new
            {
                code = exception.Code,
                message = exception.Message,
                details = new { jobId = active.JobId },
            },
        },
        _ => new { error = new { code = exception.Code, message = exception.Message } },
    };
}
