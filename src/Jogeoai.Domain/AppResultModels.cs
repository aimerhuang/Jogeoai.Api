using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jogeoai.Domain;

/// <summary>App 回答里的一条引用来源，只含界面上展示的内容；站点名和 URL 在界面没有展示时为空，不推测、不补全。</summary>
/// <param name="Title">来源标题，1–512 个字符。</param>
/// <param name="Site">站点名；界面没有展示时为空。</param>
/// <param name="Url">来源 URL；界面没有展示时为空。没有 URL 的来源不进入引用表。</param>
/// <param name="List">来源所在的列表（例如 cited）；界面只有一个列表时可以为空。</param>
public sealed record AppResultCitation(string Title, string? Site, string? Url, string? List);

/// <summary>App 采集器提交的一条结果（contract.md 9.6）。绑定、账号代号不在其中：控制面从任务的租约取。</summary>
/// <param name="NodeId">令牌对应的节点。</param>
/// <param name="TaskId">采集任务标识。</param>
/// <param name="LeaseVersion">提交时持有的租约版本。</param>
/// <param name="IdempotencyKey">幂等键，8–191 个字符；同一任务同一幂等键只接受一次。</param>
/// <param name="CollectorBuildRevision">采集器构建版本，1–128 个字符。</param>
/// <param name="SentAt">提问发出的时刻（UTC）。</param>
/// <param name="CompletedAt">回答采集完成的时刻（UTC），写入检测的采集时间。</param>
/// <param name="AnswerText">完整的回答正文，UTF-8 不超过 1 MiB。</param>
/// <param name="Citations">引用来源，最多 100 条；读取状态为 read 时非空，否则为空。</param>
/// <param name="CitationsStatus">引用来源的读取状态。</param>
/// <param name="CitationsIssue">读取失败的原因词；只在读取状态为 unreadable 时出现。</param>
/// <param name="WebSearch">界面上可见的联网搜索状态。</param>
/// <param name="DeepThinking">界面上可见的深度思考状态。</param>
/// <param name="SearchQueries">界面上展示的搜索词，最多 32 条；没有提交时为空。</param>
/// <param name="DeviceId">采集器所在设备的标识（只保留在原始提交里；样本上的设备标识取自租约的绑定）。</param>
/// <param name="AndroidVersion">设备的 Android 版本。</param>
/// <param name="Abi">设备的 ABI；可空。</param>
/// <param name="AppPackage">App 包名。</param>
/// <param name="AppVersion">采集时的 App 版本。</param>
/// <param name="ScreenshotSha256">完成截图文件的 SHA-256（64 位十六进制）；截图本身不上传。没有截图时为空。</param>
public sealed record AppResultCommand(string NodeId, Guid TaskId, int LeaseVersion, string IdempotencyKey, string CollectorBuildRevision,
    DateTimeOffset SentAt, DateTimeOffset CompletedAt, string AnswerText, IReadOnlyList<AppResultCitation> Citations,
    AppCitationsStatus CitationsStatus, string? CitationsIssue, AppVisibleState WebSearch, AppVisibleState DeepThinking,
    IReadOnlyList<string>? SearchQueries, string DeviceId, string AndroidVersion, string? Abi, string AppPackage, string AppVersion,
    string? ScreenshotSha256);

/// <summary>App 结果提交的纯规则（contract.md 9.6）：校验与上限、回显判定、幂等用的内容哈希。不访问数据库。</summary>
public static partial class AppResultRules
{
    /// <summary>校验失败的错误码（400）。</summary>
    public const string InvalidCode = "INVALID_RESULT";

    /// <summary>回答与提问原文相同（回显）的错误码（400）。</summary>
    public const string EchoCode = "ECHO_ANSWER";

    /// <summary>同一任务同一幂等键、内容不同的错误码（409）。</summary>
    public const string IdempotencyConflictCode = "RESULT_IDEMPOTENCY_CONFLICT";

    /// <summary>回答正文的 UTF-8 字节上限（1 MiB）。</summary>
    public const int MaxAnswerBytes = 1_048_576;

    /// <summary>引用来源的条数上限。</summary>
    public const int MaxCitations = 100;

    /// <summary>来源标题的字符数上限（citations.title 的列宽）。</summary>
    public const int MaxTitleLength = 512;

    /// <summary>站点名的字符数上限。</summary>
    public const int MaxSiteLength = 255;

    /// <summary>来源 URL 的字符数上限；超过 750 个字符的 URL 存不进引用表，按没有 URL 处理。</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>搜索词的条数上限。</summary>
    public const int MaxSearchQueries = 32;

    /// <summary>单条搜索词的字符数上限。</summary>
    public const int MaxSearchQueryLength = 500;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,19}$")]
    private static partial Regex ListLabel();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex IssueWord();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    /// <summary>
    /// 校验一条结果并返回规范化后的命令：标题、站点名、URL、列表名、搜索词、原因词去除首尾空白，空白的站点名 / URL / 列表名视为空，
    /// 截图哈希转为小写。不满足任何一条规则时抛出 400 <see cref="InvalidCode"/>。
    /// </summary>
    /// <param name="command">采集器提交的结果。</param>
    /// <exception cref="CollectorStoreException">结果不满足 contract.md 9.6 的校验或上限。</exception>
    public static AppResultCommand Validate(AppResultCommand command)
    {
        if (command.TaskId == Guid.Empty || command.LeaseVersion < 1) throw Invalid("taskId 必须是 UUID，leaseVersion 必须是正整数。");
        if (string.IsNullOrWhiteSpace(command.NodeId) || command.NodeId.Length > 128) throw Invalid("nodeId 必须为 1–128 个字符。");
        if (command.IdempotencyKey is not { Length: >= 8 and <= 191 }) throw Invalid("idempotencyKey 必须为 8–191 个字符。");
        if (string.IsNullOrWhiteSpace(command.CollectorBuildRevision) || command.CollectorBuildRevision.Length > 128)
            throw Invalid("collectorBuildRevision 必须为 1–128 个字符。");
        if (command.SentAt.Offset != TimeSpan.Zero || command.CompletedAt.Offset != TimeSpan.Zero)
            throw Invalid("sentAt 与 completedAt 必须是 UTC 时间。");
        if (string.IsNullOrWhiteSpace(command.AnswerText)) throw Invalid("answerText 不能为空。");
        if (Encoding.UTF8.GetByteCount(command.AnswerText) > MaxAnswerBytes) throw Invalid("answerText 超过 1 MiB。");
        if (command.Citations is null || command.Citations.Count > MaxCitations) throw Invalid($"citations 最多 {MaxCitations} 条。");

        var citations = new AppResultCitation[command.Citations.Count];
        for (var i = 0; i < citations.Length; i++)
        {
            var citation = command.Citations[i] ?? throw Invalid("citations 不能包含空项。");
            var (title, site, url, list) = (citation.Title?.Trim(), Optional(citation.Site), Optional(citation.Url), Optional(citation.List));
            if (title is not { Length: >= 1 and <= MaxTitleLength }) throw Invalid($"引用的 title 必须为 1–{MaxTitleLength} 个字符。");
            if (site is { Length: > MaxSiteLength }) throw Invalid($"引用的 site 最多 {MaxSiteLength} 个字符。");
            if (url is { Length: > MaxUrlLength }) throw Invalid($"引用的 url 最多 {MaxUrlLength} 个字符。");
            if (list is not null && !ListLabel().IsMatch(list)) throw Invalid("引用的 list 必须是小写字母开头的 1–20 个小写字母、数字或下划线。");
            citations[i] = new AppResultCitation(title, site, url, list);
        }
        if ((command.CitationsStatus == AppCitationsStatus.Read) != (citations.Length > 0))
            throw Invalid("citationsStatus 为 read 时 citations 必须非空，否则必须为空。");
        var issue = Optional(command.CitationsIssue);
        if ((command.CitationsStatus == AppCitationsStatus.Unreadable) != (issue is not null))
            throw Invalid("citationsIssue 只在 citationsStatus 为 unreadable 时出现，且此时必须出现。");
        if (issue is not null && !IssueWord().IsMatch(issue)) throw Invalid("citationsIssue 必须是小写字母开头的 1–64 个小写字母、数字或下划线。");

        string[]? queries = null;
        if (command.SearchQueries is { } submitted)
        {
            if (submitted.Count > MaxSearchQueries) throw Invalid($"searchQueries 最多 {MaxSearchQueries} 条。");
            queries = new string[submitted.Count];
            for (var i = 0; i < queries.Length; i++)
                queries[i] = submitted[i]?.Trim() is { Length: >= 1 and <= MaxSearchQueryLength } query
                    ? query
                    : throw Invalid($"每条搜索词必须为 1–{MaxSearchQueryLength} 个非空白字符。");
        }

        var (device, android, abi) = (command.DeviceId?.Trim(), command.AndroidVersion?.Trim(), Optional(command.Abi));
        if (device is not { Length: >= 1 and <= 128 } || android is not { Length: >= 1 and <= 32 } || abi is { Length: > 32 })
            throw Invalid("device 需要 1–128 个字符的 deviceId、1–32 个字符的 androidVersion，abi 最多 32 个字符。");
        var (package, version) = (command.AppPackage?.Trim(), command.AppVersion?.Trim());
        if (package is not { Length: >= 1 and <= 255 } || version is not { Length: >= 1 and <= 64 })
            throw Invalid("app 需要 1–255 个字符的 package 和 1–64 个字符的 version。");
        var screenshot = Optional(command.ScreenshotSha256)?.ToLowerInvariant();
        if (screenshot is not null && !Sha256Hex().IsMatch(screenshot)) throw Invalid("screenshotSha256 必须是 64 位十六进制。");

        return command with
        {
            Citations = citations, CitationsIssue = issue, SearchQueries = queries, DeviceId = device, AndroidVersion = android, Abi = abi,
            AppPackage = package, AppVersion = version, ScreenshotSha256 = screenshot,
        };
    }

    /// <summary>回显判定：去掉全部空白字符后，回答与提问原文相同。空白不同（换行、首尾空格、全角空格）不算不同。</summary>
    /// <param name="answerText">回答正文。</param>
    /// <param name="questionText">任务的提问原文。</param>
    public static bool IsEcho(string answerText, string questionText)
    {
        var answer = WithoutWhitespace(answerText);
        return answer.Length > 0 && string.Equals(answer, WithoutWhitespace(questionText), StringComparison.Ordinal);
    }

    /// <summary>
    /// 幂等用的内容哈希（SHA-256，小写十六进制）：覆盖回答正文和规范化后的引用。引用逐条去除首尾空白、空白字段视为空，
    /// 再按（列表、标题、站点名、URL）排序，所以引用的先后顺序、字段缺失还是为 null、首尾空白都不改变哈希；回答正文按原样参与。
    /// </summary>
    /// <param name="answerText">回答正文。</param>
    /// <param name="citations">引用来源。</param>
    public static string PayloadHash(string answerText, IReadOnlyList<AppResultCitation> citations)
    {
        var canonical = citations
            .Select(citation => new[] { Optional(citation.List), citation.Title?.Trim() ?? "", Optional(citation.Site), Optional(citation.Url) })
            .OrderBy(fields => string.Join('\u001f', fields.Select(field => field is null ? "\u0000" : "\u0001" + field)), StringComparer.Ordinal)
            .Select(fields => new { list = fields[0], title = fields[1], site = fields[2], url = fields[3] });
        var payload = JsonSerializer.Serialize(new { answerText, citations = canonical });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static string WithoutWhitespace(string? value)
        => value is null ? "" : string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CollectorStoreException Invalid(string message) => new(InvalidCode, message, 400);
}
