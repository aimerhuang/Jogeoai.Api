# 素材库产品信息：使用、实现与验收状态

> **2026-11-03 起页面已被替代**：「品牌信息」页改为按维度的 [品牌知识库](brand-knowledge.md)，「文件资料 / 产品信息」两页签和对应前端已删除；本机 `jogeoai` 已执行 043，3188DH 已迁为「产品/服务」条目。下列产品与文件上传接口保留但在 OpenAPI 中标记为 deprecated，已迁移产品的写操作返回 409 `PRODUCT_MIGRATED`。本文保留为历史记录。

核对日期：2026-10-19。本文是素材库「文件资料 / 产品信息」当前接口、机制和本机状态的现役入口；[PRD](../.trellis/tasks/10-19-knowledge-product-library/prd.md)、[技术设计](../.trellis/tasks/10-19-knowledge-product-library/design.md) 与 [验证记录](../.trellis/tasks/10-19-knowledge-product-library/verification.md) 保留完整目标和证据。源码已实现；本机 `jogeoai` 已于 2026-10-19 执行 023，5260 API 与完整 Worker 已重启为新构建，并于同日完成真实 `completion` 与 `url` 识别验收（见下表）。

## 当前实现与使用

产品信息是品牌（Site）下的独立实体，不复用文档上的 `productKey` 字符串。三种录入方式：

1. 上传文件（新增支持 `.jpg .jpeg .png`，沿用 `.pdf .docx .md .txt`）后「识别为产品信息」。
2. 手动填写产品名、关联品牌、产品介绍（必填）、优势、适用场景与人群、客户案例、规格参数（≤ 50 项）、产品页面网址、标签；可用「AI 智能补全」按产品名与品牌知识库补全空字段。
3. 添加产品详情页网址：服务端用文章导入同一套公开网址安全读取规则抓取正文，存为 `.md` 链接资料（`sourceKind=link`），再发起识别。

识别任务由 Worker `ProductParseWorker` 领取（2 分钟租约、心跳、最多 3 次重试），调用研究 Provider 配置中的 `openai-compatible` 网关与固定模型 `gpt-6-luna`。文本资料送文本；JPG / PNG 和抽取文本不足 200 字的 PDF 以 OpenAI 兼容的 `image_url` 内容块送图片（单张 ≤ 10 MB、最多 20 张）。模型只产出逐字段草稿，每个字段带来源片段或「未找到」标记，产品进入「待核验」。识别不消耗积分，每租户每日 50 次。

核验入库要求所有 AI 识别且已找到的字段逐个确认（`verifiedFields`），入库后按固定 Markdown 模板写入品牌知识库（`documentType=product`、`productKey=<产品 UUID>`、`sourceKind=product`），进入 RAG 检索；文章生成 `article-generation/options` 新增 `products`（仅已入库），`material-preview` 的 `productKey` 可传产品 UUID。归档产品停用对应资料。

## 当前 HTTP 接口

品牌级路径均位于 `/api/v1/sites/{siteId}`：

| 方法 | 路径 | 用途 |
|---|---|---|
| GET / POST | `/knowledge/products` | 列表（`status` / `sourceKind` / `q` / 游标，返回 `KnowledgeProductListPage` 含 `pendingReviewCount`）/ 创建草稿 |
| GET / PATCH | `/knowledge/products/{productId}` | 详情（含 `latestJob`）/ 保存草稿；已入库产品编辑后重新同步知识库 |
| POST | `/knowledge/products/{productId}/parse` | 发起识别（`kind=file|url|completion`），202 返回 `ProductParseJobView` |
| GET | `/knowledge/products/{productId}/parse-jobs/{jobId}` | 轮询任务 |
| POST | `/knowledge/products/{productId}/parse-jobs/{jobId}/cancel` | 取消排队中的任务 |
| POST | `/knowledge/products/{productId}/approve` | 核验入库（`fields` + `verifiedFields`） |
| POST | `/knowledge/products/{productId}/reject` | 驳回回草稿 |
| POST | `/knowledge/products/{productId}/archive` | 归档 |
| POST | `/knowledge/products/{productId}/sync-knowledge` | 重试写入知识库 |

权限沿用内容管理：真实会话 + 租户成员；viewer 只读；member 只能修改自己创建的产品；owner / admin 不限。错误码：`PRODUCT_NOT_FOUND`、`PRODUCT_NAME_CONFLICT`、`PRODUCT_VALIDATION`、`PRODUCT_PARSE_IN_PROGRESS`、`PRODUCT_PARSE_RATE_LIMITED`（429）、`PRODUCT_PARSE_IMAGE_UNSUPPORTED`、`PRODUCT_PARSE_INVALID_OUTPUT`、`PRODUCT_PARSE_RETRYABLE`、`PRODUCT_FIELDS_UNVERIFIED`、`PRODUCT_STATE_CONFLICT`、`KNOWLEDGE_LINK_UNREADABLE`（422）。`POST /knowledge/links` 已于 2026-11-07 下线（安全修复 C1）；产品网址识别仍在入队前内部登记链接资料。字段与响应以运行环境 Swagger 和 [领域 DTO](../src/Jogeoai.Domain/KnowledgeProductModels.cs) 为准。

## 数据与迁移

[023 migration](../db/migrations/023_knowledge_products.sql) 只做加法、可重复执行：`knowledge_documents` 加 `source_kind` / `source_url` / `source_url_hash`，`knowledge_document_versions` 加 `content_kind`（图片版本允许 0 片段发布），新增 `knowledge_products`、`knowledge_product_tags`、`knowledge_product_parse_jobs`（租约表，`active_key` 生成列保证同一产品只有一个进行中任务）。新构建的 API / Worker 读取这些列，目标库必须先执行 023 再部署；旧构建忽略新表。

## 验证状态（2026-10-19）

| 项目 | 结果 |
|---|---|
| 解决方案构建 | 通过 |
| 后端定向测试 | 91 通过、1 跳过（`KnowledgeProductStoreSqlTests` 需 `JOGEOAI_KNOWLEDGE_TEST_CONNECTION` 隔离库） |
| Swagger（临时 5281 实例） | 10 条产品 / 链接路由与新 DTO 均为中文说明 |
| 前端 | `vue-tsc`、118 项定向测试、生产构建、prettier 通过；类型由 5281 的 OpenAPI 离线生成 |
| 浏览器走查 | 夹具 `jogeoai-web-fixture-knowledge`（3006 / 5275）走通上传弹窗、添加链接、新建产品、从 JPG 识别、逐字段核验入库、向导指定产品 |
| Luna 多模态探测 | 2026-10-19 早些时候网关对本机所有请求返回 nginx 403，原因是本机 Clash 全局/fake-ip 代理出口被网关拒绝（直连真实 IP 时同一凭据返回 200）；代理调整后恢复 |
| 023 执行 | 本机 `jogeoai` 2026-10-19 备份后执行并重放验证；其他库未执行 |
| 真实链路 | PNG 经 5260 上传、Worker 发布为 `content_kind=image` 0 片段版本（需 `.env` 显式共享 `Knowledge__StorageRoot/VectorRoot`）；产品识别已用真实登录会话验收：`completion` 任务 done（gpt-6-luna，6.4 s）；`url` 任务读取 szulink 产品页后 done（43 s），产品进入 `pending_review`，填入介绍、优势与 14 条规格。`url` 首次失败「重定向次数过多」系 `ArticleSourceReader` 抓取时误用去 `www.` 的去重规范化地址，已改为 `NormalizeFetchUrl` 保留主机名，并补 2 个单元测试 |

夹具走查只证明页面与合同一致，不能证明后端链路；真实验收以本机执行 023、重启 API / Worker 并完成一次真实识别为准。
