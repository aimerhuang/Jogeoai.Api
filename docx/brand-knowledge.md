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

## 摄取重领上限与文件解析预算（2026-11-27，本机已部署）

Trellis `11-07-sec-pdf-budget`（[PRD](../.trellis/tasks/11-07-sec-pdf-budget/prd.md)、[技术设计](../.trellis/tasks/11-07-sec-pdf-budget/design.md)、[验证记录](../.trellis/tasks/11-07-sec-pdf-budget/verification.md)）。适用于所有经过 `KnowledgeIngestWorker` 的资料：条目同步出的文档、仍保留的文件上传接口，以及产品识别读取来源文件。没有迁移，没有接口变化。本机 API 与 Worker 已于 2026-11-27 22:07 用新构建重启，验证范围见本节末尾的表。

### 重领上限

摄取 Worker 是所有租户共用的单循环，一次处理一个任务，没有心跳。任务每被领取一次，`attempt_count` 加一。

- 领取时，如果选中的任务是 `running`（说明租约已过期：上一次执行中断，或没有在租约内写回结果）并且 `attempt_count` 已经是 3，就不再执行：在领取事务内把任务记为 `failed`、清空租约、写完成时间；版本记为 `failed`（版本已经是 `ready` 时不改，见下）；资料在没有已发布版本时记为 `failed`，已有已发布版本时保持 `ready`。错误码 `KNOWLEDGE_INGEST_INTERRUPTED`，提示「资料处理多次中断，请重新上传。」。这次领取返回空，Worker 2 秒后领取下一个任务。
- 发布时逐条语句判断租约，租约恰好在「版本写成 `ready`」与「任务写成 `done`」之间过期时，会留下已发布的版本和一个 `running` 任务。这种任务达到上限被放弃时只把任务记为失败，已发布的版本和资料保持 `ready`（2026-11-27 评审时补上的条件；该用例已写进 SQL 测试，并已在本机隔离库通过）。
- `attempt_count` 为 2 的过期任务仍会被重领，这是它的第 3 次、也是最后一次执行。
- 用户对失败版本调用重试接口时，任务按待处理重新排队，不受上限限制；但次数不清零，所以重试后的任务只有一次执行机会，再被中断就会在下次领取时失败。
- 条目同步出的文档不能调用重试接口（409 `KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM`）；这类文档摄取失败后，编辑条目会生成新版本和新的摄取任务。
- 产品识别任务（`ProductParseWorker`，2 分钟租约加 30 秒心跳）同样处理：错误码 `PRODUCT_PARSE_INTERRUPTED`，提示「识别多次中断，请重新发起。」，产品变为「识别失败」，可以重新发起识别。
- 摄取租约由 2 分钟改为配置项 `Knowledge:Ingest:LeaseSeconds`（环境变量 `Knowledge__Ingest__LeaseSeconds`），默认 300 秒，允许 30–3600，不合法时 Worker 启动失败。Worker 崩溃后，正在处理的任务要等租约到期才会被重领。

### 解析预算

配置节 `Knowledge:Extraction`（环境变量形如 `Knowledge__Extraction__MaxPdfPages`）。API 与 Worker 共用同一份注册，不配置时使用默认值；配置超出允许范围时两个进程都启动失败，不会静默修正。实际调用解析的只有 Worker。

| 键 | 默认 | 允许范围 | 行为 |
|---|---|---|---|
| `MaxPdfPages` | 1000 | 1–100000 | 打开 PDF 后、解析任何页之前检查页数，超过返回 `KNOWLEDGE_PDF_TOO_MANY_PAGES` |
| `MaxCharacters` | 10000000 | 1–100000000 | PDF 逐页累计正文字符，超过就停止并返回 `KNOWLEDGE_CONTENT_TOO_LARGE`；其他格式在解析完成后检查（与此前相同） |
| `MaxImagePixels` | 40000000 | 1–1000000000 | 只约束需要解码的图片。扫描件取页图时，按图片声明的宽 × 高在解码之前判断；超过上限、又需要解码成 PNG 的图片不解码、不参与「每页最大图」的选择，结果标记为截断。原始字节就是 JPEG 的图片不解码、原样送出，不受此项约束，仍只受单张 10 MB 的字节上限约束 |
| `ParseTimeoutSeconds` | 90 | 1–3600 | 四种格式的解析都在线程池执行，调用方最多等这么久，超时返回 `KNOWLEDGE_PARSE_TIMEOUT`「文件解析超时，请拆分后重新上传。」 |

- 这些错误都不重试，Worker 在租约内把任务记为失败，不进入重领。
- 取消令牌贯穿 PDF 与 DOCX 解析，在页与页、图片与图片、段落与段落之间检查。Worker 关机时的取消照常作为取消传出，不会被记成解析失败或超时。
- 产品识别读取来源文件时沿用原有映射：上述错误显示为 `PRODUCT_PARSE_SOURCE_UNREADABLE`，提示文字不变。
- 像素上限只管解码。页图有两条路径：原始字节以 JPEG 文件头开始的图片（DCT 编码的扫描页）不解码，原样作为 `image/jpeg` 送给模型，和此前一样只受 10 MB 字节上限约束，声明像素再大也照常送出（600 dpi 的 A3 扫描约 7000 万像素）；其余图片要解码成 PNG，声明像素超过上限的在解码前跳过并标记截断。声明像素超限的图片一律不调用解码。
- 页图全部被跳过、又没有文本层的扫描件，结果是 `KNOWLEDGE_EMPTY_CONTENT`「文件未解析出正文。」，没有单独的像素错误码。
- 是否「原样送出」按图片流原始字节的 JPEG 文件头判断，不检查 PDF 里声明的编码方式；原样送出的字节不在本进程解码。

### 仍然存在的风险

- **进程内超时不能强制中止解析。** PDF 库卡在单个页面内部（或卡在打开文件阶段）不返回时，超时只让 Worker 放弃等待并把任务记为失败。那个线程继续占用一个 CPU 核心和该文件的内存，直到 Worker 进程重启；连续遇到多个这样的文件会叠加。本次评估后没有改为独立进程解析。
- **没有内存预算。** 100 MiB 的输入上限不变；压缩流在页内解压占用的内存没有上限，`deploy/compose.yml` 也没有容器内存上限。Worker 被系统杀掉后会自动重启，重领上限保证同一个文件最多被执行 3 次。
- **合法的大文件可能失败。** 解析在超时内完成、但分块和向量化超过剩余租约的文件，会在 3 次执行后失败。此前是无限重领，同样不会成功，而且会挡住其他租户。
- **被中断 3 次的任务需要用户处理。** 例如 Worker 连续重启时正在处理的文件，此前会一直重领直到成功，现在记为失败，需要重试或重新上传。本机 `knowledge_ingest_jobs` 现有 6 行全部 `done`，`attempt_count` 最大为 4；多出来的领取来自租约过期还是人工重试，只读查询无法区分，前一种情况在新规则下会失败。
- `ParseTimeoutSeconds` 不小于摄取租约时，慢文件只能靠重领上限在 3 次后失败；Worker 启动时记一条警告，不阻止启动。
- **超时只覆盖解析。** 从对象存储把文件读进内存发生在解析之前，不计入 `ParseTimeoutSeconds`，只受租约和关机取消约束；本机是本地磁盘，S3 存储下读取慢的文件会占用租约时间。默认 300 秒租约减去 90 秒解析超时，剩余时间要容纳读取、分块、向量化和发布。

### 验证状态（2026-11-27）

| 项目 | 结果 |
|---|---|
| 解决方案构建 | 0 错误 |
| 解析预算定向测试 | `KnowledgeAdapterTests`、`BasicDocumentTextExtractorImageTests` 共 41 项通过（连续 3 次）；样本 PDF 由测试代码生成 |
| 不连库测试合并运行 | 六个安全子任务涉及的 33 个不连库测试类合并运行 3 次，每次 845 项通过、0 失败。合并运行发现调用方取消时被放弃的 PDF 解析没有被取消，`KnowledgeAdapters.cs` 已改为显式取消，上述结果是修复之后的 |
| 重领上限 SQL 测试 | 已在本机隔离测试库（已执行 001–052）运行：`KnowledgeStoreSqlTests` 与 `KnowledgeProductStoreSqlTests` 共 6 项通过、0 失败，其中包含 4 个新的重领上限用例 |
| 默认上限与真实样本 | 未核对：本机 `knowledge_document_versions` 只有 5 个 `.md`（最长 1699 字符）和 1 个 `.png`，没有 PDF，也没有 `page_count` |
| 恶意 PDF、内存占用 | 未构造真实的恶意 PDF，未实测内存 |
| 本机部署 | API 与 Worker 已于 2026-11-27 22:07 用新构建重启，健康检查为 200；Worker 重启后运行一分多钟日志没有错误，摄取和产品识别循环按新的领取语句轮询；知识库资料列表接口经 Development 的 `X-Tenant-Id` 兼容桥返回 200。重领上限和解析上限没有在运行态触发过 |

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
