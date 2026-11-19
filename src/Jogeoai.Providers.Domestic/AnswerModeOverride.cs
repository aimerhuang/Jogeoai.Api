using Jogeoai.Domain;

namespace Jogeoai.Providers.Domestic;

/// <summary>
/// 把请求上的回答模式套用到 Provider 全局配置：只覆盖思考参数（<see cref="AnswerModeProfile"/>），其他配置不变。
/// 模式为 null 时不覆盖（管理员诊断接口按全局配置探测）。
/// </summary>
internal static class AnswerModeOverride
{
    /// <summary>套用回答模式；模式无效或 Provider 不支持时返回 false，并给出不含原始取值的中文说明。</summary>
    public static bool TryApply(AnswerProviderDefinition definition, AnswerProviderConfiguration configuration, string? mode,
        out AnswerProviderConfiguration applied, out string error)
    {
        applied = configuration;
        error = "";
        if (mode is null) return true;
        if (!AnswerModeRules.TryParse(mode, out var parsed))
        {
            error = "回答模式取值无效。";
            return false;
        }
        if (!AnswerModeProfile.TryApply(definition, configuration.Options, parsed, out var options))
        {
            error = $"{definition.DisplayName}在当前配置下不支持回答模式 {AnswerModeRules.ToStorage(parsed)}。";
            return false;
        }
        applied = configuration with { Options = options };
        return true;
    }
}
