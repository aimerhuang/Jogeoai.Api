# 品牌监控模型配置：回答模式 × 采集频率

> 对应 Trellis `11-09-brand-model-modes`，迁移 [047](../db/migrations/047_answer_mode_frequency.sql)。本文按 2026-11-09 的源码、已执行的迁移和本机真实验收编写；00:05 / 09:01 真实时隙已于 2026-11-11 核对。部署状态、已验证和未验证的事项见 [README](../README.md)「品牌监控模型配置：回答模式 × 采集频率」，命令与证据见 [verification.md](../.trellis/tasks/11-09-brand-model-modes/verification.md)。

## 1. 用户怎么用

入口：团队管理 → 品牌管理 → 品牌详情 →「单独配置」，或团队管理 →「大模型监控配置」→ 品牌行「编辑频次」，打开「{品牌名}大模型监控」弹窗。owner / admin / member 可以编辑；viewer 只能查看，看不到这个入口。保存后品牌变为「单独配置」，不再跟随团队默认配置（048 起；团队默认配置、跟随与恢复跟随见 [团队默认监控配置说明](team-monitoring-config.md)）。团队默认配置自 2026-11-11 起内嵌在团队页，品牌配置仍用弹窗，两处共用矩阵组件；内嵌页走查证据见 [前端夹具记录](../.trellis/tasks/11-11-team-monitoring-inline-matrix/verification.md)，不代表真实 API 页面验收。

- 每个模型一行。国内外模型放在同一张表里，不显示接入来源（Cloro、PackyAPI 等）。国内网页采集目标不在这里显示，保存时也不受影响。
- 两列：「快速回答」「深度思考」。每个单元格有一个勾选框和一个频率下拉。模型不支持的模式显示 `—`。同一模型可以两种模式都开。
- 列头勾选框可以整列全选或全不选（部分选中时显示半选）。「一键关闭」取消全部勾选，「批量设置」把所有已勾选单元格改为同一频率。
- 右侧「预计生效配置」显示每周采集量、下一轮和每周的积分预估（来自 `POST /api/v1/sites/{siteId}/targets/estimate`）。余额不足时只提示缺口，不阻止保存。
- 点「保存配置」后才生效。没有勾选任何单元格时不能保存，也就是说，不能在这个弹窗里关掉全部每日监控。
- 如果别人先保存了，会返回 409，弹窗提示「配置已被他人修改，请刷新」，并按服务端最新配置重置草稿。
- 回答记录列表和回答详情会显示回答模式标签：「快速」「深度」；047 之前的历史回答显示「历史」，不回填推测值。

## 2. 模型能力

每个模型支持哪些模式、默认是哪种模式，由后端目录（`CollectionTargetView.supportedAnswerModes` / `defaultAnswerMode`）给出，前端不写死。下表依据 2026-11-09 的真实探测（每个组合 1 次，共 8 次，见 [answer-mode-probe.md](../.trellis/tasks/11-09-brand-model-modes/research/answer-mode-probe.md)）和 Cloro 文档查阅（见 [cloro-modes.md](../.trellis/tasks/11-09-brand-model-modes/research/cloro-modes.md)）。

| 模型 | 快速回答 | 深度思考 | 默认模式 | 依据 |
|---|---|---|---|---|
| ChatGPT、Perplexity、Copilot、Gemini、Google AI Mode、Google AI Overview | ✓ | — | 快速 | Cloro 各接口请求体是严格 schema，没有思考、推理或深度研究参数；只查了文档，没有实测 |
| DeepSeek | ✓ | ✓ | 快速 | fast（`thinking=disabled`）：13 秒，1231 字，36 条引用，0 个推理块；deep（`thinking=enabled`）：38 秒，3378 字，49 条引用，4 个推理块 |
| 通义千问 | ✓ | ✓ | 快速 | deep（`enable_thinking`）：98 秒，6511 字，61 条引用，10 个搜索词，开启思考后仍会联网搜索 |
| Kimi | ✓ | ✓ | 快速 | deep（`thinking=enabled`）：72 秒，2010 字，18 条引用，有推理块 |
| 豆包 | ✓ | ✓ | **深度** | 现网是 messages 模式且没有设置 thinking，模型默认开启思考，所以迁移前的采集实际是深度思考。fast（`thinking=disabled`）：17 秒，956 字，20 条引用，0 个推理块。快速回答只在 messages 模式下可用 |
| 元宝 | — | ✓ | **深度** | hy4 设置 `thinking=disabled` 后仍有推理块（169 秒），关不掉思考 |
| 智谱清言 | ✓ | — | 快速 | glm-5.3-flash 在 low / high 两档下都会推理，字数、引用、搜索次数几乎相同，单样本区分不出差异。用户 2026-11-09 决定只开放快速回答，避免为两条几乎一样的回答各付一次积分 |

说明：

- **默认模式 = 迁移前的实际行为**。所以 047 之后，豆包和元宝的存量目标会出现在「深度思考」列。这不是改动，它们本来就在深度思考。
- **国外模型的「快速回答」指 Cloro 的默认界面**。实际用哪个模型由平台自己决定，例如 ChatGPT 会按请求自动选择模型，可能路由到推理模型。我们无法也没有要求它关闭思考。Cloro 以后开放模式参数时，只需扩展支持列表，契约不变。
- 深度思考明显更慢（DeepSeek 约 3 倍，千问约 98 秒），但仍在 Worker 租约和 Provider 超时之内。
- 每日监控不再读取 Provider 全局配置里的 `thinking` / `reasoningEffort`，模式完全由品牌的单元格决定。管理员诊断接口（Provider test）仍按全局配置调用。

## 3. 采集频率和时刻

| 频率 | 每周次数（用于预估） | 什么时候采 |
|---|---|---|
| 2次/天 | 14 | 每天 00:05 和 09:01 各一次 |
| 1次/天（默认） | 7 | 每天 09:01 |
| 每2天1次 | 3.5 | 09:01，从锚点日期起每隔一天（锚点当天算第一次） |
| 1次/周 | 1 | 09:01，与锚点日期相同的星期几 |

- **两个本地时刻**：09:01（主计划 `jogeoai:auto-prompt-monitoring:v1`）和 00:05（凌晨计划 `jogeoai:auto-prompt-monitoring:night:v1`）。两者都按主计划的时区计算；主计划在创建时使用品牌（团队）时区。同一本地日期内，00:05 那一轮先于 09:01 那一轮。主计划原来是 09:00，047 改为 09:01。
- **锚点 `frequencyAnchor`**：目标首次启用，或频率改为「每2天1次」「1次/周」时，写为保存当天的本地日期；改成其他频率时保留原值（1次/天、2次/天不使用锚点）。日期按主计划时区计算，没有主计划时按品牌时区。原因：品牌改时区后，主计划的时区不会跟着改，而时隙日期按主计划时区算，两者必须一致。保存当天的 09:01 如果已经过去，第一次采集顺延到下一个满足条件的日期，不会补采。
- **未到期的单元格**：在当天的时隙中被跳过，不建检测、不预留积分。如果一个时隙里没有任何到期的单元格，时隙记为 `skipped`，`skip_code=MONITORING_NOT_DUE`，不建 run。
- **生效时间**：修改模式或频率后，从下一个还没冻结的时隙开始生效。已经冻结或正在运行的轮次不变。新开启的单元格同样从启用之后的下一个时隙开始，不补启用前的时隙。
- **与提问平台范围（042）的关系**：频率按「品牌 × 模型 × 模式」设置，同一品牌的所有提问共用。提问的自定义平台范围只决定这条提问跑哪些单元格（取交集，交集为空时该提问本轮不采集）。

### 凌晨计划的生命周期

2026-11-12 源码核对增量：新增提问 / 新增模型的立即采集不受频率到期限制；09:01 主时隙另剔除当天已立即采集的「提问 × 模型单元格」。这是提问写入触发的行为，不是修改品牌模式或频率后补采。合同、同版本发布要求及尚未闭合的真实去重 / 积分验收见 [提问管理说明](question-management.md#立即采集增量2026-11-12-核对)。以下凌晨生命周期不变。

- 品牌有至少一个「2次/天」的单元格，且主计划处于启用状态时，系统创建或重新启用凌晨计划；没有时停用（`next_slot_at` 清空）。没有主计划时不创建。
- 触发时机：站点目标 PUT 提交后立即对账一次（失败只记日志）；每个时隙展开前，以及 `prompt-monitoring` 的 GET / POST，还会懒对账一次兜底。
- 凌晨计划的快照只包含 2次/天的单元格，采样数跟随主计划。不为凌晨计划补首轮 run。重新启用时从下一个 00:05 开始。
- 凌晨的 run 不会让当天 09:01 被判为「已跑过」：「当天是否已跑」只看首轮和主计划自己的 run。
- 凌晨轮与主轮各自独立预留和结算积分。

## 4. 积分

- 单价不变：每条检测 10 积分（`PromptMonitoringPricing.UnitPrice`），即每条提问 × 每个单元格 × 每个采样 × 每轮 10 积分。同一模型两种模式都开时，每轮算两条检测，各扣 10 积分。
- **手动采集与采集计划同价计费（Trellis `11-27-sec-manual-run-charging`，本机 API / Worker 已于 2026-11-27 用该构建重启，预占与结算没有运行态验证，见 §9）**：是否计费不看计划名称，也不看客户端提交的任何字段，凡是会产生真实 Provider 调用的采集都先预占积分。
  - `POST /api/v1/visibility-runs` 的 `previewId` / `targetIds` / `tagIds` 形态和 `targets` 形态：创建运行时预占「检测数 × 10」，余额不足返回 402 `CREDIT_INSUFFICIENT`，不创建运行；同一 `Idempotency-Key` 重放返回原运行，不重复预占。系统管理员发起的手动采集同样扣所在租户的积分。结算规则与每日监控相同（提交成功扣除，未提交即失败的释放）。
  - 所有采集计划（每日监控主计划、凌晨计划和用户在 `collection-schedules` 自建的计划）的时隙运行都预占积分。余额不足时该时隙记为 `skipped`、`skip_code=CREDIT_INSUFFICIENT`，计划保持启用，下一个时隙照常尝试。只有每日监控计划按提问的平台范围展开，自建计划仍是提问 × 目标的全量组合。
  - `platforms` 形态（国内网页采集通道）不计费，见 §9。
  - 采集预览 `CollectionPreviewView.estimatedAmount` 返回「预期检测数 × 10」，即按该预览创建运行时会预占的积分。
- 每周预估（`targets/estimate`）：

  ```
  weeklyChecks  = Σ（每个已启用单元格）该单元格的有效提问数 × 采样数 × 每周次数
  weeklyCredits = weeklyChecks × 10
  ```

  「有效提问数」已按提问平台范围取交集；「每2天1次」按 3.5 计，界面显示为「约」。草稿里的国内网页采集目标和停用的目标不计入。
- 下一轮预估：取 00:05 和 09:01 中较近的一轮，只计当轮到期的单元格，返回 `nextSlotAt` / `nextSlotKind` / `nextSlotChecks` / `nextSlotCredits`。`balance` / `shortfall` 与 `prompt-monitoring/estimate` 口径相同，余额不足不阻断保存。
- 上限：每轮最多 20 个目标、1000 条检测，一种模式算 1 个目标。当前最多可勾选 16 个单元格（国外 6 + DeepSeek / 千问 / Kimi / 豆包各 2 + 智谱 1 + 元宝 1）。超过时返回 400 `MONITORING_TOO_LARGE`。
- 已知口径差异：`prompt-monitoring/estimate` 的「每轮」仍按全部单元格同时到期计算（用于判断 1000 条上限），因此它的 `shortfall` 是上限值；计费视图（`prompt-monitoring` 的 billing）按下一个 09:01 时隙过滤，不含 00:05 那一轮。弹窗以 `targets/estimate` 为准。

## 5. 保存语义（运维和接口调用方）

- `PUT /api/v1/sites/{siteId}/targets` 是替换语义，带 `expectedVersion` 做乐观并发，冲突返回 409。048 起保存成功后品牌标为单独配置（`followsTeamDefault=false`）；团队默认配置下发与恢复跟随复用同一段写入逻辑（`ApplySiteTargetsAsync`），锚点、`monitoring_enabled_at` 与版本号语义与本节一致。
- 请求体的 `scope`：
  - `all`（默认，旧客户端不传时就是它）：替换全部站点目标，与 047 之前完全一致。
  - `monitoring`（新弹窗使用）：只替换 `monitoringEligible=true` 的目标，国内网页采集目标原样保留。此时提交不可监控的目标返回 400 `TARGET_NOT_ALLOWED`；`scope` 取值未定义返回 400 `INVALID_TARGETS`。
- 每个目标可带 `answerMode`（省略表示默认模式）和 `frequency`（省略表示 `daily`）。模式不在该模型的支持列表中返回 400 `ANSWER_MODE_UNSUPPORTED`；同一个键重复返回 400 `DUPLICATE_TARGET`。未知的字符串枚举值（如 `"frequency":"hourly"`）在 JSON 绑定阶段就返回通用 400。
- **目标键**：默认模式的键是 `Hex(provider|engine|captureMode|country|locale)`，与 047 之前逐字节相同；非默认模式在末尾追加 `|mode`。所以同一模型的快速 / 深度是两个不同的站点目标和两个 run target。各 Provider 的默认模式发布后不能再改，否则键的含义会漂移。
- 未执行 047 的库：读取按默认模式 + daily 处理；写入默认模式 + daily 照常；写入非默认模式或非 daily 返回 503 `ANSWER_MODE_SCHEMA_NOT_MIGRATED`。

## 6. 部署

1. 备份目标库。
2. 用 root 执行 047，mysql 客户端加 `--default-character-set=utf8mb4`。可重复执行。执行后核对：新列存在；主计划 `local_time=09:01:00`，`next_slot_at` 只顺延 1 分钟；`site_targets` 行数不变，全部是 `answer_mode IS NULL`、`frequency='daily'`。
3. **同时**发布 API 与 Worker。旧 Worker 不认识 `answer_mode`，会按全局配置的思考参数采集，记录的模式就会错；不支持新旧 Worker 滚动并存。
4. 发布前端，并从新构建重新生成 `src/types/generated/api.ts`。

## 7. 回滚

047 新增的数据会在回滚时丢失：所有 `visibility_checks.answer_mode`（之后历史 deep 检测都显示为模式未知），以及目标的频率和锚点。执行前列出受影响的对象和数量，并取得确认。

1. **先在新版产品里把配置改回默认**（推荐，API 会同步处理目标键和凌晨计划）：
   - 删除所有非默认模式的单元格，例如 DeepSeek 深度、豆包快速。**不能只把 `answer_mode` 清空**：非默认模式的目标键带 `|mode` 段，清空后旧代码会把它当成另一个同名目标。
   - 所有单元格的频率改回「1次/天」。
   - 确认凌晨计划已停用（没有 2次/天 单元格后，API 会自动停用）。
2. 如果只能手工处理，先用 rollback 脚本头部注释里的 SELECT 列出 `answer_mode IS NOT NULL OR frequency <> 'daily'` 的目标和启用的凌晨计划，逐行确认后再处理：非默认模式的行删除；`answer_mode` 等于默认模式的行可以置空；频率改为 `daily` 并清空锚点；凌晨计划设为停用并清空 `next_slot_at`（停用的凌晨计划行可以保留）。
3. 回滚前端，再回滚 API 与 Worker 到不含回答模式和频率的版本。
4. 执行 [rollback/047](../db/migrations/rollback/047_answer_mode_frequency_rollback.sql)（`--default-character-set=utf8mb4`）。以下任一情况存在时，脚本用 SIGNAL 拒绝执行，且不做任何修改：启用的凌晨计划、`answer_mode` 非空的目标、`frequency <> 'daily'` 的目标。通过后，主计划改回 09:00（`next_slot_at` 减 1 分钟），删除 3 个 CHECK 和 4 个新列。脚本可重复执行。

还没有任何非默认模式或非 daily 目标时（例如刚执行 047、尚未发布新 API），可以直接执行回滚脚本，或从第 1 步之前的备份恢复。

## 8. 已知限制

- `prompt-monitoring` 视图的「最近一轮」只看首轮和主计划的 run，不显示凌晨 run。
- 计费视图不含 00:05 那一轮；`prompt-monitoring/estimate` 的每轮数是所有单元格同时到期时的上限（见 §4）。
- 文章工作台的目标快照里，同时开两种模式的模型会出现两次，只影响上下文文字。
- `CollectionPreviewView.targets[].frequency` 总是 daily；手动采集不看频率。
- 积分流水中手动采集和自建计划的预占、结算沿用「每日监控预留 / 每日监控已提交」等文案，没有按来源区分。
- 看板按 Provider + 引擎 + 地区合并同一模型的两种模式；回答记录没有按回答模式筛选的参数；MCP 输出不含回答模式。
- 回答详情里原有的「采集来源」浮层仍显示 Provider 名称。

## 9. 手动采集与采集计划的计费、名称和权限（2026-11-27）

Trellis `11-27-sec-manual-run-charging`。没有数据库迁移。状态（2026-11-27）：

- 代码与不连库的定向测试已完成。
- SQL 测试已在本机隔离测试库（已执行 001–052）运行：`ManualRunChargingSqlTests`、`CollectionScheduleSqlTests`、`InstantCollectionSqlTests`、`AnswerModeSqlTests` 合计 22 项通过（首次运行 19 项通过、1 项跳过；跳过的用例随后接上提问范围测试库运行，所在测试类 3/3 通过），`PromptImportSqlTests` 9 项通过。`GeminiAnswerPersistenceSqlTests` 没有运行：它只接受 33318 端口的专用实例，本机没有。
- 本机 5260 API 与 Worker 已于 2026-11-27 22:07 用该构建重启，健康检查为 200；`prompt-monitoring`、`collection-schedules`、`visibility-runs`、`targets` 的只读接口经 Development 的 `X-Tenant-Id` 兼容桥返回 200。
- 没有运行态验证的：手动采集和自建计划时隙的积分预占与结算，以及下面的名称、数量和权限规则（只有上述测试证据）。

### 计费

见 §4。计费只取决于运行是否带预占项，与计划名称、`requested_by`、Provider 无关。

### 计划名称与数量

| 情形 | 结果 |
|---|---|
| 创建计划，名称是每日监控主计划或凌晨计划的保留名称 | 409 `SCHEDULE_NAME_RESERVED` |
| 自建计划改名为上述保留名称 | 409 `SCHEDULE_NAME_RESERVED` |
| 每日监控主计划或凌晨计划改名 | 409 `SCHEDULE_NAME_LOCKED` |
| 站点的自建计划已达上限后再创建 | 409 `SCHEDULE_LIMIT_REACHED` |
| 创建或改名为 App 采集计划的名称 | 400 `INVALID_SCHEDULE_NAME`（原有行为，未变） |

- 保留名称的判断不区分大小写、全角 / 半角、重音符号和不可见字符：`collection_schedules.name` 列的排序规则是 `utf8mb4_0900_ai_ci`，这些写法在按名称查询计划时与保留名称相等。这类写法（包括 App 采集计划名称的变体）一律返回 409 `SCHEDULE_NAME_RESERVED`。
- 判断分两步，以第二步为准。第一步在应用层按 Unicode 规范化折叠后比较（`PromptMonitoringIdentity.IsReservedScheduleName`），不访问数据库，是近似判断：没有 Unicode 分解、但排序规则视为与基本字母相等的写法（如带删除线的 `ø`）会通过这一步。第二步在创建或改名的同一事务内，写入后由 MySQL 按 `name` 列自身的排序规则把这一行与三个保留名称比较（`CollectionScheduleStore.HasReservedNameAsync`，与对账逻辑按名称查计划用的是同一种比较），相等就不提交事务，返回 409 `SCHEDULE_NAME_RESERVED`。第二步除了一条按源码文本固定调用顺序的测试，还有隔离库用例，已于 2026-11-27 在本机隔离库运行并通过（比较由 MySQL 按 `utf8mb4_0900_ai_ci` 完成）。隔离库用例对 `ø` 是先问 MySQL 再按结果断言，所以用例通过不说明该排序规则把 `ø` 视为等于 `o`。
- 这次改动之前已经存在的、名称与保留名称仅写法不同的自建计划不会被处理（改名才会触发判断）；这类计划可能被按名称查询的对账逻辑当成监控计划。发布前盘点时请用 `name IN (...)` 按列的排序规则查询，再与精确名称比对。计费不依赖名称，所以这不是计费绕过。
- 每日监控计划只由服务端内部创建（`ICollectionScheduleStore.CreateAutomaticPlanAsync`，调用方只有 `PromptMonitoringService`），不经过保留名称与数量上限校验；公开接口只调用 `CreateAsync`。
- 上限由配置 `Collection:Schedules:MaxCustomPerSite` 决定，默认 20，只在 API 进程读取。启用和停用的自建计划都计入；主计划、凌晨计划和 App 采集计划不计入。
- 已知限制：上限不是严格上限。数量在创建事务内统计，该事务对站点行持有的是共享锁（`FOR SHARE`），不串行化同一站点的并发创建，并发请求可能同时通过检查，使计划数超过上限。上限只限制计划行数，不影响计费（每个计划的时隙都计费）。
- 已知限制：采集计划没有删除接口，也没有删除计划的代码路径。达到上限后只能修改并复用已有计划（停用的计划同样计入），所以上限实际是每个站点累计能创建的自建计划数。
- 改名前已经被改过名的历史计划不做数据修复：它们现在是自建计划，时隙同样计费。

### 已知限制：预占长期不结算的情形

预占在检测提交成功时扣除、在任务终态失败（且未提交到外部）时释放。下面两种情形任务到不了终态，预占一直是 `held`，对应积分留在「已预占」里不回到可用余额。这两种情形在每日监控中原本就存在；手动采集计费之后，用户发起的手动采集也会遇到。只读了源码，没有用真实任务验证；目前没有自动释放，需要人工处理。

- **对应 Provider 的 Worker 没有运行或被关闭**（`Doubao:Worker:Enabled=false`、`DeepSeek:Worker:Enabled=false`、`Packy:Worker:Enabled=false`，或 Worker 进程不在）：任务停在 `pending`。创建手动采集时接口只检查 Provider 是否已配置，不检查 Worker 是否在运行，所以运行可以创建成功并预占。关闭某个 Worker 之前先停用对应目标，或确认没有待处理任务。
- **任务租约反复过期**（Worker 在调用中途崩溃或被重启、单次调用超过租约时长）：过期的任务回到 `pending` 重新领取，这条路径没有次数上限（重试次数上限只在 Worker 收到 Provider 错误时生效）。

另外两条沿用既有规则、不属于本次改动：已提交到 Cloro 后失败的任务不退还；提交结果不明（`submission_unknown`）的检测保持预占，等人工对账。

### 发起人标识

`visibility_runs.requested_by` 由服务端写入：有会话用户时为 `user:{userId:N}`，Development 的 `X-Tenant-Id` 兼容桥（没有会话用户）为 `dev-bridge`。请求体中的 `requestedBy` 保留以兼容旧客户端，只参与 HTTP 请求摘要，不存储，也不参与任何判断。「当天是否已有监控运行」的判断（`prompt-monitor:{siteId}`、`schedule:{planId}`、`prompt-instant:{siteId}`）因此不会被手动采集命中。

兼容性：没有 `http_request_hash` 的早期运行（该列引入之前创建）用原幂等键重放时，会与新的服务端发起人标识比对不上，返回 409 `RUN_IDEMPOTENCY_CONFLICT`。有 `http_request_hash` 的运行重放不受影响。

### `platforms` 形态（国内网页采集通道）

- 非 Development 环境只允许系统管理员发起，其他人返回 403 `ADMIN_REQUIRED`，在幂等重放查询和任何运行存储调用之前判定。Development 下行为不变（`X-Tenant-Id` 兼容桥和已登录成员都可以发起）。
- 每次最多 200 个 Prompt、20 个平台入口，超出返回 400 `INVALID_RUN`。
- 这条通道不预占积分。

### 上线前后

- 发布后，已有的自建计划从下一个时隙开始计费；余额不足的租户会看到时隙被跳过。**发布前在目标库盘点自建计划**（名称不是三个保留名称的计划）的数量、所属租户和启用状态。本机开发库 `jogeoai` 由协调方在 2026-11-27 只读核对：没有自建计划，只有 3 个租户的 5 个主计划、1 个停用的凌晨计划和 1 个 App 采集计划，所以本机没有计划会因此开始计费。其他运行库尚未核对。
- 运营在本机用 `platforms` 形态 + Development 发起的国内采集不受影响。用 `targets` / `previewId` 形态做的手动采集会扣所在租户的积分，需要时由系统管理员用现有的积分发放接口补充。
- 验证脚本 `tests/stage1_api_smoke.py` 与 `tests/doubao_api_smoke.py` 用 `targets` / `previewId` 形态创建手动运行，现在同样预占积分：测试租户余额不足时脚本会在创建运行处得到 402。两个脚本没有随本次改动修改，也没有重新运行过。
- API 与 Worker 需要一起发布：计划时隙由 Worker 生成和处理。
- 回滚：还原代码后重启 API 与 Worker。已经预占或扣除的积分不随代码回滚，需要时用积分发放接口人工调整。
- 前端 `Jogeoai.web` 的类型尚未按新契约重新生成（`requestedBy` 的说明、`estimatedAmount` 有值、新的状态码）。
