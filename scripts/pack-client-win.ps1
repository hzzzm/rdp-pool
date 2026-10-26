# Build Windows x64「解压即用」zip（内置 .NET 6 运行时，本机可不装 SDK/Runtime）.
# 入口：RdpPoolSetup.exe + RdpPoolClient.exe（均为 WinExe，无黑框）；实验室自签 Authenticode。
param(
  [string]$DotnetPath = "",
  [string]$RuntimeRoot = "",
  [string]$OutZip = "D:\Code\rpd-pool\dist\RdpPoolClient-win-x64.zip",
  [switch]$SkipSign
)

$ErrorActionPreference = "Stop"
$Repo = Resolve-Path "$PSScriptRoot\.."
$ClientProj = Join-Path $Repo "client\RdpPoolClient\RdpPoolClient.csproj"
$SetupProj = Join-Path $Repo "client\RdpPoolSetup\RdpPoolSetup.csproj"
$Pub = Join-Path $Repo "client\publish\win-x64-fd"
$Stage = Join-Path (Split-Path $OutZip) "RdpPoolClient-win-x64"
$LauncherSrc = Join-Path $Repo "client\launchers"
$SignDir = Join-Path $Repo "client\signing"

if (-not $DotnetPath) {
  foreach ($c in @(
      "D:\tmp\dotnet\dotnet.exe",
      "$env:ProgramFiles\dotnet\dotnet.exe",
      "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    )) {
    if (Test-Path $c) { $DotnetPath = $c; break }
  }
}
if (-not $DotnetPath -or -not (Test-Path $DotnetPath)) {
  throw "dotnet not found. Install .NET 6 SDK (Windows Desktop) or pass -DotnetPath"
}
if (-not $RuntimeRoot) { $RuntimeRoot = Split-Path $DotnetPath }

$fxr = Get-ChildItem (Join-Path $RuntimeRoot "host\fxr") -Directory -ErrorAction SilentlyContinue |
  Sort-Object Name -Descending | Select-Object -First 1
if (-not $fxr) { throw "hostfxr missing under $RuntimeRoot — need a full .NET 6 install with Windows Desktop" }
$ver = $fxr.Name
$netCore = Join-Path $RuntimeRoot "shared\Microsoft.NETCore.App\$ver"
$desktop = Join-Path $RuntimeRoot "shared\Microsoft.WindowsDesktop.App\$ver"
if (-not (Test-Path $netCore)) { throw "missing $netCore" }
if (-not (Test-Path $desktop)) { throw "missing $desktop (WindowsDesktop runtime required)" }

$csc = @(
  "${env:WINDIR}\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
  "${env:WINDIR}\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "csc.exe not found (need .NET Framework 4.x for portable launchers)" }

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
  Sort-Object FullName -Descending |
  Select-Object -First 1 -ExpandProperty FullName

Write-Host "Using $DotnetPath (bundle runtime $ver from $RuntimeRoot)"
Write-Host "Launcher csc: $csc"
if (Test-Path $Pub) { Remove-Item -Recurse -Force $Pub }
New-Item -ItemType Directory -Force -Path $Pub | Out-Null

& $DotnetPath publish $ClientProj -c Release -r win-x64 --self-contained false -o $Pub
if ($LASTEXITCODE -ne 0) { throw "publish RdpPoolClient failed" }
& $DotnetPath publish $SetupProj -c Release -r win-x64 --self-contained false -o $Pub
if ($LASTEXITCODE -ne 0) { throw "publish RdpPoolSetup failed" }

New-Item -ItemType Directory -Force -Path (Split-Path $OutZip) | Out-Null
if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null
Copy-Item (Join-Path $Pub '*') $Stage -Recurse -Force

# 去掉 SDK 生成的 apphost，改用 netfx WinExe 启动器
foreach ($name in @("RdpPoolClient.exe", "RdpPoolSetup.exe")) {
  $p = Join-Path $Stage $name
  if (Test-Path $p) { Remove-Item -Force $p }
}

$wf = "${env:WINDIR}\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll"
if (-not (Test-Path $wf)) {
  $wf = "${env:WINDIR}\Microsoft.NET\Framework\v4.0.30319\System.Windows.Forms.dll"
}

$clientExe = Join-Path $Stage "RdpPoolClient.exe"
$setupExe = Join-Path $Stage "RdpPoolSetup.exe"
$appIco = Join-Path $Repo "client\assets\app.ico"
$icoArg = @()
if (Test-Path $appIco) { $icoArg = @("/win32icon:$appIco") }

# 两个入口均为 /target:winexe（无控制台子系统）
& $csc /nologo /optimize+ /target:winexe "/r:$wf" "/out:$clientExe" @icoArg (Join-Path $LauncherSrc "LaunchClient.cs")
if ($LASTEXITCODE -ne 0) { throw "compile RdpPoolClient.exe launcher failed" }

& $csc /nologo /optimize+ /target:winexe "/r:$wf" "/out:$setupExe" @icoArg (Join-Path $LauncherSrc "LaunchSetup.cs")
if ($LASTEXITCODE -ne 0) { throw "compile RdpPoolSetup.exe launcher failed" }

Copy-Item (Join-Path $Repo "client\appsettings.example.json") (Join-Path $Stage "appsettings.json") -Force

$rt = Join-Path $Stage "dotnet"
New-Item -ItemType Directory -Force -Path (Join-Path $rt "host\fxr\$ver") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $rt "shared\Microsoft.NETCore.App\$ver") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $rt "shared\Microsoft.WindowsDesktop.App\$ver") | Out-Null
Copy-Item (Join-Path $fxr.FullName '*') (Join-Path $rt "host\fxr\$ver\") -Recurse -Force
Copy-Item (Join-Path $netCore '*') (Join-Path $rt "shared\Microsoft.NETCore.App\$ver\") -Recurse -Force
Copy-Item (Join-Path $desktop '*') (Join-Path $rt "shared\Microsoft.WindowsDesktop.App\$ver\") -Recurse -Force
Copy-Item (Join-Path $RuntimeRoot "dotnet.exe") (Join-Path $rt "dotnet.exe") -Force

# --- 实验室自签 Authenticode ---
$cerOut = Join-Path $Stage "RdpPoolLab-CodeSigning.cer"
if (-not $SkipSign) {
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo "scripts\ensure-lab-codesign-cert.ps1") -CertDir $SignDir | Out-Host
  $cerSrc = Join-Path $SignDir "RdpPoolLab-CodeSigning.cer"
  $pfxSrc = Join-Path $SignDir "RdpPoolLab-CodeSigning.pfx"
  $pwdFile = Join-Path $SignDir "pfx-password.txt"
  if (-not (Test-Path $cerSrc) -or -not (Test-Path $pfxSrc)) {
    throw "signing cert export missing under $SignDir"
  }
  Copy-Item $cerSrc $cerOut -Force
  Copy-Item (Join-Path $SignDir "install-lab-cert.ps1") (Join-Path $Stage "install-lab-cert.ps1") -Force

  if ($signtool -and (Test-Path $signtool)) {
    $pwd = (Get-Content $pwdFile -Raw).Trim()
    foreach ($exe in @($clientExe, $setupExe)) {
      & $signtool sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com `
        /f $pfxSrc /p $pwd $exe
      if ($LASTEXITCODE -ne 0) {
        Write-Warning "timestamp sign failed for $exe, retry without timestamp"
        & $signtool sign /fd SHA256 /f $pfxSrc /p $pwd $exe
        if ($LASTEXITCODE -ne 0) { throw "signtool failed: $exe" }
      }
      Write-Host "Signed: $exe"
      Get-AuthenticodeSignature $exe | Format-List Status, StatusMessage, SignerCertificate
    }
  } else {
    Write-Warning "signtool.exe not found — shipping unsigned (SAC may block)"
  }
} else {
  Write-Host "SkipSign: not signing"
}

# 清除 MOTW（若从网络复制过）
Get-ChildItem $Stage -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
  try { Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue } catch {}
  $zone = $_.FullName + ":Zone.Identifier"
  if (Test-Path $zone) { Remove-Item $zone -Force -ErrorAction SilentlyContinue }
}

@'
# RDP 资源池客户端（Windows x64 · 解压即用）

本包已内置 .NET 6 运行时，本机不必单独安装 .NET。
需要本机有「远程桌面连接」(mstsc)。
RdpPoolSetup.exe / RdpPoolClient.exe 为 Windows 应用（无黑色 CMD 窗口）。
运行日志：同目录 rdp-pool-client.log / rdp-pool-setup.log。

## 启动

1. 解压全部文件到任意文件夹（保留 dotnet 子目录）
2. 双击 RdpPoolSetup.exe：填写服务器地址、用户名、密码并保存（会先校验登录）
3. 双击 RdpPoolClient.exe 登录并分配资源

## 若被「智能应用控制 / Smart App Control」拦截

Win11 对「发布者未验证」的程序可能直接拦截。本包使用【实验室自签】证书签名，
不是商业 EV/OV 证书，因此需要你在本机信任该证书，或临时调整 SAC。

### 方法 A（推荐 · 实验室）：安装自签证书

1. 右键「install-lab-cert.ps1」→ 使用 PowerShell 运行
   （若提示权限：开始菜单搜 PowerShell → 右键「以管理员身份运行」，然后执行：
   Set-Location "解压目录"
   Set-ExecutionPolicy -Scope Process Bypass
   .\install-lab-cert.ps1 ）
2. 脚本会把 RdpPoolLab-CodeSigning.cer 装入：
   - 本地计算机\受信任的根证书颁发机构
   - 本地计算机\受信任的发布者
3. 关闭资源管理器窗口后重新打开，再双击 RdpPoolSetup.exe

验证签名（可选）：
  Get-AuthenticodeSignature .\RdpPoolSetup.exe
  期望 Status 为 Valid（安装证书后）或 UnknownError/NotTrusted（安装前）

### 方法 B：临时关闭智能应用控制（仅实验机）

1. 设置 → 隐私和安全性 → Windows 安全中心 → 应用和浏览器控制
2. 智能应用控制 → 关闭
   （部分版本路径：Windows 安全中心 → 应用和浏览器控制 → 智能应用控制设置）
3. 注意：SAC 一旦关闭，很多版本无法在不重装系统的情况下重新打开。

### 方法 C：其它缓解

- 用「解除锁定」：右键 exe → 属性 → 勾选「解除锁定」（仅对 MOTW/SmartScreen 有时有效；SAC 仍可能拦未信任签名）
- 将解压目录放到已排除扫描的路径（视企业策略而定）
- 内网分发时由 IT 用正式代码签名证书重签，或通过 Intune/软件中心下发

本实验室证书仅证明「未被篡改且由打包机签发」，不能代替商业公证，请勿用于公网分发。

## 配置（appsettings.json）

- ApiBaseUrl / LoginProtected / PasswordProtected：由 Setup 在登录校验成功后写入
- SkipMstsc / AutoRelease / PreferredGroupId / ConnectHoldSec：非敏感项
- DisplayResolution：本地显示（fullscreen / match_local / 1280x720 等），不上传服务器
  可在 Setup「显示分辨率」+「仅保存显示」、或客户端托盘菜单「显示分辨率…」修改，无需重新登录

控制面需已启动。仅 Windows x64。账密 DPAPI 与 Windows 用户绑定。
'@ | Set-Content (Join-Path $Stage "README.txt") -Encoding UTF8

if (Test-Path $OutZip) { Remove-Item -Force $OutZip }
# Compress-Archive 可能带上异常流；先清 zone 再压
Compress-Archive -Path (Join-Path $Stage '*') -DestinationPath $OutZip -CompressionLevel Optimal -Force
Unblock-File -Path $OutZip -ErrorAction SilentlyContinue
Write-Host "OK: $OutZip ($([math]::Round((Get-Item $OutZip).Length/1MB,2)) MB)"
