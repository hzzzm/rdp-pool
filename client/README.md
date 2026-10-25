# Windows 客户端

带自动凭据注入（cmdkey / `.rdp` / MsTscAx）。仅 Windows 会调 `mstsc`。

## 解压即用（推荐给终端用户）

```powershell
# 在开发机打包（需本机有 .NET 6 Windows Desktop）
..\scripts\pack-client-win.ps1
# 产出：D:\Code\rpd-pool\dist\RdpPoolClient-win-x64.zip
```

解压后编辑 `appsettings.json`，双击 **`启动.cmd`**。包内已带运行时，**不必安装 .NET**。

## 开发机直接跑

依赖：**.NET 6 SDK**（Windows Desktop）。

```powershell
cd RdpPoolClient
copy ..\appsettings.example.json .\appsettings.json
# 填写 ApiBaseUrl（默认 http://127.0.0.1:8081）、Login/Password
# 测试机关机时: "SkipMstsc": true

dotnet run -- appsettings.json
```

流程对齐架构 §6.6：登录 → 可用组 0/1/>1 → acquire → 注入控制面返回的资源账密 → 建连 →（退出）清理本地凭据并 `release`。

**凭据注入（有票据密码时）**

1. `cmdkey` 预存 `TERMSRV/<host>`（裸用户名 + `host\user`）
2. 临时 `.rdp`：`password 51`（DPAPI）+ `prompt for credentials:i:0`
3. **优先** MsTscAx `AdvancedSettings*.ClearTextPassword`（不经 mstsc 凭据框）
4. ActiveX 不可用时回退 `mstsc` + `.rdp`；退出/Ctrl+C 时删除 cmdkey 与临时文件

`ConnectHoldSec`：冒烟时可设为正整数，Connected 后自动退出。仅当服务端未配置资源密码时才需手输。日志脱敏 `password`。
