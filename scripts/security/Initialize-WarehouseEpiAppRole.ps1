[CmdletBinding()]
param(
    [string]$HostName = 'localhost',
    [int]$Port = 5432,
    [string]$Database = 'warehouseEPI',
    [string]$AdministratorUser = 'postgres'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$psql = Get-Command psql -ErrorAction Stop
$scriptPath = Join-Path $PSScriptRoot 'provision-postgresql-role.sql'
& $psql.Source --host $HostName --port $Port --username $AdministratorUser --dbname $Database --file $scriptPath
if ($LASTEXITCODE -ne 0) {
    throw "La provisión del rol warehouse_epi_app falló con código $LASTEXITCODE."
}
