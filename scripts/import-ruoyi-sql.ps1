# Import RuoYi schema + RDP menus into running rdp-mysql (for existing volumes).
$ErrorActionPreference = "Stop"
$Root = Resolve-Path "$PSScriptRoot\.."
$RootPw = if ($env:MYSQL_ROOT_PASSWORD) { $env:MYSQL_ROOT_PASSWORD } else { "root_pass_change_me" }
$Schema = Join-Path $Root "deploy\mysql\init\02_ruoyi_schema.sql"
$Menu = Join-Path $Root "deploy\mysql\init\03_rdp_menu.sql"

Write-Host "Importing RuoYi schema..."
Get-Content $Schema -Raw | docker exec -i rdp-mysql mysql -uroot "-p$RootPw"
Write-Host "Importing RDP menus..."
Get-Content $Menu -Raw | docker exec -i rdp-mysql mysql -uroot "-p$RootPw"
Write-Host "Done."
