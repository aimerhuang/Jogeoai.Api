using System.Security.Cryptography;
using System.Text;

namespace Jogeoai.Application;

/// <summary>MCP 访问令牌的生成、格式校验与哈希（纯函数）。明文 = jgm_ + 32 字节随机数的 base64url（共 47 字符）。</summary>
public static class McpTokenCodec
{
    /// <summary>明文前缀。</summary>
    public const string Prefix = "jgm_";

    /// <summary>随机部分的字节数。</summary>
    public const int RandomBytes = 32;

    /// <summary>明文总长度：前缀 4 + base64url(32 字节) 43。</summary>
    public const int TokenLength = 47;

    /// <summary>库内保存、列表展示的明文前缀长度（含 jgm_）。</summary>
    public const int DisplayPrefixLength = 12;

    /// <summary>令牌有效期上限（天）。</summary>
    public const int MaxExpiresInDays = 365;

    /// <summary>每个租户启用令牌数上限。</summary>
    public const int MaxActiveTokensPerTenant = 20;

    /// <summary>令牌名称最大字符数。</summary>
    public const int MaxNameLength = 100;

    /// <summary>单个令牌最多限定的站点数。</summary>
    public const int MaxSiteIds = 50;

    /// <summary>生成新的令牌明文。</summary>
    public static string Generate() => Prefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(RandomBytes));

    /// <summary>判断字符串是否为 MCP 令牌格式（前缀、长度、base64url 字符集）；不访问数据库。</summary>
    /// <param name="value">待校验的字符串。</param>
    public static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length != TokenLength || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        for (var index = Prefix.Length; index < value.Length; index++)
        {
            var c = value[index];
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        }
        return true;
    }

    /// <summary>明文的 SHA-256 小写 hex（64 字符）。</summary>
    /// <param name="token">令牌明文。</param>
    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>列表展示用的前缀（前 12 个字符）。</summary>
    /// <param name="token">令牌明文。</param>
    public static string DisplayPrefix(string token) => token[..DisplayPrefixLength];

    /// <summary>定长比较两个哈希字符串，耗时不随首个不同字符的位置变化。</summary>
    /// <param name="left">哈希一。</param>
    /// <param name="right">哈希二。</param>
    public static bool FixedTimeEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    /// <summary>根据吊销、过期时间计算状态：revoked、expired 或 active。</summary>
    /// <param name="expiresAt">过期时间。</param>
    /// <param name="revokedAt">吊销时间。</param>
    /// <param name="now">当前 UTC 时间。</param>
    public static string Status(DateTimeOffset expiresAt, DateTimeOffset? revokedAt, DateTimeOffset now)
        => revokedAt is not null ? "revoked" : expiresAt <= now ? "expired" : "active";

    /// <summary>最后使用时间的节流间隔：距上次记录超过该间隔才写库。</summary>
    public static readonly TimeSpan LastUsedTouchInterval = TimeSpan.FromSeconds(60);

    /// <summary>是否需要更新最后使用时间：从未使用，或距上次记录超过 60 秒。</summary>
    /// <param name="lastUsedAt">已记录的最后使用时间。</param>
    /// <param name="now">当前 UTC 时间。</param>
    public static bool ShouldTouchLastUsed(DateTimeOffset? lastUsedAt, DateTimeOffset now)
        => lastUsedAt is null || now - lastUsedAt.Value > LastUsedTouchInterval;

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
