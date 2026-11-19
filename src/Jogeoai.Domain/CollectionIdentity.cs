using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jogeoai.Domain;

/// <summary>Target 和采集集合的稳定身份。</summary>
public static class CollectionIdentity
{
    /// <summary>
    /// 计算 Provider、引擎、采集模式、地区语言及回答模式构成的 Target 键；采集模式与默认国家由 Provider 定义派生。
    /// 回答模式为空或等于 Provider 默认模式时省略模式段，与引入回答模式前的键逐字节一致；其他模式追加 <c>|mode</c>（小写）。
    /// App 目标（<see cref="AppChannelDefinition.Provider"/>）的采集模式、国家、语言和回答模式是固定的，
    /// 键恒等于 <see cref="AppTargetKey"/>，不看输入上的国家、语言和回答模式。
    /// </summary>
    public static string TargetKey(CloroVisibilityTarget target)
    {
        // App targets are not in the registry; without this branch they would take the "scraped" / "US" fallback below.
        if (MonitoringProviders.IsApp(target.Provider)) return AppTargetKey(target.Engine);
        // Cloro keys must stay byte-identical to the historical "cloro|engine|scraped|country|locale" form.
        // Unknown providers fall back to the Cloro defaults so legacy callers keep their key shape.
        var definition = AnswerProviderDefinition.TryGet(target.Provider);
        return TargetKey(definition?.Provider ?? target.Provider, target.Engine, definition?.CaptureMode ?? "scraped",
            target.Country ?? definition?.DefaultCountry ?? "US", target.Locale, ModeSegment(definition, target.AnswerMode));
    }

    /// <summary>
    /// 计算 App 目标的目标键：<c>TargetKey("app_collector", engine, "app", "CN", "zh-CN")</c>，不带模式段。
    /// 与同平台 API 目标、国内网页目标的 Provider 和采集模式都不同，键必然不同。
    /// </summary>
    /// <param name="engine">平台代码（引擎标识），调用方负责先规范化为小写。</param>
    public static string AppTargetKey(string engine)
        => TargetKey(AppChannelDefinition.Provider, engine, AppChannelDefinition.CaptureMode,
            AppChannelDefinition.Country, AppChannelDefinition.Locale);

    /// <summary>计算任意 Provider 的目标键（默认回答模式，不含模式段）。</summary>
    public static string TargetKey(string provider, string engine, string captureMode, string country, string? locale)
        => Hex($"{provider}|{engine}|{captureMode}|{country}|{locale ?? ""}");

    /// <summary>计算任意 Provider 的目标键；<paramref name="answerMode"/> 为空时与 5 参数重载相同，否则追加 <c>|answerMode</c>。
    /// 调用方负责先把默认模式折叠为空值。</summary>
    public static string TargetKey(string provider, string engine, string captureMode, string country, string? locale, string? answerMode)
        => string.IsNullOrEmpty(answerMode)
            ? TargetKey(provider, engine, captureMode, country, locale)
            : Hex($"{provider}|{engine}|{captureMode}|{country}|{locale ?? ""}|{answerMode}");

    // Default modes (and blanks) collapse to no segment; registered providers write the canonical lowercase value,
    // unknown providers have no capability table and keep any non-blank mode as a lowercase segment.
    private static string? ModeSegment(AnswerProviderDefinition? definition, string? answerMode)
    {
        if (string.IsNullOrWhiteSpace(answerMode)) return null;
        if (definition is not null && AnswerModeRules.TryParse(answerMode, out var mode))
            return mode == definition.DefaultAnswerMode ? null : AnswerModeRules.ToStorage(mode);
        return answerMode.Trim().ToLowerInvariant();
    }

    /// <summary>计算固定 Prompt、Target 和样本数的选择键。</summary>
    public static string SelectionHash(IReadOnlyList<Guid> promptIds, IReadOnlyList<CloroVisibilityTarget> targets, int sampleCount)
        => SelectionHash(promptIds, targets, sampleCount, null);

    /// <summary>计算计划选择键；只有存在自定义平台范围时才把它纳入摘要，全部为 all 时与旧键逐字节相同。</summary>
    public static string SelectionHash(CollectionScheduleSelection selection)
        => SelectionHash(selection.PromptIds, selection.Targets, selection.SampleCount, selection.PromptTargets);

    /// <summary>计算固定 Prompt、Target、样本数及自定义平台范围的选择键；<paramref name="promptTargets"/> 为空时与旧键逐字节相同。</summary>
    public static string SelectionHash(IReadOnlyList<Guid> promptIds, IReadOnlyList<CloroVisibilityTarget> targets, int sampleCount,
        IReadOnlyList<CollectionPromptTargets>? promptTargets)
    {
        var prompts = promptIds.Order().Select(id => id.ToString("N")).ToArray();
        var targetKeys = targets.Select(TargetKey).Order(StringComparer.Ordinal).ToArray();
        if (promptTargets is not { Count: > 0 })
            return Hex(JsonSerializer.Serialize(new { prompts, targetKeys, sampleCount }));
        // Only selections with custom scopes get the extra member, so legacy plans keep their hash and do not re-version.
        var scopes = promptTargets.OrderBy(p => p.PromptId)
            .Select(p => new { prompt = p.PromptId.ToString("N"), targetKeys = p.TargetKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() })
            .ToArray();
        return Hex(JsonSerializer.Serialize(new { prompts, targetKeys, sampleCount, promptTargets = scopes }));
    }

    private static string Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
