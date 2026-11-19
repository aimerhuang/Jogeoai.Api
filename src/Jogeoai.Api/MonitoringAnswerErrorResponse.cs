namespace Jogeoai.Api;

/// <summary>回答详情接口的结构化失败响应，与现有错误外壳保持一致。</summary>
/// <param name="Error">面向调用方的错误代码和说明。</param>
public sealed record MonitoringAnswerErrorResponse(MonitoringAnswerError Error);

/// <summary>回答详情请求的公开错误信息，不包含内部异常或凭据。</summary>
/// <param name="Code">稳定的业务错误代码。</param>
/// <param name="Message">面向用户的错误说明。</param>
public sealed record MonitoringAnswerError(string Code, string Message);
