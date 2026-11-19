namespace Jogeoai.Domain;

/// <summary>豆包适配器的稳定错误码；定义在 Domain 以便 Infrastructure 的 SafeErrorCode 白名单与 Worker 的重试策略共用，且不引入 Providers.Domestic 引用。</summary>
public static class DoubaoErrorCodes
{
    /// <summary>设置未启用或未保存凭据；终态，不重试。</summary>
    public const string NotConfigured = "DOUBAO_NOT_CONFIGURED";
    /// <summary>401 / 账号状态异常 / 其他 403；终态，不重试。</summary>
    public const string AuthFailed = "DOUBAO_AUTH_FAILED";
    /// <summary>豆包搜索或模型未开通、接入点无效（403 AccessDenied / ServiceNotOpen、404）；终态。</summary>
    public const string ServiceNotOpen = "DOUBAO_SERVICE_NOT_OPEN";
    /// <summary>欠费或配额用尽（403 Overdue、429 Quota*）；延后重试，DeferMinutes=10。</summary>
    public const string QuotaExceeded = "DOUBAO_QUOTA_EXCEEDED";
    /// <summary>限流或过载（其他 429、RequestBurstTooFast、ServerOverloaded）；可退避重试。</summary>
    public const string RateLimited = "DOUBAO_RATE_LIMITED";
    /// <summary>问题触发平台输入审核；终态。</summary>
    public const string InputFiltered = "DOUBAO_INPUT_FILTERED";
    /// <summary>参数无效或未支持的请求模式；终态。</summary>
    public const string InvalidRequest = "DOUBAO_INVALID_REQUEST";
    /// <summary>408 / 5xx / 网络错误；可重试。</summary>
    public const string RequestFailed = "DOUBAO_REQUEST_FAILED";
    /// <summary>超过设置的超时秒数；可重试。</summary>
    public const string Timeout = "DOUBAO_TIMEOUT";
    /// <summary>响应不是合法 JSON 或缺少 output；终态。</summary>
    public const string InvalidResponse = "DOUBAO_INVALID_RESPONSE";

    /// <summary>全部错误码，供 Store 白名单与测试枚举。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        NotConfigured, AuthFailed, ServiceNotOpen, QuotaExceeded, RateLimited, InputFiltered, InvalidRequest,
        RequestFailed, Timeout, InvalidResponse,
    ];
}
