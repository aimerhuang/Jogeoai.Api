# 部署说明：API 与采集端

本文说明如何把 Jogeoai 部署成“数据服务器 + 前端服务器 + 多台采集电脑”。仓库结构：

| 目录 | 内容 |
|---|---|
| 仓库根目录 | Jogeoai.Api（.NET 10）：`src/Jogeoai.Api`（HTTP API）、`src/Jogeoai.Worker`（后台任务）、`db/migrations`（MySQL 迁移） |
| `deploy/` | 服务器用的 Dockerfile、`compose.yml`、`.env.production.example` |
| `collector/` | 采集端（Node.js 24 工作区，原 Ai-visiblility-collector），含网页采集与安卓 App 采集；文档在 `collector/README.md` 与 `collector/docs/runbooks/` |

> 状态说明：本文中标注“已验证”的内容是在一台 Apple Silicon Mac 上用隔离的 compose 项目实测过的（见文末“验证记录”）。生产服务器、网关、HTTPS、多台采集电脑联合运行**都还没有在真实环境里部署或验证过**。

## 1. 拓扑

```
浏览器 ──HTTPS──▶ 前端服务器 (Nginx)
                   ├─ /       → Jogeoai.web 的 dist/
                   └─ /api/   → 反代到 https://<后端域名>/api/

数据服务器（本仓库 deploy/compose.yml）
  宿主机网关 (Caddy / Nginx, :443) ──▶ 127.0.0.1:5260 → api 容器 :8080
  api、worker ──▶ mysql（只在 compose 内网，不映射端口）

采集电脑 A/B/C …（原生运行，不用 Docker）
  每台：1 个 App 节点（pnpm collector:app:serve）+ N 台安卓模拟器
  只向后端发起 HTTPS 出站请求：/internal/collector/app/*
```

- 采集端只连后端，不连数据库；后端不需要主动连采集电脑，采集电脑不用开放入站端口。
- 前端不在本仓库（Jogeoai.web）。保持**同源反代**：浏览器只访问前端域名，`/api/` 由前端服务器转发。不要把前端改成跨域直连后端（会话 Cookie 是 HttpOnly + SameSite=Lax，后端 CORS 不允许携带凭据）。

## 2. 数据服务器：API + Worker + MySQL

### 2.1 准备

- Linux 或 macOS，Docker 29+ 与 Compose v2。所用的官方基础镜像（.NET 10、MySQL 8.4）都提供 amd64 与 arm64；目前只在 arm64（Apple Silicon）上实测过构建。
- 至少 4 GB 内存、20 GB 磁盘（MySQL 数据与镜像）。
- 一个解析到本机的后端域名，供网关签发证书。

### 2.2 配置

```bash
git clone https://github.com/aimerhuang/Jogeoai.Api.git
cd Jogeoai.Api
cp deploy/.env.production.example deploy/.env
chmod 600 deploy/.env
```

编辑 `deploy/.env`（变量说明见文件内注释）。生成随机密钥可以用 `openssl rand -hex 24`。

| 变量 | 说明 |
|---|---|
| `MYSQL_PASSWORD`、`MYSQL_ROOT_PASSWORD` | 必填。首次启动时用来创建数据库账号；之后改这里**不会**改已有账号的密码 |
| `API_PORT` | API 在宿主机上监听的端口，只绑定 `127.0.0.1`，默认 5260 |
| `COLLECTOR_CHINA_SERVICE_TOKEN` | 网页采集端（Playwright）调用 `/internal/collector/*` 的令牌；不用网页采集可留空 |
| `COLLECTOR_CHINA_TENANT_ID_0` | 网页采集端允许的租户 GUID |
| `COLLECTOR_APP_TENANT_ID_0` | 开放 App 渠道的租户 GUID；留空表示 App 渠道对所有租户关闭。API 与 Worker 自动使用同一个值 |
| `EMAIL_*` | 注册验证码发信用的 SMTP；密码填 SMTP 授权码 |
| `JOGEOAI_TAG` | 镜像标签；升级和回滚都靠它 |

租户名单是数组配置。需要第二个租户时，在 `deploy/compose.yml` 的 `x-dotnet-env` 里增加 `Collector__AppTenantIds__1`（或 `Collector__ChinaTenantIds__1`），按 .NET 配置约定以下标区分。

### 2.3 启动

```bash
docker compose -f deploy/compose.yml --env-file deploy/.env up -d --build
docker compose -f deploy/compose.yml --env-file deploy/.env ps
curl -fsS http://127.0.0.1:5260/health/live
curl -fsS http://127.0.0.1:5260/health/ready
```

- **全新数据库**：MySQL 镜像由 `deploy/Dockerfile.mysql` 构建，把 `db/migrations/*.sql`（001–051，不含 `rollback/`）打进镜像。`mysql_data` 数据卷为空时，首次启动按文件名顺序全部执行，客户端字符集固定为 utf8mb4。全部成功后写入完成标记 `/var/lib/mysql/.jogeoai-initdb-complete`，健康检查要求这个标记存在，所以 api / worker 只会在迁移全部成功后启动。首次启动约需半分钟到几分钟。
- **初始化失败**：任何一个迁移失败，标记不会写入，MySQL 永远不会变成 healthy，api / worker 不会启动。用 `docker compose ... logs mysql` 查看是哪个迁移报错。修复后删除这个**空库**的数据卷重来：`docker compose -f deploy/compose.yml --env-file deploy/.env down -v`（会删除该项目的全部数据卷，只能用在还没有业务数据的新库上）。
- **数据卷已有数据**：迁移**不会**再自动执行。后续新增的迁移要按 2.5 节人工执行。
- API 以 `Production` 环境运行：Swagger 关闭；非开发环境启用 HTTPS 重定向，因为容器内只有 HTTP，网关负责 TLS。
- Worker 以完整模式运行（采集、分析、计划、App 渠道截止处理等全部后台任务）。

### 2.4 网关（必须）

API 只监听 `127.0.0.1`，需要在宿主机上放一个 TLS 网关转发到它。Caddy 示例（`/etc/caddy/Caddyfile`，自行替换域名和 IP）：

```
api.example.com {
    @collector path /internal/collector/*
    handle @collector {
        @allowed remote_ip <采集电脑出口 IP …>
        handle @allowed {
            reverse_proxy 127.0.0.1:5260
        }
        respond 403
    }
    @api path /api/* /health/*
    handle @api {
        @allowed remote_ip <前端服务器 IP>
        handle @allowed {
            reverse_proxy 127.0.0.1:5260
        }
        respond 403
    }
    respond 404
}
```

这只是示例，未在真实服务器上验证。`/swagger`、`/openapi` 不要对外开放。如果要开放 `/mcp`，单独加规则（应用层用 MCP 令牌鉴权）。

### 2.5 迁移已有数据 / 执行新迁移

把现有库（例如本机 `jogeoai`）搬到服务器：

```bash
# 在旧机器上
mysqldump --default-character-set=utf8mb4 --single-transaction --routines --triggers \
  -h 127.0.0.1 -u root -p jogeoai | gzip > jogeoai-YYYYMMDD.sql.gz

# 在服务器上：先只启动 mysql，等它变成 healthy
docker compose -f deploy/compose.yml --env-file deploy/.env up -d mysql
```

**注意**：空数据卷第一次启动时已经执行了全部迁移。导入旧库前先删除这个库并重建，再导入，否则会与迁移建出的表冲突：

```bash
gunzip -c jogeoai-YYYYMMDD.sql.gz | docker compose -f deploy/compose.yml --env-file deploy/.env \
  exec -T mysql sh -c 'mysql --default-character-set=utf8mb4 -uroot -p"$MYSQL_ROOT_PASSWORD" -e "DROP DATABASE IF EXISTS $MYSQL_DATABASE; CREATE DATABASE $MYSQL_DATABASE CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;" && mysql --default-character-set=utf8mb4 -uroot -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_DATABASE"'
```

然后核对迁移版本与关键表行数，再启动 api 和 worker。以后新增迁移，用同样的方式执行单个文件：

```bash
docker compose -f deploy/compose.yml --env-file deploy/.env exec -T mysql \
  sh -c 'mysql --default-character-set=utf8mb4 -uroot -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_DATABASE"' \
  < db/migrations/0NN_xxx.sql
```

迁移前先备份。各迁移的发布顺序要求（例如“先迁移再同时发布 API 与 Worker”）见 README 对应章节。

### 2.6 升级与回滚

```bash
git pull
JOGEOAI_TAG=20261120 docker compose -f deploy/compose.yml --env-file deploy/.env up -d --build
```

- 把 `deploy/.env` 里的 `JOGEOAI_TAG` 改成新标签再构建，旧标签的镜像会保留在本机。
- 回滚：把 `JOGEOAI_TAG` 改回旧标签，执行 `up -d`（不要 `--build`）。
- 已执行的迁移不会随镜像回滚；需要时按 `db/migrations/rollback/` 的脚本与 README 中的说明处理。
- 备份：`mysql_data` 数据卷需要每天 `mysqldump` 备份并异地保存；`knowledge_data` 卷存放知识库文件，也要备份。

### 2.7 日志

```bash
docker compose -f deploy/compose.yml --env-file deploy/.env logs -f api worker
```

## 3. 采集端

采集端代码在 `collector/`，有两种通道：

| 通道 | 运行位置 | 本仓库提供 |
|---|---|---|
| **App 通道**（安卓模拟器，主力） | 每台采集电脑上原生运行 | 本节步骤；详细说明 `collector/docs/runbooks/app-channel-node.md`、`app-channel-local.md` |
| 网页通道（Playwright 浏览器） | 需要有人登录浏览器的电脑 | `collector/docs/runbooks/account-pool.md` 与各平台 runbook |

> 为什么采集端不放进服务器的 Docker：安卓模拟器不能在 Mac 的 Docker 里运行，App 通道需要宿主机上的 adb 和模拟器；网页通道需要有人在浏览器里登录、处理验证码。`deploy/compose.yml` 里的 `collector` 服务（profile `collector`，默认不启动）只构建采集端 Node 工作区镜像，镜像里没有浏览器和 adb，**不能在服务器上做真实采集**。

### 3.1 App 通道：加一台采集电脑

每台采集电脑运行**一个**节点，节点下可以挂多台模拟器。后端按设备派单：每台模拟器串行，不同模拟器并行；每台模拟器两次提问至少间隔 100 秒（从实际发送算起）；发送前失败的任务可以转给其他电脑的模拟器。加电脑、加模拟器都不需要改代码。

**1）后端准备（一次性）**

- 数据库已执行迁移 049–051（全新库已包含）。
- `deploy/.env` 设置了 `COLLECTOR_APP_TENANT_ID_0`，并为目标品牌开启了 App 目标。
- 以系统管理员身份为这台电脑注册节点：`POST /api/v1/admin/collector-app/nodes`，请求体 `{"nodeId":"<节点名>"}`。201 响应里直接带有 `nodeId` 和 `token`，**只保存在这台电脑上**，不要打印、不要提交。丢失或泄露时用 `POST /api/v1/admin/collector-app/nodes/{nodeId}/token` 轮换（旧令牌立即失效）。

**2）电脑环境**

- macOS（Apple Silicon，已有 launchd 常驻方法）；Linux / Windows 未测试。
- Node 24.x 与 pnpm 10.34.5（`node -v` 必须是 24）。
- Android SDK：`adb` 在 `PATH` 上，设置 `ANDROID_HOME`（或 `ANDROID_SDK_ROOT`）与 `JAVA_HOME`。
- 模拟器：Android 15、arm64-v8a、4k 页、系统语言 `zh-Hans-CN`、时区 `Asia/Shanghai`。每次启动模拟器后都要重设时区（`adb -s <serial> shell cmd alarm set-timezone Asia/Shanghai`），节点只检查、不修改。
- 每个 App 由人手动安装并登录。
- 时钟同步（NTP）；后端会校验发送时间。

**3）代码**

```bash
git clone https://github.com/aimerhuang/Jogeoai.Api.git
cd Jogeoai.Api/collector
pnpm install --frozen-lockfile
```

**4）这台电脑的私有文件（都放在 Git 之外，路径里要有 `aivc/`）**

| 文件 / 目录 | 内容 |
|---|---|
| `~/.config/aivc/app-node.env`（权限 600） | 环境变量，见下 |
| `~/aivc/app-bindings.json` | 这台电脑的设备与绑定，格式同 `collector/apps/collector/app-bindings.json` |
| `~/aivc/app-contracts/` | 审核过的 App 界面合同，每个平台一个 `<platformCode>.json`（600），7 天内审核过；每台电脑都要有一份 |
| `~/aivc/app-evidence/` | 证据目录（发送账本、记录、截图）。只给这一台电脑用，不要共享、不要放在网络盘上 |

`app-bindings.json` 要点：

- `devices[].id` 是 AVD 名称（不是 adb 序列号），声明的系统版本、ABI、页大小、语言、时区要和真实模拟器一致，否则这台设备不会被使用。
- 每条绑定是“设备 × 平台 × 账号标签”。一个账号只登录在一台模拟器上；账号标签在**所有电脑之间**也建议保持唯一，方便排查。
- 不需要的平台把 `enabled` 设为 `false` 并写 `disabledReason`。

`app-node.env`：

```bash
APP_COLLECTOR_API_URL=https://api.example.com
APP_COLLECTOR_NODE_ID=<节点名>
APP_COLLECTOR_NODE_TOKEN=<注册时拿到的令牌>
APP_COLLECTOR_BINDINGS_FILE=/Users/<你>/aivc/app-bindings.json
APP_COLLECTOR_CONTRACT_DIR=/Users/<你>/aivc/app-contracts
APP_COLLECTOR_EVIDENCE_DIR=/Users/<你>/aivc/app-evidence
```

`APP_COLLECTOR_API_URL` 指向后端网关（http/https 均可；4311、4312 端口会被拒绝，那是 Mock 和测试队列）。其他可选变量见 `collector/apps/collector/src/app-channel/env.ts`。

**5）自检（可选）与首次运行**

```bash
cd Jogeoai.Api/collector
set -a; source ~/.config/aivc/app-node.env; set +a
pnpm collector:app:check     # 只读：设备、App 版本、登录状态
pnpm collector:app:serve     # 第一次请在有人值守时前台运行
```

`check` 只是诊断，不是启动条件，不要把它包成“全部通过才启动”的脚本。

**6）常驻运行（macOS）**

按 `collector/docs/runbooks/app-channel-node.md` 的 launchd 示例创建 `~/Library/LaunchAgents/com.aivc.app-node.plist`（把仓库路径改成 `.../Jogeoai.Api/collector`），然后：

```bash
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.aivc.app-node.plist
launchctl bootout gui/$(id -u)/com.aivc.app-node   # 停止
```

停止时节点不再领新任务，正在进行的提问最多再等 10 分钟。

**7）需要人处理的情况**

- 登录失效、验证码、设备验证：节点只上报，这条绑定停止，直到有人在**那台电脑的模拟器上**处理完并在后台恢复该绑定。每台采集电脑都要能远程操作（屏幕共享，或经 SSH 隧道访问 VNC）。**不要**用任何自动化方式处理登录、验证码或设备验证，也不要改设备指纹。
- 模拟器掉线：第一版节点不会自动启动模拟器，也不会在重启后恢复语言和时区，会上报 `device_offline`，需要人处理。
- App 界面改版：该绑定会持续在发送前失败，后台告警（`GET /api/v1/admin/collector-app/alerts`）会列出，需要人手动停用并更新合同。

### 3.2 网页通道

网页通道用专用浏览器 profile 访问各平台网站，向 `/internal/collector/*` 领取任务（令牌是 `COLLECTOR_CHINA_SERVICE_TOKEN`）。它需要有人登录 profile、处理验证码，按 `collector/docs/runbooks/account-pool.md` 和各平台 runbook 在采集电脑上原生运行。`collector/compose.yaml` 是采集端自带的 **Mock** 演示环境（API 4311），不是生产部署方式，生产采集不能指向 4311 / 4312。

## 4. 上线前必须完成

以下事项本仓库**没有**完成，部署到公网前必须处理：

1. **应用安全问题**：2026-11 的内部安全审计结论是当前代码不宜直接开放公网，需要先修复审计列出的高优先级问题（审计报告不在公开仓库中）。网络白名单和网关不能替代这些修复。
2. **反向代理链**：API 尚未处理 `X-Forwarded-*`，经网关后限流会把所有用户当作同一个 IP；需要在应用中按实际拓扑配置受信代理。
3. **网关与 TLS**：按 2.4 节配置并实测白名单生效；`/swagger`、`/openapi` 不对外。
4. **备份**：MySQL 与知识库数据卷的每日备份和异地保存。
5. **采集电脑联调**：多台电脑、多台模拟器同时连生产后端还没有实测过；先接一台电脑有人值守试跑，再逐台增加。

## 5. 验证记录

2026-11-20，Apple Silicon Mac，Docker 29.7.2（arm64），用公开仓库导出目录和隔离的 compose 项目实测：独立项目名、独立数据卷、API 端口 5390，没有碰本机 `jogeoai` 库和 5260。

| 项目 | 结果 |
|---|---|
| 构建 api、worker、mysql 镜像 | 成功 |
| 空库首次启动执行 001–051 | 全部成功，完成标记已写入，MySQL 没有重启；库中 125 张表 |
| `/health/live`、`/health/ready` | 200 |
| `/swagger/index.html` | 404（Production 关闭） |
| 匿名 `GET /api/v1/auth/me` | 401 |
| Worker | 启动并持续运行，日志显示 MySQL 连接与后台 Worker 已启动 |
| 采集端镜像（profile `collector`） | 成功（2026-11-20 重新构建，前两次因 Docker Hub 网络超时失败）：821 MB，Node v24.22.3，以 `node` 用户运行，`apps/{collector,api,worker}/dist` 都在；镜像内没有 adb、Chromium 或 Playwright 浏览器 |

实测中发现并已修复：镜像内 mysql 客户端默认 latin1，043 迁移在初始化时失败；失败后容器自动重启，又对**只迁移了一半的库**报告 healthy。现在客户端字符集固定为 utf8mb4，并用完成标记阻止这种情况。

**没有验证的**：x86_64 服务器上的构建、真实生产服务器、网关与 HTTPS、从旧库导入数据（2.5 节）、真实采集任务端到端、多台采集电脑联合运行。
