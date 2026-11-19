namespace Jogeoai.Domain;

/// <summary>保存结果的质量信息；空值或未知版本不证明历史结果完整。</summary>
/// <param name="SchemaVersion">元数据版本，当前为一。</param>
/// <param name="AnswerTruncated">正文是否在入库时截断；历史未知为空。</param>
/// <param name="AnswerRedacted">正文是否经过安全替换；历史未知为空。</param>
/// <param name="ModelVersionProvided">提供方是否给出具体模型版本；历史未知为空。</param>
/// <param name="Citations">实际暴露来源的保留质量，不保证网页来源完整。</param>
/// <param name="AnswerAbsence">无回答的已知原因：ai_overview_not_shown 表示 Google 搜索结果页未展示 AI 概览；empty_answer 表示接口正常返回但正文为空；safety_filtered 表示提供方输出审核移除了回答（三者仍为成功的无回答，不计入可见率分母）；无已知原因或历史未知时为空。</param>
public sealed record ResultQualityView(int SchemaVersion, bool? AnswerTruncated, bool? AnswerRedacted,
    bool? ModelVersionProvided, CitationQualityView Citations, string? AnswerAbsence = null);

/// <summary>回答级来源质量；完整仅表示收到的来源字段已保留，不代表抓取了全部网页来源。</summary>
/// <param name="Availability">可用状态：available 有来源、empty 明确空数组、not_exposed 未暴露、unknown 未知。</param>
/// <param name="Completeness">保留完整性：complete 完整、partial 部分、unknown 未知。</param>
/// <param name="ReceivedCount">收到的候选数；结构无法枚举时为空。</param>
/// <param name="RetainedCount">实际保存的条目数；历史未知为空。</param>
/// <param name="FilteredCount">因安全规则过滤的条目数。</param>
/// <param name="InvalidCount">格式无效的条目数，与过滤互斥。</param>
/// <param name="TruncatedCount">超过保存上限的其他合法条目数。</param>
/// <param name="Reasons">固定安全原因代码，不含原始响应或链接。</param>
public sealed record CitationQualityView(string Availability, string Completeness, int? ReceivedCount,
    int? RetainedCount, int? FilteredCount, int? InvalidCount, int? TruncatedCount, IReadOnlyList<string> Reasons);
