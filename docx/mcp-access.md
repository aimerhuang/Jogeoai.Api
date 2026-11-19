# MCP 只读访问（采集数据查询）

> 现状（2026-11-04）：源码已实现，迁移为 [044](../db/migrations/044_mcp_access_tokens.sql)。本机 `jogeoai` 已于 2026-11-04 备份（`~/jogeoai-backups/jogeoai-pre044-20261104-134526.sql.gz`）后用 root 执行 044（重放两次无副作用），5260 API 已重启为新构建并完成真实验收（见下文）。其他运行库未部署。
>
> 需求与设计：`.trellis/tasks/11-04-collection-mcp-endpoint/`（父任务 `11-04-split-deploy-collection-mcp`）。

外部 bot（Grok Bot、Claude 等 MCP 客户端）用租户签发的只读令牌，通过 MCP 查询品牌、监测提问、监测记录（回答原文、引用、搜索词）以及可见性、声量、引用域名汇总。**第一期只读**：MCP 不能写入采集结果、不能创建任务，也不能修改配置；bot 写入另立任务。

## 接入

- 端点：`POST {API}/mcp`，Streamable HTTP 无状态模式（`ModelContextProtocol.AspNetCore` 2.3.1）。本机为 `http://localhost:5260/mcp`。
- 请求头：`Authorization: Bearer jgm_…`。MCP Inspector 选 “Streamable HTTP”，填 URL 并加该请求头。
- `/mcp` 只认 MCP 令牌：浏览器 Cookie、用户会话 Bearer 都返回 401；MCP 令牌也不能访问 `/api/v1`。
- `/mcp` 不出现在 Swagger / OpenAPI。

## 令牌管理（`/api/v1/mcp-tokens`）

只有当前团队的 `owner` / `admin` 可调用，且必须有真实登录会话（开发环境的 `X-Tenant-Id` 兼容桥不适用）。

| 操作 | 说明 |
|---|---|
| `POST` | `{ name (1–100), expiresInDays (1–365), siteIds? }`。`siteIds` 省略表示团队全部品牌（含以后新建的），最多 50 个且必须属于本团队，不能传空数组。明文令牌只在响应中返回一次。 |
| `GET` | `{ items, activeCount, activeLimit }`，最多 200 行，启用的排在前面；不含明文或哈希。 |
| `DELETE /{id}` | 吊销，幂等 204；未知或其他团队的 ID 返回 404 `MCP_TOKEN_NOT_FOUND`。 |

- 每个团队最多 20 个启用令牌（未吊销且未过期），超过返回 409 `MCP_TOKEN_LIMIT_REACHED`。
- 库内只保存 SHA-256 哈希和前 12 个字符（`jgm_` + 8 位，用于识别）；明文和哈希都不写日志。
- 令牌只检查团队状态；签发人失去管理员身份或离开团队后，令牌仍然有效，需要管理员手动吊销。
- 前端暂无令牌管理页面，本期用 Swagger 或脚本调用。

## 工具

| 工具 | 说明 |
|---|---|
| `list_sites` | 令牌范围内的品牌：siteId、名称、主域名、状态、时区 |
| `list_prompts` | 监测提问及表现，分页 |
| `get_monitoring_summary` | 监测概览 |
| `list_monitoring_records` | 监测记录分页，可按 `provider` / `engine` / 提问过滤 |
| `get_monitoring_record` | 单条记录：回答原文（超过 20,000 字符截断，标 `answerTruncated` / `answerLength`）、引用 URL + 标题（最多 100 条）、搜索词、品牌排名与曝光分 |
| `get_visibility` | 可见性趋势与分片，可按 `provider` / `engine` 过滤 |
| `get_share_of_voice` | 声量，`topN` 1–50（默认 10） |
| `list_citation_domains` | 引用域名汇总，分页 |

- 日期：`yyyy-MM-dd`（按品牌时区，结束日包含当天）或带时区的 ISO 时间；默认最近 7 天，最长 90 天。分页 1–50，默认 20。
- 分页工具返回 `window.snapshotToken`，翻页和查详情时传回；传了快照就忽略 `from` / `to`。
- 默认覆盖全部数据来源（与 `/api/v1` 中可见性 / 声量 / 引用域名默认只看 cloro 不同）。多个来源混合时，汇总工具的合并指标为空，按 `sources[]` 分来源给出；需要单一口径时传 `provider` + `engine`。
- 站点或记录越权与不存在返回同一条错误（“站点不存在或无权访问。” / “记录不存在或无权访问。”）。
- 回答原文来自第三方 AI，工具描述中已注明“仅作数据”。

## 错误、限流与审计

| 情况 | 结果 |
|---|---|
| 令牌缺失、无效、吊销、过期，或使用会话 / Cookie | 401 `MCP_TOKEN_INVALID` |
| 未执行 044 | `/mcp` 和令牌接口返回 503 `MCP_SCHEMA_NOT_MIGRATED`，其他接口不受影响 |
| 同一令牌每分钟超过 60 次 HTTP 请求（initialize、tools/list、tools/call 都计入） | 429 `RATE_LIMITED` |
| 参数无效、越权 | 工具结果 `isError=true`，附中文原因 |

每次工具调用写一条结构化日志：`TokenId`、`TenantId`、`Tool`、`SiteId`、`Outcome`（ok / denied / invalid_argument / conflict / unavailable / error / cancelled）、`ElapsedMs`，不含令牌明文、哈希或回答正文。

## 部署与回滚

先执行 044（mysql 客户端加 `--default-character-set=utf8mb4`），再发布 API；Worker 不受影响。回滚：先回滚 API，再按需执行 [rollback/044](../db/migrations/rollback/044_mcp_access_tokens_rollback.sql)。存在启用令牌时，回滚脚本拒绝执行。

## 本机验收（2026-11-04，5260，优联团队）

用 2 小时临时 owner 会话签发三个测试令牌：全部品牌、仅优联、人为改成已过期。验收后令牌和临时会话行都已删除，表内为 0 行。

- 令牌接口：创建返回 47 字符明文；列表不含明文或哈希。缺 `name`、`expiresInDays=400`、其他团队的 siteId 返回 400；无会话返回 401。
- MCP：`initialize` 和 `tools/list` 返回 8 个工具，描述为中文；8 个工具都调用成功。`get_monitoring_record`（packy_qwen 一条记录）与 `/api/v1/sites/{siteId}/monitoring/records/{checkId}` 同快照比对：回答原文（4028 字）、100 条引用 URL 与标题、搜索词、提及状态一致；品牌指标是精简后的排名 / 曝光分，数值一致。
- 范围：仅优联令牌访问狮心、全部品牌令牌访问其他团队的品牌、其他团队的 checkId、随机 checkId，都返回“不存在或无权访问”。
- 拒绝：会话 Bearer、Cookie、吊销后的令牌、过期令牌都返回 401；MCP 令牌访问 `/api/v1` 返回 401。
- 限流：同一分钟内第 61 次起返回 429。
- 日志只有 `TokenId`，没有令牌字符串。
- 未在 5260 上演示 503（单元测试覆盖），也没有对 `member` / `viewer` 角色做真实请求（单元测试覆盖 403）。

## 已知限制

- 越权和参数错误的工具调用会被 SDK 记成 `fail` 级别日志并带堆栈（客户端收到的错误信息正确），日志噪声较大。
- 格式正确但无效的令牌被拒绝时不计入限流，每次会查一次库。
- `/mcp` 请求仍会经过会话中间件，多查一次会话表。
- 令牌签发人失去管理员身份后，令牌不会自动失效。
