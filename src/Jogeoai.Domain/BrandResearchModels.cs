namespace Jogeoai.Domain;

/// <summary>品牌研究任务的状态。</summary>
public enum BrandResearchJobStatus
{
    /// <summary>等待 Worker 领取。</summary>
    Pending,
    /// <summary>正在执行。</summary>
    Running,
    /// <summary>已完成并有候选结果。</summary>
    Done,
    /// <summary>执行失败。</summary>
    Failed,
    /// <summary>已取消。</summary>
    Cancelled,
}

/// <summary>候选监控问题。</summary>
/// <param name="Prompt">问题文本。</param>
/// <param name="Tags">问题标签。</param>
/// <param name="Branded">是否明确包含品牌词。</param>
/// <param name="Reason">经过审核的推荐理由。</param>
/// <param name="EvidenceIds">支持该问题前提的来源编号。</param>
/// <param name="Product">对应的产品或服务类别。</param>
/// <param name="Scenario">客户使用场景。</param>
/// <param name="Intent">客户提问意图。</param>
public sealed record SuggestedPrompt(string Prompt, IReadOnlyList<string> Tags, bool Branded,
    string? Reason = null, IReadOnlyList<string>? EvidenceIds = null, string? Product = null,
    string? Scenario = null, string? Intent = null);

/// <summary>品牌研究返回的候选配置。</summary>
/// <param name="BrandName">模型识别的品牌名称。</param>
/// <param name="AdditionalDomains">建议增加的域名。</param>
/// <param name="Aliases">建议增加的别名。</param>
/// <param name="Competitors">建议增加的竞品。</param>
/// <param name="Prompts">建议监控的问题。</param>
/// <param name="Provider">生成结果的 Provider。</param>
/// <param name="Model">生成结果使用的模型。</param>
public sealed record BrandResearchSuggestion(
    string BrandName,
    IReadOnlyList<string> AdditionalDomains,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<ResearchCompetitorSuggestion> Competitors,
    IReadOnlyList<SuggestedPrompt> Prompts,
    string Provider,
    string Model);

/// <summary>品牌研究建议的竞品。</summary>
/// <param name="Name">竞品名称。</param>
/// <param name="Domains">竞品域名。</param>
/// <param name="Aliases">竞品别名。</param>
public sealed record ResearchCompetitorSuggestion(string Name, IReadOnlyList<string> Domains, IReadOnlyList<string> Aliases);

/// <summary>发起品牌研究的请求。</summary>
/// <param name="BrandName">可选品牌名称。</param>
/// <param name="Website">可选研究官网地址；无官网时须提供 BusinessDescription，或品牌知识库中已有条目。</param>
/// <param name="MaxPrompts">候选问题上限，默认 30。</param>
/// <param name="MaxCompetitors">竞品上限，默认 10。</param>
/// <param name="RefreshKey">换一批时使用的幂等标识。</param>
/// <param name="ReuseJobId">同品牌已保存的研究任务；在服务端校验作用域后复用。</param>
/// <param name="BusinessDescription">用户补充或纠正的主营业务，最多 4000 字。</param>
/// <param name="ExcludedQuestions">已选或不希望再次推荐的问题。</param>
/// <param name="Feedback">问题反馈，用于下一批筛选。</param>
/// <param name="Market">目标市场，默认 CN。</param>
/// <param name="Language">目标语言，默认 zh-CN。</param>
/// <param name="SingleRound">快速模式：只做一轮候选生成与审核，不足 MaxPrompts 时返回已通过的部分；默认 false，最多三轮补问。</param>
public sealed record StartBrandResearchRequest(
    string? Website = null,
    string? BrandName = null,
    int MaxPrompts = 30,
    int MaxCompetitors = 10,
    string? RefreshKey = null,
    Guid? ReuseJobId = null,
    string? BusinessDescription = null,
    IReadOnlyList<string>? ExcludedQuestions = null,
    IReadOnlyList<ResearchQuestionFeedback>? Feedback = null,
    string Market = "CN",
    string Language = "zh-CN",
    bool SingleRound = false);

/// <summary>发起品牌研究后返回的任务摘要。</summary>
/// <param name="JobId">任务标识。</param>
/// <param name="Status">任务状态。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record BrandResearchAccepted(Guid JobId, BrandResearchJobStatus Status, DateTimeOffset CreatedAt);

/// <summary>品牌研究任务状态响应。</summary>
/// <param name="JobId">任务标识。</param>
/// <param name="Status">任务状态。</param>
/// <param name="Suggestion">完成时的候选结果。</param>
/// <param name="ErrorCode">失败时的安全错误码。</param>
/// <param name="ErrorMessage">失败时的安全提示。</param>
/// <param name="Provider">实际使用的 Provider。</param>
/// <param name="Model">实际使用的模型。</param>
/// <param name="CreatedAt">任务创建时间。</param>
/// <param name="CompletedAt">任务完成时间。</param>
/// <param name="Research">当前阶段和已保存的业务上下文，旧任务为空。</param>
public sealed record BrandResearchJobView(
    Guid JobId,
    BrandResearchJobStatus Status,
    BrandResearchSuggestion? Suggestion,
    string? ErrorCode,
    string? ErrorMessage,
    string? Provider,
    string? Model,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    ResearchContext? Research = null);

/// <summary>全局结构化研究 Provider 配置。</summary>
/// <param name="Provider">Provider 标识。</param>
/// <param name="Model">默认模型标识。</param>
/// <param name="ApiUrl">兼容接口地址。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="Priority">选择优先级，数字越小越优先。</param>
/// <param name="WebSearchEnabled">是否允许联网搜索。</param>
/// <param name="TimeoutSeconds">单次调用超时秒数。</param>
/// <param name="CredentialConfigured">是否已配置凭据。</param>
/// <param name="CredentialHint">凭据末尾脱敏提示。</param>
/// <param name="UpdatedAt">最近更新时间。</param>
public sealed record ResearchProviderSettingsView(
    string Provider,
    string Model,
    string ApiUrl,
    bool Enabled,
    int Priority,
    bool WebSearchEnabled,
    int TimeoutSeconds,
    bool CredentialConfigured,
    string? CredentialHint,
    DateTimeOffset UpdatedAt);

/// <summary>保存全局研究 Provider 配置的请求。</summary>
/// <param name="Model">默认模型标识。</param>
/// <param name="ApiUrl">兼容接口地址。</param>
/// <param name="Credential">新 API 凭据；按当前部署方案直接保存到数据库，省略时保留已有凭据。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="Priority">选择优先级。</param>
/// <param name="WebSearchEnabled">是否启用联网搜索。</param>
/// <param name="TimeoutSeconds">单次调用超时秒数。</param>
public sealed record SaveResearchProviderSettingsRequest(
    string Model,
    string ApiUrl,
    string? Credential,
    bool Enabled = true,
    int Priority = 100,
    bool WebSearchEnabled = true,
    int TimeoutSeconds = 90);

/// <summary>结构化研究 Provider 的调用结果。</summary>
/// <param name="Suggestion">规范化后的品牌研究建议。</param>
/// <param name="Provider">实际使用的 Provider 标识。</param>
/// <param name="Model">实际使用的模型标识。</param>
/// <param name="Research">本次研究的最终上下文与来源。</param>
public sealed record ResearchProviderResult(BrandResearchSuggestion Suggestion, string Provider, string Model,
    ResearchContext? Research = null);

/// <summary>官网资料或用户主动补充的业务资料；正文仅作为不可信数据使用。</summary>
/// <param name="Id">本次资料内的稳定编号。</param>
/// <param name="Url">实际来源；用户补充资料为空。</param>
/// <param name="Title">来源标题。</param>
/// <param name="Excerpt">有界正文摘要。</param>
/// <param name="ContentHash">摘要的 SHA256。</param>
/// <param name="FetchedAt">读取时间。</param>
public sealed record ResearchEvidence(string Id, string? Url, string Title, string Excerpt,
    string ContentHash, DateTimeOffset FetchedAt);

/// <summary>有原文依据的业务事实。</summary>
/// <param name="Value">业务事实。</param>
/// <param name="EvidenceId">来源编号。</param>
/// <param name="Quote">来源中的原文片段，用于核验事实而非执行指令。</param>
public sealed record ResearchFact(string Value, string EvidenceId, string Quote);

/// <summary>经过证据核验的行业画像；客户、需求等推断项与已核实的行业、产品分开。</summary>
/// <param name="Version">画像及证据版本。</param>
/// <param name="Summary">简短业务摘要。</param>
/// <param name="Identity">品牌身份及原文依据。</param>
/// <param name="Industry">行业及原文依据。</param>
/// <param name="Products">主营产品或服务及原文依据。</param>
/// <param name="Vocabulary">客户常用表达。</param>
/// <param name="Scenarios">应用场景线索。</param>
/// <param name="Customers">可能的客户类型，属于待用户纠正的推断。</param>
/// <param name="Needs">可能的需求与痛点，属于推断。</param>
/// <param name="DecisionFactors">选型与购买条件。</param>
/// <param name="Constraints">能力边界、未知项和资料矛盾。</param>
/// <param name="VerifiedCompetitors">官网明确提及为替代方案的竞品；未核实则为空。</param>
public sealed record ResearchBusinessProfile(string Version, string Summary, ResearchFact Identity,
    ResearchFact Industry, IReadOnlyList<ResearchFact> Products, IReadOnlyList<string> Vocabulary,
    IReadOnlyList<string> Scenarios, IReadOnlyList<string> Customers, IReadOnlyList<string> Needs,
    IReadOnlyList<string> DecisionFactors, IReadOnlyList<string> Constraints,
    IReadOnlyList<ResearchCompetitorSuggestion>? VerifiedCompetitors = null);

/// <summary>用户对推荐提问的反馈。</summary>
/// <param name="Prompt">被反馈的问题。</param>
/// <param name="Reason">off_topic、generic、duplicate 或 more_like_this。</param>
public sealed record ResearchQuestionFeedback(string Prompt, string Reason);

/// <summary>随任务持久化的研究进度、证据及业务上下文；客户端只能提交补充文字，不能写入已核实画像。</summary>
/// <param name="StrategyVersion">抓取、提取、生成和审核策略版本。</param>
/// <param name="Phase">collecting、profiling、generating、reviewing、done 或 needs_input。</param>
/// <param name="Website">规范化官网；无官网时为 null，使用已确认业务介绍研究。</param>
/// <param name="BrandName">用户填写的品牌。</param>
/// <param name="Market">目标市场。</param>
/// <param name="Language">提问语言。</param>
/// <param name="BusinessDescription">用户最近补充或纠正的主营业务。</param>
/// <param name="Profile">已核验画像；资料不足时为空。</param>
/// <param name="Evidence">原始来源及摘要。</param>
/// <param name="ExcludedQuestions">此前展示、拒绝或已选的问题。</param>
/// <param name="Feedback">近期用户反馈。</param>
public sealed record ResearchContext(string StrategyVersion, string Phase, string? Website, string? BrandName,
    string Market, string Language, string? BusinessDescription, ResearchBusinessProfile? Profile,
    IReadOnlyList<ResearchEvidence> Evidence, IReadOnlyList<string> ExcludedQuestions,
    IReadOnlyList<ResearchQuestionFeedback> Feedback)
{
    /// <summary>当前研究策略版本，旧结果不能作为当前画像复用（industry-v3 起证据包含品牌知识条目）。</summary>
    public const string CurrentStrategy = "industry-v3";
}
