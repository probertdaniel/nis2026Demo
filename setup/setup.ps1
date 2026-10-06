# Prepares the local demo: Azurite blob container and queue, plus the SQL seed.
# Requires: Azurite running, Azure CLI (az), and sqlcmd for the SQL step.
param([string]$SqlServer = "(localdb)\NIS2026", [switch]$SkipSql)
$ErrorActionPreference = "Stop"
$cs = "UseDevelopmentStorage=true"
Push-Location $PSScriptRoot
az storage container create --name customers --connection-string $cs | Out-Null
az storage blob upload --container-name customers --name 4471.json --file customers/4471.json --connection-string $cs --overwrite | Out-Null
az storage queue create --name faults --connection-string $cs | Out-Null
Write-Host "Azurite: container 'customers' (with 4471.json) and queue 'faults' ready."
if (-not $SkipSql) {
    sqlcmd -S "$SqlServer" -E -i seed.sql
    Write-Host "SQL: OrdersDemo.dbo.Orders ready, SO-9931 seeded."
}
Pop-Location
