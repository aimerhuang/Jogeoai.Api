namespace Jogeoai.Domain;

/// <summary>重算范围；配置版本变化时会扩展到完整工作区以保证一致发布。</summary>
public sealed record AnalysisJobRequest(DateTimeOffset? From = null, DateTimeOffset? To = null,
    IReadOnlyList<Guid>? PromptIds = null);

/// <summary>可轮询的异步分析任务状态。</summary>
public sealed record AnalysisJobView(Guid JobId, Guid SiteId, Guid ReleaseId, int AnalysisVersion,
    int SubjectConfigVersion, string Status, int TotalChecks, int CompletedChecks, int FailedChecks,
    string? ErrorSummary, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt,
    AnalysisJobRequest? RequestedScope = null, string EffectiveScope = "site", int ReusedChecks = 0);

/// <summary>单处可回溯至原文 UTF-16 偏移的提及证据。</summary>
public sealed record MentionEvidenceView(Guid SubjectId, string SubjectType, string MatchedText,
    string Snippet, string MatchBasis, int OffsetStart, int OffsetEnd, string ExtractorVersion,
    int SubjectConfigVersion);

/// <summary>一条 Check 当前已发布版本的分析状态与命中证据。</summary>
public sealed record CheckAnalysisView(Guid CheckId, Guid ReleaseId, int AnalysisVersion,
    int SubjectConfigVersion, string Status, bool ConfigurationOutdated, bool ConfigurationMissing,
    IReadOnlyList<MentionEvidenceView> Mentions);
