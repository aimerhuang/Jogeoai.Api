# Copilot 回答采集与品牌监控

更新：2026-10-11。Copilot 复用现有 Cloro 链路，2026-10-09 专项构造样例及监控回归 242 项定向测试通过，零失败/零跳过；已修复手动创建 Run 的 viewer 写限制。首个含 Copilot 的每日轮次已只读核对，证据见 [本机状态核对](local-state-verification.md)。数据库集成、真实采集及目标环境部署分别验收，不以代码验证或历史成功代替运行态证据。

## 1. 功能与启用策略

针对用户已保存、启用且未归档的问题，经 Cloro 获取 Microsoft Copilot 消费端回答，保存正文、实际引用来源及结果质量，在现有监控记录中筛选和回查。

沿用 Gemini 策略：Copilot 按品牌显式启用，新品牌默认目标仍仅 ChatGPT。添加 Copilot 后，它参加启用边界之后的有效每日轮次，不补启用前遗漏日期。品牌时区、每日 09:01 主计划（2026-11-09 起由 09:00 改为 09:01；「2次/天」的单元格另有 00:05 凌晨轮，见 [品牌监控模型配置说明](brand-monitoring-config.md)）、首次计划首轮及已有计划去重行为沿用现有合同。

暂停计划保持暂停；自定义计划和已冻结的轮次保留原选择。停用只影响未来选择；重新启用从新的边界生效。研究候选没有被保存为启用问题时不参与监控。

## 2. 目标配置与查询入口

1. 用 `GET /api/v1/collection-targets` 查看当前可用目标、国家和语言。
2. 用 `GET /api/v1/sites/{siteId}/targets` 取得完整目标集合和 `configVersion`。
3. 有写权限的成员通过同路径 PUT 提交 `expectedVersion` 和完整 `targets` 集合，保留其他已有目标的启用/停用状态，加入 Copilot。版本冲突时先重新读取配置。
4. `GET/POST /api/v1/sites/{siteId}/prompt-monitoring` 查询/初始化每日监控；既有计划不会因读取、登录或重复 POST 新建额外首轮。viewer 的 POST 仅查询状态，不创建计划，且费用信息不返回。

可以加入完整 `targets` 数组的单个条目示例：

```json
{
  "provider": "cloro",
  "engine": "copilot",
  "captureMode": "scraped",
  "country": "US",
  "locale": "en-US",
  "enabled": true
}
```

此对象是一个目标条目；PUT 请求还需要当前版本和其他目标的完整数组。国家和语言应以环境中的目录为准，保存中文问题不意味着需要翻译问题或强行更改目录语言。

| 用途 | 现有入口 |
| --- | --- |
| 单次手动运行 | `POST /api/v1/visibility-runs`，携带 `Idempotency-Key`，选择已保存问题及 Copilot Target |
| 采集进度 | `GET /api/v1/visibility-runs/{runId}` 和 `.../{runId}/checks` |
| Check 结果 | `GET /api/v1/visibility-checks/{checkId}` |
| 模型监控 | `GET /api/v1/sites/{siteId}/monitoring/models` |
| 监控记录 | `GET /api/v1/sites/{siteId}/monitoring/records`，按现有引擎筛选参数选择 `copilot` |
| 同快照回答详情 | `GET /api/v1/sites/{siteId}/monitoring/records/{checkId}`，沿用列表的 `snapshotToken` |

问题原文及版本在创建运行时冻结；后续修改、停用或归档问题不会改写旧回答。采集状态与实际分析状态分别判断：结果未完成分析时不能显示成“未提及”，也不能以旧 Run 汇总的固定分析字段判断完成。

## 3. 回答与引用质量

结果身份为 `cloro/copilot/scraped/observed_surface`。正文与可保留 Markdown 来自提供方，版本缺失时标识版本不可用，不推测底层 GPT 型号。搜索词仅返回提供方暴露的内容及可用性。

引用来自 `sources` 与 `citationPills`。无引用块时可省略 `citationPills`；同一引用块可有多个来源，来源标题可空。现役保存按两个数组展平的顺序赋予位置，因此保存位置并非上游位置或句子编号；两个数组重叠时，保存条数也不等于独立网站数。

| `resultQuality.citations` | 含义 |
| --- | --- |
| `available/complete` | 提供方暴露的合法来源均被保留 |
| `empty/complete` | 已识别来源字段明确为空 |
| `not_exposed/unknown` | 提供方未暴露受支持来源字段 |
| `unknown` 或 `partial` | 结构异常、部分损坏、安全过滤、脱敏或超限，结合原因码解释 |
| 历史质量缺失 | 原回答和引用仍可读，质量未知 |

最多保留 100 条安全引用，URL 最长 750 字符；正文按 1 MiB UTF-8 边界保存，实际截断另有标记。`complete` 说明本系统对收到数据的保留情况，不保证上游页面抓取无遗漏。全部来源被过滤不能显示成提供方明确无引用。

不会把正文任意链接、购物卡片、地图条目或品牌官网补造为引用，也不推断逐句关系。公共 API 和文档只包含安全规范化结果，不暴露原始敏感响应。

## 4. 费用、权限与恢复

自动监控费用为“有效问题 × 目标 × 采样 × 10 项目积分”。5 个问题、ChatGPT/Gemini/Copilot 三目标、单采样预计每轮 150 积分，比两目标增加 50 积分。余额不足不开始新的收费提交。

手动运行自 2026-11-27 起与自动监控同价预占项目积分（检测数 × 10），余额不足返回 402 `CREDIT_INSUFFICIENT` 且不创建运行（Trellis `11-27-sec-manual-run-charging`，代码已实现，本机 API / Worker 尚未用该构建重启；规则见[品牌监控配置说明](brand-monitoring-config.md) §4、§9）。在此之前手动运行不扣项目积分，只产生实际 Cloro 用量，本文记录的手动实测都属于这一时期。自动监控预留、结算和释放沿用现役规则，不保证外部任务受理后的失败退款。viewer 保持只读边界，Target PUT 与手动 Run POST 在写入前拒绝；手动 Run 返回 `403 CONTENT_FORBIDDEN`，拒绝发生在幂等查询和 Store/Provider 调用前。监控 POST 对 viewer 仅查询状态，Run/Check 的授权读入口保留。

有外部任务 ID 的任务恢复后继续轮询原任务；提交结果不明保留 `submission_unknown`，不自动重新提交。正文、引用、质量、终态及分析入队保持原子保存，重复完成不产生第二份结果。

## 5. 部署依赖与验证状态

复用 [021](../db/migrations/021_gemini_result_quality.sql) 的通用质量和目标启用时间字段，本轮没有新增 Copilot 专属迁移。历史文档记录本机已部署 021，新的目标环境仍应单独核实；缺依赖时保留既有 `GEMINI_RESULT_SCHEMA_NOT_MIGRATED` 错误码，提示所需迁移未部署。

242 项定向测试已通过，涵盖 Copilot/Cloro 来源解析、目标生效/权限、手动 Run 权限、Worker 恢复、质量与 UTF-8 容量、监控详情/筛选及测试连接保护。实际命令、失败修正和逐类计数见 [实施记录](../.trellis/tasks/10-09-copilot-monitoring-integration/research/implementation-validation.md)；[15 项验收表](../.trellis/tasks/10-09-copilot-monitoring-integration/research/acceptance-status.md) 分别记录代码与剩余数据库证据。20 个 SQL 案例已编译并发现，执行 0；连接保护测试不等于数据库测试。2026-09-28 历史四引擎运行包含 Copilot 成功结果，但不代替本轮质量与自动监控验收。

代码阶段[独立审查通过](../.trellis/tasks/10-09-copilot-monitoring-integration/research/check-report.md)，没有新的阻断代码缺陷；受影响项目编译和改动文件 Analyzer 通过。隔离数据库场景尚未执行，测试对象清理前列出对象并取得确认。

2026-10-10 在本机 `jogeoai` 为品牌长期启用 Copilot（US/zh-CN），手动采集 5 个问题、单采样，5/5 成功，引用质量均为 `available/complete`，前端「回答记录」可按 Copilot 筛选查看，详见[实测记录](../.trellis/tasks/10-09-copilot-monitoring-integration/research/real-collection-2026-10-10.md)。

同日修复了每日计划时隙的命名锁缺陷：`CollectionScheduleStore` 原先在事务连接上执行 `GET_LOCK`/`RELEASE_LOCK` 时未携带事务，自 10-09 起每日轮次没有建出 Run。修复后，10-09 积压时隙经用户确认以 `OPERATOR_SKIPPED_STALE_SLOT` 跳过；10-10 时隙补跑 ChatGPT+Gemini，10/10 成功，扣除 100 积分。**2026-10-11 已确认 09:00 轮次完成：三目标各 5/5 成功，15 条分析完成，结算 150 积分**；数据库、Run/Check 与监控列表一致，三个模型各抽查一条同快照详情，正文、质量和引用 URL/标题/顺序一致，详见 [本机状态核对](local-state-verification.md)。前端「采集任务」页新增「采集模型」区，可启用/停用海外模型；品牌概览新增「按模型对比」。2026-10-10 页面与开关验收仍见 [子任务运行态记录](../.trellis/tasks/10-10-copilot-auto-collection-frontend/research/runtime-verification.md)，本轮没有重验前端页面。

规划与证据入口：[PRD](../.trellis/tasks/10-09-copilot-monitoring-integration/prd.md)、[技术设计](../.trellis/tasks/10-09-copilot-monitoring-integration/design.md)、[实施计划](../.trellis/tasks/10-09-copilot-monitoring-integration/implement.md)、[前置研究](../.trellis/tasks/10-09-copilot-monitoring-integration/research/repository-findings.md)。
