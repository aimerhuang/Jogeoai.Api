# 提问管理模块 PRD

> **文档定位（2026-11-12 整理）**：下方编号章节是 018 `question_groups` / `questions` 及 `/question-groups`、`/questions` 接口的原始 PRD，不是现役接入合同。这两组接口已于 2026-11-07 从 API 下线（安全修复 C1：原实现只校验任意 GUID 租户头、无会话与成员校验），控制器与仓储代码已删除，`questions`/`question_groups` 表与数据保留。现役的提问管理页面（前端 `/prompts`）使用既有 `prompts` 资源（`/api/v1/sites/{siteId}/prompts`）和 020 `prompt_groups`（`/prompt-groups`）。监控积分（019）与 019/020 部署状态见 [README](../README.md) 的提问管理迁移说明。
>
> **2026-11-03 增量**：`/prompts` 的「创建提问」已改为「新增监测问题」弹窗（Trellis `11-03-prompt-create-wizard`）。弹窗支持三种方式得到候选：AI 推荐（复用 `brand-research`）、核心词拓展（`POST .../prompt-suggestions/keyword`）、批量导入（每行一句或两行一组）。候选通过 `POST .../prompts/batch` 一次批量创建，同一事务内按名称新建或归入分组。每条提问可以单独保存 AI 平台范围（迁移 042：`prompts.target_scope` 与 `prompt_target_scopes`），编辑弹窗也能修改，列表按各提问自己的平台显示头像；创建前由 `POST .../prompt-monitoring/estimate` 预估每轮检测数与积分，与每日监控同一口径。042 已在本机 `jogeoai` 执行，部署顺序、已验证与未验证项见 README「新增监测问题弹窗与按提问平台范围」。本文下方的 018 原始 PRD 不涉及这些能力。

## 立即采集增量（2026-11-12 核对）

证据为当前 `ManagementController`、`PromptMonitoringService`、`CollectionScheduleStore` 和 5260 的 `/openapi/v1.json`；对应任务 [实施计划](../.trellis/tasks/11-11-prompt-list-models-instant-collect/implement.md) 尚无 `verification.md`，以下区分代码合同与真实验收。

- 新建启用提问、导入 / 批量创建中的实际新提问会尝试立即采集；修改已有启用提问的平台范围时只补新增模型，只减少模型不触发。不看该模型当天是否按频率到期；没有监控计划时先尝试建立计划和首轮，已暂停则不采集。
- 写接口响应可带 `instantCollection`：`started`（已建运行，不代表回答已完成）、`already_collected`、`skipped`，以及 `runId`、`checkCount`、`estimatedCredits`、`reasonCode`、`message`、`shortfall`。批量结果新增 `createdPromptIds`。余额不足、服务不可用等采集失败不回滚提问保存。
- 每条检测 10 积分，按提问 × 有效模型单元格 × 采样数计算；已有当天排队、运行中或成功的监控检测参与去重。主时隙展开会剔除当天已立即采集的对应项，全被剔除时跳过码为 `MONITORING_ALREADY_COLLECTED`；凌晨时隙不做这项剔除。
- 不新增迁移；发布时 API 与 Worker 同版本（或先 Worker 后 API），避免旧 Worker 在主时隙重复采集。方案记录的主时隙展开与立即采集并发竞态仍待解决，见 [设计 §9](../.trellis/tasks/11-11-prompt-list-models-instant-collect/design.md)。
- **已核对**：后端实现及 5260 公开字段存在。**pending**：本轮未运行定向单测 / 隔离 SQL、未验证真实创建到回答可见、积分、幂等重放及下个真实主时隙去重；未核对相邻前端的模型弹层和类型生成。不得把任务清单或 OpenAPI 存在当作这些验收已通过。

## 1. 产品概述

提问管理模块为品牌提供可组织、可复用的提问库管理能力，支持分组管理、启停控制和批量操作，用于可见性监控场景。

## 2. 核心功能

### 2.1 提问分组（Question Group）

- 创建、编辑、删除分组
- 分组包含多个提问
- 支持展开/折叠显示
- 显示分组下的提问数量和启用状态统计

### 2.2 提问（Question）

- 在分组下创建提问
- 提问包含：
  - 问题文本（必填）
  - 关联的盘测平台
  - 排名/优先级
  - 投及率（预期或实测）
  - 趋势指标
  - 启用状态（开关）
  - 创建时间

### 2.3 批量操作

- 批量启用/停用提问
- 批量删除
- 批量移动到其他分组

### 2.4 状态管理

- 单个提问启停开关
- 分组级别的启用/停用状态统计
- 已启用问题数量展示（如：15/15）

## 3. 数据模型

### 3.1 提问分组表 (question_groups)

```sql
CREATE TABLE question_groups (
    id BINARY(16) NOT NULL PRIMARY KEY COMMENT '分组ID',
    site_id BINARY(16) NOT NULL COMMENT '品牌站点ID',
    name VARCHAR(200) NOT NULL COMMENT '分组名称',
    description TEXT COMMENT '分组描述',
    display_order INT NOT NULL DEFAULT 0 COMMENT '显示顺序',
    is_active BOOLEAN NOT NULL DEFAULT TRUE COMMENT '是否启用',
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) COMMENT '创建时间',
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6) COMMENT '更新时间',
    
    INDEX idx_site_active (site_id, is_active, display_order),
    
    CONSTRAINT fk_question_groups_site 
        FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci 
COMMENT='提问分组表';
```

### 3.2 提问表 (questions)

```sql
CREATE TABLE questions (
    id BINARY(16) NOT NULL PRIMARY KEY COMMENT '提问ID',
    group_id BINARY(16) NOT NULL COMMENT '所属分组ID',
    site_id BINARY(16) NOT NULL COMMENT '品牌站点ID',
    question_text TEXT NOT NULL COMMENT '提问内容',
    
    -- 监控配置
    target_platforms JSON COMMENT '目标平台列表，如 ["ChatGPT", "Perplexity"]',
    priority INT NOT NULL DEFAULT 0 COMMENT '优先级/排名',
    expected_reach_rate DECIMAL(5,2) COMMENT '预期投及率（百分比）',
    
    -- 状态
    is_enabled BOOLEAN NOT NULL DEFAULT TRUE COMMENT '是否启用',
    display_order INT NOT NULL DEFAULT 0 COMMENT '分组内显示顺序',
    
    -- 统计字段（运行时更新）
    actual_reach_rate DECIMAL(5,2) COMMENT '实际投及率（百分比）',
    trend_direction ENUM('up', 'down', 'stable', 'unknown') DEFAULT 'unknown' COMMENT '趋势方向',
    last_monitored_at DATETIME(6) COMMENT '最后监控时间',
    
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) COMMENT '创建时间',
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6) COMMENT '更新时间',
    
    INDEX idx_group (group_id, display_order),
    INDEX idx_site_enabled (site_id, is_enabled),
    INDEX idx_priority (site_id, priority),
    
    CONSTRAINT fk_questions_group 
        FOREIGN KEY (group_id) REFERENCES question_groups(id) ON DELETE CASCADE,
    CONSTRAINT fk_questions_site 
        FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci 
COMMENT='提问表';
```

## 4. API 接口设计

### 4.1 分组管理

#### 4.1.1 获取分组列表

```
GET /api/v1/sites/{siteId}/question-groups
```

**查询参数：**
- `includeQuestions`: boolean，是否包含提问列表（默认 false）
- `isActive`: boolean，筛选启用状态
- `cursor`: string，分页游标
- `limit`: int，每页数量（默认 50，最大 100）

**响应：**
```json
{
  "items": [
    {
      "id": "uuid",
      "siteId": "uuid",
      "name": "CCTV银行扩大提示词",
      "description": "cctv银行扩大提示问",
      "displayOrder": 0,
      "isActive": true,
      "questionCount": 3,
      "enabledQuestionCount": 3,
      "createdAt": "2024-09-08T00:00:00Z",
      "updatedAt": "2024-09-08T00:00:00Z",
      "questions": []
    }
  ],
  "nextCursor": "string"
}
```

#### 4.1.2 创建分组

```
POST /api/v1/sites/{siteId}/question-groups
```

**请求体：**
```json
{
  "name": "公共提示词",
  "description": "公共提示词",
  "displayOrder": 0
}
```

**响应：** 返回创建的分组对象

#### 4.1.3 更新分组

```
PATCH /api/v1/sites/{siteId}/question-groups/{groupId}
```

**请求体：**
```json
{
  "name": "公共提示词（已更新）",
  "description": "更新后的描述",
  "displayOrder": 1,
  "isActive": false
}
```

#### 4.1.4 删除分组

```
DELETE /api/v1/sites/{siteId}/question-groups/{groupId}
```

**说明：** 级联删除分组下的所有提问

### 4.2 提问管理

#### 4.2.1 获取提问列表

```
GET /api/v1/sites/{siteId}/questions
```

**查询参数：**
- `groupId`: uuid，筛选分组
- `isEnabled`: boolean，筛选启用状态
- `cursor`: string，分页游标
- `limit`: int，每页数量（默认 50，最大 100）

**响应：**
```json
{
  "items": [
    {
      "id": "uuid",
      "groupId": "uuid",
      "siteId": "uuid",
      "questionText": "管理像你们这样的中规模最容易做什么？它对 CCTV 校验到...",
      "targetPlatforms": ["ChatGPT", "Perplexity"],
      "priority": 1,
      "expectedReachRate": null,
      "isEnabled": true,
      "displayOrder": 1,
      "actualReachRate": 0.0,
      "trendDirection": "unknown",
      "lastMonitoredAt": "2024-09-08T00:00:00Z",
      "createdAt": "2024-09-08T00:00:00Z",
      "updatedAt": "2024-09-08T00:00:00Z"
    }
  ],
  "nextCursor": "string"
}
```

#### 4.2.2 创建提问

```
POST /api/v1/sites/{siteId}/question-groups/{groupId}/questions
```

**请求体：**
```json
{
  "questionText": "有什么设备能在养护阶段起到降温的作用？",
  "targetPlatforms": ["ChatGPT"],
  "priority": 4,
  "expectedReachRate": null,
  "displayOrder": 0,
  "isEnabled": true
}
```

**响应：** 返回创建的提问对象

#### 4.2.3 获取提问详情

```
GET /api/v1/sites/{siteId}/questions/{questionId}
```

#### 4.2.4 更新提问

```
PATCH /api/v1/sites/{siteId}/questions/{questionId}
```

**请求体：**
```json
{
  "questionText": "有哪些设备能在养护阶段起到降温的作用？",
  "targetPlatforms": ["ChatGPT", "Perplexity"],
  "priority": 3,
  "displayOrder": 1,
  "isEnabled": false
}
```

#### 4.2.5 删除提问

```
DELETE /api/v1/sites/{siteId}/questions/{questionId}
```

#### 4.2.6 移动提问到其他分组

```
POST /api/v1/sites/{siteId}/questions/{questionId}/move
```

**请求体：**
```json
{
  "targetGroupId": "uuid",
  "displayOrder": 0
}
```

### 4.3 批量操作

#### 4.3.1 批量更新提问状态

```
POST /api/v1/sites/{siteId}/questions/batch-update-status
```

**请求体：**
```json
{
  "questionIds": ["uuid1", "uuid2"],
  "isEnabled": false
}
```

**响应：**
```json
{
  "updatedCount": 2,
  "failedIds": []
}
```

#### 4.3.2 批量删除提问

```
POST /api/v1/sites/{siteId}/questions/batch-delete
```

**请求体：**
```json
{
  "questionIds": ["uuid1", "uuid2"]
}
```

**响应：**
```json
{
  "deletedCount": 2,
  "failedIds": []
}
```

#### 4.3.3 批量移动提问

```
POST /api/v1/sites/{siteId}/questions/batch-move
```

**请求体：**
```json
{
  "questionIds": ["uuid1", "uuid2"],
  "targetGroupId": "uuid"
}
```

**响应：**
```json
{
  "movedCount": 2,
  "failedIds": []
}
```

## 5. 业务规则

### 5.1 权限控制

- 所有操作需验证 siteId 归属于当前租户
- 跨站点操作返回 404
- 开发环境使用 `X-Tenant-Id` 头，生产环境使用可信身份

### 5.2 数据验证

- 分组名称：1-200 字符，必填
- 提问文本：1-2000 字符，必填
- 优先级：整数，默认 0
- 投及率：0-100 的小数，保留两位

### 5.3 级联操作

- 删除分组时级联删除其下所有提问
- 删除站点时级联删除所有分组和提问

### 5.4 排序规则

- 分组按 `display_order ASC, created_at ASC` 排序
- 提问在分组内按 `display_order ASC, priority DESC, created_at ASC` 排序

## 6. 与现有系统集成

### 6.1 与监控系统集成

- 提问可关联到 `prompts` 表（通过 prompt_id 外键，可选）
- 启用的提问可自动创建监控计划
- 实际投及率从监控结果中更新

### 6.2 与分析系统集成

- 提问的监控结果进入现有 `visibility_runs`、`visibility_checks` 链路
- 趋势方向根据历史监控数据计算

## 7. 数据库迁移

创建 `db/migrations/018_question_management.sql`

## 8. 实现阶段

### 阶段一：基础 CRUD（018 已实现，现有前端未使用）
- 数据库迁移
- 分组和提问的增删改查 API
- 批量操作接口
- 基础验证和权限检查

### 阶段二：监控集成（后续）
- 与 `prompts` 表关联
- 自动创建监控计划
- 实时更新投及率和趋势

### 阶段三：高级功能（后续）
- 提问模板库
- 智能推荐提问
- 导入/导出功能

## 9. 测试要点

### 9.1 单元测试
- 数据验证逻辑
- 权限检查
- 级联删除

### 9.2 集成测试
- 完整 CRUD 流程
- 批量操作
- 跨租户隔离

### 9.3 针对性验证
```bash
dotnet test tests/Jogeoai.Tests/Jogeoai.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~QuestionManagementTests'
```

## 10. 前端对接（未采用）

现有前端改为在 `prompts` + `prompt_groups` 上实现以下展示与交互，未对接本节接口。

### 10.1 显示逻辑
- 分组可展开/折叠
- 显示启用状态统计（如：15/15）
- 提问显示投及率、趋势、启用开关

### 10.2 交互逻辑
- 创建提问按钮
- 新建分组按钮
- 批量启停按钮
- 单个提问的编辑/删除操作

### 10.3 数据刷新
- 列表操作后自动刷新
- 支持手动刷新
- 实时更新投及率（WebSocket 或轮询）
