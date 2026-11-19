# 千问网页采集说明

## 1. 本机实采轮次（2026-10-22）

按用户要求，继续为优联科技（ulinktek / `szulink.com.cn`）接入千问网页采集，使用与豆包、文心、元宝相同的 10 个启用提问，各采集一次。

本轮 Run：`f021f463-d56e-494e-9cd8-689bb3b550c8`。租户：`38685331-621e-4dc6-bcc3-d5e35703ef90`；站点：`ded1a716-f791-491c-96e2-f737bb998960`。

本轮于 2026-10-22 17:36:07（北京时间）完成，17:36:44 经 API / MySQL 核对：

| 项目                 | 实际结果                                                  |
| -------------------- | --------------------------------------------------------- |
| Run / Checks / Tasks | `succeeded`，10/10 成功，0 失败，10 个不同提问            |
| 正文                 | 10 条，946–1525 字符，纯文本                              |
| 引用                 | 145 条，全部带标题，覆盖 47 个不同域名                    |
| 后台分析             | 10/10 `completed`                                         |
| 回答记录接口         | 按千问筛选读回 10 条，Check ID 集合与本轮全部 Checks 一致 |
| 搜索关键词           | 未上传，10 条 `queryAvailability=unknown`                 |

9 个任务的 `attempt_count=1`；第 7 题原租约在等待人工验证期间超时，正常重领后 `lease_version=2`、`attempt_count=2`，提交的是原对话已完成的回答。尝试账本仍为 10 条记录、10 个不同任务、10 个不同问题，未增加该题的浏览器尝试预留。最终无待提交加密 spool，采集浏览器已正常关闭，Profile 与 spool 的进程锁均已释放。

## 2. 页面与接入配置

- 复用已有的独立千问 Profile，现场确认登录有效，不导出 Cookie。页面为 `https://www.qianwen.com/`，保持可见的“快速”模式；页面显示模型名称不作为每条结果模型版本的证据。
- 旧页面规则复审于 2026-09-08，已过期。授权页面上重新检查账号、输入框、新对话、模式、发送、助手正文、完成标记和来源；新规则保存为仓库外 `qwen-page-contract-20261022.json`，保留旧规则。
- 新导航不再有旧 `#new-nav-tab-wrapper`；侧栏展开时使用 `[data-session-switch-target="new-chat"]`，收起时使用可见的 `qwpcicon-newDialogue` 工具栏按钮。侧栏内的旧按钮即使移到窗口外仍可能被判断为可见，规则须按实际工具栏可见状态选择唯一控件。两种布局均已现场验证。账号标记使用有头像的账号菜单按钮。助手容器当前带 `message-select-wrapper-answer-…`，正文仍为 `.qk-markdown`，完成时带 `.qk-markdown-complete`。
- “快速”按钮同时存在实际控件和透明测量副本，后者仍可能通过常见可见性判断。模式规则限定为 `[data-role="visible-capsule"] button[aria-label="快速"]`，探测返回 `ready` 后才启动采集。
- 来源控件位于对应助手回答内，形如“N 篇来源”；来源卡片在回答区外的独立面板中，卡片 `data-click-extra` 中已有 URL 和标题。千问适配器检查声明数量、读取网址与可选标题，并按规范化 URL 去重、对齐标题；旧侧栏、重复控件、来源数量不完整或无效 URL 均停止采集。
- 空 Slate 输入框的 `innerText` 实际可能为 `\uFEFF\n\n向千问提问`。适配器允许已复核提示文字前后的空白；其他未清空草稿仍在发送前停止。已补有换行提示、无换行提示及真实草稿拦截的回归用例。
- 第一题发送前因布局和提示文字校验停止，复核未新增历史问题后沿用原租约修正规则。首次发送又遇到平台滑块验证；用户手动完成后，原临时对话未持久化，访问原地址返回空白页，因此沿用同一已预留任务继续首题。新的回答经正文完成、原提问逐字匹配及来源校验后，以加密 spool 提交并得到 `accepted`。未重置尝试账本或重新领取该任务。外置验证规则现检查可见的 `iframe#baxia-dialog-content`，仅检查 iframe 元素，不读取或操作验证内容。
- 第二题在一次验证停止后复核未新增历史问题，沿用原租约完成采集。此后保留同一浏览器会话逐题执行。第 7、9 题触发的新验证均由用户手动完成，随后从当前原对话校验提问、正文稳定完成状态与对应来源，保存已有回答。第 7 题先只读留存 946 字符正文和 13 条带标题引用，按正常队列完成剩余未尝试任务后，再通过控制面领取该题新租约提交；未直接改写数据库的任务顺序或租约字段。第 9 题在仍有效的原租约下恢复提交。
- 复核品牌当前启用提问与 10 题批准清单逐字一致。通过带版本的目标接口启用 `domestic_collector/qwen/consumer_web/CN/zh-CN`，目标版本 14 → 15。
- 本机旧库的千问 Provider 目录名称有乱码，按已有迁移中的正确值将 `collector_platforms` 的 `code='qwen'` 一行恢复为“通义千问”，修改前保留该行元数据快照；未修改标识、入口或采集配置。

## 3. 验证入口

本机外置环境：`~/Library/Application Support/aivc/real-smoke/.env.real-qwen-jogeoai-ulinktek`，权限 0600。连接本机 5260 API、优联租户和站点，使用千问专属节点、既有 Profile 和新建的独立加密 spool。密钥、登录态和原始敏感响应不写入本文。

在相邻 `Ai-visiblility-collector` 仓库执行采集：

```bash
/opt/homebrew/opt/node@24/bin/node --env-file='/Users/mac/Library/Application Support/aivc/real-smoke/.env.real-qwen-jogeoai-ulinktek' --import tsx apps/collector/src/main.ts
```

页面探测由同一配置加载的 `QwenAdapter.probe` 实际执行，返回 `ready`。相邻 Collector 已实际执行：

```bash
/opt/homebrew/opt/node@24/bin/node node_modules/vitest/vitest.mjs run --config vitest.config.ts apps/collector/src/adapters/qwen.test.ts
/opt/homebrew/opt/node@24/bin/node node_modules/typescript/bin/tsc --noEmit
/opt/homebrew/opt/node@24/bin/node node_modules/eslint/bin/eslint.js apps/collector/src/adapters/qwen.ts apps/collector/src/adapters/qwen.test.ts
/opt/homebrew/opt/node@24/bin/node node_modules/prettier/bin/prettier.cjs --check apps/collector/src/adapters/qwen.ts apps/collector/src/adapters/qwen.test.ts apps/collector/src/adapters/fixtures/qwen-synthetic.html
```

千问定向测试 22/22、类型检查、修改文件 lint 和格式检查均通过；独立复核未发现待修问题。后端业务源码未修改，未运行 .NET 测试或全局测试。已执行仓库外 `verify-qwen-20261022.py`，最终结果为第 1 节的全轮成功状态；安全摘要保存为同目录 `qwen-verification-20261022.json`，关闭与尝试账本计数保存为 `qwen-session-close-20261022.json`，均为 0600。运行与恢复入口见相邻 Collector 的 `docs/runbooks/real-qwen-smoke.md`。

## 4. 当前边界

- 正文为纯文本，引用标题为可选元数据。无可靠搜索关键词时不上传 `searchQueries`，`query_availability=unknown`，不能推断为没有联网搜索。
- 网页通道仍需手动创建 Run 并启动外部 Collector，未纳入品牌每日自动计划。
- 每次只领取一题，题间保持间隔，沿用尝试账本。登录失效、验证码、限流或页面变化时停止，不重置账本或绕过验证；已发送的问题从已有对话检查，不为复核页面额外重发。

## 5. 本机复采轮次（2026-10-23）

在本日豆包、文心、元宝三轮完成后，按用户“继续接千问”的要求，对优联科技同一组 10 个启用问题新采一轮。创建前确认当前启用问题与批准清单逐字一致、千问无其他非终态任务、目标已启用，既有独立 Profile 的页面探测返回 `ready`。复用 2026-10-22 已复审且仍在有效期内的页面规则；本轮未修改业务源码或页面规则。

Run：`231ed328-e3d7-43b2-844d-9b5ae39083cf`；租户、站点与第 1 节相同。数据库记录的北京时间执行区间为 **09:33:26–10:30:35**。

| 项目 | 实际结果 |
| --- | --- |
| Run / Checks / Tasks | `succeeded`，10/10 成功，0 失败，10 个不同提问 |
| 正文 | 10 条纯文本，957–1647 字符 |
| 引用 | 150 条，全部带标题，覆盖 64 个不同域名 |
| 后台分析 | 当前已发布分析版本下 10/10 `completed` |
| 回答记录接口 | 本轮读回 10 条，Check ID 集合与本轮全部 Checks 一致 |
| 搜索关键词 | 未上报，10 条 `queryAvailability=unknown` |
| 尝试与租约 | 10 个任务的 `attempt_count=1`、`lease_version=1` |
| 浏览器尝试账本 | 按本轮任务筛选，10 条、10 个不同任务、10 个不同问题 |

### 人工验证与恢复

- 第 3、6、8、10 题触发平台滑块验证，均由用户手动完成。仅检查验证 iframe 元素是否可见，不读取或操作验证内容。
- 第 3、6 题的原临时对话未持久化：重新打开原地址返回 `/chat`，无问题及助手回答；历史搜索各只找到 2026-10-22 的同题旧对话。确认后沿用原任务与仍有效的租约继续采集，未使用旧轮次回答，也未重置账本或增加尝试预留。第 6 题人工等待的本地心跳阶段达到时限后，只读核对数据库租约仍有效，再经正常心跳 API 续期，租约版本保持为 1。
- 第 8、10 题从原对话恢复已有回答，核对提问、正文稳定完成状态和对应来源后提交，未重发。第 10 题页面将原提问的两处普通空格显示为不换行空格（U+00A0），核对时仅将这两处还原为空格；批准提问和回答内容未改写。
- 各结果均通过加密 spool 提交并得到 `accepted`。未直接修改数据库任务顺序、租约字段或已入库回答。

### 实际验证与关闭

本轮通过 `/api/v1/visibility-runs/{runId}/checks` 与按 `domestic_collector/qwen` 筛选的 `/api/v1/sites/{siteId}/monitoring/records` 核对上述结果；Docker MySQL 只读核对 Run、任务状态及尝试计数、引用数量/标题/域名和当前已发布分析版本的工作状态。对原 Profile 尝试账本按本轮 10 个任务 ID 计数，未混入历史轮次。

北京时间 **10:31:51** 完成关闭检查：独立 spool 的待提交 `.json.enc` 和 `.tmp` 文件为 0，采集浏览器与会话已正常关闭，Profile 和 spool 的 `.collector.lock` 均不存在，操作进程正常退出。

本轮私有配置、批准清单和安全元数据以 `20261023-1792719159011` 区分，位于仓库外 `~/Library/Application Support/aivc/real-smoke/`，权限均为 0600；最终 API 摘要、只读 SQL 和关闭检查保存于 `collection-qwen-ulinktek-20261023-1792719159011.json`。密钥、Cookie 和原始敏感响应不写入本文。

文档验证实际执行 `python3 -`，检查新增段落的验收数字、README 相对文档链接及尾空白，结果通过。本轮只操作既有采集器并更新验收文档，未运行代码测试。网页采集仍需手动发起，未纳入品牌每日自动计划。

## 6. 当日千问采集（2026-10-28，本机，已完成）

按用户“继续跑千问”的要求，对优联科技当前 10 个启用提问采集当日新一轮，沿用“采集不创建 Trellis 任务”的长期偏好。开始前只读确认千问队列无非终态旧任务、原批准清单与当前提问一致，专用 Profile/spool 空闲、页面规则仍有效，`login.ts probe` 返回 `ready`。本轮未归档或改动其他历史千问数据。

Run：`da83f919-8c4b-48ab-a9b8-fa278700f3b2`，台北时间 **11:13:06** 创建，**12:32:01** 完成，**12:32:42** 经 API / MySQL 只读核对。最终安全快照见 [本轮记录](qwen-collection-20261028.json)。

| 项目 | 最终结果 |
| --- | --- |
| Run / Checks / Tasks | `succeeded`，10/10 成功、0 失败，10 个不同提问 |
| 正文 | 10 条纯文本，996–1566 字符，未发现简短拒答 |
| 引用 | 220 条，全部带标题 |
| 后台分析 | 逐条详情与 `analysis_check_work` 均为 10 条 `completed`，分析版本 10，配置版本 7，无过期配置 |
| 回答记录 | 本轮读回 10 条，Check ID 集合与本轮完全一致 |
| 提交审计 | 10 条 `accepted` |
| 搜索关键词 | 未可靠上报，10 条 `queryAvailability=unknown` |
| 浏览器尝试账本 | 10 条、10 个不同任务、10 个不同问题，每题一次预留 |
| 控制面尝试 | 第 4 题 `attempt_count=3`、租约版本 3；其余 9 题均为 1 |

Run 摘要中的兼容字段仍返回 `analysisStatus=not_started/analyzedChecks=0`，与逐条真实分析状态不一致；本轮分析完成结论依据逐条详情及 `analysis_check_work`，快照保留原始字段并标注该边界。

### 人工验证与恢复

- 第 3、4、7、10 题触发人工验证，第 4 题在接续时再次触发，全部由用户手动完成。仅检查验证 iframe 是否可见，不读取或操作验证内容。
- 第 3、7、10 题验证后，原临时地址重新打开均回到 `/chat`，无用户消息、助手回答或匹配提问。保留空白证据后，沿用仍有效的原租约版本 1 和既有尝试预留继续采集。第 7 题直接入库；第 3、10 题生成回答后在最终页面复检暂停，再从原对话恢复已有回答。
- 第 4 题 `655568b4-c000-455f-92da-4c13d3574249` 两次验证后均只读确认原临时对话为空。配置租约为 900 秒；原租约在 11:48:18 超时，其他 9 题完成后正常重领为版本 2，第二次等待人工验证时又在 12:18:51 超时。12:31 经正常控制面队列重领为版本 3，沿用一次浏览器尝试预留完成采集，12:32:01 提交获 `accepted`：1309 字符正文、28 条带标题引用，账本哈希未变。未直接修改数据库任务顺序或租约字段。
- 第 1、3、5、8、10 题在 `page_contract_changed` 后，从已生成的原对话恢复入库。逐题核对完整带前缀提问、完成标记、稳定正文 SHA-256 和完整来源，分别为 21、17、15、20、28 条带标题引用；均通过原租约版本 1、正常心跳、既有加密 spool 和提交 API 获 `accepted`，恢复时没有发问，账本哈希未变。
- 第 2 题曾在领取前探测暂停，任务仍为 `pending/0`，只读复探为 `ready` 后正常采集；第 6、9 题正常入库。瞬时页面检查失败的具体原因尚未确定，本轮未修改采集器源码或放宽来源完整性要求。

### 实际验证与关闭

实际执行以下入口，并只读核对 Chrome History、任务、分析工作和提交 SQL；恢复前空白对话证据、原文哈希和恢复结果保存在 `/private/tmp/aivc/qwen-20261028/`。命令中的私有环境文件未写入文档。

- Node 24 加载既有私有配置，执行 `apps/collector/src/login.ts probe/login`、`inspect-original.mjs`、`recover-original.mjs` 和 `resume-unsent.mjs`；探测为 `ready`，恢复结果均为 `accepted`。
- `/opt/homebrew/opt/node@24/bin/node /private/tmp/aivc/qwen-20261028/round.mjs run`：完成正常队列采集；遇验证或页面检查即暂停，没有重发已有回答。
- `/opt/homebrew/opt/node@24/bin/node /private/tmp/aivc/qwen-20261028/round.mjs status`：最终核对本节全部数字，10 个成功 Check ID 与回答记录完全一致，尝试账本每题一次。
- `python3 -`：用 `urllib.request` 检查 `/health/ready`，返回 HTTP 200 / `Healthy`；`lsof -nP -iTCP:5260 -sTCP:LISTEN -Fpc` 确认 API 监听；`ps -axo pid=,comm=` 独立确认 Worker 进程运行，另查 Node 进程确认采集 / 登录进程为 0。
- 最终 Profile 锁不存在，spool 只剩 `.binding`，无待提交密文或临时文件；采集会话正常关闭，既有 API 和 Worker 继续运行。关闭证据保存为同目录 `session-close.json`，安全字段已并入本轮记录。
- 文档核对执行 `python3 -`，检查最终快照的 10/10 状态、220 条带标题引用、10 条分析完成、账本唯一性、README 数字和文档链接及尾空白，结果通过。

本轮未创建 Trellis 任务、未修改业务源码、未执行 migration 或运行代码测试。网页通道仍需手动发起，未纳入品牌每日自动计划。
