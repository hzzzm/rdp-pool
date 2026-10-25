# Build Windows x64「解压即用」zip（内置 .NET 6 运行时，本机可不装 SDK/Runtime）.
# Prefer local SDK at D:\tmp\dotnet when PATH has no dotnet.
param(
  [string]$DotnetPath = "",
  [string]$RuntimeRoot = "",
  [string]$OutZip = "D:\Code\rpd-pool\dist\RdpPoolClient-win-x64.zip"
)

$ErrorActionPreference = "Stop"
$Repo = Resolve-Path "$PSScriptRoot\.."
$Proj = Join-Path $Repo "client\RdpPoolClient\RdpPoolClient.csproj"
$Pub = Join-Path $Repo "client\publish\win-x64-fd"
$Stage = Join-Path (Split-Path $OutZip) "RdpPoolClient-win-x64"

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

Write-Host "Using $DotnetPath (bundle runtime $ver from $RuntimeRoot)"
if (Test-Path $Pub) { Remove-Item -Recurse -Force $Pub }
& $DotnetPath publish $Proj -c Release -r win-x64 --self-contained false -o $Pub
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

New-Item -ItemType Directory -Force -Path (Split-Path $OutZip) | Out-Null
if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null
Copy-Item (Join-Path $Pub '*') $Stage -Recurse -Force
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
需要本机有「远程桌面连接」(mstsc)。请用「启动.cmd」启动。

## 启动

1. 解压全部文件到任意文件夹（保留 dotnet 子目录）
2. 编辑同目录 appsettings.json
3. 双击「启动.cmd」

## 配置（appsettings.json）

- ApiBaseUrl: 控制面地址，默认 http://127.0.0.1:8081（不是管理后台 8080）
- Login / Password: 终端用户（池用户），默认 demo / demo123
- PreferredGroupId: 多组时优先组 ID；空则交互选择
- SkipMstsc: true 时只拿票据不拉远程桌面（冒烟）
- AutoRelease: 退出时自动软释放，默认 true
- ConnectHoldSec: >0 则 Connected 后自动退出秒数

控制面需已启动。带自动凭据注入，一般无需手输 RDP 密码。仅 Windows x64。
'@ | Set-Content (Join-Path $Stage "README.txt") -Encoding UTF8

@"
@echo off
cd /d "%~dp0"
set "DOTNET_ROOT=%~dp0dotnet"
set "DOTNET_MULTILEVEL_LOOKUP=0"
if not exist "appsettings.json" (
  echo Missing appsettings.json
  pause
  exit /b 1
)
if not exist "dotnet\host\fxr\$ver\hostfxr.dll" (
  echo Bundled runtime missing. Re-extract the full zip.
  pause
  exit /b 1
)
RdpPoolClient.exe %*
set ERR=%ERRORLEVEL%
if not %ERR%==0 pause
exit /b %ERR%
"@ | Set-Content (Join-Path $Stage "启动.cmd") -Encoding ASCII

@'
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $PSScriptRoot "dotnet"
$env:DOTNET_MULTILEVEL_LOOKUP = "0"
& .\RdpPoolClient.exe @args
exit $LASTEXITCODE
'@ | Set-Content (Join-Path $Stage "启动.ps1") -Encoding UTF8

if (Test-Path $OutZip) { Remove-Item -Force $OutZip }
Compress-Archive -Path (Join-Path $Stage '*') -DestinationPath $OutZip -CompressionLevel Optimal -Force
Write-Host "OK: $OutZip ($([math]::Round((Get-Item $OutZip).Length/1MB,2)) MB)"
