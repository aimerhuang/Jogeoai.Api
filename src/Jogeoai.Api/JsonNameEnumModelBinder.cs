using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jogeoai.Api;

/// <summary>
/// 按枚举成员的 JSON 名称（如 generation_failed、ai_generated）绑定查询字符串，同时兼容成员名。
/// 默认的简单类型绑定只识别成员名，Swagger 却按 JSON 名称公开取值，二者会不一致。
/// </summary>
public sealed class JsonNameEnumModelBinder : IModelBinder
{
    /// <summary>绑定可空或不可空枚举；空值保持默认值，未知取值记为模型错误（400）。</summary>
    /// <param name="bindingContext">模型绑定上下文。</param>
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);
        var result = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (result == ValueProviderResult.None) return Task.CompletedTask;
        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, result);
        var value = result.FirstValue?.Trim();
        if (string.IsNullOrEmpty(value)) return Task.CompletedTask;
        var enumType = Nullable.GetUnderlyingType(bindingContext.ModelType) ?? bindingContext.ModelType;
        if (!enumType.IsEnum) return Task.CompletedTask;
        foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var jsonName = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name;
            if (string.Equals(jsonName, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(field.Name, value, StringComparison.OrdinalIgnoreCase))
            {
                bindingContext.Result = ModelBindingResult.Success(field.GetValue(null));
                return Task.CompletedTask;
            }
        }
        bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, $"不支持的取值：{value}。");
        return Task.CompletedTask;
    }
}

/// <summary>从查询字符串按 JSON 名称绑定枚举参数；绑定来源固定为 Query，Swagger 仍按枚举类型公开取值。</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class FromQueryJsonEnumAttribute : ModelBinderAttribute
{
    /// <summary>使用 <see cref="JsonNameEnumModelBinder"/> 绑定查询字符串。</summary>
    public FromQueryJsonEnumAttribute() : base(typeof(JsonNameEnumModelBinder))
    {
        BindingSource = BindingSource.Query;
    }
}
