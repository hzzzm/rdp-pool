# 管理端（若依 RuoYi-Vue）集成说明

按架构 **v1.8 §2.2 方案 A**：若依只做系统管理壳；池调度仍在 Go。

## 挂载方式（推荐，避免整仓拷贝）

1. 另目录克隆官方脚手架：
   ```bash
   git clone https://gitee.com/y_project/RuoYi-Vue.git ruoyi-vue
   ```
2. 将本目录下文件拷入对应位置：
   - `menu/rdp-menu.sql` → 若依菜单初始化
   - `api-stubs/rdp/*.js` → `ruoyi-vue/src/api/rdp/`
   - 页面可先放占位 `views/rdp/`（后续补）
3. Nginx（见 `deploy/nginx/default.conf`）：
   - `/` → 若依 Vue
   - `/prod-api/` → 若依 Java
   - `/pool-api/` → Go API（管理员请求带 `admin_token`）

## 双 Token（§2.3 桥接 1）

1. 浏览器先 `/prod-api` 登录若依，持若依 Token。
2. 进入「远程桌面资源池」前调用：
   `POST /pool-api/api/v1/admin/auth/exchange` → 得到短时 `admin_token`。
3. `src/api/rdp/*` 独立 Axios 实例，只带 `Authorization: Bearer <admin_token>`。
4. **禁止**与终端用户 JWT / 若依 Token 共用同一 localStorage 键。

## 权限字（示例）

| 权限字 | 用途 |
|---|---|
| `rdp:group:list` / `rdp:group:edit` | 资源组 |
| `rdp:resource:list` / `rdp:resource:softRelease` | 资源 / 软释放 |
| `rdp:user:grantGroup` | 用户↔组授权 |
| `rdp:config:edit` | 池配置 |

软释放二次确认文案须写明：**不断开 RDP，且该机进入组内空闲供给队尾(linger)**。
