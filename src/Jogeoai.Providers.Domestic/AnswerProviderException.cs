namespace Jogeoai.Providers.Domestic;

/// <summary>
/// 同步 API 回答 Provider（豆包、DeepSeek）外部调用的安全错误基类；Worker 按 <see cref="Code"/>、<see cref="Retryable"/>
/// 与 <see cref="DeferMinutes"/> 决定重试、延后或终态。消息只含中文说明和已校验的错误标识，不含响应正文或凭据。
/// </summary>
/// <param name="code">稳定错误码（如 DOUBAO_*、DEEPSEEK_*）。</param>
/// <param name="message">安全的中文说明。</param>
/// <param name="retryable">是否允许重试当前调用。</param>
/// <param name="deferMinutes">建议延后的分钟数；为空表示按普通退避重试或不重试。</param>
public abstract class AnswerProviderException(string code, string message, bool retryable, int? deferMinutes = null) : Exception(message)
{
    /// <summary>稳定错误码。</summary>
    public string Code { get; } = code;
    /// <summary>是否允许重试当前调用。</summary>
    public bool Retryable { get; } = retryable;
    /// <summary>建议延后的分钟数（配额、欠费或余额不足时为 10）；为空表示按普通退避重试或不重试。</summary>
    public int? DeferMinutes { get; } = deferMinutes;
}
