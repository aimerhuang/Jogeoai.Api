# 品牌资料识别与无官网创建

2026-10-25：源码、定向测试及页面夹具通过，用户随后授权本机部署。**本机 `jogeoai` 已备份后执行 031→032，5260 API 与原完整模式 Worker 已重启并分别核验**；专业版现为 20/20，既有品牌保留。**真实 Luna、隔离 SQL 并发/幂等与真实用户创建未验**，当前浏览器没有登录会话。部署细节见 [本机部署记录](../.trellis/tasks/10-25-pro-plan-brand-onboarding/local-deployment.md)，完整验证边界以 [集成记录](../.trellis/tasks/10-25-pro-plan-brand-onboarding/verification.md) 为准；不推导其他运行库已部署。

前端重新构建后已在原 `127.0.0.1:3000` 以原 Vite 开发模式重启，同源代理仍指向5260；首页、新增品牌路由与模块返回200，匿名认证经代理和直连均为401。此只读检查不等于已登录成功创建。详见 [前端部署记录](../.trellis/tasks/10-25-pro-plan-brand-onboarding/frontend-deployment.md)。

## 用户流程

- 专业版目录经 031 升级为 20 个品牌、20 位成员；页面明确区分上限和已用。品牌计 active，成员计 active+invited，包含所有者；未新增成员邀请功能。
- 首次引导和已有团队新增都可粘贴介绍文字、填写资料网址，或手动填写品牌资料。Luna 返回的名称、介绍、官网和别名是可编辑建议，必须由用户明确确认。
- 最终品牌名称 1–200 字、介绍 1–2000 字必填；官网可空。第三方资料页地址不会直接当作品牌官网。识别失败/取消保留材料，允许手动填写。
- 已有团队的「添加品牌」进入 `/team/brands/new`，不会重新走原团队引导或修改旧品牌。成功后刷新列表、当前品牌和套餐用量，进入新详情。
- 首次引导无官网时基于已确认介绍研究，仍可手动添加提问和跳过竞品；最后完成步骤也允许无官网。

## 接口合同

均使用会话 Cookie 与 `X-Tenant-Id`。owner/admin/member 可操作，viewer 只读；识别始终要求真实会话。

| 接口 | 合同 |
| --- | --- |
| `POST /api/v1/brands/recognize` | `{name?: string, aliases?: string[], description?: string, url?: string}`，名称、介绍、网址至少一项非空；名称 ≤200 字、别名 ≤20 个（每个 ≤200 字）、介绍 ≤4000 字、网址 ≤2000 字。返回 `{name, description, primaryDomain, aliases, warnings, provider, model, sources}`，前三项可 null，`sources` 为联网搜索引用的 `{title, url}`（最多 10 条） |
| `POST /api/v1/sites`、`POST /api/v1/onboarding/brands` | `BrandWrite`：新建必填 name/description，primaryDomain 可省略/null/空白；可选 creationRequestId UUID，新前端总是提供 |
| `PATCH /api/v1/sites/{siteId}` | `BrandPatch.primaryDomain` 省略/null保留，`""`清空，非空校验设置；需要 expectedVersion；仅改 description 不升主体版本 |
| 品牌/团队列表与详情 | primaryDomain 无官网返回 null；页面不生成空官网链接 |
| `POST /api/v1/sites/{siteId}/brand-research` | website 可空，无网址要求 businessDescription；跳过网页抓取，保留 user 证据和原研究质量门禁 |

识别只读取已启用 `openai-compatible` 配置，固定 `gpt-6-luna`，不改全局模型配置。有名称或网址时，先由同一网关的 Responses API（由配置的 `…/chat/completions` 推导为 `…/responses`）带 `web_search` 工具按名称、别名和官网搜索公司资料，同时用公开网页安全读取器尝试读取网址正文（限制私网、重定向、来源长度）。搜索没有引用、网关失败或网页读不到只返回 warning，不再中止；只有用户介绍、搜索摘要、网页正文三者都没有时返回 422 `BRAND_SOURCE_UNAVAILABLE`（不再返回 `BRAND_SOURCE_UNREADABLE`）。三类资料分段交给无工具的提取调用，每个字段仍须有资料原文摘录，官网须有「官网/official」摘录，否则留空。总时限不超过 120 秒、模型响应最多 2 MiB、格式最多修复一次；前端仅此请求放宽到 150 秒，其他请求仍为 30 秒。按服务端已认证用户每分钟 6 次限流，不按可伪造租户头分区。识别不写 Site、研究任务或积分账本。

新建在租户锁内先检查 creationRequestId 与规范化内容，再检查额度；同标识同内容返回原品牌，内容不同返回 `409 BRAND_CREATE_IDEMPOTENCY_CONFLICT`。网络结果未知重试须复用原标识；已获得创建成功响应但列表刷新失败时，不再提交创建。

其他主要错误：`INVALID_BRAND_RECOGNITION_REQUEST` (400)、`BRAND_SOURCE_UNAVAILABLE` (422)、`BRAND_RECOGNITION_NOT_CONFIGURED` (503)、`BRAND_RECOGNITION_TIMEOUT` (504)、`BRAND_RECOGNITION_UNAVAILABLE` / `BRAND_RECOGNITION_INVALID_OUTPUT` (502)、`RATE_LIMITED` (429)、`CONTENT_FORBIDDEN` (403)、`PLAN_LIMIT_EXCEEDED` (409)。

无官网并不影响名称/别名提及识别。没有任何自有域名依据时归属维持未知，不能把任意外链算为自有引用；后续补填或清空官网生成新修订，不改写旧冻结快照和 KPI 定义。

## 发布与回滚

1. 单独确认目标库与服务部署权限，备份并记录 pro 原两项额度。若管理员设过更高/无限额度，要明确确认此次统一为 20 的变化。
2. 按序执行 031、032；032 保留非空域名唯一键/小写约束，允许多个 NULL，增加 nullable 创建请求标识/hash及唯一键。迁移不由应用启动自动执行。
3. 部署 API、Worker，再部署已生成新类型的 web。现有外部创建客户端需补 description；已有品牌与普通 PATCH 不强制补介绍。
4. 在授权隔离库验证首次/重放、并发最后名额、多个无官网和创建幂等，再做真实配置 Luna 验收。

开始写入 NULL 官网之后，不能直接回退不支持 NULL 的旧服务或改回 NOT NULL。优先前向修复或停用新增入口，不捏造域名、不删除品牌。031 撤回只按备份值恢复两项目录权益，不删除超额资源、不改变余额或周期。

本机迁移前后保留两条既有品牌和 0 条 NULL 官网，未创建测试品牌或认证会话。实际旧 host 排序为 `utf8mb4_0900_ai_ci`，032 按合同变为 `utf8mb4_0900_bin` 并允许 NULL；已有唯一键和小写 CHECK 保留。此次没有在业务库重放迁移或写测试数据，首次迁移成功不能替代隔离库重放/多 NULL/并发验证。

本轮未创建/删除数据库、表或数据卷，备份仍保留在权限受限目录；032 的临时存储过程按迁移自身流程创建并移除。仍须按 AGENTS.md 对将来产生的临时对象列明并确认后清理。
