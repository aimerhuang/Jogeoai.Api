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
| `COLLECTOR_APP_TENANT_IDS` | 其他开放 App 渠道的租户，多个 GUID 用逗号分隔（也可用分号或空格），与 `COLLECTOR_APP_TENANT_ID_0` 合并生效；留空不增加。改完需重启 API 和 Worker（2026-11-27 源码，未在服务器上验证） |
| `EMAIL_*` | 注册验证码发信用的 SMTP；密码填 SMTP 授权码 |
| `JOGEOAI_TAG` | 镜像标签；升级和回滚都靠它 |
| `JOGEOAI_SUBNET` | compose 网络的网段，默认 `172.30.250.0/24`。同一个值也传给 API，作为受信代理网段（`ForwardedHeaders__KnownNetworks__0`）。只在和宿主机已有网段冲突时才改 |
| `FRONTEND_PROXY_IP` | 前端服务器访问后端网关时的出口 IP（单个 IP，不是网段），传给 API 作为受信代理（`ForwardedHeaders__KnownProxies__0`）。留空表示只信任宿主机网关这一跳，API 看到的浏览器流量来源是前端服务器 IP |
| `FORWARDED_LIMIT` | API 最多采用 `X-Forwarded-For` 中的几项，默认 2（前端服务器 + 宿主机网关）。只能是 1–5，否则 API 拒绝启动 |

后三个变量只传给 api 容器，Worker 没有 HTTP 入口。它们怎样和网关配合见 2.4 节“转发头”。网络已经创建过的项目修改 `JOGEOAI_SUBNET` 后，旧网络不会自动换网段，需要先 `down`（不要加 `-v`）再 `up`；这一步没有实测过。

租户名单是数组配置。App 渠道的多个租户直接写进 `COLLECTOR_APP_TENANT_IDS`（compose 把它作为 `Collector__AppTenantIds__1` 传入，`AppChannelOptions` 按逗号拆分），不需要改 compose。网页采集端需要第二个租户时，仍在 `deploy/compose.yml` 的 `x-dotnet-env` 里增加 `Collector__ChinaTenantIds__1`，按 .NET 配置约定以下标区分。

### 2.3 启动

```bash
docker compose -f deploy/compose.yml --env-file deploy/.env up -d --build
docker compose -f deploy/compose.yml --env-file deploy/.env ps
curl -fsS http://127.0.0.1:5260/health/live
curl -fsS http://127.0.0.1:5260/health/ready
```

- **全新数据库**：MySQL 镜像由 `deploy/Dockerfile.mysql` 构建，把 `db/migrations/*.sql`（001–052，不含 `rollback/`）打进镜像。`mysql_data` 数据卷为空时，首次启动按文件名顺序全部执行，客户端字符集固定为 utf8mb4。全部成功后写入完成标记 `/var/lib/mysql/.jogeoai-initdb-complete`，健康检查要求这个标记存在，所以 api / worker 只会在迁移全部成功后启动。首次启动约需半分钟到几分钟。
- **初始化失败**：任何一个迁移失败，标记不会写入，MySQL 永远不会变成 healthy，api / worker 不会启动。用 `docker compose ... logs mysql` 查看是哪个迁移报错。修复后删除这个**空库**的数据卷重来：`docker compose -f deploy/compose.yml --env-file deploy/.env down -v`（会删除该项目的全部数据卷，只能用在还没有业务数据的新库上）。
- **数据卷已有数据**：迁移**不会**再自动执行。后续新增的迁移要按 2.5 节人工执行。
- API 以 `Production` 环境运行：Swagger 关闭；非开发环境启用 HTTPS 重定向，因为容器内只有 HTTP，网关负责 TLS。
- Worker 以完整模式运行（采集、分析、计划、App 渠道截止处理等全部后台任务）。

### 2.4 网关（必须）

API 只监听 `127.0.0.1`，需要在宿主机上放一个 TLS 网关转发到它。三组路径的放行方式不同（2026-11-27 确定）：

| 路径 | 网关放行 | 应用层鉴权 |
|---|---|---|
| `/internal/collector/app/*`（App 采集节点） | **不限来源 IP**。采集电脑多、出口 IP 不固定，不做白名单 | 每个节点自己的令牌，可单独轮换和停用（见 3.1 节） |
| `/internal/collector/*` 的其余路径（网页采集端） | 只放行网页采集电脑的出口 IP | 所有网页采集端共用的 `COLLECTOR_CHINA_SERVICE_TOKEN`，泄露后不能只吊销某一台，所以保留 IP 限制 |
| `/api/*`、`/health/*` | 只放行前端服务器的 IP | 用户会话 |

Caddy 示例（`/etc/caddy/Caddyfile`，自行替换域名和 IP）：

```
api.example.com {
    handle /internal/collector/app/* {
        reverse_proxy 127.0.0.1:5260
    }
    handle /internal/collector/* {
        @allowed remote_ip <网页采集电脑出口 IP …>
        handle @allowed {
            reverse_proxy 127.0.0.1:5260
        }
        handle {
            respond 403
        }
    }
    @api path /api/* /health/*
    handle @api {
        @allowed remote_ip <前端服务器 IP>
        handle @allowed {
            reverse_proxy 127.0.0.1:5260
        }
        handle {
            respond 403
        }
    }
    handle {
        respond 404
    }
}
```

这只是示例，未在真实服务器上验证。`/swagger`、`/openapi` 不要对外开放。如果要开放 `/mcp`，单独加规则（应用层用 MCP 令牌鉴权）。

- **采集接口：应用内已有请求体上限和先鉴权后读请求体；按 IP 的限流只在转发头配好之后才生效；网关限流仍建议保留。**（2026-11-27 实现，有进程内测试；开发机的 5260 上用 HTTP 核对过先鉴权后读请求体和 413，限流在开发机上按设计不生效、没有验证，真实网关后面都没有验证过，详见 README“采集接口的鉴权前开销限制”）
  - **请求体上限**：App 的 `leases`、`sent`、`heartbeats`、`failures` 为 16 KiB，`bindings/report` 1 MiB，`results` 4 MiB；网页通道的 `results` 6 MiB，其余 16 KiB。超限返回 413，不反序列化。网关上的请求体上限不要小于这些值，否则合法的结果提交会被网关拒绝。
  - **先鉴权后读请求体**：令牌缺失、错误或节点被停用时直接返回 401 / 403，应用不读取也不解析请求体。带令牌的 App 请求仍然会查一次节点表；`/internal/*` 和 `/mcp` 的请求不再查用户会话表。
  - **按客户端 IP 限流**：`/internal/collector/*`（含 `health`）每个客户端 IP 每分钟 600 次，超出返回 429 和 `Retry-After`，被拒的请求不查库。
    - **没有配置受信转发头时，采集接口没有任何应用内限流**（请求体上限和先鉴权后读请求体照常生效）。这时 API 启动日志里有一行 Warning：`Collector rate limit inactive: …`。只能靠网关限流。
    - **转发头配好之后，只对“网关给出了客户端地址”的请求限流。** API 处理转发头之后得到的地址如果仍是受信代理自己（compose 网段里的地址或 `FRONTEND_PROXY_IP`），说明网关没有写 `X-Forwarded-For`，这个请求不限流。从前端服务器、宿主机本机或 compose 网络里其他容器自己发出的请求也因此不受限流。
    - 为什么这样设计（2026-11-27 评审后修订）：限流先于鉴权，不带令牌的请求同样计数，而 App 采集路径在网关上不限来源 IP。如果在不知道客户端地址时按网关地址限流，所有采集电脑共用一个桶，任何人每秒发约 10 个请求就能让它一直处于用完的状态，全部采集电脑收到 429 后各自退避 10 分钟。所以这种情况下不限流，而不是把所有人一起锁住。
    - 客户端不能靠伪造 `X-Forwarded-For` 退出限流，前提是网关按下面“转发头”的第 1 条覆盖（或至少追加）这个头：API 采用的是网关写的对端地址，客户端自己写的值不会被采用（进程内测试覆盖了覆盖、追加和 `FORWARDED_LIMIT` 为 1、2、5 的情况）。网关原样透传时客户端可以任意选择地址，限流对它无效，这正是第 1 条禁止透传的原因。
    - 600 的依据：一个网页采集进程空闲时每秒轮询一次，加上心跳约 100 次 / 分钟；App 节点每个绑定约 1–5 次 / 分钟。同一出口 IP 后面的采集进程很多时需要调高：在 `deploy/compose.yml` 的 api 服务 `environment` 里增加 `Collector__RateLimit__PermitPerMinute: "<次数>"` 后重新 `up`。必须是不小于 1 的整数，否则 API 拒绝启动。这个写法没有在服务器上验证过。
    - 现行采集端把任何 4xx（包括 413 和 429）都当作“被拒绝”，退避 10 分钟，所以阈值宁高勿低。
  - **网关限流仍然建议配置**：没有配置转发头时它是采集接口唯一的限流；配置之后，分布在很多 IP 上的来源每个 IP 仍能以 600 次 / 分钟触发节点表查询，这部分也只能靠网关。限流按完整的 IP 地址分桶，IPv6 来源在自己的前缀里换一个地址就是一个新桶。Caddy 限流需要额外插件，Nginx 自带 `limit_req`。没有验证过。
- 不使用网页采集时，把 `COLLECTOR_CHINA_SERVICE_TOKEN` 留空，网页通道的领取和提交接口一律返回 401，网关上那一段可以直接改成 `respond 403`。
- 网页采集电脑的出口 IP 也不固定时，可以把那一段同样改成直接转发，风险高于 App 通道，原因见上表。

#### 转发头：让 API 看到真实的客户端 IP

宿主机网关经端口映射进入 api 容器，所以 API 看到的 TCP 对端是 compose 网络里的地址，不是真实客户端。注册、验证码、兑换失败等限流按客户端 IP 分区，会话记录也保存来源 IP，这些都依赖 `X-Forwarded-For`。API 的规则（2026-11-27 实现，见 README“代理与来源校验”）：

- 只有 TCP 对端在受信列表里，才读取 `X-Forwarded-For` 和 `X-Forwarded-Proto`。受信列表是 `JOGEOAI_SUBNET`（宿主机网关这一跳）加 `FRONTEND_PROXY_IP`（前端服务器这一跳）。
- 从 `X-Forwarded-For` **最右边**一项开始往左取。当前这一跳受信才取下一项；遇到不受信的地址，或取满 `FORWARDED_LIMIT` 项，就停。
- 不读取 `X-Forwarded-Host`。

对网关的要求：

1. **宿主机网关必须在每个请求上自己写 `X-Forwarded-For`，最右边一项是它看到的对端 IP。** 对端不受信时（采集电脑、MCP 客户端、其他任何直接访问者），必须**覆盖**客户端带来的值，不能追加，更不能原样透传。API 信任整个 compose 网段，宿主机网关送来的最右一项一定会被采用；如果网关原样透传，客户端就能随意填写自己的 IP，限流和来源记录都会失效，比不配置转发头更糟。
   - Caddy：`reverse_proxy` 默认会覆盖。对端不在 `trusted_proxies` 里时，客户端带来的 `X-Forwarded-*` 被丢弃，改写为对端 IP 和实际协议。
   - Nginx：默认把客户端的请求头**原样转给上游**。每个 `location` 都要显式写 `proxy_set_header X-Forwarded-For $remote_addr;` 和 `proxy_set_header X-Forwarded-Proto $scheme;`。
2. **只有来自前端服务器的请求，宿主机网关才保留它带来的值并在后面追加前端服务器 IP。**
   - Caddy：在全局选项里把前端服务器列为受信代理，例如 `{ servers { trusted_proxies static <前端服务器 IP> } }`。
   - Nginx：只在已经限制为前端服务器 IP 的 `/api/` 那一段使用 `proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;`。
3. **前端服务器面对的是浏览器，全部不受信**：`/api/` 反代里写 `proxy_set_header X-Forwarded-For $remote_addr;`（覆盖）和 `proxy_set_header X-Forwarded-Proto $scheme;`。如果这里误用了追加，浏览器自带的值会排在最左边；`FORWARDED_LIMIT=2` 时 API 不会取到它。

各路径的跳数（`FORWARDED_LIMIT=2`）：

| 路径 | 链路 | 到达 API 的 `X-Forwarded-For` | API 得到的客户端 IP |
|---|---|---|---|
| `/api/*` | 浏览器 → 前端服务器 → 宿主机网关 → api，两跳 | `<浏览器 IP>, <前端服务器 IP>` | 浏览器 IP |
| `/internal/collector/*`、`/mcp` | 客户端 → 宿主机网关 → api，一跳 | `<客户端 IP>` | 客户端 IP。它不是受信代理，所以不会再往左取；客户端自带的值在网关覆盖时已被丢弃，即使残留在左边也不会被采用 |

- `FRONTEND_PROXY_IP` 留空，或宿主机网关没有把前端服务器列为受信代理（于是把头覆盖成前端服务器 IP）时，`/api/*` 得到的是前端服务器 IP。所有浏览器用户仍然落在同一个限流分区，和没有转发头时一样，不会被伪造，但问题也没有解决。
- 能直接连到宿主机 `127.0.0.1:${API_PORT}` 的进程，以及 compose 网络里的其他容器，都在受信网段内。不要让其他程序复用这个端口或这个 compose 网络。
- 不要给 api 容器设置 `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`，它会让框架信任所有来源的转发头。

部署后核对（**尚未在真实环境做过**）：

- API 启动日志里应有 `ForwardedHeaders enabled: knownProxies=1 knownNetworks=1 forwardLimit=2`。出现 `ForwardedHeaders disabled` 说明两个受信项都没有传进容器。
- 从浏览器故意输错一次密码，API 日志 `LoginRejected … ip=` 应是浏览器的出口 IP。
  - 是 `JOGEOAI_SUBNET` 里的地址：宿主机网关没有写 `X-Forwarded-For`。
  - 是一个不在 `JOGEOAI_SUBNET` 里的内网地址，而且转发头明显没有生效：容器看到的对端不在受信网段内。这取决于 Docker 的端口映射方式，本文假设对端是 compose 网络的网关地址，Linux 服务器和 Docker Desktop 上都还没有核对。
  - 是前端服务器 IP：第 2 条没有配好，或 `FRONTEND_PROXY_IP` 为空。
- 从一台不受信的机器带上伪造的 `X-Forwarded-For` 请求 `/internal/collector/*`，API 记录的来源仍应是那台机器的真实 IP。
- 不带令牌向 `/internal/collector/app/leases` 发送一段损坏的 JSON，应返回 401（不是 400）；带有效节点令牌发送超过 16 KiB 的请求体，应返回 413。API 启动日志里不应出现 `Collector rate limit inactive`；出现了说明受信转发头没有传进容器，采集接口没有应用内限流。不要用循环请求去试 429：它会占用这个出口 IP 一分钟的额度，同一 IP 后面的采集电脑会被退避 10 分钟。

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

- 数据库已执行迁移 049–052（全新库已包含；已有数据的库按 2.5 节人工执行，052 要先于新版 API / Worker 执行）。
- `deploy/.env` 设置了 `COLLECTOR_APP_TENANT_ID_0`，并为目标品牌开启了 App 目标。
- 以系统管理员身份为这台电脑注册节点：`POST /api/v1/admin/collector-app/nodes`，请求体 `{"nodeId":"<节点名>"}`。201 响应里直接带有 `nodeId` 和 `token`，**只保存在这台电脑上**，不要打印、不要提交。丢失或泄露时用 `POST /api/v1/admin/collector-app/nodes/{nodeId}/token` 轮换（旧令牌立即失效）。
- 一个节点默认最多登记 4 台不同的模拟器。这台电脑要接更多模拟器时，先调高上限：`PUT /api/v1/admin/collector-app/nodes/{nodeId}/max-devices`，请求体 `{"maxDevices": 8}`（1–64，不能低于已登记的台数）。超出上限的模拟器，它的绑定不会被登记。

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

**5.1）在后台批准新绑定（必须，否则领不到任务）**

节点第一次运行时会把 `app-bindings.json` 里的绑定上报给后端。**新上报的绑定都是“待批准”，批准之前领不到任何任务**（后端返回给节点的状态是 `stopped`、原因 `disabled_by_operator`，与被运营停用的绑定相同）。之后每新增一台模拟器，或者给已有模拟器新增一个平台的绑定，都要再批准一次；已经批准过的绑定不需要重复批准。

1. 列出这个节点待批准的绑定：`GET /api/v1/admin/collector-app/bindings?nodeId=<节点名>`，看 `approvalPending` 为 `true` 的那些；`GET /api/v1/admin/collector-app/alerts` 的 `pendingApprovalBindings` 列出全部节点的。
2. 逐条核对节点、设备（AVD 名称）、平台和账号标签，确认是自己刚加的。
3. 逐条批准：`POST /api/v1/admin/collector-app/bindings/{bindingId}/resume`。节点在下一次上报绑定时得到新的状态，之后开始领取任务。

出现不认识的待批准绑定，或者上报响应里出现 `device_limit_exceeded` 而自己并没有加模拟器，说明这个节点的令牌可能已经泄露：**不要批准**，先轮换令牌（或停用节点），再排查。

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

1. **应用安全问题**：2026-11 的内部安全审计列出了上线前必须处理的五项问题（审计报告不在公开仓库中）。这五项在 2026-11-27 已有代码修复，验证只做到开发机（隔离测试库、本机开发库、本机运行的 API / Worker），上线前还要完成下面几件事。网络白名单和网关不能替代这些修复。
   - **已有的证据**（都来自 2026-11-27 的开发机）：源码与不需要数据库的进程内测试（845 个用例通过）。需要数据库的测试已在六个隔离测试库（Compose MySQL 8.4，执行了 001–052）上运行并通过，例外有两个：`GeminiAnswerPersistenceSqlTests` 没有运行（它只接受 33318 端口的专用实例），一个需要专用库的既有品牌研究用例被跳过。迁移 052 已在这六个隔离库（root）和本机开发库（应用账号，执行前有备份）上执行，回填结果符合预期。本机 API / Worker 已用新构建重启，健康检查为 200；用 HTTP 核对过：匿名 `GET /api/v1/auth/me` 返回 401；App 采集接口不带令牌时，请求体合法、损坏或超限（1.1 MB）都返回 401，带错误令牌加损坏的 JSON 也返回 401；带网页采集令牌时，损坏的 JSON 返回 400，超过上限的请求体返回 413。
   - **还没有的证据**：没有任何一项在真实服务器、真实网关后面或用真实采集端验证过。开发机上后台接口的读取与设备上限接口的反例已用临时管理员会话验证过；开发机上也没有验证的：设备上限接口的成功写入；用真实节点令牌走 App 接口的 413；429 限流（开发环境按设计不生效）；手动采集和自定义计划时隙的积分预占与结算；品牌研究的 409 / 429；真实 PDF 下的解析上限；真实采集端遇到待批准绑定、`device_limit_exceeded`、413、429 时的表现。迁移 052 没有在其他库上执行过。
   - **发布顺序**：先在目标库执行迁移 052，再同时发布 API 和 Worker。没有执行 052 的库上，新版 API 的 App 采集上报、领取、发送、失败接口返回 503。
   - **发布前盘点**：自定义采集计划从发布后的下一个时隙开始扣积分，余额不足的时隙会被跳过；发布前在目标库盘点现有的自定义计划。
   - **前端配套**：品牌研究改为每个品牌同时只能有一个任务，前端需要先改两处（等取消完成再提交；收到 409 时接上响应里给出的现有任务），否则提问弹窗会停在冲突提示上。见 [注册引导说明](registration-onboarding.md)。
   - **新增的运营步骤**：每新增一台模拟器或一个平台绑定，都要在后台批准后才会领取任务（3.1 节）。
   - **仍未处理**：审计中优先级较低的其余事项没有在这一轮修复。
2. **反向代理链**：API 已支持受信转发头（2026-11-27，只有进程内测试，真实网关链路没有验证过）。上线前必须做完三件事：
   - 在 `deploy/.env` 里填 `FRONTEND_PROXY_IP`，并核对 `JOGEOAI_SUBNET`、`FORWARDED_LIMIT`（2.2 节）。
   - 按 2.4 节“转发头”配置网关：宿主机网关覆盖不受信来源的 `X-Forwarded-For`，只保留前端服务器带来的值；前端服务器覆盖浏览器带来的值。
   - 按 2.4 节的核对步骤确认 API 看到的是浏览器 IP，并且伪造的 `X-Forwarded-For` 不被采用。

   不配置时 API 不采用任何转发头，按 IP 的限流会把所有用户算作同一个地址。配置了受信网段但网关原样透传客户端的 `X-Forwarded-For` 时，客户端 IP 可以被伪造，这比不配置更糟。
3. **网关与 TLS**：按 2.4 节配置并实测：`/api/*` 与网页采集路径的白名单生效；App 采集路径不带令牌或带错误令牌时返回 401（请求体是否合法都一样），超过请求体上限返回 413；`/swagger`、`/openapi` 不对外。应用内已经有请求体上限、先鉴权后读请求体和按客户端 IP 的限流（2026-11-27，有进程内测试；请求体上限和先鉴权后读请求体在开发机的 5260 上用 HTTP 核对过，限流没有在运行态验证，真实网关后面都没有验证）。其中限流依赖第 2 条的转发头：没有配置受信转发头，或网关没有写 `X-Forwarded-For` 时，采集接口**没有任何应用内限流**（请求体上限和先鉴权后读请求体仍然生效），只能靠网关。网关限流仍建议配置并实测，目前没有验证过。
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

上表是 2026-11-20 的记录，当时最新的迁移是 051。之后新增的 052（App 绑定审批，2026-11-27）按文件名自动进入镜像，并且仍然排在完成标记脚本 `999-jogeoai-initdb-complete.sh` 之前；带 052 的空库首次启动**还没有重新实测**。

**没有验证的**：x86_64 服务器上的构建、真实生产服务器、网关与 HTTPS、从旧库导入数据（2.5 节）、真实采集任务端到端、多台采集电脑联合运行。

2026-11-27 补充（受信转发头）：`deploy/compose.yml` 固定了网络网段并给 api 传了三个 `ForwardedHeaders__*` 变量，只用 `docker compose … config` 检查过能正确展开，**没有启动容器**。固定网段后的网络创建、容器内看到的对端地址、Caddy / Nginx 的实际转发头，都没有验证过。
