[CmdletBinding()]
param(
    [string]$HostName = 'localhost',
    [int]$Port = 5432,
    [string]$Database = 'warehouseEPI',
    [string]$Role = 'warehouse_epi_app'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$psql = Get-Command psql -ErrorAction Stop
$password = Read-Host -AsSecureString "Contraseña de $Role"
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
try {
    $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    $verification = Join-Path $PSScriptRoot 'verify-postgresql-role.sql'
    & $psql.Source --no-psqlrc --host $HostName --port $Port --username $Role --dbname $Database --file $verification
    if ($LASTEXITCODE -ne 0) { throw 'Falló la verificación positiva del rol de aplicación.' }

    foreach ($statement in @(
        'BEGIN; CREATE TABLE public.warehouse_epi_security_probe (id integer); ROLLBACK;',
        'BEGIN; ALTER TABLE public.products ADD COLUMN warehouse_epi_security_probe integer; ROLLBACK;',
        'BEGIN; TRUNCATE TABLE public.inventory_balances; ROLLBACK;',
        'BEGIN; DROP TABLE public.inventory_balances; ROLLBACK;')) {
        & $psql.Source --no-psqlrc --host $HostName --port $Port --username $Role --dbname $Database --command $statement 2>$null
        if ($LASTEXITCODE -eq 0) {
            throw "El rol '$Role' ejecutó una operación administrativa que debía estar denegada."
        }
    }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
}

Write-Host "El rol '$Role' tiene DML operativo y rechaza permisos administrativos."
