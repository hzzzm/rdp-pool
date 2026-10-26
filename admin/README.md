# 管理端（若依 RuoYi-Vue3）集成说明

按架构 **v1.8 §2.2 方案 A**：若依只做系统管理壳；池调度仍在 Go。  
鉴权 **§2.3 桥接 1**：Nginx 路径分流 + 管理端双 Token。

## 本机访问（Compose）

| 项 | 值 |
|---|---|
| **管理 UI** | http://127.0.0.1:8080/ |
| **若依登录** | `admin` / `admin123` |
| `/prod-api/` | **Go RuoYi shim**（登录 / getInfo / getRouters；Maven 不通时的 MVP）。可选 profile `ruoyi-java` 切回 Spring Boot |
| `/pool-api/` | Go 控制面 Admin API（资源组/资源/会话/软释放/配置） |
| Go 直连（调试） | http://127.0.0.1:8081/ |

本机验证码关闭（shim 固定 `captchaEnabled=false`；Java 库亦已关）。

## 目录

| 路径 | 用途 |
|---|---|
| `vendor/RuoYi-Vue` | 若依 Java（gitignored，`scripts/fetch-ruoyi.ps1` 拉取） |
| `vendor/RuoYi-Vue3` | 若依 Vue3 前端（同上） |
| `overlay/ui/` | RDP 业务页 + `/pool-api` Axios（覆盖进 Vue3） |
| `overlay/java/` | Compose 用 `application*.yml` |
| `docker/` | `Dockerfile.admin` / `Dockerfile.ui` / nginx |
| `menu/rdp-menu.sql` | 菜单与权限字（同步到 `deploy/mysql/init/03_rdp_menu.sql`） |

## 首次构建（Go shim MVP，推荐）

```powershell
# 1) 拉若依 Vue3 源码到 admin/vendor
.\scripts\fetch-ruoyi.ps1

# 2) 本机构建 UI → admin/ui-dist（Dockerfile.ui 打进 nginx）
.\scripts\build-admin-ui.ps1

# 3) 起栈：MySQL + Redis + go-api（含 /ruoyi shim）+ nginx(UI)
cd deploy
$env:HTTP_PROXY='http://10.126.126.99:1070'; $env:HTTPS_PROXY=$env:HTTP_PROXY
docker compose --env-file ..\.env up -d --build go-api nginx

# 可选：种子数据（组/演示用户/Windows 资源）
docker compose --env-file ..\.env --profile seed run --rm seed
```

完整 Java 若依（需 Maven 镜像可达）才用：`docker compose --profile ruoyi-java up -d --build`。

## 双 Token（§2.3）

1. 浏览器先 `/prod-api` 登录（shim 或 Java），持若依 Token（`Admin-Token` Cookie）。
2. 打开「远程桌面资源池」任一页时，`request-pool.js` 自动：
   `POST /pool-api/api/v1/admin/auth/exchange`（body/`X-RuoYi-Token` 带若依 JWT）→ 短时 `admin_token`（Cookie `Pool-Admin-Token`）。
3. `src/api/rdp/*` 只带 `Authorization: Bearer <admin_token>`；可附带 `X-RuoYi-Token`。
4. **禁止**与终端用户 JWT / 若依 Token 共用同一本地键。

Shim MVP：exchange **校验** `kind=ruoyi_shim` 的 HS256 JWT（`ADMIN_JWT_SECRET`）后再签发 `admin_token`。切 Java 后改为校验若依 JWT/公钥。

## 业务页（经 Go Admin API）

| 菜单 | 路由组件 | 主要能力 |
|---|---|---|
| 资源组 | `rdp/group/index` | CRUD 列表/新建 |
| Windows 资源 | `rdp/resource/index` | 录入、凭据、**软释放** |
| 终端用户 | `rdp/user/index` | 池用户 + 组授权 |
| 会话与排队 | `rdp/session/index` | 会话/排队只读 |
| 池配置 | `rdp/settings/index` | `max_occupation_ttl_sec` 等 + 审计 |

## 权限字

| 权限字 | 用途 |
|---|---|
| `rdp:group:list` / `rdp:group:edit` | 资源组 |
| `rdp:resource:list` / `rdp:resource:softRelease` | 资源 / 软释放 |
| `rdp:user:grantGroup` | 用户↔组授权 |
| `rdp:config:edit` | 池配置 |

软释放二次确认文案须写明：**不断开 RDP，且该机进入组内空闲供给队尾(linger)**。
