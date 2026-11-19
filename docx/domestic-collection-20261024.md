# 国内网页五平台采集（2026-10-24）

## 范围与结果

按用户要求，对优联科技当前启用的同一组 10 个问题，按豆包 → 文心一言 → 腾讯元宝 → 千问 → 智谱清言各采集一次。豆包和千问遇到验证后，经用户同意，先跑其余平台，最后补采。

租户：`38685331-621e-4dc6-bcc3-d5e35703ef90`；站点：`ded1a716-f791-491c-96e2-f737bb998960`；控制面：本机 5260 API。本轮是首次对豆包、文心、元宝、千问实际发送带统一前缀的问题（前缀见任务 `10-23-web-collector-question-prefix`），回答条件与 10-23 上午的无前缀轮次不同，比较时不能把差异直接归因于品牌变化。

| 平台 | Run ID | 成功回答 | 引用 | 后台分析 | 未完成 |
| --- | --- | --- | --- | --- | --- |
| 豆包 | `dce8a3cd-d5bf-45de-b3f8-6f44e37eb8a0` | 10/10 | 406 | 10/10 完成 | — |
| 文心一言 | `9568cff1-8401-414c-a56f-a11f93f63ebc` | 10/10 | 405 | 10/10 完成 | — |
| 腾讯元宝 | `664e718a-7cbd-4e2a-b801-03876a08138c` | 10/10 | 261 | 10/10 完成 | — |
| 千问 | `9d40a873-f070-4227-9afb-f02fa72069cb` | 8/10 | 139 | 8/8 完成 | 2 题，各已计 2/3 次 |
| 智谱清言 | `2da2461a-d57e-412e-a335-fc52ef267242` | 6/10 | 88 | 6/6 完成 | 4 题已发送并捕获，未提交 |

共 44 条回答入库，44 条分析完成。引用数为 `/api/v1/visibility-runs/{runId}/checks` 读回的成功 Check 引用之和。正文长度（字符）：豆包 1073–1913、文心 760–2740、元宝 1191–2095、千问 1018–1675、智谱 2536–4427。豆包 10 条 `queryAvailability=available`，其余平台为 `unknown`。

北京时间：豆包 13:11 创建、17:39 完成；文心 13:51–13:59；元宝 14:01–14:10；千问 14:12 创建，18:12 停止补采；智谱 14:14 创建，未完成。

千问和智谱的 Run 汇总显示 `partial_failed`，`failed_checks` 分别为 2 和 3；但 API 与 SQL 中对应 Check 均为 `pending`，任务为 `collecting`（租约未到期）或 `retryable_failed`。汇总计数与 Check 实际状态不一致，本次未修改后端。

## 执行方式

- 创建前用只读 SQL 核对站点启用提问，与 10-23 批准清单逐字相同；另存本轮清单。
- 每个平台复用原登录 Profile，新建本轮节点 ID 和空的加密 spool；Run 用 Development 的 `X-Tenant-Id` 通过 `POST /api/v1/visibility-runs` 创建。
- 外置驱动脚本按控制面领取条件和顺序只读检查队列，每次只为一个指定任务设置 `COLLECTOR_RETRY_TASK_ID`、`COLLECTOR_MAX_TASKS_PER_RUN=1`、`COLLECTOR_RUN_ONCE=true`；任一非 `accepted` 结果立即停止。题间隔 30 秒，豆包续跑 60 秒、千问续跑 120 秒。
- 发送前停止的任务，先从平台历史对话只读确认未产生新对话，才作为“已核实未发送”续跑一次；保留尝试账本。

## 现场处理

### 豆包

首题在发送前以 `page_contract_changed` 停止。现场只填不发诊断确认：豆包输入框每行一个 `<p>`，选中文本回读会把“前缀、空行、问题”读成 4 个换行，与发送内容不一致；平台历史无新对话。相邻 Collector 的共享网页适配器随后修复回读（见下文），实页复核逐段回读与发送内容一致后续跑。

之后有 2 次领取在发送前返回 `challenge_required`，1 次在发送前返回 `page_contract_changed`，历史对话均无对应问题。只读观察中，用户在可见的采集窗口发送了一条“1”，随后页面出现验证遮罩；用户确认该消息为本人发送。用户在登录窗口完成验证后，剩余 6 题全部成功。4 个任务控制面 `attempt_count=2`，其余为 1。

### 文心一言、腾讯元宝

两平台均一次通过 10/10，无停止，`attempt_count` 均为 1。

### 千问

领取后数秒内多次返回 `challenge_required`（发送前），平均约每 2 题触发一次。用户三次在登录窗口处理验证。历史对话核对：停止的任务均无对应对话。`a20291ce-…` 续跑成功（`attempt_count=2`）。剩余两题 `13f43f20-2f24-40bd-b7d1-7675483ffd1d`（米数计数定位）和 `ba8447d7-18e1-4f57-815e-b1b78833a81c`（预算 3 万、512Hz 定位）均已核实未发送，但控制面已计 2/3 次；为避免最后一次失败使任务永久失败，本轮停止。历史中出现两条非本轮问题的对话（“休息建议”“简单问候场景”），来自其他使用者。

### 智谱清言

6 题成功。4 题（`8c754e50-…`、`09e493d1-…`、`199d4a90-…`、`1ca0c83a-…`）在发送后来源读取阶段以 `page_contract_changed` 停止；每题在下一题前从原对话只读捕获，核对完整发送内容（前缀加原题）、正文双哈希与稳定性。证据仅存于仓库外，按 0600 保存。

恢复提交时，`8c754e50` 的第 9 张来源卡片（`m.chanpin818.com`）点击后有导航请求但浏览器不创建新页面，读取器按设计失败关闭。Clash 对该域名命中 `Match → 漏网之鱼`，走海外节点，本机直连测试也无法建立连接。未修改代码或代理设置。控制面按 `next_attempt_at` 领取，该题排在其余 3 题之前，所以 4 题本轮都没有提交。`199d4a90` 的只读来源试读曾成功读出 15 条。

## 相邻 Collector 改动

`apps/collector/src/adapters/consumer-web.ts`：输入框的直接子节点全部为 `<p>`（可夹空白文本节点）时，按段落 `textContent` 去掉 `\uFEFF` 后用单换行拼接作为回读结果；其他结构仍用原选中文本读取。新增两个豆包合成用例：段落式编辑器发送“前缀、空行、问题”，以及编辑器丢掉空行时不点击发送。在修复前，这两个用例均会失败，其中后一用例说明旧回读会把丢掉空行的内容当作一致而发送。

已执行（相邻 Collector，Node 24）：

```bash
node node_modules/vitest/vitest.mjs run --config vitest.config.ts apps/collector/src/adapters
node node_modules/typescript/bin/tsc --noEmit
node node_modules/eslint/bin/eslint.js apps/collector/src/adapters/consumer-web.ts apps/collector/src/adapters/doubao.test.ts
node node_modules/prettier/bin/prettier.cjs --check apps/collector/src/adapters/consumer-web.ts apps/collector/src/adapters/doubao.test.ts
```

适配器测试 11 个文件 203/203 通过，类型检查、lint 与格式检查通过。本仓库后端源码未修改，未运行 .NET 测试。

## 验证依据

- 5260 `/health/ready` 返回 `Healthy`；API 与 Worker 进程在跑，已入库回答的分析实际完成。
- 五个适配器均先 `probe` 返回 `ready`；豆包出口经 mihomo 连接确认为 `GeoSite cn → DIRECT`。
- API 读回五个 Run 的 Checks，状态与 SQL 中的 `collector_tasks`、`visibility_checks`、`analysis_check_work` 一致。
- 最终检查：五个本轮 spool 无待提交文件；五个 Profile 无 `.collector.lock`；无残留采集进程。智谱一次只读诊断异常退出，留下的锁核实 pid 已退出后改名为 `.collector.lock.stale-37973-20261024` 保留。

外置文件（`~/Library/Application Support/aivc/real-smoke/`，均 0600）：本轮批准清单与环境文件（`…-20261024-1792818521063`）、驱动脚本 `round-driver-20261024.py`、状态 `round-20261024-1792818521063.json` 与各平台事件日志、智谱恢复脚本 `qingyan-recover-20261024.mjs` 及 4 份 `qingyan-recovery-evidence-20261024-*.json`。密钥、Cookie 与原始回答不写入本文。

## 待处理

- 千问 2 题：控制面各剩 1 次。建议在验证刚处理完、间隔充足时逐题单独跑。
- 智谱 4 题：需要来源站点可达（例如临时让 `漏网之鱼` 走直连），然后按队列顺序用恢复脚本 `submit` 模式提交；不重发问题。
- Run 汇总状态与 Check 状态不一致的问题，需要单独排查后端汇总逻辑。
