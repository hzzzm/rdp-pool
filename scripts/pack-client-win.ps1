# Build Windows x64 portable zip (bundled .NET 6 runtime).
# Entries: RdpPoolSetup.exe + RdpPoolClient.exe (WinExe); lab Authenticode required by default.
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
if (-not $fxr) { throw "hostfxr missing under $RuntimeRoot - need full .NET 6 with Windows Desktop" }
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

# Replace SDK apphost with netfx WinExe launchers
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

# Lab Authenticode: ALWAYS sign Setup+Client launchers unless -SkipSign
$cerOut = Join-Path $Stage "RdpPoolLab-CodeSigning.cer"
$installLabOut = Join-Path $Stage "install-lab-cert.ps1"
if ($SkipSign) {
  Write-Warning "SkipSign set: shipping UNSIGNED. Win11 Smart App Control will likely block."
} else {
  if (-not $signtool -or -not (Test-Path $signtool)) {
    throw "signtool.exe not found under Windows Kits. Install SDK Signing Tools, or pass -SkipSign (not recommended)."
  }
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo "scripts\ensure-lab-codesign-cert.ps1") -CertDir $SignDir | Out-Host
  $cerSrc = Join-Path $SignDir "RdpPoolLab-CodeSigning.cer"
  $pfxSrc = Join-Path $SignDir "RdpPoolLab-CodeSigning.pfx"
  $pwdFile = Join-Path $SignDir "pfx-password.txt"
  if (-not (Test-Path $cerSrc) -or -not (Test-Path $pfxSrc) -or -not (Test-Path $pwdFile)) {
    throw "signing cert export missing under $SignDir (need .cer .pfx pfx-password.txt)"
  }
  $installLabSrc = Join-Path $SignDir "install-lab-cert.ps1"
  if (-not (Test-Path $installLabSrc)) {
    throw "missing client\signing\install-lab-cert.ps1"
  }
  Copy-Item $cerSrc $cerOut -Force
  Copy-Item $installLabSrc $installLabOut -Force

  $pwd = (Get-Content $pwdFile -Raw).Trim()
  if ([string]::IsNullOrWhiteSpace($pwd)) { throw "pfx-password.txt empty" }

  $toSign = @($clientExe, $setupExe)
  foreach ($exe in $toSign) {
    if (-not (Test-Path $exe)) { throw "missing launcher to sign: $exe" }
    Write-Host "Signing: $exe"
    & $signtool sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /f $pfxSrc /p $pwd $exe
    if ($LASTEXITCODE -ne 0) {
      Write-Warning "timestamp sign failed for $exe, retry without timestamp"
      & $signtool sign /fd SHA256 /f $pfxSrc /p $pwd $exe
      if ($LASTEXITCODE -ne 0) { throw "signtool failed: $exe" }
    }
  }

  # Require SignerCertificate. Status may be UnknownError/NotTrusted if lab root not installed locally.
  foreach ($exe in $toSign) {
    $sig = Get-AuthenticodeSignature $exe
    if ($null -eq $sig.SignerCertificate) {
      throw "post-sign verify failed (no SignerCertificate): $exe Status=$($sig.Status)"
    }
    if ($sig.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned) {
      throw "post-sign verify failed (NotSigned): $exe"
    }
    Write-Host ("Signed OK: {0} Subject={1} Thumbprint={2} Status={3}" -f `
      $exe, $sig.SignerCertificate.Subject, $sig.SignerCertificate.Thumbprint, $sig.Status)
  }

  if (-not (Test-Path $cerOut)) { throw "stage missing cer" }
  if (-not (Test-Path $installLabOut)) { throw "stage missing install-lab-cert.ps1" }
}

# Clear MOTW / Zone.Identifier if present
Get-ChildItem $Stage -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
  try { Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue } catch {}
  $zone = $_.FullName + ":Zone.Identifier"
  if (Test-Path $zone) { Remove-Item $zone -Force -ErrorAction SilentlyContinue }
}

$readmeSrc = Join-Path $Repo "client\packaging\README.client-zip.txt"
if (-not (Test-Path $readmeSrc)) { throw "missing client\packaging\README.client-zip.txt" }
Copy-Item $readmeSrc (Join-Path $Stage "README.txt") -Force

if (Test-Path $OutZip) { Remove-Item -Force $OutZip }
Compress-Archive -Path (Join-Path $Stage '*') -DestinationPath $OutZip -CompressionLevel Optimal -Force
Unblock-File -Path $OutZip -ErrorAction SilentlyContinue
$mb = [math]::Round((Get-Item $OutZip).Length / 1MB, 2)
Write-Host ("OK: {0} ({1} MB)" -f $OutZip, $mb)