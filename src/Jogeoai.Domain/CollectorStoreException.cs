namespace Jogeoai.Domain;

/// <summary>采集控制面可向 API 映射的安全业务异常。</summary>
public class CollectorStoreException(string code, string message, int statusCode = 409)
    : Exception(message)
{
    /// <summary>稳定错误码。</summary>
    public string Code { get; } = code;

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; } = statusCode;
}
