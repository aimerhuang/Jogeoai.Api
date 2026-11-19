# DeepSeek 网页采集说明

> 2026-10-23 起另有官方 API 通道 `deepseek_api`，支持每日自动监控，见 [DeepSeek 官方 API 回答采集说明](deepseek-api-answer-recording.md)。两条通道在回答记录里共用「DeepSeek」页签。

## 1. 本机实采轮次（2026-10-22）

按用户要求，以 DeepSeek 网页版「智能搜索」模式（「深度思考」关闭）对优联科技当前启用的 10 个提问各采集一次。使用既有 `deepseek-cn` 登录 Profile（打开时已是登录状态，模式已经是“智能搜索开、深度思考关”，用户无需额外操作）和 `deepseek.consumer_web`。Jogeoai 创建任务，外部 Node.js Collector 采集后提交到本机 5260 API。

| 项目 | 结果 |
|---|---|
| 品牌 | 优联科技（ulinktek），`szulink.com.cn` |
| 租户 / 站点 | `38685331-621e-4dc6-bcc3-d5e35703ef90` / `ded1a716-f791-491c-96e2-f737bb998960` |
| Run | `ed7d43ec-2bf4-4cd1-89ea-1bbc1c958a3d` |
| 创建 / 完成时间 | 北京时间 13:02:11 / 13:25:12 |
| Run / 任务 | `succeeded`；10 成功、0 失败；10 个不同提问，每个任务 `attempt_count=1` |
| 回答 | 816–1702 字符，10 条均为 Markdown（标题、列表、加粗），不含思考过程和行内引用角标 |
| 引用 | 149 条 URL（每题 14–15 条），60 个域名，149 条全部带标题 |
| 搜索词 | 未上报；10 条 `query_availability=unknown` |
| 后台分析 | 10 条 `analysis_check_work` 全部 `completed` |
| 回答记录接口 | `totalCount=10`，全部 `succeeded/completed`，Check ID 集合与本轮一致 |

本轮没有遇到登录、验证码或限流阻塞。

## 2. 本轮调通内容

- **页面规则复审**：旧规则 `deepseek-page-contract.json`（2026-09-10）已过期，而且英文输入框 placeholder 已改为中文；旧规则还把“开启新对话”按钮 `._5a8ac7a` 同时当作登录标记。本轮通过本机 CDP 只读连接到已打开的窗口，在一条已有对话上复审，新规则另存为仓库外的 `deepseek-page-contract-20261022.json`，旧文件保留。
  - 登录标记改为左下角用户头像；新建对话为 `._5a8ac7a:has-text("开启新对话")`；输入框为 `textarea[placeholder^="给 DeepSeek 发送消息"]`。
  - 采样模式为新增的 `web_search_conversation`。`conversationActive` 断言「智能搜索」被选中且「深度思考」未被选中；模式不符时，在发问前就以 `page_contract_changed` 停止。适配器不会自己切换模式。
  - 正文为 `.ds-assistant-message-main-content`；思考和搜索过程位于独立的 `.ds-think-content`，不进入正文。
  - 行内引用角标结构为 `span[data-citation-group] > a[href] > span.ds-markdown-cite`。引用选择器取其中的链接；`answerIgnore` 剔除整个 `[data-citation-group]`，因此 Markdown 里没有角标，也没有空链接。
  - 拦截类选择器（登录、地区、验证码、限流，含“服务器繁忙，请稍后再试”）改为整段文字锚定匹配，避免被回答正文误触发。
- **来源读取**（Collector `deepseek.ts` 的 `readDeepSeekSources`）：回答页脚显示“N 个网页”。适配器点击它，等待「搜索结果」侧栏正好列出 N 张 `a[href]:has(.search-view-card__title)` 卡片，读取公开 URL 和标题，然后关闭侧栏。数量不符、来源不公开、侧栏已经打开时一律停止，不伪造来源，也不把未加载完的来源当作 0 条。来源超过 100 条时直接拒绝而不截断（Jogeoai 入库上限）。没有搜索的回答（没有页脚数量，也没有行内引用）不提交来源。
- **正文格式**：接入与豆包、文心、元宝相同的 Markdown 转换（见父任务 `10-22-domestic-collector-deepseek-markdown`）。
- **发问前的实页空跑**：在已有对话上只读执行来源读取和 Markdown 转换，结果为 70/70 条来源带标题，8 个行内引用排在最前，侧栏已关闭，Markdown 中没有角标残留。
- **Jogeoai 配置**：通过带 `expectedVersion` 的 `PUT /api/v1/sites/{siteId}/targets` 启用 `domestic_collector/deepseek/consumer_web/CN/zh-CN`，目标版本 13 → 14，原有 8 个目标及其 `targetId` 不变。创建 Run 前，复用的批准清单 `approved-questions-ulinktek-20261022-094626.json` 与品牌当前启用的 10 个提问逐字一致；Run 的 10 个任务快照也与清单一致。
- **执行**：先采第 1 题，经 API、SQL 和 3001 预览核对，并经用户确认后，逐个进程采集其余 9 题。中途出现一次执行脚本错误：命令行覆盖了 `COLLECTOR_MAX_TASKS_PER_RUN=1`，而这个值在真实模式下同时是该绑定的终身账本上限，账本里已有 1 条，因此启动前检查以 `task_not_approved` 停止。这次停止发生在租用任务之前，没有消耗 Jogeoai 尝试次数，也没有发问。去掉覆盖、只保留 `COLLECTOR_RUN_ONCE=true` 后，9 题依次完成。

## 3. 配置与验证

本机外置配置 `~/Library/Application Support/aivc/real-smoke/.env.real-deepseek-jogeoai-ulinktek`（0600），以元宝配置为模板，修改平台、Profile、节点 ID（`deepseek-cn-jogeoai`）、独立 spool `spool-deepseek-ulinktek-20261022`、新生成的 spool 密钥和 `DEEPSEEK_PAGE_CONTRACT_PATH`。密钥、Cookie 和原始响应不写入文档。

实际执行（相邻 Collector 仓库，Node 24.22.0）：

```bash
/opt/homebrew/opt/node@24/bin/node --env-file='/Users/mac/Library/Application Support/aivc/real-smoke/.env.real-deepseek-jogeoai-ulinktek' --import tsx apps/collector/src/login.ts probe
COLLECTOR_RUN_ONCE=true /opt/homebrew/opt/node@24/bin/node --env-file='/Users/mac/Library/Application Support/aivc/real-smoke/.env.real-deepseek-jogeoai-ulinktek' --import tsx apps/collector/src/main.ts
/opt/homebrew/opt/node@24/bin/node node_modules/vitest/vitest.mjs run apps/collector/src/adapters/{ernie,yuanbao,deepseek,answer-markdown}.test.ts
/opt/homebrew/opt/node@24/bin/node node_modules/typescript/bin/tsc --noEmit
```

- `probe` 返回 `ready`。10 个采集进程均退出 0，各提交 1 条 `accepted`。第 1 题使用了 `COLLECTOR_MAX_TASKS_PER_RUN=1`，当时账本为空，所以正常完成。
- Collector 定向测试 54/54 通过（DeepSeek 9 个，新增智能搜索的来源读取、数量不符、缺少页脚、未搜索、模式断言等用例，使用按实页结构编写的合成 fixture `deepseek-search-synthetic.html`），`tsc --noEmit`、所改文件的 eslint/prettier 均通过。因为改动了共享的 `consumer-web.ts`（只给采样模式枚举加了一个值），又跑了整个 adapters 目录：172 个用例中 1 个失败，是智谱清言 `qingyan-sources.test.ts` 的 “preserves the initial target rather than its redirect destination”。把枚举改回原样后同样失败，说明这是原本就存在的问题，与本轮无关，未处理。
- `GET /api/v1/visibility-runs/{runId}/checks` 与按本轮时间窗、`provider=domestic_collector&engine=deepseek` 查询的 `GET /api/v1/sites/{siteId}/monitoring/records` 都读回 10 条，Check ID 一致。MySQL 只读核对 `visibility_runs`、`collector_tasks`、`visibility_checks`、`citations`、`analysis_check_work`，结果与上表一致。核对结果保存在仓库外的 `deepseek-20261022/deepseek-verification-20261022.json`。
- 3001 预览中出现「DeepSeek」页签，第 1 题详情按标题、列表、加粗分层显示，显示“参考 14 篇资料”，没有残留 `**`。
- 后端未修改源码，未运行 .NET 测试。

## 4. 当前边界

- 「智能搜索」由平台自行决定是否搜索、搜索几轮；`web_search_conversation` 只代表批准的采样模式，不证明每条回答都经过联网搜索。本轮 10 条都带来源。
- 搜索关键词在思考区可见，但本轮未解析、未上报，记为 `unknown`。
- 页脚和部分选择器使用的是哈希类名（`.f93f59e4`、`._5a8ac7a`、`img.fdf01f38`），页面改版后需要重新复审；规则 7 天后过期。
- 网页 `domestic_collector` 通道仍需手动创建 Run 并启动外部采集器，未接入每日自动监控。账本按绑定累计，目前已用 10/10；以后再对同一组提问采集，需要按授权显式重试，或新建绑定（例如新的 spool 或项目配置），不能删除账本。
