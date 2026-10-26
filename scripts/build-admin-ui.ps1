# Build RuoYi-Vue3 + RDP overlays into admin/ui-dist for Dockerfile.ui
$ErrorActionPreference = "Stop"
$Repo = Resolve-Path "$PSScriptRoot\.."
$VendorUI = Join-Path $Repo "admin\vendor\RuoYi-Vue3"
$Overlay = Join-Path $Repo "admin\overlay\ui"
$Work = Join-Path $Repo "admin\.build-ui"
$Out = Join-Path $Repo "admin\ui-dist"

if (-not (Test-Path (Join-Path $VendorUI "package.json"))) {
  throw "Missing $VendorUI — run scripts/fetch-ruoyi.ps1 first"
}

Write-Host "Preparing build tree at $Work"
if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
robocopy $VendorUI $Work /E /XD node_modules dist .git /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
robocopy $Overlay $Work /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null

# Disable gzip plugin in container/host builds (optional compression deps)
$envProd = Join-Path $Work ".env.production"
if (Test-Path $envProd) {
  (Get-Content $envProd -Raw) -replace "VITE_BUILD_COMPRESS = gzip", "VITE_BUILD_COMPRESS = " |
    Set-Content $envProd -NoNewline
}

Push-Location $Work
try {
  $env:npm_config_registry = "https://registry.npmmirror.com"
  Write-Host "npm install..."
  npm install --no-fund --no-audit
  if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
  Write-Host "npm run build:prod..."
  npm run build:prod
  if ($LASTEXITCODE -ne 0) { throw "npm build failed" }
} finally {
  Pop-Location
}

$dist = Join-Path $Work "dist"
if (-not (Test-Path $dist)) { throw "dist missing after build" }
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
robocopy $dist $Out /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
Write-Host "UI dist ready: $Out"
