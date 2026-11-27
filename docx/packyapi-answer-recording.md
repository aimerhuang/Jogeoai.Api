# PackyAPI 回答采集说明（千问 / 智谱清言 / Kimi / 元宝）

Trellis 任务：`10-29-domestic-52pay-api-provider`（千问 / 智谱清言 / Kimi，接入前实测见任务目录 `research/probe-findings.md` §8–§13）；`11-08-packy-yuanbao-hunyuan`（元宝 / 混元 hy4，见 §6）。

## 1. 通道概览

| Provider | 引擎（前端页签） | PackyAPI 分组 | 接口 | 默认模型 | 来源口径 |
| --- | --- | --- | --- | --- | --- |
| `packy_qwen` | `qwen`（通义千问） | `bailian` | `POST https://www.packyapi.ai/v1/responses`（流式）+ `web_search`，`enable_thinking:false` | `qwen3.8-flash` | 全部检索结果，只有 URL（回答标注里出现的同一 URL 会补上标题） |
| `packy_glm` | `zhipu_qingyan`（智谱清言） | `zai-officially` | 同上，`reasoning:{effort:"low"}`（对应网页端“快速”；未设 effort 时发 `thinking:{type:"disabled"}`） | `glm-5.3-flash`（037 起；之前为 `glm-5.2`） | 同上 |
| `packy_kimi` | `kimi`（Kimi） | `kimi-officially` | `POST https://www.packyapi.ai/v1/messages`（Anthropic 兼容）+ `web_search_20250305`，`max_uses=1` | `kimi-k3`（代码种子；本机设置行 2026-11-02 起为 `kimi-k2.6`，见 §5.3） | 一轮搜索的检索结果，带标题 |
| `packy_yuanbao` | `tencent_yuanbao`（元宝） | `hunyuan-officially` | `POST https://www.packyapi.ai/v1/responses`（流式）+ `web_search`，`max_tool_calls=3`，`max_output_tokens=32000` | `hy4`（该分组唯一模型） | 渠道不返回检索结果；只记模型打开过的网页，通常为空（`not_exposed`），见 §6 |

- 四个 Provider 都是 `captureMode=api`、`evidenceClass=api_response`，固定 CN / zh-CN。parser 版本：千问 / GLM 为 `packy-responses-v1`，Kimi 为 `packy-anthropic-v1`。用量类型为 `packy_{qwen|glm|kimi|yuanbao}_call`。
- **为什么拆成三个 Provider**：PackyAPI 的 key 按分组发放、不能互用，三个引擎的协议也不同。每个 Provider 一行设置、一把 key，所以目录、手动 Run、每日计划这些共用的「是否已配置」判断都不用改。
- 执行：`PackyQwenTaskWorker`、`PackyGlmTaskWorker`、`PackyKimiTaskWorker`、`PackyYuanbaoTaskWorker` 共用配置节 `Packy:Worker`，默认批量 2、并发 2、租约 600 秒，并发上限每个 Provider 各算一份。每个任务同步调用一次，结果通过 `CompleteSynchronousTaskAsync` 在一个事务里写入。
- 积分与 Cloro、DeepSeek 同价：每日监控按「问题 × 目标 × 样本」预留并结算 10 积分。手动 Run 自 2026-11-27 起同价预留，余额不足返回 402 `CREDIT_INSUFFICIENT`（Trellis `11-27-sec-manual-run-charging`，代码已实现，本机 API / Worker 尚未用该构建重启；见[品牌监控配置说明](brand-monitoring-config.md) §4、§9）；在此之前不预留，§5、§6 记录的手动 Run 都属于这一时期。

## 2. 配置

- 迁移 `db/migrations/036_packy_answer_providers.sql` 依赖 025，只插入三行种子（未启用、无凭据；超时千问 540 秒、GLM / Kimi 240 秒），可重复执行。执行时要加 `--default-character-set=utf8mb4`。回滚：`DELETE FROM answer_provider_settings WHERE provider IN ('packy_qwen','packy_glm','packy_kimi')`。
- Key 写入：
  - 本机约定是 `python3 -I .trellis/tasks/10-29-domestic-52pay-api-provider/research/store_packy_keys.py`：依次用 getpass 输入三个分组的 key，写库并启用。
  - 也可以由系统管理员调用 `PUT /api/v1/providers/packy/{qwen|glm|kimi|yuanbao}/settings`。
- 迁移 `db/migrations/046_packy_yuanbao_provider.sql` 依赖 025：只插入 `packy_yuanbao` 种子行（`hy4`、超时 540 秒、`maxToolCalls=3`，未启用、无凭据），可重复执行；回滚 `DELETE FROM answer_provider_settings WHERE provider='packy_yuanbao'`（先停用品牌目标）。
- 迁移 `db/migrations/037_packy_glm_fast_mode.sql` 依赖 036：把仍是种子模型 `glm-5.2` 的 `packy_glm` 行改为 `glm-5.3-flash` 加 `reasoningEffort=low`，不动凭据、开关和超时，可重复执行；回滚 SQL 写在文件头。
- `options`：`{"requestMode":"web_search","sources":[],"maxToolCalls":1-10,"thinking":"disabled"|"enabled","reasoningEffort":null}`。
  - 默认值贴近网页端：千问 `qwen3.8-flash` 关思考（快速），GLM `glm-5.3-flash` + `reasoningEffort=low`（快速），Kimi `kimi-k3` 关思考（`kimi-officially` 分组没有 `kimi-k2.8`，2026-10-29 用户确认保持 `kimi-k3`；2026-11-02 用户要求换成更便宜的 `kimi-k2.6`，只改了本机设置行，代码种子未改，见 §5.3）。网页端 Kimi 的“对话长度：标准”在 API 里没有对应参数，输出上限仍为 16000 token。
  - `reasoningEffort` 只对 GLM 开放（minimal / low / medium / high），设置后代替 `thinking` 下发：`glm-5.3-flash` 不认 `thinking:disabled`（2026-10-29 实测仍有 4,647 个推理 token、186 秒、8 次搜索、70 个来源 URL），`effort=low` 实测 43 秒、87 个推理 token、3 次搜索、22 个来源 URL、正文 1,480 字。代价是来源比 `glm-5.2`（约 75 个 URL）少。探测记录见任务目录 `research/probe-zai-glm53flash-responses-*.json`。千问、Kimi 设置 `reasoningEffort` 仍返回 400。
  - 千问 / GLM 的 `maxToolCalls` 只做记录，不发给上游（PackyAPI 是否接受这个参数没有实测）。
  - Kimi 必须为 1：`kimi-officially` 渠道只执行第一轮服务端搜索，多轮时会没有正文，或者在正文里漏出 `<|open|>tools…` 标记。
- 诊断：`POST /api/v1/providers/packy/{qwen|glm|kimi|yuanbao}/test`（系统管理员），返回回答、搜索词、来源、质量和用量，不返回 key。
- 品牌要单独启用 `packy_*/<engine>/api/CN/zh-CN` 目标。启用后，每日 09:01 的自动监控会按各单元格的回答模式和频率带上它们（2026-11-09 起由 09:00 改为 09:01；「2次/天」的单元格另在 00:05 采一轮，见 [品牌监控模型配置说明](brand-monitoring-config.md)）。**目标已启用但 key 未配置或被禁用时，每日时隙整轮以 `TARGET_UNAVAILABLE` 跳过（同轮的 Cloro 也一起跳过）**，所以撤销 key 之前要先停用目标。
- 回滚 Worker：设置 `Packy:Worker:Enabled=false`（三个 Worker 都不注册，已创建的任务保持 pending）。

## 3. 回答与引用口径

- **千问 / GLM（Responses）**：
  - 流式读取，只保留终止事件（`response.completed`）里的完整 `response` 对象。
  - 正文只取**最后一个** `message`，前面的 message 是搜索过程中的过渡语。
  - 来源合并两部分：所有 `web_search_call.action.sources[].url`，以及**所有** message 的 `url_citation` 标注。按规范化 URL 去重（`www.` 会被去掉），最多 100 条。
  - 搜索词取 `action.query`，按 `;` 拆分、去重。
- **Kimi（Messages）**：
  - 正文只取最后一个工具块之后的 text，这样会去掉开头「Search results for query: …」的回显。
  - 来源取 `web_search_tool_result` 中的 `web_search_result`（url + title）。
  - 搜索词取 `server_tool_use.input.query`。
- **没有触发搜索**：照常成功入库，`query_availability=empty`、来源 `availability=not_exposed`，与 DeepSeek 一致。
- 原始响应存档前剥离 `encrypted_content`、`signature`、思考正文、`reasoning.summary/content` 和 `logo_url`。

## 4. 错误与重试

| 条件 | 错误码 | 处理 |
| --- | --- | --- |
| 未启用或没有 key | `PACKY_NOT_CONFIGURED` | 手动 Run 返回 503；每日时隙跳过 |
| 401 / 403 | `PACKY_AUTH_FAILED` | 终态 |
| 402 | `PACKY_INSUFFICIENT_BALANCE` | 延后 10 分钟，最长 2 小时 |
| 429，或 503 / 529（不是 `model_not_found`） | `PACKY_RATE_LIMITED` | 退避 30 / 60 / 120 秒，最多 5 次 |
| `model_not_found`（分组下没有渠道），以及 400 / 404 / 413 / 422 | `PACKY_INVALID_REQUEST` | 终态 |
| 408、5xx（含 524、500 `packy_fastapi_error`）、网络错误、流中断或 `error` 事件、`failed`、`pause_turn` | `PACKY_REQUEST_FAILED` | 最多重试 3 次 |
| 超过超时设置 | `PACKY_TIMEOUT` | 最多重试 3 次 |
| `incomplete`（输出上限或内容过滤）、`max_tokens`、结构不合法 | `PACKY_INVALID_RESPONSE` | 终态 |
| 搜索后没有正文，或正文漏出原始工具标记 | `PACKY_INCOMPLETE_ANSWER` | 最多重试 3 次 |

## 5. 部署与验证状态（2026-10-29）

- 代码：`dotnet build Jogeoai.slnx` 通过；定向测试 572 个全部通过，其中 PackyAPI 41 个，其余是 DeepSeek、豆包、Cloro 以及共用契约的回归。
- 迁移 036：已在本机 `jogeoai` 执行。其他库未执行。
- 迁移 037（2026-10-29 23:20 北京时间）：已在本机 `jogeoai` 执行，`packy_glm` 行变为 `glm-5.3-flash` + `reasoningEffort=low`，仍启用。其他库未执行。对应代码的定向测试通过（Packy 44 个；DeepSeek / 豆包 / 回答 Provider 设置 242 个）；`api.ts` 已从临时 5280 实例重新生成。本机 5260 API 与完整 Worker 于 2026-10-29 23:47（北京时间）按用户要求停掉 22:24 的旧进程后用新代码重启，`/health/live` 与 `/health/ready` 均为 200。随后 `POST /api/v1/providers/packy/glm/test`（临时 2 小时 owner 会话，调用后已删除）：HTTP 200，33 秒，模型 `glm-5.3-flash`，推理 91 token，2 次搜索，18 条来源（16 个域名，质量 `complete`），正文 1,121 字。每日时隙与手动 Run 在新代码下的真实采集尚未核对。
- Key：三把分组 key 由用户授权直接写库（SQL 经 stdin），三行均已启用。目录中三个目标为 `available`。
- 本机 5260 API 与完整 Worker：2026-10-29 22:24（北京时间）用最终代码重启，`/health/live` 和 `/health/ready` 都返回 200。
- 前端：`Jogeoai.web` 的 `PROVIDER_LABELS` 新增三个标签；`api.ts` 从 5260 重新生成（同时补齐了 035 采集器账号池类型）；`team-brands-logic` 17 个测试和 `npm run build` 通过。

### 5.1 真实采集（优联科技 10 个启用提问 × 3 引擎 × 1 样本，手动 Run，显式 targets）

Run：`packy_qwen` `09753996-…`、`packy_glm` `bd4e27af-…`、`packy_kimi` `9bfd5c18-…`。三个 Run 都是 `succeeded`，共 30/30 成功。

| 引擎 | 成功 | 正文字数 | 平均搜索词 | 来源（带标题） | 去重域名 | 平均 / 最长耗时 | 提及优联 / 引用 szulink |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 千问 | 10/10 | 1,615–5,991 | 20.2 | 931（0） | 287 | 156 秒 / 293 秒 | 8/10 / 8/10 |
| 智谱清言 | 10/10 | 1,799–4,083 | 6.8 | 499（0） | 160 | 61 秒 / 85 秒 | 6/10 / 6/10 |
| Kimi | 10/10 | 20–2,133 | 1.0 | 156（155） | 84 | 51 秒 / 74 秒 | 7/10 / 10/10 |

- 「提及」是对正文做的简单字符串匹配（优联 / ulinktek / szulink），不是系统的分析口径。
- 查询接口与 MySQL 的来源数逐个一致。`usage_ledger` 三类用量各 10 条；手动 Run 不预留积分。`analysis_check_work` 30 条全部 `completed`。正文里没有回显或工具标记。
- 实测中发现并已修正的问题：
  1. **千问超时**：完整回答比 R0 慢得多（平均 156 秒，最长 293 秒，搜索词最多 20 多个），首批有任务在 240 秒处超时。已把 `packy_qwen` 的超时改为 540 秒（库、036 种子、默认值同步修改），Worker 租约默认值改为 600 秒，并重启 Worker；之后超时的那个任务重试成功。
  2. **Kimi 搜完没有正文**：有 2 次「搜索后没有正文」（`PACKY_INCOMPLETE_ANSWER`），重试后都成功了。
  3. **Kimi 过渡语被当成正文**：有 1 条回答只有一句过渡语「我再搜索一些更具体的型号和实测案例信息。」（20 字，Check `1ab41e7b-…`），作为成功入库了。已增加规则：Messages 搜索后的正文若不到 50 字且只有一行，记为 `PACKY_INCOMPLETE_ANSWER` 并重试；补测试后 572 个通过，Worker 已重启加载。**这条已入库的数据保留未改**，是否删除或补采由用户决定。
- 管理员测试接口：未实调（需要系统管理员会话）。
- 优联科技品牌目标（2026-10-29 22:33 北京时间按用户要求启用）：`PUT /sites/ded1a716-…/targets`，`expectedVersion` 17 → 18，目标从 12 个变为 15 个，原有 12 个 targetId 不变。新增目标 `packy_qwen` `1e6a0272-…`、`packy_glm` `5f04dfab-…`、`packy_kimi` `8c6580c1-…`，写入时已记录 `monitoring_enabled_at`（14:33 UTC），不会触发 `MONITORING_ACTIVATION_PENDING`。
  - 自动监控计划 `2952952d-…`（`jogeoai:auto-prompt-monitoring:v1`，Asia/Shanghai 09:00）的快照仍是 4 个 Cloro 加 `deepseek_api`：快照只在时隙到期（`ReconcileAsync` 与 `AutomaticMonitoringSelection.Targets`）或提问变化时刷新。**首个包含 PackyAPI 的时隙是 2026-10-30 09:00（北京时间），尚未发生**；之后要核对 slot、Run、积分结算和回答记录页签。
  - 每日积分由 10 题 × 5 目标 × 10 = 500 变为 10 × 8 × 10 = 800；租户可用积分 1,253,200（2026-10-29 核对）。每天还会产生 30 次 PackyAPI 调用的费用。
  - 撤销任何一把 PackyAPI key 之前，必须先停用对应目标，否则整轮（包括 Cloro）会以 `TARGET_UNAVAILABLE` 跳过。

### 5.2 首个每日轮次（2026-10-30 09:00）

2026-10-30（北京时间）由定时核对任务只读查验，本机 API 与完整 Worker 自 2026-10-30 07:56（北京时间）起运行，早于时隙触发。

- **slot**：`schedule_slots` `a2653597-…`（`local_slot_key` `20261030T0900`，创建于 2026-10-30 09:00:08 北京时间 / 01:00:08 UTC），status `completed`，无 skip。`selection_snapshot` 含 8 个目标（4 个 Cloro + `deepseek_api` + `packy_qwen`/`packy_glm`/`packy_kimi`）、10 个提问、样本数 1。对应 Run `271c2938-ddd5-4c1c-94f4-132649d968e9`。计划 `2952952d-…` 的 `selection_snapshot` 已刷新为 8 目标，`next_slot_at` 2026-10-31 09:00（北京时间），计划行 `version` 16。前两个时隙（10-28、10-29）均为 `skipped`（`CLORO_KEY_POOL_EXHAUSTED`），本轮未再跳过。
- **Run**：status `partial_failed`，`target_config_version` 18，09:00:08 开始、09:22:22 完成（北京时间，约 22 分钟），80 个检查，成功 79、失败 1（`error_code` `PACKY_INCOMPLETE_ANSWER`）。`provider_tasks`：cloro 40 个 `succeeded`（轮询计数最高 174）、deepseek_api 10 个 `succeeded`（1 次尝试）、packy_glm 10 个 `succeeded`（1 次）、packy_qwen 10 个 `succeeded`（最多 2 次）、packy_kimi 9 个 `succeeded`（最多 3 次）+ 1 个 `terminal_failed`（3 次尝试，`PACKY_INCOMPLETE_ANSWER`，「PackyAPI 搜索后没有生成完整回答。」，提问为「工业和市政每天使用、预算 3 万元以内……管道内窥镜品牌和型号？」）。
- **检查（packy 部分 30 项，29 成功 / 1 失败）**：

  | 引擎 | 成功 | 正文字数 | 有搜索词 | 来源（带标题） | 去重域名 | 平均 / 最长耗时 | 提及优联 / 引用 szulink |
  | --- | --- | --- | --- | --- | --- | --- | --- |
  | 千问 | 10/10 | 2,127–4,593 | 10/10 | 936（0） | 312 | 185 秒 / 333 秒 | 8/10 / 9/10 |
  | 智谱清言 | 10/10 | 961–1,777 | 8/10 | 157（0） | 69 | 35 秒 / 57 秒 | 4/10 / 4/10 |
  | Kimi | 9/10 | 58–2,080 | 9/9 | 119（118） | 65 | 45 秒 / 70 秒 | 4/9 / 8/9 |

  「提及 / 引用」仍是对正文和 `citations.source_domain` 的字符串匹配（优联 / ulinktek / szulink），不是系统分析口径。三个引擎正文中都没有 `<|open|>`。
- **遗留质量问题（未修复，仅记录）**：Kimi 有 3 条「成功」检查（`bae26e9f-…`、`e5b2c3cd-…`、`f122faee-…`，58 / 88 / 100 字）整条正文只有一行英文过渡语 `Search results for query: …`。5.1 中加的「不足 50 字且只有一行」规则没拦住它们——这 3 条都超过 50 字。相应地上表 Kimi 的来源数、字数下限和提及数都被这 3 条拉低或污染。是否删除、补采或收紧规则（例如把 `Search results for query` 前缀直接判为 `PACKY_INCOMPLETE_ANSWER`）由用户决定。
- **积分与用量**：预留 800（80 项 × 10，`monitoring_credit_reservations` status `partial`），capture 790、释放 10（即失败的那条 Kimi，释放发生在 09:06:10 北京时间）。packy 部分 29 项 capture 共 290 积分 + 1 项释放 10 积分，与「30 项 × 10 = 300」的预期一致（失败项按规则释放）。`usage_ledger` 本轮 `packy_qwen_call` 10 条、`packy_glm_call` 10 条、`packy_kimi_call` 9 条（失败项无用量条目）。`credit_ledger` 对本轮为 1 条 reserve(-800)、79 条 capture、1 条 release(+10)。租户 `credit_accounts`：可用 1,251,890、预留 500——这 500 是 2026-10-30 00:02（北京时间）「文章链接每日监控预留」，与本轮无关；本轮监控预留已全部结清（无 held）。`analysis_check_work` 79 条全部 `completed`（失败检查不产生分析任务）。
- **同轮其他 Provider**：Cloro 四引擎 40/40、DeepSeek API 10/10 全部成功，不展开。

小结：首个每日时隙如期带上三个 PackyAPI 目标并正常执行。千问与智谱清言 10/10，Kimi 9/10（1 项搜索后无正文、重试 3 次后按规则失败并释放积分）；积分预留 / 结算 / 释放全部对账。需要用户决策的遗留问题：Kimi 3 条超过 50 字的纯过渡行回答以「成功」入库。

### 5.3 Kimi 失败与改用 `kimi-k2.6`（2026-11-02）

- **背景**：本机 Worker 从 10-30 轮次后就没有运行，10-31 到 11-02 的时隙都没有触发。11-02 16:28（北京时间）重启 Worker 后，计划只补跑了当天的 `20261102T0900`（计划 `2952952d-…` 的 `missed_slots` 累计到 5），Run `01e3e5c5-…`。
- **Kimi 10/10 失败**：16:28:22–16:28:27 之间，10 个任务都在第一次尝试时失败，错误码 `PACKY_INVALID_REQUEST`，不可重试。PackyAPI 控制台的使用日志里没有这 10 次请求，所以请求在到达模型之前就被拒绝了，最可能的原因是分组暂时没有可用渠道（`model_not_found`）。目前无法确认：store 把 adapter 的原始消息替换成了通用文案，HTTP 状态也没有保存。约 45 分钟后用同一把 key 探测：`/v1/models` 能看到 `kimi-k2.6`、`kimi-k2.7-code`、`kimi-k3`、`kimi-k3.1-pro`、`kimi-k3.1-pro-highspeed`，最小请求返回 200，说明渠道已经恢复。
- **k3 与 k2.6 对比**（请求体与生产相同：`max_tokens` 16000、`thinking:disabled`、`web_search_20250305` `max_uses=1`）：同一个「多个弯 / P 弯」问题，`kimi-k3` 用时 25 秒，正文只有一行 `Search results for query: …`（69 字），这是 §5.2 中的遗留问题；`kimi-k2.6` 用时 77 秒，正文 3,362 字、15 条来源。另一个问题第一次用 k2.6 时，240 秒内没有响应。这个渠道上 `thinking:disabled` 不生效，两个模型都仍会返回 thinking 块。k3 的那次调用在 PackyAPI 日志里花费 $0.198，分组倍率从 0.9 涨到 0.95（用户在控制台看到的）。
- **配置变更**：用户批准后，只把 `answer_provider_settings` 中 `packy_kimi` 行的 `model` 从 `kimi-k3` 改成 `kimi-k2.6`（2026-11-02 17:38 北京时间），没有改凭据、开关、超时（240 秒）和 `options`。适配器每次调用都直接读库，所以不用重启。回滚：`UPDATE answer_provider_settings SET model='kimi-k3' WHERE provider='packy_kimi';`。
- **补采**：手动 Run `c4c29f07-c4be-4e72-b581-f9872383dd5e`（`targetIds` 只有 `8c6580c1-…`，用 11-02 时隙的 10 个提问，样本数 1；手动 Run 不扣项目积分）。结果 10/10 成功，都只尝试了 1 次，分析 10/10 `completed`，模型版本都是 `kimi-k2.6`，用时 54–128 秒，正文 1,732–12,263 字，来源 7–23 条（共 134 条）。8 条回答是干净的。另外 2 条（`32497983-…` 8,192 字、`67d5eb12-…` 12,263 字）开头带「让我再搜索…」，正文里还以文字形式夹着渠道额外跑的搜索轮次（`Search results for query: …`），但结尾都有完整答复；这些额外轮次的来源没有结构化入库。当天定时 Run 里失败的 10 条 Kimi 检查保持原样。
- **待办（代码，未做）**：`model_not_found` 改为延迟重试；失败时保留上游的 HTTP 状态和错误码；把开头的「再搜索」过渡语和夹在正文里的 `Search results for query` 段识别出来，剔除或判为不完整。另外要观察 240 秒超时对 k2.6 是否够用。

### 5.4 千问思考开关核对与 Kimi 降价探测（2026-11-05）

- **千问思考**：本机 `packy_qwen` 行为 `qwen3.8-flash` + `thinking:disabled`，每次请求都下发 `enable_thinking:false`。这个开关有效（不关时模型把输出额度全用在推理上，不搜索），但关不干净：2026-11-02 到 11-05 共 40 次成功调用，每次平均推理 5,100 token（最多 14,917），约占输出的 59%；平均 6.6 次搜索、8.4 万输入 token（其中约 5.3 万命中缓存）。千问不接受 `reasoningEffort`（400），API 侧没有更“快”的参数可用。
- **Kimi 价格**（`GET https://www.packyapi.ai/api/pricing`，按每百万 token、已乘分组倍率折算的相对值）：`kimi-officially`（0.95）里 `kimi-k2.6` 与 `kimi-k2.7-code` 同为最低（输入 6.2 / 输出 25.7）；`kimi-sale`（0.5）只有 `kimi-k3`（10 / 50）和 `kimi-k3.1-pro`（6.5 / 34），都更贵；只有 `bailian`（0.5）分组的 `kimi-k2.5` 更便宜（2 / 10.5，按当前平均用量约为 k2.6 的 38%）。
- **百炼 `kimi-k2.5` 探测**（用 `packy_qwen` 的 key，提问同 §5.3 的「多个弯 / P 弯」；脚本与记录见任务目录 `research/probe_bailian_kimi.py`、`research/probe-bailian-kimi-k25-*.json`）：
  - Responses + `web_search`（加 `enable_thinking:false` 或不加）：两次都是 `response.failed`，上游返回 `InvalidParameter: Agent capabilities are not enabled for the current model`，没有 usage。
  - Anthropic Messages + `web_search_20250305`（`max_uses=1`，与生产 Kimi 相同）：HTTP 200，81 秒，正文 2,857 字，但**没有执行搜索**（输入 30 token，没有 `server_tool_use`，来源 0 条），正文里的“实际案例”没有依据；`thinking:disabled` 也不生效（仍有 3,216 字的思考块）。
  - 结论：百炼分组的 `kimi-k2.5` 拿不到检索来源，不能用于回答采集。经 PackyAPI 带来源的 Kimi 最便宜仍是 `kimi-officially` 的 `kimi-k2.6`，本机设置未改。

## 6. 元宝（`packy_yuanbao`，混元 hy4）与停用 Kimi（2026-11-08）

### 6.1 停用 Kimi 每日采集

- 用户要求停掉 Kimi 的每日采集。2026-11-08 约 21:30（北京时间）把优联科技（site `ded1a716-…`）目标集合从版本 19 改到 20，只把 `packy_kimi/kimi/api/CN/zh-CN`（target `8c6580c1-…`）停用；Provider 设置行、key 与其他目标都没动，也没有待执行的 Kimi 任务。
- 没有选择禁用 `packy_kimi` 设置行：已启用目标的 Provider 不可用时，每日时隙会整轮以 `TARGET_UNAVAILABLE` 跳过。
- 当天 09:00 的时隙已在停用前完成；计划快照在下一个时隙（2026-11-09 09:00）刷新后不再包含 Kimi。恢复：在品牌目标里重新启用即可（targetId 不变）。

### 6.2 接入实测与选型

探测记录见任务目录 `research/probe-findings.md`。key 属于 `hunyuan-officially` 分组，`/v1/models` 只有 `hy4`（定价表：模型倍率 3、补全倍率 3、分组倍率 0.5）。

- 只有 Responses + `web_search` 会真正检索。Chat Completions 的混元增强参数（`enable_enhancement`、`search_info`、`citation`）被网关丢弃，不检索；Messages + `web_search_20250305` 只返回检索计划文字，并以 `stop_reason=tool_use` 结束，网关不执行搜索。
- 思考关不掉：`reasoning.effort=low`、`thinking:{type:disabled}`、`enable_thinking:false` 三种写法都无效，网关回显 `reasoning.effort=high`。
- **不返回来源**：检索项只有 `query`，没有 `sources`；正文没有 `url_citation`，`include` 参数也无效。只有 `visit` 动作带一个 URL（模型打开过的网页）。
- 不限搜索次数时，一题搜 20 次以上，用时 300–480 秒，输入 36–47 万 token。`max_tool_calls:3` 能把搜索严格限制在 3 次、输入降到约 2.6 万 token，但模型会改为多思考，用时仍约 5 分钟。
- 用户选择按「限 3 次搜索」接入（网页版元宝默认是混元、联网、不开深度思考，限次最接近），并接受来源基本为空。

### 6.3 实现口径

- `packy_yuanbao/tencent_yuanbao`，`api/api_response`，固定 CN / zh-CN，parser `packy-responses-v1`，用量 `packy_yuanbao_call`，管理渠道 `yuanbao`。
- 请求：`model=hy4`、`stream`、`tools:[web_search]`、`max_tool_calls=options.maxToolCalls`（默认 3，只有元宝会下发）、`thinking:{type:disabled}`（不生效，但保持选项语义）、`max_output_tokens=32000`（其他 Provider 仍 16000）。
- 解析：沿用千问 / GLM 的 Responses 解析（最后一个 message 为正文）。`visit` 动作的 URL 记为无标题来源（对所有 Responses Provider 生效）。元宝搜过但没打开任何网页时，来源可用性记 `not_exposed`，不记 `empty`。
- 回答在解析阶段被拒时，适配器记一条 Warning（错误码 + 安全消息）；`incomplete` 的消息带 `incomplete_details.reason`。入库的错误文案仍是按错误码统一的通用文案。
- 目录与品牌目标的引擎显示名新增 `tencent_yuanbao → 腾讯元宝`（`ManagementStore.EngineDisplayName`），网页采集的 `domestic_collector/tencent_yuanbao` 目标也从原始代码改显示为「腾讯元宝」。
- 代码检查（`trellis-check`）后 2026-11-08 22:4x 再次重启 API / Worker，加载显示名、Swagger 说明与更严格的日志 token 校验（`SafeCode` 改用 `\z`）；前端 `api.ts` 随之重新生成。定向测试 119/119 通过。

### 6.4 部署与验收（本机 `jogeoai`）

- 代码：`dotnet build Jogeoai.slnx` 通过；`FullyQualifiedName~Packy` 定向测试 50/50 通过（含 6 个元宝用例）。Provider 列表相关定向测试中，8 个 `CitationDomainSqlTests` 因缺少隔离库环境变量 `JOGEOAI_CITATION_TEST_CONNECTION` 失败，`MonitoringAnswerApiTests` 的 Swagger 参数断言缺 `termPolarity` 失败，二者与本次改动无关。
- 迁移 046：2026-11-08 21:50（北京时间）先备份到 `~/jogeoai-backups/jogeoai-pre046-20261108-215037.sql.gz`，再在本机 `jogeoai` 执行。其他库未执行。
- Key：用户提供，经 stdin 直接写入 `packy_yuanbao` 设置行并启用（提示 `****ZDwW`）；不进入文件、文档与日志。
- 进程：停掉 13:03 用 `dotnet run` 启动的旧 API / Worker，在 Claude 终端标签 c1 / c2 用 `./scripts/run-local.sh` 重启；22:31 为加载 32000 输出上限再次重启。`/health/live`、`/health/ready` 均为 200。
- 品牌目标：优联科技目标集合版本 20 → 21，新增并启用 `packy_yuanbao/tencent_yuanbao/api/CN/zh-CN`（target `bac8ee82-…`，目录中为 `available`），原有 16 个目标的 targetId 与开关不变。每日计划在 2026-11-09 09:00 刷新快照后包含元宝。每日新增 10 题 × 10 积分。
- 首轮手动 Run `572d6619-…`（计划中的 10 个提问 × 元宝 × 1 样本，手动 Run 不预留积分；当时输出上限为 16000）：`partial_failed`，8/10 成功，均只尝试 1 次。成功的 8 题模型均为 `hy4`，每题 3 个搜索词（`available`），引用 0 条（`not_exposed`，这一轮没有出现 `visit`），正文 2,735–5,414 字，用时 145–340 秒，3 题正文提到优联 / szulink（字符串匹配，不是系统分析口径）。分析 8/8 `completed`，`usage_ledger` 记 `packy_yuanbao_call` 8 条。另外 2 题以 `PACKY_INVALID_RESPONSE`（不可重试）在 328 秒和 402 秒时失败；当时没有保存上游原因。用同样的请求体重放这 2 题都成功（`completed`，输出 11.6k / 12.8k token，其中推理约 9.3k），判断为偶发的输出上限截断（未直接证实：失败时没有记录 `incomplete_details`），因此把元宝的输出上限改为 32000，并增加拒绝日志。
- 补跑 Run `bc269673-…`（22:31 重启后的新代码，只含失败的 2 个提问）：`succeeded`，2/2 成功，均尝试 1 次，用时 307 秒 / 204 秒，正文 3,722 / 3,216 字，各 3 个搜索词，引用 0 条（`not_exposed`），两题正文都提到优联，分析 2/2 `completed`。至此优联 10 个提问在元宝上都有成功回答（共 10 条）。
- 未核对：首个包含元宝的每日时隙（2026-11-09 09:00）的 slot、积分预留与结算；管理员 `GET/PUT /providers/packy/yuanbao/settings` 与 `/test` 未实调（需要系统管理员会话）；前端页面未走查。
- 入库的原始响应里 `usage` 只剩 `tool_usage`，没有 token 数；同期千问 / GLM 的记录同样没有 token 数（重放时网关流里是有的），裁剪发生的位置未查明。要核对用量需看 PackyAPI 控制台。

### 6.5 停用元宝每日采集（2026-11-08 晚）

- 验收完成后，用户要求把元宝从采集任务中去掉。优联科技目标集合从版本 21 改到 22，只把 `packy_yuanbao/tencent_yuanbao/api/CN/zh-CN`（target `bac8ee82-…`）停用；`packy_yuanbao` 设置行、key、迁移 046 与代码都保留，没有待执行的元宝任务，已入库的 10 条元宝回答保留。
- 网页采集目标 `domestic_collector/tencent_yuanbao` 未改动；它不属于每日计划（每日计划只含 API / Cloro 目标）。
- 计划 `2952952d-…` 当前存的快照仍是旧的 9 个目标（含 Kimi、不含元宝），会在下一个时隙 2026-11-09 09:00（北京时间）到期时按当前启用目标刷新，预计为 8 个目标：Cloro 四个 + `deepseek_api` + `doubao_api` + `packy_qwen` + `packy_glm`；每日积分 10 题 × 8 目标 × 10 = 800。该时隙尚未发生，未核对。
- 恢复：在品牌目标里重新启用元宝（targetId 不变）。
