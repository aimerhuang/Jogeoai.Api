# 品牌知识库（品牌信息）：使用、实现与验收状态

核对日期：2026-11-03。本文是「品牌信息」页（品牌知识库）当前接口、机制和本机状态的现役入口；[PRD](../.trellis/tasks/11-03-brand-knowledge-dimensions/prd.md)、[技术设计](../.trellis/tasks/11-03-brand-knowledge-dimensions/design.md) 与 [验证记录](../.trellis/tasks/11-03-brand-knowledge-dimensions/verification.md) 保留完整目标和证据。源码已实现；本机 `jogeoai` 已于 2026-11-03 执行 043，5260 API 与完整 Worker 已重启为新构建并完成真实链路验收（网址识别成功路径除外，见文末）。它替代了 [素材库产品信息](knowledge-product-library.md) 的「文件资料 / 产品信息」两页签。

## 当前实现与使用

品牌知识是品牌（Site）下的条目，每条 = 维度 + 标题（1–100 字）+ 内容（1–2000 字）+ 来源。维度固定 6 个：`brand_intro` 品牌介绍、`selling_point` 核心卖点、`audience` 目标人群、`differentiator` 差异化优势、`product_service` 产品/服务、`faq` 常见问题。每品牌最多 50 条（所有维度合计，全套餐统一）；同品牌同维度标题不能重复（忽略大小写、全半角与多余空白），删除为软删除，删后可再用同名。

录入方式：

1. 「添加知识」弹窗手动填写，可「保存并继续添加」（保留维度、清空标题内容）。
2. 「品牌介绍」为空时可「从品牌资料导入」：预填品牌在团队管理里的介绍，确认后才保存；品牌没有介绍时按钮禁用。
3. 「从网址识别」：服务端先用文章导入同一套公开网址安全读取规则直接抓取正文；直接读取失败或正文不足 200 字、且网址是公开域名（非 IP、localhost、`.local`/`.internal`、单段主机名）时，改由 Luna Responses API 的 `web_search` 联网打开该网址读取（最长 120 秒，失败自动重试 1 次），只有引用来自同一网站（忽略 `www.`）才采信；两种方式都失败时提示同时给出两边原因。读到正文后，调用研究 Provider 配置的 `openai-compatible` 网关与固定模型 `gpt-6-luna`，按维度拆成最多 30 条候选，每条带原文依据（策略 `knowledge-extract-v3`：逐维度拆分指引，「品牌介绍」只出 1 条综合介绍（标题为名词短语、不写成问句，同批多出的品牌介绍候选在标准化时丢弃），产品页通常是 1 条「产品/服务」概述加多条「核心卖点」，「差异化优势」只收页面自述的区别能力，品牌介绍以外的不同事实不得合并）；依据不是网页正文原句的候选直接丢弃。与已有条目重复的候选标记「可能重复」、默认不勾选。用户勾选、就地修改后批量保存，保存在一个事务内完成（超额或重名时一条不写）。候选保留 24 小时；网页正文不入库、不进检索。识别不扣积分，每租户每日 50 次，与产品识别共用额度；每品牌同一时间只有一个未处理的识别任务。

条目由 Worker `KnowledgeItemSyncWorker` 单写为知识库文档（`source_kind=item`、`document_type=<维度>`、`product_key=<条目 UUID>`），编辑在同一文档上新增版本，删除则停用文档；再由 `KnowledgeIngestWorker` 摄取后参与检索。同步连续失败 5 次后停止自动重试，卡片显示「未能进入知识库，编辑后会重试」，编辑或删除会重置。网址识别任务由 `KnowledgeExtractWorker` 领取（2 分钟租约、心跳、最多 3 次）。

条目的使用：

- **写稿**：知识上下文检索包含条目；`article-generation/options.products` 列出「产品/服务」条目（`productId` 为条目 UUID）。指定产品时先按条目解析，找不到再回退到未迁移的已入库旧产品；已删除条目返回 404 `PRODUCT_NOT_FOUND`「指定产品不存在或已删除」。
- **AI 推荐监测问题**：`brand-research` 入队时读取条目（品牌介绍、核心卖点、目标人群、差异化优势、产品/服务、常见问题的优先顺序，最多 12 条、单条 1200 字、合计 8000 字），作为 `knowledge:<条目 ID>` 证据冻结进任务上下文；条目变化时重新建立品牌画像（策略版本 `industry-v3`）。没有官网、没有业务描述但有条目的品牌也能发起推荐。

页面：侧边栏「品牌信息」→ `/knowledge`。标题显示「品牌知识库 (已用/50)」，维度筛选写入 `?dimension=`；viewer 只读，member 只能编辑、删除自己创建的条目，owner / admin 不限。旧地址 `/knowledge/products/*` 与 `?tab=` 重定向到 `/knowledge`。

## 当前 HTTP 接口

均位于 `/api/v1/sites/{siteId}`：

| 方法 | 路径 | 用途 |
|---|---|---|
| GET / POST | `/knowledge/items` | 列表（`?dimension=`；返回全部条目、6 维度计数、`total`、`limit=50`）/ 新增 |
| PATCH / DELETE | `/knowledge/items/{itemId}` | 编辑（带 `expectedVersion`）/ 删除 |
| POST | `/knowledge/extract-jobs` | 发起网址识别（`url`、可选 `dimensions`），202 |
| GET | `/knowledge/extract-jobs/current` | 当前未处理的任务（含 24 小时内未处理的失败任务）；没有则 204 |
| GET | `/knowledge/extract-jobs/{jobId}` | 轮询 |
| POST | `/knowledge/extract-jobs/{jobId}/cancel` · `/save` · `/discard` | 取消 / 保存勾选的候选（201，返回新条目）/ 放弃 |

错误码：`KNOWLEDGE_ITEM_NOT_FOUND` 404、`KNOWLEDGE_ITEM_VALIDATION` 400、`KNOWLEDGE_ITEM_TITLE_CONFLICT` 409、`KNOWLEDGE_ITEM_LIMIT` 409、`KNOWLEDGE_ITEM_STALE` 409、`KNOWLEDGE_EXTRACT_NOT_FOUND` 404、`KNOWLEDGE_EXTRACT_IN_PROGRESS` 409、`KNOWLEDGE_EXTRACT_NOT_READY` 409、`KNOWLEDGE_EXTRACT_STATE_CONFLICT` 409、`KNOWLEDGE_EXTRACT_EXPIRED` 410、`KNOWLEDGE_EXTRACT_RATE_LIMITED` 429、`KNOWLEDGE_EXTRACT_NOT_CONFIGURED` 503（发起前即检查，不占额度）；任务内失败码 `KNOWLEDGE_LINK_UNREADABLE`、`KNOWLEDGE_EXTRACT_SOURCE_TOO_SHORT`、`KNOWLEDGE_EXTRACT_INVALID_OUTPUT`、`KNOWLEDGE_EXTRACT_RETRYABLE`、`KNOWLEDGE_EXTRACT_TIMEOUT`、`KNOWLEDGE_EXTRACT_FAILED`。字段以运行环境 Swagger 和 [领域 DTO](../src/Jogeoai.Domain/BrandKnowledgeModels.cs) 为准。

旧接口：「添加链接」`POST …/knowledge/links` 已于 2026-11-07 下线（原实现先抓取网址后鉴权）；产品信息全部接口与知识库文件上传接口在 OpenAPI 中标记为 deprecated，读接口照常可用；对已迁移为条目的产品，7 个写接口返回 409 `PRODUCT_MIGRATED`；知识库文档的修改、重试、停用和上传新版本对条目文档（`source_kind=item`）返回 409 `KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM`，条目文档只能由同步 Worker 维护。新前端已不再调用这些旧接口。归档品牌的条目不能编辑或删除（404 `RESOURCE_NOT_FOUND`），其未完成的识别任务在领取时直接失败。

## 数据与迁移

[043 migration](../db/migrations/043_brand_knowledge_items.sql) 新增 `brand_knowledge_items`、`brand_knowledge_extract_jobs`，唯一的非加法改动是让 `knowledge_documents.source_kind` 允许 `item`。数据段把每个 `approved` 产品迁为一条「产品/服务」条目：**沿用原产品 UUID**、接管原产品文档（`source_kind=item`、`document_type=product_service`，`product_key` 不变），因此已冻结的素材预览和文章快照无需改动；内容按「介绍 / 优势 / 适用场景与人群 / 客户案例 / 规格参数」拼接，超过 2000 字截断。草稿、待核验、已归档产品和同名冲突的产品不迁移。迁移末尾输出 5 份报告（各品牌计数、截断条目、未迁移产品、活跃上传文件、标题含全角字符的迁移条目）。043 可重复执行。

部署顺序：先在目标库执行 043（`--default-character-set=utf8mb4`），再发布 API 与 Worker，最后发布前端。新构建的品牌研究入队、产品识别频控与旧产品写接口都会读取新表，未执行 043 就重启会报错。回滚见 [rollback/043](../db/migrations/rollback/043_brand_knowledge_items_rollback.sql)：存在 `source_kind=item` 的文档时拒绝执行，不删除新表数据。

## 验证状态（2026-11-03）

| 项目 | 结果 |
|---|---|
| 后端构建与定向测试 | 构建 0 错误；A–E 阶段定向测试 225 通过、0 失败、2 跳过（需真实网关 / 另一任务专用库），连续 3 次稳定；共用隔离库的 SQL 测试类归入串行集合 `Knowledge isolated SQL` |
| 前端 | 定向测试 93/93、`vue-tsc -b`、生产构建、prettier 通过；类型由临时 5281 实例的 OpenAPI 生成 |
| 043 隔离库 | 空库与带种子库各执行两次、回滚（有 / 无条目两种情况）均按预期；实测发现并修正 `check_clause` 判断（MySQL 存为 `\'item\'`） |
| 夹具走查 | `jogeoai-web-fixture-knowledge`（3006/5275）、`…-knowledge-viewer`（3016/5286）、`…-knowledge-unreadable`（3017/5287）走通列表、筛选、导入、增删改、网址识别全流程、失败重试、只读角色、旧路由 |
| 本机部署 | 备份 `~/jogeoai-backups/jogeoai-pre043-20261103-183012.sql.gz` 后执行 043：优联 3188DH 迁为条目并同步为文档第 2 版；3188DH 网页链接资料经用户同意停用 |
| 真实链路 | 检索 3188DH 只命中条目文档；新增 / 编辑 / 删除分别 4 / 4 / 2 秒反映到检索；乐观锁冲突返回 `KNOWLEDGE_ITEM_STALE`；无官网研究在有品牌介绍条目时生成 10 个问题并引用条目；写稿 `options.products` 与指定产品的素材预览正确；旧产品写接口返回 `PRODUCT_MIGRATED`；真实页面在窄屏与桌面宽度显示正常 |

**网址识别（2026-11-03 晚补充）**：本机 Clash fake-ip 曾使所有域名解析到 198.18.x、被网页读取器按内网地址拒绝。现有两层处理：① 开发环境专用开关 `ArticleSource:AllowFakeIpRange`（仅 `Development` 生效，API 与 Worker 的 `appsettings.Development.json` 已打开，照搬 `SiteIcons:AllowFakeIpRange`；10.x、127.x 等真实内网仍拒绝），本机可直接读取网页，同时修复写稿工作室与品牌研究在本机抓取网页失败；② 上述 AI 联网读取兜底，用于正式环境被站点拦截的情况。真实验收：4188KH 产品页直接读取 1509 字，28 秒完成，得到 2 条带原文依据的「产品/服务」候选（未替用户保存）。此前仅开 AI 兜底时，模型 `open_page` 对该站点间歇性超时、无引用，任务按规则失败，因此兜底不保证成功。真实文章生成（扣积分）未执行。其他运行库未执行 043。

本机当前状态：优联只有 3188DH 一条知识；`sites.description` 为空，所以真实环境中「从品牌资料导入」对优联为禁用状态。验收数据、临时会话和隔离库已于同日清理。
