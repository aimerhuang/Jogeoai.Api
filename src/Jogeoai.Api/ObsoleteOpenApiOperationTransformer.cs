using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Jogeoai.Api;

/// <summary>
/// 把控制器或操作上的 <see cref="ObsoleteAttribute"/> 写成 /openapi/v1.json 中的 deprecated: true。
/// Swashbuckle（/swagger/v1/swagger.json）自带该行为，Microsoft.AspNetCore.OpenApi 不会自动标记，前端类型又从后者生成。
/// </summary>
public sealed class ObsoleteOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    /// <summary>端点元数据（含控制器级特性）带有 <see cref="ObsoleteAttribute"/> 时把操作标记为不再推荐。</summary>
    /// <param name="operation">正在生成的操作。</param>
    /// <param name="context">操作对应的接口描述。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<ObsoleteAttribute>().Any()) operation.Deprecated = true;
        return Task.CompletedTask;
    }
}
