# App 渠道采集（安卓模拟器上的消费级 App）

> 对应 Trellis `11-16-app-channel-monitoring-api`（总 PRD 的子任务 A），迁移 [049](../db/migrations/049_app_channel.sql)，回滚 [rollback/049](../db/migrations/rollback/049_app_channel_rollback.sql)。本文按 2026-11-17 的源码和隔离库上的测试结果编写。每一项验证的命令和结果见 [verification.md](../.trellis/tasks/11-16-app-channel-monitoring-api/verification.md)，接口约定见 [contract.md](../.trellis/tasks/11-16-app-channel-monitoring-api/contract.md)（第 9 节给采集器，第 10 节给前端），设计见同目录的 `design.md`、`results.md`。

## 0. 现状（2026-11-17）

> **2026-11-19 更新：device-v1 已部署到本机。** 按设备派单、设备级 100 秒问题冷却（从实际发题时刻起算）、失败后同一任务转给其他设备，取消绑定和平台间隔、每日配额等约束。来源是 Collector 仓库 `.trellis/tasks/11-18-app-device-dispatch-retry/` 的补丁，测试结果见那里的 `verification.md`。执行 051 前，用 root 全库备份到 `.backups/jogeoai-db-pre-051-20261119-114756.sql.gz`；用应用账号执行 [051](../db/migrations/051_app_device_dispatch.sql)，只新增 `collector_app_device_attempts` 一张表，App 任务数不变（pending 60、succeeded 55、terminal_failed 5），执行时没有在途租约。API 与 Worker（监控模式 `--MonitoringOnly=true`，与重启前相同）已用新构建重启，健康检查为 200，OpenAPI 已包含 `device-v1` 和尝试历史接口。051 没有回滚脚本，回滚做法是回退源码，表保留。还没有采集节点用 device-v1 接入过，也没有做两台模拟器的联调。下文中“绑定 100 秒、平台 30 秒间隔”等派单规则已经不再适用。

- **源码已完成；本机已部署，但没有对任何租户开放。** 阶段 0–10 的代码在隔离库（`jogeoai_app_channel_test` 等，名称都含 `_test` 或 `_app049`）上通过了各阶段的定向测试和兼容性回归；这些隔离库已在 2026-11-17 部署后按用户确认删除，重跑 SQL 测试时按 `research/preflight.md` 0.7 重建。
- **本机 `jogeoai` 已执行 049（2026-11-17 22:16，用户确认后）。** 执行前用 root 备份到 `~/jogeoai-backups/jogeoai-pre049-20261117-221551.sql.gz`；用应用账号执行，1 秒完成。表从 118 张变为 124 张，现有各表的行数和关键内容哈希不变。API 与 Worker 已用新构建重启（Worker 为完整模式），`/health/live`、`/health/ready` 均为 200。详见 `verification.md`“闸门 7”。其他运行库没有执行 049。
- **真实请求只做过只读核对。** 重启后对本机 API 做了几个只读请求：目录和站点目标里没有 App 行；App 采集状态为未开启；不带令牌调用采集节点接口、不带会话调用运营接口都返回 401。没有任何采集节点接入，也没有用真实设备做过端到端验证。
- **默认不对任何租户开放。** `Collector:AppTenantIds` 默认为空。提问检查规则（DC-01）还没有经过用户确认，确认之前保持为空，或者只放测试租户。
- **另两个子任务还没有做。** 采集器一侧（子任务 B，Collector 仓库）还没有接入这些接口，前端（子任务 C）也还没有做。所以目前没有任何采集节点会来领取任务，页面上也看不到 App 行。
- 接口约定第 9 节（给采集器）已与源码逐项对照，还没有用 Collector 仓库的 zod 契约校验过，因为子任务 B 还没有提供契约。

## 1. 模块概览

App 渠道把“在安卓模拟器上的 App 里提问”当作一种正式的监控目标：

- **目标身份**：Provider `app_collector`，采集模式 `app`，引擎是平台代码，国家和语言固定为 CN / zh-CN，只支持快速回答，频率只能是每周一次。
- **平台**：豆包、DeepSeek、腾讯元宝、通义千问、Kimi、智谱清言，共 6 个可用平台。文心一言的入口存在，但种子就是停用的，原因是 `APP_CONTENT_UNREADABLE`。
- **目标键**：`CollectionIdentity.TargetKey("app_collector", engine, "app", "CN", "zh-CN")`。它与同平台 API 目标的键不同，现有的目标键都没有变。
- **不进任何默认集合**：App 没有加入 `AnswerProviderDefinition.All`。主计划、夜间计划、立即采集、手动采集、默认统计口径都不包含 App。
  - 立即采集和手动采集里如果带了 App 目标，返回 400 `APP_TARGET_NOT_ALLOWED`。
  - 有三处文章相关功能不筛选来源，App 回答里带 URL 的引用照常计入：文章监控、文章列表的被引次数、引用发现。团队汇总的“被引用文章数”取自文章监控，所以也包含 App 引用（用户 2026-11-17 决定）。
- **每周计划**：每个开启了监控的品牌有一个单独的 App 计划 `jogeoai:auto-prompt-monitoring:app:v1`，每天 09:11 物化，时区与主计划相同。目标按周频规则到期，到期后建一个周期 Run 和对应的任务，截止时间是 7 天后。
  - App 计划独立于主计划：只启用 App 目标的品牌也可以开启监控。
  - 品牌有主计划时，App 计划的启用状态跟随主计划；没有主计划时，App 计划自己的 `enabled` 就是开关。
  - 新启用的 App 目标，第一个周期从次日开始。
- **采集节点**：一台电脑上运行一个采集节点。一条“绑定”是设备 × App × 账号。全部节点的绑定组成一个全局账号池，不属于任何租户，共同消化同一个任务队列。节点经 `/internal/collector/app/*` 领取和提交，用节点自己的令牌认证。
- **限速**：每个绑定两次领取至少间隔 300 秒；每个平台的全部绑定合计至少间隔 30 秒；一台设备同一时刻只持有一个租约。默认不设每日上限。
- **计费**：按次预留。任务第一次被领取时，为这一条检测预留 10 积分；领取的条件是租户可用积分不少于 10，不为 API 监控留保护额。采到有效回答时结算，任务终态或到截止时释放。
- **未采集**：到截止还没有成功的检测，状态置为 `cancelled` 并写原因码（`APP_NOT_COLLECTED_CAPACITY` / `_CREDIT` / `_BINDING` / `_PLATFORM` / `_FAILED`），不计入失败数。
- **结果**：回答写入 `visibility_checks`（`capture_mode=app`）。带 URL 的引用进入引用表；没有 URL 的引用只保存在回答的元数据里，用于回答详情，不进入任何引用统计。
- **统计**：只有请求里明确带 `captureMode=app&provider=app_collector` 时才是 App 口径；`app` 不能与其他采集模式出现在同一个筛选里，否则返回 400 `INVALID_FILTER`。引用域名汇总一族用 `channel=api|app` 参数，默认是 `api`。团队汇总、文章工作台和 MCP 只读工具首期不提供 App 口径。
- **运营**：系统管理员接口 `/api/v1/admin/collector-app/*`，以及 Worker 的每日告警汇报。汇报首期只存档、不发送。

主要源码位置：

| 部分 | 文件 |
|---|---|
| 定义与规则 | `src/Jogeoai.Domain/AppChannelDefinition.cs`、`AppPromptRules.cs`、`AppCycleRules.cs`、`MonitoringProviders.cs` |
| 计划与周期 Run | `src/Jogeoai.Infrastructure/AppMonitoringPlan.cs`、`AppCollectionStore*.cs` |
| 账号池、领取与结果 | `src/Jogeoai.Infrastructure/AppCollectorPoolStore*.cs`、`AppCreditReservations.cs` |
| 容量、预估、状态 | `AppCapacityQuery.cs`、`AppMonitoringEstimate.cs`、`AppCollectionStatusQuery.cs` |
| 告警与每日汇报 | `AppAlertQuery.cs`、`AppDailyReportStore.cs`、`src/Jogeoai.Worker/AppDailyReportWorker.cs` |
| 接口 | `src/Jogeoai.Api/Controllers/AppCollectorController.cs`、`AppCollectorAdminController.cs`、`AppCollectionStatusController.cs` |
| 迁移是否已执行 | `src/Jogeoai.Infrastructure/AppChannelSchema.cs`（未执行时 App 相关入口返回 503 `APP_CHANNEL_SCHEMA_NOT_MIGRATED`） |

## 2. 配置项

| 配置 | 进程 | 默认值 | 说明 |
|---|---|---|---|
| `Collector:AppTenantIds` | API 和 Worker | 空 | 已开放 App 渠道的租户 ID 数组，写法与 `Collector:ChinaTenantIds` 相同（环境变量用 `Collector__AppTenantIds__0`、`__1` …）。无法解析为 GUID 的条目会被忽略。为空、缺失或不含某个租户时，对该租户：目录、站点目标和团队默认里都不返回 App 行；保存或预估带 App 目标时返回 400 `TARGET_NOT_ALLOWED`；App 时隙按 `skipped / APP_CHANNEL_NOT_OPEN` 跳过，不生成任务。已有的 App 行保留，不会被停用或删除。**API 和 Worker 必须配置成相同的值。** |
| `AppChannel:DailyReport:Enabled` | Worker | `true` | 为 `false` 时不注册每日汇报任务。 |
| `AppChannel:DailyReport:LocalTime` | Worker | `09:30` | 每天生成汇报的本地时刻，格式 `HH:mm`；无法解析时按默认值。 |
| `AppChannel:DailyReport:TimeZone` | Worker | `Asia/Shanghai` | 本地时刻所在的时区；无法识别时按默认值。 |
| `AppChannel:DailyReport:PollInterval` | Worker | 1 分钟 | 检查是否到期的间隔。 |

- 每日汇报只在完整模式的 Worker 里注册，用 `--MonitoringOnly=true`、`--ResearchOnly=true`、`--ReputationOnly=true` 这些分拆模式启动时不运行。App 截止处理（`AppDeadlineWorker`，每 30 秒一次）与 App 时隙的物化（`CollectionScheduleWorker`）注册在一起，完整模式和 `MonitoringOnly` 模式都运行。注册方式只读代码核对过，没有用真实进程验证。
- 重启后只补当天的汇报，不补之前漏掉的日子。
- 首期没有任何发送配置。汇报的发送状态记为 `not_configured`，不会发出任何网络请求，也没有机器人地址之类的配置项。
- 节点令牌由运营接口签发，不写在任何配置文件里。

## 3. 部署顺序

1. **备份目标库。**
2. **在目标库执行 049**（mysql 客户端加 `--default-character-set=utf8mb4`）。
   - 049 只做加法，可以重复执行。重放不会把运营停用的 App 入口重新启用，也不会改回已调整的限速。
   - 可以用应用账号执行，不需要 root。049 不建触发器，`collector_app_events` 的“只追加”由代码保证。
   - 049 改动了 4 张现有表，`collector_platforms` 完全不动：
     - `visibility_checks`：`capture_mode` 的 CHECK 增加 `app`，新增 `app_binding_id`。
     - `collector_platform_entries`：`channel` 的 CHECK 放开为 `consumer_web` / `consumer_app`，新增 `unavailable_reason`，并插入 7 条 `consumer_app` 入口。
     - `collector_tasks`：`channel` 的 CHECK 同样放开，新增 5 列和 App 领取用的索引。
     - `collector_attempts`：新增 `app_binding_id` 和索引，失败码的 CHECK 增加 App 的取值。
   - 049 另外新增 6 张表：`collector_app_nodes`、`collector_app_bindings`、`collector_app_platform_states`、`collector_app_events`、`collector_app_daily_reports`、`prompt_app_exclusions`。
   - **大表提示**：替换 `visibility_checks` 的 CHECK 会重建整张表。本机 `jogeoai`（2,262 条检测）上整个 049 用了 1 秒；更大的库没有测过要多久、锁表多久，执行前先评估，并先停 API 与 Worker。
   - 执行后核对：表数量比执行前多 6 张；`consumer_web` 入口不变；`consumer_app` 入口 7 条，其中 6 条启用；平台状态 7 行，间隔都是 30 秒；没有新增触发器；没有遗留名为 `jogeoai_049_*` 的存储过程。
3. **同时发布 API 和 Worker。** 两者都读 `Collector:AppTenantIds`，Worker 负责 App 时隙、截止处理和每日汇报。发布后检查 `/health/live`、`/health/ready`。
4. **保持 `Collector:AppTenantIds` 为空**，确认没有任何 App 时隙生成任务，现有品牌的站点目标、预估和主计划时隙与部署前一致。
5. 等 DC-01 确认、子任务 B 和 C 完成并做完全链路验证之后，才把租户加进 `Collector:AppTenantIds`，并且 API 和 Worker 要同时改。

如果 049 还没有执行：目录不返回 App 行，App 相关的写入、领取和运营接口返回 503 `APP_CHANNEL_SCHEMA_NOT_MIGRATED`，现有功能不受影响。这条退化路径只有纯逻辑测试，没有在未迁移的库上实测（见第 7 节）。

## 4. 运营操作

全部运营接口在 `/api/v1/admin/collector-app/` 下，只限系统管理员：未登录返回 401，不是系统管理员返回 403。每个实际发生了变化的写操作都追加一条审计事件。本机调用需要临时的系统管理员会话，做法同 [账号池说明](collector-account-pool.md)。下面这些路由都没有经过真实 HTTP 请求。

### 4.1 节点登记与令牌

| 路由 | 作用 |
|---|---|
| `GET nodes` | 节点列表：状态、最近出现时间、是否失联（超过 15 分钟没有出现）、绑定数。不返回令牌 |
| `POST nodes` | 登记节点并签发令牌（`{ nodeId }`，1–128 个字符）。重复登记返回 409 `APP_NODE_EXISTS` |
| `POST nodes/{nodeId}/token` | 轮换令牌，旧令牌立即失效 |
| `POST nodes/{nodeId}/disable`、`resume` | 停用或恢复节点。停用后该节点的全部请求返回 403 `APP_NODE_DISABLED`，它的绑定不计入容量，其他节点不受影响 |

- **令牌明文只在登记和轮换的响应里出现一次**，库里只存它的 SHA-256。拿到之后立即配置到该节点上，不要写进文档、日志或 Git。
- 网页采集器的共享令牌调不通 App 接口，节点令牌也调不通网页采集器的接口。

### 4.2 绑定

绑定由采集节点通过 `POST /internal/collector/app/bindings/report` 上报，控制面负责登记。新绑定默认为 `active`。同一平台的同一个账号代号只能属于一个绑定，重复上报会被拒绝（`APP_ACCOUNT_ALREADY_BOUND`），并写一条审计事件。

| 路由 | 作用 |
|---|---|
| `GET bindings?nodeId=&platformCode=&status=` | 绑定列表：状态、停止原因与停止时长、冷却、是否离线、最近出现、领取和成功的时间、限速。账号只以代号出现 |
| `POST bindings/{id}/disable` | 运营停用（`disabled_by_operator`）。之后采集器无论上报什么都不会解除，只能由运营恢复。正在执行的租约不收回 |
| `POST bindings/{id}/resume` | 恢复一个已停止的绑定，任何停止原因都可以。冷却不受影响；不会顺带恢复 App 入口 |
| `PUT bindings/{id}/limits` | `{ minIntervalSeconds, dailyQuota }`，间隔 60–86400 秒，上限 1–10000，或为 `null` 表示不设。两个值整体替换，省略 `dailyQuota` 就等于不设上限。越界返回 400 `INVALID_APP_LIMITS` |

- 停止原因分两类。`device_offline`、`version_mismatch`、`contract_unverified` 在采集器之后上报 `ok` 时会自动恢复。`login_required`、`challenge_required`、`contract_changed`、`emulator_rejected`、`disabled_by_operator` 只能由运营恢复。
- 遇到需要登录或需要人工验证时，**只能由授权人员在模拟器里手动处理**，处理好之后再调 `resume`。控制面和采集器都不会自动处理验证。
- 绑定间隔和平台间隔的下限（60 秒、10 秒）是实现时定的，设计里没有，目的是防止误操作让真实账号提问过于频繁。

### 4.3 平台的启停、限速与入口恢复

| 路由 | 作用 |
|---|---|
| `GET platforms` | 每个平台的合计间隔、每日上限、是否暂停及原因、App 入口是否可用及原因、入口能否由运营恢复 |
| `POST platforms/{code}/pause`、`resume` | 暂停或恢复平台。恢复时也会解除验证熔断，熔断计数从这一刻重新开始 |
| `PUT platforms/{code}/limits` | `{ minIntervalSeconds, dailyCap }`，间隔 10–86400 秒，上限 1–100000，或为 `null` 表示不设（按 Asia/Shanghai 的自然日计算）。两个值整体替换 |
| `POST platforms/{code}/entry/restore` | 恢复因为“App 拒绝模拟器”而被置为不可用的入口。其他原因停用的入口（例如文心一言的 `APP_CONTENT_UNREADABLE`）返回 409 `APP_ENTRY_NOT_RESTORABLE` |

- **验证熔断**：60 分钟内有 2 个不同的绑定上报 `login_required` 或 `challenge_required` 时，平台自动暂停。只统计运营最近一次恢复该平台之后的上报。
- **入口不可用**：该平台所有没有被运营停用的绑定都因为 `emulator_rejected` 而停止时，入口被置为不可用。恢复绑定不会自动恢复入口，恢复入口也不会恢复任何绑定，两者要分别操作。

### 4.4 容量与队列

`GET capacity` 返回每个平台的限速上限、实测产能、周容量、周需求、预计排不到的条数，以及当前队列里待领取和进行中的数量。

- 实测产能按单条任务的耗时计算：取最近 14 天成功任务耗时的中位数，再把每台设备的时间平均分给它有绑定的平台。样本少于 20 条时不使用实测，只用限速上限。
- 这个值只用于预估和运营视图，不用于拒绝保存，也不影响领取。
- 公式见 `design.md` 6.5。

### 4.5 提问排除

- 自动检查：`AppPromptRules`（候选规则 v1）。长度 1–200（按 UTF-16 码元计），单行，不含控制字符，不含 URL。**这套规则还没有经用户确认（DC-01）。** 没有通过检查的提问不生成 App 任务，提问管理的列表里会显示 `appEligibility`。
- 人工排除：`POST prompt-exclusions`（`{ tenantId, siteId, promptId, reason }`，原因 1–500 个字符），撤销用 `POST prompt-exclusions/revoke`。
  - 从下一个尚未展开的周期开始生效，API 渠道不受影响。
  - 排除记录只追加和撤销，不会被删除，它本身就是审计记录。另外还会写一条审计事件，事件里只有标识，不含提问原文和原因。
  - 审计事件在单独的事务里写。如果它写入失败，排除仍然有效，只记一条警告日志。

### 4.6 告警、审计事件与每日汇报

| 路由 | 作用 |
|---|---|
| `GET events` | 审计事件，可按时间、节点、绑定、平台、种类筛选，用游标分页 |
| `GET alerts` | 当前告警信息，与每日汇报用的是同一个查询 |
| `GET daily-reports`、`GET daily-reports/{yyyy-MM-dd}` | 汇报历史与单份汇报 |

告警信息包括：

- 失联的节点及失联时长；
- 停止的绑定，以及停止原因和停止时长；
- 被暂停或判定为不可用的平台；
- 过去 24 小时未采集的任务数，按平台和原因分列；
- 过去 24 小时的领取、成功、失败次数；
- 各平台本周的容量占用。

文心一言的入口种子就是停用的，不算告警。汇报里只有节点、设备、平台、账号代号和计数，不含提问原文、租户名或品牌名，也不含账号凭证。没有告警的日子也会生成一份，写明“无告警”。

## 5. 回滚

按影响从小到大分三级（`design.md` 第 12 节）：

1. **第一级：清空 `Collector:AppTenantIds`**，API 和 Worker 都要改并重启。之后 App 时隙全部跳过，不再生成新任务，已有的数据保留。
2. **第二级：停用全部 `consumer_app` 入口。** 目录会显示不可用，新的 App 目标不能再启用，领取会返回 `platform_unavailable`。
3. **第三级：执行回滚脚本**，先回退前端和 API / Worker，最后执行 `db/migrations/rollback/049_app_channel_rollback.sql`。
   - 脚本可以用应用账号执行，mysql 客户端加 `--default-character-set=utf8mb4`，可以重放。
   - 下列任一情况存在时，脚本会 SIGNAL 拒绝执行，不做任何修改：
     1. 有 `capture_mode='app'` 的回答；
     2. 有 `provider='app_collector'` 的站点目标；
     3. 有启用的 App 计划（停用的 App 计划不阻止回滚）；
     4. 有 `channel='consumer_app'` 的采集任务；
     5. 有任务引用了 `consumer_app` 入口；
     6. 有领取记录带 App 的失败码。
   - 后两条正常情况下已被第 4 条覆盖，只在数据不一致时触发，作用是防止回滚做到一半失败。
   - 这些数据是采集证据和计费依据，脚本不会代为删除。需要清理时，先列明对象并取得确认。
   - 回滚会删除节点、绑定、审计事件、每日汇报和提问排除。删除前先导出审计事件和每日汇报，查询语句写在脚本开头的注释里。
   - 被拒绝的回滚会留下存储过程 `jogeoai_049_app_channel_rollback`，下次执行时会被清除。

回滚脚本的拒绝路径只通过测试运行器验证过，没有用 mysql 客户端手工触发过。

## 6. 兼容性

- 没有 App 目标的品牌：站点目标（除新增的三个字段外）、目标键、主计划的选择、快照、`SelectionHash` 和预估，改动前后相同（`AppSiteTargetsSqlTests.Compat`）。
- 没有 App 单元格的预估响应与以前逐字节相同，不会多出 `channels` 字段。
- 有 App 目标的品牌：主计划的快照、`SelectionHash` 和预估里的 API 数字不变。
- 网页采集路径有五处加了 `channel = 'consumer_web'` 条件，另有一处在统计平台当日领取次数时加了渠道条件。对只有网页任务的库，结果没有变化。`CollectorSessionPoolSqlTests` 和 `CollectorResultIntakeTests` 在执行过 049 的隔离库上全部通过。
- 未采集的 App 检测会写 `captured_at`，值是判定未采集的时刻，这样默认口径的“缺少采集时间的检测数”不会变。
- 有两处可见的接口变化：
  - 显式传空的 `captureMode=`，同时 `provider` 为空或含 `app_collector` 时，现在返回 400。
  - MCP 读取 App 回答的详情现在返回 400。
- `GET /credits/usage`：同一天既有 API 又有 App 的监控消耗时，`prompt_monitoring` 会有两条。按类型只取第一条的旧前端会漏掉 App 那一条。这种情况只有在 App 对租户开放后才会出现。

## 7. 已知限制与未验证项

汇总自 `verification.md` 各阶段的“未验证”。

**部署与运行**

- 049 没有在本机 `jogeoai` 或任何其他运行库上执行过。
- API 和 Worker 没有用新构建启动过，所以下面这些都没有经过真实运行：
  - 模型绑定、中间件、真实路由、Worker 的调度循环和依赖注入解析；
  - Worker 循环里的截止处理（只通过直接调用 Store 验证过）；
  - 每日汇报在真实 Worker 里的生成。
- 没有用真实设备做过端到端验证（总 PRD AC-C18），要等子任务 B、C 完成并确认 DC-01。
- 第 9 节的接口没有用 Collector 仓库的 zod 契约校验（implement 10.3）。

**迁移与数据库**

- 049 在有真实数据的库上的耗时和锁表时间没有测过，见第 3 节的大表提示。
- 只在 MySQL 8.4.11 上执行过，没有用 root 执行过。
- 在未执行 049 的库上返回 503（以及状态接口返回空列表），只有纯逻辑测试。
- 新增查询和 JOIN 在大表上的执行计划没有看过，包括领取、截止、耗时中位数和回答记录列表多出的那次读取。
- `deviceId` 匹配和 `accountLabel` 唯一性不区分大小写，这一点是从 DDL 推断的，没有在库上验证。

**并发**

- 没有测试的并发情形：
  - 死锁重试；
  - 两个截止处理同时运行；
  - 同一任务的结果提交与失败上报竞争；
  - 对账与时隙物化同时发生；
  - 两个并行的开启监控请求；
  - 两个进程同时生成同一天的汇报；
  - 并发上报同一绑定。
- 两个节点并发领取同一平台、同一设备并发领取两个平台、积分并发：执行代理报告这些测试用例是真正并行的，主会话重跑通过，但没有另外验证“确实并行”。

**接口行为**

- 请求体缺失、JSON 损坏或类型不对时，由框架返回默认形状的 400，而且早于鉴权。这一点没有用真实请求验证过。
- 省略 `leaseSeconds`、`extendSeconds` 时取 60：靠记录类型的默认值，没有测试从 JSON 里省略这两个字段的情况。
- 不带时区偏移的时间按服务器本地时区解析，结果取决于部署机器的时区。
- 每次领取最多查看 8 条候选，这个上限被用满的情况没有测过。
- 汇报 JSON 超过 256 KB 时返回 500，并每分钟重试一次，不会截断。
- 超过 16 KB 的 App 回答在真实入库后再读详情，只有不连库的测试。
- 夏令时时区下 09:11 时隙的行为没有测过。
- 数据库时钟与应用时钟有偏差时的表现没有测过。

**已知现状（只记录，没有改）**

- 失败上报带着错误的 `taskId` 时返回 404 / 403，整条上报都不生效，绑定也不会停止。采集器应改用 `probe` 阶段重新上报。
- 续租是把截止时间设为“现在 + `extendSeconds`”，可能反而缩短租约；续租也不刷新绑定的最近出现时间。
- 实测产能只用成功任务的耗时，会偏高；设备时间是按平台平均分的。
- 团队预估对每个跟随品牌各读一次容量，品牌多时会重复读取。

**其他测试缺口**

- `GeminiAnswerPersistenceSqlTests` 要求本机没有的端口和库名，没有运行。阶段 6 抽取 API 积分结算代码时，用机械核对代替了它。
- `CitationDomainSqlTests` 的 8 个数据库用例要求本机没有的库，改动前后都失败，没有运行。
- 文档与配置：`.env.example` 已加 `Collector__AppTenantIds`（默认空）。

**确认状态**

- DC-01 提问检查规则：未确认。
- 两项按设计建议采用、用户没有逐项确认的做法：新目标从次日开始第一个周期；“一周内分散执行”用“一次创建、7 天截止”实现。
