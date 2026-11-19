# Cloro 多账号 API Key 池

> 现状（2026-10-11）：后端代码已实现，定向测试 202 项通过。2026-10-11 已在本机 `jogeoai` 备份后执行 022，并用真实账号完成切换验收：故意无效的 key 被 Cloro 以 401 拒绝后，同一次调用内换到下一个账号提交成功；Worker 链路的任务绑定提交账号并轮询完成。两个任务实际各扣 7 Cloro 积分。本机池中现有 4 个账号（`default`、`account-b/c/d`）。真实的 403 积分不足、429、排队后积分不足重提和 2 小时上限尚未在真实环境触发。其他运行库仍须先执行 022：未执行时 Worker 不会领取 Cloro 任务（`CLORO_KEY_POOL_SCHEMA_NOT_MIGRATED`），管理接口返回 503。证据见 `.trellis/tasks/10-10-cloro-key-pool/research/live-acceptance-20261011.md`。需求与设计见 `.trellis/tasks/10-10-cloro-key-pool/`。

## 概念

- **一条记录 = 一个 Cloro 账号（组织）+ 它的唯一 key。** Cloro 的积分属于组织，同组织的所有 key 共享余额。把同一组织的两个 key 录成两个账号没有意义：一个耗尽时另一个也耗尽。系统无法通过 API 判断两个 key 是否属于同一组织，录入时请自行核对。
- 这里的“积分”是上游 Cloro 账号的积分，与平台向用户收取的积分（`credit_accounts`、监控预留）是两套账；本功能不改变用户侧扣费规则。
- key 按部署约定（同 010）明文存于 `cloro_api_keys.api_key`，另存 SHA-256 去重；所有接口、日志、事件只出现账号名称和 key 后 4 位。

## 部署与回滚

**发布顺序**：备份 → 执行 022 → 同版本发布 API 与 Worker → `GET /api/v1/providers/cloro/pool` 确认 `default` 存在、余额刷新成功 → 再录入其他账号。

022 只做加法，可重复执行：

- 新建 `cloro_api_keys`、`cloro_key_events`；`provider_tasks` 增加 `cloro_key_id`、`credit_resubmit_count`、`pool_exhausted_since` 和索引 `(cloro_key_id, state)`。
- 把单例 `cloro_provider_settings.api_key` 导入为 `default` 账号（优先级 100，状态 `unknown`），并把已提交、未终态的 Cloro 任务关联到它。
- 单例表的 `api_key` 改为可空并**保留原值**，供回滚使用；新版本不再读取它。`PUT /settings` 带 `apiKey` 时会同步写入该列。

**回滚**：先停用所有非 `default` 账号，等 `GET /keys` 中它们的 `inFlightTaskCount` 归零，再部署旧版本。旧版本只读单例 key，用其他账号提交的在途任务在旧版本中会轮询失败。022 无需回滚 SQL。

**行为变化**：`GET/PUT /api/v1/providers/cloro/settings` 和 `POST /test` 现在需要系统管理员（未登录 401、非管理员 403），与研究 Provider 接口一致。`tests/stage1_api_smoke.py` 的设置变更步骤（`STAGE1_ALLOW_SETTINGS_MUTATION=1`）已改为通过 `STAGE1_ADMIN_TOKEN` 使用测试管理员会话，并断言匿名调用返回 401；尚未对运行中的 API 实际执行。

## 配置（API 与 Worker 共用，节 `Cloro:KeyPool`）

| 配置 | 默认 | 说明 |
| --- | --- | --- |
| `LowBalanceThreshold` | 0 | 余额 ≤ 该值视为耗尽 |
| `ConcurrencyCooldown` / `RateLimitCooldown` / `QueueFullCooldown` | 30s / 5s / 60s | 429 三类错误的冷却 |
| `ForbiddenCooldown` | 10min | 非积分原因的 403 |
| `MaxSubmitAttemptsPerLease` | 3 | 一次租约最多尝试的账号数 |
| `MaxCreditResubmitsPerTask` | 2 | 排队后因积分不足失败的重提上限 |
| `PoolExhaustedRetryInterval` | 10min | 池耗尽时单次延后上限 |
| `PoolExhaustedMaxWait` | 2h | 超过后终态失败并释放用户积分预留 |
| `CreditRefreshInterval` | 10min | 余额定时刷新间隔 |
| `PoolLowBalanceWarning` | 100 | 池概览“余额偏低”阈值 |
| `CreditsUrl` | 空 | 余额接口；为空时由全局 `api_url` 的协议和主机推导为 `/v1/credits` |

例如 `.env` 中 `Cloro__KeyPool__PoolExhaustedMaxWait=01:00:00`。

## 运行规则

**选 key**：全局启用、key 启用、未归档、未在冷却期，状态为 `unknown`/`active`/冷却已过的 `cooling`（且已知余额高于阈值），或 `exhausted` 但周期重置时间已过（且晚于最近一次确认耗尽）。排序：优先级升序 → 周期重置时间升序（未知排最后）→ 最近使用时间升序。

**提交结果与切换**（只在 Cloro 明确拒绝、未创建任务时换账号）：

| 提交结果 | 账号处理 | 任务处理 |
| --- | --- | --- |
| 成功 | 记录最近使用 | 记录外部任务 ID 和 `cloro_key_id` |
| 403 `INSUFFICIENT_CREDITS` | `exhausted`，请求尽快刷新余额 | 同一租约换下一个账号 |
| 429 并发 / 限流 / 队列满 | 冷却 30s / 5s / 60s | 换账号 |
| 401 | `invalid`，Warning 日志 | 换账号 |
| 其他 403 | 冷却 10min | 换账号 |
| 超时、5xx、网络错误、不可解析 | 不变 | 保持 `CLORO_SUBMISSION_UNKNOWN`，**不换 key 重提** |
| 其他 4xx | 不变 | 保持 `CLORO_SUBMIT_FAILED` 终态 |

错误体只读取 `error.code` / `code` 并与白名单比对，读不到时按 HTTP 状态归类；Cloro 原始错误文本不入库、不进日志。一次租约尝试满 3 个账号仍被拒时任务记为可重试 `CLORO_KEY_SWITCH_LIMIT`，下次租约继续。

**轮询**始终使用提交时的 key，停用或归档后也一样；没有 `cloro_key_id` 的在途任务（022 之后、新版本发布之前由旧版本提交的任务）回退到 `default` 账号。这些任务计入 `default` 的 `inFlightTaskCount`：未结束前 `default` 不能归档、不能通过 `PUT /settings` 替换 key，也不能改名（409 `CLORO_KEY_IN_USE`）。轮询收到 401 时把该 key 标记为无效，任务以 `CLORO_KEY_INVALID` 终态失败。

**排队后因积分不足失败**：轮询到 `FAILED` 且能确认是积分原因（错误码为 `INSUFFICIENT_CREDITS`，或刷新后该账号余额 ≤ 阈值）时，标记账号耗尽，任务回到 `pending` 换账号重提，最多 2 次。旧外部任务 ID 写入 `cloro_key_events`（`resubmitted`）。Cloro 不对 FAILED 任务计费；用户侧监控积分已结算的不会重复扣，也不退还。无法确认原因的 FAILED 维持终态失败。

**池耗尽**：只有所有启用账号都积分不足、key 无效或因非积分 403 冷却时才算耗尽。未提交任务延后到最早恢复时间（冷却结束、周期重置、10 分钟三者取最早），错误码 `CLORO_KEY_POOL_EXHAUSTED`，用户积分保持预留；累计等待超过 2 小时后终态失败并释放预留。

**池繁忙**：没有可选账号，但至少一个账号只是因 429（并发 / 限流 / 队列满）短暂冷却且仍有积分时，错误码为 `CLORO_KEY_POOL_BUSY`：任务延后到冷却结束，不计入 2 小时上限，也不会终态失败；手动 Run 照常创建，计划时隙照常执行。池不可读（`CLORO_KEY_POOL_UNAVAILABLE`）同样只延后、不计时。手动创建 Cloro Run 返回 503 `CLORO_KEY_POOL_EXHAUSTED`（含 `error.nextAvailableAt`）；池中没有启用 key 时仍返回 `CLORO_NOT_CONFIGURED`。计划时隙在预留积分前跳过，`skip_code = CLORO_KEY_POOL_EXHAUSTED`。首次开通监控时直接创建的首轮 Run 不做此预检，由任务层延后与释放处理。`/health/ready` 不受积分影响。

**余额刷新**：Worker 的 `CloroKeyCreditsWorker` 每 15 秒检查一次，刷新到期（默认 10 分钟）或等待刷新的启用账号。余额 ≤ 阈值 → `exhausted`；已耗尽账号只有在余额增加（充值）、周期推进（重置）或此前余额未知时才恢复为 `active`，避免“余额为正但不够一次任务”的账号反复被拒；401 → `invalid`；其他失败只记录错误码，不改变可用性。

## 管理 API（仅系统管理员，前缀 `/api/v1/providers/cloro`）

| 方法与路径 | 说明 |
| --- | --- |
| `GET /keys?includeArchived=false` | 账号列表：状态、是否可用、余额、重置时间、冷却、最近使用、最近错误码、在途任务数 |
| `POST /keys` | `{accountLabel, apiKey, priority=100, enabled=true, skipValidation=false}` → 201；名称或 key 重复 409 `CLORO_KEY_DUPLICATE`；校验 401 → 400 `CLORO_KEY_INVALID`；Cloro 不可达 502 `CLORO_KEY_VALIDATION_UNAVAILABLE`（可用 `skipValidation=true` 以余额未知保存） |
| `PATCH /keys/{id}` | 只改 `accountLabel` / `priority` / `enabled`；带 `apiKey` 返回 400 `CLORO_KEY_IMMUTABLE`。重新启用 `invalid` 账号会重置为 `unknown` 并触发校验 |
| `DELETE /keys/{id}` | 软归档；有在途任务返回 409 `CLORO_KEY_IN_USE`（含 `inFlightTaskCount`） |
| `POST /keys/{id}/refresh-credits` | 同步刷新单个账号余额 |
| `POST /keys/refresh-credits` | 串行刷新全部启用账号 |
| `GET /pool` | 概览：总数、可用、耗尽、冷却、无效、已知余额、最早重置、最早恢复、状态 `healthy` / `low` / `exhausted` |

`GET /settings` 的 `configured` 表示“全局启用且至少有一个启用中的 key”；采集目标目录（`GET /api/v1/collection-targets`、预览可用性、每日监控开通）的 Cloro `available` 使用同一规则，不再读取单例 `api_key`。`PUT /settings` 的 `apiKey` 可选：`default` 不存在则新增，存在且无在途任务则替换，否则 409 `CLORO_KEY_IN_USE`。

## key 轮换

1. `POST /keys` 新增新 key（新账号名称）。
2. `PATCH` 旧账号 `enabled=false`，新任务不再使用它；已提交任务继续用它轮询。
3. 等 `GET /keys` 中旧账号 `inFlightTaskCount` 归零，`DELETE` 归档。

## 可观测

- `cloro_key_events` 记录提交、拒绝、余额刷新、状态变化、重提和管理操作，不含 key 与 Cloro 错误文本。本期不做自动清理。
- Warning 日志：账号被判定无效、池中无可用账号（每进程每分钟最多一次）；只含账号名称与 key 后 4 位。

## 待验收

- 在隔离 MySQL 测试库执行并重放 022，运行 `CloroKeyPoolSqlTests`（需 `JOGEOAI_STAGE1_TEST_CONNECTION`）。
- 真实的 403 `INSUFFICIENT_CREDITS`、429、排队后因积分不足失败的重提（R-04）及池耗尽 2 小时上限。本机 022 部署、401 明确拒绝后切换、Worker 账号绑定及费用已验收，不再列为待办。

## 真实环境观察（2026-10-11）

- Cloro 错误体结构为 `{"success":false,"error":{"code":"...","message":"..."}}`，适配器只读 `error.code`。格式错误的 key 返回 401 `INVALID_API_KEY_FORMAT`。
- 本机 4 个账号的 `GET /v1/credits` 都只返回 `remaining`，`perCycle` 与 `cycleResetsAt` 为 `null`。这类账号不参与“先用最早重置”排序；优先级相同时按最近使用时间轮换。账号耗尽后，只有余额刷新看到余额增加才会恢复。
- Cloro 余额在任务完成后约 1 分钟才更新。
- 无效 key 每 10 分钟复查一次；重新启用无效 key 会立即复查。
