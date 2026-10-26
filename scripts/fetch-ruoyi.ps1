# Fetch RuoYi sources into admin/vendor (gitignored). Run before first compose build.
$ErrorActionPreference = "Stop"
$Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not (Test-Path "$PSScriptRoot\..\admin")) { $Root = "D:\Code\rpd-pool" }
$Repo = Resolve-Path "$PSScriptRoot\.."
$Vendor = Join-Path $Repo "admin\vendor"
$Cache = Join-Path (Split-Path $Repo -Parent) "_vendor"

New-Item -ItemType Directory -Force -Path $Vendor, $Cache | Out-Null

function Ensure-Clone($url, $dest) {
  if (Test-Path (Join-Path $dest ".git")) {
    Write-Host "OK $dest"
    return
  }
  Write-Host "Cloning $url -> $dest"
  git clone --depth 1 $url $dest
}

Ensure-Clone "https://gitee.com/y_project/RuoYi-Vue.git" (Join-Path $Cache "RuoYi-Vue")
Ensure-Clone "https://gitcode.com/yangzongzhuan/RuoYi-Vue3.git" (Join-Path $Cache "RuoYi-Vue3")

robocopy (Join-Path $Cache "RuoYi-Vue") (Join-Path $Vendor "RuoYi-Vue") /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
robocopy (Join-Path $Cache "RuoYi-Vue3") (Join-Path $Vendor "RuoYi-Vue3") /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
Write-Host "vendor ready under admin/vendor"
