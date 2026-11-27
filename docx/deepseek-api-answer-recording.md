# DeepSeek 官方 API 回答采集说明

Trellis 任务：`10-23-deepseek-api-provider`。网页采集通道见 [DeepSeek 网页采集说明](deepseek-answer-recording.md)。

## 1. 通道概览

| 项目 | 内容 |
| --- | --- |
| Provider / 引擎 | `deepseek_api` / `deepseek`，`captureMode=api`，`evidenceClass=api_response`，固定 CN / zh-CN |
| 接口 | DeepSeek Anthropic 兼容 Messages API `POST https://api.deepseek.com/anthropic/v1/messages`，服务端工具 `web_search_20250305` |
| 默认模型 | `deepseek-flash`，`thinking.type=disabled`（与网页实采的「智能搜索开、深度思考关」一致），单次回答最多搜索 5 次（`max_uses`） |
| 解析器 | `deepseek-api-v1`（`DeepSeekAnthropicAdapter`） |
| 执行 | `DeepSeekApiTaskWorker` 领取 `provider_tasks`，同步调用一次，通过 `CompleteSynchronousTaskAsync` 在一个事务里写入结果、引用、用量和分析工作项 |
| 积分 | 与 Cloro、豆包同价：每日监控按「问题 × 目标 × 样本」预留并结算 10 积分；手动 Run 自 2026-11-27 起同价预留，余额不足返回 402 `CREDIT_INSUFFICIENT`（Trellis `11-27-sec-manual-run-charging`，代码已实现，本机 API / Worker 尚未用该构建重启；见[品牌监控配置说明](brand-monitoring-config.md) §4、§9），在此之前不预留，§4 的实测记录属于这一时期 |
| 设置 | `answer_provider_settings` 中 `provider='deepseek_api'` 这一行（迁移 030 插入种子） |

## 2. 配置

- 迁移 `db/migrations/030_deepseek_answer_provider.sql` 依赖 025，只插入种子（默认未启用、无凭据），可重复执行。执行时须加 `--default-character-set=utf8mb4`。回滚：`DELETE FROM answer_provider_settings WHERE provider='deepseek_api'`。
- Key 的写入方式有两种：
  - 系统管理员调用 `PUT /api/v1/providers/deepseek/settings`；
  - 与 OpenAI Key 一样直接写库：`credential` 列明文，`credential_hint` 填 `****` 加后 4 位，同时把 `enabled` 设为 1。

  本机 2026-10-23 用的是直接写库方式（`research/store_deepseek_key.py`，Key 经 getpass 输入，SQL 走 stdin），Key 不进文档、日志或对话。
- `options`：`{"requestMode":"web_search","sources":[],"maxToolCalls":1–10,"thinking":"disabled"|"enabled"}`。如果要换成 `deepseek-v4-pro` 或开启思考，只改设置即可。
- `POST /api/v1/providers/deepseek/test`（系统管理员）：用已保存的设置提一个问题，返回回答、搜索词、来源、质量和用量，不返回 Key。
- 品牌需要单独启用目标 `deepseek_api/deepseek/api/CN/zh-CN`。启用后，自动监控计划（每日 09:01 本地时间，2026-11-09 起由 09:00 改为 09:01；「2次/天」的单元格另在 00:05 采一轮，见 [品牌监控模型配置说明](brand-monitoring-config.md)）的目标列表会自动带上 DeepSeek。同一轮里 Cloro 与 DeepSeek 的任务同时创建、并行执行，不保证先后。
- 如果目标已启用，但 Key 未配置或被禁用：手动 Run 返回 503 `DEEPSEEK_NOT_CONFIGURED`，每日时隙**整轮**以 `TARGET_UNAVAILABLE` 跳过（同轮的 Cloro 也跳过），不预留积分。这条规则与豆包相同。
- 回滚 Worker：设 `DeepSeek:Worker:Enabled=false`，或在品牌上停用目标。

## 3. 回答与引用口径

- **引用 = 检索来源**：DeepSeek 两种兼容接口都不返回行内引用（`citations: null`）。系统记录的是每次搜索返回的结果（`web_search_result` 的 URL 和标题），按出现顺序以规范化 URL 去重，最多 100 条。这和网页采集读取「搜索结果」侧栏的口径一致，但其中部分来源回答里可能并没有用到。
- **回答**：取最后一个工具块之后的 `text`，这样会去掉关思考时搜索前的那句引导语。经 `DeepSeekMarkdown.Normalize` 处理：统一换行、去掉行尾空白、合并连续空行、去掉首尾空白；代码块内不处理，也不改写任何 Markdown 语法。
- **搜索词**：`server_tool_use.input.query`，忽略空串。`query_availability` 的取值：未搜索为 `empty`，有搜索词为 `available`，有调用但没有非空搜索词为 `unknown`。
- `web_search_tool_result_error`（`max_uses_exceeded`、`invalid_tool_input`）不算失败。
- 原始响应入库前剥离 `encrypted_content`、`signature`、思考正文和 `redacted_thinking.data`。`usage`（含 `server_tool_use.web_search_requests`）写在 `provider_tasks.provider_response.usage`，不对用户展示。
- 错误码 `DEEPSEEK_*`：
  - 401/403 凭据无效：终态。
  - 402 余额不足：每 10 分钟延后一次，最长 2 小时。
  - 429/503/529 限流或过载：退避 30/60/120 秒，最多 5 次。
  - 408/5xx/网络错误/`pause_turn`：最多重试 3 次。
  - `max_tokens` 截断：终态，按无效响应处理。

  HttpClient 不跟随重定向，避免 `x-api-key` 被发到其他主机。

## 4. 实测记录（2026-10-23，本机）

- 030 已执行两次，验证幂等。5260 API 与 Worker 于 15:40 重启为新构建。
- 未配置时：目录中 `deepseek_api` 为 `unavailable`，手动 Run 返回 503 `DEEPSEEK_NOT_CONFIGURED`，均已实查。Key 写入后目录变为 `available`。
- 优联科技（租户 `38685331-…`，站点 `ded1a716-…`）启用 DeepSeek API 目标：目标版本 15 → 16，原有 10 个 targetId 不变，新目标 `1fd1326e-fe76-4e3c-adca-7ab61f04643c`。自动监控计划 `2952952d-…` 的目标列表变为 4 个 Cloro 加 `deepseek_api`，下一时隙 2026-10-24 09:00（北京时间）。
- 手动 Run `fc2227f8-35c8-4907-9bb9-093e6ade3e6d`：10 个启用提问，与 10-22 网页轮次的批准清单逐字一致，单样本。

| 项目 | 结果 |
| --- | --- |
| 时间 | 北京时间 16:07:53 创建，16:09:18 完成（约 85 秒） |
| 状态 | `succeeded`，10/10，每个任务 `attempt_count=1`，单题耗时 8.3–22.7 秒 |
| 模型 | `model_version=deepseek-flash`，`parser_version=deepseek-api-v1` |
| 回答 | 1415–4588 字符 Markdown。10 题都有标题，8 题含表格，没有引导语残留，规范化检查 0 异常 |
| 搜索 | 9 题搜索了 2–6 次（`available`）。「下水道反复堵塞…」一题模型未搜索：`query_availability=empty`、0 条来源、引用 `availability=not_exposed` |
| 来源 | 共 264 条，261 条带标题 |
| 用量 | 10 条 `usage_ledger`（`deepseek_api_call`）；input 合计 258,394 tokens，output 16,516 tokens |
| 分析 | 10 条 `analysis_check_work` 全部 `completed` |
| 一致性 | `GET /visibility-runs/{runId}/checks` 与 MySQL 逐条比对正文 SHA-256、搜索词、来源 URL 顺序：10/10 一致；`monitoring/records?provider=deepseek_api` 读回 10 条 |
| 安全 | 入库 `raw_response` 中不含 `encrypted_content`、`signature`，也不含 Key 后 4 位 |
| 前端 | 3001 预览「DeepSeek」页签同时列出 API 10 条与网页 10 条；详情按标题和表格分层，显示「参考 34 篇资料」，没有残留 `**` |

- 积分：手动 Run 不预留积分。积分的预留与结算（AC4 积分部分）待 2026-10-24 09:00 首个每日轮次核对。

## 5. 与网页端对比（优联 10 题，网页 Run `ed7d43ec-…` vs API Run `fc2227f8-…`）

| 提问 | 优联提及 网页/API | 来源 网页/API | 同 URL | 同域名 | 字数 网页/API | API 搜索词数 |
|---|---|---|---|---|---|---|
| 下水道反复堵塞，想先检查原因再疏通… | 否 / 否 | 15 / 0 | 0 | 0 | 1019 / 1693 | 0 |
| 什么市政工业管道检测设备比较适合建筑运维… | 否 / 否 | 14 / 20 | 2 | 6 | 1404 / 1775 | 2 |
| 哪些厂家的管道摄像机支持高清成像和精准定位… | 否 / 否 | 15 / 20 | 1 | 6 | 816 / 1415 | 2 |
| 哪些高清管道摄像系统适合带米数计数定位堵塞… | 是 / 否 | 15 / 38 | 0 | 3 | 889 / 2805 | 4 |
| 工业和市政每天使用、预算3万元以内… | 是 / 否 | 15 / 33 | 0 | 3 | 1192 / 2814 | 4 |
| 市政和工业管道检测需要采购推杆式内窥镜或… | 是 / 否 | 15 / 40 | 3 | 4 | 1702 / 4588 | 4 |
| 市政管道巡检、供水管道、排水管网、电缆管… | 是 / 否 | 15 / 19 | 1 | 2 | 1429 / 1747 | 2 |
| 有哪些厂家的下水道检查摄像头，适合建筑团… | 否 / 否 | 15 / 17 | 0 | 3 | 1206 / 1745 | 2 |
| 需要有支持Wi-Fi控制，可以手机或平板… | 否 / 是 | 15 / 34 | 1 | 5 | 1277 / 3334 | 4 |
| 需要能通过多个弯或P弯，推荐哪些管道摄像… | 否 / 是 | 15 / 43 | 1 | 4 | 1114 / 2083 | 6 |

- 优联提及：网页 4/10，API 2/10，没有一题两边都提及。
- 来源：网页 149 条，API 264 条，同 URL 只有 9 条。
- 知乎：网页 29 条，API 5 条。网页端前几名域名是 zhuanlan.zhihu.com、vevor.*；API 是 made-in-china、alibaba、vevor.com、srodcn.com。
- 平均字数：网页 1204，API 2399。API 回答更长，8/10 含表格。
- 结论：API 和网页端搜索到的来源、篇幅和品牌提及差异都很大。这次网页端还处于账号历史和个性化的影响下（同日 Collector 已统一问题前缀，但这轮是在那之前采的）。API 结果应看作独立的一个回答面，不是网页端的复刻。单样本对比只能看方向；以后如果要调模型或思考开关，先用同一组题重跑本表。明细见任务 `research/web-vs-api-20261023.md`、`research/probe-findings.md`。

## 6. 未完成项

- 2026-10-24 09:00 首个每日轮次：核对 DeepSeek 积分的预留与结算，以及 `usage_ledger` 与积分余额是否一致。
- 管理员测试接口 `POST /providers/deepseek/test` 未实调（本机的 Key 是直接写库的，没有使用管理员会话），错误体结构也未实测；Worker 的成功路径已经真实跑通。
- 隔离库 SQL 持久化测试未执行（与豆包同样待补）。
- 2026-11-01 起，DeepSeek 服务端搜索按 $0.75/千次计费，模型 token 另计。
