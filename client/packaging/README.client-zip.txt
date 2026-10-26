# RDP 资源池客户端（Windows x64 · 解压即用）

本包已内置 .NET 6 运行时，本机不必单独安装 .NET。
需要本机有「远程桌面连接」(mstsc)。
RdpPoolSetup.exe / RdpPoolClient.exe 为 Windows 应用（无黑色 CMD 窗口）。
运行日志：同目录 rdp-pool-client.log / rdp-pool-setup.log。

## 启动（首次务必先装证书）

1. 解压全部文件到任意文件夹（保留 dotnet 子目录与本 README）
2. 【管理员】安装实验室签名证书（见下方「方法 A」）——不解压后直接双击常被 SAC 拦截
3. 关闭资源管理器后重新打开解压目录
4. 双击 RdpPoolSetup.exe：填写服务器地址、用户名、密码并保存（会先校验登录）
5. 双击 RdpPoolClient.exe 登录并分配资源

本包 RdpPoolSetup.exe / RdpPoolClient.exe 均已用实验室自签证书 Authenticode 签名，
并附带 RdpPoolLab-CodeSigning.cer 与 install-lab-cert.ps1。

## 若被「智能应用控制 / Smart App Control」拦截

Win11 SAC 对「无微软云信誉」的程序可能直接拦截。本包是【实验室自签】，
不是商业 EV/OV，因此必须本机信任证书；且即便已信任，SAC 仍可能拦截自签程序
（已知限制：信任根证书不等于 SAC 放行）。

### 方法 A（必做 · 实验室）：管理员安装自签证书

开始菜单搜索「Windows PowerShell」→ 右键「以管理员身份运行」，然后执行
（把路径换成你的解压目录）：

  Set-Location "D:\Code\rpd-pool\dist\RdpPoolClient-win-x64"
  Set-ExecutionPolicy -Scope Process Bypass
  .\install-lab-cert.ps1

脚本会把 RdpPoolLab-CodeSigning.cer 装入：
  - 本地计算机\受信任的根证书颁发机构 (LocalMachine\Root)
  - 本地计算机\受信任的发布者 (LocalMachine\TrustedPublisher)
并复核两个存储均含该证书指纹。

装完后：
  1) 关掉所有资源管理器窗口再打开解压目录
  2) 再双击 RdpPoolSetup.exe

验证签名（可选）：
  Get-AuthenticodeSignature .\RdpPoolSetup.exe
  Get-AuthenticodeSignature .\RdpPoolClient.exe
  安装证书后 Status 常为 Valid；安装前多为 UnknownError / NotTrusted（仍表示已签名）。

### 方法 B：证书已装仍被 SAC 拦（已知 quirk · 仅实验机）

即使 LocalMachine Root + TrustedPublisher 已信任自签根，SAC 仍可能因「无云端信誉」
拦截。此时只能：

1. 设置 → 隐私和安全性 → Windows 安全中心 → 应用和浏览器控制
2. 智能应用控制 → 关闭
   （部分版本：Windows 安全中心 → 应用和浏览器控制 → 智能应用控制设置）
3. 注意：SAC 一旦关闭，很多 Win11 版本无法在不重装系统的情况下重新打开。

### 方法 C：其它缓解

- 「解除锁定」：右键 exe → 属性 → 勾选「解除锁定」（只对 MOTW/SmartScreen 有时有效；挡不住 SAC）
- 企业 IT 用正式代码签名证书重签，或经 Intune/软件中心下发

本实验室证书仅证明「未被篡改且由打包机签发」，不能代替商业公证，请勿用于公网分发。

## 配置（appsettings.json）

- ApiBaseUrl / LoginProtected / PasswordProtected：由 Setup 在登录校验成功后写入
- SkipMstsc / AutoRelease / PreferredGroupId / ConnectHoldSec：非敏感项
- DisplayResolution：本地显示（fullscreen / match_local / 1280x720 等），不上传服务器
  可在 Setup「显示分辨率」+「仅保存显示」、或客户端托盘菜单「显示分辨率…」修改，无需重新登录

控制面需已启动。仅 Windows x64。账密 DPAPI 与 Windows 用户绑定。
