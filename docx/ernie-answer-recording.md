# 文心一言网页采集说明

## 1. 本机实采结果（2026-10-22）

按用户要求，先验证文心一言，对优联科技当前启用的同一组 10 个提问各采集一次。使用既有文心登录 Profile 和 `ernie.consumer_web`，由 Jogeoai 创建任务，外部 Node.js Collector 通过浏览器采集，再提交回答与引用入库。

| 项目 | 本轮结果 |
|---|---|
| 品牌 | 优联科技（ulinktek），`szulink.com.cn` |
| 租户 / 站点 | `38685331-621e-4dc6-bcc3-d5e35703ef90` / `ded1a716-f791-491c-96e2-f737bb998960` |
| Run | `19a56c57-1cc9-4f6f-92d4-96f827213fa4` |
| 创建 / 完成时间 | 北京时间 10:29:47 / 10:39:46 |
| 任务 | 10 成功、0 失败，10 个不同提问，每个任务 `attempt_count=1` |
| 回答长度 | 838–4413 字符 |
| 引用 | 291 条 URL，覆盖 71 个域名 |
| 后台分析 | 10 条 `analysis_check_work` 全部 `completed` |
| 回答记录接口 | `totalCount=10`，全部 `succeeded/completed`，Check ID 集合与本轮完全一致 |

本轮未遇登录或验证码阻塞。当前文心适配器只上报正文与引用 URL，**不包含引用标题和搜索关键词**：291 条引用的标题为空，10 条回答的 `query_availability=unknown`。不能将其记为“没有搜索词”或声称与豆包的标题、关键词能力相同。

## 2. 本轮调通内容

- 既有文心 Profile 登录有效，当前页面为 `https://wenxin.baidu.com/` 的百度文心助手；保留可见的“快速”会话模式。这不是每条回答都发生联网搜索的证明。
- 旧启动器连接历史 API `4312`；本轮新建独立的 Jogeoai 配置，指向 `http://127.0.0.1:5260`，绑定优联租户与站点，并使用独立的文心节点和加密 spool。
- 旧页面规则复审时间为 2026-09-08，已超过 7 天。对已登录首页及既有对话进行只读检查，确认账号标记、输入框、快速模式、发送按钮、助手正文、完成标记和引用元数据；“新对话”按钮选择器已失效，更新为实页观察到的 `.new-dialog-container-plain`。新规则另存为外置 `ernie-page-contract-20261022.json`，旧文件保留。
- 复用本轮已获授权的优联 10 题批准清单，并在创建 Run 前与品牌当前启用的提问逐字核对。
- 通过带 `expectedVersion` 的目标接口启用 `domestic_collector/ernie/consumer_web/CN/zh-CN`，目标版本 11 → 12，其余目标保留。文心结果可从品牌的回答记录入口查询。
- 第一题完成并经 API/SQL 核对后再执行其余 9 题；每个进程 `COLLECTOR_RUN_ONCE=true`，保持题间间隔，沿用尝试账本，未重置 Profile 或账本，未额外重发问题。

## 3. 配置与实际验证

本机外置配置位于 `~/Library/Application Support/aivc/real-smoke/.env.real-ernie-jogeoai-ulinktek`，权限 0600；登录态、页面规则、批准清单与 spool 均在仓库外。凭据和原始响应不写入本说明。

实际执行（Collector 仓库，Node 24.22.0）：

```bash
/opt/homebrew/opt/node@24/bin/node --env-file='/Users/mac/Library/Application Support/aivc/real-smoke/.env.real-ernie-jogeoai-ulinktek' --import tsx apps/collector/src/login.ts probe
/opt/homebrew/opt/node@24/bin/node --env-file='/Users/mac/Library/Application Support/aivc/real-smoke/.env.real-ernie-jogeoai-ulinktek' --import tsx apps/collector/src/main.ts
```

- `probe` 返回 `ready`；`main.ts` 共执行 10 次，每次处理 1 题，均退出 0，结果提交均为 `accepted`。
- `GET /api/v1/visibility-runs/{runId}/checks` 返回本轮 10 条；`GET /api/v1/sites/{siteId}/monitoring/records` 按本轮时间窗、`provider=domestic_collector&engine=ernie` 查询返回 10 条，ID 集合一致，引用合计 291，分析全部完成。
- MySQL 只读核对 `visibility_runs`、`collector_tasks`、`visibility_checks`、`citations` 和 `analysis_check_work`，确认上述数量、每题一次尝试及最终状态。
- 本轮没有修改业务源码，未运行代码测试；验证依据为实际 probe、10 题采集、API 读回与 SQL 核对。

## 4. 边界与后续

- 本轮先完成文心。其他已配置国内平台尚未在本轮执行，不能据此声称已经调通。
- 文心属于网页 `domestic_collector` 通道，目前不进入每日自动监控计划；后续轮次仍需创建 Run 并启动外部采集器。
- 尝试账本限制同一绑定中的重复采集。下一轮须按已授权的新任务显式处理重复尝试，不删除账本或放宽页面、登录与验证码检查。
- 如需与豆包一样展示引用标题和搜索关键词，应另行扩展并验证文心适配器；当前没有把这些能力记为已实现。正文已改为 Markdown（见第 5 节）。

## 5. 回答格式化（2026-10-22 追加）

- **采集器**：`ErnieAdapter` 接入与豆包相同的 `readAnswerMarkdown`。完成和稳定判定仍只看纯文本；转换为空或失败时回退纯文本。文心多个正文块之间改为空行分隔（成为独立段落）。只读复审 10 条实页回答，正文里只有 `marklang` 段落类，没有来源角标，因此文心页面规则不需要 `answerIgnore`，规则文件未改动。
- **结果**：10 条回答中，Markdown 去掉语法符号后与原纯文本逐字一致（例外只有零宽字符和列表标记），文字没有增减。
- **回填（不重发提问）**：脚本以只读方式逐个打开账号里的历史对话，按采集器原读法取纯文本，去空白后必须与入库正文完全一致才读取 Markdown；按正文匹配，不依赖对话标题。随后在一个事务里按旧正文 SHA-256 逐条替换，替换前后均用 `CHECK (n = 10)` 守卫表校验，只改 `answer` 和 `updated_at`；引用、`search_queries`、`captured_at`、`result_hash` 不变，原纯文本保留在 `collector_result_submissions`（20/20 已核对），可作为回滚来源。
- **重新分析**：`POST /api/v1/sites/{siteId}/analysis-jobs`，时间窗 `02:29:00Z–03:23:00Z` 加 10 个提问 ID（窗口内只有文心、元宝这 20 条），`requested_scope_with_reuse`，复用 280、重算 20，release v6（`dbdd5f9e-…`）已发布。回答记录接口前后逐条对比：排名、提及、声量分子分母与曝光分全部不变。
- **前端**：`Jogeoai.web/src/utils/answerMarkdown.ts` 把 https 图片渲染为 `img`（`referrerpolicy="no-referrer"`、懒加载、限高），点击在新窗口打开原图；非 https 或不安全的地址仍只显示说明和链接。浏览器会直连图片所在站点，没有像站点图标那样走后端代理。3001 预览已核对标题、列表、表格、加粗显示正常，没有 `**` 或反斜杠残留。
- **验证**：Collector `vitest`（文心、元宝、DeepSeek、answer-markdown 四个测试文件共 49 个用例）、`tsc --noEmit`、所改文件 eslint/prettier 通过；前端 `tests/answer-markdown.test.ts` 13/13 与 `npm run build` 通过。后端未修改源码，未运行 .NET 测试。
