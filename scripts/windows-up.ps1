#Requires -Version 5.1
# One-shot: copy env, compose up, seed. Run from repo root in PowerShell.
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

if (-not (Test-Path ".env")) {
  Copy-Item ".env.example" ".env"
  Write-Host "Created .env from .env.example — edit DEMO_RDP_* before RDP smoke." -ForegroundColor Yellow
}

Set-Location (Join-Path $Root "deploy")
docker compose --env-file (Join-Path $Root ".env") up -d --build
docker compose --env-file (Join-Path $Root ".env") --profile seed run --rm seed
Write-Host "Health:" -NoNewline
Invoke-RestMethod "http://127.0.0.1:8081/healthz" | ConvertTo-Json -Compress
Write-Host ""
Write-Host "Next: cd client\RdpPoolClient && dotnet run"
