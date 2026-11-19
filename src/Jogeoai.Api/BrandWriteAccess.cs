using Jogeoai.Api.Authorization;

namespace Jogeoai.Api;

/// <summary>品牌写入的角色检查；识别必须有真实会话，旧管理接口保留开发环境租户桥。</summary>
public static class BrandWriteAccess
{
    /// <summary>校验当前用户在目标租户的活动写权限；规则委托 <see cref="TenantWriteAccess"/>，保留品牌专用拒绝文案。</summary>
    /// <param name="context">当前 HTTP 请求。</param>
    /// <param name="tenantId">目标租户。</param>
    /// <param name="requireSession">是否必须携带真实会话。</param>
    public static void Require(HttpContext context, Guid tenantId, bool requireSession = false)
        => TenantWriteAccess.Require(context, tenantId, requireSession, "当前角色无权创建、识别或修改品牌。");
}
