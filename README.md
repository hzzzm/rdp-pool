# RDP Pool — Windows 本机跑通（MVP）

架构 **v1.8**：RuoYi-Vue + Go 控制面、Nginx 双 Token、RDP **直连**、资源组、软释放队尾、新登录踢旧。

本 README 面向：**在你自己的 Windows 电脑上**用 Docker Desktop 起服务，再用客户端调起 `mstsc`。

## 前置

| 组件 | 说明 |
|---|---|
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | 启用 WSL2 后端即可 |
| [.NET 6 SDK](https://dotnet.microsoft.com/download) | 仅跑客户端需要 |
| 测试 Windows | 与本机同网可达 `host:3389`；关机时可只测 API，不开 mstsc |

## 一、启动控制面（一条命令）

在仓库根目录 PowerShell：

```powershell
copy .env.example .env
# 编辑 .env：填 DEMO_RESOURCE_HOST / DEMO_RDP_USERNAME / DEMO_RDP_PASSWORD（真实密码只放本地 .env）

cd deploy
docker compose --env-file .env up -d --build
docker compose --env-file .env --profile seed run --rm seed
curl http://127.0.0.1:8081/healthz
```

预期：`{"status":"ok"}`。经 Nginx：`http://127.0.0.1:8080/pool-api/healthz`。

停止：`docker compose down`（加 `-v` 会清 MySQL 数据）。

### `.env` 关键字段（勿提交）

| 字段 | 用途 |
|---|---|
| `DEMO_RESOURCE_HOST` | 测试机 IP，例 `10.126.126.4` |
| `DEMO_RDP_USERNAME` / `DEMO_RDP_PASSWORD` | 进 vault，供 acquire 票据；**只写本地 .env** |
| `DEMO_POOL_USER` / `DEMO_POOL_PASSWORD` | 终端用户账号（默认 `demo` / `demo123`） |

也可不用 seed，改用 Admin API（见下）。

## 二、录入测试机（两种方式）

### A. Compose seed（推荐）

见上一节 `docker compose --profile seed run --rm seed`。幂等：同 host 会更新凭据。

### B. Admin API

```powershell
# 1) 换 admin_token（脚手架：不校验若依）
$ex = Invoke-RestMethod -Method POST http://127.0.0.1:8081/api/v1/admin/auth/exchange `
  -ContentType application/json -Body '{"login":"admin","admin_id":"admin"}'
$h = @{ Authorization = "Bearer $($ex.admin_token)" }

# 2) 建组
$g = Invoke-RestMethod -Method POST http://127.0.0.1:8081/api/v1/admin/groups `
  -Headers $h -ContentType application/json `
  -Body '{"code":"office","name":"办公桌面"}'

# 3) 建资源（host 填测试机；凭据后续可走 credentials API）
Invoke-RestMethod -Method POST http://127.0.0.1:8081/api/v1/admin/resources `
  -Headers $h -ContentType application/json `
  -Body (@{ group_id = $g.id; name = "win-test-01"; host = "10.126.126.4"; rdp_port = 3389 } | ConvertTo-Json)
```

终端用户仍建议用 seed 创建 `demo` 并授权组；否则需自行插 `pool_users` / grants。

## 三、Windows 客户端（登录→选组→分配→mstsc）

```powershell
cd client\RdpPoolClient
copy ..\appsettings.example.json .\appsettings.json
# 编辑 ApiBaseUrl / Login / Password；若测试机关机可设 "SkipMstsc": true

dotnet run -- appsettings.json
```

流程：登录 → `groups/available`（0 报错 / 1 跳过 / >1 选组）→ `acquire`（票据含 host/port/username/password）→ DPAPI `.rdp` + `cmdkey` + `mstsc` → Enter/退出后清理本地凭据并 `release`。

控制面已配置 RDP 密码时，客户端应尽量直连、不弹本机凭据框；未配置密码才允许手输。

若测试机关机或 3389 不通：设 `SkipMstsc: true` 只验证 API 分配链。运行客户端时若本机设了 HTTP 代理，请对 `127.0.0.1,localhost` 设 `NO_PROXY`。

## 四、冒烟检查清单

1. `curl http://127.0.0.1:8081/healthz` → ok  
2. seed 成功（日志含 resource host）  
3. 客户端登录 + acquire 返回 `rdp_ticket.host`  
4. 测试机开机且同网 → mstsc 能连；否则停在步骤 3 即可  

## 仓库结构

```text
server/   Go API + migrations + seed
deploy/   docker-compose（mysql / redis / go-api / nginx / seed profile）
admin/    若依挂载说明
client/   .NET 6 控制台客户端
```

## 开发说明

- Go API 启动会 **重试 MySQL**（约 30s），适配 Docker Desktop 就绪竞态。  
- RDP 密码经 AES-GCM 存 `resource_secrets`；主密钥 `CREDENTIAL_AES_KEY`（32 字节）。  
- 云端 Agent 访问不到你家局域网 `10.126.126.4` 属预期；本机联调在你 Windows 上完成。
