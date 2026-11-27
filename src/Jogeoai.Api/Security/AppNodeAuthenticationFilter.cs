using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jogeoai.Api.Security;

/// <summary>
/// App 采集节点的令牌鉴权过滤器。授权过滤器在模型绑定之前执行：令牌缺失、未知或节点被停用时直接返回
/// 401 / 403（未执行迁移时 503），不读取也不解析请求体。鉴权规则与错误体由
/// <see cref="AppCollectorController"/> 提供，与动作内返回的完全相同。
/// 鉴权成功后把节点放进 <c>HttpContext.Items</c>，动作从那里读取，同一个请求只查一次节点表。
/// </summary>
/// <remarks>
/// 没有实现 <c>IOrderedFilter</c>，顺序为 0，先于 <c>[RequestSizeLimit]</c>（顺序 900）执行。
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AppNodeAuthenticationFilter : Attribute, IAsyncAuthorizationFilter
{
    /// <summary><c>HttpContext.Items</c> 中保存已鉴权节点的键。只有本过滤器写入。</summary>
    public const string NodeItemKey = "Jogeoai.AppCollectorNode";

    /// <summary>校验节点令牌；失败时短路请求，成功时记录已鉴权的节点。</summary>
    /// <param name="context">授权过滤器上下文。</param>
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        try
        {
            var store = http.RequestServices.GetRequiredService<IAppCollectorPoolStore>();
            http.Items[NodeItemKey] = await AppCollectorController.AuthenticateNodeAsync(http.Request, store, http.RequestAborted);
        }
        catch (CollectorStoreException exception)
        {
            context.Result = AppCollectorController.ErrorResult(exception);
        }
    }

    /// <summary>读取本请求中已由过滤器鉴权的节点；过滤器没有执行或鉴权失败时为 null。</summary>
    /// <param name="context">当前 HTTP 请求。</param>
    /// <returns>已鉴权且处于启用状态的节点。</returns>
    public static AppNodeIdentity? GetNode(HttpContext context)
        => context.Items.TryGetValue(NodeItemKey, out var value) ? value as AppNodeIdentity : null;
}
