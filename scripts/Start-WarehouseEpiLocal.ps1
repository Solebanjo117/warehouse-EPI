[CmdletBinding()]
param(
    [string]$PostgreSqlUser = 'postgres',
    [switch]$Watch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryPath = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryPath 'src\WarehouseEPI.Web\WarehouseEPI.Web.csproj'
$developmentDatabase = 'warehouse_epi_dev_copy_20260923_141051'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($dotnetCommand) { $dotnetCommand.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    throw 'No se encontro .NET SDK. Revisa la instalacion de dotnet.'
}

$environmentNames = @(
    'ConnectionStrings__Warehouse',
    'ASPNETCORE_ENVIRONMENT',
    'DOTNET_ENVIRONMENT',
    'ASPNETCORE_URLS',
    'AllowedHosts',
    'Development__UseEphemeralDataProtection',
    'ServiceConfigPath'
)
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$connectionBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection = $null
$secureConnection = $null
try {
    $connection = $previousEnvironment['ConnectionStrings__Warehouse']
    if ([string]::IsNullOrWhiteSpace($connection)) {
        Write-Host "Conexion local: localhost:5432, base $developmentDatabase, usuario $PostgreSqlUser."
        Write-Host 'La entrada de la contrasena se oculta.'
        $secureConnection = Read-Host "Contrasena de PostgreSQL para $PostgreSqlUser" -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureConnection)
        try {
            $connectionBuilder['Host'] = 'localhost'
            $connectionBuilder['Port'] = '5432'
            $connectionBuilder['Database'] = $developmentDatabase
            $connectionBuilder['Username'] = $PostgreSqlUser
            $connectionBuilder['Password'] = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
            $connection = $connectionBuilder.get_ConnectionString()
        }
        finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
    }

    try {
        $connectionBuilder.set_ConnectionString($connection)
    }
    catch {
        throw 'La cadena de conexion no tiene un formato valido.'
    }
    if (-not $connectionBuilder.ContainsKey('Database') -or
        [string]$connectionBuilder['Database'] -notin @('warehouse_epi_dev', $developmentDatabase)) {
        throw "La conexion debe apuntar a la copia de desarrollo $developmentDatabase."
    }
    if (-not $connectionBuilder.ContainsKey('Host') -or
        [string]$connectionBuilder['Host'] -notin @('localhost', '127.0.0.1', '::1')) {
        throw 'La conexion debe apuntar al PostgreSQL de esta computadora.'
    }
    if ($connectionBuilder.ContainsKey('Port') -and [string]$connectionBuilder['Port'] -ne '5432') {
        throw 'El PostgreSQL local recuperado usa el puerto 5432.'
    }

    $connectionBuilder['Database'] = $developmentDatabase
    $connection = $connectionBuilder.get_ConnectionString()
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Warehouse', $connection, 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Development', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Development', 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', 'http://127.0.0.1:5142', 'Process')
    [Environment]::SetEnvironmentVariable('AllowedHosts', 'localhost;127.0.0.1', 'Process')
    [Environment]::SetEnvironmentVariable('Development__UseEphemeralDataProtection', 'true', 'Process')
    [Environment]::SetEnvironmentVariable('ServiceConfigPath', $null, 'Process')

    Write-Host "Iniciando Warehouse EPI en http://127.0.0.1:5142 con $developmentDatabase."
    if ($Watch) {
        Write-Host 'Al guardar cambios, la aplicacion se recompila y reinicia automaticamente.'
    }
    Write-Host 'Deten la aplicacion con Ctrl+C. Este script no aplica migraciones.'
    Push-Location $repositoryPath
    try {
        if ($Watch) {
            & $dotnetPath watch --project $projectPath --no-launch-profile --no-hot-reload
        }
        else {
            & $dotnetPath run --project $projectPath --no-launch-profile
        }
        if ($LASTEXITCODE -ne 0) {
            throw 'Warehouse EPI no pudo iniciarse. Revisa el error anterior.'
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    $connectionBuilder.Clear()
    $connection = $null
    if ($secureConnection) { $secureConnection.Dispose() }
}
