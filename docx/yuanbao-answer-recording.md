# 腾讯元宝网页采集说明

## 1. 本机实采轮次（2026-10-22）

按用户要求，对优联科技当前启用的同一组 10 个提问各采集一次。使用腾讯元宝网页的“快速回答”模式、独立持久化 Profile 和 `tencent_yuanbao.consumer_web`。用户完成微信或手机登录后继续执行；Jogeoai 创建任务，外部 Node.js Collector 采集并提交到本机 5260 API。

本轮 Run 为 `f7d6bcaf-a645-4d3d-8f57-9263456385a9`，租户为 `38685331-621e-4dc6-bcc3-d5e35703ef90`，站点为 `ded1a716-f791-491c-96e2-f737bb998960`（优联科技 / `szulink.com.cn`）。

| 项目 | 最终结果 |
|---|---|
| 创建 / 完成时间 | 北京时间 10:56:45 / 11:22:22 |
| Run / 任务 | `succeeded`；10 成功、0 失败 |
| 提问与尝试 | 10 个不同提问，每个任务 `attempt_count=1` |
| 回答长度 | 630–2557 字符 |
| 引用 | 263 条 URL，139 个域名；262 条有标题、1 条标题为空 |
| 后台分析 | 10 条 `analysis_check_work` 全部 `completed` |
| 回答记录接口 | `totalCount=10`；全部 `succeeded/completed`，Check ID 集合与本轮完全一致 |
| 搜索词 | 未上报；10 条 `query_availability=unknown` |

## 2. 本轮调通内容

- 复用已批准的优联 10 题清单，创建前与品牌当前启用的提问逐字核对。通过带版本的目标接口启用 `domestic_collector/tencent_yuanbao/consumer_web/CN/zh-CN`，目标版本 12 → 13。
- 旧页面规则已过期，账号、“新建对话”、回答区及引用布局也已变化。本轮在授权登录页和第一题实际回答上复核，新规则另存为仓库外的 `yuanbao-page-contract-20261022.json`，保留旧文件。
- 实页以 `.agent-chat__list__item--ai` 限定助手回答，正文为 `.hyc-common-markdown`；等待正文出现、文本稳定、生成结束，并要求回答内有“重新生成”完成标记。
- 元宝的“源”控件带有“引用 N 篇资料作为参考”的可访问标签，引用在回答区外的独立侧栏。元宝适配器按声明数量检查侧栏来源卡片，从 `data-url` 读取公开网址，从标题节点读取可选标题；拒绝不完整、旧侧栏或非公开来源，不把尚未加载的来源当成零条。
- 第二题首次采集因侧栏关闭规则错误而停止：外层关闭容器比实际按钮宽，点击容器中心不会关闭侧栏。修正为点击其内部 `button`，并把这一结构加入合成测试。随后从匹配的历史对话读取第二题现有回答，以原有效租约提交，未重发问题。
- 第一题现场复核后直接保存现有回答；第二题按上述方式恢复；第三至第十题通过正常采集流程串行执行。保留尝试账本与加密 spool，每次启动前检查下一个任务属于本轮且尝试次数为零。

## 3. 配置与验证

本机外置配置：`~/Library/Application Support/aivc/real-smoke/.env.real-yuanbao-jogeoai-ulinktek`（0600）。它指向本机 5260 API、优联租户与站点，并使用独立元宝节点、Profile 和加密 spool；不将密钥、Cookie 或原始敏感响应写入文档。

实际执行（相邻 Collector 仓库，Node 24.22.0）：

```bash
/opt/homebrew/opt/node@24/bin/node --env-file='/Users/mac/Library/Application Support/aivc/real-smoke/.env.real-yuanbao-jogeoai-ulinktek' --import tsx apps/collector/src/login.ts probe
/opt/homebrew/opt/node@24/bin/node --env-file='/Users/mac/Library/Application Support/aivc/real-smoke/.env.real-yuanbao-jogeoai-ulinktek' --import tsx apps/collector/src/main.ts
/opt/homebrew/opt/node@24/bin/node node_modules/vitest/vitest.mjs run apps/collector/src/adapters/yuanbao.test.ts
/opt/homebrew/opt/node@24/bin/node node_modules/typescript/bin/tsc --noEmit
/opt/homebrew/opt/node@24/bin/node node_modules/eslint/bin/eslint.js apps/collector/src/adapters/yuanbao.ts apps/collector/src/adapters/yuanbao.test.ts
/opt/homebrew/opt/node@24/bin/node node_modules/prettier/bin/prettier.cjs --check apps/collector/src/adapters/yuanbao.ts apps/collector/src/adapters/yuanbao.test.ts apps/collector/src/adapters/fixtures/yuanbao-synthetic.html docs/runbooks/real-yuanbao-smoke.md
```

- `probe` 返回 `ready`。元宝定向测试最终 9/9 通过，覆盖侧栏网址与标题、侧栏关闭、缺失来源、非公开来源、旧侧栏和正文延迟渲染。类型检查、所改 TypeScript 文件 lint、所改文件格式检查通过。
- 第一、第二题各提交一次并获 `accepted`；第三至第十题的 8 次正常采集进程均退出 0、各处理 1 题、提交均为 `accepted`。第二题此前的采集进程以 `page_contract_changed` 停止，不能记为该进程成功。
- 最终实际执行 `GET /api/v1/visibility-runs/{runId}/checks` 和按本轮时间窗、`provider=domestic_collector&engine=tencent_yuanbao` 筛选的 `GET /api/v1/sites/{siteId}/monitoring/records`，各读回 10 条且 Check ID 集合一致、引用合计 263、分析均完成。MySQL 只读核对 `visibility_runs`、`collector_tasks`、`visibility_checks`、`citations` 和 `analysis_check_work`，确认上述最终状态、数量与每题一次尝试。
- 合成测试显式声明 UTF-8；缺少声明会把路由返回 HTML 中的中文可访问标签解码错误，导致测试未执行预期来源分支。合成页面不是实页验证的替代。
- 本轮代码仅修改 Collector 元宝适配器及对应测试 / fixture；后端仅同步说明，不运行 .NET 测试，也没有运行全局测试套件。

## 4. 当前边界

- 正文自 2026-10-22 起为 Markdown（见第 5 节）。当前元宝页面未可靠暴露搜索关键词，未上报 `searchQueries`，应记为 `query_availability=unknown`。
- 标题是附加元数据；超过接口 512 字符限制、不可读或格式无效时保留 URL、标题置空，不伪造标题。
- 网页 `domestic_collector` 通道仍需手动创建 Run 并启动外部采集器，未接入品牌每日自动监控计划。
- 本轮只验证本机这一品牌、这组问题及当前授权账号。页面变化、登录失效、验证码或限流仍会停止采集，需要按实际状态处理，不绕过验证或删除尝试账本。

## 5. 回答格式化（2026-10-22 追加）

- **页面结构**（只读复审 10 条实页回答）：列表项带 `.ybc-li-component__dot-wp` 圆点；正文内嵌来源角标 `.hyc-common-markdown__ref-list`；5 条回答含京东商品卡 `.hyc-common-markdown__goods-card-v2`（图片、标题、标签、价格、已售、店铺、“查看更多商品”按钮，没有商品链接）；1 条含“相关视频”卡 `.ybc-chat-videoBoxV2-bigCard-wrapper`（封面、标题、来源账号、时长、发布时间）。`.hyc-common-markdown__url-link__trigger` 只是带样式的品牌或域名文字，作为正文保留。
- **页面规则**：新文件 `yuanbao-page-contract-20261022b.json`（旧文件保留）新增 `answerIgnore`：`.ybc-li-component__dot-wp, .hyc-common-markdown__ref-list, .hyc-common-markdown__goods-card-v2, [class*="ybc-chat-videoBoxV2-bigCard"]`。外置配置 `.env.real-yuanbao-jogeoai-ulinktek` 已改为指向新文件（修改前已备份），`probe` 返回 `ready`。
- **采集器**：`readYuanbaoAnswerMarkdown` = 正文 Markdown + `---` + “**相关商品**”“**相关视频**”两节，按用户要求把卡片附在正文下方。每张卡片包含 https 图片（括号会被百分号编码）、加粗标题、标签行，以及价格 · 已售 · 店铺（视频卡为时长 · 来源 · 发布时间）；卡片文字做 Markdown 转义。任一卡片缺少标题时整条回答回退为纯文本（纯文本里仍有卡片文字），不会丢失信息。页面内读取使用对象字面量方法，避开 tsx `__name` 包装；回填脚本在 tsx 下实际运行过。
- **结果**：10 条回答中，除“查看更多商品”按钮文字、`∙` 圆点、零宽字符外，原纯文本内容全部保留；商品卡和视频卡中的品牌（REEDEA、珀瑞克、量界、优利德、泰视朗、PQWT、RIDGID、源恒通、深圳唯科等）仍在正文中，所以提及统计不变。
- **回填（不重发提问）**：脚本以只读方式逐个打开账号里的历史对话，按采集器原读法取纯文本，去空白后必须与入库正文完全一致才读取 Markdown；按正文匹配，不依赖对话标题。随后在一个事务里按旧正文 SHA-256 逐条替换，替换前后均用 `CHECK (n = 10)` 守卫表校验，只改 `answer` 和 `updated_at`；引用、`search_queries`、`captured_at`、`result_hash` 不变，原纯文本保留在 `collector_result_submissions`（20/20 已核对），可作为回滚来源。
- **重新分析**：`POST /api/v1/sites/{siteId}/analysis-jobs`，时间窗 `02:29:00Z–03:23:00Z` 加 10 个提问 ID（窗口内只有文心、元宝这 20 条），`requested_scope_with_reuse`，复用 280、重算 20，release v6（`dbdd5f9e-…`）已发布。回答记录接口前后逐条对比：排名、提及、声量分子分母与曝光分全部不变。
- **前端**：`Jogeoai.web/src/utils/answerMarkdown.ts` 把 https 图片渲染为 `img`（`referrerpolicy="no-referrer"`、懒加载、限高），点击在新窗口打开原图；非 https 或不安全的地址仍只显示说明和链接。浏览器会直连图片所在站点，没有像站点图标那样走后端代理。3001 预览已核对标题、列表、表格、加粗显示正常，没有 `**` 或反斜杠残留。
- **验证**：Collector `vitest`（文心、元宝、DeepSeek、answer-markdown 四个测试文件共 49 个用例）、`tsc --noEmit`、所改文件 eslint/prettier 通过；前端 `tests/answer-markdown.test.ts` 13/13 与 `npm run build` 通过。后端未修改源码，未运行 .NET 测试。
