# Jogeoai

AI 可见性服务的 .NET 后端。本文是当前实现与本机开发的入口；[底层框架设计](docx/cloro-foundation-design.md) 描述后续目标，[Elmo 参考](docx/elmo-reference.md) 提供架构与页面参考。

采集上层的需求见 [Elmo 级管理与分析接口 PRD](.trellis/tasks/09-26-elmo-collection-upper-api/prd.md)。品牌管理、多样本采集、提及证据、Overview/Visibility/Share of Voice、Fan-Out 与引用细分已实现；Opportunities 属第二期。

最新只读服务快照见 [本机状态核对](docx/local-state-verification.md)（2026-11-12；保留 10-11 / 10-19 历史记录）。各模块带日期的部署与实测段落只说明当次证据，不代表服务一直运行或其他环境已部署；健康检查、后台消费和完整验收分别判断。

## 部署与仓库结构（2026-11-20）

- **服务器部署**：`deploy/` 提供 API、Worker 的 Docker 镜像与 `compose.yml`（MySQL + API + Worker，全新库首次启动自动执行 `db/migrations` 全部迁移）。步骤、网关示例、数据迁移、升级回滚见 [部署说明](docx/deployment.md)。
- **采集端**：`collector/` 是采集端（Node.js，原 Ai-visiblility-collector）的源码快照。安卓 App 采集节点在每台采集电脑上原生运行，不放进服务器的 Docker；“加一台采集电脑”的步骤见 [部署说明](docx/deployment.md) 第 3 节。
- **公开仓库**：GitHub 上的仓库由 `scripts/export-public-repo.sh` 从本机两个目录导出，不包含 `.env`、数据库备份、本机数据、`.trellis` 开发记录（含安全审计）、第三方参考代码。下文中指向 `.trellis/` 的链接在公开仓库里不可用。
- **上线前**：当前代码不宜直接开放公网，必须先完成 [部署说明](docx/deployment.md) 第 4 节列出的事项。

## App 渠道采集（2026-11-17，本机已执行 049 并重启 API / Worker；默认不对任何租户开放）

Trellis `11-16-app-channel-monitoring-api`，是跨仓库总 PRD 的子任务 A。采集器接入（子任务 B，Collector 仓库）和前端（子任务 C）都还没有做。

**做了什么**

- 增加一种监控目标：安卓模拟器上的消费级 App。Provider 是 `app_collector`，采集模式是 `app`，固定 CN / zh-CN，只支持快速回答，频率只能是每周一次。
- 可用的平台有 6 个：豆包、DeepSeek、元宝、千问、Kimi、智谱清言。文心一言的入口种子就是停用的。
- App 没有加入 `AnswerProviderDefinition.All`。它不进主计划、夜间计划、立即采集和手动采集，也不进默认统计口径，不占 20 个目标的上限。只有三处文章相关功能不筛选来源。
- 每个品牌有一个独立的 App 计划，名称 `jogeoai:auto-prompt-monitoring:app:v1`，每天 09:11 运行。目标到期时一次创建一个周期 Run，截止时间是 7 天后，到截止仍未采到的记为未采集（`cancelled`）。

**接口**

- 安卓采集节点用节点令牌（库里只存 SHA-256）调用 `/internal/collector/app/{bindings/report, leases, heartbeats, failures, results}`。全部节点的绑定组成一个全局账号池；限速为每个绑定 300 秒、每个平台合计 30 秒，一台设备同时只持有一个租约。
- 计费是按次预留：领取时为这一条检测预留 10 积分，采到有效回答时结算。
- 新增 `GET /api/v1/sites/{siteId}/app-collection/status`。
- 在现有接口上增加了这些字段和参数：
  - 预估的 `channels`；
  - 回答记录的 `captureMode` / `channel` / `errorCode`，详情的 `app` / `sourcesWithoutUrl`；
  - 引用域名汇总的 `channel` 参数；
  - `/credits/usage` 条目的 `channel`。
- 新增系统管理员接口 `/api/v1/admin/collector-app/*`。Worker 增加截止处理和每日告警汇报，汇报首期只存档，不发送。

**迁移与部署**

- 依赖迁移 [049](db/migrations/049_app_channel.sql)，回滚脚本见 [rollback/049](db/migrations/rollback/049_app_channel_rollback.sql)。
- 049 只做加法，可以重复执行，可以用应用账号执行，不需要 root，也不建触发器。
- 部署顺序：先在目标库执行 049，再**同时**发布 API 与 Worker。两者都要配置相同的 `Collector:AppTenantIds`。

**现状**

- 2026-11-19：本机 `jogeoai` 已执行 [051](db/migrations/051_app_device_dispatch.sql)（device-v1：按设备派单、设备级 100 秒冷却、失败转给其他设备；执行前全库备份 `.backups/jogeoai-db-pre-051-20261119-114756.sql.gz`），API 与监控模式 Worker 已用新构建重启，健康检查为 200。详见 [App 渠道说明](docx/app-channel.md) 第 0 节。
- 本机 `jogeoai` 已于 2026-11-17 执行 049（执行前备份 `~/jogeoai-backups/jogeoai-pre049-20261117-221551.sql.gz`；118 → 124 张表，现有各表行数与关键内容哈希不变），API 与完整模式 Worker 已用新构建重启并通过健康检查。`Collector:AppTenantIds` 为空，没有任何 App 任务。其他运行库未执行。
- API 和 Worker 没有用新构建重启过，没有发过真实 HTTP 请求，也没有用真实设备验证过。
- `Collector:AppTenantIds` 默认为空，所以 App 不对任何租户开放。提问检查规则（DC-01）还没有经用户确认。

配置项、运营操作、三级回滚和完整的未验证项见 [App 渠道说明](docx/app-channel.md)，命令与证据见 [verification.md](.trellis/tasks/11-16-app-channel-monitoring-api/verification.md)，规范见 [.trellis/spec/backend/app-channel.md](.trellis/spec/backend/app-channel.md)。

## 新增提问 / 新增模型后的立即采集（2026-11-12，源码与本机接口已核对，端到端待验收）

`11-11-prompt-list-models-instant-collect` 的后端代码已存在，5260 OpenAPI 已包含 `PromptView.instantCollection`、`PromptImportResult.createdPromptIds` / `instantCollection` 和立即采集状态类型。新增提问与给已有提问增加模型会尝试触发采集；保存成功不等于采集成功。合同与证据边界见 [提问管理说明](docx/question-management.md#立即采集增量2026-11-12-核对)。本轮没有创建提问、调用 Provider、扣积分或重跑代码测试，任务尚缺验收记录，不标为功能验收完成。

## 团队默认监控配置：跟随与单独配置（2026-11-11，本机已部署并真实验收）

Trellis `11-11-team-monitoring-config`（子任务 `-api` / `-web`）。团队管理「大模型监控配置」（`/team/monitoring`）不再是占位页：团队有一份默认监控配置（模型 × 回答模式 × 频率），品牌默认跟随，可单独配置并恢复跟随；保存团队默认配置时同一事务内同步到所有跟随品牌。新增 `GET|PUT /api/v1/team/monitoring-config`、`POST /api/v1/team/monitoring-config/estimate`、`POST /api/v1/sites/{siteId}/targets/follow-default`，`SiteTargetsView` 增加 `followsTeamDefault`，公开的品牌目标 PUT 保存后品牌变为单独配置。依赖迁移 048：本机 `jogeoai` 于 2026-11-11 备份后用 root 执行（`site_targets` 校验和不变；主团队 4 个品牌两两不同，默认取基线、全部单独配置；其余 3 个单品牌团队跟随），5260 API 已重启并用临时 owner 会话在单品牌验收团队走通读写，Worker 未运行、未改动。用法、归类规则、部署与回滚见 [团队默认监控配置说明](docx/team-monitoring-config.md)，证据见 [verification.md](.trellis/tasks/11-11-team-monitoring-config-api/verification.md)。

## 品牌监控模型配置：回答模式 × 采集频率（2026-11-09，本机已部署；2026-11-11 真实时隙已核对，优联已恢复原配置）

Trellis `11-09-brand-model-modes`。品牌管理里原来的「单独配置监控目标」弹窗改为「{品牌名}大模型监控」矩阵：每个模型一行，列为「快速回答」和「深度思考」，每个单元格单独勾选并选择采集频率（2次/天、1次/天、每2天1次、1次/周）。每日监控的计划、时隙展开、积分预留与结算，以及 Worker 下发的思考参数，都改为按「模型 × 回答模式 × 频率」计算。用法、能力表、积分口径和回滚步骤见 [品牌监控模型配置说明](docx/brand-monitoring-config.md)，前端改动见 Jogeoai.web README，命令与证据见 [verification.md](.trellis/tasks/11-09-brand-model-modes/verification.md)。

- **迁移**：[047](db/migrations/047_answer_mode_frequency.sql) 只做加法，可重复执行：
  - `site_targets` 新增 `answer_mode`（NULL 表示 Provider 默认模式）、`frequency`（默认 `daily`）、`frequency_anchor`（DATE）。
  - `visibility_checks` 新增 `answer_mode`（NULL 表示历史检测，模式未知）。
  - 新增 3 个 CHECK（`ck_site_targets_answer_mode`、`ck_site_targets_frequency`、`ck_visibility_checks_answer_mode`）。**不改任何索引**：005 起的唯一键 `(run_id, prompt_id, run_target_id, sample_index)` 已经能区分同一模型的两种模式，因为非默认模式的目标键带 `|mode` 段，是另一个 run target。
  - 主计划 `jogeoai:auto-prompt-monitoring:v1` 从 09:00 改为 09:01，`next_slot_at` 同步顺延 1 分钟，避免在 09:00–09:01 之间迁移时当天多出一个 `T0901` 时隙。只更新 `local_time='09:00:00'` 的行，重放时不会再次顺延；停用的计划（`next_slot_at` 为 NULL）只改时刻。
  - 回滚见 [rollback/047](db/migrations/rollback/047_answer_mode_frequency_rollback.sql)。以下任一情况存在时拒绝执行（SIGNAL），且不做任何修改：启用的凌晨计划、`answer_mode` 非空的目标、`frequency <> 'daily'` 的目标。检查通过后，主计划改回 09:00（`next_slot_at` 减 1 分钟），删除 CHECK 和新列。`visibility_checks.answer_mode` 的记录会随回滚丢失。完整步骤见说明文档「回滚」。
- **兼容**：迁移后，存量目标都是默认模式 + 1次/天。默认模式的目标键与旧键逐字节相同，因此提问平台范围（042）、计划快照、`SelectionHash`、首轮键和运行请求哈希都不变。约束：各 Provider 的 `DefaultAnswerMode` 发布后不能修改，否则键的含义会变。未执行 047 的库：读取时按默认模式 + daily 处理，写入默认模式 + daily 照常；写入非默认模式或非 daily 返回 503 `ANSWER_MODE_SCHEMA_NOT_MIGRATED`（这条退化路径只做过代码审查，没有在未迁移的库上实测）。
- **部署顺序**：先在目标库执行 047（root，mysql 客户端加 `--default-character-set=utf8mb4`），再**同时**发布 API 与 Worker，最后发布前端。旧 Worker 不认识 `answer_mode`，会按 Provider 全局配置的思考参数采集，记录的模式就会错。不支持新旧 Worker 滚动并存。
- **接口**：
  - 目标目录 `CollectionTargetView` 新增 `supportedAnswerModes`、`defaultAnswerMode`。
  - 站点目标 `SiteTargetsView.items[]` 新增 `answerMode`、`frequency` 和只读的 `frequencyAnchor`；`SiteTargetWrite` 新增可选的 `answerMode`（省略表示默认模式）和 `frequency`（省略表示 `daily`）。
  - PUT 请求体新增 `scope`：`all`（默认，与旧行为一致）或 `monitoring`（只替换 `monitoringEligible=true` 的目标，国内网页采集目标保持原样，新弹窗使用这个值）。`expectedVersion` 冲突仍返回 409。
  - 新增错误码：400 `ANSWER_MODE_UNSUPPORTED`（模型不支持该模式）、`FREQUENCY_INVALID`、`DUPLICATE_TARGET`、`MONITORING_TOO_LARGE`（启用的监控单元格超过 20 个）、`TARGET_NOT_ALLOWED`（`scope=monitoring` 时提交了不可监控的目标）、`INVALID_TARGETS`（`scope` 取值未定义）；503 `ANSWER_MODE_SCHEMA_NOT_MIGRATED`。未知的字符串枚举值（如 `"frequency":"hourly"`）在 JSON 绑定阶段就返回通用 400，不带 `FREQUENCY_INVALID`。
  - 新增 `POST /api/v1/sites/{siteId}/targets/estimate`：按草稿目标（不落库）返回 `weeklyChecks` / `weeklyCredits`、下一轮（00:05 或 09:01 中较近的一轮，已按频率过滤）的 `nextSlotChecks` / `nextSlotCredits` / `nextSlotAt` / `nextSlotKind`、逐单元格的 `cells[]`、`balance` / `shortfall`（余额不足不阻断）和 `blockers`。viewer 返回 403。
  - Check 视图、监控列表行、回答详情和 run 汇总中的 target 条目新增 `answerMode`（`fast` / `deep`；历史检测为 null）。本任务没有加 `answerMode` 筛选参数，MCP（044）的输出也没有改。
- **调度**：
  - 09:01 主计划（品牌时区）：每个时隙只包含当天到期的单元格。1次/天每天都到期；每2天1次从锚点日起隔一天到期；1次/周在与锚点相同的星期几到期；2次/天也包含在 09:01 这一轮。
  - 00:05 凌晨计划 `jogeoai:auto-prompt-monitoring:night:v1`：只包含 2次/天的单元格。品牌有 2次/天的单元格时创建或启用，没有时停用。站点目标 PUT 提交后立即对账（失败只记日志，时隙前的懒对账兜底）；没有主计划时不创建；不补首轮。凌晨的 run 不会让当天 09:01 被判为「已跑过」。
  - 某个时隙没有到期的单元格时，时隙记为 `skipped`，`skip_code=MONITORING_NOT_DUE`，不建 run、不预留积分。
  - 锚点日期在目标首次启用，或频率改为每2天1次 / 1次/周时写为保存当天。日期按**主计划的时区**计算（品牌改时区后主计划时区不会跟着变，所以不用品牌时区）；没有主计划时用品牌时区。保存当天的 09:01 如果已经过去，不会补采。
  - 修改从下一个未冻结的时隙开始生效；已冻结或运行中的轮次不变。
- **Worker**：每条新检测的 `answer_mode` 写入解析后的非空模式。DeepSeek、豆包、PackyAPI 三个 Worker 把它传给 adapter，adapter 只覆盖 `thinking` / `reasoningEffort`；`requestMode`、搜索来源、`maxToolCalls`、超时仍取 Provider 全局配置。管理员诊断接口（Provider test）不传模式，仍按全局配置。默认模式下的请求体与迁移前逐字节一致（单测锁定）。Cloro 不变。
- **能力表**（阶段 0 真实探测 8 次 + Cloro 文档查阅，见 [answer-mode-probe.md](.trellis/tasks/11-09-brand-model-modes/research/answer-mode-probe.md) 与 [cloro-modes.md](.trellis/tasks/11-09-brand-model-modes/research/cloro-modes.md)）：

  | 模型 | 快速回答 | 深度思考 | 默认模式（= 迁移前的实际行为） |
  |---|---|---|---|
  | ChatGPT / Perplexity / Copilot / Gemini / Google AI Mode / Google AI Overview（Cloro） | ✓ | — | 快速（Cloro 请求体没有模式参数） |
  | DeepSeek | ✓ | ✓ | 快速 |
  | 通义千问 | ✓ | ✓ | 快速（深度模式实测仍会联网搜索） |
  | Kimi | ✓ | ✓ | 快速 |
  | 豆包 | ✓ | ✓ | **深度**（现网 messages 模式未关思考；快速 = `thinking=disabled`，只在 messages 模式下可用） |
  | 元宝 | — | ✓ | **深度**（hy4 关闭思考后仍会推理） |
  | 智谱清言 | ✓ | — | 快速（low / high 两档单样本区分不出差异，用户 2026-11-09 决定只开放快速） |

  最多可勾选 16 个单元格，不超过每轮 20 个目标的上限。
- **验证**：`dotnet build Jogeoai.slnx` 0 错误。阶段 1–4 的完整定向过滤器在隔离库 `jogeoai_answer_mode_20261109`（现已删除）上通过 765、失败 0、跳过 0（`AnswerModeSqlTests` 5 个用例实际执行）。047 在同一隔离库验证了执行、重放、三种拒绝回滚、成功回滚和重新应用。Swagger 在临时 5281 实例核对了中文说明。
- **本机部署（2026-11-09）**：备份 `~/jogeoai-backups/jogeoai-before-047-20261109-162452.sql.gz` 后，用 root 对 `jogeoai` 执行 047。对比基线，只有 4 个主计划从 09:00 / next 01:00 UTC 改为 09:01 / 01:01 UTC，version +1；计划快照、目标键、提问平台范围的哈希和各表行数都不变；33 条目标都是 NULL 模式 + daily。5260 API 和完整 Worker 用 `./scripts/run-local.sh api|worker` 在 Claude 终端页签 c1 / c2 重启为新构建（16:25），`/health/live`、`/health/ready` 200。
- **已验证**：
  - AC-9（存量不变）：新 API 上 `prompt-monitoring/estimate` 和计费视图与旧构建完全一致，四个品牌每轮 18 / 1 / 4 / 80 次检测；新的 `targets/estimate` 每周 126 / 7 / 28 / 560 次，正好是每轮的 7 倍，下一轮都是 2026-11-10T01:01Z（main）。重启后 SQL 基线与 047 执行后相同，自动对账没有改动快照。
  - AC-10（按模式下发参数）：优联手动采集 run `1b0b2d6f-…`，DeepSeek fast + deep 各 1 条检测，都成功。fast 使用与站点旧键相同的 run target 键，0 个推理块，1961 字，19 条引用；deep 使用带模式段的新键，4 个推理块，3868 字，50 条引用。`GET /visibility-runs/{id}/checks` 返回的 `answerMode` 分别为 fast / deep。手动采集不扣积分。
  - AC-3 / AC-6（真实时隙）：11-09 把优联临时改为观察配置 version 23（DeepSeek 深度 2次/天、Google AI Overview 1次/周，锚点都是 2026-11-09 周一）。00:05 凌晨轮：11-10 和 11-11 两轮都只有 DeepSeek 深度 10 次，预留并结算 100 积分。09:01 主轮：11-10 因 Worker 未运行没有生成时隙，Worker 在 11-11 本地 11:03 恢复后，把它合并进 `20261111T0901` 补跑（`missed_slots=1`）。该轮 80 次检测，没有 AI Overview（周三未到期），DeepSeek 深度和快速各 10 次，没有被 `INITIAL_RUN_ALREADY_EXECUTED` 跳过。21 次失败都来自上游：Copilot `CLORO_MODEL_UNAVAILABLE`、Packy 千问 `PACKY_REQUEST_FAILED`，以及 1 次 ChatGPT `CLORO_TASK_FAILED`。结算 600 积分、释放 200 积分；Cloro 任务提交后即结算，所以那次 ChatGPT 失败也扣了积分。
  - 恢复（2026-11-11 19:01）：按 `.trellis/tasks/11-09-brand-model-modes/research/youlian-targets-v22-before-observation.json` 调用 `PUT /sites/{id}/targets`（scope=monitoring），恢复后为 configVersion 24。凌晨计划随即停用（enabled=0，`next_slot_at` 为 NULL）。主计划对账后为 10 个提问 × 8 个目标、每轮 80 次 / 800 积分，与迁移前一致。DeepSeek 深度目标行保留，但已停用。按 048 规则，优联仍是「单独配置」。
- **未验证 / 进行中**：
  - 「同一本地日已有 00:05 run，09:01 仍不被跳过」：11-11 的补跑中，主计划 run 比凌晨 run 先创建，所以这个顺序没有在真实运行中出现过。目前只有隔离库 SQL 测试 `AnswerModeSqlTests.SlotsFilterCellsByFrequencyAndNightRunsDoNotSkipTheMainSlot` 覆盖。
  - AC-5：没有在 3001 真实 API 预览中打开弹窗，核对预估与保存后的计划是否一致；弹窗只在测试数据中走查过。
  - 需要其他环境变量的隔离 SQL 套件没有跑：`CollectionScheduleSqlTests` 的既有用例（`JOGEOAI_STAGE1_TEST_CONNECTION` / `JOGEOAI_PROMPT_SCOPE_TEST_CONNECTION`）、`DashboardAnalyticsSqlTests`（`JOGEOAI_DASHBOARD_TEST_CONNECTION`）。看板把同一模型的两种模式按 Provider + 引擎 + 地区合并，这条 SQL 路径没有执行过。
  - 品牌改时区后的真实时隙；未执行 047 的库上的退化路径；其他运行库都没有执行 047。
- **已知限制**：
  - `prompt-monitoring/estimate` 的「每轮」仍按全部单元格计算（1000 次检测上限要按所有单元格同时到期来判断），其中的 `shortfall` 是上限值。弹窗使用的是按频率计算的 `targets/estimate`。
  - 计费视图（GET / POST `prompt-monitoring` 的 billing）按下一个 09:01 时隙过滤，不包含 00:05 那一轮。
  - `prompt-monitoring` 视图的「最近一轮」只看首轮和主计划的 run，不显示凌晨 run。
  - 文章工作台（`ArticleStudioService`）的目标快照里，同时开两种模式的模型会出现两次。这只影响上下文文字，不影响哈希和计费。
  - `CollectionPreviewView.targets[].frequency` 总是 daily；手动采集不看频率。
  - 已知的无关失败：`MonitoringAnswerApiTests.SwaggerDetailDeclaresOnlyTheRequiredSnapshotAndOptionalMonitoringFilters` 因口碑下钻新增的 `termPolarity` 参数而失败（早于本任务，与本任务无关）。
- **测试数据（2026-11-09 已清理）**：经用户确认，隔离库 `jogeoai_answer_mode_20261109` 已删除，并撤销了应用账号对它的授权；其他库和数据卷没有改动。重跑 SQL 测试需要按测试说明新建隔离库。本机 `jogeoai` 中仍保留 AC-10 手动采集 run `1b0b2d6f-…` 的两条检测，以及观察期间的真实 run 和积分记录（11-10 / 11-11 凌晨轮，11-11 主轮）；优联已于 11-11 恢复原配置。

## MCP 只读访问（2026-11-04，本机已部署）

Trellis `11-04-collection-mcp-endpoint`（父任务 `11-04-split-deploy-collection-mcp`）。新增 `/mcp`（Streamable HTTP 无状态，`ModelContextProtocol.AspNetCore` 2.3.1），供外部 bot 用团队签发的只读令牌查询品牌、监测提问、监测记录（回答 + 引用 + 搜索词）、可见性、声量和引用域名，共 8 个工具；不能写入采集数据。令牌由 owner / admin 通过 `/api/v1/mcp-tokens` 签发、列出、吊销，库内只存 SHA-256 哈希；`/mcp` 不接受 Cookie 或会话 Bearer，每个令牌每分钟 60 次。接入方式、工具、错误码和已知限制见 [MCP 访问说明](docx/mcp-access.md)。

- **迁移**：[044](db/migrations/044_mcp_access_tokens.sql) 只新增 `mcp_access_tokens` 表，可重复执行。先执行 044 再发布 API，Worker 不受影响；未执行时 `/mcp` 和令牌接口返回 503 `MCP_SCHEMA_NOT_MIGRATED`。回滚见 [rollback/044](db/migrations/rollback/044_mcp_access_tokens_rollback.sql)，存在启用令牌时拒绝执行。
- **本机部署（2026-11-04）**：备份 `~/jogeoai-backups/jogeoai-pre044-20261104-134526.sql.gz` 后用 root 对 `jogeoai` 执行 044（重放两次无副作用）；5260 API 用 `./scripts/run-local.sh api` 在终端页签 c1 重启为新构建，`/health/live`、`/health/ready` 200。Worker 未重启（不受影响）。真实验收：签发 / 列表 / 吊销、8 个工具、同快照下记录详情与 `/api/v1` 一致、站点和团队越权、会话 / Cookie / 吊销 / 过期令牌 401、第 61 次请求 429；测试令牌与临时会话已删除。
- **验证**：`dotnet build Jogeoai.slnx` 通过；`dotnet test tests/Jogeoai.Tests --filter "FullyQualifiedName~Jogeoai.Tests.Mcp"` 67 通过、3 跳过（`McpTokenStoreSqlTests` 需隔离 `_test` 库，未跑）。同时运行的 `MonitoringAnswerApiTests.SwaggerDetailDeclaresOnlyTheRequiredSnapshotAndOptionalMonitoringFilters` 失败，原因是 11-02 新增的 `termPolarity` 参数，与本任务无关，尚未修复。
- **前端**：Jogeoai.web 已从 5260 重新生成 `src/types/generated/api.ts`（只新增 `/api/v1/mcp-tokens`），未做页面。

## 品牌知识库维度列表（2026-11-03，本机已部署）

Trellis `11-03-brand-knowledge-dimensions`。「品牌信息」页由「文件资料 / 产品信息」两页签改为按 6 个维度（品牌介绍、核心卖点、目标人群、差异化优势、产品/服务、常见问题）的品牌知识库：每条 = 维度 + 标题 + 内容（≤2000 字），每品牌最多 50 条；支持「从网址识别」把一个网页拆成多条候选、勾选后批量保存。条目进入知识库检索，写稿与 AI 推荐监测问题都会读取。用法、接口与错误码见 [品牌知识库说明](docx/brand-knowledge.md)，前端改动见 Jogeoai.web README。

- **迁移**：[043](db/migrations/043_brand_knowledge_items.sql) 新增 `brand_knowledge_items`、`brand_knowledge_extract_jobs`；唯一的非加法改动是 `knowledge_documents.source_kind` 允许 `item`。数据段把 `approved` 产品迁为「产品/服务」条目，沿用产品 UUID 并接管原产品文档，已冻结的写稿快照不受影响。043 可重复执行，末尾输出迁移报告。
- **部署顺序**：先执行 043（`--default-character-set=utf8mb4`），再发布 API 与 Worker（新增 `KnowledgeItemSyncWorker`、`KnowledgeExtractWorker`），最后发布前端。新构建的品牌研究入队、产品识别频控和旧产品写接口都会读新表，**未执行 043 不能重启新构建**。回滚：先回滚前端，再回滚 API / Worker，最后视需要执行 [rollback/043](db/migrations/rollback/043_brand_knowledge_items_rollback.sql)；存在 `source_kind=item` 的文档时回滚脚本拒绝执行。
- **接口**：`/api/v1/sites/{siteId}/knowledge/items`（GET / POST / PATCH / DELETE）与 `/knowledge/extract-jobs`（发起、`current`、轮询、`cancel`、`save`、`discard`）。网址识别不扣积分，每租户每日 50 次，与产品识别共用；保存候选单事务完成。`article-generation/options.products` 改为列出「产品/服务」条目；`brand-research` 把条目作为 `knowledge:` 证据，无官网、无描述但有条目的品牌也可发起。产品信息接口、知识库文件上传接口和 `knowledge/links` 在 OpenAPI 中标记 deprecated（新增 `ObsoleteOpenApiOperationTransformer`），已迁移产品的写操作返回 409 `PRODUCT_MIGRATED`；旧文档接口不能修改条目文档（409 `KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM`）。
- **验证**：`dotnet build Jogeoai.slnx` 0 错误；定向测试 225 通过、0 失败、2 跳过（需真实网关 / 另一任务专用库），连续 3 次稳定；前端定向测试 93/93、`vue-tsc -b`、生产构建通过。043 在隔离库 `jogeoai_knowledge_test_20261103` 实测执行、重放与回滚（该库已于 2026-11-03 删除）。命令与结果见 [verification.md](.trellis/tasks/11-03-brand-knowledge-dimensions/verification.md)。
- **本机部署（2026-11-03）**：备份 `~/jogeoai-backups/jogeoai-pre043-20261103-183012.sql.gz` 后，用 root 对 `jogeoai` 执行 043，优联 3188DH 迁为条目；5260 API 和完整 Worker 用 `./scripts/run-local.sh api|worker` 在终端页签 c1 / c2 重启为新构建，`/health/live`、`/health/ready` 200，Worker 启动后立即把 3188DH 同步为文档第 2 版。经用户同意停用了 3188DH 的网页链接资料。真实链路：检索只命中条目文档；新增 / 编辑 / 删除 4 / 4 / 2 秒反映到检索；无官网研究在有品牌介绍条目时生成 10 个问题并引用条目；写稿产品选项与素材预览正确。
- **网址读取（2026-11-03 晚）**：直接读取失败或正文不足 200 字时，对公开域名改用 Luna `web_search` 联网读取（同站引用校验、失败重试 1 次）；开发环境新增 `ArticleSource:AllowFakeIpRange`（仅 Development 生效，两份 `appsettings.Development.json` 已打开）放行 Clash fake-ip 198.18.0.0/15，本机写稿工作室与品牌研究的网页抓取一并恢复。真实验收：4188KH 产品页直接读取成功，2 条候选。模型联网读取对该站点间歇性超时，兜底不保证成功。
- **未验证**：真实文章生成（扣积分）；其他运行库未执行 043。
- **测试数据（2026-11-03 已清理）**：验收用条目、文档及其版本 / 分片 / 向量 / 存储文件、识别任务、研究任务、素材预览和临时会话已删除；本机优联当前只有 3188DH 一条知识。

## 新增监测问题弹窗与按提问平台范围（2026-11-03，本机已部署）

Trellis `11-03-prompt-create-wizard`。「用户提问」页的「创建提问」改为「新增监测问题」弹窗（AI 推荐 / 核心词拓展 / 批量导入 → 选择 AI 平台 → 看预估积分后一次创建），每条提问可以单独保存 AI 平台范围；每日监控的检测数、预留和逐条积分项都按「每条提问 × 它的有效平台」计算。前端改动见 Jogeoai.web README，契约见 [.trellis/spec/backend/prompt-target-scope.md](.trellis/spec/backend/prompt-target-scope.md)。

- **迁移**：[042](db/migrations/042_prompt_target_scopes.sql) 只做加法：`prompts.target_scope`（`all|custom`，默认 `all`）、`prompts.creation_source`（`manual|ai_recommend|keyword_expand|batch_import`，历史行为空）、新表 `prompt_target_scopes`（按 `CollectionIdentity.TargetKey` 保存 custom 提问选中的平台，删除提问时级联删除）。custom 的有效平台 = 所选平台 ∩ 品牌当前启用且可监控的目标；交集为空时该提问本轮不采集，不回退为全部平台。042 可重复执行。未执行 042 时，不带平台范围的读写照常（读取回落为 `all`），带平台范围或来源的写入返回 503 `PROMPT_TARGET_SCHEMA_NOT_MIGRATED`。
- **部署顺序**：先在目标库执行 042（mysql 客户端加 `--default-character-set=utf8mb4`），再**同时**发布 API 和 Worker，最后发布前端。旧 Worker 会忽略计划快照里的 `promptTargets`，按全部提问 × 全部目标采集并扣费。回滚顺序：先回滚前端，再回滚 API / Worker，最后视需要执行 [rollback/042](db/migrations/rollback/042_prompt_target_scopes_rollback.sql)。只要还有 `target_scope='custom'` 的提问，回滚脚本就拒绝执行；需要先经确认把它们恢复为 `all`。回滚会丢失平台范围和来源记录。
- **兼容**：没有 custom 提问时，计划快照 JSON、`SelectionHash`、首轮键、运行请求哈希、`total_checks`、预留和账单视图与改动前逐字节一致（旧值写死在 `CollectionIdentityTests`）；`/prompts/import` 旧请求的幂等哈希也不变。
- **接口**：
  - `POST /api/v1/sites/{siteId}/prompts/batch`：请求体 `{rows, targetKeys?}`，幂等键放在 `Idempotency-Key` 请求头。每行可带 `groupId` 或 `groupName`（二者互斥；按规范化名称归入已有分组，或在同一事务里新建，同一批中同名只建一次）、`brandedStatus`、`source`。以下任一校验失败整批回滚，不留下空分组：套餐上限（409 `PLAN_LIMIT_EXCEEDED`）；每品牌 200 个问题、每轮 1000 次检测、20 个目标（400 `MONITORING_TOO_LARGE`）；平台校验（400 `PROMPT_TARGET_INVALID`）。同一个键配不同请求体返回 409 `IMPORT_IDEMPOTENCY_CONFLICT`。
  - `/prompts/import`、`/prompts/import-preview`：仍是行数组 + 请求头，行新增上述四个可选字段；预览行新增 `groupResolution`（`existing|create|none`）和 `groupId`。
  - `POST /api/v1/sites/{siteId}/prompt-monitoring/estimate`：请求 `{newPromptCount（0–200）, targetKeys?}`，返回当前、新增和合计的每轮检测数与积分，以及余额、`shortfall` 和 `blockers`（`MONITORING_TOO_LARGE`、`PLAN_LIMIT_EXCEEDED`、`MONITORING_TARGETS_REQUIRED`；余额不足只体现在 `shortfall`，不阻断）。计算与每日计划使用同一检测矩阵和计划采样数；viewer 返回 403。
  - `POST /api/v1/sites/{siteId}/prompt-suggestions/keyword`：输入核心词（修剪后 1–20 字），返回 5 条行业词 + 5 条品牌词候选，排除已有提问；不足时补生成 1 轮，仍不足返回 `partial=true`。每用户每分钟 6 次（429 `RATE_LIMITED`），不落库、不扣积分。复用研究 Provider 配置，单次模型调用最长 45 秒；未配置返回 503 `PROMPT_SUGGESTION_NOT_CONFIGURED`，失败返回 502 `PROMPT_SUGGESTION_FAILED`，参数错误返回 400 `KEYWORD_INVALID`。AI 推荐直接复用 `brand-research`，不扣积分，也不写引导草稿。
  - `POST /api/v1/sites/{siteId}/prompt-suggestions/quick`、`POST /api/v1/sites/{siteId}/competitor-suggestions`（2026-11-11，引导第 3/4 步）：只用品牌名称/别名/介绍/主域名，一次模型调用同步返回约 8 行业词 + 2 品牌词问题、最多 10 个竞品（剔除品牌自身与已有竞品）；同一限流，不落库、不扣积分；Provider 首个最多 20 秒、总预算 28 秒，超时 504 `*_TIMEOUT`。本机三个品牌实测 7–12 秒。引导不再创建 `brand-research` 任务。详见 [注册引导说明](docx/registration-onboarding.md)。
  - 提问创建与 PATCH 新增 `targetKeys`。PATCH 中省略表示不变，`[]` 或 `resetTargetScope=true` 表示恢复全部平台；范围变化只递增 `configVersion`，不新建提问版本。`PromptView` 新增 `targetScope`、`targetKeys`（只在 custom 时返回）和 `creationSource`；`CollectionTargetView` 新增 `targetKey` 和 `monitoringEligible`（国内网页采集目标为 false）；计划 API 的 `CollectionScheduleSelection` 新增 `promptTargets`；`PromptMonitoringBillingView` 新增 `checkCount`。
- **验证**：`dotnet build Jogeoai.slnx` 0 错误。定向测试命令、结果和三道评审门的记录见 [verification.md](.trellis/tasks/11-03-prompt-create-wizard/verification.md)。隔离库 `jogeoai_prompt_scope_20261103` 执行了 001–042，042 重复执行、回滚后重新应用均成功；custom 范围的运行展开与预留、批量创建等 SQL 用例在该库实际执行（该库已于 2026-11-03 删除，重跑需按测试说明新建）。
- **本机部署（2026-11-03）**：备份 `~/jogeoai-backups/jogeoai-pre042-20261103-092155.sql.gz` 后，用 root 对 `jogeoai` 执行 042，41 条未归档提问均为 `all`。5260 API 和 Worker 用 `./scripts/run-local.sh api|worker` 在终端页签 c1 / c2 重启为新构建，`/health/live`、`/health/ready` 200，OpenAPI 含 `/prompts/batch`。重启后对账多次，优联每日计划 v17（10 提问 × 9 目标 × 1 采样 = 90 次）没有变化。用真实弹窗（3002 预览，代理到 5260）验证：
  - 新增 0 条时预估 90 次 / 900 积分，与计划一致。
  - 核心词拓展调用真实模型，返回 5 + 5 条中文候选，耗时约 54 秒。
  - 用「两行一组」导入 2 条，只选 ChatGPT 和豆包，预估新增 4 次 / 40 积分；创建后库中两条均为 `custom`、`batch_import`，各有 2 行范围，分组在同一事务新建。计划对账为 v18（12 提问、2 条 `promptTargets`），账单视图 `checkCount=94`，estimate 为 94 次 / 940 积分。
  - 编辑其中一条改回全部平台后，`configVersion` 1→2，提问版本仍为 1，计划为 v19。
- **未验证**：带自定义范围的第一个真实每日时隙（2026-11-04 09:00 本地）的 `total_checks` 和预留积分；目前运行展开与预留只有隔离库 SQL 测试证据。AI 推荐、余额不足、限流和部分结果只做了夹具走查。未迁移的库上，不带新字段的写入只核对了代码路径（只读接口已在临时实例核对）。其他运行库未执行 042。
- **测试数据（2026-11-03 已清理）**：本机分组「E2E测试分组」及其 2 条提问、版本、平台范围行和导入幂等记录已删除，无孤儿行；优联每日计划快照恢复为当日基线（10 提问 × 9 目标，无 promptTargets）。本机当前没有 custom 提问。隔离库 `jogeoai_prompt_scope_20261103` 已删除并撤销应用账号授权。

## 专业版 20/20 与无官网品牌创建（2026-10-25，本机已部署）

新增 [031](db/migrations/031_pro_plan_brand_member_limits.sql) 将专业版目录调整为 **20 个品牌 / 20 位成员**，只修改这两项权益，不改积分、周期或其他套餐；套餐页显示目录额度，当前套餐另列已用数量。成员邀请仍未实现，本次只是额度、计数守卫及展示。

新增 [032](db/migrations/032_domainless_brands.sql) 支持官网为 SQL NULL 和创建请求幂等。新建品牌要求名称和介绍，官网可不填；`POST /api/v1/brands/recognize` 按品牌名称和官网经固定 Luna 联网搜索（`web_search`），结合介绍文字与可读网页返回可编辑建议和参考来源，网页读不到不再中止（2026-10-25，Trellis `10-25-brand-recognition-web-search`），不自动保存、不占品牌或积分。已有团队通过 `/team/brands/new` 独立创建；首次引导复用表单，无官网研究使用介绍文字，仍可手动选择提问后完成。

后端定向回归、前端 114 项定向测试、解决方案/前端构建及独立检查已通过；宽窄屏内存夹具确认了两个无官网品牌、新用量、识别取消和首次引导完成。用户随后授权本机部署，**031→032 已在备份后执行到 `jogeoai-mysql-1` / `jogeoai`，5260 API 与完整模式 Worker 已重启**；停写前后数据对比仅专业版品牌额度 5→20，成员仍为 20，其他套餐、余额、订阅周期和既有品牌未因迁移变化。API 健康与真实 OpenAPI 已核验，Worker 单独确认启动及原轮询任务恢复，不能仅由 API 健康推导 Worker 状态。**真实 Luna、隔离 SQL 并发/幂等与真实用户创建仍未验**；构建仍有既有 PKCS 依赖告警。发布顺序和契约见 [品牌创建说明](docx/brand-creation.md)，命令与本机证据见 [部署记录](.trellis/tasks/10-25-pro-plan-brand-onboarding/local-deployment.md) 和 [集成验证记录](.trellis/tasks/10-25-pro-plan-brand-onboarding/verification.md)，不代表其他环境已部署。

新前端已重新构建并在原 `http://127.0.0.1:3000` 重启，仍为 Vite 本地开发服务、同源 `/api` 代理到 5260，不是生产静态托管部署。首页/新路由/模块及匿名401代理检查通过，浏览器刷新可见登录页；当前没有可用登录会话，未伪造会话进行业务验收。见 [前端部署记录](.trellis/tasks/10-25-pro-plan-brand-onboarding/frontend-deployment.md)。

## 品牌口碑后端（2026-10-30，源码就绪，未部署）

Trellis `10-30-reputation-backend`（父任务 `10-29-reputation-prd`）。新增情感事实、Worker 与聚合接口；前端页面属兄弟任务 `10-30-reputation-frontend`：`Jogeoai.web` 的 `/reputation`（品牌口碑）与回答记录页的口碑下钻已源码就绪，只在内存夹具（launch 配置 `jogeoai-web-fixture-reputation`，3014/5284）走查，未对真实 API 验证，见该仓库 README「品牌口碑」一节。

- **采集后自动判定（2026-11-02，Trellis `11-02-reputation-results-consolidation`，本机已启用）**：Worker 的 `appsettings.json` 改为 `Reputation:Worker:Enabled=true`、`AutoEnqueueSinceUtc="2000-01-01T00:00:00Z"`。每轮按提及分析完成时间升序最多登记 200 条，已发布 / 构建中版本里的历史回答自动补录，之后新采集的回答在提及分析完成后自动进入判定，不再需要 `POST reputation-jobs`。代码默认值（`ReputationSentimentWorkerOptions.Enabled=false`、`Program.cs` 的 `?? false`）不变，缺配置节时仍然关闭。**回滚**：设 `Reputation__Worker__Enabled=false` 后重启 Worker，已写入的判定保留。**成本**：其他运行库启用后同样会一次性补录全部历史有效回答，每条提及本品牌的回答调用一次 gpt-6-luna。本机 14:17 UTC 重启完整 Worker（Claude 终端页签 `worker`，日志 `/tmp/jogeoai-worker-20261102.log`），重启前没有被领取的采集任务。前 7 分钟新登记约 810 条、完成 96 条（未提及 60、模型判定 30），33 次调用共 96,361 输入 / 5,979 输出 token，无重试、无失败。全部补录于 14:58 UTC 前完成：882 个工作项全部 completed，0 failed；本轮未提及 524 条（不调模型），模型判定 286 次，共 830,423 输入 / 60,862 输出 token，无重试、无失败。已发布版本判定分布：未关联 566、正面 219、中性 70、未知 25、负面 2。真实接口核对（优联，2026-10-03 至 11-02）：已提及 297 条、已判定 273 条、判定中 0、失败 0，覆盖率 91.9%，健康度 89.2（优秀）；`monitoring/records?sentiment=pending` 为 0 条。同批前端改动见 Jogeoai.web README。
- **现状**：迁移 [038](db/migrations/038_reputation_sentiment.sql) 只新增 `reputation_check_work`、`reputation_check_sentiments`、`reputation_term_facts`、`reputation_analysis_jobs` 四张表，不修改旧表。2026-10-30 已用 root 在本机 `jogeoai` 执行 038（四表 InnoDB / utf8mb4，执行前无存活租约；未做备份，因只新建表），随后 5260 API 与 Worker 以当前构建重启，口碑 Worker 保持关闭（`Reputation:Worker:Enabled=false`），因此本机全部口碑回答显示为待判定。其他运行库未执行。真实接口核对：优联站点近 8 天 367 条有效回答、144 条提及本品牌、0 条已判定，健康度为空、环比为样本不足，国内网页平台出现在 `platformsWithoutData`；`monitoring/records?sentiment=pending` 合计 144，与聚合一致。
- **分类器未评测**：`LunaReputationSentimentClassifier`（固定 `gpt-6-luna`，`reputation-sentiment-v1`）只用测试替身验证，从未调用真实网关，判定质量、延迟与成本未知。样本评测需用户单独批准额度后再做。
- **Worker 登记规则**（默认值已于 2026-11-02 改为启用，见上文）：启用后，Worker 只登记提及分析完成时间不早于 `Reputation:Worker:AutoEnqueueSinceUtc` 的有效回答；该项为空时不自动登记。更早的历史回答通过 `POST /api/v1/sites/{siteId}/reputation-jobs`（`Idempotency-Key`）补录。未提及本品牌的回答直接写 `not_mentioned`，不调用模型。Provider 未配置时，已提及的回答保持待判定。最多尝试 3 次。缺表、未配置或 Provider 故障时，采集、提及分析和旧分析接口行为不变。
- **接口**：`GET /api/v1/sites/{siteId}/analytics/reputation?view=range|day` 返回 `ReputationSummaryView`。所有维度共用一个健康度函数：正面占比 × 100 + 中性占比 × 50，覆盖率不乘入分数。样本为 0 时健康度为空；少于 3 个样本标 `lowSample`，覆盖率低于 80% 标 `partialCoverage`，等级阈值为 80/65/50/35。平台按 provider + engine + captureMode 区分，热力图列与 `platforms` 一致，无数据平台放在 `platformsWithoutData`。`monitoring/records` 新增 `sentiment` 和 `term` 筛选，列表行与详情新增 `reputationSentiment`，与聚合使用同一状态归并。未执行 038 时：口碑接口及带口碑筛选的列表返回 503 `REPUTATION_SCHEMA_NOT_MIGRATED`；不带口碑筛选的列表照常返回，`reputationSentiment` 为空。
- **验证（2026-10-30）**：`dotnet build Jogeoai.slnx` 0 错误；`FullyQualifiedName~Reputation` 及受影响的监控记录、文档测试类全部通过。在临时库 `jogeoai_reputation_tmp_20261030a`/`b`（001–038，其中 a 连续执行 038 两次）和 `c`（001–037）上跑过隔离 SQL 测试，临时库已删除。契约见 [.trellis/spec/backend/reputation.md](.trellis/spec/backend/reputation.md)。
- **试运行（2026-10-30）**：Worker 新增 `--ReputationOnly=true` 模式，只注册口碑判定 Worker，可与正在采集的完整 Worker 并行，避免重启打断采集；为不覆盖运行中进程加载的 `bin`，用 `dotnet build src/Jogeoai.Worker --artifacts-path /tmp/jogeoai-reputation-artifacts` 构建后以 `dotnet <artifacts>/bin/Jogeoai.Worker/debug/Jogeoai.Worker.dll --ReputationOnly=true --contentRoot <同目录>` 启动。分类器每次调用记录模型、prompt/completion token 与耗时（不记正文）。优联站点当日 72 条回答经 `POST reputation-jobs` 补录：42 条未提及（不调模型），30 次模型调用共 127,461 输入 / 11,112 输出 token，无重试；结果正面 16、中性 4、负面 0、unknown 10（其中 9 条 `EVIDENCE_NOT_FOUND`，均含 Markdown 加粗、多数含表格，疑为依据片段与原文格式不一致），自动登记仍关闭。
- **依据容错匹配与 039（2026-10-30）**：依据片段先精确匹配，找不到时两侧忽略 `*`、`` ` ``、`|` 与空白再匹配（去格式后至少 6 字），偏移映射回原文；印象词计数同样回退。unknown 结果保留模型原始依据片段（≤ 400 字），写入迁移 [039](db/migrations/039_reputation_model_quote.sql) 新增的 `reputation_check_sentiments.model_quote`（039 已用 root 在本机 `jogeoai` 执行；未执行的库照常判定、只是不写该列）。`EVIDENCE_NOT_FOUND` 的 unknown 不再跨版本复用。只把当日 10 条 unknown 的工作项重置为 pending 并重判：10 次调用、35,753 输入 / 3,901 输出 token、无重试；8 条转为已判定（正面 +5、中性 +3），剩 2 条：1 条依据含 Markdown 链接 `[文本](URL)`（模型只引用链接文字），1 条正文未出现品牌、仅引用了品牌域名链接（`MENTION_DISAGREE`，模型判断合理）。当日结果：正面 21、中性 7、负面 0，健康度 87.5，覆盖率 82.4%。
- **情感因子分组（2026-11-02，Trellis `11-02-reputation-sentiment-factors`，源码就绪，未部署）**：`ReputationSummaryView.terms`（全局 Top 50）改为 `termGroups: { positive, neutral, negative }`，属于公开契约的破坏性变更，需要与 Jogeoai.web 同批发布。每个印象词只归入其主要极性一组；组内按该极性回答数、总贡献回答数、出现次数降序，再按 `termKey` 升序排列，各取 `ReputationRules.TopTermsPerPolarity = 10`。先分组再截断，回答数少的负面词不会再被正面词挤掉。词的提取、过滤与 `term` 下钻口径不变，无迁移。验证：`dotnet build Jogeoai.slnx` 0 错误；`dotnet test tests/Jogeoai.Tests --no-build --filter "FullyQualifiedName~ReputationProjectorTests|FullyQualifiedName~ReputationSqlTests"` 通过 13 项，`ReputationSqlTests` 3 项因未设置 `JOGEOAI_REPUTATION_TEST_CONNECTION` 跳过。随后经用户确认，用 root 在本机容器建临时库 `jogeoai_reputation_tmp_20261102`，依次执行 001–041（共 111 张表），把该库设为 `JOGEOAI_REPUTATION_TEST_CONNECTION` 后重跑同一过滤条件：16/16 通过，0 跳过；按词筛选的列表计数与分组后的因子回答数一致。临时库已删除。前端类型由临时 5280 实例导出 OpenAPI 后重新生成，5260 上运行的仍是旧构建。
- **情感因子按词极性下钻（2026-11-02，Trellis `11-02-reputation-term-polarity-drill`，源码就绪，未部署）**：`GET …/monitoring/records` 新增 `termPolarity=positive|neutral|negative`，只保留 `term` 以该极性出现的回答。它与回答整体判定 `sentiment` 相互独立，两者按且组合；在分页前筛选，参与 `totalCount` 与游标作用域（`:termPolarity:{极性}`）。
  - 只能与 `term` 一起使用：单独传或取值不支持返回 400 `INVALID_FILTER`。详情接口接受同一参数，只做相同校验，不改变详情内容。
  - 不带三项口碑筛选的请求仍走原 10 参数入口；单独传 `termPolarity` 也会进入口碑入口，因此会返回 400，不会被静默忽略。
  - 成员判定仍只用 `ReputationProjector.MatchesRecordFilter`（新增可选参数 `termPolarity`），因此情感因子的 `positiveAnswers` / `neutralAnswers` / `negativeAnswers` 等于同一快照下 `records?term=X&termPolarity=P` 的条数。接口重载 `IAnalyticsQueryStore.MonitoringRecordsAsync(..., sentiment, term, termPolarity, ct)` 新增一个位置参数。无迁移；前后端需同批发布，新前端打到旧后端会收到未知参数 400。
  - 验证：`dotnet test tests/Jogeoai.Tests --filter "FullyQualifiedName~ReputationProjectorTests|FullyQualifiedName~ReputationApiTests"` 22/22 通过，新增分极性计数一致性、`termPolarity` 校验、游标作用域和详情校验用例。`ReputationSqlTests` 已补同口径断言并通过编译，但需要 MySQL 隔离库，本次未运行。前端类型由临时 5280 实例重新生成，实例已停止；5260 未重启，运行的仍是旧构建。
- **部署顺序**：先在目标库执行 038（mysql 客户端加 `--default-character-set=utf8mb4`），再发布 API 与 Worker。分类器评测获批后，才能设置 `AutoEnqueueSinceUtc` 并开启 Worker。回滚：关闭 Worker、回退 API 即可，四张新表可保留。

## 文章创作后端管线（2026-10-30，本机已部署并真实验收；前端未接入）

Trellis `10-30-article-studio-backend-pipeline`（父任务 `10-30-article-studio-two-step`，前端与编辑器后端属其他子任务）。新增 `GET /api/v1/sites/{siteId}/article-studio/prepare`、`POST .../article-studio/generations`（`Idempotency-Key`，预占 100）与 `GET .../article-studio/generations/{taskId}`，以及 Worker 中 `pipeline=studio_v1` 任务的六阶段管线（引用源 → 抓取 → 结构提取 → 联网搜索 → 应用要求 → 生成）。旧向导接口与 Worker 旧任务路径不变；生成单价抽为常量 `ArticleGenerationPricing.UnitPrice = 100`（价格不变）。

- **迁移**：[040](db/migrations/040_article_studio.sql)（任务加 `pipeline` / `include_competitors` / `optimization_goal`，`extra_requirements` 改 TEXT，新表 `article_generation_task_stages`、`article_generation_task_sources`；回滚见 [rollback/040](db/migrations/rollback/040_article_studio_rollback.sql)）。2026-10-30 已在本机 `jogeoai` 执行（备份 `~/jogeoai-backups/jogeoai-pre040-20261030-175332.sql.gz`；107 → 109 张表，业务行数不变，既有 5 个任务为 `legacy`），5260 API 与完整 Worker 已重启为新构建；未执行 040 的库上 studio 提交与轮询返回 503 `ARTICLE_STUDIO_SCHEMA_NOT_MIGRATED`，旧向导与 Worker 认领不受影响。部署顺序：先执行 040，再发布 API / Worker。
- **联网搜索**：阶段 4 复用 `deepseek-article` 凭据调用 DeepSeek Anthropic 兼容 Messages + `web_search_20250305`，只有该行 `web_search_enabled=1` 时才调用（默认 0，阶段显示「联网搜索未启用」并跳过，文章照常生成）。该 Key 是否支持 `web_search` 未做真实探测。
- **验证**：`dotnet build Jogeoai.slnx` 0 错误；定向测试 89 项。2026-10-30 在隔离临时库（已删除）执行 001–040：040 重复执行、回滚两次、重新应用的完整 schema 比对一致；13 个 SQL 测试逐类串行两轮全部通过，并行整组一轮 89/89（另两轮出现夹具 `subject_terms` 建品牌间隙锁死锁，与 040 无关）。联网搜索：本机 `deepseek-article` 已开启 `web_search_enabled`，经用户批准做真实探测（`ArticleWebResearchLiveTests`，默认跳过），返回 6 次搜索、2778 字摘要、30 条带标题来源，请求与解析格式确认（AC-A7 通过；首次探测因报告目录缺失丢失结果，共调用 2 次）。真实链路（AC-A8 通过）：优联品牌提问「需要能通过多个弯或P弯…」开启竞品，6 阶段依次推进——高频引用源 12 条（11 引擎）、抓取 12 条全部失败（本机 Clash fake-ip 触发私网守卫，属环境问题）、结构提取因无正文跳过、联网搜索 7 次 30 条来源、成文首次因篇幅校验 `ARTICLE_GENERATION_LENGTH_INVALID` 失败后自动重试成功；生成 2254 字 `generated` 版本，预占 100 → capture 一次，可用积分减少 100。Swagger（5260 `/openapi/v1.json`）含三个 article-studio 端点及中文说明（AC-A6 通过；枚举按现有约定显示为 integer）。验收中发现并修复既有缺陷：素材预览对内存上下文求摘要，而 MySQL JSON 列会改写部分 double（`7.6000000000000005` → `7.6`），提交与 Worker 用回读快照重算摘要永远不一致，导致 409 `KNOWLEDGE_CONTEXT_STALE`（旧向导同样受影响）；现改为对落库后回读的上下文求摘要（`ArticleMaterialSelectionService.PreviewAsync`，回归测试 `ArticleMaterialPreviewHashTests`）。细节见 [内容管理说明](docx/content-management.md)「文章创作（智能学习）后端管线」。

## 文章编辑器后端（2026-10-30，本机已部署并真实验收；前端未接入）

Trellis `10-30-article-editor-backend`（父任务 `10-30-article-studio-two-step` 子任务 B）。新增 `PUT /api/v1/sites/{siteId}/articles/{articleId}/draft`（自动保存：未被发布的人工编辑版本原地覆盖，`editRevision` 乐观锁，冲突 409 `ARTICLE_VERSION_CONFLICT`，不改标签）、`POST .../articles/{articleId}/refine`（智能微调，`Idempotency-Key`，预占 40 → 释放连接后同步调用 `deepseek-article`（≤120 秒）→ 成功写 `refined` 版本并结算，失败释放；`credits/usage` 新类型 `article_refine`）、`POST /api/v1/sites/{siteId}/content/images` 与 `GET /api/v1/content/images/{imageId}`（PNG / JPEG / WebP / GIF ≤5 MB、魔数校验、同租户读取、nosniff；对象键 `content-images/{tenantId}/{imageId}`，复用知识库对象存储），以及 `GET .../articles/{articleId}/export?format=docx`（默认 markdown 不变）。draft 与 PATCH 共用编辑侧安全检查（拒绝 script / iframe / object / embed / form、`on*=`，以及链接 / 图片地址与 HTML 属性中的 `javascript:` / `data:`；新增图片只允许平台图片或 https；不禁止代码块；正文与当前版本相同时不检查，已有图片地址可保留）；PATCH 新增可选 `expectedRevision`，防止旧页面覆盖自动保存的草稿。新增依赖 `Markdig` 1.5.1（BSD-2-Clause）与 `DocumentFormat.OpenXml` 3.5.1（MIT）。

- **迁移**：[041](db/migrations/041_article_editor.sql)（`version_kind` 加 `refined`、`edit_revision`、新表 `article_refine_operations` / `content_images`、`credit_ledger.refine_operation_id`；回滚 [rollback/041](db/migrations/rollback/041_article_editor_rollback.sql)）。**未在任何库执行**；未执行时草稿 / 微调 / 图片返回 503 `ARTICLE_EDITOR_SCHEMA_NOT_MIGRATED`，详情与用量按旧列读取。部署顺序：先执行 041，再发布 API。
- **验证**：`dotnet build Jogeoai.slnx` 0 错误；定向测试 176 项（通过 159、跳过 17 个需隔离库的 SQL 用例，失败 0）；检查修正后同一过滤加 `Knowledge` 共 232 项（通过 212、跳过 20、失败 0）。2026-10-30 隔离库 `jogeoai_editor_test_20261030`（root 创建，执行 001–041，用后已删除）：041 第二次执行、回滚两次、重新应用的完整 schema（列 / 约束 / 索引 / CHECK / 外键）比对一致，回滚不影响 040 对象，应用与回滚之差恰为 041 计划对象（AC-B1 通过）；SQL 类逐类串行两轮全部通过：`ArticleDraftSqlTests` 3、`ArticleRefineSqlTests` 2、`ArticleStudioSubmitSqlTests` 2、`ArticleGenerationSubmissionSqlTests` 4、`ContentStoreSqlTests` 6、`CreditStoreSqlTests` 1、`KnowledgeProductStoreSqlTests` 1（AC-B2 / AC-B3 SQL 部分通过）；`KnowledgeStoreSqlTests` 固定要求库名 `jogeoai_stage1_20260926`，未运行。未专门验证同租户两个微调并发预占的死锁情形。本机真实验收（AC-B7 通过）：备份 `~/jogeoai-backups/jogeoai-pre041-20261030-194440.sql.gz` 后执行 041 并重启 API / Worker；草稿原地覆盖与 409 冲突、PNG 上传与读取、docx 导出（标题 / 列表 / 表格 / 内嵌图片）、框选段落真实微调 1 次（约 2 秒，capture 40，同键重放不重复扣费，`credits/usage` 含 `article_refine`）均符合预期。待验收：docx 在 Word 中打开、前端接入；测试图片 1 张未清理。细节见 [内容管理说明](docx/content-management.md)「文章编辑器后端」。

## 当前实现

当前源码已具备国内 AI Web Collector 控制面和结果入库、品牌管理、分析、知识库及内容管理能力。以下按模块列出实现；部署和实测结论以各段绝对日期及对应说明为准：

- API 提供进程健康检查、MySQL readiness、Cloro 连接配置和 Cloro 测试调用；Development 环境提供 OpenAPI JSON、Swagger UI 和本地跨域调试。
- Worker 启动时检查数据库连接；国内 Collector 租约每 30 秒恢复，Cloro 任务由独立 Submit/Poll 循环领取和恢复。Cloro 轮询会先读取 Provider 状态再应用本地超时，避免服务短暂停止后把已经完成的外部任务误标为失败；旧版 Worker 已留下的超时失败记录可按 Run 显式重核；浏览器执行仍由独立 Node.js Collector 负责。
- MySQL 8.4 使用独立命名卷，默认库 `jogeoai`、应用账号 `jogeoai_app`，字符集 `utf8mb4`、排序规则 `utf8mb4_0900_ai_ci`、时区 UTC；`db/migrations/002_visibility_foundation.sql` 已落地首期业务基础表。
- 连接验证使用 MySqlConnector 执行 `SELECT 1`；国内采集仓储使用参数化 MySQL 事务，支持租约、心跳、结果幂等和引用入库。
- 已加入 Cloro 适配器和 `cloro_provider_settings` 连接表；API 可以保存 Cloro API URL/Key，并通过异步 Submit/Poll 流程测试获取一条标准化回答。业务 Run 使用统一的 `POST /api/v1/visibility-runs` 与 `targets` 字段，Worker 会持久化外部任务 ID 后恢复轮询，结果进入统一 Check、Citation、Usage 和 Outbox 数据链路。
- `003_domestic_collector.sql` 增加国内平台入口、采集任务、租约审计、提交审计和 `visibility_checks` 采集元数据；启动服务不会自动执行业务 migration。
- `004_cloro_visibility.sql` 在 002/003 通用表上补充 Cloro 的租约版本、外部任务时间点、答案/搜索词/采集元数据、受控原始响应哈希及任务关联约束；启动服务不会自动执行该 migration。
- `005_elmo_management_orchestration.sql` 新增品牌/竞品主体配置、Prompt 版本/标签、站点 Target、固定预览和本地时区计划，并将旧 Check 回填到 Target 和 Prompt v1。Cloro Run 支持同引擎跨地区及 1–10 个样本；旧 Cloro 和国内请求仍默认单样本。管理 API 提供导入预览、幂等提交、配置版本保护；计划 Worker 以时隙唯一键防重。
- `006_elmo_analysis_core.sql` 新增不可变主体分析版本、Check 到达和分析完成水位、可重试工作项、Check 级主体 presence、原文 UTF-16 提及证据与 15 分钟查询快照。采集完成事务登记分析工作项，独立 Worker 只读取已入库回答；新版本完整就绪后原子发布，不再次调用 Cloro。旧快照缺匹配词时标记 unknown，不用当前别名伪造历史。
- `007_elmo_detailed_analytics.sql` 新增搜索词可用状态、版本化 Check-query/词项事实、外部引用事件与分类、Google 内部链接事实；旧空搜索词保持 unknown。旧站点会排入完整细分版本重算，全部事实写完后才切换已发布版本。重复 URL 的原引用位置保留，统计时按 Check × URL 计一次。
- `011_dashboard_analytics.sql` 为引用细分事实增加 URL 规范化状态，并将仍含 `utm_*` 的历史事件标记为待重算；品牌概览和模型监控接口会回显该质量状态。该 migration 需在 007 之后手动执行。
- `012_rag_knowledge.sql` 新增公司级/品牌级知识资料、文件夹、版本、摄取任务、片段和上下文审计表；启动服务不会自动执行。首期知识库接口支持 PDF、DOCX、Markdown 和 TXT 上传、异步处理、停用/有效期过滤和可追溯检索。开发环境默认将原文件保存在 `Knowledge__StorageRoot` 指定的本地私有目录。本机正式库 `jogeoai` 已于 2026-09-29 执行该 migration。
- `013_knowledge_context_snapshot.sql` 为写作上下文保存完整响应快照；本机正式库 `jogeoai` 已在 012 后执行，其他已执行 012 的目标库仍需手动执行 013，启动服务不会自动升级。
- `023_knowledge_products.sql`（2026-11-03 起素材库页面已由 043 品牌知识库替代，产品接口 deprecated，见上文「品牌知识库维度列表」；以下为原记录。2026-10-19 源码就绪，尚未在任何库执行）新增品牌级产品信息、产品标签与识别任务租约表，并为资料加 `source_kind` / `source_url`、版本加 `content_kind`。素材库新增 JPG / PNG 上传、添加链接、Luna 多模态识别（文本或 `image_url` 图片块，固定 `gpt-6-luna`，不扣积分、每租户每日 50 次）与人工逐字段核验入库；入库产品写入品牌知识库并成为文章生成 `options.products` 的「指定产品」。新构建依赖 023；本机 `jogeoai` 已于 2026-10-19 备份后执行 023 并重放验证，5260 API 与完整 Worker 已重启为新构建（Swagger 实查 10 条产品/链接路由）。真实链路已验证 PNG 上传经 Worker 发布为 `content_kind=image` 的 0 片段版本；2026-10-19 已用真实登录会话完成 `completion` 与 `url` 两种识别（gpt-6-luna，分别 6.4 s / 43 s，产品进入待核验并填入 14 条规格）；此前的网关 403 与链接「重定向次数过多」分别由本机 Clash 全局/fake-ip 代理和抓取器误用去 `www.` 规范化地址造成，后者已修复（`ArticleSourceReader.NormalizeFetchUrl`）。本机 `.env` 必须显式设置绝对路径的 `Knowledge__StorageRoot` 与 `Knowledge__VectorRoot`，否则 API 与 Worker 各自落在项目目录下的 `.data`，Worker 会报 `KNOWLEDGE_OBJECT_MISSING`。接口与证据见 [素材库产品信息说明](docx/knowledge-product-library.md)。
- `024_subscription_plans.sql`（2026-10-19 本机 `jogeoai` 已备份后执行并重放验证，5260 API 与完整 Worker 已重启为新构建；其他库未执行）新增套餐目录（5 行种子：免费体验版 / 入门版 / 基础版 / 标准版 / 专业版）、租户订阅（一租户一行）、订阅事件、卡密批次与卡密表，为 `credit_ledger` 增加 `expire` 类型，并把既有租户回填为免费体验版（不写账本、不改余额）。注册改为按 `free_trial.signup_credits` 赠送并写订阅行；品牌 / 提问 / 竞品的创建、启用、导入、引导完成入口在同一事务内按当前套餐校验上限（409 `PLAN_LIMIT_EXCEEDED` 含 `details`）；新增 `GET /api/v1/plans`、`GET /api/v1/subscription`、`POST /api/v1/subscription/redeem` 与系统管理员的卡密 / 指派 / 套餐维护接口；Worker 每 60 秒滚动额度周期（清零 `expire` + 发放 `grant` 同事务、幂等）并把到期付费订阅降为免费体验版。新构建依赖 024，须先执行迁移再发布 API / Worker；回填后超出免费上限的租户需管理员指派套餐。定向测试 56 项通过、10 项隔离库 SQL 场景跳过；本机真实验收已覆盖上限 409、指派、卡密六种分支、周期滚动与到期降级。执行迁移须加 `--default-character-set=utf8mb4`（容器内 mysql 客户端默认 latin1，本次中文种子乱码已修复）。说明与证据见 [套餐说明](docx/subscription-plans.md)。
- `025_answer_provider_settings.sql`（2026-10-20 本机 `jogeoai` 已执行两次验证幂等；其他库未执行）新增 `answer_provider_settings`：按 Provider 一行保存 API 回答 Provider 的地址、模型、明文凭据（只回显后 4 位提示）、启用、超时与 JSON 选项，并 seed 未启用的 `doubao_api`（火山方舟 Responses API + 豆包搜索）。新构建不依赖它即可运行 Cloro；未执行时 `/api/v1/providers/doubao/*` 返回 503 `ANSWER_PROVIDER_SCHEMA_NOT_MIGRATED`，目录中的豆包 Target 为 `unavailable`。执行须加 `--default-character-set=utf8mb4`。说明见 [豆包接入说明](docx/doubao-answer-recording.md)。
- `026_site_remark_description.sql`（2026-10-20 本机 `jogeoai` 已备份后执行并重放验证，5260 API 已重启为新构建；Worker 未重启，其他库未执行）为 `sites` 增加 `remark`（≤200）与 `description`（≤2000）两列，供新前端品牌管理页使用；`BrandWrite / BrandPatch / BrandView` 末尾新增同名可选字段。新构建读取品牌时会 SELECT 这两列，**须先执行 026 再发布 API / Worker**；只改备注 / 描述 / 报告标题的 PATCH 走不递增 `subjectConfigVersion` 的路径。同批新增 `GET /api/v1/team/summary`（团队级跨品牌汇总：四项指标及上一周期差值、启用提问 / 分组 / 竞品计数、文章数 / 追踪中 / 被引用、启用监控目标；行业排名字段恒为 null）与 `GET /api/v1/credits/usage`（积分用量按团队时区自然日 × 操作类型聚合，capture 金额从 `credit_reservations` / `monitoring_credit_items` 关联取得；viewer 403，member 只看本人），并修复 `credit_ledger` 的 `expire` 行被映射为 `reversal` 的问题（`CreditLedgerEntryType.Expire`）。后端构建 0 错误、定向测试 87 通过 / 5 项隔离库 SQL 跳过；本机真实验证（团队汇总与品牌概览同区间一致、用量与账本关联对账一致、品牌备注编辑不递增版本）已通过。真实验证同时修复了内容管理既有 SQL 的两个潜伏缺陷（`ArticleMonitoringQuery` 的保留字别名 `release` 与 `DISTINCT` 查询 ORDER BY 列不在 SELECT 列表），此前 `article-monitoring/summary` / `domain-rankings` 在本机真实数据上返回 500。说明见 [套餐说明](docx/subscription-plans.md) 与 Trellis 任务 `10-20-fe-team-management`。
- 内容管理已有文章/版本、链接导入、RAG 素材快照、异步生文、引用监控及租户积分账本实现，注册赠送 500 积分。2026-10-28 起生文模型改为固定 DeepSeek-V4.1-Flash（DeepSeek 官方 Chat Completions，API 模型名 `deepseek-flash`，关闭思考），配置为 `research_provider_settings` 独立行 `deepseek-article`（[033](db/migrations/033_article_deepseek_provider.sql) 种子；本机未单独执行 033，由 `./scripts/set-article-deepseek-key.sh` 建行并写入 Key），不再读取 Luna 网关配置。本机 5260 API 与完整 Worker 已重启为该构建，真实生成 1 篇（选型 / 中篇）首次尝试成功，用时约 21 秒，扣 100 积分。本机 014 的 15 张表及 23 个内容 HTTP 操作已于 2026-10-06 只读核对；真实文章生成、账务并发、性能和部分 PRD 功能仍待验收。接口、计费和本机证据统一见 [内容管理说明](docx/content-management.md)。2026-11-04 文章列表新增引用表格参数与字段（`citationDays` / `cited` / `citationEngine` / `ownership`、`primaryLink`、`citationSummary.engines/firstObservedAt`、`citedCounts`、按最早/最近被引排序），按引用次数排序改为与行内次数同口径（含追踪范围），并修复被引筛选重复行与同秒文章翻页遗漏；无 migration，已用新构建只读起 5281 在真实数据验证，5260 尚未重启为该构建。2026-10-22 起素材预览对勾选的资料或指定产品直接取用其片段，中文问题在无 Embedding 时按二字词匹配，`und` 语言资料不再被文章语言过滤排除；无 migration、无公开契约变化，5260 API 已重启为新构建并用真实数据验证（Worker 未改动）。
- 2026-10-25 文章选型模板源码新增 `strategy=product_selection` 与 `article-writing-v2`：以主问题、关键词、冻结品牌介绍/产品证据及参考文章组织选型指南；只有中文中篇改为 2000–3000 个非空白字符，其他类型/档位范围不变。补充素材存于既有 JSON 快照扩展，显式 no-RAG 不读取品牌或产品事实；参考页只作结构参考，缺少型号/评分/报价不编造。同轮隔离库确认并修复提交 SQL 漏绑定 `@title`、领取上下文与成功结算的二进制 UUID 未转换问题；完整人工提交到保存/结算回归通过。无新 migration；真实 API/Worker 未重启，真实 Luna/账务仍待授权验收。当前契约及验证入口见 [内容管理说明](docx/content-management.md#产品选型模板2026-10-25源码)。
- 2026-10-25 文章格式排查后续：原失败文章实际为 `answer_guide`，留存旧版真实响应两次把 `answeredQuestion` 返回为字符串；历史原响应未保存，不能断言历史每次都错在该字段。修复明确十字段JSON类型及允许列表内的纠错反馈，87项定向测试及独立检查通过。20:00重启完整Worker，映射程序集与新构建一致；API/数据库未重启，无migration。20:35经最终确认仅提交一篇同配置真实验收，约49秒成功：正文2199个非空白字符、v1入库、task/batch succeeded、100积分capture一次；原失败文章及100积分released状态保留。新文章仍待发布，未开启新链接追踪。此结果不代表product_selection策略或所有模型响应均已验收。证据见 [排查与部署验证](.trellis/tasks/archive/2026-10/10-25-article-format-generation-diagnosis/verification.md)。
- 2026-10-22 修复使用知识库的文章生成提交必报 `KNOWLEDGE_CONTEXT_STALE`：素材预览按 Web JSON 口径计算上下文快照 hash，提交按默认口径重算，Worker 又对 MySQL JSON 列规范化后的原文取 hash，三处永远不一致。现统一由 `KnowledgeContextSnapshot` 对反序列化后的对象按 Web 口径序列化取 hash（今日之前的素材预览 hash 口径不变，仍可提交）。检索证据的 `location` 改为返回 `location_json.value` 原串（此前为整段 `{"value": ...}`），前端素材命中改显示「第 1–31 行」类区间。无 migration、无类型变化；5260 API 与 Worker 已用新构建重启，真实接口确认素材预览与 `location` 输出，带知识库的实际提交与 Worker 生成尚待真实验收。
- 2026-10-18 为新前端文章管理页补充（无新迁移）：文章列表响应改为 `ArticleListPage`（`items`、`nextCursor`、`totalCount`、`activeLinkCount`、六个键的 `stageCounts`），列表行新增后端派生阶段 `stage`（generating / generation_failed / pending_publish / publishing / tracking / archived）、关联提问快照 `prompt`、最新生成任务摘要 `generation` 与 `ownedPublication`，新增 `stage` / `language` / `ownedOnly` 筛选；详情新增生成依据 `generation`；新增 `GET /api/v1/sites/{siteId}/article-monitoring/domain-rankings`（复用品牌引用域名快照并附带每个域名已登记的文章/链接数）；登记发布链接时服务端按品牌主域名/附加域名计算官网归属。契约见 [.trellis/spec/backend/content-articles.md](.trellis/spec/backend/content-articles.md)；本机 5260 已于 2026-10-19 09:54 重启为包含这些字段的构建（Swagger 实查 24 个内容操作、`stage` / `language` / `ownedOnly` 参数与 `domain-rankings` 路由），其他环境以各自部署为准。
- 2026-10-28 为内容详情「推荐渠道」新增 `GET /api/v1/sites/{siteId}/article-monitoring/recommended-channels` 与迁移 `034_citation_domain_types.sql`（全局来源域名类型字典：品牌官网 / 媒体平台 / 电商B2B / 其他，1265 条规则，种子来自本机 1494 个引用域名的 AI 分类 + 用户审核，审核表 `docx/citation-domain-types-review.xlsx`）。接口复用 `domain-rankings` 的引用域名快照口径，只返回字典判定为媒体平台的非自有域名，按平台合并后按近 30 天引用降序取 Top N（默认 5）；字典未收录的新域名视为未分类、不推荐。分析口径 `website-category-v1` 不变。**须先执行 034 再发布 API**（未执行时该接口 500，其他接口不受影响）。034 已在隔离库连续执行两次验证幂等与 CHECK（隔离库已删除），2026-10-28 备份后执行到本机 `jogeoai`（1265 行），临时实例 5281 + 3012 预览核对真实推荐渠道只含媒体平台；本机 5260 API 尚未重启为该构建，其他环境未执行。契约见 [.trellis/spec/backend/content-articles.md](.trellis/spec/backend/content-articles.md)，Trellis 任务 `10-28-content-recommended-channels`。
- 2026-10-28 新增采集器账号池控制面，迁移为 `035_collector_session_profiles.sql`，**必须用 root 执行**（创建只追加触发器需要该权限）。
  - 新接口：`/internal/collector/leases` 支持可选的 `sessionProfileId`；新增 `eligibility`、`failures`、`session-events` 三个接口；新增系统管理员运维接口 `api/v1/admin/collector-pool/*`。
  - 派发规则：每账号配额与最小间隔、在线账号中最久未用者优先、平台最小间隔、北京时间每日总上限、验证码熔断、失败后退避重新入队。样本的账号取自租约记录，不读请求体。
  - 兼容：未登记账号的租户，领取行为不变；一旦登记（包括停用的账号），该平台不带账号 ID 的领取就返回空。
  - 附带修复：提交结果时的运行汇总只统计当前运行的任务，避免不同运行之间互相死锁，计算结果不变。
  - 已验证：在隔离库执行 001–035 并重放 035；22 个定向测试通过；临时实例 5281 的实际响应通过参考 Collector 的 zod 校验。
  - 本机部署：2026-10-30 备份后用 root 对 `jogeoai` 执行 035，两个只追加触发器已建、doubao/cn 平台启用；首次执行被一条人工归档审计的空失败码拦下，已在尝试记录失败码白名单中加入 `operator_archived` 并回填该行。5260 已是该构建，健康检查 200。
  - 冒烟准备：2026-10-30 已为专用租户 `16a7e7fd…`（站点“柯丽尔”）开启豆包入口、加入 Collector 白名单，并登记 `doubao-a`、`doubao-b` 两个账号。
  - 2026-10-30 有人值守两账号豆包冒烟完成：Run `185c49d4…` 4/4 成功，样本账号归属与租约一致，验证了平台/账号间隔、每小时配额、验证码等待超时与 `login_required` 退避重派；暴露 Collector 停止后 `.collector.lock` 残留问题（Collector 仓库已修复，未提交）。证据见 [账号池说明](docx/collector-account-pool.md)。
  - 运维说明与冒烟准备清单见 [账号池说明](docx/collector-account-pool.md)，Trellis 任务 `10-28-collector-account-pool`。
- `027_content_link_monitoring.sql`（2026-10-20 本机 `jogeoai` 已备份（`~/jogeoai-backups/jogeoai-before-027-*.sql.gz`）后执行，95 → 98 张表、业务行数不变；5260 API 与完整 Worker 已重启为新构建；隔离库 `jogeoai_content_test_20261020` 上 001–027 顺序执行并重放验证通过后已按用户确认删除；其他库未执行）：`content_articles.source_kind` 增加 `citation_discovered`，`content_publication_links` 增加 `discovered_check_id / discovered_at`，新增 `content_citation_discovery_state`、`article_monitoring_rounds`、`article_monitoring_round_items`，`credit_ledger` 增加 `article_monitoring_round_id`。新构建的完整 Worker 注册 `ArticleLinkMonitoringWorker`（`ArticleLinkMonitoring:Enabled=false` 可关闭）：按分析到达水位把大模型回答中引用品牌域名的链接自动建为「AI 引用发现」文章并开启追踪（首次回溯近 30 天），再为每个品牌按本地自然日给追踪中的发布链接开轮预占 10 积分/条、次日结算；余额不足当天跳过。新增 `GET /api/v1/sites/{siteId}/article-monitoring/billing`、列表行 `discovery`、详情链接发现字段、`credits/usage` 的 `article_monitoring` 类型。**新构建依赖 027（列表 SQL 读取新列），须先执行迁移再发布 API / Worker**；执行须加 `--default-character-set=utf8mb4`。后端构建 0 错误，定向测试 79 通过（含 8 项隔离库 SQL）。真实链路：首次重启暴露发现查询用错了归属值（存储值为 `brand`，不是 API 枚举的 `owned`），修正后重启 Worker 即对本机品牌自动建文 20 篇（标题读取因开发机代理 fake-ip 触发私网守卫失败，属环境问题）并开当天轮次预占 200 积分；次日结算待观察。前端已按新合同重新生成类型并完成页面改动（Trellis 子任务 `10-20-fe-content-link-monitoring`）。说明见 [内容管理说明](docx/content-management.md) 的「链接自动发现与每日计费」。
- `030_deepseek_answer_provider.sql`（2026-10-23 本机 `jogeoai` 已执行两次验证幂等；其他库未执行）依赖 025，只向 `answer_provider_settings` 插入 `deepseek_api` 种子：DeepSeek Anthropic 兼容接口、`deepseek-flash`、关闭思考、`maxToolCalls=5`，未启用、无凭据。未执行时目录中的 DeepSeek API Target 为 `unavailable`，手动 Run 返回 503 `DEEPSEEK_NOT_CONFIGURED`。执行须加 `--default-character-set=utf8mb4`。说明见 [DeepSeek API 接入说明](docx/deepseek-api-answer-recording.md)。
- `036_packy_answer_providers.sql`（2026-10-29 本机 `jogeoai` 已执行；其他库未执行）依赖 025，只向 `answer_provider_settings` 插入 `packy_qwen`、`packy_glm`、`packy_kimi` 三行种子（PackyAPI 千问 / 智谱清言 / Kimi，未启用、无凭据、超时 240 秒）。未执行时这三个 Target 为 `unavailable`，手动 Run 返回 503 `PACKY_NOT_CONFIGURED`。执行须加 `--default-character-set=utf8mb4`。说明见 [PackyAPI 回答采集说明](docx/packyapi-answer-recording.md)。
- `037_packy_glm_fast_mode.sql`（2026-10-29 本机 `jogeoai` 已执行；其他库未执行）依赖 036，把仍是 `glm-5.2` 的 `packy_glm` 行改为 `glm-5.3-flash` + `options.reasoningEffort=low`（贴近网页端“快速”），不动凭据与开关。需要配合同期 API / Worker 代码；旧进程会忽略 `reasoningEffort`。执行须加 `--default-character-set=utf8mb4`。
- `038_reputation_sentiment.sql`（2026-10-30 已在本机 `jogeoai` 执行；其他运行库未执行）
- `039_reputation_model_quote.sql`（2026-10-30 已在本机 `jogeoai` 执行）依赖 038，给 `reputation_check_sentiments` 增加可空列 `model_quote`，可重放依赖 006，新增口碑情感工作项、判定、印象词与补录任务四张表；见上文「品牌口碑后端」。
- `040_article_studio.sql`（2026-10-30 先在已删除的隔离临时库验证，再经备份后在本机 `jogeoai` 执行）依赖 014，为文章生成任务增加管线、竞品开关、优化目标与六阶段/来源两张表；见上文「文章创作后端管线」。
- `041_article_editor.sql`（2026-10-30 源码，**未在任何库执行**）依赖 014，增加 `refined` 版本类型、草稿修订号、智能微调预占表、编辑器图片表与账本外键列；见上文「文章编辑器后端」。
- `042_prompt_target_scopes.sql`（2026-11-03 先在隔离库验证，再经备份后用 root 在本机 `jogeoai` 执行；其他库未执行）依赖 002 / 005，为提问增加平台范围与创建来源两列和 `prompt_target_scopes` 表。须先执行迁移，再同时发布 API 与 Worker；见上文「新增监测问题弹窗与按提问平台范围」。
- `045_auth_login_throttle.sql`（2026-11-08 先在已删除的隔离库 `jogeoai_login_throttle_test_20261108` 验证，再经备份 `~/jogeoai-backups/jogeoai-pre045-20261108-095933.sql.gz` 后用 root 在本机 `jogeoai` 执行并重放；其他库未执行）依赖 008，只新增 `auth_login_throttle`（按规范化邮箱 SHA-256 记录连续登录失败与锁定截止时间，不存邮箱明文与 IP）。**须先迁移再发布 API**：未执行时登录返回 503 `AUTH_THROTTLE_SCHEMA_NOT_MIGRATED`。可选清理（不自动执行）：`DELETE FROM auth_login_throttle WHERE updated_at < UTC_TIMESTAMP() - INTERVAL 7 DAY;`。回滚见 `db/migrations/rollback/045_auth_login_throttle_rollback.sql`（先回退 API）。
- `046_packy_yuanbao_provider.sql`（2026-11-08 经备份 `~/jogeoai-backups/jogeoai-pre046-20261108-215037.sql.gz` 后在本机 `jogeoai` 执行；其他库未执行）依赖 025，只向 `answer_provider_settings` 插入 `packy_yuanbao` 种子（PackyAPI `hunyuan-officially` 分组的混元 `hy4`，`maxToolCalls=3`、超时 540 秒，未启用、无凭据）。未执行时元宝 API Target 为 `unavailable`，手动 Run 返回 503 `PACKY_NOT_CONFIGURED`。执行须加 `--default-character-set=utf8mb4`。说明见 [PackyAPI 回答采集说明](docx/packyapi-answer-recording.md) §6。
- `047_answer_mode_frequency.sql`（2026-11-09 先在隔离库 `jogeoai_answer_mode_20261109` 验证，再经备份 `~/jogeoai-backups/jogeoai-before-047-20261109-162452.sql.gz` 后用 root 在本机 `jogeoai` 执行；其他库未执行）依赖 002 / 005，为 `site_targets` 增加回答模式、采集频率与频率锚点三列，为 `visibility_checks` 增加 `answer_mode`，不改索引；同时把每日监控主计划从 09:00 改为 09:01（`next_slot_at` 同步顺延 1 分钟）。须先执行迁移，再同时发布 API 与 Worker；见上文「品牌监控模型配置：回答模式 × 采集频率」。
- `048_team_monitoring_defaults.sql`（2026-11-11 先在隔离库 `jogeoai_team_monitoring_20261111` 验证重放与回滚往返，再经备份 `/tmp/claude-501/jogeoai-backup-20261111/jogeoai-before-048.sql` 后用 root 在本机 `jogeoai` 执行；其他库未执行）依赖 005 / 047，新增 `tenant_monitoring_defaults`、`tenant_monitoring_default_cells` 和 `site_brand_settings.monitoring_follows_default`，并按存量配置一次性归类跟随 / 单独配置；不写 `site_targets`。须先迁移再发布 API（未执行时团队接口 503 `TEAM_MONITORING_SCHEMA_NOT_MIGRATED`）。回滚见 `db/migrations/rollback/048_team_monitoring_defaults_rollback.sql`；见上文「团队默认监控配置」。
- `049_app_channel.sql`（2026-11-17 源码；**只在隔离库执行过**，包括用应用账号执行和重放；本机 `jogeoai` 和其他库都未执行）：
  - 依赖 003 / 004 / 035 / 048。
  - 改动 4 张现有表：
    - `visibility_checks`：`capture_mode` 的 CHECK 增加 `app`，新增 `app_binding_id`；
    - `collector_platform_entries`：`channel` 的 CHECK 放开，新增 `unavailable_reason`，并插入 7 条 `consumer_app` 入口；
    - `collector_tasks`：`channel` 的 CHECK 放开，新增 5 列和 App 领取索引；
    - `collector_attempts`：新增 `app_binding_id`，失败码的 CHECK 增加 App 的取值。
  - 新增 6 张表；`collector_platforms` 不动。
  - 可以用应用账号执行，不需要 root，不建触发器。执行须加 `--default-character-set=utf8mb4`。替换 `visibility_checks` 的 CHECK 会重建整张表，在有数据的库上执行前先评估耗时。
  - 须先迁移，再同时发布 API 与 Worker。未执行时 App 相关入口返回 503 `APP_CHANNEL_SCHEMA_NOT_MIGRATED`，现有功能不受影响。
  - 回滚见 `db/migrations/rollback/049_app_channel_rollback.sql`。存在 App 回答、App 站点目标、启用的 App 计划或 App 任务时，脚本拒绝执行。
  - 见上文「App 渠道采集」和 [App 渠道说明](docx/app-channel.md)。
- `008_auth_brand_onboarding.sql` 新增邮箱密码用户、可撤销会话、租户成员、品牌引导进度、全局研究 Provider 配置和异步品牌研究任务，并为站点增加报告标题与子域名开关。`009_system_admin_provider_access.sql` 再增加默认关闭的系统管理员标记，`010_research_provider_plaintext_credential.sql` 将 Provider 凭据切换为明文列并写入默认 OpenAI 配置；三份 migration 均需手动执行，只有数据库显式授权的系统管理员可以修改全局 Provider。

## 品牌概览首位 / 前三提及率（2026-11-02）

`GET /api/v1/sites/{siteId}/analytics/brand-dashboard` 的 `metrics` 末尾新增 `firstPositionRate`、`topThreeRate`（`MetricRatio`）：分母为合格回答数，分子为本品牌名次 = 1 / ≤ 3 的回答数。名次与曝光分 v2 的名次分同一口径（只计已配置主体，回答只提及本品牌时为第 1 名，与平均排名跳过这类回答不同）；已提及但缺少出现位置的回答只计入分母。纯增量字段，无 migration、无新查询参数。前端概览顶部改为七格「品牌数据概览」，其中品牌 AI 健康度（0.8 × 曝光分 + 0.2 × 口碑健康度）在前端合成，不是后端字段。已验证：`dotnet build Jogeoai.slnx` 0 错误，`DashboardAnalyticsTests` 32 项通过；5260 API 已于 2026-11-02 在 Claude 终端页签 c1 重启为新构建，优联近两周真实数据（对话 600、提及率 38.5%、首位 22.7%、前三 35.5%）与 3001 预览一致。Worker 未重启（本改动不涉及）。Trellis 任务 `11-02-overview-kpi-expansion`。

## 品牌概览主体逐日序列（2026-10-16）

`GET /api/v1/sites/{siteId}/analytics/brand-dashboard` 新增 `subjectTrends`：本品牌与所选竞品逐日的提及率、平均排名和样本数，只含当日有效检测数大于 0 的日期，与 `trend[]` 同一快照和时区日界；原字段不变，无 migration。前端洞察分析页与概览「品牌趋势」的竞品曲线使用该字段。已验证 `DashboardAnalyticsTests` 与 `DashboardAnalyticsSqlTests` 8/8、`dotnet build Jogeoai.slnx` 0 警告 0 错误，以及 5280 临时实例 Swagger 中文描述。`DashboardAnalyticsSqlTests` 只连 `JOGEOAI_DASHBOARD_TEST_CONNECTION` 指向的已迁移隔离库，未设置时跳过，且拒绝 `jogeoai` 业务库。2026-10-16 已按用户要求停止改动前启动的 5260 API，尚未重新启动；Worker 仍是改动前的进程（本改动不涉及 Worker）。

## 回答记录单条指标与总数（2026-10-17）

`GET /api/v1/sites/{siteId}/analytics/monitoring/records` 的响应类型由 `AnalyticsPage<MonitoringRecordView>` 改为 `MonitoringRecordPage`，新增 `totalCount`（同一快照、同一筛选含搜索和提及状态下分页前的记录数，翻页不变）；每行及 `GET …/monitoring/records/{checkId}` 详情新增 `brandMetrics`（`CheckBrandMetrics`：`ranking`、`exposure`），列表与详情由同一纯函数 `DashboardAnalyticsProjector.CheckBrandMetrics` 计算。单条排名 `check-subject-first-offset-v1` 按各主体在回答中的首次出现位置取名次，可用时分子分母均为 1；单条曝光分数沿用 `exposure-v1` 权重（提及 0/1 × 50% + 该回答内声量份额 × 30% + 自有外部引用 0/1 × 20%）。与仪表盘口径有两处差异：回答只提及本品牌时记第 1 名（仪表盘平均排名跳过这类回答）；回答未提及任何已配置主体时声量按 0 计，分数为 0 或 20（仪表盘返回 `ZERO_REQUIRED_DENOMINATOR`）。品牌未提及、缺少位置、未配置竞品、采集或分析未就绪时返回 `null` 并携带原因代码，不以 0 代替。无新路由、查询参数或 migration；自有引用判定抽为 `OwnCitationDomains`/`IsOwnCitationUrl`，`LoadFactsAsync` 行为不变。2026-10-17 已验证 `dotnet build Jogeoai.slnx` 0 错误，定向测试 122/122 通过（`DashboardAnalyticsTests`、`OwnCitationTests`、`MonitoringAnswerDetailTests`、`MonitoringAnswerApiTests`、`MonitoringRecordSearchTests`、`CitationDomainApiDocumentationTests`），5280 临时实例 Swagger 中文描述正确并据此重新生成前端类型；`DashboardAnalyticsSqlTests` 新增的总数与列表/详情一致断言已在临时隔离库 `jogeoai_dashboard_test_20261017`（22 份迁移）上通过 3/3；本机 5260 API 已于同日经用户授权用新构建重启（Worker 未动），真实账号页面核对待执行。（2026-10-22 起为 exposure-v2，见下文“提及识别 mention-v2 与曝光分 exposure-v2”）

## 引用域名详情（2026-10-23，后端）

供首页「引用域名排行榜」的域名详情抽屉使用。这些都是只读接口，复用 summary 快照，只有追加字段，没有 migration。前端抽屉已经在 Jogeoai.web 实现（见该仓库 README「引用域名详情抽屉」一节），并在 3001 真实 API 预览中核对过。

- 新增 `GET …/citation-domain-summary/domain-detail?snapshotToken=&domain=[&engine=]`，返回该域名的全部引用次数和我的引用次数、按平台的分布（不受 `engine` 影响），以及按快照时区逐日、补零的趋势（受 `engine` 影响）。全部引用次数和 `/domains` 行的口径相同。
- `/urls` 新增可选参数 `engine`、`q`（标题或 URL 包含匹配，1–100 字）、`mine`。这三个参数必须和 `domain` 一起使用，否则返回 400。行上新增 `isMine`、`myCitationEvents`、`promptCount`，以及 `domainRank`（域内 1..N，在 engine 过滤之后、q/mine 过滤之前排定；不传 `domain` 时为 null）。全局 `rank` 不变。不传新参数时，原有输出不变。
- 「我的」指引用 URL 命中本品牌在内容管理中的发布链接（包括 Worker 自动发现的链接），并满足与文章监控相同的发布时间、停用和监控范围门槛。没有登记的自有页面不算。这和首页 KPI 中按品牌域名判定的「自有引用」口径不同。
- `GET /api/v1/sites/{siteId}/monitoring/records` 新增 `citedDomain`，只返回引用了该域名的回答，参与总数和游标计算。详情接口接受这个参数，但只做校验。

2026-10-23 验证结果：
- `dotnet build Jogeoai.slnx` 0 错误。
- 定向非 SQL 测试 150/150 通过。
- 隔离库上的 `CitationDomainSqlTests` 通过 9/10，其中新增的 2 个都通过。剩下的 1 个失败是已知的旧测试：它期望的 provider 列表里没有 `doubao_api`，与本任务无关。
- `ArticleLinkMonitoringSqlTests` 2/2，`DashboardAnalyticsSqlTests` 3/3。
- 本机真实库只读冒烟（临时 5281 实例，没有重启 5260）：
  - szulink.com.cn 的 detail 总数 140 与榜单行一致，我的引用 46 与独立 SQL 逐平台核对一致。
  - 按 `citedDomain` 筛选的回答记录总数等于该域名的 `answerCount`（28/28、71/71）。
  - 各接口耗时都在 0.2s 以内。
- 5260 已于 2026-10-23 重启到新代码（终端标签「Jogeoai API 5260」），Worker 没有重启：Worker 侧只把门槛 SQL 提取为共享常量，行为不变。同日修复了一处 OpenAPI 问题：新 action 和旧的 `analytics/citations/domains/{domain}` action 同名，导致旧接口的参数说明被覆盖。新 action 已改名为 `CitationDomainSummaryDetail`，文档测试加了回归断言。证据见 `.trellis/tasks/10-23-domain-detail-backend/research/live-smoke.md`。

## 内容管理域名详情：自有标记与问题引用明细（2026-11-09）

Trellis 任务 `11-09-content-domain-detail-ownership`，无 migration：
- `POST /api/v1/sites/{siteId}/content/owned-urls`（`{ url, title? }`）把 URL 标记为自有：已有有效发布链接 → `unchanged`；只有停用链接 → 恢复该链接（文章已归档则恢复文章）→ `reactivated`；都没有 → 新建一篇 `published_import` 文章与有效链接（不抓网页、不扣积分，标题取请求或 URL）→ `created`。
- `DELETE /api/v1/sites/{siteId}/content/owned-urls/{urlHash}` 停用该 URL 摘要在本品牌下的全部有效链接，`published_import` 文章若已无有效链接则归档 → `disabled`；无有效链接 → `unchanged`。两个写接口 viewer 403，返回 `OwnedUrlMarkResult`。
- `GET …/analytics/citation-domain-summary/url-prompts?snapshotToken=&urlHash=[&engine=]` 返回单个引用 URL 在快照内按提问分组的引用次数（分组之和 = `/urls` 行 `citationEvents`，未分组最后），URL 不在快照 404。
- `/urls` 的 `isMine` 本来就按当前有效链接实时判定，标记后立即生效；`myCitationEvents` 仍受登记时间门槛限制，不回算历史。
- 验证：`dotnet build Jogeoai.slnx` 0 错误；定向测试 184 通过、9 跳过（隔离库 SQL，未设置 `JOGEOAI_CONTENT_TEST_CONNECTION`）。5260 已于 2026-11-09 17:52 用新构建在终端标签 c1 重启（Worker 未动）；真实数据核对 url-prompts（30 = 10 + 20、400/404）与标记后 `isMine=true`；真实库「移除」（链接停用、导入文章归档）与再标记（原链接恢复、文章恢复）已走查并还原。

## 首页分组环比与引用排行（2026-10-20）

`brand-dashboard` 的 `groups[]`（及 `/breakdowns`）改为按**提问分组**统计（提问当前的 `prompts.group_id`，未归组的提问计入「未分组」，`groupId = null`），不再按采集时的 Prompt 标签统计。顺序为分组创建时间、id，「未分组」排在最后。每组三项环比都是真实值（当前窗口 − 紧邻的前一等长窗口，同一分组按 `groupId` 匹配）：`previousPercentagePointDifference`（提及率百分点，原先恒为 null）、新增 `previousAverageRankingDifference`、`previousExposureDifference`。任一侧不可用或前期没有该分组时返回 null，不返回 0。`quality` 新增 `groupCount`；没有分组时原因代码为 `NO_PROMPT_GROUP_DATA`；`tagCount` 含义不变。已知限制：按提问当前所属分组统计，提问换组后历史数据会跟着移过去。

`citation-domain-summary` 的变更：
- 域名行新增 `rank`（全局名次，筛选后不重新编号）和 `previousRank`（前一等长窗口的名次，前期没有该域名时为 null；仅 `/domains` 和 `/evidence` 惰性计算，按快照缓存）。
- `meta` 新增 `previousFrom`/`previousTo`；`metrics` 新增 `distinctUrls`。
- 新增只读 `GET …/citation-domain-summary/urls?snapshotToken=&domain=&cursor=&limit=`（默认 50），返回引用文章（URL）榜：与域名汇总共用快照和母集，标题取自 `citation_events.title`，只给当前页查询；错误码与 `/domains` 一致。

以上都是追加字段，没有 migration。

2026-10-20 验证结果：
- `dotnet build Jogeoai.slnx` 0 错误。
- 定向测试 70 通过、3 跳过（`DashboardAnalyticsTests`、`CitationDomain*`，SQL 测试除外）。
- 临时隔离容器上，`CitationDomainSqlTests` 8/8、`DashboardAnalyticsSqlTests` 3/3 通过。临时容器和卷已删除；两组测试共用一个库并行跑会互相抢占分析队列，需要分开跑。
- 本机 5260 已于同日经用户授权用新构建重启（Worker 未动）。用测试账号对真实品牌做了只读核对：两期分组差值一致；首页 `domains`/`urls` 同一 snapshotToken，`distinctUrls` 等于 `filteredUrlCount`（881）；291 个域名的 `previousRank` 与单独请求前一窗口得到的名次逐一一致。
- 真实库数据量小，各接口中位耗时 20–40ms；108k 规模压测未执行。

前端对应 `Jogeoai.web` 的 `/overview` 分组总览、双榜单和 `/overview/citations`。

## 引用域名汇总（2026-09-30 本机部署状态）

新增只读 `GET /api/v1/sites/{siteId}/analytics/citation-domain-summary`、`/domains`、`/evidence`，普通回答详情可通过 `citationProjection=domain-summary-v1` 请求冻结归属补充。新投影统一国内/Cloro 合格回答母集、十一类网站类型、事件占比、回答覆盖率及当前品牌自有身份；原引用接口和首页 KPI 口径不变。

部署前需在目标环境经审阅显式执行 `016_citation_domain_projection.sql`，再执行 `017_detailed_analysis_completion.sql`，然后部署新 API/Worker。016 冻结每个 token 的投影规则；017 保存零引用也成立的详细分析完成证明。未来 detail-v1/v2 的事实与证明同事务写入；历史 v2 只有核心完成而没有详细证明时计为待分析。历史正常 v1 保留原子详细写入兼容。无自动历史回填、重算或数据库修复。

初次迁移与 SQL 回归在独立的 `jogeoai-citation-test-20260930` 容器 / `jogeoai_citation_test_20260930` 数据库完成，该测试容器已按用户确认清理。用户随后明确允许本机部署，2026-09-30 的 016→017 已在 `jogeoai-mysql-1` / `jogeoai` 执行成功，表结构及六条外键已复核。现有 5260 API 运行本次新构建且健康，无需重启；Worker 的磁盘构建已包含新写入逻辑，但 2026-09-30 该次核验时没有运行中的 Jogeoai Worker，未在该次部署中启动。

真实品牌页面已不再报告缺少引用投影表，但当前无合格统计回答；加法迁移不会补齐历史证明或更新已发布分析。未重算、回填或修复业务数据，原回答/引用/分析记录数量未因迁移变化。该结论仅对应上述本机环境，不表示其他环境已部署。该次部署未包含启动分析 Worker 或发起历史分析；回滚不删除原始引用、发布分析或新增证明表。精确命令及运行版本见 [本机部署报告](../Jogeoai.vue/.trellis/tasks/09-30-citation-domain-summary/research/local-deployment.md)。

契约与定向验证入口见 [.trellis/spec/backend/citation-domain-summary.md](.trellis/spec/backend/citation-domain-summary.md)。SQL 测试拒绝缺失或非隔离连接；文档生成测试使用无数据库宿主。实际结果及有限规模性能观测见相邻前端的 [任务验证记录](../Jogeoai.vue/.trellis/tasks/09-30-citation-domain-summary/verification.md)，不宣称生产容量已验证。

## 团队管理模块后端增量（2026-10-20）

为新前端「团队管理」二级模块（团队视图 / 计量中心 / 团队设置 / 品牌管理）补充：`GET /api/v1/team/summary`（`TeamSummaryView`：`range` 当前期与上一期、每品牌 `TeamBrandRow` 的 `mentionRate / exposure / averageRanking / monitoredPrompts` 四个 `PeriodMetric{current, previous, difference, unavailableReason}`、计数与 `enabledTargets`，`enabledEngines` 去重、`totals`；默认近 7 个本地日，`from/to/timeZone/provider/engine/siteId` 可选，单品牌分析失败只置该行不可用）、`GET /api/v1/credits/usage`（`CreditUsageView`：`days[]`（仅有消耗的日期，升序）、`totals[]`（按积分降序）、`totalCredits`、`activeDays`、`latest`；`from/to` 为 `yyyy-MM-dd`，跨度 ≤ 366 天，类型 `article_generation`（100/篇）/ `prompt_monitoring`（10/条）/ `other`，027 起新增 `article_monitoring`（10/链接/日））、品牌 `remark` / `description`（迁移 026）与 `CreditLedgerEntryType.expire`。实现：`TeamSummaryQuery` 逐品牌复用 `AnalyticsQueryStore` 的分析管线（每品牌一次准备并落快照，并行度 3）；纯规则在 Domain 的 `TeamSummaryProjector` / `CreditUsageAggregator`，无库测试覆盖。前端页面、夹具走查与真实验证记录见 Trellis 任务 `10-20-fe-team-management/verification.md`；2026-10-20 本机已执行 026、重启 5260 并完成真实验证（AC04 / AC08 / AC13 与四页浏览器核对）。

## 站点图标（2026-10-21 本机部署并实测）

引用来源列表的站点图标改由后端抓取并缓存，前端统一请求本站接口（Trellis 任务 `10-21-citation-site-icons`）。2026-10-21 经用户确认，028 已在本机 `jogeoai-mysql-1` / `jogeoai` 执行，5260 API 已重启为包含该接口的构建（开发环境开启 `AllowFakeIpRange`）。用临时会话（验证后已删除）请求截图中的 10 个引用域名：8 个返回 200，来源都是首页 HTML 声明（wohlerusa.com 返回真实 32×32 PNG，而不是伪装成 favicon 的 HTML）；inspectioncamera.cn 返回 404 并缓存 7 天；iesdouyin.com（抖音分享域名）站点本身没有图标，之后新增别名 `iesdouyin.com → douyin.com`，实测返回抖音 4286B 的 x-icon（来源 favicon，缓存在 `douyin.com` 行）。（当时 404 带 1 天浏览器缓存；改为 `no-cache` 后不再有这个问题，旧的浏览器缓存最晚 1 天后自然失效。）再次请求约 5ms 命中缓存，没有出站请求，`fetched_at` 不变。3002 真实 API 预览中，引用域名表和回答详情参考资料都显示真实图标，没有图标的显示彩色首字母。定向测试：SiteIcon 118 通过、1 跳过（隔离 SQL 未执行），ContentManagement 49 通过。其他环境尚未执行 028。

- 接口 `GET /api/v1/site-icons/{domain}`，需要登录（Cookie 或 Bearer），不区分租户。
  - 200：返回图标字节，`Content-Type` 是魔数识别出的真实类型（png / x-icon / gif / jpeg / webp / bmp），带 `Cache-Control: private, max-age=604800`。
  - 400 `INVALID_SITE_DOMAIN`：域名不合法（IP、localhost、`.local` / `.internal`、非 ASCII、超长等），不会发出出站请求。
  - 401 `AUTHENTICATION_REQUIRED`：未登录。
  - 404 `SITE_ICON_NOT_FOUND`：所有来源都拿不到，带 `Cache-Control: private, no-cache`。
  - 所有响应都带 `X-Content-Type-Options: nosniff` 和 `Content-Security-Policy: default-src 'none'`。
- 获取顺序：未过期缓存 → 首页 HTML 的 `<link rel=icon / shortcut icon / apple-touch-icon>`（先 https，连不上再 http，最多读 256KB，取前 3 个候选）→ `/favicon.ico` → `www.{域名}` 的同样步骤 → 第三方服务（Google faviconV2 宽度 ≤ 16px 的占位图会被丢弃；DuckDuckGo 返回 404 视为没有；Yandex `favicon.yandex.net/favicon/v2/{域名}?size=32` 宽度 ≤ 16px 视为占位，未收录时返回 1×1）→ 上级域名。
  - www 兜底只用于可注册域名且域名本身不以 `www.` 开头。可注册域名按启发式判断：两段，或倒数第二段为 `com/net/org/gov/edu/co/ac`、最后一段为两字母国家码的三段，例如 `sz-water.com.cn`。首页已跳转到 www、或站点自身剩余预算不足一次请求时，不再尝试 www；来源仍记为 html / favicon。
  - 子域名最终仍拿不到时，直接查询它的可注册域名（任意深度只额外查一次，中间层级不抓取），不会查 `com.cn` 这类公共后缀。上级域名走正常的缓存、合并和抓取流程，并按上级域名单独缓存；查到后，本域名以上级图标写入 found（30 天），查不到则保留本域名自己的 missing 结果。上级查询在释放全局并发名额后进行，避免子域名占满名额等待上级而死锁。
  - 别名先于以上步骤解析，后续都针对目标域名。不使用 icon.horse，因为未收录时它会生成字母图，无法和真实图标区分；SVG 继续拒绝。
- 出站请求走命名 HttpClient `site-icon`，复用 `ArticleSourceReader.CreatePinnedHandler` 和公网主机校验。重定向手动处理，最多 5 跳，每跳重新校验。拒绝 HTML 和 SVG，超过大小上限时在读取过程中直接中止。
- 迁移 `028_site_icons.sql` 新增全局表 `site_icons`，只做加法，可以重复执行。需要手动执行，并加 `--default-character-set=utf8mb4`。**先执行迁移再发布 API**；表不存在时该接口返回 500，前端会退回显示首字母，其他接口不受影响。只有 API 注册了这个服务，Worker 不需要。
- 迁移 `029_site_icons_yandex.sql` 放宽 `ck_site_icons_source`，允许来源 `yandex`；通过 `information_schema` 判断，可以重复执行。未执行时，来源为 yandex 的结果写库会违反约束，接口返回 500，前端显示首字母，因此要先执行 029 再发布包含 yandex 的 API，或者先把 `ThirdParty:Providers` 配置为不含 `yandex`。2026-10-21 经用户确认，029 已在本机 `jogeoai` 执行（重放一次无报错），之前测试写入的 64 条 missing 缓存已删除，5260 已重启。用本机 446 个真实引用域名（其中 `jtkjnkj` 不是合法域名）重跑：覆盖率由 381/445（85.6%）提升到 402/445（90.3%），来源分布为 html 289、favicon 66、google 41、yandex 9，另有 5 个子域名借用了可注册域名的图标；404 响应头实测为 `private, no-cache`。仍拿不到的主要是站点本身没有图标或首页不可达（例如 jiutaigood.com 的主域名 https/http 都超时，用完站点预算，没来得及尝试 www）。其他环境尚未执行 028/029。
- 配置节 `SiteIcons`（默认值见 `src/Jogeoai.Api/appsettings.json`）：

  | 配置项 | 默认值 |
  |---|---|
  | `ThirdParty:Enabled` | `true` |
  | `ThirdParty:Providers` | `["google","duckduckgo","yandex"]`（只认这三个名称，其他名称忽略） |
  | `FoundTtlDays` | `30` |
  | `MissingTtlDays` | `7` |
  | `TransientMissingTtlHours` | `24`（只因超时或网络错误失败时使用；已有图标在这种情况下继续返回，有效期顺延） |
  | `MaxIconBytes` | `102400` |
  | `RequestTimeoutSeconds` | `5` |
  | `TotalBudgetSeconds` | `25`（单个域名一次抓取链路的预算，上级域名另算。启用第三方时预留 `RequestTimeoutSeconds × min(2, 服务数)` 给第三方，默认站点自身和 www 兜底共用 15s） |
  | `MaxConcurrency` | `8` |
  | `AllowFakeIpRange` | `false`（仅开发环境：`appsettings.Development.json` 设为 `true`，额外放行 Clash TUN fake-ip 网段 198.18.0.0/15，DNS 预检和 `site-icon` 连接回调都生效；其他私网/回环/元数据地址仍拒绝，`ArticleSourceReader` 不受影响；非 Development 环境配置为 true 时启动记录警告并按 false 处理） |
  | `DomainAliases` | `{ "iesdouyin.com": "douyin.com" }`（域名别名：键为引用中的域名，同时匹配其子域名，值为实际取图标的域名；只做一跳，按值域名读写缓存，不合法条目启动时忽略并告警） |

  同一域名的并发请求在进程内合并为一次抓取。
- 契约与验证命令见 [.trellis/spec/backend/site-icons.md](.trellis/spec/backend/site-icons.md)。

## 本机开发（macOS/Linux）

需要 .NET 10 SDK、Docker 和 Compose。`global.json` 指定 `10.0.301`，允许 `latestFeature` 滚动；本机已验证 SDK `10.0.400`。在项目根目录执行：

```bash
./scripts/setup-local.sh
```

脚本仅在 `.env` 不存在时从 `.env.example` 创建它，随后启动数据库并等待健康检查、还原依赖、写入 API/Worker 各自的 User Secrets，最后构建解决方案。已有 `.env` 会保留；重新执行会用其中的连接串更新 User Secrets。

在两个终端分别运行：

```bash
./scripts/run-local.sh api
./scripts/run-local.sh worker
```

每日问题监控也可以单独运行 `./scripts/run-local.sh monitoring`：仅注册 Cloro、豆包方舟、DeepSeek API 与 PackyAPI（千问 / 智谱清言 / Kimi / 元宝）采集，Cloro 余额刷新，每日计划，回答分析和自动重新分析 Worker，可与 `research` 进程同时运行。只启动 API 或 `research` 不会消费问题采集任务；需要保持监控 Worker 运行，生产环境交给进程管理器守护。

旧版 Worker 曾将外部任务已完成、但本地已超时的记录标为失败时，可用 `dotnet run --project src/Jogeoai.Worker -- --ReconcileCloroRun=<runId>` 对指定 Run 做一次外部状态重核；该命令只修复带外部任务 ID 且错误码为本地超时的 Check，完成后退出。正常 Worker 启动不再批量重开历史失败任务。

前端进入已登录工作空间时，`POST /api/v1/sites/{siteId}/prompt-monitoring` 幂等建立当前已启用问题的首轮采集和品牌时区每日 09:01 主计划（047 起由 09:00 改为 09:01）；品牌有「2次/天」的监控单元格时，另有品牌时区 00:05 的凌晨计划，只采这些单元格（见上文「品牌监控模型配置：回答模式 × 采集频率」）。已有计划的 POST/GET 会对账未来选择并返回当前/最近运行，不另建首轮或补跑；暂停的计划不会被登录自动启用。首轮键绑定问题版本、品牌分析配置与采集选择；跨实例 MySQL 命名锁防止重复建立计划。首次开启时，跟随团队默认配置且没有启用的可监控目标的品牌写入团队默认配置（048 起，见上文「团队默认监控配置」）；团队从未保存过默认配置时仍按原规则：首次无目标时从目录选择 ChatGPT 的支持地区（当前 US）并保留提问语言，不能直接将品牌的 CN 地区作为 ChatGPT 采集地区。已有目标保持原配置。

2026-10-08 的 Gemini 增量复用这些表，新增 [021 加法迁移](db/migrations/021_gemini_result_quality.sql) 保存回答/来源质量及目标启用时间。Gemini 须由品牌显式启用，进入未来自动轮次，不加入启用前遗漏时隙；自定义计划和已冻结轮次不自动扩展。Check、监控列表和回答详情新增 `resultQuality`，区分明确空来源、未暴露、异常、安全过滤与截断，历史质量保持未知。021 已经用户授权部署至本机 `jogeoai`；优联科技 5 个原问题的 Gemini 单样本真实采集 **5/5 成功，保存 47 条引用**，数据库、Check、监控列表及同快照详情回查一致。实测暴露并修复了列表按长正文分组导致的 MySQL 排序内存错误，追加 87 项定向测试及单节点编译通过。原 169 项代码验证与本次真实验收分别记录；6 项隔离 SQL 套件仍未执行，不能据此声称所有数据库场景通过。手动采集现役入口不扣项目积分，Cloro 真实用量新增 5；自动监控仍为 10 积分/问题/目标/采样。已知 Run 汇总分析字段仍为旧常量，应以监控列表/详情判断本批实际分析完成状态。合同、结果全文及证据入口见 [Gemini 回答记录说明](docx/gemini-answer-recording.md)。

Copilot 沿用 Gemini 的按品牌显式启用策略，复用 Cloro、未来每日轮次和结果质量能力，新品牌默认仍仅 ChatGPT。2026-10-09 补齐 Copilot 专项样例与监控回归，并修复手动创建 Run 缺少 viewer 写限制的入口；242 项定向测试通过，零失败/零跳过。2026-10-10 在本机 `jogeoai` 完成 Copilot 手动实测（5/5 成功），并修复了每日计划时隙的命名锁事务缺陷。2026-10-11 已只读确认首个含 Copilot 的每日轮次：ChatGPT/Gemini/Copilot 各 5/5 成功，15 条分析完成，结算 150 项目积分；证据见 [本机状态核对](docx/local-state-verification.md)。前端可在「采集任务」页启用/停用模型，隔离 SQL 仍未执行。配置入口、积分估算及质量语义统一见 [Copilot 接入说明](docx/copilot-answer-recording.md)。

Perplexity 复用同一 Cloro 链路，没有新表、迁移或接口。2026-10-11 补齐 Perplexity 专项构造样例（18 项）并在 Worker、监控详情/API 用例加入 `perplexity`，定向 211 项全部通过（既有 Copilot 429 用例已在 key 池改动后另行修正）。Perplexity 没有版本字段，版本标记为不可用；检索词只取 `search_model_queries`，`related_queries` 不作为检索词。10-11、10-12 在本机 `jogeoai` 两次手动实采 5 个问题，Cloro 因 Perplexity 重大故障共 10/10 返回失败（未扣额度），本系统正确记为失败、未改品牌目标和计划；**尚未拿到真实回答**，任务已按现状归档，Cloro 恢复后可重新实采。按用户决定暂未为任何品牌启用，也不在每日监控中。2026-10-16 第三次实采仍 5/5 失败：Cloro 在提交阶段以 400 `MODEL_UNAVAILABLE` 拒绝（无法用未登录会话访问 Perplexity），未建任务、未扣额度；此后这类拒绝记为 `CLORO_MODEL_UNAVAILABLE`。详见 [Perplexity 接入说明](docx/perplexity-answer-recording.md)。

Perplexity 故障期间，Google AI Overview（`google-ai-overview`）作为替代回答面，复用同一 Cloro 链路，没有新表、迁移或接口。请求以 Target locale 作为 `hl` 界面语言。Google 没展示 AI 概览时，Check 记为无回答，`resultQuality.answerAbsence=ai_overview_not_shown`，不计入可见率分母。2026-10-16 定向 359 项测试通过（1 项需隔离库的 SQL 测试跳过）。本机 `szulink.com.cn` 5 个问题实采 5/5 成功，均出现 AI 概览，共 141 条引用，每题 5 Cloro 额度。已为该品牌启用每日监控（目标配置 9 → 10），首个含该引擎的每日轮次尚未发生。详见 [Google AI Overview 接入说明](docx/google-ai-overview-recording.md)。

2026-10-11 Cloro 多账号 key 池已实现，本机 `jogeoai` 已备份后执行 [022 加法迁移](db/migrations/022_cloro_api_key_pool.sql)。新代码依赖 022：其他运行库在执行 022 之前重启 API/Worker，Cloro 采集会停止领取任务，手动 Cloro Run 和 key 管理接口返回 503。一行代表一个 Cloro 账号（积分按组织共享，同一组织只录一个 key）。只有在提交被明确拒绝（401/403/429）时才换账号；超时或 5xx 仍按“提交结果未知”处理，不重提。轮询使用提交时的 key。所有账号都积分不足时，任务延后，最多等 2 小时后失败并释放用户积分预留；仅 429 冷却不计入。Worker 每 10 分钟刷新各账号余额。Cloro 配置、测试和 key 管理接口仅限系统管理员。定向测试 202 项通过、6 项依赖隔离测试库的 SQL 测试未执行。真实验收：故意无效的 key 被 Cloro 以 401 拒绝后，同一次调用内换账号提交成功；Worker 链路的任务绑定提交账号并轮询完成。两个任务各扣 7 Cloro 积分，不扣用户积分。真实的 403 积分不足、429 和 2 小时上限尚未触发。运维与回滚见 [Cloro key 池说明](docx/cloro-key-pool.md)。

2026-10-19 套餐与免费体验版后端已实现并在本机验收（[024 加法迁移](db/migrations/024_subscription_plans.sql) 已于备份后执行到本机 `jogeoai`，5260 API / Worker 已重启为该构建；其他运行库未执行）。每个租户一行订阅，注册默认免费体验版并按目录赠送 500 积分；付费套餐按开通日为锚每月清零可用积分再发放额度；品牌 / 成员 / 提问 / 竞品上限在 Store 事务内强制校验；升级只通过卡密兑换或系统管理员指派，不做平台内支付。API 定向测试、Swagger 中文核对与本机运行态验收（上限 409、指派、卡密、Worker 滚动与降级）见 [套餐说明](docx/subscription-plans.md)；隔离库 SQL 场景仍待授权执行，验收租户等临时对象待确认后清理。前端 `Jogeoai.web` 的升级套餐页、卡密兑换页与兑换码管理页已实现，2026-10-20 完成 fixture 走查和本机 5260 联调（临时会话已删除）；联调暴露并修复了回填租户免费版「已用」口径（消耗基线），本机已重放 024。

2026-10-20 豆包（`doubao_api/doubao`）作为第二个 API 回答 Provider 接入，主通道为火山方舟 Responses API 加 `web_search(sources=["doubao"])`，`captureMode=api`、`evidenceClass=api_response`，固定 CN/zh-CN，与 Cloro 共用 Run、`provider_tasks`、积分（同价 10 积分/问题/目标/采样）、`resultQuality`、分析与监控链路；独立 `DoubaoApiTaskWorker` 同步调用并一次事务入库，不依赖 Cloro key 池。豆包按品牌显式启用，新品牌默认仍仅 ChatGPT；方舟未配置时手动 Run 返回 503 `DOUBAO_NOT_CONFIGURED`，每日时隙以 `TARGET_UNAVAILABLE` 跳过且不预留积分。`resultQuality.answerAbsence` 新增 `empty_answer` 与 `safety_filtered`。方舟凭据由系统管理员经 `/api/v1/providers/doubao/settings` 写入数据库（025），不进 `.env`。豆包助手 API（与豆包 App 同源的 `doubao_app` 工具）beta 申请被拒，设置项保留 `requestMode=doubao_app` 占位，开通后只需扩展适配器。定向测试 600 项通过（含 Cloro 回归），构建通过，5260 API 与 Worker 已于 2026-10-20 重启为新构建；真实实采结果见 [豆包接入说明](docx/doubao-answer-recording.md)（待实测）。2026-10-30 本机设置行已写入方舟凭据、模型改为 `doubao-seed-2-0-lite-260428` 并启用；同日开通联网内容插件与豆包搜索 Custom 版后，直连方舟的 `sources=["doubao"]` 调用已成功；同日经 5260 API 与 Worker 端到端验收：管理员测试接口成功，测试租户单品牌 5 问题手动 Run 5/5 成功（引用 8 条、分析完成，手动 Run 不计费）；同日按用户确认给优联科技启用 `doubao_api/doubao` 并纳入每日 09:00 自动监控（10 提问 × 9 目标，每日约 900 积分），首次时隙 2026-10-31 09:00，结果待核对。

2026-11-08 豆包方舟 API（`doubao_api`）新增 `options.requestMode=messages`（Trellis `11-08-doubao-api-full-sources`）：改走方舟 Anthropic 兼容 Messages API（`/api/compatible/v1/messages`，`web_search_20250305`），引用记录豆包搜索召回的全部来源（每次搜索 20 条），不再只有 Responses 模式的 1–5 条正文角标引用；`parser_version=doubao-api-messages-v1` 区分新口径，Responses 模式保留为回滚选项，模式与 `apiUrl` 不一致时保存返回 400。实测 Responses/Chat 的任何参数都拿不到全部来源。本机 5260 API 与完整 Worker 已以新构建重启，设置行已切换；优联当日计划 Run 中切换后的 8 条豆包回答每条 20–58 条带标题来源，分析全部完成。定向测试 261/261 通过，构建通过；历史回答不回填。详情见 [豆包接入说明](docx/doubao-answer-recording.md) 第 3、4、7、9 节。

2026-10-21 国内 Playwright Collector 通道首次对 Jogeoai 真实闭环：本机用相邻 `Ai-visiblility-collector` 的豆包采集器完成 4/4 回答与引用入库（Run `e5e27060-…`，测试租户 `acc024-one.example.test`），监控列表与分析均可见。过程中修复了控制面三处合同缺陷（租约时间 `Z` 格式、`lease_until` 时区误判导致全部 `stale`、disposition 大小写）和国内结果缺少 `capture_mode`/`evidence_class`/`answer_present` 的入库缺口；采集器侧重审豆包 contract 并改为允许部分来源（豆包内联最多 12 条引用）。首次自动发送触发图片验证需用户手动完成；回答带账号个性化痕迹。方舟 API 通道（`doubao_api`）代码已就绪但按用户决定暂不配置凭据。细节见 [豆包接入说明](docx/doubao-answer-recording.md) 第 10 节。

2026-10-21 同一通道对真实品牌优联科技完成 8/8 采集（Run `ddc04f7e-…`）：采集器问题白名单改为运营批准清单（`COLLECTOR_APPROVED_QUESTIONS_FILE`），结果接口新增可选 `citationTitles`（与 `citations` 按位对齐）和 `searchQueries`，181 条引用全部带标题、8 条回答均有搜索关键词；优联启用 `domestic_collector/doubao` 目标后回答记录出现「豆包」页签，概览引用排行计入豆包引用。国内采集仍只能手动发起，不预留积分。同日追加：采集器把豆包回答转为 Markdown（去掉图片、视频与正文来源角标），优联 8 条已入库回答从豆包历史对话只读回填为 Markdown 并经分析任务重算（release v5）；前端豆包标识改为官方图标。细节见 [豆包接入说明](docx/doubao-answer-recording.md) 第 11 节。同日 `analytics/prompts` 的默认来源由仅 Cloro 改为全部三个 Provider，用户提问页按提问合并多来源、多版本的行。已在临时 API 5281 用真实数据核对，本机 5260 API 需重启后才生效。

2026-10-22 按用户要求，对优联科技当前启用的 10 个提问执行一次豆包网页采集（Run `d6877fc1-…`），10/10 成功，236 条引用全部带标题，10 条回答均有搜索关键词且后台分析完成；回答记录接口读回 10 条并与本轮 Check ID 一致。当前品牌每日 09:00 计划仅包含 4 个 Cloro 引擎，豆包网页通道仍需手动创建 Run 并启动外部采集器；支持定时计划的方舟 `doubao_api` 本机仍禁用且无凭据。本轮未修改业务代码、未运行代码测试。详情见 [豆包接入说明](docx/doubao-answer-recording.md) 第 12 节。

2026-10-25 再次按用户要求对优联科技当前 10 个启用提问完成一轮豆包网页采集（Run `9112f588-14f4-4a04-bf33-a0b3f4e3c69b`）：10/10 成功，358 条引用全部带标题，10 条回答均保留 Markdown 和搜索词，逐条详情与 SQL 确认 10 条分析完成；回答记录接口读回同一组 10 条。第 3 题页面检查停止后，从原对话校验完整提问和正文哈希，使用仍有效的原租约恢复提交；浏览器账本确认每题仅一次，没有重复发问。采集器已退出、spool 无待提交文件，API/Worker 继续运行。未修改业务代码，未运行代码测试；国内兼容 Run 概要的分析计数仍返回 `not_started/0`，分析结论以逐条详情与工作项为准。详情和验证命令见 [豆包接入说明](docx/doubao-answer-recording.md) 第 13 节。

2026-10-26 按用户要求直接完成优联科技当前 10 个启用提问的豆包网页采集（Run `b0203785-969b-4eb1-8c96-9b749576630b`）：10/10 成功，332 条引用全部带标题，10 条回答均保留 Markdown 和搜索词，逐条详情与 SQL 确认 10 条分析完成，回答记录接口返回同一组 10 条。两次发送前验证由用户手动完成后继续；一次队列顺序变化使用采集器已持有的有效租约接续。发现本机 Worker 未运行后以既有构建恢复完整 Worker，采集结束时 API/Worker 均在运行，采集进程已退出、spool 无待提交文件。未创建 Trellis 任务、未修改业务代码或运行代码测试；兼容 Run 概要仍显示 `not_started/0`，分析完成以逐条详情和工作项为准。详情见 [豆包接入说明](docx/doubao-answer-recording.md) 第 14 节及 [验证快照](docx/doubao-collection-20261026.json)。

2026-10-30 今日优联豆包网页采集已建 Run `54f34364-4591-4e60-8497-f7743b307d27`，但本机缺迁移 035，租约接口 HTTP 500；10 个任务仍全部 `pending/attempt_count=0`，尚未发问。035 另被一条历史人工归档审计的空失败码阻断；10:10 已按授权回填该审计并执行 035（见账号池条目），同一轮尚未续跑。页面探测 `ready`、Profile/spool 已释放。证据见 [豆包接入说明第 16 节](docx/doubao-answer-recording.md)。

2026-10-28 完成优联科技当日 10 个启用提问的豆包网页采集（Run `af5bf609-0de8-4ff2-a6ce-bfd4325b7208`）：10/10 成功、368 条引用全部带标题，Markdown 与搜索词齐全，10 条逐条详情和分析工作项均为 `completed`；回答记录的 10 条 Check ID 与本轮一致。第 5 题触发人工验证，用户完成后只读确认原草稿无消息或回答，再经正常租约恢复接续；该题租约尝试 2 次，其余 9 题为 1 次。按用户授权将昨天唯一未完成豆包任务置为 `terminal_failed/operator_archived`，保留已成功回答及全部审计，并归档已确认失效的 Profile 锁。采集已退出、spool 无待提交文件，API/Worker 继续运行；“采集任务不创建 Trellis 任务”的偏好已写入 `AGENTS.md`。未修改业务源码或运行代码测试。分析完成口径及操作证据见 [豆包接入说明](docx/doubao-answer-recording.md) 第 15 节与 [验证快照](docx/doubao-collection-20261028.json)。

2026-10-25 随后继续其他国内网页模型：文心 10/10（403 条引用）、元宝 10/10（278 条引用，全部有标题）均入库并完成分析，回答记录与 Run 的 Check ID 集合一致；千问入库 6/10、分析完成 6 条，其中 3 条正常回答共 63 条带标题引用，第 4–6 题为各 24 字符、无引用的简短拒答。第 3、4 题经用户手动验证后在原租约下恢复，第 7 题再次触发验证后，用户决定跳过千问剩余 4 题。采集进程已退出，无待提交密文，Profile/spool 锁已释放；控制面仍保留未完成任务。DeepSeek 按用户选择本次跳过；智谱被昨日 4 条待恢复任务及原来源卡片读取失败阻塞，两者尚未创建本日新 Run。现状、精确 Run/任务标识及恢复条件见 [国内采集记录](docx/domestic-collection-20261025.md)。本次未修改业务代码、未运行代码测试。

2026-10-22 文心一言网页通道已对优联科技同一组 10 个启用提问完成首次真实采集（Run `19a56c57-…`）：10/10 成功，每题尝试 1 次，291 条引用 URL，10 条后台分析完成，回答记录接口按文心筛选读回全部 10 条。沿用既有登录态，现场复核并修正文心“新对话”按钮规则，外置配置接到当前 5260 API，品牌启用 `domestic_collector/ernie`。本轮未修改业务源码、未运行代码测试。文心目前不上传引用标题和搜索关键词，正文自 2026-10-22 起为 Markdown（见下文）；网页通道仍需手动发起，其他国内平台未在本轮执行。详情见 [文心采集说明](docx/ernie-answer-recording.md)。

2026-10-22 腾讯元宝网页通道已对优联科技同一组 10 个启用提问完成真实采集（Run `f7d6bcaf-…`）：10/10 成功，每题尝试 1 次，263 条引用 URL（262 条带标题，覆盖 139 个域名），10 条后台分析完成，回答记录接口读回全部 10 条并核对 Check ID 集合一致。用户完成登录后复审页面规则，并修复相邻 Collector 元宝适配器对独立引用侧栏的读取与关闭；第二题停止后从原历史回答恢复提交，没有重发。元宝定向测试 9/9、类型检查与修改文件 lint 通过；后端未修改业务源码，未运行 .NET 测试。搜索关键词未知，正文自 2026-10-22 起为 Markdown（见下文），网页采集仍需手动发起。详情见 [元宝采集说明](docx/yuanbao-answer-recording.md)。

2026-10-22 千问网页通道已对优联科技同一组 10 个启用提问完成真实采集（Run `f021f463-…`）：10/10 成功，145 条引用全部带标题，覆盖 47 个域名，10 条后台分析完成；回答记录接口读回全部 10 条，Check ID 集合与本轮一致。相邻 Collector 已适配独立来源面板和带换行的空输入框提示。用户手动完成平台验证后，第 7、9 题从原对话恢复已有回答；第 7 题旧租约等待时超时，正常重领后提交，未重发问题，故该题控制面尝试计数为 2，其余 9 题为 1；浏览器尝试账本仍为 10 个不同任务和问题。千问定向测试 22/22、类型检查、修改文件 lint 与格式检查通过；后端未修改业务源码，未运行 .NET 测试。正文为纯文本，搜索关键词未知，网页通道仍需手动发起。详情见 [千问采集说明](docx/qwen-answer-recording.md)。

2026-10-28 千问当日轮次已完成（Run `da83f919-8c4b-48ab-a9b8-fa278700f3b2`）：台北时间 12:32:01 完成、12:32:42 核对，10/10 成功，220 条引用全部带标题，逐条详情与分析工作项均为 10 条 `completed`，回答记录的 Check ID 与本轮一致。人工验证由用户完成；空白原对话确认后接续，已生成回答从原对话核对恢复。第 4 题因两次等待验证超时，经正常队列重领至租约版本 3，其余 9 题为 1；浏览器尝试账本仍为 10 个不同任务/提问各一次预留。采集进程已退出，Profile/spool 无锁或待提交文件，API 与 Worker 继续运行。未创建 Trellis 任务、未修改业务源码或运行代码测试。分析摘要兼容字段的边界及证据详见[千问采集说明](docx/qwen-answer-recording.md)第 6 节及[最终快照](docx/qwen-collection-20261028.json)。

2026-10-22 文心、元宝回答改为 Markdown（Trellis `10-22-ernie-yuanbao-deepseek-markdown`）：相邻 Collector 的文心、元宝、DeepSeek 适配器接入与豆包相同的 Markdown 转换；元宝剔除列表圆点和来源角标，京东商品卡、相关视频卡移到正文下方（含图片、标题、标签、价格、店铺 / 来源与时长）。上述两轮的 20 条回答已在不重发提问的情况下，从原对话只读取回、按正文逐字核对后，在事务里按旧 SHA-256 替换；之后对本轮 20 条执行范围重新分析，发布 release v6（复用 280、重算 20），排名、提及与曝光指标逐条对比无变化。原始纯文本保留在 `collector_result_submissions`。新前端 `answerMarkdown.ts` 现在把 https 图片渲染为不带 Referer 的懒加载 `img`。Collector 定向测试 49/49、前端定向测试 13/13 及 `npm run build` 通过；后端未修改源码，未运行 .NET 测试。详情见文心、元宝两份说明的“回答格式化”一节。

2026-10-22 DeepSeek 网页通道（「智能搜索」开、「深度思考」关）已对优联科技同一组 10 个启用提问完成首次真实采集（Run `ed7d43ec-…`，Trellis `10-22-deepseek-collector-ulinktek`）：10/10 成功，每题尝试 1 次；回答为 Markdown（816–1702 字符）；149 条引用全部带标题，覆盖 60 个域名；10 条后台分析完成，回答记录接口读回全部 10 条，Check ID 集合一致。相邻 Collector 新增 `web_search_conversation` 采样模式和「搜索结果」侧栏来源读取，并在实页上复审后另存 DeepSeek 页面规则；品牌启用 `domestic_collector/deepseek`（目标版本 13 → 14）。Collector 定向测试 54/54、类型检查与所改文件 lint 通过；后端未修改源码，未运行 .NET 测试。搜索关键词未上报，网页采集仍需手动发起。详情见 [DeepSeek 采集说明](docx/deepseek-answer-recording.md)。

2026-10-23 按豆包 → 文心 → 元宝的顺序，对优联科技同一组 10 个启用提问完成新一轮网页采集：三轮各 10/10 成功，共 30 条回答、892 条引用、30 条已完成分析，每个任务尝试 1 次。回答记录接口的 Check ID 与三轮结果分别一致，三个加密 spool 均无待提交文件，会话和锁均已释放。文心“快速”按钮现显示为“对话”，现场确认仍选中 `fast` 模式后更新外置规则；元宝其中一题原回复只有视频卡片，按实际内容保存为 98 字符纯文本、无引用，并已只读核对历史对话，未重发。本轮未修改业务源码、未运行代码测试。Run ID、引用标题与搜索关键词边界、现场恢复及验证证据见 [国内网页顺序采集记录](docx/domestic-collection-20261023.md)。

2026-10-24 对优联科技同一组 10 个启用提问，带统一前缀采集豆包、文心、元宝、千问、智谱。豆包、文心、元宝各 10/10，千问 8/10，智谱 6/10，共 44 条回答、44 条分析完成。千问剩 2 题，发送前遇到验证，已核实未发送，但控制面各已计 2/3 次。智谱 4 题已发送，原对话已只读捕获；其中排在队首的一题，有一个来源站点经代理不可达，来源读取失败关闭，4 题均未提交。相邻 Collector 修复了段落式输入框（豆包）带前缀时的回读，适配器测试 203/203 通过。千问、智谱 Run 汇总显示 `partial_failed`，但对应 Check 仍为 `pending`。详情和待处理项见 [国内网页五平台采集记录](docx/domestic-collection-20261024.md)。

2026-10-23 在上述三个平台完成后，继续对优联科技同一组 10 个启用问题执行千问新一轮采集（Run `231ed328-…`）：10/10 成功，150 条引用全部带标题、覆盖 64 个域名，10 条后台分析完成，回答记录接口与本轮 Check ID 集合一致。每个任务的尝试计数和租约版本均为 1，浏览器账本为 10 个不同任务和问题。第 3、6、8、10 题的滑块验证由用户手动完成；第 3、6 题确认本轮临时对话未持久化后沿用原任务继续，第 8、10 题从原对话恢复已有回答。独立 spool 无待提交文件，会话已关闭、锁已释放。本轮未修改业务源码、未运行代码测试，网页采集仍需手动发起。详情见 [千问采集说明](docx/qwen-answer-recording.md) 第 5 节。

2026-10-23 本地网页采集器统一问题前缀（Trellis `10-23-web-collector-question-prefix`）：相邻 `Ai-visiblility-collector` 在每次新发问时，发送“用户指定的独立查询、禁止引用历史问题、要求联网搜索说明 + 两个换行 + 当前原问题”，说明保留用户提供的全部原文及重复字词。覆盖共享浏览器入口的豆包、文心、元宝、千问、DeepSeek、Kimi、智谱清言、直接浏览器 ChatGPT/Gemini；任务快照、批准清单、尝试账本哈希和问题展示继续使用原问题。同一原任务重复构造不会累加前缀，纯文本、Markdown 与提交阶段均拦截问题回显。使用新版代码新启动的 Collector 进程生效；历史回答和已有加密 spool 不补前缀、不重采，Cloro 与官方 API 通道不在此次范围。仍保留逐题新建对话、空输入框和采样模式检查；提示词不能证明账号记忆被清除或实际联网，继续按页面状态与来源核验。前缀原文及恢复说明见 [Collector README](../Ai-visiblility-collector/README.md#browser-question-prefix)。Collector 定向测试 14 个文件、253/253 项通过，类型检查、修改文件的 lint 与格式检查通过；精确命令及失败重试记录见本任务验收报告。本轮没有创建真实采集 Run，未执行 .NET 或数据库测试。

2026-10-23 智谱清言网页通道已对优联科技同一组10个启用问题完成本轮采集（Run `84c9650d-…`，Trellis `10-23-qingyan-markdown-collection`）：10/10成功、140条引用URL（98个域名）、10条后台分析完成，回答记录与本轮Check ID集合一致。相邻Collector的智谱适配器接入已有Markdown转换器，每条回答保留标题、段落、列表及表格（2447–3883字符、10–31个段落块）；沿用指定原文前缀，每题新对话。第9题校验停止后从原对话严格核对问题及正文哈希并恢复，未重发；该题过期租约重领后控制面计数为2，其余为1，浏览器账本仍每题1次。独立spool无待提交文件，锁均释放。69/69定向测试、类型检查及修改文件lint/格式检查通过。引用标题和搜索关键词未上报，网页通道仍需手动发起；未回填历史回答、未修改后端/前端业务源码，未执行.NET或数据库测试。详情见 [智谱采集说明](docx/qingyan-answer-recording.md)。

2026-10-29 PackyAPI（Trellis `10-29-domestic-52pay-api-provider`）接入千问、智谱清言、Kimi 三个 API 回答 Provider（优联 10 题手动实采 30/30 成功；2026-10-29 已为优联启用三个目标；首个每日时隙 2026-10-30 09:00 已核对：packy 29/30 成功、积分 capture/释放对账一致，Kimi 1 项失败按规则释放积分，另有 3 条成功正文只剩英文搜索过渡行待处理，见说明 §5.2；2026-11-02 Worker 停了三天后重启，当天轮次 Kimi 10/10 被上游拒绝，本机设置行随后改用 `kimi-k2.6`，手动补采 10/10 成功，其中 2 条开头带搜索过渡文字，见 §5.3）：`packy_qwen/qwen`（`qwen3.8-flash` 关思考）、`packy_glm/zhipu_qingyan`（037 起 `glm-5.3-flash` + `reasoning.effort=low`，对应网页端“快速”）走 OpenAI Responses 流式接口加 `web_search`，来源为全部检索结果（只有 URL）；`packy_kimi/kimi` 走 Anthropic 兼容接口加 `web_search_20250305`（`max_uses=1`），来源带标题。每个 Provider 一把 PackyAPI 分组 key。代码与定向测试、迁移 036、5260 与完整 Worker 重启已完成；真实采集与品牌目标启用见 [PackyAPI 回答采集说明](docx/packyapi-answer-recording.md) §5。52pay 豆包与腾讯云混元未接入。

2026-11-08 PackyAPI 元宝（Trellis `11-08-packy-yuanbao-hunyuan`）：按用户要求先停用优联科技的 Kimi 目标（目标版本 19 → 20，Kimi 设置与 key 保留），再接入 `packy_yuanbao/tencent_yuanbao`（混元 `hy4`，Responses 流式 + `web_search`，`max_tool_calls=3`，`max_output_tokens=32000`）。该渠道思考关不掉，单题约 2.5–6 分钟，**不返回检索来源**，只记录模型打开过的网页，通常来源为 `not_exposed`，有回答正文和搜索词。迁移 046 已在本机执行，key 已写库并启用，API / Worker 已重启，优联已启用元宝目标（版本 21），当晚按用户要求又把元宝目标停用（版本 22，设置与 key 保留），2026-11-09 09:00 起的每日时隙预计不含 Kimi 与元宝（尚未核对）。首轮手动 Run 8/10 成功；2 题在 16000 输出上限下失败，上限提高到 32000 后补跑 2/2 成功，10 个提问都有元宝回答（引用均为 0），分析全部完成。首个包含元宝的每日时隙与管理员接口尚未核对，见说明 §6.4。

2026-10-23 DeepSeek 官方 API（`deepseek_api/deepseek`，Trellis `10-23-deepseek-api-provider`）作为第三个 API 回答 Provider 接入。通道使用 DeepSeek Anthropic 兼容接口加服务端 `web_search`，默认 `deepseek-flash`、关闭思考；回答为 Markdown，引用记录检索来源（接口不返回行内引用）。它与 Cloro、豆包共用 Run、`provider_tasks`、积分（每日监控 10 积分/问题/目标/样本）、`resultQuality`、分析和监控链路。原先写死豆包的目标、Run、计划和 503 判断已改为由 `AnswerProviderDefinition` 的 `DisplayName`/`PinnedSurface`/`NotConfiguredCode` 驱动，豆包的文案与行为保持不变；同步 Worker 抽出公共基类 `SynchronousAnswerTaskWorker`。本机已执行 030；Key 与 OpenAI Key 一样明文写入 `answer_provider_settings`；5260 API 与 Worker 已重启。优联科技已启用 DeepSeek API 目标，自动监控计划自 2026-10-24 09:00 起包含 DeepSeek。手动 Run `fc2227f8-…` 10/10 成功（约 85 秒，264 条来源、261 条带标题，9 题有搜索词），API 与 MySQL 逐条一致，分析全部完成，3001 的「DeepSeek」页签同时显示 API 与网页回答。与 10-22 网页轮次逐题对比：优联提及 API 2/10、网页 4/10，同 URL 9 条，API 回答平均长约一倍。定向测试 543/543 加文档测试 26/26 通过，`dotnet build Jogeoai.slnx` 通过；前端 vitest 51/51 与 `npm run build` 通过。每日轮次的积分结算与管理员测试接口待核对。详情见 [DeepSeek API 接入说明](docx/deepseek-api-answer-recording.md)。

2026-10-22 提及识别 mention-v2 与曝光分 exposure-v2（Trellis `10-22-mention-matcher-exposure-v2`）：
- **识别修正**：正文里的域名把 `www.` 视为主站（与引用侧 `normalized_url` 一致，不受“包含子域名”开关影响）；英文或数字别名紧挨汉字时也能命中（如“优联Ulinktek”）；汉字紧贴域名（如“访问www.x.com”）时取其中的 ASCII 片段比对。识别器版本变化后，`CreateJobAsync` 记为 `extractor_version_changed`，不复用旧事实，全量重算。
- **曝光分 v2**：单条回答 = 40 × 提及 + 30 ÷ 名次（未提及记 0）+ 20 × 按出现次数的声量 + 10 × 自有引用。概览、趋势、分组和团队汇总按合并口径计算：提及率、平均名次分、合并的出现次数声量、自有引用率。`ExposureScore` 新增 `positionWeight`、`positionScore`（`MetricMean`）；品牌已提及但缺少位置时，分数不可用（`SUBJECT_ORDER_UNAVAILABLE`）。v1 为 50 × 提及 + 30 × (1 ÷ 提及主体数) + 20 × 自有引用，名次不参与计算。分数在查询时实时计算，历史数据现在全部按 v2 显示。
- **本机验证**：重启 5260 API 与 Worker 后，对优联发起全站作业（release v7，重算 310 条、复用 0 条），补回 28 条漏判（Copilot 15、ChatGPT 12、Google AI Overview 1），没有由提及变为未提及的回答。概览（09-22 至 10-23）：提及率 21.7% → 30.7%，平均名次 1.91 → 1.83，曝光分 17.7 → 23.5（其中仅更换公式时为 16.4）。只写简称“优联”或只写型号的回答仍为未提及，因为品牌配置没有改动。本机其他站点仍保留 mention-v1 事实，等下一次作业时重算。
- **测试**：`dotnet build` 与定向测试（AnalyticsCore、DashboardAnalytics、TeamSummary、MonitoringAnswerDetail/Api、CitationDomainApiDocumentation、AnalysisSql、DashboardAnalyticsSql）共 125 个通过、3 个跳过（没有隔离库的 SQL 用例），0 个失败；`AnalysisSqlTests` 的数据库用例在没有隔离库时直接返回，没有真正执行 SQL。前端已重新生成类型，定向测试 131 个通过，`npm run build` 通过；回答详情的曝光分新增分项悬浮说明。

2026-10-22 品牌配置变化后自动重新分析（Trellis `10-22-auto-reanalysis-on-config-change`）：Worker 新增 `AnalysisRebuildWorker`，每 30 秒检查一次。站点的最新主体配置版本与已发布分析结果不一致，或识别器版本落后时，自动发起全站重新分析。配置变化要在最后一次修改后等待 60 秒，以合并连续修改；识别器升级则立即发起。这一机制走与手动 `POST analysis-jobs` 相同的路径，幂等键为 `auto-rebuild:{siteId}:cfg{n}:{extractor}`；已有进行中的分析时跳过；失败不自动重试。可用 `AnalysisRebuild:Enabled=false` 关闭。只改备注、描述不会触发。本机实测：Worker 重启后，另一个仍为 mention-v1 的测试站点被自动重算（4 条，3 秒发布）。给优联加临时别名后约 85 秒自动发布 release v9；删除临时别名后再次自动发布，提及数仍为 100 条，与改动前一致。每次修改只产生 1 个作业。前端在品牌识别配置或竞品保存后提示“约 1–2 分钟后自动重新分析历史回答”。验证：`dotnet build` 通过，定向测试 28 个通过（`AnalysisRebuildRulesTests` 8 个；新增的隔离库 SQL 用例因没有隔离库直接返回，检测 SQL 已在本机库上只读执行并核对）；前端 `team-brands-logic` 16 个测试通过，`npm run build` 通过。

每日问题监控的早期定向验证：`dotnet test tests/Jogeoai.Tests/Jogeoai.Tests.csproj --no-restore --filter 'FullyQualifiedName~PromptMonitoringTests|FullyQualifiedName~Jogeoai.Tests.CollectionScheduleTests'`。2026-09-29 实际通过 15 项；当时运行态确认该品牌只有一个每日计划，并发重放复用同一首轮运行。前端联调记录见相邻项目的 `docs/verification.md`。

API 默认监听 `http://localhost:5260`。启动脚本加载 `.env`；ASP.NET Core 本身不会自动读取这个文件。执行过 setup 后，也可直接使用 `dotnet run --project src/Jogeoai.Api --launch-profile http` 和 `dotnet run --project src/Jogeoai.Worker`，数据库连接由各项目的 Development User Secrets 提供。

`.env` 是本机实际配置，`.env.example` 只是模板，启动时不会合并或读取模板。模板中额外的知识库和国内采集配置按需填写，无需为了邮件注册补齐；邮箱配置使用 `Email__*`。启动脚本通过 Bash `source` 加载 `.env`，含空格的值必须加引号，例如 `Email__FromName='Jogeo AI'`。修改 `.env` 后需重启对应服务。

| 地址                                   | 预期结果                                                                                   |
| -------------------------------------- | ------------------------------------------------------------------------------------------ |
| `GET /health/live`                     | HTTP 200，`{"status":"ok"}`，仅检查 API 进程                                               |
| `GET /health/ready`                    | 数据库可连接且查询成功时 HTTP 200 `Healthy`；失败时 HTTP 503                               |
| `GET /openapi/v1.json`                 | Development 下返回 OpenAPI JSON                                                            |
| `GET /api/v1/providers/cloro/settings` | 系统管理员：查看是否已配置 Cloro（不会返回 API Key）                                       |
| `PUT /api/v1/providers/cloro/settings` | 系统管理员：保存 Cloro API URL 和启用状态；`apiKey` 可选，兼容写入 `default` 账号           |
| `POST /api/v1/providers/cloro/test`    | 系统管理员：提交并轮询一个 Cloro 任务，返回回答、引用、搜索词、受控摘要及所用账号后 4 位    |
| `/api/v1/providers/cloro/keys`         | 系统管理员：Cloro 账号 key 池的列表、新增、修改、归档与余额刷新（需要 022）                 |
| `GET /api/v1/providers/cloro/pool`     | 系统管理员：key 池概览（可用 / 耗尽 / 冷却 / 无效账号数、已知余额、最早恢复时间）           |
| `GET /api/v1/providers/doubao/settings` | 系统管理员：查看豆包（火山方舟）地址、模型、启用、超时、选项与凭据提示（不返回凭据，需要 025） |
| `PUT /api/v1/providers/doubao/settings` | 系统管理员：保存豆包设置；`credential` 为空时保留已保存凭据                                   |
| `POST /api/v1/providers/doubao/test`    | 系统管理员：用已保存设置向豆包提一个问题，返回回答、搜索词、引用、模型版本与用量摘要           |
| `GET /api/v1/providers/deepseek/settings` | 系统管理员：查看 DeepSeek 地址、模型、启用、超时、选项（含 `thinking`）与凭据提示（不返回凭据，需要 030） |
| `PUT /api/v1/providers/deepseek/settings` | 系统管理员：保存 DeepSeek 设置；`credential` 为空时保留已保存凭据 |
| `POST /api/v1/providers/deepseek/test` | 系统管理员：用已保存设置向 DeepSeek 提一个问题，返回回答、搜索词、检索来源、质量与用量摘要 |

请求示例见 [Jogeoai.Api.http](src/Jogeoai.Api/Jogeoai.Api.http)。Development 环境可打开 [Swagger UI](http://localhost:5260/swagger)，OpenAPI JSON 仍在 `/openapi/v1.json` 和 `/swagger/v1/swagger.json` 提供。

Swagger 是否启用由 `Swagger:Enabled` 控制；默认配置仅在 Development 打开。跨域由 `Cors:AllowedOrigins` 和 `Cors:AllowAnyOrigin` 控制，Development 默认允许前端本地调试，生产环境应显式配置可信来源。

Swagger UI 使用 API、Domain、Application 和 Provider 契约中的 XML 注释展示接口摘要、参数和响应说明；新增或修改公开接口时同步维护 Controller、DTO 和枚举的中文 XML 注释。

`POST /api/v1/providers/cloro/test` 的 `model` 可填 `chatgpt`、`perplexity`、`copilot`、`gemini`、`google-ai-mode` 或 `google-ai-overview`。这里选择的是 Cloro 的回答引擎/页面类型；若提供方返回具体版本，响应的 `model` 会保留它，否则可能回退为引擎标识，不能把该回退值当作具体模型版本。当前适配器没有额外传入 GPT 版本参数。

配置并测试 Cloro 的最小请求如下（`apiUrl` 填 Cloro 的异步任务地址，例如 `https://api.cloro.dev/v1/async/task`）：

以下请求都需要系统管理员会话（`Authorization: Bearer <admin-session-token>`）；更多账号通过 `POST /api/v1/providers/cloro/keys` 录入：

```http
PUT http://localhost:5260/api/v1/providers/cloro/settings
Authorization: Bearer <admin-session-token>
Content-Type: application/json

{"apiKey":"<your-cloro-key>","apiUrl":"https://api.cloro.dev/v1/async/task","enabled":true}

###

POST http://localhost:5260/api/v1/providers/cloro/test
Authorization: Bearer <admin-session-token>
Content-Type: application/json

{"model":"chatgpt","prompt":"What are the best products for...","country":"US"}
```

全局地址和启用开关存储在 MySQL 的 `cloro_provider_settings` 单例表中；执行 022 后，调用使用的 key 来自 `cloro_api_keys`，单例表里的旧 key 只为回滚保留。应用启动时会执行幂等的建表语句，部署工具也可以直接执行 [001_cloro_provider_settings.sql](db/migrations/001_cloro_provider_settings.sql)。API 响应和日志不会打印连接 Key。

首期业务数据库结构由 [002_visibility_foundation.sql](db/migrations/002_visibility_foundation.sql) 提供，包含租户、站点、Prompt、运行、Provider 任务、回答证据、积分流水和 Outbox 表。站点 Host 由应用规范化为小写后按字节精确唯一；引用 URL 限制为 750 个字符，以满足 MySQL `utf8mb4` 唯一索引上限。它不会在 API 或 Worker 启动时自动执行，也不会修改 `cloro_provider_settings`。本机手动执行时从 `.env` 读取连接信息，避免把密码写进命令历史或日志：

```bash
set -a; . ./.env; set +a
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/002_visibility_foundation.sql
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/003_domestic_collector.sql
```

`003_domestic_collector.sql` 使用信息架构检查保护增量列、索引和约束，开发环境可重复执行；它会回填 `visibility_checks.tenant_id`，并 seed 七个国内平台的 `consumer_web` 入口。

Cloro 业务链路需要在 003 之后显式执行 004：

```bash
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/004_cloro_visibility.sql
```

上层管理与多样本采集需要在 004 后显式执行 005。执行时先停止旧 API/Worker 写入，迁移会核对旧 Check 能否按 Run/Engine 唯一回填 Target；遇到无法归属的旧行会报错并保留数据，修正后可重放：

```bash
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/005_elmo_management_orchestration.sql
```

提及分析和核心指标需要在 005 后显式执行 006。先停止旧 API/Worker，迁移保留原 Check/引用并为可分析的历史回答建立工作项；服务启动不自动执行它：

```bash
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/006_elmo_analysis_core.sql
```

Fan-Out 和引用细分需要在 006 后显式执行 007，同样先停止旧 API/Worker。迁移会为已有站点建立新分析版本及重算工作项，启动 Worker 后异步处理；旧发布版本可继续读取核心指标，细分接口在新版本发布前返回 `ANALYSIS_VERSION_NOT_READY`：

```bash
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/007_elmo_detailed_analytics.sql
```

鉴权与品牌引导需要在 007 后显式执行 008：

```bash
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/008_auth_brand_onboarding.sql
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/009_system_admin_provider_access.sql
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/010_research_provider_plaintext_credential.sql
```

细分引用 URL 规范化状态需要在 007 之后显式执行 011：

```bash
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/011_dashboard_analytics.sql
```

知识库结构需要在 011 之后手动执行 012，再执行 013 以保存完整的写作上下文快照；API/Worker 启动不会自动执行迁移：

```bash
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/012_rag_knowledge.sql
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/013_knowledge_context_snapshot.sql
```

其他环境部署内容管理需要在 013 后显式执行 014；先核对经授权的目标库和停写窗口，再执行迁移。本机 `jogeoai` 已具备该结构，不能把它当作测试库或清空对象：

```bash
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql -u"$MYSQL_USER" "$MYSQL_DATABASE" < db/migrations/014_content_management.sql
```

提问管理页面依赖 016–018 基础结构和 019、020 增量结构。2026-10-06 对本机 `jogeoai` 的只读实查已确认 019 的两张监控积分表、账本关联列/外键、计划跳过原因列，以及 020 的 `prompt_groups`、`prompts.group_id`、分组索引/外键均存在；本轮未执行迁移，也未验证该模块业务链路。其他环境仍须按目标库状态部署。018 的 `/question-groups`、`/questions` 接口已于 2026-11-07 从 API 下线（安全修复 C1，`questions`/`question_groups` 表与数据保留），原始需求见 [提问管理 PRD](docx/question-management.md)。019 增加监控积分预留/明细及计划跳过原因，依赖 005、014、018 创建的租户、站点、运行、检查、积分和计划表；020 将现有 `prompts` 接入可选的持久化分组，依赖 `prompts` 和 `sites`。016–018 已在本机运行库中存在，脚本只负责显式执行 019 → 020；API 和 Worker 启动不会自动修改数据库。仅针对本机 Docker Compose 数据库时，可从后端目录执行可重复的入口：

```bash
./scripts/migrate-local-feature.sh
```

该脚本读取后端 `.env`，要求 Compose 的 `mysql` 服务已经运行，先用只读 `SELECT 1` 检查连接，再用 `information_schema` 检查 016–018 的产物表和 019/020 所需基础表。脚本不会启动、重建或初始化 MySQL；服务未运行、连接失败或前置表缺失时，在发送任何迁移 SQL 前退出。检查通过后按顺序执行 `db/migrations/019_prompt_monitoring_credits.sql`、`db/migrations/020_prompt_groups.sql`。任一文件失败都会停止后续步骤并保留 MySQL 错误输出；修正后重新执行脚本会从 019 开始重放，迁移中的 `IF NOT EXISTS`/信息架构检查保护已存在对象。脚本不会执行 down migration、清空命名卷或被 API/Worker 自动调用。生产部署应在停写窗口审阅同一编号顺序后由部署流程显式执行，不能把本地脚本作为启动钩子。

`./scripts/migrate-local-feature.sh --help`（或 `-h`）只显示说明，不读取 `.env`、调用 Docker 或执行迁移；未知参数会报错退出。无参数调用才显式执行上述迁移流程。可运行 `python3 tests/test_migrate_local_feature.py` 验证入口的成功顺序、失败停止和参数边界；测试使用临时目录及模拟 Docker，不连接 MySQL。

执行后可在同一后端目录检查页面所需对象（查询结果不包含凭据）：

```bash
set -a; . ./.env; set +a
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql --protocol=TCP -u"$MYSQL_USER" "$MYSQL_DATABASE" -NBe \
  "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('analytics_snapshot_projections','check_detailed_completions','question_groups','questions','monitoring_credit_reservations','monitoring_credit_items','prompt_groups') ORDER BY table_name;"
docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
  mysql --protocol=TCP -u"$MYSQL_USER" "$MYSQL_DATABASE" -NBe \
  "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = DATABASE() AND ((table_name = 'prompts' AND column_name = 'group_id') OR (table_name = 'schedule_slots' AND column_name IN ('skip_code','skip_message'))) ORDER BY table_name, column_name;"
curl -fsS http://localhost:5260/health/live
curl -fsS http://localhost:5260/health/ready
```

上述检查命令只读取 schema 和服务健康，不证明迁移执行日期或业务链路通过；只读检查无需停服。执行迁移脚本前须确认 `.env` 指向经授权的目标库并安排相关 API/Worker 的停写窗口。

按提问平台范围的 042 不在 `migrate-local-feature.sh` 中，需单独手动执行（先迁移，再同时发布 API 与 Worker，回滚脚本在存在 custom 提问时拒绝执行），说明见上文「新增监测问题弹窗与按提问平台范围」。品牌知识库的 043 同样需单独手动执行，并且必须先于新构建的 API / Worker 重启，说明见上文「品牌知识库维度列表」。回答模式与采集频率的 047 也不在该脚本中，需用 root 单独手动执行，然后同时发布 API 与 Worker，说明见上文「品牌监控模型配置：回答模式 × 采集频率」。

知识库路由包括 `POST /api/v1/knowledge/documents`（公司级上传）、`POST /api/v1/sites/{siteId}/knowledge/documents`（品牌级上传）、`GET /api/v1/knowledge/usage`、`POST /api/v1/sites/{siteId}/knowledge/search` 和 `POST /api/v1/sites/{siteId}/knowledge/context`。上传文件大小上限为 100 MB，租户保留版本容量上限为 1 GB；对象下载始终经过鉴权，不返回公开对象地址。列表返回项目统一的 `items`/`nextCursor` 游标分页，支持 `status`、`tag` 和 `documentType` 过滤；品牌列表另支持 `scopeMode=brand_only`。游标绑定租户、品牌和过滤条件，`status` 指逻辑资料状态（替换版本处理中时，旧版仍可用的资料保持 `ready`）。品牌详情、下载、更新、重试和停用路由也必须带品牌范围。上传响应同时给出 `latestVersion` 的处理状态和 `currentVersion` 的当前可用版本。 2026-10-19 起源码另有 `/api/v1/sites/{siteId}/knowledge/products*` 产品信息路由（原 `POST …/knowledge/links` 已于 2026-11-07 下线），依赖 023，见 [素材库产品信息说明](docx/knowledge-product-library.md)。2026-11-03 起产品路由与上传接口标记 deprecated，品牌事实改用 `/api/v1/sites/{siteId}/knowledge/items` 与 `/knowledge/extract-jobs`（依赖 043），见 [品牌知识库说明](docx/brand-knowledge.md)。

文件夹更新支持 `moveToRoot=true` 将目录移回根目录，并拒绝与 `parentId` 同时设置或移动到自身后代。

开发环境默认使用本地对象目录、确定性 Embedding 和 `Knowledge__VectorRoot` 指定的持久化本地向量目录；API 与 Worker 应共享这两个目录。确定性 Embedding 仅适合本机链路验证，实际语义相关度需用真实 Embedding 模型评估。需要 MinIO/Qdrant 时可执行 `docker compose --profile knowledge up -d`，再将 `Knowledge__Storage__Provider` 设为 `s3`、`Knowledge__Embedding__Provider` 设为 `openai-compatible`、`Knowledge__Vector__Provider` 设为 `qdrant`，并通过 User Secrets 或生产密钥管理系统注入对象存储、Embedding 和 Qdrant 配置；Qdrant collection 的模型档案和维度跟随当前 Embedding 配置，`Knowledge__Embedding__Dimensions` 必须设置为正数。默认 Compose profile 不会启动这些外部服务。

首次配置系统级研究 Provider 时，先注册用户并取得该用户 ID，再由数据库管理员在目标数据库中按精确 ID 授予系统管理员权限。下面的 SQL 仅是模板，执行前必须替换为已核对的用户 ID，禁止直接对共享业务库执行未审阅的示例：

```sql
UPDATE auth_users
SET is_system_admin = TRUE, updated_at = UTC_TIMESTAMP(6)
WHERE id = UUID_TO_BIN('<registered-user-id>') AND status = 'active';
```

授权后，使用该用户登录并调用 Provider 配置接口；接口接收 `model`、完整 `apiUrl` 和 `credential`，凭据按当前部署方案直接保存到 MySQL，列表接口只返回 `credentialConfigured` 与末尾脱敏提示。撤销权限时将同一用户的 `is_system_admin` 改回 `FALSE`。

注册先调用 `POST /api/v1/auth/register/email-code` 发送 6 位邮件验证码，再向 `POST /api/v1/auth/register` 提交 `email`、`password`、`emailCode` 和可选 `displayName`。前端仅显示邮箱、验证码、密码三项，不提交 `displayName`。验证成功后才创建账号、初始团队和会话。163 邮箱使用 `smtp.163.com:465` 隐式 TLS；API `.env` 中的 `Email__Username`、`Email__Password` 需填写真实邮箱及 SMTP 授权码。本机已执行新增迁移 `015_registration_onboarding.sql`；2026-09-29 已验证保存后的凭据可通过 SMTP 登录（235），并重启 API 加载配置，健康检查正常。该 SMTP 登录结果不证明实际验证码投递；发送验证状态以注册引导说明中的日期记录为准。配置、合同及定向测试见 [邮箱注册与五步引导](docx/registration-onboarding.md)。

登录和当前用户接口为 `POST /api/v1/auth/login`、`POST /api/v1/auth/logout`、`GET /api/v1/auth/me`。品牌引导摘要使用 `GET /api/v1/onboarding`（包含初始品牌、步骤、`teamConfigured`、`required`），确认团队使用 `PUT /api/v1/onboarding/team`，品牌列表使用 `GET /api/v1/onboarding/brands`，指定品牌进度使用 `GET /api/v1/onboarding/{siteId}`，品牌创建使用 `POST /api/v1/onboarding/brands`，草稿使用 `PUT /api/v1/onboarding/{siteId}/draft`。品牌名称和官网域名必须填写；最终选择 1–5 个问题、0–10 个竞品，在单一事务中保存并完成引导，重复完成保持幂等。

品牌引导完成也提供 PRD 约定的 `POST /api/v1/sites/{siteId}/onboarding/complete` 别名；品牌设置可通过 `PATCH /api/v1/sites/{siteId}` 或 `PATCH /api/v1/sites/{siteId}/onboarding-settings` 写入，均需携带当前 `expectedVersion`。

引导第三/四步自 2026-11-11 起使用 `POST /api/v1/sites/{siteId}/prompt-suggestions/quick` 与 `POST /api/v1/sites/{siteId}/competitor-suggestions` 同步推荐，不抓取官网、不创建研究任务。问题候选默认约 8 条行业词 + 2 条品牌词，引导选择 1–5 条；竞品域名由模型给出、未经联网核验。接口只推荐、不落库、不扣积分，真实模型与完整新用户流程的验收边界见 [注册引导说明](docx/registration-onboarding.md)。

「新增监测问题」弹窗的 AI 推荐仍使用 `POST /api/v1/sites/{siteId}/brand-research` 创建异步任务、GET 查询、POST `/{jobId}/cancel` 取消。`industry-v2` 策略依次收集官网资料、提取有证据的业务画像、生成候选并独立审核；资料不足返回 `RESEARCH_NEEDS_INPUT`。弹窗使用 `singleRound=true` 返回本轮已通过部分，可通过 `reuseJobId` 复用画像换批。详细参数、写法规则、抓取与租约边界统一见注册引导说明中的「异步品牌研究」，不与同步引导混用。

本机可在另一个终端运行 `./scripts/run-local.sh research`，只启动品牌研究 Worker；`./scripts/run-local.sh worker` 保持启动全部后台任务。2026-11-11 起引导第三/四步改用同步快速推荐，不依赖研究 Worker；研究任务仍供「新增监测问题」弹窗的 AI 推荐使用。定向验证与实际模型调用记录见 [注册引导说明](docx/registration-onboarding.md)。

例如管理员登录后可将 OpenAI 兼容接口写入数据库（`apiUrl` 应填写完整的 chat-completions 地址）。凭据字段会写入 `research_provider_settings.credential`；API 列表响应仍只返回末尾脱敏提示：

```bash
curl -X PUT http://localhost:5260/api/v1/research/providers/openai-compatible \
  -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer <admin-session-token>' \
  -d '{"model":"gpt-6-luna","apiUrl":"https://api.openai.com/v1/chat/completions","credential":"<provider-api-key>","enabled":true,"priority":10,"webSearchEnabled":false,"timeoutSeconds":90}'
```

Development 下可用 `X-Tenant-Id` 调用 `POST/GET/PATCH /api/v1/sites` 及站点下的 `competitors`、`prompts`、`tags`、`targets`、`collection-previews`、`collection-schedules`。品牌、竞品、标签和 Prompt 列表统一返回 `{items,nextCursor}`，接受 `cursor` 和 `limit`（默认 50、最大 100）；游标绑定租户、站点及筛选条件。`PUT /targets`、配置 PATCH 要携带响应中的预期版本；Run 或导入提交另需 `Idempotency-Key`。新编排先选择已启用 Target 并取得预览，再以 `siteId` 和 `previewId` 创建 Run；返回的 Check 含 `runTargetId`、`sampleIndex` 和 Prompt 快照。相同幂等键和规范化 HTTP 请求返回原 Run，即使随后预览过期、Prompt/Target 编辑或 Cloro 停用；不同请求返回 409。归档的工作区仍可读取历史品牌、竞品、Prompt、标签、Run 和 Check。开发租户头只在 Development 生效，不能当作认证。

**授权基线（2026-11-07，安全修复 C1）**：所有 Controller 默认要求服务端会话，无会话返回 401 `AUTHENTICATION_REQUIRED`（Development 下带有效 `X-Tenant-Id` 的兼容桥除外）；匿名入口仅限 `auth` 的发码/注册/登录/登出、`/health/live` 与 `/internal/collector/*`（服务令牌认证），白名单由 `SessionAuthorizationEndpointTests` 维护，新增匿名入口须同步修改该测试。`/mcp`、`/health/ready` 不受此策略影响。写操作统一经 `TenantWriteAccess` 判定（owner/admin/member 可写，viewer 403 `CONTENT_FORBIDDEN`），竞品/标签写入、引导完成与草稿、品牌研究发起/取消、分析重算均已纳入。本机 5260 API 已于 2026-11-07 13:26 用该构建重启（Claude 终端标签 c1，`run-local.sh api`），匿名 401、开发兼容桥、下线路由 404、`/mcp` 不受影响均已实测；无迁移；Worker 未运行、未重启。证据见 `.trellis/tasks/archive/2026-11/11-07-sec-authz-baseline/verification.md`。

**登录防护（2026-11-08，安全修复 C2）**：只统计登录失败、不限制 IP 访问次数；同一账号（规范化邮箱，含不存在的邮箱）连续失败 30 次后锁定 1→2→4→8→15 分钟（上限 15 分钟，可经 `Auth:LoginThrottle` 配置），锁定期内直接返回 429 `RATE_LIMITED` 与 `Retry-After`，不校验密码；成功登录复位。不存在或停用账号同样执行一次等成本假散列，不按计时区分账号是否存在。计数跨实例共享（迁移 045）。身份接口响应带 `Cache-Control: no-store`；浏览器默认只写 HttpOnly Cookie，响应体不再含 `sessionToken`，非浏览器客户端需带请求头 `X-Jogeoai-Session-Delivery: token` 才在响应体取得令牌（此时不写 Cookie），其他取值返回 400 `INVALID_SESSION_DELIVERY`。本机 5260 已于 2026-11-08 10:00 用该构建重启（终端标签 c1），证据见 `.trellis/tasks/archive/2026-11/11-07-sec-login-hardening/verification.md`。

可重复的隔离库 API 验证脚本在 [tests/stage1_api_smoke.py](tests/stage1_api_smoke.py)。运行前应让 API 指向已执行 001–005 的独立测试库，并准备测试租户与测试用 Cloro 设置；脚本创建测试记录，不运行 Worker，不调用真实 Cloro，也不清理测试库。设置 `STAGE1_ALLOW_SETTINGS_MUTATION=1` 时，脚本还会暂时停用测试库的 Cloro 设置以验证幂等重放，然后恢复为启用状态。

`POST /api/v1/visibility-runs` 的 Cloro 请求使用 `targets`：每个 target 包含 `provider=cloro`、六个已注册引擎之一、可选 `locale`/`country` 和空 `options` 对象；国内 Collector 的 `platforms` 请求仍保持兼容且不能与 `targets` 混用。运行历史使用 `GET /api/v1/sites/{siteId}/visibility-runs`，接受 `status`、`provider`、`from`、`to`、`cursor` 和 `limit`，返回 `{items,nextCursor}`；不存在或跨租户的站点返回 404。详情使用 `GET /api/v1/visibility-runs/{runId}`、`GET /api/v1/visibility-runs/{runId}/checks` 和 `GET /api/v1/visibility-checks/{checkId}`，Development 下必须携带 `X-Tenant-Id`。新国内 Check 同样冻结 Prompt 版本、标签、品牌词分类与主体配置版本；旧行的 `snapshotMissing=true` 表示回填信息不完整。Cloro Check 固定为 `captureMode=scraped`、`evidenceClass=observed_surface`；原始响应只保存受控脱敏内容或哈希引用。

### 提及分析与核心指标

Development 下带 `X-Tenant-Id` 查询 `GET /api/v1/sites/{siteId}/analytics/overview`、`visibility`、`share-of-voice`、`share-of-voice/leaderboard`、`prompts`、`evidence`；`GET /api/v1/sites/{siteId}/analytics/prompts/{promptId}` 从不可变 `prompt_versions` 读取历史版本与标签快照，并按同一筛选和快照返回引擎表现、每日趋势、最近 Run、搜索词/引用摘要和逐来源竞品差距。聚合详情中的 `checkHistoryPath` 指向 `GET /api/v1/sites/{siteId}/prompts/{promptId}/checks` 跨 Run Check 历史，`GET /api/v1/visibility-checks/{checkId}` 查看回答、引用和原文命中位置。生产环境未配置可信租户身份时，这些接口关闭。采集和分析状态分别返回；无回答、待分析、分析失败及缺失快照不会被算成未提及。

默认是站点时区最近 30 个当地自然日的实际观测。`analytics/prompts` 与品牌概览、监控接口一样，默认覆盖 `cloro`、`doubao_api`、`deepseek_api`、`packy_qwen`、`packy_glm`、`packy_kimi`、`packy_yuanbao`、`domestic_collector` 八个 Provider、`api`/`scraped` 两种采集模式及全部证据类别；每行对应一个 Prompt 版本与来源（`provider|captureMode|evidenceClass`），按提问汇总时分子与分母分别相加。显式传入的来源筛选原样生效。其余旧分析入口仍默认 Cloro `scraped/observed_surface`。`from` 含、`to` 不含，须提供带时区的 ISO 时间；支持 Provider、Engine、国家、语言、采集模式、证据类别、Prompt/版本、标签和品牌词状态筛选。第一个响应的 `meta.snapshotToken` 可传给图表、榜单分页和下钻；令牌 15 分钟后过期返回 `SNAPSHOT_EXPIRED`。未知参数返回 `INVALID_FILTER`，游标绑定筛选与快照。跨来源请求按 `provider|captureMode|evidenceClass` 返回 `sourceGroups`/`sourceTrends`；全榜要求单一来源。

提及按 Check × 稳定主体 ID 计一次，每处原文命中留证据。`eligibleChecks` 是成功、有非空回答且指定发布版本分析完成的 Check（N）；品牌可见性为品牌命中 Check 数/N，声量份额为品牌命中数/全部配置主体命中数，自有引用率为 N 中至少有一个自有外部引用的 Check 数/N。比率均返回分子、分母和值；零分母为 `null`，有效样本中的零命中为 `0`。`aggregationMode=latest_observation` 只在 Visibility/SOV 使用，按 Prompt 版本 × Target 整组替换主体向量，给出真实观测数、延用数与来源日期。

`POST /api/v1/sites/{siteId}/analysis-jobs` 带 `Idempotency-Key` 和可选 `from`、`to`、`promptIds` 创建重算，`GET /api/v1/sites/{siteId}/analysis-jobs/{jobId}` 轮询进度。任务响应的 `requestedScope` 与 `effectiveScope` 明示实际范围：主体配置未变时复用请求范围外的已完成事实；主体配置变化时完整重建工作区以保持主体集合一致。新版本发布前继续读旧版本并标记 `configurationOutdated`。

### Fan-Out 与引用细分

`GET /api/v1/sites/{siteId}/analytics/query-fanout` 返回 Q 扩展事件、F 有扩展词 Check、C 成功 Check、Q/F、Q/C、F/C、搜索词暴露率及关联回答的品牌提及率。`query-fanout/queries`、`query-fanout/queries/{queryHash}`、`query-fanout/terms` 提供搜索词、Prompt 变体和中英文词项；公共 `analytics/evidence?queryHash=...` 下钻贡献 Check。搜索词只来自 Provider 返回字段；`queryAvailability` 区分 available、empty、not_exposed、unknown，旧空数组为 unknown。原数组仍在 Check 详情，分析时同 Check 去重并排除原 Prompt 和 unavailable 哨兵。中文词项采用单字及相邻双字，英文按字词边界；其他语言仅词项为 unavailable。

`analytics/citations` 返回外部引用事件数、成功 Check 数 C、有效 Check 数 N、自有引用率 `ownCitationRate`（N 中有自有外部引用的 Check 数/N）、自有事件占比 `ownEventShare`（自有外部引用事件数/全部外部引用事件数）、来源/页面类型分布和日趋势；多来源混合时 `ownCitationRate` 没有单一核心口径，值为 `null`。`citations/urls`、`citations/domains`、`citations/changes`、`citations/gaps` 提供榜单、前后等长窗口变化和竞品引用缺口；`analytics/evidence?urlHash=...` 或 `?domain=...` 下钻贡献 Check。域名详情内联最多 100 个 URL，`urlListPath` 指向同一快照下可继续翻页的 `citations/urls?domain=...`；域名筛选绑定 URL 榜游标，单个 URL 的事件份额仍以全部外部引用为分母。变化接口接受 `minimumEvents`（默认 2）、`declineThreshold`（默认 0.5）、`cursor` 和 `limit`（默认 50、最大 100），返回 `changes` 与 `nextCursor`；游标绑定快照和两个阈值，换筛选或阈值需从第一页重新查询。变化行的 `currentEvidencePath` 与 `previousEvidencePath` 分别固定当前和前期的同一分析版本及水位，指向可翻页的 `citations/evidence`；内联 `checkIds` 超过一百个时标记 `checkIdsTruncated`。URL/域名分别计算引用事件、不同 Check 和 Prompt 覆盖；同 Check 同 URL 只计一次，原始多位置仍在 URL/域名详情。`citations/google-internal` 单列已解析的 Google 搜索或购物链接，不计入外部引用分母。分类依据只使用不可变主体配置中的域名、维护的主机规则、URL 和标题，不访问引用页正文；未知归 other。消失仅表示比较窗口内未观察到。

这些入口复用核心分析的站点/租户、时间、模型、标签、来源筛选及 15 分钟 `snapshotToken`；分页游标绑定令牌与查询范围。只接受 `aggregationMode=observed`，`latest_observation` 返回 `INVALID_FILTER`。成功但无回答的 Check 进入 C，也会派生已有搜索词和引用；待分析 Check 的细分事实计入积压，不被当作零事件。

针对性验证使用隔离测试库，不指向默认 `jogeoai` 业务库。MySQL 8.4 新库执行 001–006 并重放 006；另用合成旧数据在 001–005 升级库执行并重放 006，2 条原 Check 保留、1 条有回答的旧 Check 入队并因缺冻结 terms 标为配置 unknown。设置 `JOGEOAI_STAGE2_TEST_CONNECTION` 和 `JOGEOAI_STAGE2_UPGRADE_CONNECTION` 指向独立库后执行 `dotnet test tests/Jogeoai.Tests/Jogeoai.Tests.csproj --no-restore --filter 'FullyQualifiedName~AnalysisSqlTests|FullyQualifiedName~AnalyticsCoreTests'`：10/10 通过，覆盖 Prompt 编辑与快照并发；`dotnet build Jogeoai.slnx --no-restore`：0 警告、0 错误。`tests/stage2_api_smoke.py` 对独立库上的 Development API 验证了路由、快照、分页、跨租户、Prompt 历史版本、错误和中文 Swagger 摘要。测试脚本会生成独立测试库；2026-09-29 已按项目指引删除本机测试库及 `.local/store-smoke`，正式库仅保留全局 Provider 配置和平台种子。

第三阶段针对性验证在隔离库完成：001–007 新库执行及 007 重放、001–006 带旧 Check 升级库执行及重放、旧数据异步发布、固定 Q/F/C 与引用事件算例、位置证据、Google 分流、前后窗口变化、跨租户和快照/游标，以及 `tests/stage3_api_smoke.py` 的 HTTP 与中文 Swagger 摘要。多站点同租户的旧隔离库升级与 007 重放也已验证。最终跨阶段执行 `dotnet build Jogeoai.slnx --no-restore`（0 警告、0 错误）及 `dotnet test tests/Jogeoai.Tests/Jogeoai.Tests.csproj --no-restore`（62 通过、3 跳过），并在已迁移的隔离库上复跑第一、二、三阶段 HTTP smoke，均通过。鉴权与品牌引导另外完成 PBKDF2 会话、Provider 结构化结果、子域名匹配和临时端口 Swagger/401 冒烟验证。隔离测试库和临时 store 已于 2026-09-29 清理，未删除正式库的全局 Provider 配置或平台种子。独立 AnalysisWorker 从新 Check 入库提交到细分分析可查询实测 3.145 秒，低于正常负载 60 秒目标。分析查询读取窗口内 Check 事实并在应用内投影；同一 `snapshotToken` 的 Check、前期指标和细分事实由 API 进程内最多两个快照的有界缓存复用。按 PRD §9.3 在 108,000 Check、540,000 引用、10 并发、预热后实测的 Overview、Visibility、SOV、Prompt 列表、Fan-Out、Citations 入口 P95≤1.29 秒、Check 详情 P95 25.74 毫秒；引用变化在 45 天当前窗口与前期窗口、`minimumEvents=1`、`limit=50` 下 P95 158.38 毫秒，全站 URL 榜 P95 128.33 毫秒，域名筛选 URL 榜 P95 293.01 毫秒。硬件、索引、缓存状态及局限见 [压测报告](tests/perf/analytics-108k-20260927.md)。历史 Check 缺 `capturedAt` 时进入质量计数，不以创建时间冒充采集时间。

`CREATE TABLE IF NOT EXISTS` 只保证目标表已经存在时不报错，不会把旧版 `002` 自动升级到新结构；如果某个环境已经执行过不同版本的 `002`，必须新增编号 migration 做结构变更，禁止依赖重复执行覆盖旧表。

### 断点调试

用 VS Code 打开项目根目录，安装 C# / C# Dev Kit 扩展，执行一次 setup 后，在“运行和调试”选择 `Jogeoai API`、`Jogeoai Worker` 或 `Jogeoai API + Worker`，按 F5。配置会先启动数据库、构建项目，然后加载 `.env`。

如果终端已运行 API，先在对应终端按 Ctrl+C，再启动 F5 调试，避免 5260 端口冲突。命令行启动与接口已验证；VS Code 交互断点命中尚未实测。Development 默认使用 HTTP，不需要配置 HTTPS 证书；`https` profile 是可选入口。

### 配置与数据库维护

- 数据库绑定 `127.0.0.1:3306`；Compose 项目名为 `jogeoai`，服务名为 `mysql`。
- 连接信息保存在本地 `.env`，示例中的凭据只用于本机开发；不要提交真实密钥。
- 更改端口、数据库名或账号时，同时更新 `.env` 的 `MYSQL_*` 与 `ConnectionStrings__Default`，然后重新执行 setup。
- 已初始化数据卷的账号、密码和数据库不会随 `MYSQL_*` 自动更改，需要单独执行数据库变更。
- Cloro Key 和 API URL 不再从环境变量读取，而是通过上面的配置接口写入数据库；setup 只同步数据库连接串，启动脚本和 VS Code 会加载完整 `.env`。
- 查看数据库：`docker compose ps`、`docker compose logs --tail 100 mysql`。
- 交互连接：`docker compose exec mysql mysql -u jogeoai_app -p jogeoai`，按提示输入本地配置的密码。
- 停止 API/Worker 用对应终端 Ctrl+C；停止数据库用 `docker compose stop mysql`。命名卷保留数据，`docker compose down -v` 会删除数据库数据。
- `.gitignore` 已列出 `.env`、构建输出等排除规则；2026-10-11 核对时项目根目录仍未初始化 Git，没有可核对的提交或分支。

## 项目目录

| 目录                                            | 职责 / 当前状态                                                                                                               |
| ----------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `src/Jogeoai.Api`                               | REST API 入口，已实现健康检查、鉴权/品牌管理、Cloro 配置/诊断、Visibility Run/Check、分析查询、品牌研究、知识库和内容管理接口 |
| `src/Jogeoai.Worker`                            | 后台进程，负责国内租约恢复、Cloro Submit/Poll、豆包方舟、DeepSeek API 与 PackyAPI 同步采集、分析、品牌研究、知识库摄取、品牌知识条目同步与网址识别、产品信息识别和文章生成任务 |
| `src/Jogeoai.Infrastructure`                    | MySQL 持久化、对象存储、文档解析、Embedding、向量索引和各业务 Store                                                           |
| `src/Jogeoai.Domain`、`src/Jogeoai.Application` | 领域模型、DTO 和跨层应用接口                                                                                                  |
| `src/Jogeoai.Providers.*`                       | Provider 契约、Cloro 适配器与 Domestic 项目中的豆包方舟与 DeepSeek Anthropic 兼容适配器；国内 Collector 控制面在 API/Infrastructure                        |
| `scripts/`、`.vscode/`                          | 本机环境和调试入口                                                                                                            |
| `docx/`                                         | 当前功能使用/运行说明、目标设计及参考索引                                                                                                        |
| `references/elmo/`                              | 项目内的只读 Elmo 参考源码，不参与 .NET 构建                                                                                  |

## 验证与后续工作

```bash
bash -n scripts/setup-local.sh scripts/run-local.sh scripts/migrate-local-feature.sh
docker compose config --quiet
dotnet build Jogeoai.slnx
curl -fsS http://localhost:5260/health/live
curl -fsS http://localhost:5260/health/ready
```

### 针对性测试原则

新增加或修改功能模块时，优先执行覆盖受影响行为的针对性测试，避免每次修改都运行全量测试。测试范围按变更边界选择：API 路由或中间件改动验证对应 HTTP 状态、响应和关键 Header；Provider 改动验证协议映射、超时和错误分支；数据库改动验证对应 migration、读写和约束；Worker 改动验证任务领取、重试和租约。

只有在跨项目公共契约、共享基础设施、解决方案配置或发布流程发生变化，或定向验证不足以覆盖明确的跨模块风险时，才扩大到 `dotnet build Jogeoai.slnx` 等验证，并记录扩大范围的原因。每次提交都应记录实际执行的针对性命令和结果；Provider 回归测试位于 `tests/Jogeoai.Tests`，运行时接口检查可使用 `curl` 或 Swagger UI。

纯 Markdown 文档或规则修改只验证链接、结构及事实一致性，不运行代码构建/测试。若修改了 Controller、DTO 或影响 Swagger 输出的 XML 注释，则构建受影响的 API 项目并检查对应 OpenAPI 摘要、响应和 HTTP 行为；记录本次实际执行的命令与结果。

数据库 migration 或集成测试完成后，应清理不再需要的临时测试表和测试库。清理前必须先列出对象、确认不属于共享开发库或业务数据，并取得确认后再执行删除；禁止用清理命令处理 `jogeoai` 业务库或命名数据卷。

当前能力和迁移状态见本文“当前实现”及各功能说明；内容管理的未完成需求和验收边界见 [内容管理说明](docx/content-management.md)，邮箱注册及引导见 [注册引导说明](docx/registration-onboarding.md)。其他目标环境的迁移仍需显式部署。真实页面采集仍需在独立 `Ai-visiblility-collector` 中配置 CN Profile、Service Token 和平台 contract 后执行。

### 国内 Collector 对接

API 使用 `Collector:ChinaServiceToken` 校验 `Authorization: Bearer <token>`；生产环境可通过 `Collector:ChinaTenantIds` 配置租户白名单。业务请求使用 `X-Tenant-Id` 和 `Idempotency-Key`。Collector 请求体中的 `region` 必须为 `cn`，结果必须带 `egressCountry=CN`。结果响应同时提供 `sampleId`（参考 Collector 合同）和 `checkId`（Jogeoai 业务查询）。

随后 Node.js Collector 使用 `/internal/collector/leases` 领取任务，按租约发送心跳，并把回答和公开引用提交到 `/internal/collector/results`；结果可选携带 `citationTitles`（与 `citations` 等长，单项可为 null）和 `searchQueries`（缺省时搜索词状态保持 `unknown`）。系统不会保存 Cookie、Profile 路径、Authorization Header、HTML 或截图。

多账号模式：Collector 配置 `COLLECTOR_SESSION_PROFILE_ID` 后，在领取请求中带上账号 ID，并调用 `eligibility`、`failures`、`session-events`；不配置时行为照旧。账号登记、平台闸门、解除熔断和按账号指标见 [账号池说明](docx/collector-account-pool.md)。

早期数据库业务模型的需求与迁移顺序见已归档的 [09-25 Collector PRD](.trellis/tasks/archive/2026-09/09-25-domestic-ai-collector-integration/prd.md)；当前实现状态以本文和现行任务记录为准。
