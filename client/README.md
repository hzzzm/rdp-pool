# Windows 客户端

带自动凭据注入（cmdkey / `.rdp` / MsTscAx）。仅 Windows 会调 `mstsc`。

## 解压即用（推荐给终端用户）

```powershell
# 在开发机打包（需本机有 .NET 6 Windows Desktop）
..\scripts\pack-client-win.ps1
# 产出：D:\Code\rpd-pool\dist\RdpPoolClient-win-x64.zip
```

解压后：

1. 双击 **`RdpPoolSetup.exe`** → 填写服务器地址、用户名、密码 → 保存  
2. **保存前会调用控制面登录校验**；失败则不写配置并提示中文错误  
3. 双击 **`RdpPoolClient.exe`** 登录并分配资源（托盘会显示应用图标）  

**本地显示分辨率**（类似 mstsc「显示」，不上传服务器）：`appsettings.json` 字段 `DisplayResolution`（`fullscreen` / `match_local` / `1280x720` 等）。可在 Setup 下拉框用「仅保存显示」修改，或在客户端运行中托盘右键「显示分辨率…」——**无需重新登录**。连接时写入 MsTscAx `DesktopWidth`/`DesktopHeight`/`FullScreen` 与 `.rdp` 的 `screen mode id` / `desktopwidth` / `desktopheight`。

包内已带运行时，**不必安装 .NET**。无 `启动.cmd`。应用图标见 `assets/app.ico`（16/32/48/256）。
`RdpPoolClient.exe` / `RdpPoolSetup.exe` 为 **WinExe**（无黑色 CMD）。日志：`rdp-pool-client.log`。

若 Win11「智能应用控制」拦截：见 zip 内 `README.txt` 与 `install-lab-cert.ps1`（安装实验室自签证书到本机受信任根/发布者）。

### 凭据如何存放

- `appsettings.json` **不**保存明文 `Login` / `Password`
- Setup **仅在登录成功后**写入 `LoginProtected` / `PasswordProtected`：`ProtectedData`（DPAPI **CurrentUser**）→ Base64，以及 `ApiBaseUrl`
- 密文仅同一 Windows 用户在本机可解；换用户/换机请重新 Setup
- **旧版迁移**：若仍有明文 `Login`/`Password`，主程序首次加载会加密回写并清除明文字段

## 开发机直接跑

依赖：**.NET 6 SDK**（Windows Desktop）。

```powershell
cd RdpPoolSetup
dotnet run
# 或
cd RdpPoolClient
dotnet run -- path\to\appsettings.json
```

也可用 Setup 生成加密配置后再 `dotnet run`。测试机关机时在 Setup 勾选「仅验证 API」，或设 `"SkipMstsc": true`。

流程对齐架构 §6.6：登录 → 可用组 0/1/>1 → acquire → 注入控制面返回的资源账密 → 建连 →（退出）清理本地凭据并 `release`。

**凭据注入（有票据密码时）**

1. `cmdkey` 预存 `TERMSRV/<host>`（裸用户名 + `host\user`）
2. 临时 `.rdp`：`password 51`（DPAPI）+ `prompt for credentials:i:0`
3. **优先** MsTscAx `AdvancedSettings*.ClearTextPassword`（不经 mstsc 凭据框）
4. ActiveX 不可用时回退 `mstsc` + `.rdp`；退出/Ctrl+C 时删除 cmdkey 与临时文件

`ConnectHoldSec`：冒烟时可设为正整数，Connected 后自动退出。仅当服务端未配置资源密码时才需手输。日志脱敏 `password`。
