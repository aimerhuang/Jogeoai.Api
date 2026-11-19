using Jogeoai.Domain;

namespace Jogeoai.Api;

/// <summary>把稳定业务异常映射为 <c>{ error: { code, message[, details] } }</c>；套餐上限错误附带结构化 details。</summary>
internal static class StoreErrorResponse
{
    /// <summary>构造错误响应体；<see cref="PlanLimitExceededException"/> 附加 planCode / limit / limitValue / currentValue。</summary>
    internal static object Body(CollectorStoreException exception) => exception is PlanLimitExceededException limit
        ? new
        {
            error = new
            {
                code = exception.Code,
                message = exception.Message,
                details = new { planCode = limit.PlanCode, limit = limit.Limit, limitValue = limit.LimitValue, currentValue = limit.CurrentValue },
            },
        }
        : new { error = new { code = exception.Code, message = exception.Message } };
}
