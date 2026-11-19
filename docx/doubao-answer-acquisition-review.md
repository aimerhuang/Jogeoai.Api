# 豆包回答接入方案评审（2026-10-20）

本文评审"通过独立 Playwright Collector 获取豆包回答并入库"的既有方案，对照市面产品的获取方式，给出接入建议。结论只基于当前源码、已归档任务记录和公开资料，不把设计目标写成已实现。

## 1. 既有方案是什么

| 层 | 现状 | 证据 |
|---|---|---|
| 控制面（Jogeoai API） | 已实现 `POST /internal/collector/leases`、`heartbeats`、`results`、`health`，CN Service Token、region 绑定、租约版本、幂等 accepted/duplicate/stale、提交审计 | `src/Jogeoai.Api/Controllers/CollectorController.cs`，`src/Jogeoai.Infrastructure/DomesticCollectorStore.cs` |
| 数据库 | 003 迁移建了 `collector_platforms`、`collector_platform_entries`、`collector_tasks`、`collector_attempts`、`collector_result_submissions`，seed 七个国内平台 | `db/migrations/003_domestic_collector.sql` |
| Worker | 只做每 30 秒一次的租约过期回收，不生成任务、不触发浏览器 | `src/Jogeoai.Worker/Worker.cs` |
| 数据面（浏览器） | 相邻仓库 `Ai-visiblility-collector` 的 Node.js + Playwright，`DoubaoAdapter` 继承通用 `ConsumerWebAdapter`，靠外置 page contract（选择器清单）驱动 | `apps/collector/src/adapters/doubao.ts`、`consumer-web.ts` |
| 真实闭环 | 2026-09-07 在参考项目自己的 Postgres API（4314）上跑过 4 条豆包问题；对 Jogeoai 的真实闭环从未完成。2026-09-26 验证时本机未配置 `Collector:ChinaServiceToken`（401），七平台 contract 全部超过 7 天（`page_contract_unverified`） | `.trellis/tasks/09-26-verify-domestic-collector-seven-platforms/results.md`，归档任务 `implement.md` 阶段 6/7 全部未勾选 |

## 2. 评审结论

### 2.1 做得对的部分

- 控制面与数据面分进程、只通过版本化 HTTP 合同通信，Collector 不碰 MySQL，Jogeoai 不起浏览器。这个边界正确，应保留。
- 租约（`FOR UPDATE SKIP LOCKED` + lease_version）、幂等键、不可变提交审计、加密 spool 重放、"失败不伪造回答"，这些是生产级采集队列应有的性质，已经比大多数开源 GEO 工具扎实。
- 结果提交事务会登记分析工作项（`DomesticCollectorStore.cs:364`），说明国内回答能进入现有分析管线。

### 2.2 不适合直接作为豆包生产通道的原因

1. **Collector 的 real 模式是"受控冒烟"，不是采集器。** 硬编码 4 条问题白名单（`approval-guard.ts` 的 `APPROVED_QUESTIONS`），每次运行最多 4 条（`COLLECTOR_MAX_TASKS_PER_RUN` 上限 4），同一问题在 Profile 账本内永不重复提问，单进程只能绑定一个平台、capacity 固定 1、`headless=false`、需人工登录，遇验证码直接停机。要做每日监控，这些保护都得重写，而重写的方向正是账号风控和合规风险所在。
2. **page contract 7 天人工复审。** 七个平台每周都要有人打开真实页面核对 13 个选择器并改 `reviewedAt`，否则 probe 直接拒绝。09-26 的验证就是这样全部卡住的。这是长期运维成本，不是一次性工作。
3. **结果合同太薄，前端无法等价展示。** 国内结果只有 `answerText` 和引用 URL；没有搜索词、引用标题、模型版本、`resultQuality`，入库时也不写 `search_queries`、`model_version`、`query_availability`（对比 `CloroVisibilityStore.cs:423`）。而新前端"大模型回答记录"页的豆包样例已经假定有搜索词和引用面板（`.trellis/tasks/10-17-fe-model-answer/verification.md`）。页面抓取本身也拿不到豆包实际检索的关键词。
4. **与主编排链路割裂。** 新编排的 Target 校验明确写着"本期新编排仅支持 Cloro Target"（`ManagementStore.Targets.cs:166`，`CollectionScheduleStore.cs:530`），国内平台进不了预览、每日计划、自动监控，也不写 `usage_ledger`，不扣项目积分。国内请求仍走旧的 `platforms` 兼容入口，单样本。
5. **出口网络和账号。** Collector 强制 `--no-proxy-server` 直连，`egressCountry=CN` 只是配置断言。生产需要国内服务器和专用授权账号；本机 Clash TUN 全局模式会直接影响地区判断。用个人账号模拟消费者浏览违反豆包用户协议中的自动化访问条款，B 端产品不能建立在这上面。

### 2.3 一句话结论

旧方案适合作为"真实消费端页面证据"的低频抽样通道，不适合作为豆包每日监控的主数据源。豆包主通道应改走火山方舟官方 API，Collector 降级为校准和取证用途。

## 3. 市面产品的获取方式

### 3.1 两大流派

| 流派 | 代表 | 取数方式 | 优点 | 缺点 |
|---|---|---|---|---|
| 真实页面模拟 | Cloro（本项目海外通道）、透镜 GEO、豆见 BeanInsight、飞哨等国内 GEO 工具 | 登录态浏览器打开豆包网页/App，提问后抓正文和"参考 N 篇资料"，保留截图原文作证据；透镜 GEO 甚至把"网页端"和"APP 端"分成两个口径 | 最接近用户真实看到的回答 | 账号池、风控、选择器维护、并发低、单位成本高；Cloro 目前只覆盖 9 个海外平台，不支持豆包和任何国内模型 |
| 官方 API | 多数自建监测脚本、部分国内工具 | 火山方舟 `ark.cn-beijing.volces.com/api/v3`，配联网搜索工具 | 可规模化、可重复、结构化引用、合规 | 回答与 App 不完全一致（联网、个性化、地域），需要在产品上标明口径 |

业界共识是"API 口径"与"App 口径"会有差异，所以成熟产品要么只做一种并说明，要么两种都做并分开展示。本项目的设计文档本来就为此预留了 `captureMode=api` 与 `scraped` 的证据隔离（`docx/cloro-foundation-design.md` 第 10 节）。

### 3.2 开源逆向方案（不建议）

`doubao-2api`、`doubao2API`、`WebModel` 这类项目靠 Playwright 注入字节的 `a_bogus`/`msToken` 签名、自备 cookie 池轮询来伪装成网页请求。签名算法混淆且频繁更新，作者自己也承认风控风险，且不返回引用。不符合本项目"不绕过访问控制"的原则，排除。

### 3.3 火山方舟官方路径（2026-10 现状）

| 路径 | 说明 | 返回的内容 | 计费 | 限制 |
|---|---|---|---|---|
| **豆包助手 API**（推荐优先申请） | Responses API，`tools:[{type:"doubao_app", feature:{ai_search:{type:"enabled"}}}]`，请求头 `ark-beta-doubao-app: true`，官方描述为"与豆包同源的企业级 API 服务"，另有 `reasoning_search`（边想边搜） | 流式输出正文、搜索关键词列表、"搜索 N 个关键词，参考 M 篇资料"摘要、资料来源标注；用量字段 `usage.tool_usage_details.doubao_app` | 按次不计 token：联网搜索 0.2 元/次，边想边搜 0.5 元/次 | beta，需联系销售或提工单开通；默认 2 QPS；不能调 temperature/top_p；仅 cn-beijing |
| **Responses API + `web_search` 工具** | 模型用 `doubao-seed-2-x-pro`，`sources:["doubao"]` 走豆包搜索 Custom 版，或默认 `search_engine` 走联网内容插件 | `web_search_call.action.queries` 给出检索词；`annotations[].url_citation` 带 `url/title/site_name/publish_time/summary` | 搜索按次约 0.02 元/次（每月 500 次免费）+ 模型 token | 随时可开通，GA；但它是"方舟模型 + 搜索"，不是豆包 App 同源 |
| 纯 chat completions（无联网） | 旧教程常见做法 | 只有正文 | 最便宜 | 没有引用和检索词，对可见性监测无意义，排除 |

## 4. 推荐方案

### 4.1 双通道、证据分级

```
豆包 主通道  provider=doubao_api  captureMode=api  evidenceClass=api_response
  └ 优先：豆包助手 API ai_search；申请未通过则 Responses API + web_search(sources=["doubao"])
  └ 进入 targets 目录、预览、每日计划、积分、resultQuality、分析管线，与 Cloro 引擎同等对待

豆包 辅通道  provider=domestic_collector  captureMode=scraped  evidenceClass=observed_surface
  └ 现有 Playwright Collector，低频抽样（例如每品牌每周若干条）
  └ 用途：校准 API 口径与真实页面差异、留存页面证据；不作为每日监控数据源
```

前端沿用现有 `captureMode`/`evidenceClass` 筛选分组，豆包回答记录标明"API 口径"或"网页口径"。

### 4.2 主通道落地要点

1. 在空壳项目 `src/Jogeoai.Providers.Domestic` 实现方舟适配器，HTTP 调用模式参考现有 `OpenAiCompatibleResearchProvider` 和 `CloroAdapter`；凭据像 Cloro 一样存库、仅系统管理员可配置，不进 `.env`。
2. 复用 `provider_tasks` 的 Submit/Poll 语义做一个 `DoubaoApiTaskWorker`（方舟是同步返回，可退化为单步执行，但保留任务表、重试和超时）。
3. 标准化结果映射到 `visibility_checks`：`answer`、`search_queries`（来自 `queries`）、`model_version`、`result_quality`，引用写 `citations` 并带 `title`/`source_domain`。
4. Target 目录新增 `doubao_api` 引擎，放开 `ManagementStore.Targets.cs:166` 和 `CollectionScheduleStore.cs:530` 对非 Cloro 的拒绝；按品牌显式启用，沿用 Gemini/Copilot 的接入模式。
5. 积分：按次定价进入 `usage_ledger` 与项目积分；方舟侧 0.2 元/次可直接换算。
6. 验收顺序：管理员配置并测试一条 → 单品牌 5 个问题手动实采 → 首个每日轮次 → 前端回答记录展示。每一步记录真实命令和结果。

### 4.3 旧方案的改进清单（若保留辅通道）

1. 先完成一次对 Jogeoai 的真实闭环：配置 `Collector:ChinaServiceToken`，重审豆包 contract，跑通 lease → collect → results → 查询 API。
2. 控制面补失败上报接口；现在失败只能等租约过期，错误码丢失。
3. 结果合同补 `providerMetadata` 的约定字段（引用标题、页面模式、截图哈希），入库时写到 `visibility_checks` 的对应列。
4. 把白名单、4 条上限、同题不重复从硬编码改为服务端下发的配额；账号与 Profile 做节点注册，不复用个人账号。
5. contract 审核半自动化：probe 时对 13 个选择器做存在性自检并保存脱敏快照，减少人工复审频率，但不绕过 7 天过期的安全设计。
6. 部署到国内服务器，关闭本机代理后再做出口国家核对。

### 4.4 不推荐的方向

- 把 Playwright 迁进 .NET 进程（归档 PRD 已否决，理由仍成立）。
- 逆向豆包网页接口或 cookie 池。
- 用无联网的 chat completions 冒充豆包回答。

## 5. 待确认事项

- 是否能申请到豆包助手 API beta。能，则主通道用它；不能，先用 `web_search(sources=["doubao"])` 上线，并在前端口径说明中区分。
- 是否保留国内 Collector 作为辅通道。保留则需要国内服务器和授权账号的预算；不保留则 003 相关表只服务历史数据，不再扩展。
- 豆包按次成本如何折算成项目积分，以及是否与 Cloro 引擎同价。

## 参考资料

- 火山方舟 豆包助手 API：<https://ark.volcengine.com/region:cn-beijing/docs/doubao-assistant>，计费：<https://www.volcengine.com/docs/82379/1998171>
- 火山方舟 Web Search 工具（Responses API）：<https://docs.volcengine.com/docs/ark/web-search?lang=zh>
- 豆包搜索服务（Agent Plan）：<https://docs.volcengine.com/docs/ark/agent-plan-personal-search?lang=zh>
- Cloro 支持平台评测：<https://proxygraphy.com/cloro-dev-review>
- 国内 GEO 工具取数口径讨论：<https://www.jingdigital.com/articles/22061/>，<https://geo.timus.cn/>，<https://juejin.cn/post/7695693136153395234>
- 国内 GEO 工具盘点：<https://nb.ifeng.com/c/8x4vgULmFhM>，<https://www.53ai.com/news/zhinengyingxiao/2026090217526.html>，<https://beaninsight.net/>
- 开源逆向项目（仅作风险参考）：<https://github.com/lza6/doubao-2api>，<https://github.com/HongYan789/doubao2API>，<https://github.com/linuxhsj/WebModel>
