# 优联科技国内网页模型采集（2026-10-25）

## 本轮范围与当前进度

用户在完成豆包采集后要求继续其他模型，并沿用“不创建 Trellis 任务、直接采集”的选择。读取品牌 `ded1a716-f791-491c-96e2-f737bb998960` 的目标及当前 10 个启用提问，确认问题原文与本日批准清单一致，范围为其余已配置的国内网页模型：文心一言、腾讯元宝、千问、智谱清言、DeepSeek。

下表对应本机 5260 API 和 `jogeoai` 数据库的实际读回结果。安全元数据见 [验证快照](domestic-collection-20261025.json)，不含正文或凭据。

| 模型 | Run | 当前结果 | 引用 | 分析 |
| --- | --- | --- | ---: | --- |
| 豆包（前一轮请求已完成） | `9112f588-14f4-4a04-bf33-a0b3f4e3c69b` | 10/10 成功 | 358，全部有标题 | 10/10 完成 |
| 文心一言 | `7c156cb3-7f64-47b3-8a0a-1011eac0727f` | 10/10 成功 | 403，标题未提供 | 10/10 完成 |
| 腾讯元宝 | `03b96817-c7fa-4331-b9f8-405d4d2c81bc` | 10/10 成功 | 278，全部有标题 | 10/10 完成 |
| 千问 | `2eb26e7c-e7c6-411d-91d6-4db7df619340` | 6/10 入库：3 条正常回答、3 条简短拒答；剩余 4 题用户跳过 | 已入库 63，全部有标题 | 已入库 6/6 完成 |
| 智谱清言 | 本日未创建新 Run | 先被昨日 4 条待恢复任务阻塞 | 本日无新结果 | 本日无新结果 |
| DeepSeek 网页 | 本日未创建新 Run | 探测为 `challenge_required`，用户决定本次跳过 | 本日无新结果 | 本日无新结果 |

本次继续采集新增 26 条已入库响应、744 条引用；加上此前豆包，本日这组操作共 36 条已入库响应、1102 条引用，36 条分析均完成。其中千问有 3 条简短拒答，不能等同于正常解答。文心和元宝均确认每题浏览器发送及控制面尝试各一次；千问账本有 7 个不同任务、每个一次预留，6 条已提交，第 7 题未提交，其余 3 题尚未尝试。

## 已完成两轮

- 文心：北京时间 10:35:58 创建，10:45:31 最后一题入库，10:46:01 驱动退出 0；10 条回答均保留 Markdown。403 条引用是实际 URL，采集器本次未提供标题或搜索词。
- 元宝：北京时间 10:46:02 创建，10:56:26 最后一题入库，10:56:56 驱动退出 0；278 条引用全部有标题，10 条回答保留 Markdown，搜索词未提供。
- 两轮都通过 Run、Checks、逐条详情、按本轮时间窗及引擎过滤的回答记录核对：`totalCount=10`，Check ID 集合一致，`analysis_check_work` 为 10 条 `completed`，提交审计为 10 条 `accepted`。
- 两个平台 Profile/spool 锁均已释放，无待提交密文。浏览器账本各有本轮 10 个不同任务、每题一次。

国内兼容 Run 概要仍有分析计数差异：`analysisStatus/analyzedChecks` 不能用于证明本次分析完成；上述分析结论来自逐条详情和 SQL 工作项。

## 千问恢复与用户停止

千问于北京时间 10:56:56 创建本轮，前两题分别在 10:57:18、10:58:04 接受入库。10:58:34 开始第 3 题后，10:58:38 以 `challenge_required` 停止；对应任务为 `d2f53b44-dfa7-403d-a1b5-d468639e467e`。该任务没有结果提交审计，尝试账本已有一次预留，另外 7 题尚未尝试。

用户完成手动验证后，`probe` 重新返回 `ready`。只读查看当前页面与最近对话，未见第 3 题的本轮对话；判断原暂停发生在发送前。续期仍有效的原租约版本 1，使用同一任务及已预留的一次尝试恢复正常采集，不重新领取租约、不再次登记账本；北京时间 11:06:48 接受入库，18 条引用均有标题。安全恢复证据 `qwen-resume-evidence-20261025-d2f53b44.json` 留在 aivc 目录，确认 `ledgerUnchanged=true`。

继续剩余提问后，第 4 题在北京时间 11:07:17 再次以 `challenge_required` 暂停，任务为 `2af09581-7968-4e71-8b5d-914cc0810522`，当时没有结果提交审计。用户再次手动验证并要求继续后，恢复进程的预检仍检测到验证，因此在发送前停止。再次打开专用会话后预检恢复 `ready`，沿用仍有效的原租约版本 1 和原有一次账本预留继续；北京时间 11:24:21 接受入库，恢复证据 `qwen-resume-evidence-20261025-2af09581.json` 确认 `ledgerUnchanged=true`。

第 4–6 题均实际返回相同的简短拒答，各 24 字符、无引用；逐条只读确认含明确的无法解答表述，未包含本题产品内容。已保存原始响应并完成分析，没有重发提问来替换这些样本。第 5 题还曾在领取前因 `page_contract_changed` 停止，只读复查页面规则为 `ready` 后才继续。

第 7 题 `7f798b3b-262c-4b88-8a7e-1e786d66b5bf` 再次以 `challenge_required` 停止，没有结果提交。随后准备的剩余四题进程在队首校验处以 `queue_head_unexpected` 退出，没有继续领取其他题目。用户随后明确选择“跳过千问”。已确认本轮千问采集进程全部退出，Profile/spool 锁已释放，待提交密文为 0。

停止时 API/SQL 为 6 条 `succeeded`、第 7 题 `collecting`（attempt/lease version 均为 1）、另外 3 题 `pending`（attempt 为 0）；Run 仍为 `running`，未取消或改写业务状态。第 7 题已有一次浏览器账本预留，但是否发送尚未确认；以后恢复必须先只读核对原对话，不能直接重新发送。旧租约停止续期后由既有 Worker 过期恢复规则处理，不把本轮用户跳过描述为控制面已取消。

6 条入库结果的回答记录 Check ID 集合与成功 Checks 一致，逐条分析和 `analysis_check_work` 均为 6 条完成，提交审计为 6 条 `accepted`。账本 7 条预留均对应不同任务；快照中 `browserAttemptsExactlyOnce=false` 表示未达到全部 10 题各一次的完整轮次条件，并非出现重复登记。

## 智谱既有队列阻塞

只读预检发现昨日 Run `2da2461a-d57e-412e-a335-fc52ef267242` 有 4 条 `retryable_failed`、`attempt_count=1` 的任务，均由原租约过期产生：

| 原任务 | 原 Check |
| --- | --- |
| `8c754e50-0ac4-4f37-8d75-bf3d2f47289a` | `35ac20fd-f63b-4473-9ec2-18dd3b7b7c1e` |
| `09e493d1-2f57-4850-a6ad-de07b2ca268d` | `21195c05-86d7-43f8-86c4-ba07e9bd2244` |
| `199d4a90-7705-4a68-92f0-165afd90c635` | `1ac6adc3-c20f-44b4-b226-3508e313ade4` |
| `1ca0c83a-7e0d-4f7a-9019-26585b8e266b` | `2ab4f25f-72f9-4b5b-b4bf-6a122432c20c` |

未为智谱创建本日新轮次。使用既有 `qingyan-recover-20261024.mjs sources` 只读检查首项原对话：完整提问、纯文本和 Markdown 哈希与原证据一致；来源面板声明 15 条。现有来源读取在第 14 张卡片超时，返回 `page_contract_changed`。单独的 `panel` 诊断显示该卡点击产生了初始导航请求，但没有可确认的 popup/opener/frame 关联；正文或账号校验成功不能替代该引用验证。

本次未领取这 4 条旧任务的新租约、未修改其状态或顺序、未重新发问、未把旧回答作为本日新样本，也未放宽引用规则。待现有来源读取问题处理后，先恢复旧队列，再开始新轮次。

## DeepSeek 手动验证

既有 DeepSeek 专用 Profile 的只读 `probe` 返回 `challenge_required`，因此未创建本日 Run。用户随后明确选择“暂时跳过 DeepSeek”；本次按此选择跳过，并确认专用 Profile 已无登录进程锁。以后恢复前需重新确认 `ready` 及现有智能搜索采样模式。

## 实际执行的操作与验证

- 对五个平台依次运行 Node 24 的 `apps/collector/src/login.ts probe`：文心、元宝、千问、智谱为 `ready`，DeepSeek 为 `challenge_required`。
- 本日 0600 配置和批准清单保存在既有 aivc 目录；沿用各平台原 Profile、spool、node 和密钥，保留所有历史尝试账本。
- 运行操作副本 `/private/tmp/round-driver-other-20261025.py 20261025-1792892628240 <platform>`：文心、元宝驱动退出 0；千问驱动先在第 3 题停止，原租约恢复后再次运行，在第 4 题停止，两次退出 5。副本去除了与昨日硬编码 Run 绑定的智谱恢复分支，未修改仓库业务源码。
- `/opt/homebrew/opt/node@24/bin/node --env-file=<本轮千问配置> --import tsx /private/tmp/resume-qwen-original-20261025.mjs`：第 3 题原租约恢复 `accepted`，未重复预留账本。
- `/private/tmp/continue-qwen-session-20261025.mjs`：第 4 题恢复预检为 `challenge_required`，退出 1，未发送。`continue-qwen-manual-session-20261025.mjs` 随后沿用原租约恢复第 4 题并接受提交，在第 5 题领取前因 `page_contract_changed` 退出 1。
- `/private/tmp/qwen-readiness-inspect-20261025.mjs`：只读复核授权页面及各规则，返回 `ready`，未发送提问。`continue-qwen-remaining-session-20261025.mjs` 使用同一浏览器会话和既有 `runCollector` 完成第 5、6 题，在第 7 题因 `challenge_required` 退出 1；`continue-qwen-last-four-20261025.mjs` 随后因 `queue_head_unexpected` 退出 1。
- 前两轮及千问前半段逐题运行独立进程，千问后半段复用专用会话；每个新任务均指定 `COLLECTOR_RETRY_TASK_ID`、`COLLECTOR_MAX_TASKS_PER_RUN=1`、`COLLECTOR_RUN_ONCE=true`，题间间隔 30 秒，保留正常 probe、租约、心跳、批准清单、账本与加密提交校验。
- `python3 /private/tmp/verify-domestic-round-20261025.py doubao ernie tencent_yuanbao qwen`：只读请求 Run、Checks、逐条详情、回答记录，并核对任务/分析/提交审计 SQL、Profile 尝试账本和待提交密文，得到上表结果。
- 智谱只读来源核对和单卡诊断使用既有昨日配置及恢复脚本的 `sources`/`panel` 模式，不调用发送或提交路径。
- 用户跳过千问后，`python3 /private/tmp/qwen-stop-state-20261025.py` 只读核对全部任务；进程检查确认无本轮千问采集进程。逐条 API 质量核对及账本计数得到 3 条正常回答、3 条相同短拒答、7 个不同任务且每个最多一次账本预留，安全元数据并入验证快照。

本轮为采集运行及现状记录，未修改业务代码、未执行 migration、未运行代码测试。千问专用会话已关闭，采集证据按恢复需要保留。
