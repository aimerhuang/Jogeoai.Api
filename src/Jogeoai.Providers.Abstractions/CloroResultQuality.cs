namespace Jogeoai.Providers.Abstractions;

/// <summary>提供方规范化结果质量；正文截断由持久化层最终判定。</summary>
/// <param name="AnswerRedacted">正文是否经过安全替换。</param>
/// <param name="ModelVersionProvided">是否提供了具体模型版本。</param>
/// <param name="Citations">来源字段的接收和保留质量。</param>
/// <param name="AnswerAbsence">无回答的已知原因：ai_overview_not_shown 表示 Google 未展示 AI 概览；empty_answer 表示接口正常返回但正文为空；safety_filtered 表示提供方输出审核移除了回答；其他情况为空。</param>
public sealed record CloroResultQuality(bool AnswerRedacted, bool ModelVersionProvided, CloroCitationQuality Citations,
    string? AnswerAbsence = null);

/// <summary>来源规范化质量，不代表提供方抓取了完整网页。</summary>
/// <param name="Availability">来源可用状态。</param>
/// <param name="Completeness">本系统保留完整性。</param>
/// <param name="ReceivedCount">收到候选数，结构无法枚举时为空。</param>
/// <param name="RetainedCount">规范化后保留数。</param>
/// <param name="FilteredCount">安全过滤数。</param>
/// <param name="InvalidCount">无效条目数。</param>
/// <param name="TruncatedCount">超过保留上限的合法条目数。</param>
/// <param name="Reasons">固定安全原因代码。</param>
public sealed record CloroCitationQuality(string Availability, string Completeness, int? ReceivedCount,
    int RetainedCount, int FilteredCount, int InvalidCount, int TruncatedCount, IReadOnlyList<string> Reasons);
