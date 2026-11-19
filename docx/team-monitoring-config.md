# 团队默认监控配置：跟随与单独配置

> 对应 Trellis `11-11-team-monitoring-config`（后端子任务 `-api`、前端子任务 `-web`），迁移 [048](../db/migrations/048_team_monitoring_defaults.sql)。本文按 2026-11-11 的源码、已执行的迁移和本机真实验收编写；命令与证据见 [后端 verification.md](../.trellis/tasks/11-11-team-monitoring-config-api/verification.md)。单元格本身（回答模式、频率、积分口径）的规则见 [品牌监控模型配置说明](brand-monitoring-config.md)。

## 1. 用户怎么用

入口：团队管理 →「大模型监控配置」（`/team/monitoring`）。所有角色都能查看。

- **团队默认配置**：一份「模型 × 回答模式 × 采集频率」单元格，与品牌弹窗是同一个矩阵。直接在页面上编辑（2026-11-11 起不再通过弹窗）；只有 owner / admin 能修改并「保存配置」，其他角色只读。编辑时显示跟随品牌合计的下一轮 / 每周积分；余额不足只提示，不阻止保存。至少要勾选 1 个单元格。
- **跟随的品牌**：启用的监控单元格始终等于团队默认配置。保存团队默认配置时同步到这些品牌，从下一个尚未冻结的时隙起生效，不补采。
- **单独配置的品牌**：在团队页点「编辑频次」，或在品牌详情页点「单独配置」并保存后，品牌变为单独配置，之后不再接收团队默认配置的变更。跟随中的品牌打开弹窗时会提示这一点。
- **恢复跟随**：单独配置的品牌点「恢复跟随」（有确认），其监控单元格立即替换为团队默认配置。
- owner / admin / member 可以编辑品牌频次和恢复跟随；viewer 只读，看不到任何写入口。
- 品牌详情页「大模型监控配置」区标明「跟随团队默认」或「单独配置」。

## 2. 规则

- **地区**：团队默认配置不含国家 / 语言。同步到品牌时，品牌已有该模型的目标就沿用其国家 / 语言（优先启用的行），没有时 Cloro 取目录国家 + 品牌语言，豆包 / DeepSeek / PackyAPI 系列取 CN/zh-CN。品牌里不在默认配置中的可监控目标被停用（不删除），国内网页采集目标不受影响。
- **同一模型多个地区**：同步后只保留一个地区，其余停用；与品牌矩阵弹窗保存的行为一致。本机执行 048 时没有这样的品牌。
- **新品牌**：默认跟随。首次开启监控且没有启用的可监控目标时，写入团队默认配置；团队从未保存过默认配置时，仍写入原来的单个 ChatGPT 基线目标。
- **从未保存过**：读取时返回虚拟基线（ChatGPT 快速回答 1次/天），`configVersion = 0`，不落库。
- **并发**：团队默认配置和品牌目标各有版本号，版本不符返回 409。团队保存先锁租户再按 `site_id` 顺序锁品牌，与品牌保存不会反向加锁。

## 3. 存量品牌归类（048 执行时一次性完成）

每个团队独立计算；「已配置品牌」= 启用品牌中有启用的可监控目标者，配置按「provider | engine | 模式 | 频率」的集合比较，忽略国家 / 语言。

| 情况 | 团队默认 | 品牌 |
|---|---|---|
| 没有已配置品牌 | 不落库（虚拟基线） | 全部跟随 |
| 只有 1 个 | 该品牌的配置 | 跟随 |
| 有 ≥2 个品牌共用同一配置 | 共用最多的那组（并列取最早创建品牌所在组） | 该组跟随，其余单独配置 |
| 两两都不同 | 基线 ChatGPT 快速回答 1次/天 | 已配置品牌全部单独配置 |
| 同一模型同一模式在不同地区频率不同 | 不参与竞选 | 单独配置 |
| 未配置的启用品牌 / 归档品牌 | — | 跟随 / 单独配置 |

048 不写 `site_targets`，所以归类不改变任何品牌当前的采集。

## 4. 接口

| 接口 | 权限 | 说明 |
|---|---|---|
| `GET /api/v1/team/monitoring-config` | 所有角色 | `configVersion`、`canEdit`、默认单元格、各启用品牌的跟随状态与已启用单元格 |
| `PUT /api/v1/team/monitoring-config` | owner / admin | `{ expectedVersion, cells }`，同一事务内同步到跟随品牌；错误码同品牌目标保存，另有 `MONITORING_TARGETS_REQUIRED` |
| `POST /api/v1/team/monitoring-config/estimate` | owner / admin | 对跟随品牌逐个复用品牌目标预估并汇总；只计算 |
| `POST /api/v1/sites/{siteId}/targets/follow-default` | owner / admin / member | `{ expectedVersion }`（品牌目标版本） |

`SiteTargetsView` 增加 `followsTeamDefault`；公开的 `PUT /sites/{siteId}/targets` 保存后品牌变为单独配置。未执行 048 时团队接口返回 503 `TEAM_MONITORING_SCHEMA_NOT_MIGRATED`，品牌接口保持原状、`followsTeamDefault=false`。

## 5. 部署

1. 备份目标库。
2. 用 root 执行 048，客户端加 `--default-character-set=utf8mb4`；可重复执行。执行后核对：两张新表、`site_brand_settings.monitoring_follows_default` 为 `NOT NULL DEFAULT 1`、`site_targets` 校验和不变、归类结果符合 §3。
3. 发布 API。Worker 不读新表和新列，行为不变（建议与 API 同一版本构建）。
4. 发布前端，并从新构建重新生成 `src/types/generated/api.ts`。

本机 `jogeoai` 已于 2026-11-11 备份后执行，并重启了 5260 上的 API；其他库未执行。

## 6. 回滚

先回退前端和 API，再执行 [rollback/048](../db/migrations/rollback/048_team_monitoring_defaults_rollback.sql)（`--default-character-set=utf8mb4`），或从备份恢复。回滚只删除两张表和跟随标记列，丢失团队默认配置和跟随状态；`site_targets` 不变，各品牌当前的采集不变。

## 7. 已知限制

- 不含终端维度（参考图中的「PC」）和按套餐解锁模型。
- 团队级采样数、批量恢复跟随、团队操作日志未实现。
- 下发时未变化的跟随品牌也会使其目标版本号加 1。
- 已下线模型若仍在默认配置中，保存会被拒绝（400 `TARGET_NOT_ALLOWED`），需先取消勾选。
- 跟随品牌下发后的真实 09:01 / 00:05 时隙展开尚未在本机核对（验收团队没有启用的计划）。
