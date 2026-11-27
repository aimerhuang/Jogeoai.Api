# Google AI Overview 回答采集与品牌监控

更新：2026-10-16。Google AI Overview（引擎 `google-ai-overview`，Cloro `GOOGLE` 任务）复用现有 Cloro 链路，没有新表、迁移或接口。它是 Perplexity 故障期间的替代回答面。

本机 `jogeoai` 已完成一次 5 个问题的真实采集，并为 `szulink.com.cn` 启用了每日监控。首个含该引擎的每日轮次尚未发生。

## 1. 功能与启用

- 针对已保存、启用且未归档的问题，经 Cloro 取得 Google 搜索结果页顶部的 AI 概览，保存正文、引用来源和结果质量。
- 在现有监控记录中按 `google-ai-overview` 筛选和回查。
- 启用策略与 [Copilot](copilot-answer-recording.md) 相同：按品牌显式启用，新品牌默认仍只有 ChatGPT。启用后只进入之后的每日轮次，不补启用前的时隙。
- 可以在「采集任务」页启用，也可以经 `PUT /api/v1/sites/{siteId}/targets` 提交含 `cloro/google-ai-overview/scraped` 的完整目标集合。

## 2. 请求

- 负载为问题原文 `query`、大写的 `country`、`include.aioverview.markdown=true`，以及取自 Target locale 的 `hl`（界面语言，如 `zh-CN`），原样传递。
- 只有该引擎发送 `hl`，其他引擎的请求不变。
- 诊断接口 `POST /api/v1/providers/cloro/test` 固定使用 `en-US`。

## 3. 字段处理

| Cloro 返回 | 本系统处理 |
| --- | --- |
| `aioverview.markdown` / `text` | 正文，Markdown 优先 |
| 无模型字段 | 版本显示为 `google-ai-overview` 并标记版本不可用 |
| `aioverview.sources[]` 与 `citationPills[]` | 按顺序展平保存，不去重，与 [Perplexity](perplexity-answer-recording.md) 相同 |
| `relatedLinks`、`videos`、`places`、`map`、`ads` | 不作为引用 |
| 自然结果、People Also Ask、相关搜索 | 不采集为引用，只保留在脱敏后的原始响应中 |
| 检索词 | 不存在，`queryAvailability=not_exposed` |

## 4. 未展示 AI 概览

Google 不是每次都会展示 AI 概览。没有展示时，Cloro 返回 200 但没有 `aioverview` 键，仍然扣费。本系统的处理：

- Check 记为成功、无回答，分析为 `not_applicable`，监控列表显示 `no_answer`。
- 不计入可见率分母，与其他引擎的空回答一致。
- `resultQuality.answerAbsence = "ai_overview_not_shown"`。有 `aioverview` 键但正文为空时，该字段为 null，按普通空回答处理。
- 历史记录和其他引擎的这个字段为 null，表示未知或不适用。

## 5. 费用

- 每次请求 5 Cloro 额度：Google Search 基础 3，加 AI 概览附加 2。没有展示概览也照样扣。
- 每日监控按“有效问题 × 10 项目积分”计算。手动 Run 自 2026-11-27 起同价预占项目积分，余额不足返回 402 `CREDIT_INSUFFICIENT`（Trellis `11-27-sec-manual-run-charging`，代码已实现，本机 API / Worker 尚未用该构建重启；见[品牌监控配置说明](brand-monitoring-config.md) §4、§9）；在此之前手动 Run 不扣项目积分。

## 6. 验证状态

- **代码**：
  - 新增 `GoogleAiOverviewAdapterTests`，并在 Worker、监控和目标的参数化用例中加入该引擎。
  - 定向 359 项通过、0 失败、1 跳过（需隔离库的 `CloroKeyPoolSqlTests`）。
  - 命令见[实施记录](../.trellis/tasks/10-16-google-ai-overview-monitoring/research/implementation-validation.md)。
- **真实采集（2026-10-16）**：
  - 品牌 `szulink.com.cn` 5 个问题 × US/zh-CN，Run `d083f032-ff2e-491e-b402-3b5d525b00be`。
  - 5/5 成功，5 题都出现了 AI 概览，共保存 141 条引用（每题 20–44 条）。
  - 分析完成：1 条提及，4 条未提及。监控列表和同快照详情回查一致。
  - Cloro 每题实扣 5 额度。
  - 详见[实测记录](../.trellis/tasks/10-16-google-ai-overview-monitoring/research/real-collection-2026-10-16.md)。
- **启用**：`szulink.com.cn` 目标配置版本 9 → 10，下一时隙为 2026-10-17 09:00（北京时间）。
- **未执行**：
  - 未展示 AI 概览的真实样本，本次没有出现，只由单元测试覆盖。
  - 首个含该引擎的每日轮次。
  - 隔离 SQL 用例。
