# 豆包回答接入说明（doubao_api）

本文记录豆包作为 API 回答 Provider 的配置入口、链路行为、错误与质量语义、积分口径、实测证据和升级路径。方案评审见 [豆包回答接入方案评审](doubao-answer-acquisition-review.md)；规划与研究记录在 `.trellis/tasks/10-20-doubao-api-provider/`。

## 1. 现状（2026-10-20）

- 代码已实现并通过定向测试 600 项（含 Cloro 回归）与解决方案构建；025 迁移已在本机 `jogeoai` 执行两次验证幂等；5260 API 与 Worker 已重启为新构建。
- 方舟 API 通道：2026-10-30 本机 `jogeoai` 的 `doubao_api` 行已写入凭据（提示 `****e418`）、模型改为 `doubao-seed-2-0-lite-260428` 并启用；同日开通联网内容插件与豆包搜索 Custom 版后，直连方舟的 `web_search(sources=["doubao"])` 调用成功。同日经 5260 API 与 Worker 完成端到端验收：管理员测试接口成功，测试租户单品牌 5 问题手动 Run 5/5 成功，见第 7 节。
- 国内 Playwright Collector 通道：2026-10-21 已在本机对 Jogeoai 完成首次真实闭环，4/4 豆包回答与引用入库，见第 10 节；同日对真实品牌优联科技完成 8/8 采集，引用带标题、回答带搜索关键词，回答记录、概览引用排行均可见，见第 11 节。
- 2026-11-08 起本机 `doubao_api` 改用方舟 Messages API（`requestMode=messages`），每条回答记录豆包搜索召回的全部来源（每次搜索 20 条，实测每题 20–58 条），不再只记正文角标引用（Responses 模式每题 1–5 条）。Responses 模式保留为回滚选项。见第 4、7、9 节。
- 其他运行库未执行 025，不能据本机推断已部署。

## 2. 身份与证据口径

| 字段 | 值 |
|---|---|
| provider / engine | `doubao_api` / `doubao`（前端显示"豆包"） |
| captureMode / evidenceClass | `api` / `api_response`（Cloro 为 `scraped` / `observed_surface`） |
| country / locale | 固定 `CN` / `zh-CN`，其他值返回 400 `TARGET_NOT_ALLOWED` |
| parser_version | `doubao-api-v1` |
| usage_ledger | `entry_type=doubao_api_call`，`reference=doubao:{providerTaskId}` |
| provider_tasks.operation | `invoke`（Cloro 为 `submit_poll`） |

分析与监控按 `provider|captureMode|evidenceClass` 分组，豆包回答与 Cloro 页面抓取口径分开展示；监控列表、详情、品牌概览、`analytics/prompts`（用户提问页单提问指标）与引用域名投影的默认 Provider 集合为 `cloro`、`doubao_api`、`deepseek_api`（2026-10-23 起）、`domestic_collector`，并默认包含 `api`/`scraped` 两种采集模式及全部证据类别；`overview`、`visibility`、`share-of-voice` 等其余旧分析入口仍默认 Cloro `scraped/observed_surface`。

## 3. 配置（仅系统管理员）

| 接口 | 说明 |
|---|---|
| `GET /api/v1/providers/doubao/settings` | 地址、模型、启用、超时、选项、凭据提示（后 4 位）、是否已配置；不返回凭据 |
| `PUT /api/v1/providers/doubao/settings` | `apiUrl`、`model`、`credential`（空则保留）、`enabled`、`timeoutSeconds`（5–600）、`options` |
| `POST /api/v1/providers/doubao/test` | `{ "prompt": "...", "locale": "zh-CN" }`，返回规范化回答、搜索词、引用、模型版本与用量摘要 |

默认值：`apiUrl=https://ark.cn-beijing.volces.com/api/v3/responses`，`model=doubao-seed-2-1-pro-260915`，`timeoutSeconds=180`，`options={"requestMode":"web_search","sources":["doubao"],"maxToolCalls":3,"reasoningEffort":null}`。凭据保存在 `answer_provider_settings.credential`（明文，按 010/022 部署约定），不进 `.env`、日志或响应。

请求模式 `options.requestMode`：

| 模式 | `apiUrl` | 引用口径 | 可选项 |
|---|---|---|---|
| `web_search`（默认，Responses API） | `https://ark.cn-beijing.volces.com/api/v3/responses`，不得以 `/messages` 结尾 | 正文 `url_citation` 角标引用，每题约 1–5 条 | `reasoningEffort`（minimal/low/medium/high），`thinking` 必须为空 |
| `messages`（Anthropic 兼容 Messages API，2026-11-08 新增） | `https://ark.cn-beijing.volces.com/api/compatible/v1/messages`，必须以 `/messages` 结尾 | 全部检索来源 `web_search_result`（每次搜索 20 条，去重后保存，≤100） | `thinking`（`enabled`/`disabled`，省略=模型默认开启），`reasoningEffort` 必须为空；`sources` 必须为 `["doubao"]` |
| `doubao_app` | 占位 | — | 调用即返回 `DOUBAO_INVALID_REQUEST` |

模式与 `apiUrl` 不一致时保存返回 400，适配器调用前也会再校验一次（直接改库的行不一致时终态 `DOUBAO_INVALID_REQUEST`，不发请求）。两种模式共用同一把方舟 key 与豆包搜索额度。来源数量由模型决定的搜索次数决定：`maxToolCalls` 只是上限（实测 3 与 10 无差别），关闭思考时通常只搜 1 次（约 20 条）。

前置条件：火山引擎控制台已开通"应用组件库 > 豆包搜索"；账号每月 500 次免费搜索，之后按量或套餐计费，另计模型 token。

## 4. 采集链路

1. 目录 `GET /api/v1/collection-targets` 出现 `doubao_api/doubao`，可用性 = 已启用且有凭据。
2. 品牌用 `PUT /api/v1/sites/{siteId}/targets` 显式启用豆包 Target；新品牌默认仍仅 ChatGPT。启用后进入未来每日轮次（`monitoring_enabled_at` 语义同 Gemini）。
3. 手动 Run 或每日时隙创建 `run_engines`、`visibility_checks`、`provider_tasks`。积分通过 `ChargeMonitoringCredits` 预留和结算，单价 10，与 Cloro 同价：每日提问监控一直计费；手动 Run（`targets` / `targetIds` / `previewId` 形态）自 2026-11-27 起同价预占，余额不足返回 402 `CREDIT_INSUFFICIENT` 且不创建运行（Trellis `11-27-sec-manual-run-charging`，代码已实现，本机 API / Worker 尚未用该构建重启；规则见[品牌监控配置说明](brand-monitoring-config.md) §4、§9）。在此之前手动 Run 不计费（2026-10-30 实测核对），§7 记录的手动 Run 属于这一时期；§10 起的国内 Collector 通道 Run 用的是 `platforms` 形态，现在仍不预留积分。方舟未配置时手动 Run 返回 503 `DOUBAO_NOT_CONFIGURED`；时隙以 `TARGET_UNAVAILABLE` 跳过且不预留积分；Cloro key 池耗尽只影响含 Cloro Target 的选择。
4. `DoubaoApiTaskWorker`（配置节 `Doubao:Worker`，默认启用、批 2、并发 2、租约 300 秒）领取 `provider=doubao_api` 的任务，调用方舟一次，成功后在一个事务内写入结果列、引用、`usage_ledger`、积分捕获、任务成功、Run 聚合与分析工作项。
5. 请求体：`model`、`input=[{role:user,content:[{type:input_text,text}]}]`、`tools=[{type:web_search,sources:["doubao"]}]`、`max_tool_calls`，可选 `reasoning.effort`。响应映射：`message/output_text` → `answer`；`web_search_call.action.queries` → `search_queries`（无搜索调用时 `queryAvailability=empty`）；`annotations[].url_citation` → `citations`（含标题、域名、位置，复用 Cloro 的公开 URL 规则）；`model` → `model_version`；原始响应剥离 `encrypted_content` 后受控保存。`parser_version=doubao-api-v1`。
6. `messages` 模式请求：`x-api-key` + `anthropic-version: 2023-06-01`，体为 `model`、`max_tokens=16000`、`messages=[{role:user,content}]`、`tools=[{type:web_search_20250305,name:web_search,max_uses}]`，可选 `thinking`；不发送 `sources`（后端固定为豆包搜索 Custom 版）。响应映射与 DeepSeek 相同：最后一个工具块之后的 `text` → `answer`（只统一空白）；`server_tool_use.input.query` → `search_queries`；`web_search_tool_result.content[].web_search_result` 按规范化 URL 去重 → `citations`（标题、域名、位置）；`web_search_tool_result_error` 跳过；`stop_reason=max_tokens` 终态 `DOUBAO_INVALID_RESPONSE`，`pause_turn` 可重试 `DOUBAO_REQUEST_FAILED`；原始响应剥离 `encrypted_content`、`signature` 与思考文本。错误体与 Responses 同为方舟 `error.code`，错误码映射不变。成功写入 `parser_version=doubao-api-messages-v1`，用于区分新旧口径。管理员测试接口的用量摘要读 `usage.input_tokens/output_tokens` 与 `server_tool_use.web_search_requests`。正文没有角标，无法区分正文实际使用了哪几条来源。

## 5. 错误码与重试

| 错误码 | 触发 | 处理 |
|---|---|---|
| `DOUBAO_NOT_CONFIGURED` | 设置未启用或无凭据 | 终态，释放积分 |
| `DOUBAO_AUTH_FAILED` | 401 / 账号状态异常 / 未知 403 | 终态 |
| `DOUBAO_SERVICE_NOT_OPEN` | 403 AccessDenied（豆包搜索未开通）、404 模型未开通或不存在 | 终态 |
| `DOUBAO_QUOTA_EXCEEDED` | 403 欠费、429 QuotaExceeded* / 日配额 / 自设上限 | 延后 10 分钟重试，最长 2 小时后终态并释放积分 |
| `DOUBAO_RATE_LIMITED` | 其他 429、RequestBurstTooFast、ServerOverloaded | 退避 30/60/120 秒，最多 5 次 |
| `DOUBAO_INPUT_FILTERED` | 400 输入内容审核 | 终态 |
| `DOUBAO_INVALID_REQUEST` | 其他 400，或 `requestMode=doubao_app` 占位 | 终态 |
| `DOUBAO_REQUEST_FAILED` / `DOUBAO_TIMEOUT` | 408/499/5xx/网络/超时 | 重试最多 3 次 |
| `DOUBAO_INVALID_RESPONSE` | 响应形状异常 | 终态 |

输出内容审核（400 Output*）记为成功 Check、无正文、`resultQuality.answerAbsence=safety_filtered`；正文为空的正常响应记为 `empty_answer`。同步调用没有"已提交结果未知"状态，租约过期的任务回到 `pending`。

## 6. 前端

回答记录页按引擎展示，`doubao` 已映射为"豆包"并有图标；`empty_answer`、`safety_filtered` 有对应质量说明。`Jogeoai.web/src/types/generated/api.ts` 已于 2026-10-20 从运行中的 API 重新生成，定向测试 30 项与 `npm run build` 通过。新前端的目标启用入口在 团队 → 品牌管理 → 监控 → 单独配置（目标弹窗按 Provider 分组，「国内平台」含豆包采集器）；也可直接调用 `PUT /sites/{siteId}/targets`（整组替换，须带全部现有目标与 `expectedVersion`）。回答记录的引擎页签来自品牌已启用目标，`doubao_api` 与 `domestic_collector` 同为「豆包」页签，选中后不区分 Provider。2026-10-21 因采集器结果接口新增字段再次重新生成，`npm run build` 通过。

## 7. 实测记录

验收脚本：[tests/doubao_api_smoke.py](../tests/doubao_api_smoke.py)（环境变量 `SMOKE_API_BASE`、`SMOKE_TENANT_ID`、`SMOKE_SITE_ID`、`SMOKE_TOKEN`；不打印正文和凭据）。

- AC1 管理员测试接口：待实测（用户 2026-10-21 决定先跑通本地 Collector，方舟 API 暂不配置）。
- 2026-10-30 凭据与模型直连核对（未经 API，直接请求方舟端点，不入库）：不带工具的请求 HTTP 200、`status=completed`，凭据与 `doubao-seed-2-0-lite-260428` 可用；按适配器请求体带 `web_search(sources=["doubao"])` 返回 403 `AccessDenied`，带默认 `web_search` 返回 404 `ToolNotOpen`（账号未开通联网搜索）。适配器会把两者都映射为终态 `DOUBAO_SERVICE_NOT_OPEN`。需在火山引擎控制台开通联网搜索 / 豆包搜索后再测 AC1–AC4。当前没有品牌启用 `doubao_api` 目标，启用设置行不会触发自动采集。
- 2026-10-30 用户表示已在控制台开通联网搜索后复测（含 60 秒后再测一次）：结果不变，`sources=["doubao"]` 仍 403 `AccessDenied`，默认 `web_search` 仍 404 `ToolNotOpen`；方舟侧仍判定该 key 所属账号未开通。
- 2026-10-30 用户确认联网搜索与豆包搜索均开通在该 key 所属账号后第三次复测：仍为 403 `AccessDenied` / 404 `ToolNotOpen`（请求均已到达方舟并返回 request id，非本机代理问题）。原默认模型 `doubao-seed-2-1-pro-260915` 对该账号返回 404 `ModelNotOpen`，因此保留 lite 模型。下一步需凭 request id 向火山引擎工单核对开通状态。
- 2026-10-30 火山工单确认开通后第四次复测（直连方舟，lite 模型）：默认 `web_search`、`sources=["search_engine"]`、`sources=["toutiao"]` 均 HTTP 200 `completed`，有搜索词、正文和 2–5 条引用（`tool_usage_details` 分别计入 `search_engine` / `toutiao`）；适配器实际发送的 `sources=["doubao"]` 仍 403 `AccessDenied`。即联网搜索已生效、豆包搜索源仍未授权。设置行 `options.sources` 仍为 `["doubao"]`，未改。
- 2026-10-30 用户在方舟控制台「应用组件库 > 豆包搜索」开通并启用豆包搜索 Custom 版（每月免费 500 次）后第五次复测：按适配器请求体（lite 模型、`sources=["doubao"]`、`max_tool_calls=3`）HTTP 200 `completed`，2 个搜索词、正文 987 字、2 条去重引用，`tool_usage_details.web_search.doubao=2`，总 token 9548。结论：`doubao` 源需单独开通「豆包搜索」组件，「联网内容插件」只覆盖默认 / `search_engine` / `toutiao`。仍待经管理员测试接口与单品牌 Run 做 AC1–AC4（同日已完成，见下条）。
- 2026-10-30 端到端验收（5260 API 进程 27318、Worker 进程 47279；临时 2 小时管理员会话用后已删除）：
  - AC1 `POST /api/v1/providers/doubao/test`：HTTP 200、约 25 秒，`modelVersion=doubao-seed-2-0-lite-260428`，2 个搜索词、正文约 1000 字、2 条带标题引用、引用完整度 `complete`，用量 `doubaoSearchCalls=2`、总 token 9361。正文含 `<sup>[n]</sup>` 角标与文末来源列表，前端展示效果未核对。
  - AC2 目录：`doubao_api/doubao` 为 `available`。
  - AC3 测试租户 `acc024-one.example.test`（site `b48e6525-…`）：`tests/doubao_api_smoke.py` 经开发环境 `X-Tenant-Id` 兼容桥（无会话）新增启用 `doubao_api` Target（配置版本 1 → 2，targetId `8fd79679-…`），手动 Run `f92f7f1a-1c40-424d-8a0e-de7b62e0684e` 5/5 成功；每条 1–2 个搜索词、正文 493–675 字、1–3 条引用全部带标题与域名、完整度 `complete`，单条 24–33 秒。
  - AC4 数据库：`provider_tasks` 5 条 `succeeded`（各 1 次尝试）、`citations` 8 条、`usage_ledger` 5 条 `doubao_api_call`（`cost_status=unknown`）、`analysis_check_work` 5 条 `completed`；手动 Run 不计费，`credit_accounts` 未变，无积分预留。
  - 测试站点保留启用的 `doubao_api` Target；该站点无采集计划，不会自动采集。2026-10-30 已给优联启用（见下条）；未配置状态 503 与时隙跳过未复测。
- 2026-10-30 按用户确认给优联科技（site `ded1a716-…`）启用 `doubao_api/doubao/CN/zh-CN` Target：经 `tests/doubao_api_smoke.py`（`SMOKE_SKIP_RUN=1`，开发环境 `X-Tenant-Id` 兼容桥）整组写回，配置版本 18 → 19，原 15 个 Target 不变，targetId `6cc2c4d0-…`，`monitoring_enabled_at=2026-10-30 09:40:24Z`。自动监控计划 `jogeoai:auto-prompt-monitoring:v1` 版本 16 → 17，选择 10 个提问 × 9 个 Target（新增豆包方舟），首次纳入时隙为 2026-10-31 09:00（Asia/Shanghai），不补跑当日。估算：每日 90 次检测、900 积分（+100/日）；豆包搜索每日约 10–20 次（每次最多 3 次，上限 30），月 300–600 次，接近或超过每月 500 次免费额度，超额需在控制台开通按量付费，否则超额后该 Target 的 Check 失败且不扣积分；模型 token 约 10 万/日。首轮结果待次日核对。
- 2026-11-08 全部来源实测（Trellis `11-08-doubao-api-full-sources`，研究记录 `research/ark-messages-sources-test.md`）：同一问题、lite 模型直连方舟，Responses 模式不论 `max_tool_calls`（3/10）、`reasoning.effort`（minimal 只搜 1 次、high）、要求逐条标注来源的系统提示、pro 模型或流式，去重来源都只有 1–5 条，`web_search_call` 与流式事件不含结果；Chat API 1 条；Messages API 每次搜索固定返回 20 条，每题 37–39 条去重来源。本次实测约用 35 次豆包搜索额度。
- 2026-11-08 Messages 模式上线（本机）：定向测试 261/261 通过（其中新增 39 项），`dotnet build Jogeoai.slnx` 0 错误；5260 API 与完整 Worker 以新构建重启（Claude 终端页签 c1/c2），设置行改为 Messages 端点与 `{"requestMode":"messages","sources":["doubao"],"maxToolCalls":3}`（凭据与模型未变）。Worker 随即消费优联当日计划 Run `ab4adb61-ac6c-4f14-bfef-d18be09e149c` 积压的 10 条豆包任务：10/10 成功、各 1 次尝试、分析 10/10 `completed`；切换前已发出的 2 条为 `doubao-api-v1`、各 1 条引用；切换后 8 条为 `doubao-api-messages-v1`，每条 20–58 条引用（合计 300，299 条带标题，缺标题的 1 条为来源本身无标题），1–3 个搜索词，7 条完整度 `complete`、1 条因 1 个来源被公开 URL 规则过滤为 `partial`（`UNSAFE_SOURCE`）。
- 2026-11-08 管理员测试接口与回滚核对（3001 预览，owner 系统管理员会话，未铸造临时会话）：Messages 模式 `POST /api/v1/providers/doubao/test` HTTP 200、约 40 秒，40 条引用全部带标题与域名、完整度 `complete`、2 个搜索词，用量摘要 `webSearchCalls=doubaoSearchCalls=2`、总 token 12063、`reasoningTokens=null`。`PUT /settings` 用 web_search + `/messages` 端点、messages + Responses 端点均返回 400（中文说明准确）；改回 Responses 端点 + `web_search` 后测试接口 HTTP 200、约 30 秒、3 条引用（旧口径对照），随后改回 Messages 模式（凭据提示 `****e418` 保持）；回滚窗口内没有豆包任务被 Worker 处理。本机最终状态为 Messages 模式。
- AC2 目录可用性：未配置时 `unavailable` 已于 2026-10-20 在 5260 实查；配置后 `available` 待实测。
- AC3/AC4 单品牌 5 问题手动 Run 与数据库/查询 API/积分核对：待实测。
- 未配置状态 503 与时隙跳过：待实测。

## 8. 升级路径：豆包助手 API

与豆包 App 同源的 `doubao_app` 工具（`ai_search` 0.2 元/次、`reasoning_search` 0.5 元/次，按次不计 token，默认 2 QPS，仅 cn-beijing，需控制台开通"应用组件库 > 豆包助手 API"）。2026-10-20 beta 申请被拒。接入时：同一端点，请求头加 `ark-beta-doubao-app: true`，工具改为 `{"type":"doubao_app","feature":{"ai_search":{"type":"enabled"},...}}`，解析搜索关键词、"搜索 N 个关键词，参考 M 篇资料"摘要与资料来源；只改 `DoubaoArkAdapter` 的请求构造与 `output[]` 解析分支，并把设置 `options.requestMode` 切到 `doubao_app`。任务、Check、积分、前端契约不变。字段细节见 `.trellis/tasks/10-20-doubao-api-provider/research/ark-api-notes.md` 第 5 节。

## 9. 回滚

引用口径回滚：把设置行改回 `apiUrl=https://ark.cn-beijing.volces.com/api/v3/responses`、`options.requestMode=web_search`（经 `PUT /api/v1/providers/doubao/settings` 或直接改库），无需发布；本机切换前的原值备份在 `~/jogeoai-backups/doubao-api-settings-pre-messages-20261108.tsv`。已按新口径入库的回答不受影响。

停用设置行（`enabled=false`）或配置 `Doubao:Worker:Enabled=false` 即停止新采集；已入库数据保留。025 回滚为 `DROP TABLE answer_provider_settings`，不影响其他表。

## 10. 国内 Collector 通道首次真实闭环（2026-10-21，本机）

用户决定先用相邻 `Ai-visiblility-collector` 的 Playwright 豆包采集器把真实回答采进 Jogeoai，方舟 API 暂不配置。这是该控制面自 2026-09 实现以来第一次对 Jogeoai 的真实闭环。

**环境与数据**：测试租户 `acc024-one.example.test`（`20ae9302-…`，site `b48e6525-…`），为腾出免费版 20 条提问名额停用了 4 条旧验收提问，新建采集器白名单内的 4 个问题；Run `e5e27060-77a1-4271-9543-5e1ae40e7938`（`platforms=[doubao.consumer_web]`）。Jogeoai `.env` 新增 `Collector__ChinaServiceToken` 与 `Collector__ChinaTenantIds__0`；采集器配置文件 `~/Library/Application Support/aivc/real-smoke/.env.real-jogeoai`（0600），独立 spool 目录与密钥，`COLLECTOR_API_URL=http://127.0.0.1:5260`。

**结果**：

| 问题 | 任务状态 | 回答字符数 | 引用数 | Check |
|---|---|---|---|---|
| 下水道内窥镜制造商 | succeeded | 1150 | 17 | `1e6f5d21-1b10-4102-bd1e-690301259260` |
| 优联科技 | succeeded | 550 | 9 | `16859977-5ba0-4d98-81ae-0e6e8a802b34` |
| 管道内窥镜 | succeeded | 960 | 12 | `b24119b7-235f-4e07-9bd0-693ffa00f79f` |
| ulinktek | succeeded | 703 | 9 | `865b5ce4-6293-42af-9420-7ca51116a095` |

Run 聚合 `succeeded 4/0`；分析工作项 4 条 `completed`；`GET /api/v1/sites/{siteId}/monitoring/records` 返回 4 条 `doubao/domestic_collector` 记录（引用数一致，分析完成）；`collector_result_submissions` 另有 1 条 `stale` 审计记录（见下文时区缺陷）。引用只有 URL 与域名，没有标题（采集器合同只传 URL）；没有搜索词。

**本次暴露并修复的 Jogeoai 缺陷**（均在 `src/Jogeoai.Api/Controllers/CollectorController.cs`、`src/Jogeoai.Infrastructure/DomesticCollectorStore.cs`、`src/Jogeoai.Domain/DomesticCollectorModels.cs`）：
1. 租约/心跳响应的 `leaseExpiresAt` 以 `+00:00` 偏移输出，采集器 zod 合同要求 `Z` 结尾 → 改为 UTC `Z` 字符串。
2. 结果提交校验用 `DateTime.Parse(lease_until).ToUniversalTime()` 把不带时区的 UTC 值按本机 +08:00 再减 8 小时，所有有效租约被判为过期（`stale`）→ 改为 `SpecifyKind(Utc)`。
3. `CollectorResultDisposition` 序列化为 `Accepted/Stale`（首字母大写），采集器只接受小写 → 加 `JsonStringEnumMemberName`。
4. 国内结果入库不写 `capture_mode`/`evidence_class`/`answer_present`，监控列表按这两列筛选导致回答记录页看不到国内回答 → 入库时写 `scraped`/`observed_surface`/`TRUE`，并回填本机 4 条历史行。

**采集器侧改动**（`Ai-visiblility-collector`，未提交）：豆包 page contract 现场复审并更新 7 个失效选择器（登录标记、输入框、发送按钮、助手消息、正文、流式/完成状态均改为 `data-testid`），`challengeRequired` 增加图片验证弹窗文案；`DoubaoAdapter` 改为多正文块等待并新增 `readDoubaoSources` 来源读取：豆包展开"参考 N 篇资料"后最多内联渲染 12 条链接，其余不在 DOM 且无展开控件，因此允许少于声明数的部分来源，仍拒绝多于声明数、标题不可解析、无链接；37 项适配器测试、typecheck、lint 通过。

**运行约束与观察**：
- 首次自动发送触发了豆包图片验证，由用户在采集器 Profile 的浏览器窗口手动完成后才可继续；采集器不解验证码。
- 采集器 real 模式每个任务只能尝试一次（Profile 账本），失败任务需 `COLLECTOR_RETRY_TASK_ID` 配合 `COLLECTOR_MAX_TASKS_PER_RUN=1` 逐个重试，本次 4 个任务共跑了 4 次重试运行。
- 登录态下回答带个性化痕迹（如"你之前关注管道检测设备"），且本机经 Clash 出口时来源以英文站点为主；消费端口径受账号历史与出口地区影响，与方舟 API 口径需分开展示。
- 可直接回答的后续改进：采集器把引用标题与"搜索 N 个关键词"的关键词一并上报（需同时改采集器合同与 Jogeoai 结果接口）。

## 11. 优联科技真实采集（2026-10-21，本机）

用户决定把真实品牌优联科技（租户 `38685331-…`，站点 `ded1a716-…`）的全部 8 个启用提问经本机豆包采集器采集，并补齐引用标题与搜索关键词。

**改动**：
- 采集器（`Ai-visiblility-collector`，未提交）：real 模式问题白名单由硬编码 4 题改为可选的运营批准清单 `COLLECTOR_APPROVED_QUESTIONS_FILE`（JSON 五键 `version/tenantId/projectId/approvedAt/questions`，0600、属主本人、非符号链接，绑定租户与站点，启动时、领取任务前校验；未配置时沿用内置 4 题）；`COLLECTOR_MAX_TASKS_PER_RUN` 上限 4 → 10，并在领取前检查尝试账本余量；豆包适配器读取来源标题（链接文本去掉编号）与「搜索 N 个关键词」下方的关键词（page contract 新增 `searchQueries` 选择器，数量须与 N 一致，否则省略）；结果合同新增可选 `citationTitles`、`searchQueries`，旧 spool 载荷仍可重放。
- Jogeoai：`/internal/collector/results` 接收上述两个可选字段，标题写入 `citations.title`，关键词写入 `search_queries` 并置 `query_availability=available`（空数组为 `empty`，缺省保持 `unknown`）；幂等哈希不变。`.env` 新增 `Collector__ChinaTenantIds__1`（优联租户）。
- 数据：优联目标配置版本 10 → 11，新增启用 `domestic_collector/doubao/consumer_web/CN/zh-CN`，原 4 个 Cloro 目标不变；采集器侧新建 `approved-questions-ulinktek.json`、`.env.real-jogeoai-ulinktek` 与空 spool `spool-doubao-ulinktek`（均在 aivc 目录），复用 `doubao-cn` 登录 Profile。

**结果**：Run `ddc04f7e-14da-47dd-af10-7e2bca91888a`（手动创建，国内 Run 不预留积分），8 个任务一次跑完（约 8 分钟，无验证码、无重试），Run `succeeded 8/0`。

| 指标 | 值 |
|---|---|
| 回答字符数 | 1242–2451 |
| 每题引用数 / 带标题 | 14–32，共 181 条，全部带标题 |
| 搜索关键词 | 每题 4–8 个，8 条均 `query_availability=available` |
| 分析 | 8 条 `analysis_check_work` 均 `completed`；优联被提及 7/8，排名 1–4 |
| 引用域名 | 46 个，`iesdouyin.com`（抖音）105 次居首，其次 `zy-cam.com` 8 次 |

- 回答记录：出现「豆包」页签，列出 8 条；详情显示「搜索 N 个关键词，参考 M 篇资料」、关键词标签与带标题的参考资料（3001 真实 API 预览截图核对）。
- 概览：模型范围选「豆包」时品牌提及率 87.50%、平均排名 1.86、曝光效果分数 53；引用域名与引用文章排行榜计入豆包引用，文章榜显示来源标题，第 2、3 名为优联自己的抖音视频（分别被 6、3 条回答引用）。
- 文章被引用追踪：被引用发现水位已推进到覆盖这 8 条回答（arrival 963）；豆包引用中没有 `szulink.com.cn` 链接，与优联 21 条已发布链接无命中，因此没有新增被引用文章。优联的抖音视频被频繁引用，但不在文章追踪范围内。

**运行观察**：
- 采集前确认 `www.doubao.com` 连接在 Clash 中命中 `GeoSite cn` → DIRECT（fake-ip 解析不代表走代理，需看连接规则）；本次来源以中文站点为主。
- 采集账号名为「优联科技管道检测知识库」，账号历史可能偏向本品牌，提及率与排名需结合这一点解读。
- 账号「最近」列表中出现与优联提问相似的非本流程对话，说明该账号有人在用；采集期间应暂停人工使用。
- 尝试账本仍是一题一次：同一绑定再次采集这 8 题需 `COLLECTOR_RETRY_TASK_ID` 逐题重试或新建绑定（新 spool/新清单）。
- 未验证：Jogeoai 结果写入的隔离 SQL 测试（缺隔离库，已由本次真实入库覆盖同一路径）；失败任务在回答记录中仍不可见。
- 用户提问页单提问指标原先只统计 Cloro（`analytics/prompts` 沿用旧默认）。2026-10-21 已改为默认全部来源；接口仍按「提问版本 × 来源」逐行返回，前端按分子分母合并同一提问。用当前构建在临时 API 5281 加 3012 预览核对：8 个提问共 16 行（Cloro 19/143、豆包 7/8），页面上的单提问与分组提及率与合并值一致。5260 API 重启为该构建后才生效。

### 11.1 回答格式化、回填与官方图标（2026-10-21 追加）

- **问题**：采集器原先用 `innerText` 取正文，入库为只有换行的纯文本；前端用 `marked`（GFM、`breaks:false`）渲染回答，单个换行被合并，豆包回答显示为一整段，且正文混入「wopson.com…」「Alibaba」等来源角标。Cloro 回答本就是 Markdown，不受影响。
- **采集器**：新增 `adapters/answer-markdown.ts`，在页面内把 `message_text_content` 的 HTML 转为 Markdown（标题、加粗/斜体、嵌套有序/无序列表、引用块、GFM 表格、代码、链接、段内换行），丢弃图片轮播、视频卡片与来源角标（page contract 新增 `answerIgnore`，现值 `[data-testid="carousel-item"], [data-testid="mdbox_image"], [data-testid="video-box-bg-image"], span[class*="spacing-"] + span`，复审于 2026-10-21T08:52:51Z），正文中的 `~ * _ | [ ]` 等转义，避免误渲染删除线；加粗紧邻中文括号时把标点移到 `**` 外以符合 CommonMark 闭合规则。转换失败或为空时回退纯文本，不影响采集成功。
- **回填（优联 8 条，不重新发问）**：只读打开豆包账号中的原对话，按提问原文（去空白，豆包显示时会在中英文间加空格）对应 Check；要求页面按旧读法得到的纯文本与入库正文一致后才取 Markdown。8 条中 6 条完全一致，2 条仅差一段轮播图片说明（图片懒加载时 `innerText` 才包含，入库正文多 5–12 字），按 ≤20 字单段差异放行。随后在一个事务中按旧 SHA-256 逐条替换正文，临时表 `CHECK (n = 8)` 校验全部为新内容后提交；引用、关键词、`captured_at`、`result_hash`、`collector_result_submissions` 未改（后者为回滚来源）。
- **重新分析**：`POST /api/v1/sites/{siteId}/analysis-jobs`（窗口 `07:21:41.505Z–07:28:43.522Z` + 8 个提问 ID），`requested_scope_with_reuse`，复用 230、重算 8，release v5（`38a68d14-…`）发布；8 条均有提及证据，提及偏移与新正文逐字一致。被移除的来源角标/图片说明中含竞品名（如「武汉中仪物…」），故部分竞品提及次数减 1–2；「市政和工业管道检测需要采购推杆式内窥镜…」一条优联排名由 2 变 1，其余提及状态不变（7/8 提及）。
- **前端**：回答详情按标题、加粗、列表、引用块分层显示，无删除线、无角标残留（3001 预览核对）；豆包标识改为豆包官方图标（doubao.com 128×128 favicon，打包为 `Jogeoai.web/src/assets/model-logos/doubao.png`，圆形满铺），替换原 `@lobehub` 抽象标识。
- **未处理**：测试租户 4 条旧回答（对话重名无法可靠对应）仍为纯文本；列表 `answerSummary` 为原文截断，含 Markdown 符号（新前端不显示该字段）。

## 12. 优联科技 10 题单轮采集与调度核对（2026-10-22，本机）

用户要求对该品牌当前启用的 10 个提问采集一次。沿用豆包网页采集器与既有登录 Profile，新建本轮 10 题的批准清单（不覆盖原 8 题清单），通过 `POST /api/v1/visibility-runs` 的 `platforms=[doubao.consumer_web]` 创建 Run `d6877fc1-b70d-4763-8650-78d8a67c7c74`。保留既有尝试账本，逐任务指定 `COLLECTOR_RETRY_TASK_ID`、`COLLECTOR_MAX_TASKS_PER_RUN=1` 和 `COLLECTOR_RUN_ONCE=true` 执行，每题本轮仅采集一次。

**结果**：北京时间 09:46:26 创建、09:56:54 完成；Run 为 `succeeded`，10 成功、0 失败，覆盖 10 个不同提问。回答长 1455–2461 字符，236 条引用全部带标题，10 条回答均保存搜索关键词；10 条 `analysis_check_work` 全为 `completed`。本轮未遇登录或验证码阻塞，采集器已退出，加密 spool 无待提交文件，API 与完整 Worker 继续运行。

**实际验证**：

- `node --env-file=<既有优联采集配置> --import tsx apps/collector/src/login.ts probe` 返回 `ready`；正式采集使用本机 Node 24.22.0 执行 `apps/collector/src/main.ts`，10 个进程均退出 0、各提交 1 条 `accepted`。
- MySQL 只读核对本轮 `visibility_runs`、`visibility_checks`、`citations`、`analysis_check_work`，得到上述数量与状态。
- `GET /api/v1/visibility-runs/{runId}/checks` 与按本轮时间窗、`provider=domestic_collector&engine=doubao` 查询的 `GET /api/v1/sites/{siteId}/monitoring/records` 均返回 10 条，Check ID 集合一致；后者 `totalCount=10`，全部 `succeeded/completed`，引用合计 236。
- 本轮为运行操作与现状记录，未修改业务代码，未运行代码测试。

**调度现状**：该品牌现有每日计划在 `Asia/Shanghai` 的 09:00 执行，仅包含 Cloro 的 Google AI Overview、Copilot、ChatGPT、Gemini；没有豆包网页目标。源码 `PromptMonitoringService.SelectMonitoringTargets` 和 `CollectionScheduleStore.Normalize` 只接纳已注册的 API 回答 Provider（`cloro`、`doubao_api`），网页通道 `domestic_collector` 仍需手动创建 Run 并启动外部采集器。方舟 `doubao_api` 虽支持每日计划，但本机设置仍为禁用且无凭据；本轮没有启用它，也没有新增自动调度。

## 13. 优联科技单轮豆包采集（2026-10-25，本机）

用户要求直接采集，不创建 Trellis 任务。核对品牌 `ded1a716-f791-491c-96e2-f737bb998960` 当前 10 个启用提问与既有批准清单一致、没有未完成豆包任务后，通过 `POST /api/v1/visibility-runs`（`platforms=[doubao.consumer_web]`）创建 Run `9112f588-14f4-4a04-bf33-a0b3f4e3c69b`。复用豆包专用 Profile、原尝试账本及加密 spool；每题逐个进程执行，指定 `COLLECTOR_RETRY_TASK_ID`、`COLLECTOR_MAX_TASKS_PER_RUN=1`、`COLLECTOR_RUN_ONCE=true`，间隔 30 秒。使用当前采集器的独立查询前缀，每个原始提问本轮仅发送一次。

**已验证结果**（北京时间 09:43:48 创建，09:59:29 完成，09:59:59 采集驱动退出）：

- Run 为 `succeeded`，10 成功、0 失败，覆盖 10 个不同提问；全部任务 `attempt_count=1`，提交审计为 10 条 `accepted`。
- 358 条引用全部带标题；10 条回答均保留 Markdown、搜索词，逐条详情的分析为 `completed`，`analysis_check_work` 同样为 10 条 `completed`。
- 按本轮时间窗及 `provider=domestic_collector&engine=doubao` 查询回答记录，`totalCount=10`，Check ID 集合与 Run 的 10 条一致。
- 专用 Profile 账本中，本轮恰好 10 条记录、10 个不同任务，每题一次。Profile/spool 锁已释放，spool 仅保留 `.binding`，无待提交密文；API readiness 为 `Healthy`，完整 Worker 进程仍在运行。

**第 3 题原对话恢复**：采集器在页面检查阶段以 `page_contract_changed` 停止，未提交结果。只读打开原对话后，确认单条用户消息与本轮完整发送文本一致（忽略页面展示空白）、单条助手回答已完成且无流式状态，无登录/验证码/限流阻断。正文与 Markdown 各做 SHA-256 校验和稳定性复读，再沿用现有 `readDoubaoSources` 得到 38 条带标题引用、7 个搜索词；续期并使用仍有效的原租约版本 1，经既有加密 spool 与结果接口恢复，返回 `accepted`。没有再次发送问题，没有重新领取任务，原尝试账本逐字未变。已确认原回答可恢复；本次未确定最初瞬时页面检查失败的具体原因，也未修改采集器规则。

**实际执行的验证命令与入口**：

- `/opt/homebrew/opt/node@24/bin/node --env-file=<既有豆包专用配置> --import tsx apps/collector/src/login.ts probe`：`ready`。
- `python3 <aivc>/round-driver-20261024.py 20261025-1792892628240 doubao`：首次在第 3 题停止；原回答恢复后继续同一轮，最终退出 0，状态文件记录 10 条接受结果。
- `/opt/homebrew/opt/node@24/bin/node --env-file=<本轮豆包专用配置> --import tsx /private/tmp/recover-doubao-20261025.mjs`：原租约恢复 `accepted`，`noPromptSent=true`、`ledgerUnchanged=true`。安全恢复证据保存在 aivc 目录的 `doubao-recovery-evidence-20261025-a97dece7.json`，不包含正文或凭据。
- `python3 /private/tmp/verify-doubao-20261025.py`：调用 Run、Checks、逐条回答详情、回答记录接口，并对任务/分析/提交审计执行只读 SQL，结果见 [安全验证快照](doubao-collection-20261025.json)。
- `curl -fsS http://127.0.0.1:5260/health/ready` 返回 `Healthy`；独立进程检查确认原 Worker 仍运行，采集进程已退出。

**接口计数差异**：国内兼容 Run 概要本次仍返回 `analysisStatus=not_started`、`analyzedChecks=0`，与逐条详情及 SQL 的 10 条 `completed` 不一致。上述分析完成结论依据逐条详情与分析工作项；本轮未修复该概要计数。

本轮仅运行采集并同步现状文档，未修改业务代码，未执行 migration 或代码测试；保留已有回答、尝试账本及运行证据。

## 14. 优联科技单轮豆包采集（2026-10-26，本机）

用户要求直接采集，不创建 Trellis 任务。核对当前 10 个启用提问与上一轮批准清单一致、豆包队列无未完成任务，专用 Profile/spool 未被占用且无待提交密文，页面规则仍在七天有效期内；`probe` 返回 `ready` 后创建 Run `b0203785-969b-4eb1-8c96-9b749576630b`。复用既有专用会话、尝试账本和加密 spool，按当前独立查询前缀逐题执行，每个正常任务后间隔 30 秒。

**已验证结果**（北京时间 08:52:20 创建，09:26:52 全部入库，09:27:23 驱动退出）：

- Run 为 `succeeded`，10 成功、0 失败，覆盖 10 个不同提问；提交审计为 10 条 `accepted`。
- 332 条引用全部带标题；10 条回答均保留 Markdown 和搜索词，逐条详情的分析及 `analysis_check_work` 均为 10 条 `completed`。
- 本轮时间窗、`provider=domestic_collector&engine=doubao` 的回答记录 `totalCount=10`，Check ID 集合与本轮完全一致。
- Profile/spool 锁均已释放，spool 仅剩 `.binding`；采集驱动与登录/采集进程均已退出。API readiness 为 `Healthy`，完整 Worker 进程独立确认仍在运行。

**人工验证与恢复**：第 1、3 个任务分别在发送前遇到 `challenge_required`，均停下并由用户在专用窗口手动完成。按停止时段只读核对浏览器历史，原尝试仅产生 `local_` 草稿地址；再次只读打开原地址，均未发现用户消息或助手回答。第 1 个任务正常重新领取后完成。第 3 个任务等待租约到期期间，驱动继续采集其他未发送任务；其后队列在探测与领取之间变化，采集器因任务 ID 不符以 `task_not_approved` 停止，未调用该任务的收集方法。随后只读取回采集器已经持有的第 3 个任务版本 2 有效租约，沿用既有任务批准、页面探测、心跳、尝试登记、适配器及加密提交方法接续，成功接受，没有额外领取租约。两项任务最终 `attempt_count=2`，其余八项为 1；浏览器账本共 12 笔，包含两笔发送前停止登记，不能把登记数写成实际发问次数。账本及原始审计均保留。

**Worker 运行态**：本轮首条入库后发现没有 Worker 进程，分析工作项为 `pending`。从 `.env` 加载既有配置，以 `dotnet run --project src/Jogeoai.Worker --no-build --launch-profile Jogeoai.Worker` 恢复完整模式 Worker（启动进程 17734、应用进程 17738），随后本轮全部分析完成。该进程同时恢复既有后台任务的消费；本轮没有修改后台调度配置。

**实际执行的验证命令与入口**：

- `python3 /private/tmp/aivc/doubao-20261026/prepare.py`：只读核对启用提问、未完成豆包任务、规则日期及 spool，输出 `preflight_ready`、10 题、无旧任务/待提交密文。
- `/opt/homebrew/opt/node@24/bin/node --env-file=<本轮豆包专用配置> --import tsx apps/collector/src/login.ts probe`：`ready`。
- `python3 /private/tmp/aivc/doubao-20261026/run.py`：复用既有逐题驱动；人工验证后为已确认未发送的任务传入恢复 ID。队列变化停止后，`resume-active-lease.mjs` 接续现有租约；最终驱动退出 0、接受结果 10 条。
- `python3 /private/tmp/aivc/doubao-20261026/verify.py`：调用 Run、Checks、逐条详情与回答记录接口，并只读核对任务、分析、提交审计及尝试账本；结果见 [验证快照](doubao-collection-20261026.json)。
- `curl -fsS http://127.0.0.1:5260/health/ready`：`Healthy`；`ps -axo pid,ppid,comm` 确认 API/Worker 存活，`pgrep -fl` 检查本轮采集/登录进程无匹配。

国内兼容 Run 概要仍返回 `analysisStatus=not_started`、`analyzedChecks=0`；分析完成结论依据逐条详情及工作项。本轮仅执行采集、恢复既有 Worker 并同步现状文档，未修改业务代码、执行 migration 或运行代码测试。

## 15. 优联科技当日豆包采集与旧任务归档（2026-10-28，本机）

用户要求直接采集今天的回答，并明确长期偏好：采集任务不创建 Trellis 任务；该偏好已写入根目录 `AGENTS.md`。执行前，按用户“之前的都归档”授权，将 2026-10-27 轮次 `87e1e491-e34e-47b9-9b82-5fccb643be04` 唯一未完成任务 `8945d0f2-8ea0-4f8b-afcc-27e0ff71878d` 从 `retryable_failed` 置为 `terminal_failed`，原因 `operator_archived`，并追加失败审计。该旧轮次原为 `partial_failed`（9 成功、1 失败）；已成功回答和既有审计均保留。确认锁持有进程及关联浏览器均已退出后，把专用 Profile 的遗留 `.collector.lock` 重命名为 `.collector.lock.archived-30597-20261028`，保留可恢复副本。操作前快照保存在本机受限目录 `/private/tmp/aivc/doubao-20261028/archive-before.json`。

核对当前 10 个启用提问与既有批准清单完全一致、豆包队列无旧待领取任务、spool 无待提交密文，登录与页面探测返回 `ready` 后，创建今日 Run `af5bf609-0de8-4ff2-a6ce-bfd4325b7208`（`platforms=[doubao.consumer_web]`）。复用原专用 Profile、尝试账本及加密 spool，沿用当前独立查询前缀、每任务单独进程及 30 秒间隔。

**已验证结果**（台北时间 10:50:02 创建，11:04:15 全部入库，11:04:46 采集驱动退出）：

- Run 为 `succeeded`，10 成功、0 失败；10 个不同提问各有一条成功 Check，提交审计恰为 10 条 `accepted`。
- 共 368 条引用，全部带标题；10 条回答均保留 Markdown 和搜索词，逐条详情的分析及 `analysis_check_work` 均为 10 条 `completed`。
- 按本轮时间窗及 `provider=domestic_collector&engine=doubao` 查询回答记录，`totalCount=10`，Check ID 集合与本轮一致。
- 专用 Profile 与 spool 锁已释放；spool 仅剩 `.binding`。采集/登录进程已退出，API readiness 为 `Healthy`，进程检查独立确认 API 与完整 Worker 仍在运行。

**人工验证与接续**：第 5 题遇到 `challenge_required` 后暂停，用户在同一专用浏览器手动完成验证。只读检查停止时段的浏览器历史，只有主页和 `local_` 草稿地址；重新打开原草稿后回到空白对话，用户消息与助手回答均为 0，已登录且没有验证码/生成状态。先继续其余未发送任务，原任务租约到期并正常回到队列后再采集第 5 题，最终成功接受。该任务 `attempt_count=2`，其余 9 题均为 1；浏览器尝试账本共 11 笔、10 个不同任务及问题，含一次停止登记，不能把账本笔数写成实际发问次数。原账本及全部审计保留。

**实际验证命令与入口**：

- `node /private/tmp/aivc/doubao-20261028/round.mjs inspect`、`archive`：检查精确旧任务、锁持有进程和 spool，完成获授权的终态归档及失效锁副本保留。
- `/opt/homebrew/opt/node@24/bin/node --env-file=<既有豆包专用配置> --import tsx apps/collector/src/login.ts probe`：`ready`；遇验证码时通过同一脚本的 `login` 子命令打开窗口供用户手动验证。
- `node --env-file=<既有豆包专用配置> --import tsx /private/tmp/aivc/doubao-20261028/inspect-stopped.mjs`：原草稿只读核对，未执行发问。
- `node /private/tmp/aivc/doubao-20261028/round.mjs run`：首次在第 5 题暂停；人工验证与原草稿核对后，给同一驱动传入该精确任务 ID 接续，最终退出 0、接受 10 条。
- `node /private/tmp/aivc/doubao-20261028/round.mjs status`：回查 Run、Checks、逐条详情、回答记录、任务/分析/提交审计 SQL 及本轮尝试账本；安全结果见 [验证快照](doubao-collection-20261028.json)。
- `curl -fsS http://127.0.0.1:5260/health/ready`：`Healthy`；`pgrep -fl '/Jogeoai.Api/bin/|/Jogeoai.Worker/bin/|collector/src/(main|login)'`：API/Worker 存活，无采集/登录进程。

国内兼容 Run 概要仍显示 `analysisStatus=not_started`、`analyzedChecks=0`；本轮分析完成结论依据逐条详情及分析工作项。仅执行采集、获授权的旧任务归档和文档/规则同步，未修改业务源码、执行 migration 或运行代码测试；未创建 Trellis 任务。

## 16. 优联科技当日豆包采集受迁移阻断（2026-10-30，本机）

按用户要求直接执行今日采集，不创建 Trellis 任务。只读核对当前 10 个启用问题与既有批准清单一致，豆包无非终态旧任务，专用 Profile/spool 空闲。既有页面合同超过七日有效期；只读打开历史对话复审登录、正文、完成标记和来源选择器后，在本轮受限临时目录生成规则副本，未覆盖既有规则。使用副本执行 login.ts probe 返回 ready。

台北时间 09:53:42 创建 Run 54f34364-4591-4e60-8497-f7743b307d27，但 /internal/collector/leases 返回 HTTP 500，采集器以 control_plane_unavailable 退出。只读数据库确认 035 的三个表及新增任务/结果列均不存在；当前源码的单账号租约路径也查询 collector_session_profiles，与迁移缺失一致。此外，2026-10-28 的一条人工归档审计为 event_kind=failed、error_code=NULL，会触发 035 的历史数据前置检查，不能直接执行该迁移。尚未修改该审计或执行 migration；迁移兼容性处理及本机 schema 部署须取得用户授权后继续。

**实际验证命令与结果**：

- node --env-file=<本轮配置> --import tsx apps/collector/src/login.ts probe：ready。
- node /private/tmp/aivc/doubao-20261030/round.mjs run：两次均在领取前停止；HTTP 诊断确认租约接口为 500。
- 只读 SQL 检查 information_schema.tables/columns 和 collector_attempts：缺失 035 表/列，历史审计不兼容行数为 1。
- node /private/tmp/aivc/doubao-20261030/round.mjs status：10 个任务全部 pending、attempt_count=0，本轮尝试账本条目为 0，无提交审计、无回答记录；Profile 无锁，spool 仅 .binding。见 [安全状态快照](doubao-collection-20261030.json)。
- 提升本地执行权限后 /health/live、/health/ready 均为 200；独立进程检查确认既有 API/完整 Worker 存活。最初受限环境无法连接本机服务，随后尝试启动的额外 API 因 5260 已被占用而退出，未替换既有服务。

本轮尚未采集到回答，保留同一 Run 等待继续，未重新创建轮次。未修改业务代码、未运行代码测试、未执行数据库迁移或删除历史数据。

**后续（10:10）**：经用户授权，备份 `jogeoai` 后在 035 的尝试记录失败码白名单中加入 `operator_archived`，把上述审计行回填为 `operator_archived`（未删除任何行），再用 root 执行 035；租约路径依赖的表和列已就绪，详情见 [账号池说明](collector-account-pool.md)。Run 54f34364-4591-4e60-8497-f7743b307d27 尚未续跑。
