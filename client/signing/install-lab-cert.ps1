# 将随包分发的实验室自签代码签名证书安装到本机：
#   LocalMachine\Root（受信任的根证书颁发机构）
#   LocalMachine\TrustedPublisher（受信任的发布者）
# 必须「以管理员身份运行」PowerShell。仅用于内网/实验机，不能替代商业 Authenticode。
#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$cer = Join-Path $here "RdpPoolLab-CodeSigning.cer"
if (-not (Test-Path $cer)) {
  throw "找不到证书文件: $cer`n请确认与本脚本在同一解压目录。"
}

$certObj = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)
Write-Host "即将安装实验室代码签名证书："
Write-Host "  文件: $cer"
Write-Host "  主体: $($certObj.Subject)"
Write-Host "  指纹: $($certObj.Thumbprint)"
Write-Host "目标存储："
Write-Host "  - 本地计算机\受信任的根证书颁发机构 (Root)"
Write-Host "  - 本地计算机\受信任的发布者 (TrustedPublisher)"
Write-Host ""

& certutil.exe -f -addstore Root $cer
if ($LASTEXITCODE -ne 0) { throw "安装到 LocalMachine\Root 失败 (exit=$LASTEXITCODE)" }
& certutil.exe -f -addstore TrustedPublisher $cer
if ($LASTEXITCODE -ne 0) { throw "安装到 LocalMachine\TrustedPublisher 失败 (exit=$LASTEXITCODE)" }

# 复核是否已在两个存储中
function Test-InStore([string]$storeName, [string]$thumb) {
  $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, "LocalMachine")
  try {
    $store.Open("ReadOnly")
    return [bool]($store.Certificates | Where-Object { $_.Thumbprint -eq $thumb })
  } finally {
    $store.Close()
  }
}
$tp = $certObj.Thumbprint
$inRoot = Test-InStore "Root" $tp
$inPub = Test-InStore "TrustedPublisher" $tp
if (-not $inRoot -or -not $inPub) {
  throw "安装后复核失败: Root=$inRoot TrustedPublisher=$inPub"
}

Write-Host ""
Write-Host "证书已安装并通过复核 (Root + TrustedPublisher)。"
Write-Host ""
Write-Host "请接着做："
Write-Host "  1) 关闭所有资源管理器窗口，再重新打开解压目录"
Write-Host "  2) 再双击 RdpPoolSetup.exe / RdpPoolClient.exe"
Write-Host "  3) 可选验证签名:"
Write-Host "       Get-AuthenticodeSignature .\RdpPoolSetup.exe"
Write-Host "     安装证书后 Status 常为 Valid；安装前多为 UnknownError/NotTrusted。"
Write-Host ""
Write-Host "【重要 · Smart App Control】"
Write-Host "  Win11「智能应用控制」即使已信任自签根证书，仍可能拦截「无云信誉」的自签程序。"
Write-Host "  若安装证书后仍被 SAC 拦截：设置 → 隐私和安全性 → Windows 安全中心"
Write-Host "  → 应用和浏览器控制 → 智能应用控制 → 关闭（多数版本关闭后无法再打开）。"
Write-Host "  详见同目录 README.txt。"
Write-Host ""
Pause
