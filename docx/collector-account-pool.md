# 采集器账号池（Collector session profiles）

> 现状（2026-10-30）：后端代码已实现，迁移为 [035](../db/migrations/035_collector_session_profiles.sql)。
>
> 已验证：
> - 隔离库 `jogeoai_collector_pool_test` 依次执行 001–035，035 重放无副作用（该隔离库已于 2026-10-30 验证结束后删除，复跑 SQL 测试需按 `CollectorSessionPoolSqlTests` 说明重建）；
> - 账号池 22 个定向测试通过（2026-10-30 按新白名单重建约束后复跑，仍为 22/22）；
> - 在 5281 端口启动临时 API（连隔离库），实际响应用参考仓库 `Ai-visiblility-collector@4baf27b` 的 zod schema 校验，13 项全部通过；
> - 2026-10-30 10:06 备份共享开发库 `jogeoai`（`/tmp/jogeoai-pre035-20261030-100616.sql`）后用 root 执行 035：三张新表、新增列、索引、尝试记录失败码约束和两个只追加触发器均已存在，`collector_platforms` 中 doubao/cn 为 `enabled=1`，账号、平台状态、健康事件均为 0 行；
> - 本机 5260 的 API 已是包含账号池的构建（2026-10-30 07:56 启动），迁移后 `/health/live`、`/health/ready`、`/internal/collector/health` 均为 200。
>
> - 2026-10-30 11:50 冒烟租户已就绪：专用租户 `16a7e7fd-b225-457d-9632-46f072ac0532`（站点“柯丽尔” `b467d93a-17c1-46b3-a732-ca366a0dfb8c`，4 条启用提问）已加入 `Collector__ChinaTenantIds__2` 并重启 5260；站点开启 `domestic_collector/doubao/consumer_web`；平台参数 180 秒 / 每日 10；登记 `doubao-a` `6575f8d0-a040-4775-986a-37587410768a`、`doubao-b` `7356243e-7576-4dce-9701-0d1e441dd926`（每小时 2、每日 6、间隔 900 秒），基线均为 `active`、计数 0、`pausedAt` 为空，健康事件 3 条（2 条登记、1 条平台设置）。临时管理员会话已删除。
> - 2026-10-30 12:43 Collector 侧准备完成：`aivc/pool/` 下两个独立 profile/spool（0700）、0600 的 env / approved 问题 / 页面合同 / 账号池文件；两个账号人工登录后带账号身份 probe 均 ready，Jogeoai 各有 1 条 `verified` 事件、状态 `active`。注意：本机系统语言为繁体，新建 profile 的豆包界面默认繁体会导致 probe 返回 `page_contract_changed`，须在该 profile 的 `chrome://settings/languages` 把简体中文移到最前；doubao-a 登录的是优联单账号采集同一个豆包账号。
> - 2026-10-30 13:18–14:23 有人值守两账号冒烟完成：Run `185c49d4-3c8e-4af9-9dc6-8410fb5b5969`（4 个豆包任务，approved 文件与任务问题快照逐条一致，Clash 规则 `DomainSuffix doubao.com → DIRECT`）全部 `succeeded`，Run `succeeded` 4/4。doubao-a 采 1 条、doubao-b 采 3 条；4 条结果的 `visibility_checks.session_profile_id` 均等于租约账号，回答 588–1097 字，引用 21–39 条且全部带标题，`query_availability=available`，出口 CN。
> - 冒烟中验证的行为：平台最小间隔（a 13:18:31 领取后 b 13:21:35 才领取）、账号最小间隔（b 两次领取相隔 15.5 分钟）、每小时配额 2（两账号均等到窗口滑出才再领取）；doubao-a 发送后出现验证码，10 分钟等待超时上报 `challenge_required`（`needs_verification`，任务退避 30 分钟后由 a 第 2 次租约成功）；probe 恢复后 a 又在发送前检测到 `login_required`（`expired`，问题未发出，任务退避后由 b 成功）；人工重新登录并 probe 后恢复 `active`。只有 1 个账号触发验证码，熔断未触发。失败尝试共 2 次，均未到任务失败上限 3。
> - 冒烟暴露的 Collector 问题：登录脚本 Ctrl+C 退出、账号池转发信号停止子进程（退出码 130）后，profile 与 spool 的 `.collector.lock` 均会残留，下次启动报 `profile_unavailable`。根因是 Playwright 默认 `handleSIGINT` 在关闭浏览器后直接 `exit(130)`，跳过 collector 的 `finally` 解锁；2026-10-30 已在 Collector 仓库 `session-profile.ts` 关闭 Playwright 的 SIGINT/SIGTERM 处理并补单测（未提交，真实 Chromium 复现修复后退出码 0、锁已释放）。修复部署前，残留锁仍需确认 PID 已退出后改名为 `.collector.lock.archived-<pid>-<date>`；doubao-b 有一次常驻探测返回 `page_contract_changed` 后退出码 1 自动重启，未消耗租约、无失败事件。doubao-a 使用的是优联日常账号，验证码超时后会话很快失效，建议换成专用账号。
>
> 尚未完成：生产/其他运行库未部署 035；冒烟样本的分析结果尚未核对；Collector 锁残留修复尚未提交。
>
> 035 首次执行被前置检查拦下：2026-10-28 人工归档 10-27 豆包任务时追加了一条 `failed` 尝试记录，`error_code` 为空。处理方式是在尝试记录的失败码白名单中加入 `operator_archived`（仅用于运维人工归档，Collector 不上报，健康事件白名单不含它），并把这一行补为 `operator_archived`，与任务的 `last_error_code` 一致。
>
> 需求与设计见 `.trellis/tasks/archive/2026-10/10-28-collector-account-pool/`（已归档）。

## 边界

- **控制面决定哪个账号干活。** 每次领取时，Jogeoai 检查账号状态、冷却、每小时和每日配额、账号最小间隔，在在线账号中优先最久未用者，再检查平台闸门（熔断、平台最小间隔、北京时间每日总上限）和任务退避。Collector 和账号池守护进程不能自己授予任务。
- **只保存非敏感元数据。** 包括别名、状态、配额、时间戳。Cookie、密码、代理凭据、账号名不进入数据库、日志或文档。
- **不做的事。** 不识别或外包验证码，不改浏览器指纹，不做拟人输入，不按账号配置代理。验证码只能由操作员在该账号的 profile 窗口里手动处理。
- **样本的账号归属以租约为准。** 写入 `visibility_checks.session_profile_id` 和 accepted 尝试记录的账号 ID，取自 `collector_tasks.lease_session_profile_id`，不读请求体。

## 兼容性（单账号流程）

- 租户在某区域、某平台**没有登记任何账号**时，不带 `sessionProfileId` 的领取与以前一致。唯一可见的差别是返回的任务多一个 `sessionProfileId: null` 字段，参考 Collector 的 schema 允许它为 null 或缺省。
- 一旦登记了账号（**包括已停用的账号**），该租户在该平台不带账号 ID 的领取就返回空列表。这样做是为了防止账号池平台悄悄回落到不受配额约束的派发。
- 结论：现有的单账号 doubao collector 与账号池**不能在同一租户、同一平台上共存**。

## Collector 接口（`/internal/collector/*`，鉴权与现有接口相同）

| 接口 | 请求 | 返回 |
| --- | --- | --- |
| `POST leases` | 现有字段，加可选的 `sessionProfileId` | 每个任务加 `sessionProfileId`（无账号时为 `null`）。带账号时 capacity 强制为 1；任何"现在不行"的情况都返回 `{"tasks":[]}` |
| `POST eligibility` | `tenantId, nodeId, region, sessionProfileId, platformCode` | `eligible`、`retryAfterSeconds`（30–120），以及可选的 `stopReason`（`needs_verification` / `expired` / `disabled`，没有值时省略该字段） |
| `POST failures` | `tenantId, nodeId, region, sessionProfileId, failureCode, phase, task?` | `sessionProfileStatus`、`taskDisposition`（`requeued` / `failed` / `stale` / `null`）、`platformPaused` |
| `POST session-events` | `tenantId, nodeId, region, sessionProfileId, eventKind, phase, task?` | `sessionProfileStatus` |

**请求约束：**
- probe 阶段的失败不带 `task`；pre_send 和 post_send 阶段的失败必须带 `task`。
- `verified` 事件只能在 probe 阶段上报，且不带 `task`；验证码相关事件必须带 `task`。

**错误码：**
- 账号不存在、跨租户、跨区域或平台不符：`403 SESSION_PROFILE_FORBIDDEN`；
- 上报的任务不存在：`404 TASK_NOT_FOUND`；
- 请求不合法：400，错误码为 `INVALID_SESSION_PROFILE`、`INVALID_PLATFORM`、`INVALID_FAILURE`、`INVALID_SESSION_EVENT` 或 `INVALID_REQUEST` 之一。

**eligibility 的行为：**
- eligibility 只读，唯一的写入是 `last_seen_at`，用来让账号保持"在线"、参与最久未用者优先的公平比较。
- 它与领取共用同一套判定，但不产生租约，也不锁任务行。
- 返回 eligible 之后，领取仍可能因为竞争返回空。

**失败的影响：**

| 失败码 | 账号 | 任务（租约仍有效时） |
| --- | --- | --- |
| `challenge_required` | 变为 `needs_verification`，连续验证码次数加 1 | 变为 `retryable_failed`，`next_attempt_at` 设为当前时间加退避时间（默认 30 分钟） |
| `rate_limited` | 变为 `cooling`，冷却从 30 分钟起按连续次数翻倍，最长 8 小时。已处于需要人工处理状态的账号不改变状态 | 同上 |
| `login_required` | 变为 `expired` | 同上 |
| 其他失败码 | 不变 | 同上 |
| 任意失败码，第 3 次 | 按以上规则 | 变为 `terminal_failed` |

补充规则：
- 上报的租约已经过期或被他人接手时，只记录账号的影响，`taskDisposition` 为 `stale`，任务不变。
- probe 阶段的失败会记录 `last_probe_failed_at`。之后 15 分钟内，如果该账号没有再领取过任务，它不再作为其他账号的公平竞争者。
- **熔断：** 同一平台在 60 分钟内（从最近一次解除熔断算起）有 2 个以上不同账号上报 `challenge_required`，该平台暂停派发。熔断只能人工解除。

## 运维接口（系统管理员，`api/v1/admin/collector-pool`）

未登录返回 401，非系统管理员返回 403，035 未执行时返回 503 `COLLECTOR_POOL_MIGRATION_REQUIRED`。每个写操作都追加一条健康事件，并记录执行操作的管理员 `operator_user_id`。

本机调用方式：为系统管理员向 `auth_sessions` 插入一条 2 小时有效的临时会话（存随机 token 的 SHA-256），用 Bearer 调用，验收后删除这条会话。做法同 [订阅验收记录](subscription-plans.md)。

```bash
TOKEN=<临时会话 token>; TENANT=<租户 UUID>; API=http://127.0.0.1:5260/api/v1/admin/collector-pool
```

```bash
curl -sS -X POST "$API/session-profiles" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "{\"tenantId\":\"$TENANT\",\"platformCode\":\"doubao\",\"region\":\"cn\",\"label\":\"doubao-a\"}"
```

```bash
curl -sS "$API/session-profiles?tenantId=$TENANT&platformCode=doubao" -H "Authorization: Bearer $TOKEN"
```

```bash
curl -sS -X PUT "$API/session-profiles/<id>/status" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "{\"tenantId\":\"$TENANT\",\"status\":\"disabled\"}"
```

```bash
curl -sS -X PUT "$API/platform-dispatch-states" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "{\"tenantId\":\"$TENANT\",\"region\":\"cn\",\"platformCode\":\"doubao\",\"minIntervalSeconds\":180,\"dailyCap\":60}"
```

```bash
curl -sS -X POST "$API/platform-dispatch-states/resume" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "{\"tenantId\":\"$TENANT\",\"region\":\"cn\",\"platformCode\":\"doubao\"}"
```

**登记账号：**
- `label` 是运维别名，长度 1–128，同平台唯一，例如 `doubao-a`；不得填写手机号、邮箱或账号名。
- 返回的 `id` 就是 Collector 的 `COLLECTOR_SESSION_PROFILE_ID`。
- 可选参数 `hourlyQuota` / `dailyQuota` / `minIntervalSeconds` 的默认值为 4 / 30 / 600。

**启停账号：** 状态只接受 `active` 和 `disabled`。设为 `active` 时会同时清除冷却和连续验证码次数，所以应先确认账号健康，优先用 Collector 的 `probe` 上报 `verified` 来恢复。

**设置平台参数：** `PUT platform-dispatch-states` 中未传的字段保持原值。可设字段如下：

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| `minIntervalSeconds` | 120 | 平台最小派发间隔（秒） |
| `dailyCap` | 120 | 平台每日总上限（北京时间自然日） |
| `breakerProfileThreshold` | 2 | 熔断所需的不同账号数 |
| `breakerWindowMinutes` | 60 | 熔断统计窗口（分钟） |
| `retryBackoffSeconds` | 1800 | 失败任务重新入队的退避（秒） |
| `maxTaskFailures` | 3 | 任务失败上限 |
| `rateLimitCooldownBaseSeconds` | 1800 | 限流冷却起始值（秒） |
| `rateLimitCooldownMaxSeconds` | 28800 | 限流冷却上限（秒） |
| `probeFailureOfflineMinutes` | 15 | probe 失败后退出公平队列的时长（分钟，取值 1–1440） |

`GET platform-dispatch-states?tenantId=` 可以查看每个平台当前的闸门状态。

**按账号查看指标：**
- 返回字段：`status`、`cooldownUntil`、配额、`consecutiveChallenges`、`lastLeasedAt`、`lastSeenAt`、`leasedLastHour`、`leasedLast24h`、`acceptedLast24h`、`challengesLast24h`、`challengeRateLast24h`、`lastChallengeAt`、`lastProbeFailedAt`、`excludedFromFairness`。
- 所有计数都来自尝试记录和健康事件，不使用可变计数器。
- 一次等待超时只算一次验证码。
- 没有领取记录时，触发率为 `null`，不是 0。

以上默认值只是起始值，不是已知的平台阈值，应根据指标中的触发率和成功数调整。

## 部署与回滚

**发布顺序：** 备份目标库 → 用 **root** 执行 035 → 发布同版本的 API → 用运维接口确认工作正常，再登记账号。

035 只做加法，可以重复执行。执行方式如下：

```bash
set -a; . ./.env; set +a
docker compose exec -T -e MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql mysql -uroot --default-character-set=utf8mb4 "$MYSQL_DATABASE" < db/migrations/035_collector_session_profiles.sql
```

- **必须用 root 执行。** 本机开启了 binlog，且 `log_bin_trust_function_creators=0`，`jogeoai_app` 创建只追加触发器时会报 1419。此时触发器之前的表和列已经建好，改用 root 重放即可。
- **035 未执行时：** 新版 API 的 Collector 领取和结果接口会因为缺列返回 500，运维接口返回 503。
- **Worker 不依赖 035。** Worker 的过期租约回收逻辑没有变化。
- **回滚：** 回退 API 即可，旧代码会忽略新增的表和列。健康事件表拒绝 UPDATE 和 DELETE，即使是 root 也不行；它是审计证据，确认不再需要时只能整表 DROP，DROP 之前先导出。

**本次附带的修复：** 提交结果和上报失败时会更新运行汇总，原来的 SQL 会扫描全部 `collector_tasks`，导致不同运行、甚至不同租户的事务互相死锁（账号池测试并发时复现过）。现在只统计当前任务所属运行的任务，计算结果不变。同一运行内两个任务并发提交的死锁风险是原有问题，这次没有改。

## 有人值守的两账号豆包冒烟准备清单

> 本节只是准备清单，没有实际执行。冒烟时需要人工处理验证码和登录；遇到验证码时，Collector 原地等待，最多 10 分钟，并通知操作员。执行前请逐项确认，每一步都需要你明确同意。

### A. 控制面（Jogeoai）

1. **备份并迁移**
   - 备份 `jogeoai`。
   - 用 root 执行 035。
   - 用 `SHOW TRIGGERS LIKE 'collector_session_health_events'` 确认两个触发器已存在。
   - 确认 `collector_platforms` 里 doubao 为 `enabled=1`。
2. **发布新版 API 到 5260**
   - 当前的 5260 进程是旧版本，需要你同意后再重启。
   - 重启后检查 `/health/live`、`/health/ready` 和 `/internal/collector/health`。
   - Worker 不需要随之重启，但建议使用同一版本。
3. **选择冒烟租户**
   - 使用独立租户或专用项目。一旦登记账号，该租户 doubao 的单账号 collector 就会停止派发。
   - 确认 `Collector:ChinaTenantIds` 包含该租户（数组配置使用 `__0`、`__1` 这样的键）。
   - 确认 `Collector:ChinaServiceToken` 已配置。
4. **登记账号**
   - 登记 `doubao-a`、`doubao-b` 两个账号，记下返回的两个 `id`。
   - 冒烟期间建议用保守参数：每个账号 `hourlyQuota=2`、`minIntervalSeconds=900`；平台 `minIntervalSeconds=180`、`dailyCap=10`；熔断参数保持默认（2 个账号 / 60 分钟）。
5. **准备任务**
   - 准备 4–6 条 Prompt，必须是该项目已审批的问题，与 Collector 的 approved question file 对应。
   - 用 `POST /api/v1/visibility-runs` 创建 doubao 运行（`platforms`，`sampleCount=1`），记下 Run ID。
     - Development 环境（本机，带 `X-Tenant-Id`）照旧可以直接发起。非 Development 环境只有系统管理员能用 `platforms` 形态，其他人返回 403 `ADMIN_REQUIRED`（Trellis `11-27-sec-manual-run-charging`；本机 5260 已于 2026-11-27 用该构建重启，本机是 Development，这条限制不生效，403 没有运行态验证；其他环境未部署）。
     - 每次最多 200 个 Prompt、20 个平台入口，超出返回 400 `INVALID_RUN`。这条通道不扣积分。
     - 运行的 `requested_by` 由服务端写入（Development 兼容桥为 `dev-bridge`，登录用户为 `user:{userId}`），请求里的 `requestedBy` 不再存储；按 `requested_by` 查找自己发起的运行的脚本需要改用 Run ID 或幂等键。
6. **记录基线**：`GET session-profiles` 两个账号都为 `active`、计数为 0；`GET platform-dispatch-states` 中 `pausedAt` 为 null。

### B. Collector（`Ai-visiblility-collector`，按其 `docs/runbooks/account-pool.md`）

1. 确认分支或版本包含 `4baf27b`，即账号池、验证码等待和常驻子进程功能。
2. **每个账号使用独立的持久化 profile 目录**，例如 `…/aivc/pool/doubao-a`、`…/aivc/pool/doubao-b`，以及**各自独立的空 spool 目录**。不要在同一个浏览器 profile 里登出再登录来切换账号。
3. 对每个账号运行一次 `pnpm collector:login`，由人工登录豆包后关闭。
4. 对每个账号运行一次带账号身份的 `pnpm collector:probe`：
   - 设置 `COLLECTOR_SESSION_PROFILE_ID=<该账号的 id>`、`COLLECTOR_API_URL=http://127.0.0.1:5260`、区域 token、`COLLECTOR_TENANT_ID`、`COLLECTOR_NODE_ID`、`COLLECTOR_REGION=cn`。
   - 结果为 ready 时会上报 `verified`；在 Jogeoai 中确认产生了 `verified` 健康事件。
5. 准备 0600 权限的 env 文件，内容包括：
   - API URL、token、租户、expected project（即 Jogeoai 的 SiteId）、egress、page contract、approved question file、spool key；
   - `COLLECTOR_BROWSER_HEADLESS=false`、`COLLECTOR_CHALLENGE_WAIT_MS=600000`；
   - 可选的 `COLLECTOR_NOTIFY_COMMAND`。
6. 准备账号池文件 `COLLECTOR_POOL_FILE`（绝对路径），包含两个条目：`sessionProfileId`、`platform: doubao`、`label`、`profileDirectory`、`spoolDirectory`。设置 `COLLECTOR_POOL_STAGGER_MS=15000`。
7. 用 `dig` 确认网络出口正常：Clash 的 TUN fake-ip 模式会导致 SSRF 防护拒绝，或网关返回 403。

### C. 冒烟中要观察的现象

1. 运行 `pnpm collector:pool`：两个子进程间隔 15 秒启动，先调用 eligibility，可以派发时才加载页面。
2. 两个账号**轮流**领取任务，按最久未用者优先；领取间隔受平台 180 秒和账号 900 秒共同约束；`leasedLastHour` 不超过 2。
3. 每个被接受的样本，`visibility_checks.session_profile_id` 与租约账号一致。
4. 出现验证码时：
   - 先确认通知里只有标识和枚举值，**不含问题文本**；
   - 只在**提示等待验证码的那个账号窗口**里手动处理，不要在采集标签页里输入任何内容；
   - 处理后确认采集继续，并产生 `challenge_cleared` 事件。
5. 如果等待超时：该账号变为 `needs_verification`，子进程以退出码 3 结束，任务退避 30 分钟后重新入队。人工处理后运行 probe 上报 `verified`，再重启账号池。
6. 如果两个账号都触发验证码，平台熔断：用 `GET platform-dispatch-states` 确认 `pausedAt` 已设置，核查后再调用 `resume`。

### D. 收尾

1. 记录以下证据：Run ID、两个账号的指标、健康事件计数、accepted 数和失败码分布。证据里不写问题文本或页面内容。
2. 冒烟后该租户的 doubao 只能走账号池：停用账号不能恢复无账号派发；账号行被只追加的健康事件引用，也无法删除。因此冒烟必须使用不承担日常单账号采集的租户。
3. 删除临时会话行；按 AGENTS.md 更新 README 中的现状说明。
