# 智谱清言网页采集与回答格式

## 本轮范围（2026-10-23）

用户要求采集智谱回答并保留文字排版。本轮对优联科技当前启用的同一组10个问题各采一次，控制面为本机5260 API。

- 租户：`38685331-621e-4dc6-bcc3-d5e35703ef90`。
- 站点：`ded1a716-f791-491c-96e2-f737bb998960`。
- 平台：`zhipu_qingyan` / `zhipu_qingyan.consumer_web`，Provider 为 `domestic_collector`。
- Run：`84c9650d-0060-449b-bac5-7e074fa7f671`。
- 最终结果：10/10成功、140条引用URL（98个域名）、10条后台分析完成；2026-10-23 17:11（北京时间）验收通过。

## 正文格式

相邻 Collector 的 `QingyanAdapter` 自本轮起接入既有 `readAnswerMarkdown`。保存标题、段落、粗体、编号/无序列表、表格和链接；多个可见正文块之间使用空行。页面规则将思考正文（`.dr_margin_botttom`）、图示和来源摘要排除在正文之外；`answerIgnore=.source-item` 去掉行内引用角标，来源 URL 仍由独立来源读取器采集。转换为空或失败时沿用原纯文本，不补造结构。前端现有 `AnswerBody.vue` 通过 `renderAnswerMarkdown` 将正文渲染为段落、标题、列表和表格；本次无需修改前端或重新生成类型。

本轮使用2026-10-23已实现的统一问题前缀，每次先新建对话，再发送指定说明、空行与原问题。任务快照、批准清单和账本哈希继续使用原问题。前缀不能证明清除了账号记忆，联网依据来自平台可观察来源。

## 页面复审与操作

- 用户允许将不存在的PID90266对应的遗留锁改名备份，随后手动完成智谱登录；登录资料及既有尝试账本保留。
- 复审当前页面显示的“GLM-Flash 快速”，外置 `conversationActive` 同时匹配两个标签；此标签不证明每条回答实际模型版本。
- 在一条原历史回答上，只读验证 Markdown 提取保留4个标题、11个列表项及12行表格；来源侧栏28条URL读取成功。该历史回答仅用于规则验证，没有作为本轮样本提交。
- 新规则、批准清单、节点和加密spool独立保存于 `~/Library/Application Support/aivc/real-smoke/`，正式probe返回ready后才创建Run。品牌智谱目标从配置版本16升级到17，其余目标完整保留；网页通道仍手动采集。
- 领取前逐个只读核对任务所属Run且attempt_count=0；每进程最多1题，显式限定本题ID，题间至少30秒。失败即停，已发送题不自动重发。
- 首次启动的新spool key编码不符合32字节Base64要求，配置校验阶段退出；当时10任务均pending且attempt_count=0，spool为空。修正配置并验证通过后才开始真实发问。

## 验证记录

代码范围仅为相邻Collector的 `qingyan.ts`、`qingyan.test.ts` 和智谱运行说明。69/69定向测试通过（智谱13、来源35、共享Markdown21）；TypeScript、所改TS的ESLint及3文件Prettier通过；独立审查无遗留问题。正式应用前核对源文件基线，应用后3文件SHA-256与已验证副本一致。

精确命令及环境记录见 `.trellis/tasks/10-23-qingyan-markdown-collection/` 的 implement-report.md、check-report.md、source-application.json。未改后端业务源码、公开DTO或数据库，未运行.NET、全局或数据库测试，未回填历史回答。

## 真实采集验收

本轮于北京时间16:14创建，17:10完成，17:11验收。API Run为`succeeded`，10个Check全部成功，失败0；按智谱筛选的回答记录为10条，Check ID集合与本轮完全一致。当前启用问题与批准清单逐字一致，10条`promptSnapshot`仍是原问题，不含发送前缀。

| 项目 | 实测结果 |
| --- | --- |
| 回答长度 | 2447–3883个Unicode字符 |
| 每条段落块 | 10–31 |
| 每条标题 | 3–10 |
| 每条列表项 | 6–34 |
| 每条表格行 | 6–30（含表头与分隔行） |
| 引用 | 140条URL，覆盖98个域名；本通道未上传标题 |
| 搜索关键词 | 未上报，`queryAvailability=unknown` |
| 后台分析 | 回答记录接口及`analysis_check_work`均为10条`completed` |
| 浏览器发问 | 10个不同任务/原问题，账本每题1次 |
| 控制面领取 | 第9题2次，其余9题各1次 |
| 收尾状态 | 本平台非终态队列为空；加密spool无待提交文件；Profile与spool锁均已释放 |

### 第9题恢复

第9题首次采集以`page_contract_changed`停止，保留已生成的原对话。只读捕获时核对了唯一用户消息与完整发送内容一致、唯一助手、完成标记、无生成中/阻塞、正文稳定，并保存原问题、纯文本和Markdown的SHA-256证据。随后先采集尚未发送的第10题。

恢复时，原对话直链未通过页面就绪检查；改从站内历史进入后，一次来源读取仍触发结构校验。最终在保持原有来源读取器和全部校验的情况下，读取到15条来源，重新核对原对话ID、完整问题、双正文哈希和来源读取后的正文稳定性，再正常重领第9题过期租约并提交。恢复前后账本逐字不变；最终入库Markdown的SHA-256与捕获证据一致。第9题控制面`attempt_count=2`、`lease_version=2`，浏览器仍仅发问1次。未确定之前结构校验停止的具体根因，未放宽校验或修改来源实现。

非敏感逐条验收数据见 [live-verification.json](../.trellis/tasks/10-23-qingyan-markdown-collection/live-verification.json)，操作与验收命令见同任务的 [acceptance-report.md](../.trellis/tasks/10-23-qingyan-markdown-collection/acceptance-report.md)。会话链接、登录资料、批准清单、完整账本与本轮外置配置保留在原私有目录。Run汇总中的旧分析常量不作为完成证据；本轮以回答记录和只读SQL为准。
