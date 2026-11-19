namespace Jogeoai.Domain;

/// <summary>一条提问能否进入 App 渠道的检查结果。</summary>
/// <param name="Accepted">是否通过检查；通过的提问才会生成 App 任务。</param>
/// <param name="ReasonCode">未通过时的稳定原因码（见 <see cref="AppPromptRules"/> 的 Reason 常量）；通过时为空。</param>
public sealed record AppPromptEvaluation(bool Accepted, string? ReasonCode = null)
{
    /// <summary>通过检查的结果。</summary>
    public static AppPromptEvaluation Pass { get; } = new(true);

    /// <summary>构造未通过的结果。</summary>
    /// <param name="reasonCode">原因码。</param>
    public static AppPromptEvaluation Reject(string reasonCode) => new(false, reasonCode);
}

/// <summary>一条提问当前能否进入 App 渠道（提问列表与详情的 appEligibility）。</summary>
/// <param name="Eligible">是否会生成 App 任务：通过自动检查，且没有被运营人员排除。</param>
/// <param name="ReasonCode">不进入 App 渠道的原因码：<see cref="AppPromptRules"/> 的 Reason 常量之一，或被运营人员排除时的
/// <see cref="AppPromptRules.ReasonExcludedByOperator"/>；可以进入时为空。</param>
public sealed record AppPromptEligibility(bool Eligible, string? ReasonCode = null);

/// <summary>运营人员把一条提问排除出 App 渠道的记录（prompt_app_exclusions）；只追加和撤销，不删除，本身就是审计记录。</summary>
/// <param name="Id">排除记录标识。</param>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="PromptId">提问标识。</param>
/// <param name="Reason">排除原因（1–500 个字符）。</param>
/// <param name="CreatedBy">执行排除的用户标识。</param>
/// <param name="CreatedAt">排除的 UTC 时间。</param>
/// <param name="RevokedBy">撤销排除的用户标识；未撤销时为空。</param>
/// <param name="RevokedAt">撤销的 UTC 时间；未撤销（仍然生效）时为空。</param>
public sealed record AppPromptExclusionView(Guid Id, Guid SiteId, Guid PromptId, string Reason, Guid CreatedBy,
    DateTimeOffset CreatedAt, Guid? RevokedBy = null, DateTimeOffset? RevokedAt = null);

/// <summary>
/// 提问进入 App 渠道（通过真实账号在手机 App 里发出）之前的自动检查。纯函数：同一规则版本对同一文本结果恒定，
/// 不依赖区域设置、时间或外部状态。检查只决定“发不发”，从不修改提问：不截断、不改写、不替换。
/// <para>
/// ⚠ <b>候选规则，尚未经用户确认（DC-01）。</b>下面的取值是 design 第 5 节的候选 v1：长度上限还没有各 App 输入框的实测数据（T03），
/// 是否增加内容类规则也未决定。确认之前 App 渠道不对任何租户开放（<c>Collector:AppTenantIds</c> 保持为空）。
/// 规则内容一旦改变必须提高 <see cref="RuleVersion"/>：每条 App 任务都记录它通过的是哪个版本。
/// </para>
/// </summary>
public static class AppPromptRules
{
    // ---- 候选 v1 的全部取值都在这里（DC-01 未确认）----

    /// <summary>规则版本，随每条 App 任务保存并下发给采集器（collector_tasks.prompt_rule_version）。</summary>
    public const int RuleVersion = 1;

    /// <summary>去除首尾空白后的最少字符数。</summary>
    public const int MinLength = 1;

    /// <summary>
    /// 去除首尾空白后的最多字符数（候选值）。按 <see cref="string.Length"/>（UTF-16 码元）计数，与现有提问校验
    /// （去除首尾空白后长度 1–10000）的计数方式相同：基本平面的字符（含中文）各计 1，辅助平面的字符（多数 emoji）各计 2。
    /// </summary>
    public const int MaxLength = 200;

    /// <summary>视为 URL 的协议前缀（不区分大小写）。</summary>
    private static readonly string[] UrlSchemes = ["http://", "https://"];

    /// <summary>视为 URL 的无协议主机前缀（不区分大小写）：前面不是 ASCII 字母或数字，后面紧跟 ASCII 字母或数字。</summary>
    private const string BareHostPrefix = "www.";

    // ---- 原因码 ----

    /// <summary>提问为空，或只有空白字符。</summary>
    public const string ReasonEmpty = "EMPTY";

    /// <summary>提问超过 <see cref="MaxLength"/> 个字符。</summary>
    public const string ReasonTooLong = "TOO_LONG";

    /// <summary>提问不是单行文本：含回车、换行或其他换行符。</summary>
    public const string ReasonMultiline = "MULTILINE";

    /// <summary>提问含换行以外的控制字符（例如制表符）。</summary>
    public const string ReasonControlCharacter = "CONTROL_CHARACTER";

    /// <summary>提问含 URL：http://、https:// 或以 www. 开头的主机名。</summary>
    public const string ReasonContainsUrl = "CONTAINS_URL";

    /// <summary>全部原因码，按检查顺序排列；一条提问违反多条规则时返回其中最靠前的一个。</summary>
    public static IReadOnlyList<string> ReasonCodes { get; } =
        [ReasonEmpty, ReasonTooLong, ReasonMultiline, ReasonControlCharacter, ReasonContainsUrl];

    /// <summary>
    /// 检查一条提问能否进入 App 渠道。先去除首尾空白（与提问保存时的规范化一致），再依次检查：
    /// 为空 → <see cref="ReasonEmpty"/>；超长 → <see cref="ReasonTooLong"/>；含换行 → <see cref="ReasonMultiline"/>；
    /// 含其他控制字符 → <see cref="ReasonControlCharacter"/>；含 URL → <see cref="ReasonContainsUrl"/>。
    /// </summary>
    /// <param name="content">提问原文；空值按空提问处理。</param>
    public static AppPromptEvaluation Evaluate(string? content)
    {
        var text = content?.Trim() ?? "";
        if (text.Length < MinLength) return AppPromptEvaluation.Reject(ReasonEmpty);
        if (text.Length > MaxLength) return AppPromptEvaluation.Reject(ReasonTooLong);
        if (text.AsSpan().IndexOfAny(LineBreaks) >= 0) return AppPromptEvaluation.Reject(ReasonMultiline);
        foreach (var character in text)
            if (char.IsControl(character)) return AppPromptEvaluation.Reject(ReasonControlCharacter);
        return ContainsUrl(text) ? AppPromptEvaluation.Reject(ReasonContainsUrl) : AppPromptEvaluation.Pass;
    }

    /// <summary>提问被运营人员手动排除出 App 渠道时的原因码；不属于自动检查，所以不在 <see cref="ReasonCodes"/> 里。</summary>
    public const string ReasonExcludedByOperator = "EXCLUDED_BY_OPERATOR";

    /// <summary>
    /// 一条提问当前能否进入 App 渠道：人工排除优先（即使提问通过自动检查，也即使它同时未通过检查），否则取 <see cref="Evaluate"/> 的结果。
    /// 展开周期 Run 与提问列表读取用的是同一个函数。
    /// </summary>
    /// <param name="content">提问原文。</param>
    /// <param name="excludedByOperator">该提问是否有生效中的人工排除。</param>
    public static AppPromptEligibility Eligibility(string? content, bool excludedByOperator)
    {
        if (excludedByOperator) return new(false, ReasonExcludedByOperator);
        var evaluation = Evaluate(content);
        return new(evaluation.Accepted, evaluation.ReasonCode);
    }

    // CR, LF, NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR. NEL is also a control character; it is reported as a line break.
    private static readonly char[] LineBreaks = ['\r', '\n', '\u0085', '\u2028', '\u2029'];

    private static bool ContainsUrl(string text)
    {
        foreach (var scheme in UrlSchemes)
            if (text.Contains(scheme, StringComparison.OrdinalIgnoreCase)) return true;
        for (var index = text.IndexOf(BareHostPrefix, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = text.IndexOf(BareHostPrefix, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            var after = index + BareHostPrefix.Length;
            var startsToken = index == 0 || !char.IsAsciiLetterOrDigit(text[index - 1]);
            if (startsToken && after < text.Length && char.IsAsciiLetterOrDigit(text[after])) return true;
        }
        return false;
    }
}
