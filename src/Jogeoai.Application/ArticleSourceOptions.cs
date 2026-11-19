namespace Jogeoai.Application;

/// <summary>网页读取（<see cref="IArticleSourceReader"/> 与命名客户端 <c>research-web</c>）的出站参数（配置节 <c>ArticleSource</c>）。</summary>
public sealed class ArticleSourceOptions
{
    /// <summary>
    /// 仅开发环境：是否额外放行 Clash/Surge fake-ip 网段 198.18.0.0/15（本机 TUN fake-ip 模式下域名都会解析到该网段），默认 false。
    /// 其他私网、回环、链路本地与元数据地址仍拒绝；非开发环境配置为 true 时启动记录警告并按 false 处理。
    /// </summary>
    public bool AllowFakeIpRange { get; set; }
}
