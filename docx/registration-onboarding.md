# 邮箱注册与五步品牌引导

2026-09-29 首次实现，旧 `Jogeoai.vue` 仅保留历史参考；当前前端是相邻的 `Jogeoai.web`。用户确认使用 6 位邮件验证码；注册后的流程为：创建团队 → 创建品牌 → 选择提问 → 选择竞品 → 完成。

2026-10-25 增量支持介绍文字/资料网址经 Luna 识别和无官网品牌，创建需要名称、介绍；研究与最终完成步骤都允许官网为空。识别失败可手填、研究失败可手动添加提问。**本机 `jogeoai` 已经授权备份后执行 031/032，5260 API 与完整模式 Worker 已重启；真实 Luna、隔离 SQL 与真实用户创建仍未验**，不将页面夹具视为真实模型/业务写入验收。最新合同及发布要求见 [品牌创建说明](brand-creation.md)，下文带 2026-09-29 日期的运行态结论为历史记录。

## 部署

先执行 `db/migrations/015_registration_onboarding.sql`，再启动新 API。该迁移只新增验证码、租户引导、选择草稿三张表，不修改已有账号认证状态，不删除业务数据。本机 `jogeoai` 已执行 015。

在 API 项目 `/Library/Project/Jogeoai.Api/.env` 或生产密钥配置中设置；`.env.example` 只是模板，启动时不读取。模板中更多的知识库、国内采集选项不影响邮件注册，无需为发信补齐：

```dotenv
Email__Host=smtp.163.com
Email__Port=465
Email__Username='你的163邮箱地址'
Email__Password='该邮箱的SMTP授权码'
Email__FromName='Jogeo AI'
```

发件地址与 SMTP 登录邮箱相同；需在 163 邮箱启用 SMTP 并获取授权码。启动脚本通过 Bash `source` 加载 `.env`，含空格的值须加引号，尤其是 `Email__FromName`。代码使用 MailKit `SslOnConnect`，从连接开始启用 TLS，保留证书验证，不使用 STARTTLS 替代 465 的隐式 TLS。参见 [MailKit 连接文档](https://mimekit.net/docs/html/M_MailKit_Net_Smtp_SmtpClient_Connect_2.htm)。邮箱、授权码、验证码不进入前端配置和日志。缺少配置时发送接口返回 `503 EMAIL_NOT_CONFIGURED`，不会放行注册。

```bash
./scripts/run-local.sh api
# 另一个终端；仅执行品牌研究。需要全部后台任务时使用 worker
./scripts/run-local.sh research
```

修改 `.env` 后重启 API。真实推荐还依赖已启用并配置凭据的研究 Provider。2026-11-11 起引导第三/四步使用下文「快速推荐」同步接口，不需要研究 Worker；`research` 入口仍用于提问弹窗的异步 AI 推荐。前端保留手动添加问题/竞品入口。旧研究流程及测试结果按日期保留，不作为当前引导的操作要求。

## HTTP 合同

所有路径前缀为 `/api/v1`。认证接口无租户头；其余接口使用会话 Cookie 和 `X-Tenant-Id`。

前端注册表单依次显示邮箱、验证码、密码三项，不要求名称或确认密码；后端保留可选 `displayName` 兼容现有合同。

| 接口 | 行为 |
| --- | --- |
| `POST /auth/register/email-code` | 请求 `{email}`；发送 6 位验证码，响应仅含有效期和重发间隔 |
| `POST /auth/register` | 请求 `{email,password,emailCode,displayName?}`；验证成功后创建账号、预建团队、500 积分和会话 |
| `POST /auth/login` | 请求 `{email,password}`；同一账号连续失败 30 次后锁定（1→2→4→8→15 分钟），锁定期 429 `RATE_LIMITED` + `Retry-After`；迁移 045 未执行时 503（2026-11-08，C2） |
| `GET /onboarding` | 返回固定的初始品牌、进度、`teamConfigured`、`required` |
| `PUT /onboarding/team` | 请求 `{name}`；仅团队所有者可确认/修改初始团队名称 |
| `POST /onboarding/brands` | 新建必填品牌名称、介绍，主域名可空；可选 creationRequestId 支持同内容重试；团队确认后才允许创建，品牌与引导锚点原子写入（新合同依赖032） |
| `PUT /onboarding/{siteId}/draft` | 保存步骤、选中的问题/竞品和最近的研究任务 ID |
| `POST /onboarding/brands/{siteId}/complete` | 必选 1–5 个问题，可选 0–10 个竞品；同一事务写入全部内容和完成状态，重复提交不重复创建 |

验证码有效期 10 分钟，同邮箱 60 秒发送间隔、每小时最多 10 次，最多尝试 5 次。验证码使用 PBKDF2 哈希保存；发送失败的验证码不能使用；消费与账号创建在同一事务中。接口另外按直连 IP 限流：发送每小时 20 次，注册每 10 分钟 30 次。若生产代理后需要按最终客户端 IP 区分，必须配置受信任的转发代理，不能无条件信任客户端提供的转发头。

既有账号保留登录能力；015 不伪造历史邮箱验证时间。2026-11-08 起登录与注册响应默认不再在响应体返回 `sessionToken`（只写 HttpOnly Cookie），非浏览器客户端须带 `X-Jogeoai-Session-Delivery: token`；身份接口响应带 `Cache-Control: no-store`。新账号的业务页面由前端路由守卫强制走引导。API 层验证团队、品牌、问题数量以及租户归属；业务 API 的权限仍沿用现有会话/租户规则，引导状态不替代授权校验。

原注册积分 SQL 引用了 014 积分表中不存在的 `site_id`，现已改为租户级赠送，与实际表结构一致。

## 定向验证

后端命令：

```bash
# JOGEOAI_REGISTRATION_TEST_CONNECTION 从本地密钥配置传入；禁止指向业务库
dotnet test tests/Jogeoai.Tests --filter 'FullyQualifiedName~RegistrationOnboardingTests|FullyQualifiedName~AuthFoundationTests' -v minimal
```

12 项通过，0 跳过。7 项新增测试（5 项使用 MySQL，2 项本地验证）覆盖：配置缺失、验证码格式/邮箱绑定/哈希/失效/错误次数、发送失败、邮箱限流、并发消费只创建一个账号和一笔积分、团队所有者校验、品牌必填、引导品牌固定、草稿恢复、1–5 问题限制、事务回滚、重复提交与跨租户隔离。其余 5 项验证原有认证边界。

独立回归库为 `jogeoai_registration_20260929`，只复制业务库表结构，使用 `example.test` 假邮箱及内存邮件发送器，无真实邮件和业务数据。该库保留供此定向回归复用；清理须按 AGENTS.md 列出对象并取得确认。

2026-10-20 套餐任务按顺序为该库补执行 016–024（加法迁移，库内只有假账号），由 77 张表升至 94 张表；此前它缺 020 的 `prompts.group_id`，引导用例已无法跑通。引导用例改为先断言免费版第二个品牌被拒、指派基础版后再建第二个品牌。详见 [套餐说明](subscription-plans.md)。

前端的本次验证记录见相邻 `Jogeoai.vue/docs/verification.md`。最初凭据为空时，发送接口已验证返回 `503 EMAIL_NOT_CONFIGURED`。

2026-09-29 用户保存 `.env` 后，已确认邮箱账号与授权码非空，并修复发件人名称含空格但未加引号导致的加载失败；没有修改凭据值。本轮仅配置修正与文档同步，未重跑后端代码测试。实际执行的定向验证：

- `bash -n .env` 通过；随后在 Bash 中执行 `set -a`、`source .env`、`set +a`，加载成功。语法检查本身不能发现未加引号的名称被当作命令执行的问题，因此以实际加载结果为准。
- 加载 `.env` 后通过一次性 `python3` 脚本调用 `smtplib.SMTP_SSL`、`ssl.create_default_context()` 与 `login()`，`smtp.163.com:465` 返回 SMTP 235，TLS 证书校验与身份认证通过。未调用邮件发送方法，未记录凭据或协议内容。
- `./scripts/run-local.sh api` 重启 API 加载配置，监听 5260。
- 使用 Python `urllib.request.urlopen` 检查 `/health/live`、`/health/ready`，均返回 200；前端 3002 端口代理的 `/api/v1/auth/me` 未登录返回预期 401。

真实验证码邮件的发送、收件和真实账号注册尚未测试；SMTP 登录成功不代表邮件投递已经验证。

## 第三步模型推荐更新（2026-09-29，旧策略历史记录）

第三步向既有 `brand-research` 接口传 `maxPrompts: 10`。模型根据品牌、官网正文生成中文客户提问，提示词要求至少 80% 为不带品牌词的品类/场景问题；候选不代表实测搜索量。输出按 Unicode、空白、大小写及结尾标点去重，不足时共用 Provider 超时预算补问，最多两次，仍不足则任务失败而不保存不完整结果。官网读取支持最多 5 次跳转，每次连接保留公网地址校验，过滤脚本/样式；无法读取时继续使用品牌名称和域名。

本机新增 `./scripts/run-local.sh research`（`--ResearchOnly=true`），只执行品牌研究。已有全部 Worker 入口保持兼容。首次运行全部 Worker 发现知识库空队列读取存在既有连接复用异常，本次未改动该模块，当前本机运行独立研究模式。

实际验证：

- `dotnet test tests/Jogeoai.Tests/Jogeoai.Tests.csproj --no-restore --filter 'FullyQualifiedName~BrandResearchProviderTests|FullyQualifiedName~BrandResearchWorkerTests' --verbosity minimal`：13 项通过（0 跳过），只运行品牌研究 Provider/Worker 测试，使用内存仓储和 HTTP 测试响应，不写业务库。初次异常 JSON 测试的精确异常断言不接受子类，改成 `ThrowsAnyAsync<JsonException>` 后通过。
- `bash -n scripts/run-local.sh`、`./scripts/run-local.sh research`：语法检查和独立 Worker 启动成功。
- 一次性 C# 程序仅读取现有 Provider 配置，用公开的 Apple 产品类别调用 `OpenAiCompatibleResearchProvider.RunAsync`（10 条、0 竞品）：`gpt-6-luna` 返回 10 条互不重复中文提问，8 条不带品牌词；没有新建业务记录。临时工程使用 `/private/var/...` 实际路径启动，避免 macOS `/var` 符号链接使传递项目引用路径解析失败。
- 启动 Worker 处理了用户此前 pending 的历史任务，其旧请求参数为 30 条，最终状态 done；新前端兼容旧结果只展示 10 条，新生成请求固定 10 条。此项为恢复已有业务任务，不是向业务库注入测试数据。
- 前端 `npm run test:onboarding` 46 项通过，`npm run build` 通过；浏览器内存环境已确认显示、换批保留选择、刷新恢复和 5 条选择上限，完整记录见 `../Jogeoai.vue/docs/verification.md`。

本轮没有数据库迁移，也未运行全局测试套件；公开 DTO 及接口路径未变化，无需重新生成前端 API 类型。


## 异步品牌研究（2026-09-29 起实现，现供提问弹窗使用）

旧的“官网失败后按品牌名猜测”流程已移除。研究链路为官网资料收集、行业画像提取、候选生成、独立审核；推荐不代表实测搜索频率。该接口支持业务纠正、推荐理由与来源及反馈。2026-11-11 起引导已不用此链路，下文涉及旧引导界面和耗时的内容只保留当时证据；当前引导见「第三/四步快速推荐」。

接口仍使用 `/sites/{siteId}/brand-research`。请求新增 `reuseJobId`、`businessDescription`、`excludedQuestions`、`feedback`、`market`、`language`；状态响应新增 `research`，含 `phase`、`profile`、`evidence` 和策略版本。客户端不能直接写入核验画像。同租户同站点的旧任务在服务端读取，官网/品牌/市场/语言/策略不匹配或官网资料超过 24 小时则失效。更改业务文字使画像失效并重新分析，证据仍可复用。用户补充文字明确以 `user` 来源保存，不伪装成官网。

现有 `request_json`/`result_json` 保存研究快照，无新增 migration。历史结果仍可读取，但前端不将其作为新策略候选。幂等键包含策略隔离，MySQL JSON 以结构比较避免字段顺序引起错误冲突。`RESEARCH_NEEDS_INPUT` 为失败状态的可操作补充信息分支，不自动重试猜测。

抓取至多 10 页、深度 2、并发 3、总计 45 秒，单页最多 768 KiB / 正文摘要 6000 字符，总摘要 48000 字符；同站点均衡采样公司、产品与 FAQ/指南，保留来源 URL、哈希与时间。每跳校验 HTTP(S) 地址，并使用公网 DNS 连接固定 Handler。网络抓取保留真实 www 主机，不使用统计层去 www 的地址。模型先核对身份、行业、产品原文，再生成 20 条左右候选，独立请求审核自然意图、相关性、前提依据和语义重复；不足最多重写两轮。生成阶段使用压缩证据，审核使用完整有界摘要。竞品仅在官网明确提供依据时推荐。

租约三分钟，每 20 秒续期，单次执行总预算八分钟；每次领取产生唯一令牌，进度、完成及失败写入均要求当前且未过期租约。取消或续租失败会停止网络/模型请求。瞬时错误最多三次任务尝试，复用已持久化画像。非标准 `web_search` 配置不再发送，也不视作有可靠行业检索服务。

实际验证命令均为 `dotnet test tests/Jogeoai.Tests/Jogeoai.Tests.csproj --no-restore --filter '<下列过滤表达式>' --verbosity minimal`：

- `(FullyQualifiedName~BrandResearchProviderTests|FullyQualifiedName~BrandResearchContextTests|FullyQualifiedName~BrandResearchWorkerTests|FullyQualifiedName~BrandWebsiteCollectorTests)`：29 项通过。
- 后续新增断网恢复验证后，`FullyQualifiedName~BrandResearchWorkerTests`：9 项通过。
- `FullyQualifiedName~BrandResearchStoreTests`：1 项通过；设置 `JOGEOAI_REGISTRATION_TEST_CONNECTION`，只运行在既有 `jogeoai_registration_20260929` 隔离库，未创建新库/新表。
- 最终 `FullyQualifiedName~BrandResearchProviderTests|FullyQualifiedName~BrandResearchLiveTests`：15 项通过；真实验证设置 `JOGEOAI_RESEARCH_LIVE_CONNECTION`（仅读取 Provider 配置）和 `JOGEOAI_RESEARCH_REPORT`（本地临时输出），默认未启用时不调用真实模型。优联官网的最终结果为 10 条、8 条不带品牌/2 条带品牌，行业正确识别为管道检测定位设备及特种机器人。一次先前调用触发 90 秒配置超时，随后缩短生成上下文并验证有界重试；未以模板兜底。

前端从真实 API 执行 `npm run api:generate`，`npm run test:onboarding` 52 项通过，`npm run build` 通过；独立内存浏览器流程通过。结果和来源索引为 `../Jogeoai.vue/docs/ulinktek-industry-research-result.json`，完整记录与截图见 `../Jogeoai.vue/docs/verification.md`。

本机 API 5260 与仅研究 Worker 已更新；健康检查均为 200。未执行数据库迁移、未写入业务测试记录、未运行全局测试。第二阶段真实咨询、公开问答、搜索需求信号尚未接入。

### 2026-11-27 发起额度与领取顺序（SQL 测试已在本机隔离库通过，本机 API / Worker 已重启；409 / 429 未在运行态验证）

提问弹窗的 `brand-research` 发起接口增加了额度，领取顺序改为按团队轮转；没有迁移。引导第三/四步的同步快速推荐不经过研究队列，不受影响。

| 情况 | 响应 |
|---|---|
| 相同 `refreshKey` 且内容相同的重放 | 202，返回原任务；不计入次数，也不受下面两项限制 |
| 该品牌已有 `pending` / `running` 任务 | 409 `RESEARCH_ALREADY_ACTIVE`「该品牌已有研究任务在进行中。」，`error.details.jobId` 为现有任务 ID |
| 当前团队最近 24 小时内已新建 20 个任务 | 429 `RESEARCH_DAILY_LIMIT`「今天的品牌研究次数已用完，请明天再试。」 |

- 检查顺序：只读成员 403 → 请求内容校验 400 → 站点不存在 404 → `refreshKey` 长度 400 → 幂等重放 / 幂等冲突 → 每品牌进行中任务 → 每团队 24 小时次数 → `reuseJobId` 是否存在（404）→ 插入。全部在站点行锁内、同一事务（READ COMMITTED）完成，同一品牌的并发请求只会成功一条；对应的并发 SQL 用例已在本机隔离库通过（READ COMMITTED 下只插入一条任务）。
- “每日”是滚动 24 小时，不是自然日；已取消和失败的任务同样计入。带 `reuseJobId` 的“换一批”每次都新建任务，计入次数，也受每品牌一个进行中任务的限制。
- 同一团队不同品牌的并发请求可能使团队计数略微超过 20（最多超出“并发品牌数 − 1”次），没有为此加团队级锁。
- 数值可通过 API 的 `Research:Quota:MaxActivePerSite`（默认 1）、`Research:Quota:DailyPerTenant`（默认 20）调整，小于 1 时 API 启动失败。
- Worker 领取：最近最少被服务的团队优先。排序值是团队最近一次任务首次被领取的时间，升序；从未被服务过的团队用任务自己的创建时间代替，所以新团队不插队，排在“上次服务早于它入队时间”的团队之后。同一团队内按创建顺序。租约、续期和重试规则不变。重试不刷新团队的最近服务时间（一个任务最多 3 次尝试）；其余有上限的偏差见 README「品牌研究额度与公平领取」的风险说明。
- **前端配套改动（2026-11-27 已在 `Jogeoai.web` 的 `useRecommendation.ts` 完成）**：
  1. 发起新任务前等上一个任务的取消请求返回（成功或失败都继续）再提交。
  2. 收到 409 `RESEARCH_ALREADY_ACTIVE` 时接管 `error.details.jobId` 对应的任务：继续轮询并展示进度，可用弹窗的「取消」取消它。页面刷新、请求中止或轮询超时后遗留的任务，会在下一次推荐时被接管。429 `RESEARCH_DAILY_LIMIT` 只展示服务端文案，不提示重试。

  验证范围：`tests/prompt-recommendation.test.ts`（模拟接口）和 `npm run build`；2026-11-27 在 3001 预览对本机 5260 实测了「刷新后再次推荐 → 409 → 接管 → 取消」（只产生 1 条任务，已取消）。429 没有在运行态触发过。

验证（2026-11-27）：

- `BrandResearchQuotaSqlTests` 的 7 个新用例已在本机隔离测试库全部通过（同一品牌并发只插入一条任务、滚动 24 小时上限、领取顺序、新团队不插队）。连同 `BrandResearchStoreTests` 合计 8 项通过、1 项跳过；跳过的是既有用例 `BrandResearchStoreTests.PersistsContext…`，它需要带有既有租户数据的专用库 `jogeoai_registration_20260929`，这个用例调整后的断言仍未运行。
- 本机开发库（79 个任务、2 个团队）上对领取语句执行过 `EXPLAIN`，按当前数据量不需要补索引，结果见 README「品牌研究额度与公平领取」。
- 本机 API 5260 与 Worker 已于 2026-11-27 22:07 用新构建重启，健康检查为 200；Worker 重启后运行一分多钟日志没有错误，研究循环按新的领取语句轮询。409 和 429 没有在运行态触发过；上面两项前端改动仍未做。
- 验证命令见 `.trellis/tasks/11-07-sec-task-storage-quota/verification.md`。

### 2026-11-09 画像校验与部分结果

问题弹窗「AI 推荐」复用本接口。无官网品牌只有一句用户补充（如「主营spa，按摩，精油按摩」）时，原文不含品牌名，模型给出的身份引用无法逐字核对，12 次重放 9 次在画像阶段返回 `RESEARCH_NEEDS_INPUT`；补充业务文字不能解决。现行规则：

- 资料来自品牌方（`user` 或 `knowledge:`）且模型身份引用未通过时，身份取请求的 `brandName`，引用落在该资料原文上，并忽略模型因缺少品牌名给出的 `sufficient=false`；仅官网证据时规则不变。提取与审核提示词同步说明这一点。
- 品牌方资料的引用最短 2 字（网页证据仍为 4 字）；未通过核对的产品逐条剔除，至少保留 1 个已核对产品，不再整体作废。
- 有界改写后审核通过的问题不足 `maxPrompts` 时返回已通过部分，一条都没有才以 `RESEARCH_QUALITY_INSUFFICIENT` 失败；仍无模板兜底。当时的引导前端仍要求满 10 条，不足时显示自身的「未生成完整的 10 条」提示；该界面已于 2026-11-11 被快速推荐替代。

验证：`dotnet test tests/Jogeoai.Tests --filter "FullyQualifiedName~Jogeoai.Tests.BrandResearchProviderTests"` 17 项通过。重启本机完整 Worker 后，复制沐岛失败任务的请求入队（任务 `BA6F69F7DBEE11F192457E47E9F00832`），63 秒完成：身份「沐岛」来自 `user`，产品为 SPA、按摩、精油按摩服务，返回 10 条问题。

### 2026-11-09 提速

实测 gpt-6-luna 推理 token 很少（0–190），耗时主要来自输出速度（约 50 token/秒）：20 条候选的生成约 35 秒，审核约 16 秒，画像约 10 秒；`reasoning_effort=low` 无明显改善，deepseek-flash 推理 5000+ token、不更快。改动：

- 每轮候选按意图（选型对比 / 使用体验与注意事项）拆成 2 个并发请求，各生成一半；一批失败保留另一批，全部失败才交给 Worker 换 Provider。审核仍一次看全部候选以跨批去重。生成阶段不再输出推荐理由（审核给出的理由才会展示）。
- 请求新增 `singleRound`（默认 false）：只做一轮生成与审核。弹窗「AI 推荐」使用它，并以该品牌上次完成的任务作 `reuseJobId` 跳过画像提取；2026-11-09 当时的引导仍最多三轮，2026-11-11 起不再使用此接口。

验证：Provider / Worker / Context / 无官网品牌定向测试 48 项通过（新增并行分批、单批失败、单轮三项）。重启 5260 API 与完整 Worker 后，在 3001 预览（owner 会话）按弹窗请求体调用沐岛：首次 48 秒（画像 6 秒、生成 20 秒、审核 21 秒）返回 10 条；复用画像的「换一批」31 秒返回 7 条，直接从生成阶段开始。此前同类请求 63–160 秒。

### 2026-11-09 问题写法对齐「监测问题配置技巧」

此前生成提示词要求按「应用任务、痛点和选型条件」出题，并行批次之一专写「使用体验、注意事项」，产出的多是判断/科普/操作题（如「精油按摩和普通按摩有什么区别」），正是配置技巧里「避免这样写」的类型，AI 回答不会列举品牌。现在生成与审核共用同一份写法规则（与前端 `TipsPanel` 和核心词拓展一致）：

- 行业词（约 80%，不带品牌名）：主干必须是请 AI 推荐、列举、排行或比较多个品牌/商家，可用人群、场景、需求、预算限定；判断题、科普题、操作题、注意事项一律由审核拒绝。
- 品牌词（约 20%，仍按每 10 条最多 2 条接受）：必须带品牌名并点明质量、口碑、价格、对比、适合人群、售后之一；笼统的「怎么样 / 好用吗」拒绝；对比只用已核实竞品。
- 两个并行批次改为「行业推荐类」与「行业榜单清单类 + 全部品牌词」。

验证：`BrandResearchProviderTests` 21 项通过（新增规则同时进入生成与审核的断言）。重启完整 Worker 后，以沐岛弹窗请求入队 2 个任务，各 46 秒，分别返回 8 条、6 条：行业词如「精油按摩哪家好？能推荐几家吗？」「SPA 服务中性价比高的品牌有哪些？」，品牌词如「沐岛的 SPA 服务质量怎么样？」，没有判断/科普/操作题。单轮审核更严，每批少于 10 条，「换一批」补充。

## 2026-11-11 第三/四步快速推荐（当前实现）

用户反馈第三步等待研究任务太久。引导不再创建 `brand-research` 任务（草稿 `researchJobId` 一律写 null），改为两个同步接口，Worker 不运行也能推荐：

| 接口 | 行为 |
| --- | --- |
| `POST /sites/{siteId}/prompt-suggestions/quick` | 请求 `{excludedQuestions?, locale?, count?}`（count 1–10，默认 10）；只用品牌名称、别名、介绍和主域名，一次模型调用返回约 8 条行业词 + 2 条品牌词（`KeywordSuggestion`），写法规则与研究生成/审核共用；排除品牌已有提问与请求列出的问题；不足返回 `partial=true`，一条都没有 502 `PROMPT_SUGGESTION_FAILED` |
| `POST /sites/{siteId}/competitor-suggestions` | 请求 `{excludedNames?, locale?, market?}`；一次模型调用返回最多 10 个 `{name, domain?, reason?}`，剔除品牌自身（名称、别名、主域名及附加域名）、品牌已有竞品、排除项和重复项；域名规范为主机名，无法识别置空，未经联网核验；无结果返回空列表 |

两者都需要真实会话与品牌写权限、共用 `prompt-suggestions` 限流（每用户每分钟 6 次），不落库、不扣积分。按 `research_provider_settings` 优先级调用：首个 Provider 最多 20 秒，回退只用剩余预算，总预算 28 秒；都超时返回 504 `*_TIMEOUT`，都失败 502 `*_FAILED`，未配置 503 `*_NOT_CONFIGURED`，参数错误 400 `*_INVALID`。日志只记 Provider、耗时、异常类型，不记提示词和响应。

前端第三步改成「新增监测问题」弹窗的结构：写法技巧面板（与弹窗共用收起状态）+ AI 推荐 / 核心词拓展 / 批量导入三张卡片（默认 AI 推荐）+ 已选列表 + 自定义输入，去掉「品牌业务信息」栏、研究状态和骨架屏。AI 推荐点即选、可换一批；核心词拓展与批量导入（引导内只支持每行一句、不建分组）按顺序加入，满 5 条后提示已加入条数。第四步进入时自动请求竞品推荐，可换一批、重试、手动添加或跳过。完成接口与草稿合同未变，无 migration。

验证（2026-11-11）：

- `dotnet build Jogeoai.slnx` 通过（既有 PKCS 等告警）。`dotnet test tests/Jogeoai.Tests --filter "FullyQualifiedName~PromptKeywordExpansionTests|FullyQualifiedName~QuickSuggestionServiceTests|FullyQualifiedName~SessionAuthorizationEndpointTests|FullyQualifiedName~BrandResearchProviderTests"`：86 项通过。新增 `QuickSuggestionServiceTests` 覆盖 8+2 归类与排除、partial、Provider 回退、首个 Provider 截断、总预算超时 504、竞品剔除与域名规范化、错误码。
- 5260 API 用新构建重启（`run-local.sh api`，后台进程，日志 `/tmp/jogeoai-api-5260.log`），以临时会话（用后已删除）对本机三个真实品牌各调用一次：快速推荐 沐岛 11.5 秒、优联 9.2 秒、狮心 7.0 秒，均 10 条（8+2）、行业词不含品牌名；竞品推荐 11.5 / 11.6 / 11.5 秒，分别 5 / 10 / 2 个，均不含品牌自身。优联默认语言为英文，问题按英文返回。只用到 `gpt-6-luna`，`deepseek-flash` 回退的真实时延未测。
- 前端：`npm run api:generate`、`npm run build` 通过；`npx vitest run` 引导、路由、会话、品牌、引导 UI、品牌引导流程、弹窗逻辑、套餐逻辑 8 个文件 115 项通过。新增夹具场景 `ONBOARDING_FIXTURE_STEP=prompts`（launch 配置 `jogeoai-web-fixture-onboarding` 3018/5288）走查第三步渲染、勾选、核心词拓展满 5 条提示和第四步竞品推荐。新用户真实注册走完五步尚未验证。
