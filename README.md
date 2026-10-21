# RDP Pool — Windows 远程桌面资源池（MVP 骨架）

按架构 **v1.8** 已选定项：

- **RuoYi-Vue + Go 控制面**（调度不进若依 Java）
- **Nginx 路径分流 + 管理端双 Token**
- **RDP 直连**（控制面不转发像素）
- 资源组、软释放队尾(linger)、叠态**新登录踢旧**

## 仓库结构

```text
server/     Go API（登录、groups/available、acquire/queue/heartbeat/release、admin）
admin/      若依挂载说明 + 菜单 SQL + rdp API 封装示例
client/     Windows 客户端最小流程骨架（.NET 6）
deploy/     docker-compose（mysql、redis、go-api、nginx）
```

## 快速启动（优先打通 Go + MySQL）

```bash
cp .env.example .env          # 本地填写密钥；勿提交 .env
cd deploy
docker compose up -d --build
curl http://127.0.0.1:8081/healthz
# 或经 Nginx：curl http://127.0.0.1:8080/pool-api/healthz
```

种子数据（可选，本机 Go）：

```bash
cd server
go run ./cmd/seed
```

本地直接跑 API（需已有 MySQL 且执行过 `server/migrations/001_init.sql`）：

```bash
cd server
go mod tidy
export MYSQL_DSN='rdp_app:rdp_pass@tcp(127.0.0.1:3306)/rdp_pool?parseTime=true&loc=Local&charset=utf8mb4'
go run ./cmd/api
```

## `.env.example` 关键字段

| 字段 | 含义 |
|---|---|
| `DEMO_RESOURCE_HOST` | 测试 Windows，示例 `10.126.126.4` |
| `DEMO_RDP_USERNAME` | RDP 账号占位 |
| `DEMO_RDP_PASSWORD` | **仅本地**，勿提交 |

## API 摘要

| 终端用户 | 管理（`/pool-api`） |
|---|---|
| `POST /api/v1/auth/login` | `POST /api/v1/admin/auth/exchange` |
| `GET /api/v1/groups/available` | CRUD groups/resources/pool-users |
| `POST /api/v1/sessions/acquire` | `POST .../resources/{id}/soft-release` |
| heartbeat / release / queue | settings / sessions / queues / audit |

软释放语义：用户主动 → clean 队首 + 可唤醒；admin/TTL → linger 队尾 + 不立即唤醒。踢旧发生在新占用建连时。

## 下一步建议

1. 补全 claim / ticket vault（AES-GCM）/ WS 排队 / Reaper
2. 挂载官方 RuoYi-Vue，打通 admin_token 兑换校验
3. 客户端写 `.rdp` + mstsc，联调测试机 3389
4. IdleOffer 防饿死扫描与亲和抢占完善

## 安全

- `.gitignore` 已忽略 `.env`、密钥、`internal/` 拷贝
- 仓库内不得出现真实密码或 Token
