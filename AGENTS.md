# Jogeoai 项目指引

## 定位
Jogeoai 是 AI 可见性服务的 .NET 后端，提供采集、分析、品牌与内容管理，可本机调试。
现状和操作入口以 [README.md](README.md) 为准；[底层设计](docx/cloro-foundation-design.md) 是后续实现目标。

## 启动与验证
- 用户长期偏好（2026-10-28）：采集任务直接执行，不创建 Trellis 任务；不再逐次询问是否建任务。此偏好仅适用于运行采集，不扩展到新功能开发或采集器代码修改；人工验证码、权限审批和破坏性操作仍按各自规则处理。
- 首次运行 `./scripts/setup-local.sh`，需要 .NET 10 SDK 和正在运行的 Docker。
- API：`./scripts/run-local.sh api`；Worker：`./scripts/run-local.sh worker`。
- 验证：`dotnet build Jogeoai.slnx`，以及 `http://localhost:5260/health/live`、`/health/ready`。
- 只测试本次新增或修改的功能及其直接受影响的行为，优先运行对应测试类、测试用例或受影响项目的测试；不要每次默认运行全局测试套件。只有跨项目公共契约、共享基础设施、解决方案配置、发布流程变化，或定向测试无法覆盖明确的跨模块风险时，才扩大测试范围，并记录原因。纯文档或规则修改不运行代码测试；每次提交记录实际执行的命令和结果。
- 数据库 migration 或集成测试完成后清理不再需要的临时测试表/测试库；清理前列明对象并确认不涉及共享开发库、业务数据或命名数据卷，取得确认后再删除。
- 对外公开的 Controller、DTO、枚举和接口成员使用中文 XML 注释，确保 Swagger UI 中的接口说明为中文。
- VS Code 调试入口在 `.vscode/launch.json`；启动前检查 5260 端口是否已被现有 API 占用。

## 技术栈
- ASP.NET Core Controllers、OpenAPI、BackgroundService、MySQL 8.4。
- 业务 Store 与连接探测使用 MySqlConnector 和显式 SQL；SqlSugarCore 已引用，尚未用于实体映射或仓储。
- `global.json` 使用 .NET 10 SDK 的 `latestFeature` 滚动策略。

## 目录与约定
- `src/Jogeoai.Api`：HTTP 入口；`src/Jogeoai.Worker`：后台服务。
- Domain、Application、Infrastructure 与 Providers 项目按各自层次扩展。
- `docx/` 存放当前功能说明与目标设计，`scripts/` 存放可重复使用的本地启动脚本。
- `references/elmo/` 仅为只读参考，不加入解决方案、不安装依赖或套用其项目规则。
- 新前端在相邻的 `/Library/Project/Jogeoai.web`（约定见其 AGENTS.md，规划与验收记录在本仓库 `.trellis/tasks/10-15-frontend-rebuild*`）；旧前端 `/Library/Project/Jogeoai.vue` 只读参考，不修改。后端公开契约变更后需重新生成前端类型并同步两边 README。
- `.env` 和 User Secrets 保存本地配置；不将密钥、连接串值或原始敏感响应写进文档与日志。
- 修改数据库配置时同步 `.env` 的 `MYSQL_*` 和连接串；已有数据卷不会因环境变量变化重建账号。
- 不把设计文档中的目标内容直接描述为已实现；只有当前源码、已执行 migration 或真实运行态验证过的 `/api/v1`、任务状态和迁移才能写入现状说明。

## 当前状态与下一步
- 模块实现与迁移入口见 [README](README.md)，带日期的服务快照见 [本机状态核对](docx/local-state-verification.md)。API 健康、Worker 进程存在、时隙 `completed`、回答成功和积分结算分别验证，不能相互替代；接口定义存在也不代表端到端验收通过。
- 邮箱验证码与五步引导见 [注册引导说明](docx/registration-onboarding.md)；SMTP 登录成功不等于验证码实际投递。引导的同步快速推荐不依赖研究 Worker，提问弹窗的 `brand-research` 仍依赖。知识库依赖 012/013，内容管理依赖 014，未完成需求与验收见 [内容管理说明](docx/content-management.md)。
- 品牌信息页依赖 043，须先迁移再重启新构建的 API/Worker；本机网址读取的开发环境开关 `ArticleSource:AllowFakeIpRange`（Clash fake-ip）见 [品牌知识库说明](docx/brand-knowledge.md)。
- 采集器账号池依赖 035，必须用 root 执行，见 [账号池说明](docx/collector-account-pool.md)。MCP 只读访问依赖 044，先迁移再发布 API，见 [MCP 访问说明](docx/mcp-access.md)。
- 品牌监控「回答模式 × 采集频率」依赖 047，须先迁移再同时发布 API/Worker，见 [品牌监控配置说明](docx/brand-monitoring-config.md)。团队默认监控配置依赖 048，先迁移再发布 API，见 [团队默认监控配置说明](docx/team-monitoring-config.md)。本机验收、配置版本和未验证项只在 README / 模块说明保留，不在规则中重复快照。
- App 渠道采集依赖 049（可以用应用账号执行，不需要 root）。须先在目标库执行迁移，再同时发布 API 和 Worker；两者的 `Collector:AppTenantIds` 默认为空，即不对任何租户开放。本机 `jogeoai` 已于 2026-11-17 执行 049 并重启 API / Worker。见 [App 渠道说明](docx/app-channel.md)。
- Cloro key 池依赖 022；目标库须先执行迁移再发布 API/Worker，运维及回滚见 [Cloro key 池说明](docx/cloro-key-pool.md)。迁移与本机验收不能推导其他运行库已部署。
- 下一步按各模块待验收项补 API + MySQL + Worker 端到端证据；已有真实轮次、隔离 SQL、性能和完整 PRD 验收分别记录。
- 收尾时同步现状文档；清理数据卷或会话残留前列明对象并取得确认。
