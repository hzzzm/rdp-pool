# Build Windows x64「解压即用」zip（内置 .NET 6 运行时，本机可不装 SDK/Runtime）.
# 入口：RdpPoolSetup.exe（配置）+ RdpPoolClient.exe（主程序）；无 启动.cmd。
# Prefer local SDK at D:\tmp\dotnet when PATH has no dotnet.
param(
  [string]$DotnetPath = "",
  [string]$RuntimeRoot = "",
  [string]$OutZip = "D:\Code\rpd-pool\dist\RdpPoolClient-win-x64.zip"
)

$ErrorActionPreference = "Stop"
$Repo = Resolve-Path "$PSScriptRoot\.."
$ClientProj = Join-Path $Repo "client\RdpPoolClient\RdpPoolClient.csproj"
$SetupProj = Join-Path $Repo "client\RdpPoolSetup\RdpPoolSetup.csproj"
$Pub = Join-Path $Repo "client\publish\win-x64-fd"
$Stage = Join-Path (Split-Path $OutZip) "RdpPoolClient-win-x64"
$LauncherSrc = Join-Path $Repo "client\launchers"

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

# 去掉 SDK 生成的 apphost，改用 netfx 启动器（设置 DOTNET_ROOT）
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
& $csc /nologo /optimize+ /target:exe "/out:$clientExe" (Join-Path $LauncherSrc "LaunchClient.cs")
if ($LASTEXITCODE -ne 0) { throw "compile RdpPoolClient.exe launcher failed" }

& $csc /nologo /optimize+ /target:winexe "/r:$wf" "/out:$setupExe" (Join-Path $LauncherSrc "LaunchSetup.cs")
if ($LASTEXITCODE -ne 0) { throw "compile RdpPoolSetup.exe launcher failed" }

# 模板配置：无明文账密；用户用 Setup 写入 DPAPI 字段
Copy-Item (Join-Path $Repo "client\appsettings.example.json") (Join-Path $Stage "appsettings.json") -Force

$rt = Join-Path $Stage "dotnet"
New-Item -ItemType Directory -Force -Path (Join-Path $rt "host\fxr\$ver") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $rt "shared\Microsoft.NETCore.App\$ver") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $rt "shared\Microsoft.WindowsDesktop.App\$ver") | Out-Null
Copy-Item (Join-Path $fxr.FullName '*') (Join-Path $rt "host\fxr\$ver\") -Recurse -Force
Copy-Item (Join-Path $netCore '*') (Join-Path $rt "shared\Microsoft.NETCore.App\$ver\") -Recurse -Force
Copy-Item (Join-Path $desktop '*') (Join-Path $rt "shared\Microsoft.WindowsDesktop.App\$ver\") -Recurse -Force
Copy-Item (Join-Path $RuntimeRoot "dotnet.exe") (Join-Path $rt "dotnet.exe") -Force

@'
# RDP 资源池客户端（Windows x64 · 解压即用）

本包已内置 .NET 6 运行时，本机不必单独安装 .NET。
需要本机有「远程桌面连接」(mstsc)。

## 启动

1. 解压全部文件到任意文件夹（保留 dotnet 子目录）
2. 双击 RdpPoolSetup.exe：填写服务器地址、用户名、密码并保存
3. 双击 RdpPoolClient.exe 登录并分配资源

不要手写明文密码到 appsettings.json；Setup 会用本机当前用户 DPAPI 加密后写入。

## 配置（appsettings.json）

- ApiBaseUrl: 控制面地址（明文），默认 http://127.0.0.1:8081（不是管理后台 8080）
- LoginProtected / PasswordProtected: Base64(DPAPI CurrentUser)，由 Setup 写入
- 旧版明文 Login/Password：客户端首次启动会自动迁移为 Protected 字段并清除明文
- PreferredGroupId / SkipMstsc / AutoRelease / ConnectHoldSec：仍可用 Setup 或编辑 JSON 非敏感项

控制面需已启动。带自动凭据注入，一般无需手输 RDP 密码。仅 Windows x64。
密文与 Windows 用户绑定；换用户或换机需重新运行 Setup。
'@ | Set-Content (Join-Path $Stage "README.txt") -Encoding UTF8

# 不再生成 启动.cmd / 启动.ps1

if (Test-Path $OutZip) { Remove-Item -Force $OutZip }
Compress-Archive -Path (Join-Path $Stage '*') -DestinationPath $OutZip -CompressionLevel Optimal -Force
Write-Host "OK: $OutZip ($([math]::Round((Get-Item $OutZip).Length/1MB,2)) MB)"
