using Jogeoai.Api;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>内容管理控制器共享的会话和错误处理。</summary>
public abstract class ContentControllerBase(TenantContextResolver tenantContext) : ControllerBase
{
    /// <summary>获取已登录用户及其租户角色。</summary>
    protected (Guid TenantId, Guid UserId, string Role) RequireUser()
    {
        var identity = SessionAuthenticationMiddleware.GetContext(HttpContext);
        if (identity is null) throw new CollectorStoreException("AUTHENTICATION_REQUIRED", "请先登录。", 401);
        var tenant = tenantContext.Resolve(HttpContext);
        var membership = identity.User.Memberships.FirstOrDefault(item => item.TenantId == tenant && item.Status == "active");
        if (membership is null) throw new CollectorStoreException("TENANT_FORBIDDEN", "当前用户不是该租户成员。", 403);
        return (tenant, identity.User.UserId, membership.Role);
    }

    /// <summary>校验内容写权限。</summary>
    protected (Guid TenantId, Guid UserId, string Role) RequireWriter(bool viewerAllowed = false)
    {
        var current = RequireUser();
        if (!viewerAllowed && current.Role.Equals("viewer", StringComparison.OrdinalIgnoreCase))
            throw new CollectorStoreException("CONTENT_FORBIDDEN", "当前角色无权修改文章内容。", 403);
        return current;
    }

    /// <summary>执行内容请求并映射稳定业务错误；PLAN_LIMIT_EXCEEDED 附带 details。</summary>
    protected async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (CollectorStoreException exception)
        { return StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception)); }
        catch (ArgumentException exception)
        { return BadRequest(new { error = new { code = "INVALID_REQUEST", message = exception.Message } }); }
    }
}
