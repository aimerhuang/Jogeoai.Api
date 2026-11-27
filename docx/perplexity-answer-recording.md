# Perplexity 回答采集

更新：2026-10-16。Perplexity 复用现有 Cloro 链路，没有专属表、迁移、接口或前端改动。专项构造样例和受影响用例已通过定向测试；本机三次真实手动采集都因 Cloro 的 Perplexity 服务故障失败（15/15），**尚未拿到真实回答**。任务已按现状归档。暂未为任何品牌启用，也不在每日监控中。

## 1. 功能与启用状态

针对用户已保存、启用且未归档的问题，经 Cloro 获取 Perplexity 回答，保存正文、实际引用来源、检索词和结果质量，在现有监控记录中按 `perplexity` 筛选和回查。

2026-10-11 用户决定：Perplexity 暂不加入每日监控，新品牌默认仍仅 ChatGPT。以后启用沿用 [Copilot 的按品牌显式启用策略](copilot-answer-recording.md)：在「采集任务」页的「采集模型」区打开 Perplexity，或经 `PUT /api/v1/sites/{siteId}/targets` 提交含 `cloro/perplexity/scraped` 的完整目标集合。启用后只进入之后的每日轮次，每轮按“有效问题 × 10 项目积分”增加费用；5 个问题即每轮多 50 积分。

## 2. 入口

| 用途 | 现有入口 |
| --- | --- |
| 单次手动运行 | `POST /api/v1/visibility-runs`，携带 `Idempotency-Key`；可用已启用的 `targetIds`，或用显式 `targets`（`{"provider":"cloro","engine":"perplexity","country":"US","locale":"zh-CN"}`）而不修改品牌目标 |
| 采集进度 | `GET /api/v1/visibility-runs/{runId}` 和 `.../{runId}/checks` |
| Check 结果 | `GET /api/v1/visibility-checks/{checkId}` |
| 监控记录 | `GET /api/v1/sites/{siteId}/monitoring/records?engine=perplexity` 及同快照详情 |

手动运行自 2026-11-27 起与每日监控同价预占项目积分（检测数 × 10），余额不足返回 402 `CREDIT_INSUFFICIENT` 且不创建运行（Trellis `11-27-sec-manual-run-charging`，代码已实现，本机 API / Worker 尚未用该构建重启；见[品牌监控配置说明](brand-monitoring-config.md) §4、§9）；在此之前手动运行不扣项目积分，本文的实测属于这一时期。每个成功的 Check 另外消耗 Cloro 额度（Perplexity 标价 4；失败任务本次实测扣 0）。viewer 不能创建运行。

## 3. 字段处理

提交 `taskType=PERPLEXITY`，负载只有问题原文、国家和 `include.markdown=true`；Cloro 的 `include` 不接受其他字段，本系统也不传 `state`（美国州定向，另收费）。

| Cloro 返回 | 本系统处理 |
| --- | --- |
| `markdown` / `text` | 正文，Markdown 优先 |
| 无 `model` 字段 | 版本显示为 `perplexity` 且标记版本不可用，不推测底层模型 |
| `sources[]` | 保存 `url` 与 `label`（空则标题为空） |
| `citationPills[]` | 接在 sources 之后保存；同一引用块引用多个来源时全部保留 |
| `search_model_queries` | 作为检索词保存 |
| `related_queries` | 推荐追问，不作为检索词 |
| `images`、`videos`、`hotels`、`places`、`shopping_cards` | 不作为引用 |

`related_queries` 和媒体/购物/地点字段不作为检索词或引用保存，但会随脱敏后的原始响应留在诊断字段中，这与其他 Cloro 引擎一致。

`citationPills` 的 `position` 指向 `sources`，所以被引用的 URL 会在两个数组里各保存一行；保存序号是展平后的序号，不是上游位置或句子编号，保存条数也不等于独立网站数。域名由 URL 计算。引用质量语义（`available`、`empty`、`not_exposed`、`unknown`/`partial` 及原因码）、100 条与 750 字符上限、安全过滤和 1 MiB 正文边界见 [Copilot 说明](copilot-answer-recording.md) §3。

## 4. 验证状态

- **代码样例**：新增 `PerplexityAdapterTests` 18 项，按 2026-10-11 读取的 Cloro 官方文档构造，不是真实采集；Worker 与监控详情/API 用例加入 `perplexity` 输入。定向 6 个测试类共 211 项；当时唯一失败的既有 Copilot 429 用例（key 池改动后预期过时）已另行修正，2026-10-12 复跑 211/211 通过。详见 [实施记录](../.trellis/tasks/archive/2026-10/10-11-perplexity-monitoring-integration/research/implementation-validation.md)。
- **真实采集（2026-10-11 23:41）**：品牌 `szulink.com.cn` 5 个问题 × Perplexity（US/zh-CN）× 1 采样，Run `5472e846-efa3-44ac-bb21-f6b61c772fb2`。5 个任务都已提交，Cloro 均返回 `INTERNAL_SERVER_ERROR`（Task failed after max retries），未扣额度。同时 [Cloro 状态页](https://status.cloro.dev) 显示 Perplexity 处于重大故障（事件 #1082654，约 10-06 起，当日成功率约 5%）。本系统正确记录为失败：无正文、无来源、不进监控列表、品牌目标/计划/项目积分未变。10-12 00:06 第二次 Run `07cdf445-75f3-4c17-aec0-c7da2f5be875` 同样 5/5 失败、未扣额度；之后每小时检查至 08:30 仍未恢复，用户决定按现状归档。正文、来源和检索词的真实保存**未经真实验证**，Cloro 恢复后可按实测记录中的请求重新发起。详见 [实测记录](../.trellis/tasks/archive/2026-10/10-11-perplexity-monitoring-integration/research/real-collection-2026-10-11.md)。
- **第三次实测（2026-10-16 08:10）**：同样的 5 个问题，Run `6f300bea-89fa-403e-bc36-4f251b5247ea` 5/5 失败。
  - 这次 Cloro 在提交阶段就以 HTTP 400 `MODEL_UNAVAILABLE` 拒绝（“无法用未登录会话访问 Perplexity，正在修复”），没有建任务，没有扣额度。
  - 当时的代码只记为通用的 `CLORO_SUBMIT_FAILED`。之后适配器读取白名单码，这类拒绝记为 `CLORO_MODEL_UNAVAILABLE`（“Cloro 暂时无法访问该模型。”），不重试、不换账号。
  - 故障期间改用 [Google AI Overview](google-ai-overview-recording.md) 作为替代回答面。
- **未执行**：隔离 SQL 用例；每日监控中的 Perplexity（未启用）。

规划与证据入口：[PRD](../.trellis/tasks/archive/2026-10/10-11-perplexity-monitoring-integration/prd.md)、[技术设计](../.trellis/tasks/archive/2026-10/10-11-perplexity-monitoring-integration/design.md)、[实施计划](../.trellis/tasks/archive/2026-10/10-11-perplexity-monitoring-integration/implement.md)、[验收表](../.trellis/tasks/archive/2026-10/10-11-perplexity-monitoring-integration/research/acceptance-status.md)。
