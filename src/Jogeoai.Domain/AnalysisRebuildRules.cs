namespace Jogeoai.Domain;

/// <summary>已发布分析结果落后于当前识别配置或识别器版本的站点（自动重算候选）。</summary>
/// <param name="SiteId">站点（工作区）ID。</param>
/// <param name="TenantId">站点所属租户 ID。</param>
/// <param name="LatestConfigVersion">站点当前的识别配置版本（site_brand_settings.subject_config_version）。</param>
/// <param name="ReleaseConfigVersion">已发布结果冻结的识别配置版本。</param>
/// <param name="ReleaseExtractorVersion">已发布结果使用的识别器版本。</param>
/// <param name="LatestRevisionCreatedAt">最近一次识别配置修订的创建时间（UTC）；没有修订记录时为 null。</param>
public sealed record AnalysisRebuildCandidate(Guid SiteId, Guid TenantId, int LatestConfigVersion,
    int ReleaseConfigVersion, string ReleaseExtractorVersion, DateTimeOffset? LatestRevisionCreatedAt);

/// <summary>自动重算判定结果。</summary>
/// <param name="Rebuild">是否应立即发起全站重算。</param>
/// <param name="Reason">判定原因，取值见 <see cref="AnalysisRebuildRules"/> 中的常量。</param>
public sealed record AnalysisRebuildDecision(bool Rebuild, string Reason);

/// <summary>识别配置或识别器版本变化后自动重算的纯判定规则与幂等键。</summary>
public static class AnalysisRebuildRules
{
    /// <summary>识别器版本与当前代码不一致，立即重算。</summary>
    public const string ExtractorVersionChanged = "extractor_version_changed";

    /// <summary>识别配置版本已变化且已过合并等待时间，发起重算。</summary>
    public const string SiteConfigChanged = "site_config_changed";

    /// <summary>识别配置版本已变化，但距最后一次修改尚未达到合并等待时间，暂不重算。</summary>
    public const string WaitingForSettle = "waiting_for_settle";

    /// <summary>已发布结果与当前配置、识别器版本一致，无需重算。</summary>
    public const string UpToDate = "up_to_date";

    /// <summary>幂等键前缀。</summary>
    public const string IdempotencyKeyPrefix = "auto-rebuild:";

    /// <summary>
    /// 判断站点是否需要自动重算：识别器版本不同时立即重算；仅识别配置版本不同时，
    /// 最后一次修订距今达到 <paramref name="settle"/> 才重算（合并连续修改）；两者都一致时跳过。
    /// 没有修订时间时视为已过等待时间。
    /// </summary>
    /// <param name="latestConfigVersion">站点当前的识别配置版本。</param>
    /// <param name="releaseConfigVersion">已发布结果的识别配置版本。</param>
    /// <param name="releaseExtractorVersion">已发布结果的识别器版本。</param>
    /// <param name="currentExtractorVersion">当前代码的识别器版本（通常为 <see cref="MentionMatcher.Version"/>）。</param>
    /// <param name="latestRevisionCreatedAt">最近一次识别配置修订的创建时间（UTC）。</param>
    /// <param name="now">当前时间（UTC）。</param>
    /// <param name="settle">最后一次修改后的合并等待时间。</param>
    public static AnalysisRebuildDecision Decide(int latestConfigVersion, int releaseConfigVersion,
        string releaseExtractorVersion, string currentExtractorVersion, DateTimeOffset? latestRevisionCreatedAt,
        DateTimeOffset now, TimeSpan settle)
    {
        if (!string.Equals(releaseExtractorVersion, currentExtractorVersion, StringComparison.Ordinal))
            return new AnalysisRebuildDecision(true, ExtractorVersionChanged);
        if (latestConfigVersion == releaseConfigVersion)
            return new AnalysisRebuildDecision(false, UpToDate);
        if (latestRevisionCreatedAt is { } changedAt && now - changedAt < settle)
            return new AnalysisRebuildDecision(false, WaitingForSettle);
        return new AnalysisRebuildDecision(true, SiteConfigChanged);
    }

    /// <summary>
    /// 自动重算作业的幂等键：<c>auto-rebuild:{siteId}:cfg{latestConfigVersion}:{extractorVersion}</c>。
    /// 多个 Worker 对同一配置与识别器版本只会得到同一个作业；长度不超过 191。
    /// </summary>
    /// <param name="siteId">站点 ID。</param>
    /// <param name="latestConfigVersion">检测时的站点识别配置版本。</param>
    /// <param name="extractorVersion">当前识别器版本。</param>
    public static string IdempotencyKey(Guid siteId, int latestConfigVersion, string extractorVersion)
        => $"{IdempotencyKeyPrefix}{siteId:D}:cfg{latestConfigVersion}:{extractorVersion}";
}
