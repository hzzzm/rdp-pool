# Ensure a local lab Authenticode cert exists (CurrentUser\My) and export .cer/.pfx for packaging.
param(
  [string]$CertDir = "",
  [string]$Subject = "CN=RDP Pool Lab Code Signing",
  [string]$FriendlyName = "RDP Pool Lab CodeSigning"
)

$ErrorActionPreference = "Stop"
$Repo = Resolve-Path "$PSScriptRoot\.."
if (-not $CertDir) { $CertDir = Join-Path $Repo "client\signing" }
New-Item -ItemType Directory -Force -Path $CertDir | Out-Null

$cerPath = Join-Path $CertDir "RdpPoolLab-CodeSigning.cer"
$pfxPath = Join-Path $CertDir "RdpPoolLab-CodeSigning.pfx"
$pwdFile = Join-Path $CertDir "pfx-password.txt"

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
  Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date) } |
  Sort-Object NotAfter -Descending |
  Select-Object -First 1

if (-not $cert) {
  Write-Host "Creating self-signed code signing cert: $Subject"
  $cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -FriendlyName $FriendlyName `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyExportPolicy Exportable `
    -KeySpec Signature `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(5)
}

$plainPwd = "rdp-pool-lab-sign"
if (Test-Path $pwdFile) {
  $plainPwd = (Get-Content $pwdFile -Raw).Trim()
} else {
  Set-Content -Path $pwdFile -Value $plainPwd -Encoding ASCII
}

$secure = ConvertTo-SecureString -String $plainPwd -Force -AsPlainText
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure -Force | Out-Null

Write-Host "Thumbprint: $($cert.Thumbprint)"
Write-Host "CER: $cerPath"
Write-Host "PFX: $pfxPath"
return $cert.Thumbprint
