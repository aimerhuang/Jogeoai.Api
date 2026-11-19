namespace Jogeoai.Domain;

/// <summary>DeepSeek 适配器的稳定错误码；定义在 Domain 以便 Infrastructure 的 SafeErrorCode 白名单与 Worker 的重试策略共用，且不引入 Providers.Domestic 引用。</summary>
public static class DeepSeekErrorCodes
{
    /// <summary>设置未启用或未保存凭据；终态，不重试。</summary>
    public const string NotConfigured = "DEEPSEEK_NOT_CONFIGURED";
    /// <summary>401 / 403：凭据无效或无权访问；终态，不重试。</summary>
    public const string AuthFailed = "DEEPSEEK_AUTH_FAILED";
    /// <summary>402：账户余额不足；延后重试，DeferMinutes=10，最长等待后终态并释放积分预留。</summary>
    public const string InsufficientBalance = "DEEPSEEK_INSUFFICIENT_BALANCE";
    /// <summary>429 / 503 / 529 / overloaded_error：限流或过载；可退避重试。</summary>
    public const string RateLimited = "DEEPSEEK_RATE_LIMITED";
    /// <summary>400 / 404 / 413 / 422 或不支持的选项：参数无效；终态。</summary>
    public const string InvalidRequest = "DEEPSEEK_INVALID_REQUEST";
    /// <summary>408 / 5xx / 网络错误 / 读体中断 / stop_reason=pause_turn；可重试。</summary>
    public const string RequestFailed = "DEEPSEEK_REQUEST_FAILED";
    /// <summary>超过设置的超时秒数；可重试。</summary>
    public const string Timeout = "DEEPSEEK_TIMEOUT";
    /// <summary>响应不是合法 JSON、缺少 content 或 stop_reason=max_tokens（回答被截断）；终态。</summary>
    public const string InvalidResponse = "DEEPSEEK_INVALID_RESPONSE";

    /// <summary>全部错误码，供 Store 白名单与测试枚举。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        NotConfigured, AuthFailed, InsufficientBalance, RateLimited, InvalidRequest, RequestFailed, Timeout, InvalidResponse,
    ];
}
