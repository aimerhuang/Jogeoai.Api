using Jogeoai.Api.Controllers;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Jogeoai.Api;

/// <summary>公开分析 Controller 手工解析的实际查询参数。</summary>
public sealed class AnalyticsOpenApiOperationFilter : IOperationFilter
{
    private static readonly HashSet<string> PagedActions =
    [
        nameof(AnalyticsController.Leaderboard), nameof(AnalyticsController.Prompts),
        nameof(AnalyticsController.Evidence), nameof(AnalyticsController.PromptChecks),
        nameof(AnalyticsController.FanOutQueries), nameof(AnalyticsController.FanOutTerms),
        nameof(AnalyticsController.FanOutEvidence), nameof(AnalyticsController.CitationUrls),
        nameof(AnalyticsController.CitationDomains), nameof(AnalyticsController.CompetitorCitationGaps),
        nameof(AnalyticsController.GoogleInternal), nameof(AnalyticsController.CitationEvidence),
        nameof(AnalyticsController.CitationChanges), nameof(AnalyticsController.MonitoringRecords),
        nameof(AnalyticsController.BrandDashboardBreakdowns)
    ];

    // 与 AnalyticsController.AllSourcesFilter 一致：未显式筛选时覆盖全部来源。
    private static readonly HashSet<string> AllSourceActions =
    [
        nameof(AnalyticsController.MonitoringSummary), nameof(AnalyticsController.MonitoringModels),
        nameof(AnalyticsController.MonitoringRecords), nameof(AnalyticsController.BrandDashboard),
        nameof(AnalyticsController.BrandDashboardBreakdowns), nameof(AnalyticsController.Prompts),
        nameof(AnalyticsController.Reputation)
    ];

    private static readonly Dictionary<string, string> AllSourceDescriptions = new()
    {
        ["provider"] = "Provider 筛选（cloro、doubao_api、deepseek_api、packy_qwen、packy_glm、packy_kimi、packy_yuanbao、domestic_collector）；可重复或用逗号分隔，默认全部八个。App 口径须显式传 app_collector，它不在默认集合里。",
        ["captureMode"] = "采集模式筛选；可重复或用逗号分隔，默认 api 与 scraped。App 口径须显式传 app（同时 provider=app_collector）；app 不能与其他采集模式一起传，否则返回 INVALID_FILTER。",
        ["evidenceClass"] = "证据类别筛选；可重复或用逗号分隔，默认 api_response、observed_surface 与 unknown。"
    };

    private static readonly (string Name, string Description, JsonSchemaType Type, string? Format)[] Common =
    [
        ("from", "包含的起点：带时区的 ISO 时间；默认最近三十个当地自然日。", JsonSchemaType.String, "date-time"),
        ("to", "不包含的终点：带时区的 ISO 时间；默认当前时间，最长三百六十六个当地自然日。", JsonSchemaType.String, "date-time"),
        ("timeZone", "用于按日统计的 IANA 时区；默认工作区时区。", JsonSchemaType.String, null),
        ("provider", "Provider 筛选（cloro、doubao_api、deepseek_api、packy_qwen、packy_glm、packy_kimi、packy_yuanbao、domestic_collector）；可重复或用逗号分隔，默认 cloro。App 口径须显式传 app_collector。", JsonSchemaType.String, null),
        ("engine", "引擎筛选；可重复或用逗号分隔。", JsonSchemaType.String, null),
        ("country", "两位国家代码筛选；可重复或用逗号分隔。", JsonSchemaType.String, null),
        ("locale", "语言地区筛选；可重复或用逗号分隔。", JsonSchemaType.String, null),
        ("captureMode", "采集模式筛选；可重复或用逗号分隔，默认 scraped。App 口径须显式传 app（同时 provider=app_collector）；app 不能与其他采集模式一起传，否则返回 INVALID_FILTER。", JsonSchemaType.String, null),
        ("evidenceClass", "证据类别筛选；可重复或用逗号分隔，默认 observed_surface。", JsonSchemaType.String, null),
        ("promptId", "Prompt 标识筛选；可重复或用逗号分隔。", JsonSchemaType.String, null),
        ("promptVersionId", "Prompt 版本标识筛选；可重复或用逗号分隔。", JsonSchemaType.String, null),
        ("tag", "采集时标签名称筛选；可重复或用逗号分隔。", JsonSchemaType.String, null),
        ("tagMode", "多个标签的匹配模式：any 或 all；默认 any。", JsonSchemaType.String, null),
        ("brandedStatus", "采集时品牌词分类：branded、unbranded 或 unknown。", JsonSchemaType.String, null),
        ("aggregationMode", "聚合模式；默认 observed，仅 Visibility 和声量支持 latest_observation。", JsonSchemaType.String, null),
        ("modelVersion", "实际模型版本筛选；可重复或用逗号分隔。", JsonSchemaType.String, null),
        ("modelVersionUnavailable", "是否只看缺少具体模型版本的记录。", JsonSchemaType.Boolean, null),
        ("subjectId", "品牌或竞品主体标识筛选；可重复或用逗号分隔。", JsonSchemaType.String, "uuid"),
        ("competitorId", "竞品主体标识筛选；可重复或用逗号分隔。", JsonSchemaType.String, "uuid"),
        ("status", "Check 状态筛选；可重复或用逗号分隔。", JsonSchemaType.String, null),
        ("snapshotToken", "十五分钟有效的分析快照令牌；跨图表、翻页和证据下钻复用。", JsonSchemaType.String, null)
    ];

    /// <inheritdoc />
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(AnalyticsController)) return;

        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is not OpenApiParameter concrete) continue;
            if (concrete.Name == "siteId") concrete.Description = "品牌工作区标识。";
            else if (concrete.Name == "jobId") concrete.Description = "异步重算任务标识。";
            else if (concrete.Name == "checkId") concrete.Description = "当前品牌下的检测标识。";
        }
        Add(operation, "X-Tenant-Id", "仅本机开发环境使用的租户标识；生产环境需可信身份映射。",
            JsonSchemaType.String, null, ParameterLocation.Header);
        var action = context.MethodInfo.Name;
        if (action == nameof(AnalyticsController.CreateJob))
        {
            Add(operation, "Idempotency-Key", "重算请求的必填幂等键；同键同请求复用原任务。",
                JsonSchemaType.String, null, ParameterLocation.Header, required: true);
            return;
        }
        if (action == nameof(AnalyticsController.GetJob)) return;
        if (action is nameof(AnalyticsController.MonitoringRecords) or nameof(AnalyticsController.MonitoringAnswerDetail))
        {
            Add(operation, "search", "历史问题快照的字面子串搜索：去首尾空白、忽略大小写，最多二百字符；空白等同不搜索，游标绑定搜索值。",
                JsonSchemaType.String);
            Add(operation, "mention", "回答品牌提及筛选：mentioned 已提及、not_mentioned 已确认未提及、unknown 待确认；去首尾空白并忽略大小写，省略或空白表示全部，不支持的值返回 INVALID_FILTER。分页前筛选且游标绑定此值；详情仅据此计算 matchesListFilters。",
                JsonSchemaType.String);
            Add(operation, "citedDomain", action == nameof(AnalyticsController.MonitoringRecords)
                ? "被引域名筛选：规范化 ASCII 主机（取自引用域名榜 domain，不合并子域名）。只保留至少有一条有效引用事件属于该域名的回答，域名键、剔除规则与引用域名汇总相同；分页前筛选，参与总数与游标；省略或空白表示全部，非法值返回 INVALID_FILTER。"
                : "可选的列表被引域名筛选，仅校验格式（非法值返回 INVALID_FILTER），不改变详情内容。",
                JsonSchemaType.String);
        }
        if (action is nameof(AnalyticsController.MonitoringRecords) or nameof(AnalyticsController.MonitoringAnswerDetail))
        {
            var records = action == nameof(AnalyticsController.MonitoringRecords);
            Add(operation, "sentiment", records
                ? "口碑状态筛选：positive 正面、neutral 中性、negative 负面、unknown 未知、not_mentioned 未关联、pending 待判定、failed 判定失败、not_applicable 非有效回答；忽略大小写，省略或空白表示全部，不支持的值返回 INVALID_FILTER。与 analytics/reputation 使用同一快照与同一状态归并，分页前筛选，参与总数与游标；口碑迁移 038 未部署时返回 503 REPUTATION_SCHEMA_NOT_MIGRATED。"
                : "可选的列表口碑状态筛选，仅校验取值（非法值返回 INVALID_FILTER），不改变详情内容。",
                JsonSchemaType.String);
            SetEnum(operation, "sentiment", ["positive", "neutral", "negative", "unknown", "not_mentioned", "pending", "failed", "not_applicable"]);
            Add(operation, "term", records
                ? "印象词筛选：按与 analytics/reputation 印象词相同的规范化词键（NFKC、去首尾空白、合并空白、小写）精确匹配，只命中已判定（正 / 中 / 负）回答；规范化后 1–64 字符，否则返回 INVALID_FILTER。分页前筛选，参与总数与游标。"
                : "可选的列表印象词筛选，仅校验长度，不改变详情内容。",
                JsonSchemaType.String);
            Add(operation, "termPolarity", records
                ? "词极性筛选：positive 正面、neutral 中性、negative 负面，只保留 term 以该极性出现的回答（与 analytics/reputation 情感因子的 positiveAnswers / neutralAnswers / negativeAnswers 同口径）；与回答整体判定 sentiment 相互独立、按且组合。只能与 term 一起使用，单独传或取值不支持返回 INVALID_FILTER；忽略大小写，空白表示不限。分页前筛选，参与总数与游标。"
                : "可选的列表词极性筛选，仅校验取值且须与 term 一起传（否则返回 INVALID_FILTER），不改变详情内容。",
                JsonSchemaType.String);
            SetEnum(operation, "termPolarity", ["positive", "neutral", "negative"]);
        }
        if (action == nameof(AnalyticsController.MonitoringAnswerDetail))
        {
            Add(operation, "citationProjection", "可选冻结引用归属投影，仅支持 domain-summary-v1；不改变原始引用。",
                JsonSchemaType.String);
            SetEnum(operation, "citationProjection", ["domain-summary-v1"]);
            Add(operation, "snapshotToken", "必填的有效列表快照；保留发布版本及水位。过期返回 SNAPSHOT_EXPIRED，快照后入库或完成返回 SNAPSHOT_REFRESH_REQUIRED，需同时刷新列表和详情。",
                JsonSchemaType.String, required: true);
            return;
        }
        if (action is nameof(AnalyticsController.CitationDomainSummary)
            or nameof(AnalyticsController.CitationDomainSummaryDomains)
            or nameof(AnalyticsController.CitationDomainSummaryEvidence)
            or nameof(AnalyticsController.CitationDomainSummaryUrls)
            or nameof(AnalyticsController.CitationDomainSummaryDetail)
            or nameof(AnalyticsController.CitationDomainSummaryUrlPrompts))
        {
            var summary = action == nameof(AnalyticsController.CitationDomainSummary);
            Add(operation, "snapshotToken", "统一母集快照；缺少返回 SNAPSHOT_REQUIRED，过期返回 SNAPSHOT_EXPIRED，规则不支持返回 SNAPSHOT_REFRESH_REQUIRED。",
                JsonSchemaType.String, required: !summary);
            Add(operation, "channel", summary
                ? "采集渠道，默认 api：全来源口径，与不传时相同；app 只统计 App 渠道的回答。两个渠道不会合并统计。其他取值返回 INVALID_FILTER。"
                : "采集渠道，默认 api；必须与生成 snapshotToken 的摘要请求相同，不一致返回 INVALID_FILTER。", JsonSchemaType.String);
            SetEnum(operation, "channel", ["api", "app"]);
            if (action == nameof(AnalyticsController.CitationDomainSummaryUrlPrompts))
            {
                Add(operation, "urlHash", "必填的 URL 摘要（取自引用文章榜行的 urlHash，六十四位十六进制，忽略大小写）；格式错误返回 INVALID_FILTER，快照内没有该 URL 返回 RESOURCE_NOT_FOUND。",
                    JsonSchemaType.String, required: true);
                Add(operation, "engine", "可选的单个平台代码，规范化为小写；总数、分组与问题次数都只统计该平台（与引用文章榜同 engine 时该行 citationEvents 一致）。快照限定平台时必须在其中，否则返回 INVALID_FILTER。",
                    JsonSchemaType.String);
                return;
            }
            if (action == nameof(AnalyticsController.CitationDomainSummaryDetail))
            {
                Add(operation, "domain", "必填的规范化 ASCII 域名（取自域名榜 domain，子域名不合并）；快照内没有该域名时返回 0 与补零趋势。非法值返回 INVALID_FILTER。",
                    JsonSchemaType.String, required: true);
                Add(operation, "engine", "可选的单个平台代码，规范化为小写；只影响逐日趋势，总数与平台分布始终为全部平台。快照限定平台时必须在其中，否则返回 INVALID_FILTER。",
                    JsonSchemaType.String);
                return;
            }
            if (summary)
            {
                foreach (var (name, description, type, format) in Common.Where(p => p.Name is "from" or "to" or "timeZone" or "engine"))
                    Add(operation, name, description, type, format);
            }
            else
            {
                Add(operation, "cursor", "绑定快照、投影版本、资源与规范化筛选的游标；错误复用返回 INVALID_CURSOR。", JsonSchemaType.String);
                Add(operation, "limit", action == nameof(AnalyticsController.CitationDomainSummaryUrls)
                    ? "每页一至一百条，默认五十。" : "每页一至一百条，默认二十。", JsonSchemaType.Integer, "int32");
                if (action == nameof(AnalyticsController.CitationDomainSummaryUrls))
                {
                    Add(operation, "domain", "可选的规范化 ASCII 域名（取自域名榜 domain）；只返回该完整域名下的 URL，子域名不合并；传入时每行附带域名内连续名次 domainRank。非法值返回 INVALID_FILTER。", JsonSchemaType.String);
                    Add(operation, "engine", "可选的单个平台代码，规范化为小写，必须与 domain 一起传；引用次数、回答数、提问数和我的引用次数只统计该平台，全局名次 rank 按该平台在完整母集中重新排定，domainRank 按该平台在域名内重新排定。快照限定平台时必须在其中，否则返回 INVALID_FILTER。", JsonSchemaType.String);
                    Add(operation, "q", "可选的标题或 URL 字面包含搜索，去首尾空白并忽略大小写，一到一百字符，必须与 domain 一起传；不重新编号 rank 与 domainRank。", JsonSchemaType.String);
                    Add(operation, "mine", "可选，只接受 true 或 false，必须与 domain 一起传；true 只返回命中本品牌有效发布链接的「我的文章」，不重新编号 rank 与 domainRank。", JsonSchemaType.Boolean);
                }
                else if (action == nameof(AnalyticsController.CitationDomainSummaryDomains))
                {
                    Add(operation, "q", "规范化域名的字面子串，去首尾空白并忽略大小写；最多二百 UTF-16 字符，百分号和下划线不是通配符。", JsonSchemaType.String);
                    Add(operation, "websiteCategory", "十一类网站类型筛选；不改变摘要分母。非法值返回 INVALID_FILTER。", JsonSchemaType.String);
                    SetEnum(operation, "websiteCategory", ["corporate", "news_media", "community_forum", "reference", "government_nonprofit", "blog_personal", "education", "video", "social_media", "other", "unclassified"]);
                    Add(operation, "ownership", "独立的当前品牌归属筛选；非法值返回 INVALID_FILTER。", JsonSchemaType.String);
                    SetEnum(operation, "ownership", ["owned", "non_owned", "unknown"]);
                }
                else Add(operation, "domain", "必填的规范化 ASCII 域名；证据仅来自同一合格回答母集。", JsonSchemaType.String, required: true);
            }
            return;
        }

        var allSources = AllSourceActions.Contains(action);
        foreach (var (name, description, type, format) in Common)
            Add(operation, name, allSources && AllSourceDescriptions.TryGetValue(name, out var defaults)
                ? defaults : description, type, format);
        if (action == nameof(AnalyticsController.FanOut))
            SetSuccessExample(operation, FanOutExample(), "成功：四个有效回答的搜索词固定算例。", "搜索词指标示例");
        else if (action == nameof(AnalyticsController.Citations))
            SetSuccessExample(operation, CitationsExample(), "成功：四个有效回答的外部引用固定算例。", "引用指标示例");
        if (PagedActions.Contains(action))
        {
            Add(operation, "cursor", "绑定当前筛选与快照的下一页游标。", JsonSchemaType.String);
            Add(operation, "limit", "每页数量，范围一至一百；默认五十。", JsonSchemaType.Integer, "int32");
        }
        switch (action)
        {
            case nameof(AnalyticsController.Reputation):
                Add(operation, "view", "数据视图：range 范围数据（默认，趋势覆盖范围内每一个当地日期）或 day 当日数据（from / to 必须恰好是一个当地自然日，趋势为空、改看平台分布）。环比对比前一个等长窗口，当日数据即前一日。",
                    JsonSchemaType.String);
                SetEnum(operation, "view", ["range", "day"]);
                break;
            case nameof(AnalyticsController.ShareOfVoice):
                Add(operation, "topN", "环图显示的前 N 个主体，范围一至一百；默认五。", JsonSchemaType.Integer, "int32");
                break;
            case nameof(AnalyticsController.Prompts):
                Add(operation, "sort", "Prompt 排序：visibility、checkCount 或 recent。", JsonSchemaType.String);
                break;
            case nameof(AnalyticsController.Evidence):
                Add(operation, "subjectId", "提及主体标识。", JsonSchemaType.String, "uuid");
                Add(operation, "evidencePromptId", "贡献证据关联的 Prompt 标识。", JsonSchemaType.String, "uuid");
                Add(operation, "queryHash", "搜索词 SHA-256 摘要。", JsonSchemaType.String);
                Add(operation, "urlHash", "外部引用 URL 的 SHA-256 摘要。", JsonSchemaType.String);
                Add(operation, "domain", "外部引用域名。", JsonSchemaType.String);
                break;
            case nameof(AnalyticsController.FanOutEvidence):
                Add(operation, "queryHash", "搜索词 SHA-256 摘要。", JsonSchemaType.String);
                Add(operation, "evidencePromptId", "贡献证据关联的 Prompt 标识。", JsonSchemaType.String, "uuid");
                break;
            case nameof(AnalyticsController.CitationEvidence):
                Add(operation, "urlHash", "外部引用 URL 的 SHA-256 摘要。", JsonSchemaType.String);
                Add(operation, "domain", "外部引用域名。", JsonSchemaType.String);
                Add(operation, "evidencePromptId", "贡献证据关联的 Prompt 标识。", JsonSchemaType.String, "uuid");
                break;
            case nameof(AnalyticsController.CitationUrls):
                Add(operation, "domain", "只列出此完整域名下的 URL；分页游标绑定该域名。", JsonSchemaType.String);
                break;
            case nameof(AnalyticsController.CitationChanges):
                Add(operation, "minimumEvents", "纳入变化列表的最少引用事件数，范围一至一千；默认二。",
                    JsonSchemaType.Integer, "int32");
                Add(operation, "declineThreshold", "显著下降比例阈值，范围零至一；默认 0.5。",
                    JsonSchemaType.Number, "double");
                break;
        }
    }

    private static void SetEnum(OpenApiOperation operation, string name, string[] values)
    {
        var parameter = operation.Parameters?.OfType<OpenApiParameter>().FirstOrDefault(p => p.Name == name);
        if (parameter?.Schema is OpenApiSchema schema)
            schema.Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
    }

    private static void Add(OpenApiOperation operation, string name, string description,
        JsonSchemaType type, string? format = null, ParameterLocation location = ParameterLocation.Query,
        bool required = false)
    {
        operation.Parameters ??= [];
        if (operation.Parameters.Any(parameter => parameter.Name == name && parameter.In == location)) return;
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            Description = description,
            In = location,
            Required = required,
            Schema = new OpenApiSchema { Type = type, Format = format },
            Example = name switch
            {
                "from" => JsonValue.Create("2026-09-07T00:00:00+08:00"),
                "to" => JsonValue.Create("2026-09-08T00:00:00+08:00"),
                "timeZone" => JsonValue.Create("Asia/Shanghai"),
                "provider" => JsonValue.Create("cloro"),
                "engine" => JsonValue.Create("chatgpt"),
                "aggregationMode" => JsonValue.Create("observed"),
                "limit" => JsonValue.Create(50),
                "topN" => JsonValue.Create(5),
                "minimumEvents" => JsonValue.Create(2),
                "declineThreshold" => JsonValue.Create(0.5),
                "X-Tenant-Id" => JsonValue.Create("11111111-1111-4111-8111-111111111111"),
                "Idempotency-Key" => JsonValue.Create("analysis-example-001"),
                _ => null
            }
        });
    }

    private static void SetSuccessExample(OpenApiOperation operation, JsonNode value,
        string description, string summary)
    {
        if (operation.Responses is null || !operation.Responses.TryGetValue("200", out var result)
            || result is not OpenApiResponse response
            || response.Content is null
            || !response.Content.TryGetValue("application/json", out var media)) return;
        response.Description = description;
        media.Examples ??= new Dictionary<string, IOpenApiExample>();
        media.Examples["fixedFixture"] = new OpenApiExample
        {
            Summary = summary,
            Description = "数据与标识仅用于接口说明。",
            Value = value
        };
    }

    private static JsonNode MetaExample() => JsonNode.Parse("""
        {
          "snapshotToken": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "filters": {
            "from": "2026-09-06T16:00:00Z", "to": "2026-09-07T16:00:00Z",
            "timeZone": "Asia/Shanghai", "providers": ["cloro", "doubao_api"], "engines": ["chatgpt", "doubao"],
            "countries": [], "locales": [], "captureModes": ["api", "scraped"],
            "evidenceClasses": ["api_response", "observed_surface"], "promptIds": [], "promptVersionIds": [],
            "tags": [], "tagMode": "any", "brandedStatus": null, "aggregationMode": "observed"
          },
          "metricVersion": "mentions-v1", "analysisVersion": 2, "subjectConfigVersion": 1,
          "configurationOutdated": false, "asOf": "2026-09-07T15:00:00Z",
          "expiresAt": "2026-09-07T15:15:00Z", "lastCapturedAt": "2026-09-07T12:00:00Z",
          "dataUpdatedAt": "2026-09-07T12:00:03Z", "arrivalWatermark": 4,
          "analysisWatermark": 4, "pendingAnalysisChecks": 0,
          "failedAnalysisChecks": 0, "missingCapturedAtChecks": 0
        }
        """)!;

    private static JsonNode FanMetricsExample() => JsonNode.Parse("""
        {
          "queryEvents": 3, "checksWithExpansion": 3, "successfulChecks": 4,
          "eligibleChecks": 4,
          "distinctQueries": 2,
          "queriesPerExpandedCheck": { "numerator": 3, "denominator": 3, "value": 1.0 },
          "queriesPerSuccessfulCheck": { "numerator": 3, "denominator": 4, "value": 0.75 },
          "expansionRate": { "numerator": 3, "denominator": 4, "value": 0.75 },
          "exposedQueryRate": { "numerator": 3, "denominator": 4, "value": 0.75 },
          "associatedBrandMentionRate": { "numerator": 3, "denominator": 3, "value": 1.0 },
          "availableChecks": 3, "emptyChecks": 0, "notExposedChecks": 0,
          "unknownChecks": 1, "pendingDetailedChecks": 0
        }
        """)!;

    private static JsonNode FanOutExample() => new JsonObject
    {
        ["meta"] = MetaExample(),
        ["metrics"] = FanMetricsExample(),
        ["engines"] = new JsonArray(new JsonObject
        {
            ["engine"] = "chatgpt",
            ["metrics"] = FanMetricsExample()
        }),
        ["queryVersion"] = "query-v1",
        ["tokenizerVersion"] = "term-v1"
    };

    private static JsonNode CitationsExample() => new JsonObject
    {
        ["meta"] = MetaExample(),
        ["externalCitationEvents"] = 4,
        ["checksWithCitations"] = 3,
        ["promptsWithCitations"] = 2,
        ["successfulChecks"] = 4,
        ["eligibleChecks"] = 4,
        ["ownCitationRate"] = JsonNode.Parse("""{"numerator":2,"denominator":4,"value":0.5}"""),
        ["ownEventShare"] = JsonNode.Parse("""{"numerator":2,"denominator":4,"value":0.5}"""),
        ["sourceDistribution"] = JsonNode.Parse("""
            [{"value":"brand","citationEvents":2,"share":{"numerator":2,"denominator":4,"value":0.5}},
             {"value":"competitor","citationEvents":1,"share":{"numerator":1,"denominator":4,"value":0.25}},
             {"value":"other","citationEvents":1,"share":{"numerator":1,"denominator":4,"value":0.25}}]
            """),
        ["pageTypeDistribution"] = JsonNode.Parse("""
            [{"value":"other","citationEvents":3,"share":{"numerator":3,"denominator":4,"value":0.75}},
             {"value":"article","citationEvents":1,"share":{"numerator":1,"denominator":4,"value":0.25}}]
            """),
        ["trend"] = JsonNode.Parse("""[{"date":"2026-09-07","citationEvents":4,"checkCount":3}]"""),
        ["urlVersion"] = "url-v1",
        ["classificationVersion"] = "citation-v1",
        ["googleInternalEvents"] = 1,
        ["pendingDetailedChecks"] = 0
    };
}
