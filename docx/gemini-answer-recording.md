# Gemini 回答与引用来源记录

更新：2026-10-11。以下单次采集结果与代码验证发生于 2026-10-08：021 已经明确授权部署到本机 `jogeoai`，优联科技 5 个已保存问题的真实 Gemini 采集、分析和 API 回查全部通过。代码阶段 169 项定向测试通过；限定消费与列表查询修复追加 87 项定向测试通过，编译 0 警告/0 错误。6 项隔离 SQL 用例仍未执行，不将本次实测等同于全部数据库场景验收。详见 [真实验收](../.trellis/tasks/10-08-gemini-answer-citations/research/live-acceptance-20261008.md)、[独立回查](../.trellis/tasks/10-08-gemini-answer-citations/research/live-check-20261008.md)；代码阶段历史证据另见 [实施验证](../.trellis/tasks/10-08-gemini-answer-citations/research/implementation-validation.md) 与 [检查记录](../.trellis/tasks/10-08-gemini-answer-citations/research/check-report.md)。最新每日轮次与服务状态见 [本机状态核对](local-state-verification.md)。

## 本次真实结果

Run：`d73385de-fabd-4125-872d-d40f9287d2ad`。北京时间 2026-10-08 14:32:32 受理、14:36:26 完成；每个问题仅采样一次，无失败重采。完整问题、回答正文、引用链接及 Check/外部任务 ID 见 [真实回答与引用](../.trellis/tasks/10-08-gemini-answer-citations/research/live-answers-20261008.md)。

| 问题简述 | 采集 | 已保存引用 |
| --- | --- | --- |
| 市政管道检测的靠谱摄像头厂家 | 成功 | 3 |
| 带米数计数、定位堵塞的高清系统 | 成功 | 5 |
| 支持高清成像和精准定位的厂家 | 成功 | 14 |
| 建筑团队适用的 Wi-Fi 下水道摄像头 | 成功 | 22 |
| 建筑运维团队适用的市政工业检测设备 | 成功 | 3 |

共收到 98 条候选来源，按现役安全规则过滤 51 条、保留 47 条；5 条均明确返回 `available/partial`，不是全部来源完整保留。正文均未截断、未脱敏替换，具体模型版本均未提供。5 条分析均完成，结果为未提及品牌；这不等于采集失败。

实测发现监控列表对正文和引用展开后 `GROUP BY c.answer` 触发 MySQL 排序内存错误；已改为按 Check 独立计数引用，未修改全局数据库参数。修复后列表和同快照详情为 200，正文 SHA256、来源及顺序、质量信息与数据库和 Check API 一致。旧 65 个 Check/任务及 9 个时隙摘要不变；仅增加 1 Run、5 Check、47 引用和 5 条提供方用量。项目积分仍为 500，预留 0。

已知未修复项：`VisibilityRunSummary` 的 `analysisStatus`/`analyzedChecks` 当前写死为 `not_started`/0，不能用这两个 Run 汇总字段判断真实分析进度；本批已完成证据来自监控列表、详情和数据库。2026-10-08 恢复 Worker 时还观察到知识库空队列的连接复用错误，当时未扩大修改；2026-10-11 当前源码已先关闭 reader 再回滚，并新增 SQL 回归用例，但本轮未验证其 SQL 执行和 Worker 运行结果，详见 [本机状态核对](local-state-verification.md)。回答/引用链路通过不能推导所有后台模块或 Run 汇总字段已完善。

## 功能边界

只针对用户已保存、启用且未归档的问题采集 Gemini；品牌研究的未选候选不会自动产生检测和费用。复用 Cloro 网页采集与现有 Run/Check/引用表，不接 Google 官方 Gemini API，不创建新前端页面。

Gemini 按品牌显式启用，新品牌的默认监控目标仍仅 ChatGPT。已有启用配置保留；新增目标进入未来每日监控，不补启用前轮次，不通过登录、GET 或目标配置更新触发额外采集。暂停计划和自定义计划保留原边界，已经冻结的时隙和结果不改写。

## 接入现有接口

1. 用 `GET /api/v1/sites/{siteId}/targets` 获取目标和配置版本。
2. 有写权限的成员通过 `PUT` 同一路径提交完整目标集合与 `expectedVersion`，保留原有目标并显式启用 Gemini。该接口是全量替换，不是局部 PATCH；viewer 无权写入。
3. 已有自动监控计划由后续每日时隙读取当前有效选择，不要求再次登录。没有计划时继续使用现有 `POST /api/v1/sites/{siteId}/prompt-monitoring` 初始化流程，只有首次建计划适用现役首轮规则。
4. 用现有 Run/Check 查询跟踪采集进度。监控记录按 Gemini/问题/时间筛选，回答详情使用列表返回的同一快照；采集成功与后续品牌分析完成分开。

自动监控费用沿用产品现役合同：每个问题 × 目标 × 采样 10 积分。5 个问题、ChatGPT 与 Gemini 两个目标、单采样预计每轮 100 积分。手动 `POST /api/v1/visibility-runs`（`targets` / `targetIds` / `previewId` 形态）自 2026-11-27 起同样传 `ChargeMonitoringCredits=true`，按同价预占项目积分，余额不足返回 402 `CREDIT_INSUFFICIENT` 且不创建运行（Trellis `11-27-sec-manual-run-charging`，代码已实现，本机 API / Worker 尚未用该构建重启；规则见[品牌监控配置说明](brand-monitoring-config.md) §4、§9）。Gemini 接入当期（2026-10-08）没有改变两类入口的计费行为：当时手动入口沿用 `ChargeMonitoringCredits=false`，不扣项目积分，但有真实 Cloro 调用消耗，本文记录的手动实测属于这一时期。这不是 Cloro 对外价格；已受理的自动监控任务失败不承诺自动退款。

## 结果质量

新增 `resultQuality` 为安全的结构化说明，不是原始提供方响应。历史 null 或不支持的版本表示质量未知，不回填旧回答或引用。

| 字段 | 含义 |
| --- | --- |
| `schemaVersion` | 本期版本 1 |
| `answerTruncated` | 正文是否因 1 MiB 入库上限截断，与列表摘要截断不同 |
| `answerRedacted` | 正文是否发生安全替换 |
| `modelVersionProvided` | 是否实际获得具体版本；引擎名回退不视为模型版本 |
| `citations.availability` | `available` 收到来源；`empty` 明确空数组；`not_exposed` 未暴露字段；`unknown` 无法可靠解释 |
| `citations.completeness` | `complete`、`partial` 或 `unknown`；只说明本系统对收到来源字段的保留情况，不保证提供方抓全网页 |
| `citations.*Count` | 接收、保留、安全过滤、无效及超限数量；未知值不伪造为零 |
| `citations.reasons` | 有界固定原因代码，不携带敏感正文、链接或异常栈 |

引用仅来自提供方暴露的来源，不从正文任意链接或另行搜索补造。同 URL 在不同保存位置仍可出现；统计层去重口径不变。最多保留 100 条安全引用，并标记超过上限的情况。全部来源被安全过滤也不等于“Gemini 明确没有引用”。

列表只为分页选中的 Check 加载质量数据；详情与质量遵循相同的租户、品牌和快照边界。旧快照不能看到后来完成的正文或质量。

## 迁移与运行验收

新增 [021 迁移](../db/migrations/021_gemini_result_quality.sql)：

- `visibility_checks.result_metadata`：可空 JSON，16 KiB 上限。
- `site_targets.monitoring_enabled_at`：可空 UTC 时间，本次启用的生效边界。无变化的全量 PUT 不重置，停用后重启使用新时间。
- 无历史质量回填、无任务生成、不随启动执行。

例如在品牌当地 10:00 开启 Gemini，它不加入当日 09:01（2026-11-09 前为 09:00）或更早遗漏时隙，最早参加下一有效的每日轮次。既有未知启用时间的目标保留原计划资格；新加入的旧目标记录从当前开始的边界，不假造历史启用时间。

发布前需要明确目标库及迁移授权，协调旧 Worker 后显式迁移，再部署兼容代码。迁移未就绪时应返回 `GEMINI_RESULT_SCHEMA_NOT_MIGRATED`，不能先提交外部任务再发现无法保存。回滚保留新增列和已有结果，不自动重投外部任务。

用户在明确本机数据库、服务更新和 5 次单样本范围后回复“允许”；已据此部署 021、更新 API/Worker 并完成本批采集。采集阶段使用仅限本租户与 Run 的 Worker；结束后确认没有其他待消费历史队列或到期时隙，再恢复原后台服务完成本批分析。2026-10-08 验收结束时 API 和 Worker 均已恢复；没有删除任何表、库或卷，也没有重投历史 9 条 `submission_unknown`。当前服务状态以 [本机状态核对](local-state-verification.md) 为准。

2026-10-08 验收时品牌 Gemini 已启用，ChatGPT 原配置保持；当时两目标、5 问题、单采样的计划预计每轮 100 项目积分，下一时隙为北京时间 2026-10-09 09:00。这是历史计划快照。后续加入 Copilot 的三目标轮次及当前下一时隙见 [本机状态核对](local-state-verification.md)。隔离 MySQL 的迁移重放、故障注入、并发和完整积分阶段仍待专门验证；合成样例、历史成功和单次真实成功不能互相替代。

## 依据

- [产品需求](../.trellis/tasks/10-08-gemini-answer-citations/prd.md)
- [技术设计](../.trellis/tasks/10-08-gemini-answer-citations/design.md)
- [Cloro 官方合同核验](../.trellis/tasks/10-08-gemini-answer-citations/research/cloro-public-contract-20261008.md)

官方文档确认 Gemini 暴露 `sources` 与可选的 `citationPills`，且可能返回空来源；本期确定性样例据该 schema 构造，未冒称为真实采集响应。
