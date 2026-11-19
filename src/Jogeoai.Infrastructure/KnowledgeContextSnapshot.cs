using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>文章参考素材上下文快照的唯一序列化与摘要口径。</summary>
/// <remarks>
/// 素材预览、生成提交和 Worker 必须对同一上下文得到同一摘要。快照落库后会经过
/// MySQL JSON 列规范化（键顺序与空白会变），因此摘要只能基于反序列化后的对象重新
/// 序列化计算，不能对数据库返回的原始字符串直接取哈希。
/// </remarks>
public static class KnowledgeContextSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>按统一口径序列化上下文。</summary>
    /// <param name="context">知识库上下文。</param>
    public static string Serialize(KnowledgeContextResponse context) => JsonSerializer.Serialize(context, JsonOptions);

    /// <summary>按统一口径计算上下文的 SHA-256 摘要（小写十六进制）。</summary>
    /// <param name="context">知识库上下文。</param>
    public static string Hash(KnowledgeContextResponse context)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(context)))).ToLowerInvariant();
}
