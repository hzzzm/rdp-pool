# 将随包分发的实验室自签代码签名证书安装到本机「受信任的根」与「受信任的发布者」。
# 必须「以管理员身份运行」PowerShell。仅用于内网/实验机，不能替代商业 Authenticode。
#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$cer = Join-Path $here "RdpPoolLab-CodeSigning.cer"
if (-not (Test-Path $cer)) {
  throw "找不到证书文件: $cer"
}

Write-Host "即将安装: $cer"
Write-Host "若弹出「要安装此证书吗？」请点「是」。"
Write-Host ""

# LocalMachine Root + TrustedPublisher（管理员）
& certutil.exe -f -addstore Root $cer
if ($LASTEXITCODE -ne 0) { throw "安装到 Root 失败" }
& certutil.exe -f -addstore TrustedPublisher $cer
if ($LASTEXITCODE -ne 0) { throw "安装到 TrustedPublisher 失败" }

Write-Host ""
Write-Host "完成。请："
Write-Host "  1) 关闭所有资源管理器窗口后重新打开解压目录"
Write-Host "  2) 再双击 RdpPoolSetup.exe / RdpPoolClient.exe"
Write-Host "  3) 可选验证: Get-AuthenticodeSignature .\RdpPoolSetup.exe  （Status 应为 Valid）"
Write-Host ""
Write-Host "若仍被「智能应用控制」拦截，见 README.txt（临时关闭 SAC）。"
Pause
