using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api;

/// <summary>Cloro 全局配置接口共用的系统管理员校验：未登录 401、非系统管理员 403、迁移缺失 503。</summary>
internal static class SystemAdministratorAccess
{
    /// <summary>通过时返回空值，否则返回应直接响应的错误结果。</summary>
    internal static async Task<ActionResult?> RequireAsync(ControllerBase controller, IResearchStore store,
        CancellationToken cancellationToken)
    {
        var context = SessionAuthenticationMiddleware.GetContext(controller.HttpContext);
        if (context is null)
            return controller.Unauthorized(new { error = new { code = "AUTHENTICATION_REQUIRED", message = "请先登录。" } });
        try
        {
            if (!await store.IsSystemAdministratorAsync(context.User.UserId, cancellationToken))
                return controller.StatusCode(StatusCodes.Status403Forbidden,
                    new { error = new { code = "ADMIN_REQUIRED", message = "需要系统管理员权限。" } });
        }
        catch (CollectorStoreException exception) when (exception.StatusCode == 503)
        {
            return controller.StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = new { code = exception.Code, message = exception.Message } });
        }
        return null;
    }
}
