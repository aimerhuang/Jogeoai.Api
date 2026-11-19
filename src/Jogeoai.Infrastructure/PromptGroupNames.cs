using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 提问分组名称规则（020 prompt_groups）：显示名去除首尾空白后 1–100 字；唯一键 normalized_name 为 NFKC、合并空白并转小写。
/// 分组接口与批量创建按名称归组共用这一实现，保证「同名归入已有分组」与唯一约束一致。
/// </summary>
internal static class PromptGroupNames
{
    internal const int MaxLength = 100;

    /// <summary>校验并返回去除首尾空白后的显示名；长度不在 1–100 时抛 400 INVALID_PROMPT_GROUP。</summary>
    internal static string Name(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length is < 1 or > MaxLength)
            throw new CollectorStoreException("INVALID_PROMPT_GROUP", "分组名称长度必须为 1–100。", 400);
        return name;
    }

    /// <summary>计算 normalized_name；规范化后长度不在 1–100 时抛 400 INVALID_PROMPT_GROUP。</summary>
    internal static string Key(string name)
    {
        var normalized = string.Join(' ', name.Normalize(System.Text.NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (normalized.Length is < 1 or > MaxLength)
            throw new CollectorStoreException("INVALID_PROMPT_GROUP", "规范化后的分组名称长度必须为 1–100。", 400);
        return normalized;
    }
}
