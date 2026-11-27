using Jogeoai.Api.Controllers;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jogeoai.Api.Security;

/// <summary>
/// 网页采集端共享令牌（<c>Collector:ChinaServiceToken</c>）的鉴权过滤器。授权过滤器在模型绑定之前执行：
/// 令牌缺失、错误或服务端没有配置令牌时直接返回 401，不读取也不解析请求体。这里只做定长比较，不查库；
/// 区域与租户名单依赖请求体，仍由动作内的检查完成。比较规则与错误体由 <see cref="CollectorController"/> 提供。
/// </summary>
/// <remarks>
/// 标注在每个需要令牌的动作上；<c>GET /internal/collector/health</c> 保持匿名，不标注。
/// 没有实现 <c>IOrderedFilter</c>，顺序为 0，先于 <c>[RequestSizeLimit]</c>（顺序 900）执行。
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class CollectorServiceTokenFilter : Attribute, IAsyncAuthorizationFilter
{
    /// <summary>校验共享令牌；失败时短路请求。</summary>
    /// <param name="context">授权过滤器上下文。</param>
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        try
        {
            CollectorController.RequireServiceToken(http.Request, http.RequestServices.GetRequiredService<IConfiguration>());
        }
        catch (CollectorStoreException exception)
        {
            context.Result = CollectorController.ErrorResult(exception);
        }

        return Task.CompletedTask;
    }
}
