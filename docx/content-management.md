# 内容管理：使用、实现与验收状态

核对日期：2026-10-06，2026-10-18 补充文章管理页增量（见「文章管理页增量」一节），2026-10-20 补充链接自动发现与每日计费（见「链接自动发现与每日计费」一节；本机 `jogeoai` 已执行 027 并重启 5260 API / Worker，其他库未执行）。本文是内容管理当前接口、机制和本机运行状态的现役入口；[PRD](../.trellis/tasks/09-29-content-management/prd.md) 和 [技术设计](../.trellis/tasks/09-29-content-management/design.md) 保留完整目标。后端已经有实现，本机已有 014 表结构和内容路由；任务仍为 `in_progress`，完整验收尚未完成。

## 当前实现与使用

文章属于租户下的品牌 Site。当前源码支持文章列表/详情（含后端派生阶段与页签计数）、手工编辑版本、标签、归档/恢复、Markdown 导出、发布链接登记/停用（服务端计算官网归属）、链接导入、RAG 素材预览、异步文章生成、引用汇总/证据、全品牌引用域名排行、共享积分账本，以及（2026-10-20 新增，依赖迁移 027）大模型回答引用品牌域名链接的自动建文与追踪链接的每日 10 积分计费。首期由用户人工发布文章后回填 URL；生成成功或登记链接都不能证明文章已经被 AI 引用。

内容接口要求真实登录会话及当前租户成员身份，匿名 `X-Tenant-Id` 不能替代身份。写操作拒绝 viewer；member 只能修改自己创建的文章，owner/admin 可管理同品牌文章。钱包和账本接口拒绝 viewer，member 的账本仅返回本人发起的记录；运营入账还要求系统管理员身份。这些是源码合同，完整的跨角色 HTTP 验收仍待补齐；生成选项中的 viewer 余额展示差异见下文。

生文流程：

1. 读取 `article-generation/options` 选择已启用主问题和目标平台，读取 `materials` 选择可见的知识资料。
2. 提交 `material-preview`，确认资料范围、命中片段、缺口/冲突及冻结的 context；也可明确选择 `withoutRag=true`，此时品牌事实需要人工核对。
   - 勾选资料（`documentIds`）或指定产品（`productKey`）时，这些资料的可见片段一定进入上下文：每份先取一段，再按相关度补足，与问题不匹配的片段得分为 0；与资料无关的问题仍列入缺口。可见性规则（品牌范围、就绪、有效期等）不因指定而放宽。
   - 未配置 Embedding 时按字面匹配：中文按相邻二字切词，未指定资料时片段需命中至少 2 个不同的词。语言为 `und`（上传、网址、产品同步的默认值）的资料对任何文章语言都可见。
   - 2026-10-22 本机 5260 真实数据验证：指定 3188DH 两份资料、仅指定产品、不指定资料三种情况均返回 200（此前均为 `KNOWLEDGE_CONTEXT_EMPTY`）；Trellis 任务 `10-22-article-material-pinned-retrieval`。
3. 提交 `article-generation/preview` 获得篇数、单价、总预占额及预览摘要。素材预览和写作预览有效期均为 15 分钟，预览不扣生文积分。
4. 向 `POST /api/v1/sites/{siteId}/article-generation` 提交预览标识和摘要，并携带 `Idempotency-Key`；服务端创建批次并预占积分。通过 `GET .../article-generation/{batchId}` 轮询，取消接口只取消尚未开始的任务。
5. 成功后编辑、导出、人工发布并登记发布链接，再读取引用汇总和证据。请求/响应字段以运行环境 Swagger 和 [领域 DTO](../src/Jogeoai.Domain/ContentManagementModels.cs) 为准。

每个去重后的主问题生成一篇，选择多个目标平台不增加篇数；1–10 个主问题、1–5 个目标平台，支持 `zh-CN` / `en-US` 和 `answer_guide`、`product_selection`、`pain_comparison`、`reference_structure` 四种策略（产品选型 2026-10-25 源码新增，尚未部署）。实际生成模型固定为 DeepSeek-V4.1-Flash（API 模型名 `deepseek-flash`；`deepseek-v4.1-flash` 只是展示名，接口返回 400。2026-10-28 由 `gpt-6-luna` 切换）；目标平台只是写作输入，不是额外的模型调用。Provider 读取 `research_provider_settings` 中的独立行 `deepseek-article`（[033](../db/migrations/033_article_deepseek_provider.sql) 种子，`https://api.deepseek.com/chat/completions`，请求带 `response_format=json_object`、`max_tokens=16000` 与 `thinking.type=disabled`），不再读取 `openai-compatible` / `openai-api`；品牌研究、产品识别与品牌识别仍用 Luna。本机 Key 用 `./scripts/set-article-deepseek-key.sh` 隐藏输入写入，不在文档或接口中复制凭据。发布须先执行 033 并写入 Key，否则生文返回 503 `ARTICLE_PROVIDER_UNAVAILABLE`。

2026-10-28 本机真实验证（5260 API + 完整 Worker 重启后，临时会话用后已删除）：szulink.com.cn 品牌、主问题「预算3万元以内…512Hz定位的管道内窥镜」、指定产品 3188DH、`product_selection` + `medium`，批次 `bea0b2db…` 首次尝试成功，约 21 秒，正文 2783 个非空白字符，2 条证据 ID，4 条待核对项，版本记录 `deepseek-article` / `deepseek-flash` / `article-writing-v2`，捕获 100 积分。单样本质量问题：正文把问题中的「3万元以内」写成「万元以内」，提示链路未改动数字，属模型输出偏差。此前用错误模型名提交的批次 `f8a11ab5…` 三次 400 后失败，积分已释放，留下一篇「生成失败」文章 `8c8a3cc6…`。

### 产品选型模板（2026-10-25，源码）

`product_selection` 使用服务端 `article-writing-v2` 模板，围绕主问题及关键词完成痛点、选型标准、产品分档、问答、预算人群、总结和软性行动建议。中文中篇（`zh-CN` + `medium`）为 **2000–3000 个非空白字符**，包含正文标题与核对清单，不含其他 JSON 字段；其他类型中文中篇仍为 1500–2200。中文短篇 800–1200、长篇 2500–3500；英文短/中/长篇保持 600–900 / 1000–1400 / 1600–2200 词。系统提示、输出校验与修复重试共享同一范围；类型切换必须重新预览。

有 RAG 时，提交在既有 `knowledge_selection_snapshot.writingMaterials` 可选 JSON 扩展中冻结品牌名称、已保存介绍与更新时间，以及已入库产品身份、知识文档/版本/证据索引。产品参数仍只来自素材预览冻结的证据，不从提交后的可变产品字段补写；旧任务无该扩展仍可读取。无新 migration。显式 `withoutRag=true` 不自动附加品牌或产品资料，输出须带待核验项和披露。

模板与事实数据分离：问题、关键词、品牌资料、产品依据、素材重点、参考页均以 JSON 数据边界传入；参考链接正文只用于结构和表达，不作为品牌事实。缺评分、报价、测量、客户、资质或 OEM/ODM 等素材时不编造，缺型号的档位写作选型方向，并列待补充与核对清单。安全规则与模板不能由参考文章中的指令覆盖。最多两次格式修复会带原始输入、失败原因、明确长度和有界的上次输出，不递归累积历史。

定向验证及提交错误诊断见任务 [后端验证记录](../.trellis/tasks/10-25-article-generation-template-backend/verification.md)。这部分尚未重启真实 API/Worker，未调用真实模型；源代码/隔离夹具结果不能替代真实生成与扣费验收。

同轮隔离库确认并修复生成链路三个问题：提交 INSERT 漏绑定 `@title`，导致 RAG/no-RAG 都无法建文；领取任务的 `context_id` 以及成功结算的 `reservation_id` 未使用 `BIN_TO_UUID`，在默认 MySqlConnector 口径下分别导致丢失上下文 ID、成功路径静默保留 running 状态。现补标题参数并明确转换 UUID，未改变连接串 GuidFormat 或开启用户变量来掩盖错误。新增完整 Submit 回归已覆盖预占、冻结素材、重复提交、领取任务、保存版本、capture/release 和余额不足回滚（人工素材/结果，不调用真实模型）。

### 文章创作（智能学习）后端管线（2026-10-30，本机 `jogeoai` 已执行 040 并完成真实生成验收；前端未接入）

Trellis `10-30-article-studio-backend-pipeline`（父任务 `10-30-article-studio-two-step`）。新增两步「文章创作」的后端：第 1 步创作准备、生成提交、Worker 六阶段管线与轮询。旧向导 `article-generation/*` 的接口与 Worker 行为不变（旧 `preview` / submit 在前端切换前保留）。以下均为源码与替身测试结论，**没有在真实 API + MySQL + Worker 上运行过，也没有调用过真实模型或联网搜索**。

- **迁移 [040](../db/migrations/040_article_studio.sql)**：`article_generation_tasks` 增加 `pipeline`（`legacy` / `studio_v1`，默认 `legacy`）、`include_competitors`、`optimization_goal`，`extra_requirements` 放宽为 TEXT，新增生成列 `studio_active_article_id` + 唯一键（同一文章同时只允许一个 pending/running 的 studio 任务）；新表 `article_generation_task_stages`（六阶段状态、起止时间、公开 `detail_json`、内部 `output_text`）与 `article_generation_task_sources`（引用源 / 联网搜索来源、抓取状态、≤20000 字符正文）。回滚脚本 [rollback/040](../db/migrations/rollback/040_article_studio_rollback.sql) 在存在 studio 任务或超过 4000 字符的写作要求时拒绝执行。未执行 040 时：提交与轮询返回 503 `ARTICLE_STUDIO_SCHEMA_NOT_MIGRATED`；Worker 认领按旧列读取，旧向导不受影响。
- **定价**：`ArticleGenerationPricing.UnitPrice = 100`，替换旧预览与提交两处硬编码，价格不变。
- **`GET article-studio/prepare[?promptId=]`**（登录用户）：返回已启用、未归档的提问（含分组名）、单价、可用积分、Provider 是否已配置、要求上限 5000。传 `promptId` 时：
  - 优化目标按近 7 天全来源（全部 Provider / 采集模式 / 证据类别）推导：未提及 → `mention`，提及但不是第一 → `ranking`，第一（含并列第一）→ `defend`，无已分析回答 → `mention` + `goalBasis=no_data`。`LeaderboardAsync` 拒绝跨来源，实际用 `ShareOfVoiceAsync` 的来源分组把每个主体的提及次数相加。
  - 知识库：可写角色调用现有自动范围素材预览，0 命中不报 422，改存一条不使用知识库的预览并返回 `hitCount=0`（按现有无素材披露规则生成）；只读角色不创建预览（`knowledge` 为空）。
  - 竞品：只用当前启用的已配置竞品，取近 7 天提及次数 > 0 的前 8 名；未配置返回 `none_configured`，已配置但无提及返回 `no_recent_data`。
  - `engineCount`：近 30 天该提问有引用数据的引擎数；`existingArticle`：该提问最近一篇未归档 studio 文章及是否有有效发布链接。
- **`POST article-studio/generations`**（可写角色，`Idempotency-Key` 必填）`{promptId, materialPreviewId?, includeCompetitors, requirements(≤5000), articleId?}` → 202。服务端补齐：目标平台 = 已启用目标前 5 个，语言由提问 locale 推导（en 开头 en-US，其余 zh-CN），策略按目标（mention→answer_guide，ranking→product_selection，defend→pain_comparison），篇幅 medium，风格 professional。预占 100；同键同请求重放返回原结果，同键不同请求 409。素材预览已过期时自动重新检索，未传时服务端自动检索。`articleId` 无有效发布链接 → 同一文章，成功后新增 `generated` 版本并切为当前版本；有有效发布链接 → 新建文章并返回 `regeneratedAsNew=true`；同一文章已有 pending/running 任务 → 409 `ARTICLE_GENERATION_IN_PROGRESS`；已归档 → 409 `ARTICLE_ARCHIVED`。竞品名单与域名在提交时冻结到批次快照。
- **Worker 六阶段**（`pipeline=studio_v1` 才进入 `ArticleStudioPipeline`，旧任务仍是单次 Provider 调用）：① citations：近 30 天全来源该提问高频引用 URL 前 12（引擎集合、按回答去重的被引次数、归属）；② fetch：`ArticleSourceReader` 并发 4 抓取，`SOURCE_BLOCKED` 显示「访问被拒绝」，全部失败记 failed 后继续；③ structure：一次 `deepseek-article` JSON 调用，只提取结构、对比维度和数据类型；④ web_research：DeepSeek Anthropic 兼容 Messages + `web_search_20250305`，复用 `deepseek-article` 凭据，地址由 `api.deepseek.com` 推导为 `/anthropic/v1/messages`；⑤ requirements：组装输入，不调用模型；⑥ compose：现有 Provider 追加 `OPTIMIZATION_GOAL`、`STRUCTURE_INSIGHTS`、`COMPETITORS`（仅名称与核验链接）数据段，引用源正文与搜索结果进入 `UNTRUSTED_CONTENT`（总量 ≤60000），5000 字要求完整进入提示词；旧任务的提示词不变。阶段 1–4 失败或无数据只记 skipped / failed 及原因，不阻断生成；阶段 6 失败按现有规则重试（最多 3 次）或释放积分；重试跳过已成功 / 已跳过的阶段并复用已保存的来源与产物。阶段与来源写入受任务租约保护；写入发现租约已被其他 Worker 接管时立即停止，不再调用模型或联网搜索，也不结算积分。单个网页读取的任何异常只记该来源失败，不影响抓取阶段。
- **联网搜索开关**：只有 `deepseek-article` 行的 `web_search_enabled=1` 时阶段 ④ 才发请求；033 种子与 `set-article-deepseek-key.sh` 都写 0，所以默认跳过（原因「联网搜索未启用」）。接口返回 400/404/422 视为「当前模型服务不支持联网搜索」（skipped），其他错误记 failed。**该 Key 能否调用 `web_search` 未做过真实探测（AC-A7 待用户批准）**。
- **`GET article-studio/generations/{taskId}`**：六阶段（状态、开始/结束、耗时毫秒、计数/原因/错误码）、来源（标题、域名、引擎、被引次数、读取状态与中文失败原因、链接，不含正文）、`fetchedCount`（已抓取正文的引用源数）/ `hitCount`（阶段 1 找到的引用源数）、`versionId`、错误与预占状态。

验证（2026-10-30）：`dotnet build Jogeoai.slnx` 0 错误；定向过滤 `ArticleStudio|ArticleGeneration|ArticleWriting|ContentStoreSql|CreditStoreSql|ContentApiDocumentation` 共 89 项。隔离临时库 `jogeoai_studio_20261030` / `jogeoai_studio_test_20261030`（root 创建，执行 001–040，用后均已删除）：040 第二次执行、回滚两次、重新应用前后完整 schema（列 / 约束 / 索引 / CHECK）比对一致，应用与回滚之差恰为 040 计划对象（AC-A1 通过）；SQL 测试需库名含 `_test`，在 `_test` 库上 `ArticleStudioSubmitSqlTests` 2、`ArticleGenerationSubmissionSqlTests` 4、`ContentStoreSqlTests` 6、`CreditStoreSqlTests` 1 逐类串行两轮全部通过，整组并行一轮 89/89；另两轮并行出现死锁，InnoDB 记录为两测试类夹具同时插入 `subject_terms`（`uq_subject_terms_scope_key` 间隙锁），与 040 及 studio 代码无关，属既有 SQL 测试并行隔离问题。联网搜索（AC-A7 通过，2026-10-30）：本机 `deepseek-article` 设置行已开启 `web_search_enabled`（此前为 0，阶段 4 会显示「跳过 / 联网搜索未启用」）；用户批准后用 `ArticleWebResearchLiveTests`（需 `JOGEOAI_ARTICLE_WEB_RESEARCH_LIVE_CONNECTION` 与 `JOGEOAI_ARTICLE_WEB_RESEARCH_REPORT`，默认跳过）对真实 DeepSeek Anthropic 兼容接口调用，返回 6 次搜索、2778 字摘要、30 条带标题来源（达到 30 条上限），约 17 秒；首次调用因报告目录缺失丢失结果，共调用 2 次。本机部署与真实验收（2026-10-30，用户批准）：备份 `~/jogeoai-backups/jogeoai-pre040-20261030-175332.sql.gz` 后以 root 执行 040（107 → 109 张表，任务 / 文章 / 账本行数不变），重启 5260 API 与完整 Worker。AC-A6：5260 `/openapi/v1.json` 含 prepare / generations / 轮询三个端点与中文说明，studio DTO 描述齐全（可空引用属性的描述位于 `oneOf` 内；枚举与既有约定一致显示为 integer）。AC-A8：临时会话调用优联品牌提问 `b319d9c7…`（prepare：巩固优势、4 条知识命中、6 个已配置竞品可用、11 引擎），提交 202 预占 100；阶段：citations 成功（12 条）、fetch 失败（12 条均「地址无法访问」，本机 Clash fake-ip 触发 `ArticleSourceReader` 私网守卫，环境问题）、structure 跳过（无正文）、web_research 成功（7 次搜索、30 条来源、21 秒）、requirements 成功、compose 首次 `ARTICLE_GENERATION_LENGTH_INVALID`（含 2 次修复重试）后任务级重试 15 秒成功；文章 2254 字，正文引用竞品信息均附官网来源并对无量化依据处如实说明；账本 `reserve -100` 与 `capture` 各一条，可用积分 1251890 → 1251790，`reserved` 回到原 500。临时会话与令牌已删除。

验收中发现的既有缺陷（已修复）：素材预览对 `CreateContextAsync` 返回的内存上下文求摘要，但快照经 MySQL JSON 列落库会改写部分 double（实测 `7.6000000000000005` 读回 `7.6`），提交与 Worker 对回读快照重算摘要不一致，固定返回 409 `KNOWLEDGE_CONTEXT_STALE`（文章参考素材快照校验失败），旧向导提交同样受影响，取决于检索得分是否带浮点尾差。`ArticleMaterialSelectionService.PreviewAsync` 现改为对落库后回读的上下文求摘要并据此生成命中列表；回归测试 `ArticleMaterialPreviewHashTests`（去掉修复即失败）。

### 文章编辑器后端：草稿 / 智能微调 / 图片 / docx（2026-10-30，本机 `jogeoai` 已执行 041 并真实验收；前端未接入）

Trellis `10-30-article-editor-backend`（父任务 `10-30-article-studio-two-step` 的子任务 B）。为共用富文本编辑器提供后端；前端编辑器属子任务 D，尚未接入。以下均为源码与替身 / 夹具测试结论：**迁移 041 未在任何库执行（含本机 `jogeoai`），隔离库 SQL 测试未运行（未提供 `JOGEOAI_CONTENT_TEST_CONNECTION`，相关用例跳过），没有调用过真实 DeepSeek，也没有在 5260 + MySQL 上走通任何编辑器接口。**

- **迁移 [041](../db/migrations/041_article_editor.sql)**（待验收）：`content_article_versions.version_kind` CHECK 增加 `refined`，新增 `edit_revision INT NOT NULL DEFAULT 0`；新表 `article_refine_operations`（智能微调独立预占：held → captured / released，租户内幂等键唯一，记录基础版本、修订号、结果版本与释放原因）与 `content_images`（图片元数据）；`credit_ledger` 增加可空 `refine_operation_id` 及外键。可重放，回滚 [rollback/041](../db/migrations/rollback/041_article_editor_rollback.sql) 在存在 `refined` 版本、微调记录或图片时拒绝执行。未执行 041 时：草稿 / 微调 / 图片接口返回 503 `ARTICLE_EDITOR_SCHEMA_NOT_MIGRATED`，文章详情的 `editRevision` 恒为 0，积分用量按旧口径查询；PATCH 与 Markdown 导出不受影响。部署顺序：先执行 041，再发布 API。
- **自动保存 `PUT /articles/{articleId}/draft`**（可写角色；成员只能改自己创建的文章）`{title, bodyMarkdown, baseVersionId, expectedRevision}` → `ArticleDraftResult`（新的 `versionId` / `editRevision` 作为下一次保存的基础）。当前版本是 `manual_edit`、等于 `baseVersionId`、修订号一致且「未被发布」时原地覆盖并 `editRevision+1`；否则（当前为 `generated` / `refined` / `imported`，或已被发布）新建 `manual_edit` 版本（revision 0，沿用被编辑版本的摘要、SEO、关键词与语言）并切为当前版本。「已被发布」= 有发布链接（含已停用）显式引用该版本，或有未指定版本的链接登记于该版本成为当前版本之后。基础版本或修订号不一致 409 `ARTICLE_VERSION_CONFLICT`；归档 409 `ARTICLE_ARCHIVED`。不修改标签（标签与 SEO 仍走 PATCH）。文章详情的每个版本新增 `editRevision`。
- **编辑侧安全检查**（draft、PATCH 与智能微调结果共用 `ArticleEditSafety`）：拒绝 script / iframe / object / embed / form 标签、HTML 标签上的 `on*=` 事件属性，以及出现在地址位置（Markdown 链接 / 图片地址、自动链接、HTML 属性，即 `(`、`<`、引号或 `=` 之后）的 `javascript:` / `vbscript:` / `data:` 协议（原文与 HTML 实体解码后都检查）→ 400 `ARTICLE_CONTENT_UNSAFE`；正文里作为普通文字出现的「JavaScript:」不拦截。Markdown 图片与 `<img src>` 只允许 `/api/v1/content/images/{imageId}` 或带主机名的 https 链接 → 否则 400 `ARTICLE_IMAGE_SOURCE_INVALID`。允许 Markdown 代码块（与生成侧禁止代码块的规则区分）；超出 Markdig 嵌套 / 表格上限的正文按 400 拒绝。**兼容已有正文**：正文与当前版本完全相同（如只改标题、标签、SEO）时不检查正文；正文有改动时整篇重新检查，但当前版本里已有的图片地址（如导入或早期手工编辑留下的 http 图片，危险协议除外）可以保留。PATCH 的标题、摘要与 SEO 字段同样检查危险标记；PATCH 新增可选 `expectedRevision`，提供时与当前版本 `editRevision` 不一致返回 409 `ARTICLE_VERSION_CONFLICT`（自动保存原地覆盖不改变版本号，只靠 `expectedVersionNo` 无法发现旧页面覆盖草稿）；PATCH 其余行为（每次新建版本、按 `tagIds` 重写标签）不变。
- **智能微调 `POST /articles/{articleId}/refine`**（可写角色，`Idempotency-Key` 必填）`{scope: whole|selection, selectionMarkdown?, instruction(1–500), baseVersionId, expectedRevision}` → `ArticleRefineResult`。流程：同键重放先判定（已结算返回原结果 `replayed=true` 且不扣费；已释放返回原错误；进行中 409 `REFINE_IN_PROGRESS`；同键不同请求 409 `IDEMPOTENCY_CONFLICT`）→ 检查 `deepseek-article` 配置（未配置 503，不预占）→ 短事务校验当前版本 / 修订号并预占 40 积分（锁顺序：微调记录 → 积分账户 → 文章）→ **释放数据库连接后**同步调用 DeepSeek Chat Completions JSON 模式（`deepseek-flash`、关闭思考、单次请求、最长 120 秒，取设置行超时与 120 的较小值）→ 输出校验通过后写入 `refined` 版本、切为当前版本并 capture；模型错误、超时（504 `REFINE_TIMEOUT`）、输出不合格（502 `REFINE_OUTPUT_INVALID`）、客户端断开或期间文章被修改（409，结果丢弃）都 release，余额不变。`selection` 时选中内容（统一换行、去首尾空白）必须在当前正文中恰好出现一次，否则 422 `REFINE_SELECTION_NOT_FOUND`；模型只返回替换片段，服务端原位替换，标题不变。整体微调正文超过 20000 字符、选中内容超过 10000 字符返回 422 `REFINE_INPUT_TOO_LONG`（单次输出上限所致）。预占超过 10 分钟仍未结算（进程崩溃等）在下次同租户微调或同键重放时自动释放（`REFINE_ABANDONED`）。模型输出套用生成侧的危险标记 / 密钥形态 / 角色标签 / 控制字符检查和编辑侧图片规则，并且不得新增代码块、`{{ }}` 或原文没有的图片地址。提示词要求不新增事实、数字、价格、评分、客户、认证等，缺失时保留「【待补充】」——**提示词效果与输出格式稳定性未经真实模型验证**。
- **计费与用量**：`ArticleRefinePricing.UnitPrice = 40`；账本 `reserve -40` / `capture 0` / `release +40` 均带 `refine_operation_id`（capture 不写 `article_version_id`），`credits/usage` 新类型 `article_refine`（金额取 `article_refine_operations.credits`，判定优先于文章生成），成员可见自己发起的微调。
- **图片**：`POST /api/v1/sites/{siteId}/content/images`（可写角色，multipart 字段 `file`）只接受 PNG / JPEG / WebP / GIF，按文件头魔数识别并读取尺寸，扩展名与客户端 Content-Type 不作依据；>5 MB 413 `CONTENT_IMAGE_TOO_LARGE`，格式不符 415 `CONTENT_IMAGE_TYPE_UNSUPPORTED`；返回 `{imageId, url: "/api/v1/content/images/{imageId}"}`。`GET /api/v1/content/images/{imageId}`（登录用户）只允许图片所属租户的有效成员读取，带 `X-Tenant-Id` 时必须与图片租户一致（浏览器 img 标签带不了该请求头，因此不强制）；其他情况统一 404；响应 `X-Content-Type-Options: nosniff`、`Content-Security-Policy: default-src 'none'; sandbox`、私有缓存 1 天。对象复用知识库对象存储（本地 `Knowledge:StorageRoot` 或 S3），键为 `content-images/{tenantId:N}/{imageId:N}`，与知识库 `{tenantId:N}/{versionId:N}/source` 隔离；`IObjectStorage` 只新增 `PutAtAsync`，知识库调用不变。暂无图片配额、删除与孤儿清理（未被任何正文引用的图片会一直保留）。
- **导出 `GET /articles/{articleId}/export?format=markdown|docx`**：默认 markdown 与旧行为一致；`docx` 用 Markdig 解析 + DocumentFormat.OpenXml 生成，标题为 Title 样式，`#`–`######` 为 Heading1–6（含大纲级别），支持段落、粗 / 斜 / 删除线、行内代码与代码块、有序（保留起始序号）/ 无序 / 嵌套列表、引用、超链接、表格（表头加粗重复）；同租户平台图片从对象存储读取后嵌入（最多 50 张且合计不超过 30 MB、按像素尺寸限宽 6 英寸），已删除或他租户图片输出「[图片不可用]」，外链图片不下载，降级为「图片：说明」超链接；原始 HTML 只保留文字；超深的强调嵌套被压平为纯文字，超出解析上限的正文按纯文本段落导出，XML 不允许的控制字符被去掉。单测用 OpenXml 打开并通过 `OpenXmlValidator`，**未在 Microsoft Word / WPS 中人工打开核对（尤其 WebP 嵌入兼容性），待验收**。

验证（2026-10-30）：`dotnet build Jogeoai.slnx` 0 错误；定向过滤 `ArticleDraft|ArticleRefine|ContentImage|ArticleDocx|ArticleEditor|ArticleStudio|ArticleGeneration|ContentStoreSql|CreditStoreSql|CreditUsage|ContentApiDocumentation|ContentManagement` 共 176 项：通过 159、失败 0、跳过 17（全部为需隔离库的 SQL 用例，包括新增 `ArticleDraftSqlTests` 2 项与 `ArticleRefineSqlTests` 2 项）。同日检查修正（安全规则兼容已有正文、PATCH `expectedRevision`、微调结算异常释放、过期预占扫描不加范围锁、docx 防深嵌套与控制字符）后，同一过滤再加 `Knowledge` 共 232 项：通过 212、失败 0、跳过 20（均为需隔离库的 SQL 用例，含新增 `ArticleDraftSqlTests.PatchKeepsLegacyBodiesSaveableAndHonoursExpectedRevision`）。2026-10-30 隔离库 `jogeoai_editor_test_20261030`（root 创建，执行 001–041，用后已删除）：041 第二次执行、回滚两次、重新应用的完整 schema（列 / 约束 / 索引 / CHECK / 外键）比对一致，回滚不影响 040 对象，应用与回滚之差恰为 041 计划对象（AC-B1 通过）；SQL 类逐类串行两轮全部通过：`ArticleDraftSqlTests` 3、`ArticleRefineSqlTests` 2、`ArticleStudioSubmitSqlTests` 2、`ArticleGenerationSubmissionSqlTests` 4、`ContentStoreSqlTests` 6、`CreditStoreSqlTests` 1、`KnowledgeProductStoreSqlTests` 1（AC-B2 / AC-B3 SQL 部分通过）；`KnowledgeStoreSqlTests` 固定要求库名 `jogeoai_stage1_20260926`，未运行。未专门验证同租户两个微调并发预占的死锁情形。本机部署与真实验收（2026-10-30，用户批准，AC-B7 通过）：备份 `~/jogeoai-backups/jogeoai-pre041-20261030-194440.sql.gz` 后以 root 执行 041（109 → 111 张表，版本 / 文章 / 账本行数不变），重启 5260 API 与完整 Worker。临时会话对优联文章 `8708054b…` 验证：草稿首次保存新建 v2 `manual_edit`，再次保存原地覆盖（revision 0→1，无新版本），过期 revision 409 `ARTICLE_VERSION_CONFLICT`，插入 `<iframe>` 400 `ARTICLE_CONTENT_UNSAFE`；上传 64×40 PNG 201（`image/png`、146 字节），文本伪装 PNG 415 `CONTENT_IMAGE_TYPE_UNSUPPORTED`；会话读取（不带 X-Tenant-Id）200、字节一致、`nosniff` + `sandbox` CSP，无会话 401（由会话中间件返回，非 404）；含图片与表格的草稿导出 docx 200（Word 2007+，含 Title / Heading2、13 个列表项、1 个表格、1 张内嵌 `media/image.png`；正文来源为裸 URL 文本，不生成超链接），默认 Markdown 导出不变；测试图片与表格随后以草稿还原原正文（v2 revision 3）；框选段落真实智能微调 1 次约 2 秒成功，仅改写所选段落（更口语、未新增事实），生成 v3 `refined`；同键重放 `replayed=true` 不重复扣费；`article_refine_operations` captured，账本 `reserve -40` + `capture` 各一条（带 `refine_operation_id`），可用积分 1251790 → 1251750，`credits/usage` 含 `article_refine`。临时会话与令牌已删除。遗留：测试图片 `02105041…`（`content_images` 1 行 + 存储对象）已不被正文引用，未删除（无删除端点，清理需确认）；docx 在 Word / WPS 中实际打开仍待人工验收。

## 当前 HTTP 接口

下表中的品牌级路径均位于 `/api/v1/sites/{siteId}` 下。2026-10-06 运行中的 Swagger 可见共 23 个内容管理 HTTP 操作，均有中文 summary；2026-10-18 新增 `domain-rankings` 后为 24 个（临时实例 5281 核对；本机 5260 于 2026-10-19 09:54 重启后实查同为 24 个）；2026-10-20 源码新增 `article-monitoring/billing` 后为 25 个（本机 5260 于 2026-10-20 23:27 重启为该构建，Swagger 实查含 `billing` 路由与 `citation_discovered` 枚举）；2026-10-28 源码新增 `article-monitoring/recommended-channels` 后为 26 个（临时实例 5281 的 Swagger 已核对中文说明；本机 5260 尚未重启为该构建）。该接口依赖迁移 034 `citation_domain_types`，须先迁移再发布 API。

| 方法 | 品牌级路径 | 用途 |
|---|---|---|
| GET | `/articles` | 筛选、排序、游标分页；2026-10-18 起返回 `ArticleListPage`（含总数、有效链接数、六个阶段计数），支持 `stage` / `language` / `ownedOnly`；2026-11-04 起新增引用表格参数与字段（见下文「引用表格字段与筛选」） |
| GET / PATCH | `/articles/{articleId}` | 详情 / 保存手工版本（2026-10-30 起 PATCH 增加编辑侧安全检查） |
| PUT | `/articles/{articleId}/draft` | 自动保存草稿（原地覆盖未发布的人工编辑版本，`editRevision` 乐观锁）；2026-10-30 新增，依赖 041，待验收 |
| POST | `/articles/{articleId}/refine` | 智能微调（整体 / 框选段落，40 积分，`Idempotency-Key`）；2026-10-30 新增，依赖 041，待验收 |
| POST | `/content/images` | 上传编辑器图片（PNG / JPEG / WebP / GIF，≤5 MB）；2026-10-30 新增，依赖 041，待验收 |
| POST | `/articles/{articleId}/archive` | 归档 |
| POST | `/articles/{articleId}/restore` | 恢复 |
| PATCH | `/articles/{articleId}/monitor` | 启用、暂停、停用监控 |
| POST | `/articles/{articleId}/publication-links` | 登记人工发布链接 |
| PATCH | `/articles/{articleId}/publication-links/{linkId}` | 停用链接 |
| GET | `/articles/{articleId}/export` | 导出当前版本：默认 Markdown；`format=docx` 导出 Word（2026-10-30 新增，未在 Word 中核对） |
| GET | `/article-generation/options` | 问题、目标、策略、余额等生成选项 |
| GET | `/article-generation/materials` | 可见知识素材目录 |
| POST | `/article-generation/material-preview` | 素材选择及 RAG 预览 |
| POST | `/article-generation/preview` | 写作及费用预览 |
| POST | `/article-generation` | 提交生成批次，返回 202 |
| GET | `/article-generation/{batchId}` | 批次和逐项结算状态 |
| POST | `/article-generation/{batchId}/cancel` | 取消未开始任务 |
| POST | `/article-imports/preview` | 最多 20 个公开 URL 的逐项读取预览 |
| POST | `/article-imports/confirm` | 按预览确认导入；携带幂等键 |
| GET | `/article-monitoring/summary` | 引用汇总、趋势及域名排行 |
| GET | `/article-monitoring/evidence` | 引用回答证据分页 |
| GET | `/article-monitoring/domain-rankings` | 全品牌引用域名排行（复用品牌引用域名快照）并标注每个域名已登记的文章/链接数；2026-10-18 新增 |
| POST | `/content/owned-urls` | 把引用 URL 标记为自有：登记为有效发布链接（必要时新建导入文章，或恢复停用链接）；viewer 403；2026-11-09 新增 |
| DELETE | `/content/owned-urls/{urlHash}` | 移除自有标记：停用该 URL 的全部有效链接，无剩余链接的导入文章归档；viewer 403；2026-11-09 新增 |
| GET | `/article-monitoring/recommended-channels` | 内容详情「推荐渠道」：全品牌近 30 天引用中，域名类型字典（迁移 034）判定为媒体平台的非自有域名按平台合并后的 Top N（名称、图标域名、前往链接、引用次数）；2026-10-28 新增 |
| GET | `/article-monitoring/billing` | 链接追踪计费摘要：单价、可计费链接数、下一轮预计、可用积分、今日轮次与最近 7 轮；viewer 403；2026-10-20 新增，依赖 027 |

| 方法 | 租户 / 管理路径 | 用途 |
|---|---|---|
| GET | `/api/v1/credits` | 当前租户积分账户 |
| GET | `/api/v1/credits/ledger` | 按角色可见的账本分页 |
| GET | `/api/v1/content/images/{imageId}` | 读取编辑器图片（图片所属租户的登录成员；nosniff）；2026-10-30 新增，依赖 041 |
| POST | `/api/v1/admin/tenants/{tenantId}/credits/grants` | 系统管理员幂等正向入账 |

## 数据、计费与监控机制

[014 migration](../db/migrations/014_content_management.sql) 定义 15 张表：

| 类别 | 表 |
|---|---|
| 文章资产 | `content_articles`、`content_article_versions`、`content_article_tags`、`content_article_references` |
| 页面与监控 | `content_references`、`content_publication_links`、`content_monitor_scopes` |
| 生成流程 | `article_generation_batches`、`article_generation_tasks`、`article_generation_previews`、`article_material_previews` |
| 导入预览 | `article_import_previews` |
| 积分 | `credit_accounts`、`credit_reservations`、`credit_ledger` |

正文及版本元数据当前存于 MySQL。RAG 选择、资料版本、contextId、快照 hash 和依据被冻结到生成输入中；Worker 使用任务内的证据快照，不在执行时重新检索当前知识资料。预览过期或选择发生变化时需要重新预览，不能悄悄扩大资料范围。

每篇成功生成文章消费 100 积分：提交预占，成功保存版本时 capture，失败或未开始任务取消时 release。多篇批次按逐篇结果结算。手工编辑、自动保存、图片上传、导入、导出及读取已有引用不消费积分；智能微调每次 40 积分（2026-10-30 源码，依赖 041，独立预占表 `article_refine_operations`，成功 capture、失败 release，`credits/usage` 类型 `article_refine`，未真实验收）；提问监控计费（019）属于独立模块，见 [README](../README.md) 的提问管理迁移说明。**2026-10-20 起（迁移 027，本机已部署）追踪中的发布链接按品牌本地自然日每条 10 积分计费**，导入本身仍免费，但导入默认开启追踪，从次日轮次起计费；细则见下文「链接自动发现与每日计费」。账本追加写入；`reversal` 类型已经预留，但当前没有运营冲正接口。

[注册实现](../src/Jogeoai.Infrastructure/IdentityStore.cs) 要求邮箱验证码，并在创建用户及初始租户的同一事务创建积分账户、赠送 500 积分和写入 grant 流水，幂等引用为 `registration-{userId:N}`。不需要管理员为每次新注册手动发放初始积分；已有租户不会因部署代码自动补发。邮箱验证码流程及发送验证边界见 [注册引导说明](registration-onboarding.md)。

引用监控读取既有已发布分析和真实 Check/Citation 事实。文章命中按 `(articleId, checkId)` 去重，链接事件按 `(articleId, checkId, urlHash)` 去重；无合格样本时计数为 `null`，不把未知显示成已验证的零引用。监控启停在 `content_monitor_scopes.filter_snapshot.activeWindows` 保存历史生效区间，并递增 scope 版本；查询按 `captured_at` 排除暂停间隔，恢复不补齐暂停时段。证据游标绑定分析快照、发布链接和 scope 指纹；范围变化后需重新查询。

[文章 Worker](../src/Jogeoai.Worker/ArticleGenerationWorker.cs) 有租约、心跳、失败重试和 15 分钟执行 deadline，完成写入受租约约束。`./scripts/run-local.sh worker` 按当前源码注册全部后台任务；`research` 和 `monitoring` 模式均不注册文章生成 Worker。进程存在不能单独证明任务已被正确消费。

## 文章管理页增量（2026-10-18）

为新前端 `/content` 文章管理页（Trellis 任务 `10-18-fe-content-articles`）补充的后端能力，无新迁移、不改计费常量；契约细节见 [content-articles 规范](../.trellis/spec/backend/content-articles.md)。

| 能力 | 现状 |
|---|---|
| 派生阶段 `stage` | 列表行、页签计数与详情共用同一 SQL 表达式，并与 `ArticleStageRules.Derive` 一致：已归档 → 无版本且最新任务 pending/running = 生成中 → 无版本且最新任务 failed/cancelled/stale = 生成失败 → 无有效链接 = 待发布 → 追踪 active = 已追踪 → 其余 = 发布中 |
| 列表响应 `ArticleListPage` | `items`、`nextCursor`、`totalCount`、`activeLinkCount`、固定六键 `stageCounts`、`citedCounts`（2026-11-04）；阶段计数忽略 `stage` 与 `status` 筛选，总数/链接数随完整筛选（含 `cited`）；`stage`、`language`、`ownedOnly`、`citationDays`、`cited`、`citationEngine`、`ownership` 进入游标作用域 |
| 行内新增字段 | `prompt`（来自冻结的 prompt_snapshot）、`generation`（最新任务状态、错误码、排队时间、预占状态）、`ownedPublication` |
| 详情 `generation` | 关联提问、目标模型、策略/篇幅/风格、受众、关键词、补充要求、素材重点、仿写源文章及读取状态、`withoutRag`、命中片段数；不含证据正文与提示词 |
| 域名排行 | `GET .../article-monitoring/domain-rankings`：无 token 时冻结品牌引用域名快照（分析模块默认窗口），有 token 时沿用快照；行、游标、总数与 `analytics/citation-domain-summary/domains` 同口径，仅追加 `registeredArticleCount` / `registeredLinkCount`；无合格回答返回空页 |
| 推荐渠道 | `GET .../article-monitoring/recommended-channels?engine=&limit=`（limit 默认 5，1–20，否则 400 `INVALID_LIMIT`）：每次冻结新的引用域名快照（默认最近 30 个自然日），遍历全部域名后只保留全局字典 `citation_domain_types`（迁移 034）类型为 `media_platform` 且 `ownership≠owned` 的域名；品牌官网 `brand_site`、电商B2B `ecommerce_b2b`、其他 `other` 与字典未收录（未分类）都不返回。字典匹配为精确域名优先、其次最长的「包含子域名」规则；按 `platform_key`（为空时按命中的规则域名）合并，引用事件相加；名称/发布入口取字典，缺省为规则域名与 `https://{规则域名}`；按引用降序、名称升序取前 N；无合格回答返回空列表。字典整表缓存 10 分钟；未执行 034 时本接口 500。种子为 2026-10-28 本机 1494 个引用域名的 AI 分类经用户审核定稿（1265 条规则：媒体 254 / 品牌官网 579 / 电商B2B 256 / 其他 176；另含第一轮目录中本机暂无引用的微博、掘金、youtu.be、wikipedia.org 5 条；测评导购站按用户决定保留为媒体平台），审核表见 [citation-domain-types-review.xlsx](citation-domain-types-review.xlsx)。不返回回答数（跨子域名相加会重复）|
| 发布链接官网归属 | 客户端未传 `ownershipMatch` 时服务端按品牌主域名/附加域名（及子域名开关）计算；导入确认同样写入 |
| 顺带修复 | 布尔列解析接受 `"True"`（影响 `ownershipMatch`、`withoutRag` 与 Worker 领取任务）、多标签 GROUP_CONCAT 分隔符、详情 `created_by` 读取、枚举查询参数 JSON 名绑定、重新生成时请求摘要轮次（同请求再次提交不再恒为 409）、编辑关键词上限 10 |


### 引用表格字段与筛选（2026-11-04，Trellis `11-04-content-citation-table-api`）

`GET /sites/{siteId}/articles` 新增（均可选，不传时结果与改动前一致；无 migration）：

| 参数 | 说明 |
|---|---|
| `citationDays` | 引用统计区间 7 / 30 / 90，默认 30（站点时区近 N 个自然日）；其他值 400 `INVALID_FILTER`。引用次数、趋势、引擎、最早/最近被引、被引判定与按引用排序都用此区间 |
| `cited` | `true` = 区间内被引用 ≥ 1 次；`false` = 0 次，**含无发布链接的文章**（待发布 / 生成中 / 生成失败） |
| `citationEngine` | 单个引擎键（`visibility_checks.engine`，去空白转小写，≤64 字符）；不删行，只把上面所有引用统计和 `citedCounts` 收窄到该引擎 |
| `ownership` | `owned`（有自有域名有效链接）/ `external`（有有效链接且都不是自有域名）；`ownedOnly=true` 等同 `owned`，与 `external` 同用 400 `INVALID_FILTER` |
| `sort` | 新增 `last_cited_desc`、`first_cited_desc`（无被引时间的行排最后，同值按 `updated_at`、`id` 降序） |

响应新增：每行 `primaryLink { linkId, url, publishedAt, ownershipMatch }`（有效链接中自有域名优先，其次 `COALESCE(published_at, created_at)` 最早；`publishedAt` 未填写时为 null；无有效链接为 null；详情接口恒为 null）；`citationSummary.engines`（区间内引用过本文的引擎，按次数降序、同次数按键升序）与 `citationSummary.firstObservedAt`；页面 `citedCounts { all, cited, uncited }`（除 `cited` 与游标外的全部筛选，`all = cited + uncited`）。区间内无引用的行仍返回 `citationCount: null`、`quality: NO_ARTICLE_MONITOR_DATA`、`engines: []`，前端按 0 展示。

被引判定、`citedCounts`、`citation_desc` 与两个新排序都和行内摘要同口径：发布链接 → 引用事实 → 区间内已发布分析版本的回答，链接发布（未填取登记时间）后、停用前，且在文章追踪范围内。`citation_desc` 原先的计数不含追踪范围规则，2026-11-04 经用户决定统一为上述口径，按引用次数排序的结果会因此变化。

同日修复两处缺陷：一是 MySQL 8.4 把被引判定的 `EXISTS` 改写为半连接后，与列表的 LATERAL 关联组合会按观测次数重复返回文章（本机 `cited=true` 实测 46 行 / 39 篇），子查询加 `/*+ NO_SEMIJOIN() */` 后正确；二是既有排序的游标时间只精确到秒，同一秒创建的文章（同批生成）翻页时会被跳过（本机 69 篇只有 34 个不同秒，按创建时间每页 7 条只能取到 49 篇），现所有排序的游标键都按微秒读取。改动前签发的游标会因作用域变化返回一次 400 `INVALID_CURSOR`。

验证（2026-11-04）：`dotnet build Jogeoai.slnx` 0 错误；定向 `ContentStoreSqlTests|ArticleListFilterNormalizationTests|ContentApiDocumentationTests|TeamSummaryTests` 52 项：通过 45、跳过 7（需隔离库 `JOGEOAI_CONTENT_TEST_CONNECTION`，含新增 `CitationTableFieldsFiltersCountsAndSortsFollowWindowEngineAndOwnership`，本机未配置未运行）。真实数据：用新构建以只读方式起 5281（不重启 5260、不写库），优联 69 篇：`citedCounts` 30 天 39/30、7 天 33/36；`cited=true/false` 分别 39/30 篇无重复；`ownership=owned` 62、`external` 0；`citationEngine=kimi` 收窄为 14 篇被引且引擎只含 kimi；五种排序每页 7 条翻页均取全 69 篇无重复，新排序时间单调、空值在后；筛选变化后旧游标 400 `INVALID_CURSOR`；非法区间与 `ownedOnly`+`external` 400 `INVALID_FILTER`；抽查文章 `241b81fa…` 的次数 8、引擎顺序与最早被引 14:15:21 与 SQL 手算（扣除追踪开始 10-29 14:15:04 前的观测）一致；统一口径后 `citation_desc` 在 30 天、7 天、`citationEngine=kimi` 三种条件下每页 7 条均取全 69 篇，行内次数单调不增，次数为 0 的篇数（30 / 36 / 55）与「未被引用」计数一致。本机 5260 尚未重启为新构建。

验证记录（命令与结果见任务 [verification.md](../.trellis/tasks/10-18-fe-content-articles/verification.md)）：解决方案构建 0 错误；定向测试 61 项（55 项无库用例 + 隔离库 `jogeoai_content_test_20261018` 上 6 项 SQL 用例，2026-10-19 复跑全部通过；该临时库仍保留待确认后清理）；前端 6 个测试文件 141 项通过、`npm run build` 通过；2026-10-19 使用夹具预览（3005/5274）完成列表、右栏、新建弹窗、两步向导、生成进度轮询、详情编辑/版本/导出/登记链接/追踪启停、空态与 375 宽度走查。真实 Luna 生成、Worker 消费与次日监控轮次（AC20）仍待在 5260 + Worker 下执行，不能由夹具代替。

## 链接自动发现与每日计费（2026-10-20，迁移 027）

Trellis 任务 `10-20-content-link-monitoring-backend`（父任务 `10-20-content-link-monitoring` 持有决定 D1–D8）。迁移 [027](../db/migrations/027_content_link_monitoring.sql) 只做加法：`content_articles.source_kind` 放宽为 `ai_generated / published_import / citation_discovered`；`content_publication_links` 增加 `discovered_check_id / discovered_at`；新表 `content_citation_discovery_state`（每品牌发现水位）、`article_monitoring_rounds`（每品牌每本地自然日一轮）、`article_monitoring_round_items`（每轮每链接一项）；`credit_ledger` 增加 `article_monitoring_round_id`。`entry_type` 不新增。

| 能力 | 现状（源码） |
|---|---|
| 自动发现 | `ArticleLinkMonitoringWorker`（完整 Worker 模式注册，`ArticleLinkMonitoring:Enabled=false` 可关闭，默认每 5 分钟一轮）按 `analytics_check_arrivals.id` 水位扫描当前已发布 release 下 `citation_classifications.ownership='brand'` 的引用；首次（或已发布 release 变化后）回溯品牌时区近 30 个自然日。该 URL 在本品牌从未登记（含已停用）时建文：`source_kind='citation_discovered'`、`monitor_status='active'`、无正文版本、标题取引用标题或规范化 URL、`created_by` 为租户最早 owner、链接记录最早引用它的 Check。随后同一轮用 `ArticleSourceReader` 读取页面标题（每品牌每轮 ≤ 20 条、单条 15 秒），成功则替换仍为占位的标题并写 `read_status='ready'`，失败写原因且不再重试。发现不触发采集、不单独收费 |
| 每日计费 | 可计费链接 = 未归档且 `monitor_status='active'` 文章下 `status='active'` 的发布链接（手动登记、导入、自动发现同价）。Worker 每轮为当天（`site_brand_settings.time_zone`，缺失为 UTC）尚无轮次的品牌开轮：锁 `credit_accounts` 行，N 条链接预占 10×N（账本 `reserve` 负数，`external_reference=article-monitoring:reserve:{roundId}`）；余额不足写 `status='skipped'`、`skip_code='CREDIT_INSUFFICIENT'`，不写账本，当天不重试、次日重评。次日 00:00 后结算：项与轮次 `captured`，`reserved_credits` 转 `lifetime_captured`，账本 `capture` 金额 0 并关联轮次（与 019 同口径，`credits/usage` 归为新类型 `article_monitoring`）。开轮后停用链接、暂停或归档不释放；当天新开启的链接次日起计费；跳过不改 `monitor_status`、不隐藏引用事实 |
| 接口 | 列表行新增 `discovery`（`checkId / discoveredAt / provider / engine`），`sourceKind` 可筛选 `citation_discovered`；详情链接新增 `discoveredCheckId / discoveredAt`；`GET .../article-monitoring/billing` 返回 `ArticleMonitoringBillingView`（viewer 403）。前端需重新生成类型（子任务 `10-20-fe-content-link-monitoring`） |

验证记录（2026-10-20）：

```bash
dotnet build Jogeoai.slnx
# 0 错误
dotnet test tests/Jogeoai.Tests/Jogeoai.Tests.csproj --filter 'FullyQualifiedName~ArticleLinkMonitoring|FullyQualifiedName~ContentStoreSqlTests|FullyQualifiedName~ContentManagementTests|FullyQualifiedName~ContentApiDocumentationTests|FullyQualifiedName~CreditUsageTests'
# 未设置 SQL 连接：通过 71、跳过 2；设置 JOGEOAI_CONTENT_TEST_CONNECTION 指向隔离库后：通过 79、失败 0、跳过 0
```

隔离库 `jogeoai_content_test_20261020`（本机 Compose `mysql` 容器，root，`--default-character-set=utf8mb4`）：顺序执行 001–027 共 98 张表，027 重放第二次无报错且表/外键数不变；`ArticleLinkMonitoringSqlTests` 两项覆盖发现去重（已登记 / 已停用 / 非自有 / 40 天前的引用均不建文）、增量水位、标题读取与占位替换、开轮预占 20 积分与账户 / 账本 / 计费项、跨本地日结算与 `credits/usage` 归类、余额不足跳过且无账本行、同日重跑幂等、补足余额后次日开轮。该临时库已于当晚经用户确认删除。本机 `jogeoai` 已备份后执行 027（95 → 98 张表，`content_articles` / `credit_ledger` 行数不变），5260 API 与完整 Worker 于 23:27 重启为新构建（`/health/live`、`/health/ready` 200）。真实链路暴露并修正了一处缺陷：`citation_classifications.ownership` 的存储值是 `brand` / `competitor` / `third_party` / `unknown`（`DetailedAnalyticsRules.Classify`），而不是 API 枚举的 `owned`；修正为 `brand` 并重启 Worker 后，本机品牌 szulink.com.cn 的 20 个被引用自有 URL 被自动建文（标题取引用标题；页面标题读取因开发机 Clash fake-ip 触发 `SOURCE_PRIVATE_HOST` 全部失败，属环境问题），当天轮次 `held` 20 条 / 200 积分，账本 `reserve −200`，账户 `reserved 200`。次日结算 `capture` 与 `credits/usage` 的 `article_monitoring` 项待 2026-10-21 观察；其他环境以各自部署为准。

## 本机部署与只读核验

本节保留 2026-10-06 的专项证据；最新本机服务状态和保留对象见 [本机状态核对](local-state-verification.md)，不能用本节历史 Worker 进程证明当前后台正在消费任务。

以下结论仅对应本机 Compose 服务 `mysql` / 容器 `jogeoai-mysql-1`、数据库 `jogeoai` 和 `localhost:5260`，不代表其他运行环境，也不将本机业务库称为生产环境。既往部署记录记载 014 于 2026-09-29 执行；2026-10-06 实查其 15 张表全部存在，相关表共有 49 个外键。表结构存在不等于所有并发、CHECK、幂等和账务算例都通过集成测试。

| 2026-10-06 检查项 | 只读结果 | 能证明的边界 |
|---|---|---|
| MySQL / schema | MySQL 8.4.11；`jogeoai`；014 的 15 张表和 49 个外键存在 | 当前本机结构已具备 |
| API 健康 | `/health/live` 与 `/health/ready` 均为 200 | API 可访问且数据库连接正常 |
| Swagger | `/swagger/v1/swagger.json` 为 200；23 个内容操作均有中文 summary | 运行进程公开了这些路由 |
| 匿名内容访问 | credits 和文章列表均返回 401 | 这两个入口拒绝未登录请求 |
| 注册积分流水 | 1 条 `registration-%` grant；非 500 金额为 0 条 | 观察到一条实际注册赠送记录，未进行新注册 |
| Provider 配置 | `openai-compatible` 启用，凭据已配置 | 配置存在；没有调用网关，模型可用性待验 |
| 生成与监控样本 | generation tasks、credit reservations、monitor scopes 均无记录 | 缺少可供本轮只读核验的文章生成/监控业务样本 |
| Worker / 版本 | 有本项目 Worker 进程；缺少可关联源码版本的部署 marker | 未证明文章任务消费或当前磁盘代码全部已加载 |

核验使用 `curl` / OpenAPI 读取、`ps` / 端口检查，以及经 `.env` 注入连接的 `docker compose exec -T ... mysql` 只读 SELECT；SQL 只统计对象/金额一致性及凭据是否配置，不输出凭据值、用户标识或业务正文。本轮未执行迁移、注册、任务提交、外部模型调用、邮件发送或服务启停。

历史开发验证（2026-09-29，未在本次文档收尾复跑）：

```bash
dotnet build Jogeoai.slnx --no-restore
# 当时结果：0 警告，0 错误
dotnet test tests/Jogeoai.Tests/Jogeoai.Tests.csproj --no-restore   --filter 'FullyQualifiedName~ContentManagement'
# 当时结果：9/9
```

这 9 个 [ContentManagementTests](../tests/Jogeoai.Tests/ContentManagementTests.cs) 覆盖 URL 归一化/私网拒绝、HTTP handler 约束、提示词信任边界、假的 Provider 请求与输出校验、监控游标和启停窗口，以及迁移 SQL 关键约束文本。既往记录还包含隔离 MySQL 8.4 中 001–014 的两次重放；这些证据不能替代实际 API + MySQL + Worker + Luna 生文链路、账务并发和性能验收。

其他环境部署须审阅完整迁移顺序、确认经授权的目标库及停写窗口，由部署流程显式执行；服务启动不会自动应用 014。本机业务库不用于测试或清空；临时数据库/对象的删除需按项目规则先列明并取得确认。

## 域名详情自有标记与问题引用明细（2026-11-09，Trellis `11-09-content-domain-detail-ownership`）

- 前端 `/content` 右栏去掉「文章标签」卡片；点击引用域名排行打开与概览相同的域名详情抽屉，文章排行显示「自有 / 非自有」并可「标记 / 移除」，「数据详情」按提问分组显示引用次数（`analytics/citation-domain-summary/url-prompts`）。
- 标记复用发布链接（不新增表），因此会在内容列表出现 `published_import` 文章并进入链接追踪计费口径（与「导入链接」相同）；「我的引用次数」不回算标记前的历史引用。若同一 URL 已由生成类文章登记，「移除」只停用链接、不归档该文章。
- 待验收：viewer 页面走查、隔离库 SQL 测试（`ContentStoreSqlTests` 新增用例）。

## 待完成的需求与验收

| 项目 | 当前边界 / 下一步 |
|---|---|
| 批量文章操作（PRD §4.1） | 当前仅单篇编辑、归档、恢复、监控操作；批量标签和批量状态操作未实现（新前端「批量操作」为禁用占位） |
| 既有文章 AI 重生（AC11） | 旧向导入口仍创建新文章。2026-10-30 源码新增 `article-studio/generations` 的 `articleId` 重新生成（无有效发布链接时在同一文章新增 `generated` 版本），迁移 040 未执行、未真实验收；前端尚未接入 |
| 导出合同（PRD §4.5） | 导出当前标题和正文：Markdown，或 2026-10-30 源码新增的 docx（未在 Word 中核对）；选定历史版本及勾选 SEO 信息未实现 |
| 文章编辑器后端（10-30 子任务 B，AC-B1–AC-B7） | 本机已执行 041 并完成真实草稿 / 图片 / docx / 微调验收（AC-B7）；Word 打开与前端接入待验收（见上文「文章编辑器后端」） |
| 自定义监控范围（PRD §8.1） | 当前为品牌观测范围；仅生成目标 / 自定义问题和平台范围未实现 |
| 链接自动发现与每日计费真实链路（10-20 任务 AC-A12 / AC-P1 / AC-P2） | 本机已观察到自动建文 20 篇与当天轮次预占 200 积分（AC-P1 通过）；次日结算 `capture` 账本与 `credits/usage` 的 `article_monitoring` 项待 2026-10-21 观察（AC-P2）。页面标题读取在本机因代理 fake-ip 失败，其他环境待验。前端（`Jogeoai.web` `/content`）已接入来源徽标 / 筛选、发现信息、计费卡片与文案，夹具走查见 `10-20-fe-content-link-monitoring/verification.md`。09-29 PRD D07 / §8.1 的「监控不收费」已按本节修订 |
| viewer 积分展示（R01） | credits 接口有角色限制，但 `article-generation/options` 使用 RequireUser 并返回 Credits；完整角色隔离不能宣称已验收 |
| 真实生成与账务（AC04–AC07 等） | 在隔离环境验证真实 Luna 输出、成功 capture、失败 release、部分成功、幂等和租约恢复 |
| RAG / 导入 / 引用链路 | 在隔离环境补齐权限、资料更新、快照稳定、重复导入及多渠道引用对账；当前监控 scope 无业务样本 |
| 性能及观察期（AC20） | P95、15 分钟终态、分析发布到监控可查时延与 24 小时账务观察均未完成 |
| 前端交互（AC02 / AC21 等） | 新前端 `Jogeoai.web` 的 `/content` 页面、两步向导（RAG 素材折叠进第二步，替代 09-29 的独立第三步）和详情页已在夹具预览下走查（2026-10-19，任务 `10-18-fe-content-articles`）；真实 5260 + Worker + Luna 链路（该任务 AC20）未执行，不能据夹具宣称 AC02 / AC21 已验收 |

PRD 的 AC01–AC30 仍是验收目标；没有逐项通过凭证，不能汇总成“全部验收通过”。后续验证优先覆盖上述受影响行为及对应测试，不默认运行全局套件。任务实施与知识收尾记录见 [implement.md](../.trellis/tasks/09-29-content-management/implement.md)。
