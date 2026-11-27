namespace Jogeoai.Api.Security;

/// <summary>
/// 采集接口（<c>/internal/collector/*</c>）的请求体大小上限，单位为字节。每个 POST 动作用
/// <c>[RequestSizeLimit]</c> 引用这里的常量；超过上限时服务器在读取请求体阶段返回 413，早于反序列化。
/// </summary>
public static class CollectorRequestLimits
{
    /// <summary>领取、续租、发题确认、失败上报、预检、账号事件等小请求的上限：16 KiB。</summary>
    public const long SmallBody = 16 * 1024;

    /// <summary>App 节点绑定上报（<c>bindings/report</c>，最多 200 条绑定）的上限：1 MiB。</summary>
    public const long AppBindingReport = 1024 * 1024;

    /// <summary>App 采集结果（<c>app/results</c>）的上限：4 MiB。回答正文的业务上限是 UTF-8 1 MiB，JSON 转义后按 2 倍估算再留余量。</summary>
    public const long AppResult = 4 * 1024 * 1024;

    /// <summary>网页采集结果（<c>results</c>）的上限：6 MiB。合法请求体约 2.4 MiB，JSON 转义后按 2 倍估算再留余量。</summary>
    public const long WebResult = 6 * 1024 * 1024;
}
