#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\security\WarehouseEpi.Restore.Common.ps1')

function Resolve-DevelopmentRestorePath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ($full -ine $boundary -and -not $full.StartsWith($boundary + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'La ruta debe permanecer dentro de artifacts\local-restore.'
    }
    for ($parent = [IO.DirectoryInfo]::new($full); $null -ne $parent; $parent = $parent.Parent) {
        if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'No se permiten enlaces en desarrollo.' }
    }
    if (Test-Path -LiteralPath $full) {
        if ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'No se permiten enlaces en desarrollo.' }
    }
    return $full
}

function Set-DevelopmentRestorePrivateAcl([string]$Path) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path), $acl)
}

function Invoke-DevelopmentClusterControl([string]$Executable, [string[]]$Arguments) {
    # pg_ctl's detached server can retain redirected pipes. Wait for pg_ctl itself, never pipe EOF.
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        $null = $process.Start()
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'PostgreSQL de desarrollo no respondió.' }
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}

function New-DevelopmentRestoreContext([string]$Root) {
    $rootPath = [IO.Path]::GetFullPath($Root)
    if ((Split-Path -Leaf $rootPath) -cne 'local-restore' -or (Split-Path -Leaf (Split-Path -Parent $rootPath)) -cne 'artifacts' -or
        $rootPath.StartsWith('C:\ProgramData\WarehouseEPI', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'La restauración de desarrollo requiere artifacts\local-restore fuera de producción.'
    }
    $null = Resolve-DevelopmentRestorePath $rootPath $rootPath
    $configPath = Resolve-DevelopmentRestorePath (Join-Path $rootPath 'config.json') $rootPath
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $connection = [Data.Common.DbConnectionStringBuilder]::new()
    $connection.set_ConnectionString($config.ConnectionStrings.Warehouse)
    $port = [int]$config.Development.PostgreSqlPort
    if ($port -lt 1025 -or $port -gt 65535 -or $port -eq 5432 -or [string]$connection['Host'] -cne '127.0.0.1' -or
        [int]$connection['Port'] -ne $port -or [string]$connection['Database'] -cne 'warehouse_epi_restore_dev' -or
        [string]$connection['Username'] -cne 'warehouse_epi_dev_app' -or -not $config.Development.UseEphemeralDataProtection) {
        throw 'El destino no es la base aislada de desarrollo.'
    }
    foreach ($pair in @(@($config.Backups.ManualDirectory, 'ManualBackups'), @($config.Branding.StorageDirectory, 'Branding'),
        @($config.WarehouseMap.ReferenceStorageDirectory, 'WarehouseMapReferences'), @($config.Observability.LogDirectory, 'Logs'))) {
        if ($pair[0] -ine (Join-Path $rootPath $pair[1])) { throw 'La configuración no pertenece al entorno aislado.' }
        $null = Resolve-DevelopmentRestorePath $pair[0] $rootPath
    }
    $bin = 'C:\Program Files\PostgreSQL\18\bin'
    return [pscustomobject]@{ Root = $rootPath; ConfigurationPath = $configPath; Configuration = $config
        Host = '127.0.0.1'; Port = $port; AdminUser = 'warehouse_epi_dev_owner'
        AdminPassFile = (Resolve-DevelopmentRestorePath (Join-Path $rootPath 'owner.pgpass') $rootPath)
        Database = 'warehouse_epi_restore_dev'; AppRole = 'warehouse_epi_dev_app'; WorkDirectory = $rootPath
        Psql = (Join-Path $bin 'psql.exe'); PgRestore = (Join-Path $bin 'pg_restore.exe'); PgDump = (Join-Path $bin 'pg_dump.exe') }
}

function Assert-DevelopmentRestoreCluster([object]$Context) {
    # Prove ownership before any CREATE/ALTER/DROP, even if another server occupies the configured port.
    $actual = Invoke-WarehouseEpiRestoreSql $Context postgres "SELECT current_setting('data_directory');"
    $expected = Resolve-DevelopmentRestorePath (Join-Path $Context.Root 'postgres') $Context.Root
    if ([IO.Path]::GetFullPath($actual) -ine $expected) { throw 'El puerto pertenece a otro PostgreSQL. No se modificará.' }
}

function Remove-DevelopmentRestoreTree([string]$Path, [string]$Root) {
    $full = Resolve-DevelopmentRestorePath $Path $Root
    if ($full -ieq [IO.Path]::GetFullPath($Root)) { throw 'No se puede eliminar la raíz de desarrollo.' }
    if (Test-Path -LiteralPath $full) {
        foreach ($item in Get-ChildItem -LiteralPath $full -Recurse -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'La carpeta contiene enlaces.' }
        }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

function Copy-DevelopmentRestoreDirectory([string]$Source, [string]$Target, [string]$Root) {
    $null = Resolve-DevelopmentRestorePath $Source $Root
    $null = Resolve-DevelopmentRestorePath $Target $Root
    foreach ($item in Get-ChildItem -LiteralPath $Source -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'La carpeta contiene enlaces.' }
    }
    $null = New-Item -ItemType Directory -Path $Target -Force
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) { Copy-Item -LiteralPath $item.FullName -Destination $Target -Recurse }
}
