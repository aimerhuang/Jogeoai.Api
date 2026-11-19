using Jogeoai.Domain;
using Microsoft.AspNetCore.Http;

namespace Jogeoai.Api.Authorization;

/// <summary>
/// 租户内唯一的"可写角色"判定：owner、admin、member 可写，viewer 只读。
/// 必须在调用任何 Store、Provider、队列或出站请求之前执行；拒绝以 <see cref="CollectorStoreException"/> 抛出，
/// 由各 Controller 现有的错误转换输出 <c>{ error: { code, message } }</c>。
/// </summary>
public static class TenantWriteAccess
{
    /// <summary>默认的只读拒绝文案。</summary>
    public const string DefaultMessage = "当前角色为只读，无权执行此操作。";

    /// <summary>可写的租户角色集合（与成员表取值逐字匹配）。</summary>
    public static readonly IReadOnlySet<string> WriterRoles =
        new HashSet<string>(["owner", "admin", "member"], StringComparer.Ordinal);

    /// <summary>
    /// 校验当前请求在目标租户内具备写权限。
    /// 无会话时：<paramref name="requireSession"/> 为真则 401 <c>AUTHENTICATION_REQUIRED</c>，否则放行
    /// （仅 Development 兼容桥会走到此分支，默认会话策略已保证）。
    /// 有会话时：要求在目标租户存在活动成员关系且角色可写，否则 403 <c>CONTENT_FORBIDDEN</c>。
    /// </summary>
    /// <param name="context">当前 HTTP 请求。</param>
    /// <param name="tenantId">已由 <see cref="TenantContextResolver"/> 解析的目标租户。</param>
    /// <param name="requireSession">是否必须携带真实会话（不接受 Development 兼容桥）。</param>
    /// <param name="message">403 拒绝时返回的中文文案。</param>
    /// <exception cref="CollectorStoreException">未登录（401）或当前角色只读（403）。</exception>
    public static void Require(HttpContext context, Guid tenantId, bool requireSession = false, string message = DefaultMessage)
    {
        var identity = SessionAuthenticationMiddleware.GetContext(context);
        if (identity is null)
        {
            if (requireSession) throw new CollectorStoreException("AUTHENTICATION_REQUIRED", "请先登录。", 401);
            return;
        }
        var member = identity.User.Memberships.FirstOrDefault(item => item.TenantId == tenantId && item.Status == "active");
        if (member is null || !WriterRoles.Contains(member.Role))
            throw new CollectorStoreException("CONTENT_FORBIDDEN", message, 403);
    }
}
