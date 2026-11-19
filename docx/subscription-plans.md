# 套餐与免费体验版（订阅、额度周期、上限校验、卡密）

> 状态更新（2026-10-25）：024 的本机部署和真实验收已于 2026-10-19/20 完成，历史证据见下文。本次 031 已经用户授权、备份后执行到本机 `jogeoai`，pro 从 5/20 升为 **20/20**，随后执行 032 并重启 5260 API 与完整模式 Worker。停写前后仅 pro 品牌额度变化，其他权益、余额与订阅周期不变；套餐卡片上限与已用分开，隔离 SQL 边界/并发仍未验。原需求见 `.trellis/tasks/10-19-subscription-plans/`，本轮证据见 [20/20 集成验证](../.trellis/tasks/10-25-pro-plan-brand-onboarding/verification.md) 和 [本机部署记录](../.trellis/tasks/10-25-pro-plan-brand-onboarding/local-deployment.md)。

## 概念

- **每个租户任意时刻有且只有一个生效订阅**：`tenant_subscriptions` 一租户一行，变更原地更新，历史靠 `subscription_events`。
- **套餐目录在数据库**：`subscription_plans` 种子 5 行（`free_trial` / `starter` / `basic` / `standard` / `pro`），编码固定；系统管理员只能改上限、额度、文案、功能标记、推荐与启用状态。注册赠送金额读 `free_trial.signup_credits`（种子 500），不再硬编码。
- **积分账户不变**：仍是 `credit_accounts` / `credit_ledger`；本功能只新增 `expire` 账本类型，不改积分单价、预占 / 结算时点与消费入口。
- 「日级监控频次」「AI 引擎数量」「GEO Agent」只作为套餐属性返回，不做后端拦截。

## HTTP 合同（前缀 `/api/v1`，会话 Cookie + `X-Tenant-Id`）

| 接口 | 权限 | 行为 |
| --- | --- | --- |
| `GET /plans` | 已登录任意角色 | 套餐目录：排序、上限、月度额度、一次性赠送、功能标记、推荐标记、`enabled`、`isCurrent` |
| `GET /subscription` | 已登录任意角色 | 当前订阅：套餐、来源、生效 / 到期、周期（序号、起止、额度、已用、可用、预占）、各上限与当前用量（品牌 / 成员 / 提问 / 每品牌竞品） |
| `POST /subscription/redeem` | owner / admin（member、viewer 403 `CONTENT_FORBIDDEN`） | `{code}`；同套餐顺延、更高套餐立即切换并发放当月额度、更低套餐拒绝；返回更新后的订阅 |
| `POST /admin/plan-codes/batches` | 系统管理员 | `{planCode, months(1–36), quantity(1–500), expiresAt?, note?}`；201，响应一次性返回明文 `codes[]` |
| `GET /admin/plan-codes` | 系统管理员 | `batchId` / `status`（unused / redeemed / revoked / expired）/ `planCode` / `cursor` / `limit` 分页，只返回末 4 位 |
| `POST /admin/plan-codes/{codeId}/revoke` | 系统管理员 | 作废未使用卡密；已兑换 409 `PLAN_CODE_USED`；已作废幂等 |
| `GET /admin/tenants/{tenantId}/subscription` | 系统管理员 | 订阅详情 + 最近 50 条事件 |
| `POST /admin/tenants/{tenantId}/subscription` | 系统管理员 | `{planCode, months? \| expiresAt?, reason}`；允许降级；目标为 `free_trial` 时 months / expiresAt 都省略 |
| `PUT /admin/plans/{code}` | 系统管理员 | 修改名称、文案、推荐、启用、`limits`（整体替换，项为 null = 不限）、一次性赠送、月度额度、展示属性、`features` |

错误码：`PLAN_LIMIT_EXCEEDED`（409，`details: {planCode, limit, limitValue, currentValue}`，`limit` ∈ brands / members / prompts / competitors）、`PLAN_CODE_INVALID`（404）、`PLAN_CODE_USED` / `PLAN_CODE_REVOKED` / `PLAN_CODE_EXPIRED`（409）、`PLAN_DOWNGRADE_NOT_ALLOWED`（409）、`PLAN_NOT_FOUND`（404，含停用套餐）、`PLAN_REDEEM_RATE_LIMITED`（429）、`SUBSCRIPTION_NOT_FOUND`（404，租户无订阅行）、`SUBSCRIPTION_SCHEMA_NOT_MIGRATED`（503，024 未执行）、`ADMIN_REQUIRED`（403）、`AUTHENTICATION_REQUIRED`（401）、`CONTENT_FORBIDDEN`（403）。`PLAN_LIMIT_EXCEEDED` 的 `details` 由 `ContentControllerBase.Execute`、`ManagementController`、`OnboardingController` 和管理员控制器统一输出（`src/Jogeoai.Api/StoreErrorResponse.cs`）。

### 配套合同变更：`GET /auth/me` 增加 `isSystemAdmin`

2026-10-19 为前端判断是否显示「兑换码管理」等管理员入口，`AuthUserView` 新增布尔字段 `isSystemAdmin`（读取 `auth_users.is_system_admin`，缺列时按 false）。登录、注册与会话校验响应同步携带；注册的新用户恒为 false。已重建并重启 5260；`AuthFoundationTests`、`SubscriptionApiTests`、`CloroKeysApiTests` 33 项通过 / 5 项跳过（隔离库）。前端类型已重新生成。

## 规则（父任务决定 D1–D6）

| 编号 | 规则 | 实现位置 |
| --- | --- | --- |
| D1 | 免费体验版上限 1 品牌 / 1 成员 / 20 提问 / 2 竞品，无周期，一次性 500 积分；可由 `PUT /admin/plans/free_trial` 调整 | 024 种子；`SubscriptionStore.UpdatePlanAsync` |
| D2 | 到期降级不删除超额资源，只阻止新增 | `DowngradeExpiredAsync` 只改订阅行；守卫只在新增 / 启用时计数 |
| D3 | 月度额度不结转：周期结束先把**可用**积分清零（`expire`，预占不动），再发放新额度（`grant`），同一事务 | `RolloverOneAsync` → `ExpireAvailableAsync` + `GrantAsync` |
| D4 | 跨套餐升级：新到期 = 现在 + 月数，原剩余时间不折算；同套餐续期顺延 | `ApplyPlanChangeAsync` |
| D5 | 周期锚点 = 开通日，1 自然月；`period_ends_at = AddMonthsClamped(started_at, period_index)`，无累积漂移，目标月无对应日期取月末 | `SubscriptionStore.AddMonthsClamped` |
| D6 | 计数口径：品牌 = `sites.status='active'`；成员 = `tenant_members.status IN ('active','invited')`；提问 = `prompts.enabled=TRUE AND archived_at IS NULL` 且品牌 active；竞品 = 品牌下 `competitors.status='active'` | `SubscriptionLimitGuard` |

其他规则：

- 升级不清零剩余可用积分，只立即发放新套餐额度并开启新周期（FR-10）；管理员通过 `POST /admin/tenants/{id}/credits/grants` 入账的额外积分视为本周期额度，周期结束同样清零（FR-7）。
- 付费套餐已到期但 Worker 尚未降级时，`GET /subscription`、守卫与兑换都按免费体验版处理。
- 账本引用固定：注册 `registration-{userId:N}`（不变）、开通 `plan-activate:{subscriptionId:N}:{eventId:N}`、周期发放 `plan-period:{subscriptionId:N}:{startedAt:yyyyMMddHHmmssffffff}:{periodIndex}`、清零 `plan-expire:{subscriptionId:N}:{startedAt:yyyyMMddHHmmssffffff}:{periodIndex}`；写入前先查引用是否存在并用 `INSERT IGNORE`，重复执行不重复入账。引用带开通时间 `started_at`，因为每次升级都会把 `period_index` 重置为 1，同一租户第二次开通时的周期引用不能与上一轮相同（否则会被幂等检查跳过而不发放）。
- 管理员把已到期但尚未被 Worker 降级的付费行指派为 `free_trial` 时，会把 `plan_code` 真正落到 `free_trial` 并清空周期，而不是只清空 `expires_at`。
- 卡密格式 `JG-XXXX-XXXX-XXXX-XXXX`（32 字符 base32，去除 I / O / 0 / 1，80 bit `RandomNumberGenerator`）；只存 SHA-256 与末 4 位，明文只在批次响应中出现一次；输入大小写、空白、连字符不敏感，`JG` 前缀可省略；日志不记录卡密。
- 兑换失败限流：进程内滑动窗口，按租户与客户端 IP 各计数，只计 `PLAN_CODE_INVALID / USED / REVOKED / EXPIRED` 失败，默认每小时 10 次（`Subscription:RedeemFailuresPerHour`）。多实例部署时各实例独立计数。
- 成员上限：当前没有添加 / 邀请成员的接口，守卫 `SubscriptionLimitGuard.EnsureMembersAsync` 已实现并由隔离库测试覆盖，供后续团队模块复用；不新增成员 HTTP 接口。
- 2026-10-20 团队管理模块：新增 `GET /credits/usage`（按团队时区自然日 × 操作类型聚合 capture，金额从预占 / 监控计费项关联取得，viewer 403、member 只看本人）与 `GET /team/summary`；`CreditLedgerEntryType` 补上 `expire`（此前 024 写入的清零行在流水接口中被映射为 `reversal`）。团队管理仍不新增成员接口。

## 数据模型（024，加法、可重放）

2026-10-25 增量：[031_pro_plan_brand_member_limits.sql](../db/migrations/031_pro_plan_brand_member_limits.sql) 仅更新 `pro.max_brands=20,max_members=20`，不覆盖其他套餐或权益、不重置余额/周期。已有专业版订阅读取目录新上限，无需重新兑换。部署前记录管理员自定义原值；若原额度更高或无限，需明确确认统一为 20。卡片始终读取 `plan.limits`，当前已用另列，不将未知状态冒充 0；对比表同目录。新增第20/21品牌、并发最后名额、成员口径及迁移重放测试，但本轮隔离库连接缺失，SQL未验。无官网与创建幂等见 [品牌创建说明](brand-creation.md)。

- `subscription_plans`：目录 + 5 行种子（`INSERT IGNORE`，重放不覆盖管理员修改）。
- `tenant_subscriptions`：`tenant_id` 唯一；`period_index`（0 = 无周期）、`period_started_at / period_ends_at / period_quota / period_captured_baseline`（周期开始时的 `lifetime_captured`，已用 = 当前 `lifetime_captured − baseline`，因为 `capture` 账本金额为 0）。
- `subscription_events`：registration / migration / redeem / admin_assign / expire_downgrade / period_rollover（`plan_updated` 预留，目录级修改没有租户上下文，目前不写事件）。
- `plan_code_batches`、`plan_codes`（`code_sha256` 唯一，`code_hint` 末 4 位；「已过期」为派生状态）。
- `credit_ledger.ck_credit_ledger_type`：临时存储过程按 `information_schema.check_constraints` 判断，缺少 `expire` 时 `DROP CHECK` 后重建。
- 回填：所有没有订阅行的既有租户插入 `free_trial`（`source='migration'`，`started_at = tenants.created_at`）并写一条 `migration` 事件；**不写账本、不动 `credit_accounts`**。

## 上限校验落点与锁顺序

| 入口 | 方法 | 守卫 |
| --- | --- | --- |
| 创建品牌 | `ManagementStore.CreateBrandAsync` 新建分支 | Brands +1（创建即归档不计） |
| 品牌归档 → 启用 | `UpdateBrandAsync` | Brands +1 |
| 创建提问 | `CreatePromptAsync`（`enabled=true` 时） | Prompts +1 |
| 导入提问 | `ImportAsync` | Prompts +实际新建行数（批内去重与已有重复不计），一次性校验 |
| 启用提问 | `UpdatePromptAsync`（false → true） | Prompts +1 |
| 引导完成 | `CompleteOnboardingAsync`（保留原 1–5 / ≤10 校验） | Competitors +N、Prompts +N（启用的） |
| 创建竞品 | `CreateCompetitorAsync` | Competitors +1 |
| 竞品取消归档 | `UpdateCompetitorAsync` | Competitors +1 |
| 成员 | Store 级 `EnsureMembersAsync` | Members +1 |

校验与写入在同一事务内。品牌与提问相关入口先 `SELECT ... FROM tenants WHERE id=? FOR UPDATE`（`ManagementStore.LockTenant`）取得租户级串行化，再锁 `tenant_onboarding` / `sites` / `onboarding_progress`；`CreateBrandAsync` 原来先锁 `tenant_onboarding` 再锁 `tenants`，已调整为 `tenants` 在前，避免与引导完成互相等待。竞品按品牌计数，品牌行锁足够。订阅写路径的锁顺序为 `plan_codes` → `tenant_subscriptions` → `credit_accounts`，与积分预占 / 结算（`credit_reservations` → `credit_accounts`）兼容。上限为 NULL 直接放行；套餐表缺失（MySQL 1146）抛 503 `SUBSCRIPTION_SCHEMA_NOT_MIGRATED`，不静默放行。

## Worker

`SubscriptionWorker`（仅完整 Worker 注册，`ResearchOnly` / `MonitoringOnly` 不运行）每 `Subscription:RolloverInterval`（默认 60 s）一轮：

1. `RolloverDuePeriodsAsync`：`period_index > 0 AND period_ends_at <= now` 的订阅，逐个事务锁订阅行再锁账户行：清零可用积分 → 若 `expires_at <= now` 则降级（不发放），否则 `period_index + 1`、`period_started_at = 旧 period_ends_at`、`period_ends_at = AddMonthsClamped(started_at, period_index)`、`period_quota = 当前目录 monthly_credits`、`baseline = lifetime_captured`，发放并写 `period_rollover` 事件。长期停机时同一订阅循环追平（每周期一个事务；只有第一次有余额时写 `expire`）。
2. `DowngradeExpiredAsync`：`expires_at <= now AND plan_code <> 'free_trial'` 的订阅降为免费体验版（`source='expiry'`，清空周期，不动积分），写 `expire_downgrade` 事件。

异常后退避 1 分钟。配置节 `Subscription`：`RolloverInterval`（60 s）、`BatchSize`（100）、`RedeemFailuresPerHour`（10）。

## 部署顺序与回滚

1. 备份 → **先用 `mysql --default-character-set=utf8mb4` 执行 024**（容器内客户端默认 latin1，会把中文种子存成乱码）→ 再发布同版本 API 与 Worker。新构建依赖 024：未执行时注册返回 503 `AUTH_SCHEMA_NOT_MIGRATED`，套餐接口与品牌 / 提问 / 竞品的创建入口返回 503 `SUBSCRIPTION_SCHEMA_NOT_MIGRATED`（守卫不放行）。
2. **回填租户超出免费上限的提示**：既有租户统一回填为免费体验版。已有多个品牌、超过 20 条启用提问或超过 2 个竞品的租户，现有资源保留、可继续使用，但新增 / 启用会被 409 拒绝，必须由系统管理员通过 `POST /admin/tenants/{tenantId}/subscription` 指派合适套餐。部署后请按 `SELECT tenant_id, COUNT(*) FROM sites WHERE status='active' GROUP BY tenant_id HAVING COUNT(*) > 1` 之类的查询列出需要指派的租户。
3. 回滚：停用新构建即可；024 的表不影响旧代码，`expire` 账本类型只由新代码写入，无需回滚 SQL。

## 验证记录（2026-10-19，均在本机源码上执行）

```
dotnet build src/Jogeoai.Api/Jogeoai.Api.csproj          # 建置成功，0 警告 0 错误
dotnet build src/Jogeoai.Worker/Jogeoai.Worker.csproj    # 建置成功，0 警告 0 错误
dotnet build Jogeoai.slnx                                # 建置成功，0 警告 0 错误（原因：Domain 新模型、CollectorStoreException 取消 sealed、ContentControllerBase 错误映射为跨项目共享契约）
dotnet test tests/Jogeoai.Tests --filter 'FullyQualifiedName~Subscription' -v minimal
# 已通過! - 失敗: 0，通過: 39，略過: 5，總計: 44（略過 = SubscriptionStoreSqlTests 5 项，需 JOGEOAI_SUBSCRIPTION_TEST_CONNECTION）
dotnet test tests/Jogeoai.Tests --filter 'FullyQualifiedName~RegistrationOnboardingTests|FullyQualifiedName~CloroKeysApiTests' -v minimal
# 已通過! - 失敗: 0，通過: 17，略過: 5，總計: 22（略過 = RegistrationOnboardingTests 5 项，需 JOGEOAI_REGISTRATION_TEST_CONNECTION）

# 审查修正后（周期账本引用加入开通时间、到期行指派 free_trial 落 plan_code、兑换限定 owner / admin）复跑：
dotnet build Jogeoai.slnx                                # 建置成功，0 错误；2 个既有 CS1591 警告来自 QuestionsController / QuestionGroupsController，与本任务无关
dotnet test tests/Jogeoai.Tests --filter 'FullyQualifiedName~Subscription|FullyQualifiedName~RegistrationOnboardingTests|FullyQualifiedName~CloroKeysApiTests|FullyQualifiedName~ManagementStore' -v minimal
# 已通過! - 失敗: 0，通過: 56，略過: 10，總計: 66（略過 = SubscriptionStoreSqlTests 5 + RegistrationOnboardingTests 5，均因缺少隔离库连接串）
```

通过的 39 项覆盖：024 文本断言（表、种子 5 行、`expire` 守卫、回填不写账本）、`AddMonthsClamped`、卡密生成 / 规范化 / 哈希、失败限流、Worker 轮次与异常退避、管理员 6 个接口的 401 / 403 矩阵、viewer 兑换 403、`PLAN_LIMIT_EXCEEDED` 在 `SubscriptionController` / `ManagementController` / `OnboardingController` / 管理员控制器的 `details`、测试内临时宿主的 Swagger 中文摘要与字段描述（9 条路由、11 个 DTO 字段、3 个枚举）。

## 本机运行态验收（2026-10-19，用户授权后执行）

- 备份：`mysqldump --single-transaction --routines --triggers jogeoai` → `/tmp/jogeoai-pre024-20261019-225611.sql`（18.5 MB，89 张表定义，73 条 INSERT 段）。
- 迁移：本机 `jogeoai` 执行 024 两次均成功（第二次为重放验证）。执行后 5 张新表存在、种子 5 行、既有 1 个租户回填为 `free_trial/migration`、`subscription_events` 1 条 `migration`、`ck_credit_ledger_type` 含 `expire`；`credit_accounts` 保持 1 行 / 可用 9,500 / 预占 0 不变（AC-2）。
- **字符集问题与修复**：容器内 `mysql` 客户端默认 `character_set_client=latin1`，按 README 旧写法 `< 024.sql` 导入时，中文种子（套餐名称、文案、事件原因）以乱码存入 utf8mb4 列（API 返回 `å…è´¹ä½“éªŒç‰ˆ`）。已用 `--default-character-set=utf8mb4` 连接执行 `CONVERT(BINARY CONVERT(col USING latin1) USING utf8mb4)` 修复 `subscription_plans.name/tagline` 与 `subscription_events.reason`，API 现返回正确中文。**今后执行含中文字面量的迁移必须加 `--default-character-set=utf8mb4`**（已写入部署顺序与数据库规范）。
- 服务：停止旧 API / Worker 进程后以 `./scripts/run-local.sh api` / `worker` 重启为新构建；`/health/live`、`/health/ready` 均 200；Swagger 实查 9 条套餐 / 订阅 / 卡密路由带中文摘要。
- 会话：为避免索取密码，临时向 `auth_sessions` 插入系统管理员与验收租户各一条会话（UA `acceptance-024-*`，2 小时有效），验收后已删除（剩余 0 行）。验收租户 `验收024`（`20ae9302-3c55-4ee0-801a-d2dbfc4b137e`，用户 `acceptance-024@example.test`，密码哈希为占位串不可登录）为新建隔离数据，不触碰原租户。

| 验收项 | 实测结果 |
| --- | --- |
| AC-A3 `GET /plans` / `GET /subscription` | 5 个套餐按排序返回，`isCurrent` 标记免费体验版；订阅返回 `period=null`、`usage` 含品牌 / 成员 / 提问 / 竞品用量与 500 一次性额度 |
| AC-A4 品牌上限 | 第 1 个品牌 201；第 2 个品牌经 `POST /onboarding/brands` 与 `POST /sites` 均 409 `PLAN_LIMIT_EXCEEDED`，`details={planCode:free_trial,limit:brands,limitValue:1,currentValue:1}` |
| AC-A4 提问上限 | 已有 1 条启用时导入 20 条 → 409（`currentValue:1`）；导入 19 条 → 201 `createdCount:19`；第 21 条创建 → 409；停用提问创建 201 不计数；`PATCH enabled=true` → 409 |
| AC-A4 竞品上限 | 第 1、2 个竞品 201；第 3 个 409 `limit:competitors,limitValue:2` |
| AC-A5 管理员指派 | `POST /admin/tenants/{id}/subscription {basic, months:1}` → 套餐 basic、到期 +1 月、周期 1 额度 42,000、账本 `grant 42,000`（`plan-activate:…`）、事件 `admin_assign`；非管理员访问管理员路由 403 `ADMIN_REQUIRED` |
| AC-A7 卡密 | 生成 basic×2 / starter×1 / pro×1 批次，明文仅在响应，列表只回末 4 位；同套餐 basic 兑换 → 到期顺延 3 个月、不发放；重复兑换 → 409 `PLAN_CODE_USED`；starter → 409 `PLAN_DOWNGRADE_NOT_ALLOWED`；作废后兑换 → 409 `PLAN_CODE_REVOKED`；乱码 → 404 `PLAN_CODE_INVALID`；pro 兑换 → 立即切换、账本 `grant 1,250,000`、周期重置为 1 |
| AC-A6 周期滚动 | 把 `period_ends_at` 改到过去，Worker 40 秒内处理：账本 `expire −1,292,500`（balance 0）后 `grant 1,250,000`（`plan-period:…:{开通时间}:2`），`period_index=2`、`period_ends_at=2026-12-19`（以开通日为锚），预占不变，事件 `period_rollover` 记录清零与发放金额；Worker 日志 `rolled over 1 periods` |
| AC-A7 到期降级 | 把 `expires_at` 改到过去，Worker 40 秒内降为 `free_trial/expiry`、周期字段清空、可用积分 1,250,000 保留、事件 `expire_downgrade`；之后新增品牌仍 409，原品牌 `GET /sites/{id}` 200 |

未覆盖：成员上限（无 HTTP 入口）、viewer / member 兑换 403（仅 API 单测）、并发创建（仅隔离库 SQL 测试，未执行）。

遗留的临时对象（清理前需按 AGENTS.md 确认）：验收租户 `验收024` 及其品牌、21 条提问、2 个竞品、订阅 / 事件 / 账本行；`plan_code_batches` 4 个批次与 6 张卡密（2 张已兑换、2 张已作废、2 张未使用）；`/tmp/jogeoai-pre024-20261019-225611.sql` 备份、`/tmp/jogeoai-acc-*` 卡密明文与 ID 文件。

## 前端（子任务 B，2026-10-20）

`Jogeoai.web` 新增三页：`/subscription` 升级套餐页（四张付费套餐卡片、推荐标记、当前版本灰态、引擎行、四项上限、每月额度环形进度、功能清单、横向滚动、「查看全部套餐」对比表、底部说明）、`/subscription/redeem` 卡密兑换页（规范化输入、七种错误码文案、member / viewer 禁用、成功面板取兑换响应的可用积分）、`/admin/plan-codes` 兑换码管理页（仅 `isSystemAdmin`，生成批次后明文只在对话框内可复制 / 下载 CSV，列表筛选、游标分页、作废确认）。品牌 / 提问（创建、启用、批量）/ 引导完成收到 `PLAN_LIMIT_EXCEEDED` 时弹出「当前套餐 X 最多 N 个 Y（已有 M）」并附「升级套餐」跳转；顶部团队切换器的套餐标签绑定当前套餐。

验证：`npm run build` 通过；定向测试 7 个文件 99 项通过；fixture（`.claude/launch.json` 的 `jogeoai-web-fixture-subscription`，3007 / 5276）浏览器走查升级页、对照表、兑换页错误与成功、兑换码生成 / 列表 / 作废、限额提示均通过，走查中修复了生成表单对数字输入调用 `trim` 的 TypeError；对本机 5260 用临时管理员会话（已删除）联调升级页读取真实订阅、管理页列出已有 6 张卡密。

### 免费体验版「已用」基线（2026-10-20 修正）

联调暴露回填租户显示「1,000 / 500」：`signupUsed` 直接取 `lifetime_captured`，包含套餐上线前的历史消耗。现改为「自进入免费体验版以来」的消耗：024 回填时把 `period_captured_baseline` 记为当时的 `lifetime_captured`（并用幂等 `UPDATE` 修复早期按 0 回填的行），到期降级与指派为免费体验版时同样把基线置为当前 `lifetime_captured`，`GET /subscription` 返回 `max(0, lifetime_captured − baseline)`。本机 `jogeoai` 已用 utf8mb4 客户端重放 024，回填租户基线修为 1,000，页面显示 0 / 500；定向测试 44 通过 / 5 跳过。

## 隔离库测试与补充走查（2026-10-20）

- **新建隔离库** `jogeoai_subscription_20261019`：本机 MySQL 容器内用 root 新建，按顺序以 `--default-character-set=utf8mb4` 执行 001–024 并重放 024（94 张表、5 行种子）。未触碰业务库 `jogeoai`。
- `SubscriptionStoreSqlTests` 8 项全部通过，连续 3 轮稳定。首轮暴露两处测试自身问题并已修正：限流用例没把此前「已使用」「已作废」两次失败计入（产品行为正确：这两类失败按 PRD 计入防暴力尝试）；作废用例按列表下标取卡密，同批次卡密创建时间相同、顺序不定，改为按末 4 位定位。
- 新增 3 个 SQL 场景：同一订阅第二次开通后仍发放自己的第 2 周期（开通时间进入账本引用，不与第一轮撞号）；对已到期未降级的付费行指派免费版，行上 `plan_code` 真正落为 `free_trial`；免费版「已用」只计进入免费版之后的消耗（指派降级与到期降级都重置基线）。
- **注册测试库** `jogeoai_registration_20260929` 原停在 2026-09-29 结构（77 张表，缺 016–023），注册引导用例因 020 的 `prompts.group_id` 早已无法跑通。本次按顺序补执行 016–024（均为加法迁移，库内仅 9 个 `example.test` 假账号），现为 94 张表。引导用例原本在新账号上直接建第二个品牌，现按免费版 1 个品牌上限改为：先断言第二个品牌被 409 拒绝，再指派基础版后建第二个品牌，继续验证引导锚点不变。
- 合计：两个隔离库同时可用时，`RegistrationOnboardingTests|AuthFoundationTests|Subscription|CloroKeysApiTests|BrandResearchStoreTests` 75 项通过、0 跳过。
- 前端补充走查：新增夹具预览 `jogeoai-web-fixture-subscription-member`（3008/5277，普通成员）与 `jogeoai-web-fixture-subscription-onboarding`（3009/5278，需要引导、无品牌）。非管理员看不到「兑换码管理」，直接访问跳到无权限页；普通成员兑换页输入与按钮禁用并提示角色；引导第 2 步建品牌被限额拦截时显示内联提示。
- 走查发现并修复两个前端问题：限额文案「最多 1 个 品牌」量词与空格不对，改为「当前套餐「免费体验版」最多 1 个品牌（已有 1）」并按限制项使用 个 / 位 / 条；引导未完成时路由守卫会把「升级套餐」跳转拉回引导页，按钮失效，且引导第 4 步可选 10 个竞品而免费版每品牌只允许 2 个。现在第 4 步可选数取套餐上限与 10 的较小值（计数显示「已选 x/2」，超出提示带套餐名），引导页内只显示内联提示、不再弹带失效跳转的 toast。前端构建通过，定向 7 个文件 101 项与引导 UI 8 项通过。

## 待办（需用户授权）

- 清理：本机 `jogeoai` 中的验收租户「验收024」及其品牌、提问、竞品、订阅、事件、账本行；4 个卡密批次与 6 张卡密；`/tmp/jogeoai-pre024-20261019-225611.sql` 备份与 `/tmp/jogeoai-acc-*` 文件；隔离库 `jogeoai_subscription_20261019`（再跑 SQL 用例可按上文重建）。删除前需逐项确认。
- 用户用自己的账号在 3000 端口确认升级页、兑换页与兑换码管理页。
