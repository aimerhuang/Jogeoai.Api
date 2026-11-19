namespace Jogeoai.Domain;

/// <summary>PackyAPI 适配器（packy_qwen、packy_glm、packy_kimi、packy_yuanbao 共用）的稳定错误码；定义在 Domain 以便 Infrastructure 的 SafeErrorCode 白名单与 Worker 的重试策略共用。</summary>
public static class PackyErrorCodes
{
    /// <summary>设置未启用或未保存凭据；终态，不重试。</summary>
    public const string NotConfigured = "PACKY_NOT_CONFIGURED";
    /// <summary>401 / 403：凭据无效或无权访问；终态，不重试。</summary>
    public const string AuthFailed = "PACKY_AUTH_FAILED";
    /// <summary>402：账户余额不足；延后重试，DeferMinutes=10，最长等待后终态并释放积分预留。</summary>
    public const string InsufficientBalance = "PACKY_INSUFFICIENT_BALANCE";
    /// <summary>429 / 503 / 529（不含 model_not_found）：限流或过载；可退避重试。</summary>
    public const string RateLimited = "PACKY_RATE_LIMITED";
    /// <summary>400 / 404 / 413 / 422、model_not_found（分组下没有渠道）或不支持的选项：参数无效；终态。</summary>
    public const string InvalidRequest = "PACKY_INVALID_REQUEST";
    /// <summary>408 / 5xx / 网络错误 / 读体中断 / 流中断或 failed 状态 / pause_turn；可重试。</summary>
    public const string RequestFailed = "PACKY_REQUEST_FAILED";
    /// <summary>超过设置的超时秒数；可重试。</summary>
    public const string Timeout = "PACKY_TIMEOUT";
    /// <summary>响应不是合法结构，或回答因输出上限被截断；终态。</summary>
    public const string InvalidResponse = "PACKY_INVALID_RESPONSE";
    /// <summary>搜索后没有正文，或正文漏出原始工具调用标记；可重试。</summary>
    public const string IncompleteAnswer = "PACKY_INCOMPLETE_ANSWER";

    /// <summary>全部错误码，供 Store 白名单与测试枚举。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        NotConfigured, AuthFailed, InsufficientBalance, RateLimited, InvalidRequest, RequestFailed, Timeout, InvalidResponse,
        IncompleteAnswer,
    ];
}
