# 本机状态核对

本文按日期保留证据，顶部为最近核对；历史进程、配置、库清单和测试结果不能外推为当前状态。功能合同仍在 [README](../README.md) 和各模块说明。

## 2026-11-12 文档同步与只读核对

核对时间：2026-11-12 11:38–11:44（Asia/Taipei）。按用户「直接同步」，未创建 Trellis 任务。范围为当前后端的入口文档、规则、近期引导 / 提问 / 监控变更及本机服务；不是全项目功能验收或发布。

### 当前可确认的事实

- API 5260 的 `/health/live`、`/health/ready`、`/openapi/v1.json` 均为 200。API PID 6493，启动于 2026-11-11 17:39；Worker PID 11963，启动于 2026-11-12 09:45。Worker 进程存在不证明所有后台功能都启用或消费成功；本轮未改环境变量、启停服务或查看敏感进程环境。
- OpenAPI 包含同步问题推荐、竞品推荐、团队默认配置、恢复跟随和推荐渠道接口，相关 summary 为中文；也包含 `InstantCollectionStatus` / `InstantCollectionView` 和 `PromptView.instantCollection`。立即采集源码已在 Controller、服务和主时隙展开中接入，但任务目录尚无验收记录，详见 [提问管理说明](question-management.md#立即采集增量2026-11-12-核对)。接口存在不证明真实采集、积分或并发去重通过。
- Compose 的 `jogeoai-mysql-1`（MySQL 8.4）为 running / healthy；本机 `jogeoai` 中 043 的品牌知识条目表、044 的 MCP 令牌表、047 的三个目标列和 048 的两张默认配置表 / 跟随列存在。只查结构，不等同于完整迁移及回滚验收。
- 当前有 5 个启用的 09:01 计划、1 个停用的 00:05 计划；`20261112T0901` 有 5 个 `completed` 时隙。**这只是时隙状态，不是 Run / Check 成功或积分结算证明**；没有核对这些轮次的回答、账本及其是否覆盖团队默认配置下发场景，相关待验收项不关闭。
- 当前项目无 Git 根，不能提供提交、dirty diff、分支或合并凭证。`.env` 权限为 600，ignore 覆盖 `.env`、`.local`、`.data` 和构建输出；未读取或复制凭据值。规则入口为项目 `AGENTS.md` 与用户级定向测试规则，未发现上级覆盖文件。

### 同步与保留边界

- README 区分同步引导和异步提问研究；品牌监控说明修正「团队默认配置仍用弹窗」的旧描述，并链接立即采集增量。提问说明将 018 原始 PRD 明确标为历史接口，避免据此接入。
- AGENTS 保留迁移顺序、运行态验证和安全边界，将配置版本、某次部署 / 验收结果留在模块说明，避免多份快照漂移。
- 当前非系统库为 `jogeoai`、`jogeoai_prompt_scope_20261111`、`jogeoai_registration_20260929`、`jogeoai_repcheck_20261030a`、`jogeoai_subscription_20261019`。业务库不列清理候选；注册库与套餐库被现有定向测试明确引用，保留。prompt_scope / repcheck 两库的会话归属和后续用途尚待核对，不能仅凭名字或日期判断可删。
- 原始设计页、任务目录和任务快照 / 备份保持原样，没有依据把未完成任务批量归档。未新增明确可删除对象；复核现场仍保留，任何清场需先列明对象并在汇报后取得用户确认。

### 本轮验证记录

| 实际检查 | 结果 / 范围 |
|---|---|
| `bash /Users/mac/.codex/skills/neat-freak/scripts/audit-inventory.sh /Library/Project/Jogeoai.Api` | 退出 0；机械枚举 689 个 Markdown/MDX（含只读参考与任务历史），没有项目 Git 根；按索引与近期命中文档选读，未逐篇审计历史 |
| `rg --files --hidden`、`rg -n`、`sed` / `cat` | 排除 references、构建 / 依赖、本地数据与缓存后枚举 2169 个文件；核对启动入口、公开 DTO / Controller、立即采集服务与主时隙逻辑、近期验收记录；没有修改代码 |
| `lsof -nP -iTCP:5260 -sTCP:LISTEN`、`ps -axo pid,ppid,lstart,comm`、`docker compose ps --format json`、`stat -f '%Sp %N' .env` | API / Worker 进程、监听、容器健康及权限如上；未输出密钥 |
| `curl --max-time 8` GET 健康与 OpenAPI，JSON 解析筛选路由 / schemas | 三个 GET 200；只核对接口声明，不调用写接口或付费模型 |
| `docker compose exec -T mysql`，使用容器已有凭据运行 `SHOW DATABASES` 和只读 `SELECT` | 表 / 列、计划与当日时隙聚合如上；没有迁移、写入、授权变更或清理 |
| `node` 内联只读检查 README / AGENTS / docx 的本地 Markdown 链接、标题锚点和代码围栏；解析 `.codex/hooks.json` | 33 份文档、277 个本地链接 / 锚点通过，围栏配对；行内代码中的 `[文本](URL)` 是示例而非死链。两个 hook 脚本存在，三个项目平台目录未发现断软链 |

本轮纯文档与规则变更，未运行 `dotnet build` / `dotnet test`、真实采集、生文或邮件投递；不将历史测试记录当作本轮通过。

### 事实面状态

| 事实面 | 状态 | 边界 |
|---|---|---|
| 代码 | verified-current | 只覆盖本轮描述的入口 / 合同 / 控制流，未执行功能测试 |
| 运行态 | pending | 健康、接口声明、schema 与时隙聚合已核对；立即采集端到端、账务、部署源码版本关联仍缺证据 |
| 文档 | changed-and-verified | 已对齐本轮范围内的现役 / 历史描述；非全量历史审计 |
| 规则 | changed-and-verified | 定向测试、迁移与安全边界保留，重复运行快照收敛为指针 |
| 记忆 | out-of-scope | 宿主生成记忆按 generated-read-only 处理，未写长期记忆或 Trellis journal |
| 工作区 | pending | 无 Git；测试库和任务备份保留，未判断全部残留可清理 |

未消除的 warning：没有部署 marker 将 API / Worker 关联到精确源码版本；立即采集方案所述主时隙并发窗口仍在待验收范围。相邻前端、其他环境发布、全量历史任务和平台生成规则治理为 out-of-scope。

## 2026-10-11 历史核对

核对时间：2026-10-11 20:54–21:06（Asia/Shanghai；数据库与业务查询截至 21:00，服务与文档门禁复核至 21:06）。范围为当前后端项目、本机 `jogeoai-mysql-1` / `jogeoai` 与 API 5260；没有重验相邻前端、外部 Provider、邮件投递或其他部署环境。以下仅记录当时证据。

### 本机服务与每日监控

- `/health/live`、`/health/ready`、`/openapi/v1.json` 均为 HTTP 200。Cloro settings/pool/keys 与 credits 的匿名 GET 均为 401；没有登录或发起管理写操作。
- API 进程在 2026-10-11 20:48:47 启动并监听 5260；核对时没有 Jogeoai Worker 或其 `dotnet run` 启动进程。API 健康不代表每日计划、采集、分析、知识摄取或生文正在消费。
- 没有可将运行进程关联到源码修订的部署 marker，项目根也没有 Git；不能声称当前磁盘全部改动都已部署，或本轮已提交、合并。
- 唯一每日计划启用，时区 `Asia/Shanghai`，下一时隙为北京时间 **2026-10-12 09:00**，选择 ChatGPT/Gemini/Copilot 三目标。该配置不保证轮次执行；需要监控或完整 Worker 运行。

北京时间 2026-10-11 09:00 的时隙 `20261011T0900` 为 `completed`，Run `7bcf96f3-a827-4ad2-af0f-bc0dd556ebeb` 为 `succeeded`，15/15 成功、0 失败：

| 引擎 | 成功回答 | 保存引用行数 | 已发布分析 | 来源质量 |
|---|---:|---:|---:|---|
| ChatGPT | 5 | 153 | 5 | 5 条 available/complete |
| Gemini | 5 | 65 | 5 | 2 条 available/complete、3 条 available/partial |
| Copilot | 5 | 61 | 5 | 5 条 available/complete |

引用行数不是独立网站数。发布版本有 15 条核心分析及 15 条详细完成证明。监控积分预留记录为 `captured`：预计和实际结算均为 150，剩余预留及释放均为 0；账户可用 250、预留 0、累计结算 250。

数据库、Run/Check GET 与当日监控列表均包含这 15 条记录，列表显示分析 `completed`。三个引擎各抽查一条列表同 `meta.snapshotToken` 的详情，正文、结果质量及引用 URL/标题/顺序与 Check API 一致。详情投影不包含 Check 引用 DTO 的 `id`，不把这项形状差异当作引用丢失。本轮只回查已有结果，没有创建 Run 或调用 Cloro。

**仍存在的查询缺口**：Run 汇总返回 `analysisStatus=not_started`、`analyzedChecks=0`，与已经完成的分析不一致；当前 [VisibilityRunSummary](../src/Jogeoai.Domain/DomesticCollectorModels.cs) 仍使用常量。判断实际进度应读取监控列表/详情及已发布分析，修正汇总字段待后续开发。

### 数据、规则与保留对象

- 本机业务库有 86 张表；012/013/014、019/020 的产物仍在，021 的 `result_metadata` / `monitoring_enabled_at` 及 022 的两张表、任务关联与等待列存在。本轮未执行 migration，结构存在不证明全部迁移边界或 SQL 案例通过。
- Cloro 池保留 4 个未归档启用账号，另有已归档的无效测试账号。已知缓存余额合计 1519，最近刷新约为北京时间 15:19；这不是 21:00 的上游实时余额，没有调用外部余额接口。022、401 切换和 Worker 绑定的真实验收见 [Cloro 说明](cloro-key-pool.md)，403/429、R-04、2 小时上限及隔离 SQL 仍待验收。
- 根 `AGENTS.md` 是当前项目规则；直接上级未发现覆盖规则，用户级 `AGENTS.md` 规定定向测试和文档免代码测试。本项目没有声明 CLAUDE/AGENTS 软链同源，不为形式创建第二份规则。Codex 两个 hook 引用的脚本均存在，工作流状态注入在本会话可见。
- `.env` 权限为 600，`.gitignore` 包含 `.env`、本地数据和构建输出；未复制配置值。未初始化 Git，不能验证 tracked secrets 或 dirty lane。
- 后端目录、错误处理、日志三个基础规范仍为模板，索引已经标明 `To fill`；不将其描述为已建立的规则。当前功能合同规范与这些模板分别判断。
- `jogeoai_registration_20260929` 隔离库仍存在（77 张表），[注册说明](registration-onboarding.md) 与对应测试明确要求保留复用；不列为默认删除候选。业务库、命名卷、设计页面、只读参考源码及 `.trellis/tasks` 的未完成任务均保留。
- `.trellis/tasks` 顶层有 14 个任务，状态仍为 planning/in_progress，但没有当前任务指针；未因局部代码或某个真实轮次通过而批量改成 completed。本轮按用户选择未创建任务或更新 Trellis journal，平台记忆也未写入。

#### 知识摄取修复的证据边界

早期验收报告中的空队列 `MySqlConnection is already in use` 是当时观察。[当前 KnowledgeStore](../src/Jogeoai.Infrastructure/KnowledgeStore.cs) 已把 reader 放入独立释放作用域，在回滚前关闭；[新增 SQL 用例](../tests/Jogeoai.Tests/KnowledgeStoreSqlTests.cs) 也覆盖连续两次空队列领取。两文件修改时间为 2026-10-11 15:38/15:40。

未找到本项目内该修复的实际 SQL 执行凭证，当前隔离 stage1 库也不存在；本轮没有运行代码测试或启动 Worker。因此可确认代码和用例已存在，不能宣称回归与运行验收通过。旧任务报告保留当时证据，不改写成新的验收结果。

#### 待确认的删除候选

| 对象 | 理由 |
|---|---|
| `pelican-bicycle.html` | 独立演示，项目内仅清理清单提及，无业务依赖；2026-10-19 经用户确认删除 |
| `pelican_bicycle.html` | 同主题另一演示，无业务依赖；2026-10-19 经用户确认删除 |
| `.trellis/tasks/09-29-content-management/COMPLETION_SUMMARY.md` | 已收敛为历史指针，现役内容在内容说明及 implement.md；2026-10-19 经用户确认删除 |
| `.trellis/tasks/09-29-content-management/DEPLOYMENT_SUMMARY.md` | 部署说明已合并，仅保留历史指针；2026-10-19 经用户确认删除 |
| `/tmp/jogeoai-neat-file-inventory.txt` | 本轮机械盘点的临时清单，可重建，无独立业务内容 |

前四项在既往内容收尾中已经列为候选，本轮再次检查；临时清单也保留供复核。2026-10-11 当时复核现场保留、未删除任何对象；前四项已于 2026-10-19 经用户确认删除（见下文「2026-10-19 清场结果」），本表链接随之改为纯文本。

### 本轮验证记录

实际执行的是以下检查，不将历史测试结果写成本轮通过：

| 命令 / 检查 | 结果 |
|---|---|
| `bash /Users/mac/.codex/skills/neat-freak/scripts/audit-inventory.sh /Library/Project/Jogeoai.Api` | 退出 0；机械枚举根目录、规则及 323 个 Markdown/MDX 文件（新增本文前），没有项目 Git 根 |
| `rg --files --hidden`，显式排除 references、bin/obj、依赖和缓存 | 枚举项目源码/配置/文档；按命中的文档检查过期状态和残留引用 |
| `curl --max-time 8 -sS` 检查 live/ready；Python `urllib.request` GET OpenAPI、匿名入口、既有 Run/Check、监控列表和同快照详情 | 健康与查询均通过上述边界；23 个内容管理 HTTP 操作仍各有中文 summary |
| `ps -axo pid,ppid,args` / `ps -axo pid,lstart,comm`（只输出本项目进程及模式）、`lsof -nP -iTCP:5260 -sTCP:LISTEN`、`docker ps --format ...` | API 有监听；本项目无 Worker；MySQL 容器 healthy，仅本项目容器纳入结论 |
| `docker compose exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --raw -e "<只读 SQL>"'` | SHOW DATABASES、information_schema、计划/Run/Check/分析/积分 SELECT 均成功；不读取 key、凭据或原始响应值 |
| `python3 -` 文档本地链接、代码围栏、源码/路由事实及 hook 路径检查 | 33 份现役文档、139 个本地链接通过；10 份修改/新增文档中有 92 个本地链接，代码围栏与过期状态检查无错误；两个 hook 目标存在，项目平台目录无断软链 |
| `python3 -` 解析 `.codex/hooks.json` 与 `.vscode/launch.json` | 两份 JSON 均解析成功；删除候选四个项目文件均保留，`.local` 为空 |

DBX 连接发现因系统钥匙串不可读返回 `KEYRING_ACCESS_FAILED`；改用现有 MySQL 容器的只读查询完成数据核对，没有修改钥匙串、DBX 或全局配置。本轮仅文档/规则修改，**未运行 dotnet build/test、数据库回归、真实生文或邮件发送**。

### 收尾状态与遗留

| 事实面 | 状态 | 边界 |
|---|---|---|
| 代码 | verified-current | 与本次文档声明相关的路由、Worker 注册、模型常量、迁移、汇总及 reader 修复已核对；未宣称全部目标功能已验收 |
| 运行态 | pending | 本机健康、schema、既有每日轮次和查询已核验；Worker 未运行，源码部署 marker 与摄取修复运行验收缺失 |
| 文档 | changed-and-verified | 过期轮次待办、历史服务状态和现役指针已对齐；33 份现役文档的本地链接/结构门禁通过 |
| 规则 | changed-and-verified | 主规则改为权威指针，质量规范的构建范围与项目定向原则对齐；三个明确模板仍待补齐 |
| 记忆 | out-of-scope | 没有写授权长期记忆；Trellis 历史及宿主生成机制保持只读 |
| 工作区 | pending | 全量盘点完成，四个项目文件和本轮临时清单待确认删除；Git 分支/提交不适用，当前聊天没有附着 PR/worktree |

未消除的 warning：Worker 当前未运行；Run 汇总分析字段仍为常量；基础规范仍有模板。隔离 SQL、内容真实生文/账务/性能、验证码实际投递仍按各模块说明保持 pending。相邻前端、其他项目、其他环境发布、平台内部架构和全量历史任务治理为 out-of-scope。

## 2026-10-19 补充核对（文章管理页收尾）

只读核对时间：2026-10-19 00:45–01:05（Asia/Shanghai）。范围与 2026-10-11 相同，另加相邻前端预览进程；没有重启服务、执行迁移或改动业务数据。

- API 5260（PID 43780，2026-10-17 18:22 启动）`/health/live`、`/health/ready` 均为 200，但其 Swagger 不含 `ArticleListPage` 与 `article-monitoring/domain-rankings`：该进程仍是 2026-10-18 内容增量之前的构建，新前端 `/content` 对它会按 `fallbackStage` 估算阶段并提示「API 未更新」。
- Worker 以 `--MonitoringOnly=true` 运行（PID 74014，2026-10-16 09:15 启动），该模式不注册文章生成 Worker；真实生文链路（任务 `10-18-fe-content-articles` AC20）需要 `./scripts/run-local.sh worker` 的完整模式。
- 5280 上有一个 2026-10-17 01:11 以 `--no-build --no-launch-profile` 启动的临时 OpenAPI 实例（PID 45095/45072，含 `MonitoringRecordPage`、不含 `ArticleListPage`），属 `10-17-fe-model-answer` 遗留；2026-10-18 内容任务因此改用 5281 并已停止。
- 相邻前端 `Jogeoai.web` 的夹具预览进程：3003/5272（2026-10-15 23:51）、3004/5273（2026-10-17 09:35）、3002/5271 smoke 模式（2026-10-15 20:52）均早于 `tests/content-fixture.mjs`，不能用于文章页走查；本次使用并已停止 3005/5274。3001 为新前端开发服务器，3000 为旧 `Jogeoai.vue`。
- MySQL 容器可见非系统库：`jogeoai`（业务库）、`jogeoai_registration_20260929`（注册说明要求保留）、`jogeoai_content_test_20261018`（文章 SQL 测试隔离库，86 张表，待确认后清理）；`10-17` 的 `jogeoai_dashboard_test_20261017` 已不存在。
- 规则：根 `AGENTS.md` 新增新前端 / 旧前端只读边界一条；相邻 `Jogeoai.web/AGENTS.md` 指向本仓库 `.trellis/tasks/10-15-frontend-rebuild*`，两边一致。`.agents/skills` 与 `.claude/skills` 为 Trellis 平台生成物，差异不在本轮处理范围。
- 文档门禁：对本次改动的 18 份文档（含相邻前端 README / AGENTS）做本地链接、代码围栏与 JSON/JSONL 解析检查；修复了 `10-18-fe-content-articles/prd.md` 指向 `content-management.md` 的相对路径死链。

### 2026-10-19 新增的删除候选（未删除）

| 对象 | 理由 |
|---|---|
| `jogeoai_content_test_20261018`（MySQL 库） | 文章 SQL 测试隔离库，6 项用例已通过；再次运行可按 `verification.md` 重建。2026-10-19 已删除 |
| 5280 临时 API 进程（PID 45095，父 45072） | 2026-10-17 的 OpenAPI 生成实例，构建已过期，仅占用端口。2026-10-19 已停止，端口释放 |
| `Jogeoai.web` 旧夹具预览进程 3003/5272、3004/5273、3002/5271 | 早于当前夹具代码，走查不可用；归属其他会话，需确认后结束。2026-10-19 清场时这些进程已不存在（非本轮结束），端口全部空闲 |
| `/Library/Project/Jogeoai.Infrastructure/ArticleMaterialSelectionService.cs` | 项目外的同级目录里只有这一份 2026-09-29 10:36 的旧版本（缺 `management` 注入与 JSON 选项），仓库内 `src/Jogeoai.Infrastructure/` 的同名文件更新；疑为误写路径的残留，位于本项目写入范围之外。2026-10-19 经用户确认删除，随后为空的目录一并移除 |
| `/tmp` 下本轮脚本与日志（`nf_*.py`、`update_*.py`、`fix_counts.py`、`cf*.jar`、`content-fixture-*.log`、`web-readme-before.md`） | 文档批量编辑与夹具走查的一次性产物，可重建。2026-10-19 已删除（链接检查脚本在复跑门禁后删除） |

既往四项候选（两份鹈鹕演示、09-29 的 COMPLETION/DEPLOYMENT_SUMMARY）已于 2026-10-19 经用户确认删除。`dist/` 为新前端构建输出，已被其 `.gitignore` 排除，不列为候选。用户在看完 2026-10-19 汇报后确认清场，执行结果见下一节。

### 2026-10-19 清场结果

用户在看完 2026-10-19 汇报后确认清场，执行时间 2026-10-19 09:58–10:05。每个对象删除前再次只读核对了名称、内容或进程命令行。

| 对象 | 结果 |
|---|---|
| MySQL 库 `jogeoai_content_test_20261018` | `DROP DATABASE` 后库列表只剩 `jogeoai`（86 张表，未动）与 `jogeoai_registration_20260929`（注册说明要求保留） |
| 5280 临时 API 进程（45072 / 45095） | SIGTERM 后进程消失，5280 空闲 |
| `pelican-bicycle.html`、`pelican_bicycle.html` | 已删除，根目录不再有演示 HTML |
| 09-29 任务 `COMPLETION_SUMMARY.md`、`DEPLOYMENT_SUMMARY.md` | 已删除；任务目录剩 prd / design / implement / prompt-contract / jsonl / research / task.json，候选表链接改为纯文本 |
| `/Library/Project/Jogeoai.Infrastructure/ArticleMaterialSelectionService.cs` | 已删除，空目录一并移除；仓库内 `src/Jogeoai.Infrastructure/` 不受影响 |
| `/tmp` 一次性脚本、cookie jar 与夹具日志 | 已删除；2026-10-11 的 `/tmp/jogeoai-neat-file-inventory.txt` 在清场前已不存在 |
| 旧夹具预览进程 3003/5272、3004/5273、3002/5271 | 清场时已不存在（非本轮结束）；3001–3005、5271–5274、5281 全部空闲 |

清场后重新审计（只读）：

- API 5260 为新进程（PID 93360，2026-10-19 09:54:08 由用户以 `--launch-profile http` 启动，非本会话操作），`/health/live` 与 `/health/ready` 均 200；Swagger 含 `ArticleListPage`、`ArticleStage`、`ArticleDomainRankingPage`，`/articles` 列表参数含 `stage` / `language` / `ownedOnly`，`article-monitoring/domain-rankings` 路由存在且 summary 为中文，内容操作共 24 个。
- Worker 仍为 2026-10-16 启动的 `--MonitoringOnly=true` 进程，不注册文章生成 Worker；文章任务 `10-18-fe-content-articles` 的 AC20 真实生成仍未执行。
- 3000 现在由 `Jogeoai.web` 的 Vite 开发服务器占用（`vite.config.ts` 固定 3000、strictPort，页面标题 Jogeo AI）；旧前端 `Jogeoai.vue` 没有运行进程。记忆中「旧前端占 3000、新前端用 3001」的说法已同步修正。
- 没有误删仍含唯一改动的对象：被删文件均为演示、历史指针或旧副本，隔离库可按文章任务 `verification.md` 重建。
