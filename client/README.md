# Windows 客户端骨架

目标：.NET 6+ 控制台/WPF 最小流程，本地 `mstsc` 直连（架构 §2.4 A）。

## 配置

复制 `appsettings.example.json` → `appsettings.json`（勿提交真实密码）：

| 字段 | 说明 |
|---|---|
| `ApiBaseUrl` | Go API，如 `http://127.0.0.1:8081` |
| `Login` / `Password` | 终端用户（池库 `pool_users`） |
| `PreferredGroupId` | 可选；多组时记忆上次选择 |

测试机 host 字段名见根目录 `.env.example` 的 `DEMO_RESOURCE_HOST`（示例值 `10.126.126.4`）；**真实 RDP 密码只放本地 env，不入库**。

## 启动流程（与 §6.6 对齐）

1. 登录 `POST /api/v1/auth/login`
2. `GET /api/v1/groups/available`
   - 0 → 报错退出
   - 1 → 跳过选组
   - >1 → 选组
3. `POST /api/v1/sessions/acquire { group_id }`
4. allocated → 写临时 `.rdp` → `mstsc`；queued → 轮询 `/queue/me`
5. 心跳 `POST /sessions/{id}/heartbeat`；410 → 会话失效/被踢旧
6. 退出时 `POST /sessions/{id}/release`（clean 队首）

本目录 `RdpPoolClient/` 为最小可编译控制台骨架；GUI 后续补。
