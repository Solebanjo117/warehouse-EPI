#Requires -Version 7.4
#Requires -RunAsAdministrator
[CmdletBinding()]
param([switch]$Force)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WarehouseEpi.Restore.Common.ps1')
$taskName = 'WarehouseEPI-Restore'
$existing = Get-ScheduledTask -TaskPath '\' -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing -and -not $Force) { throw 'La tarea ya existe. Use -Force para actualizarla.' }
if ($existing -and $existing.State -eq 'Running') { throw 'Hay una restauración en curso. Termine esa tarea antes de actualizar sus scripts.' }
$queue = 'C:\ProgramData\WarehouseEPI\ManualBackups\.restore'
if ((Test-Path -LiteralPath (Join-Path $queue 'pending.json')) -or (Test-Path -LiteralPath (Join-Path $queue 'maintenance.json'))) {
    throw 'Hay una solicitud de restauración pendiente. Revise y termine esa tarea antes de actualizar.'
}
$destination = 'C:\ProgramData\WarehouseEPI\Maintenance'
for ($parent = [IO.DirectoryInfo]::new($destination); $null -ne $parent; $parent = $parent.Parent) {
    if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'No se permiten enlaces en Maintenance.' }
}
$null = New-Item -ItemType Directory -Path $destination -Force
Set-WarehouseEpiRestorePrivateAcl $destination -ServiceRead
function Install-PrivateRestoreScript([string]$Source, [string]$Target) {
    $temporary = Join-Path $destination ('.install-' + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::Copy($Source, $temporary)
        $acl = [Security.AccessControl.FileSecurity]::new(); $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'Allow'))
        }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('NT SERVICE\WarehouseEPI', 'ReadAndExecute', 'Allow'))
        [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($temporary), $acl)
        [IO.File]::Move($temporary, $Target, $true)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
foreach ($name in @('Invoke-WarehouseEpiRestore.ps1', 'WarehouseEpi.Restore.Common.ps1', 'Test-WarehouseEpiMigrationBackup.ps1')) {
    $source = Join-Path $PSScriptRoot $name; $target = Join-Path $destination $name
    if ($source -ine $target) { Install-PrivateRestoreScript $source $target }
}
$releaseCommon = Join-Path $destination 'WarehouseEpi.Release.Common.ps1'
if ($PSScriptRoot -ine $destination) { Install-PrivateRestoreScript (Join-Path $PSScriptRoot '..\release\WarehouseEpi.Release.Common.ps1') $releaseCommon }
if (-not (Test-Path -LiteralPath $releaseCommon)) { throw 'Falta el script de mantenimiento del servicio.' }
& (Join-Path $PSScriptRoot 'Initialize-WarehouseEpiManualBackups.ps1')
$null = New-Item -ItemType Directory -Path $queue -Force
Set-WarehouseEpiRestorePrivateAcl $queue -ServiceModify
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$action = New-ScheduledTaskAction -Execute $pwsh -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$(Join-Path $destination 'Invoke-WarehouseEpiRestore.ps1')`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(10) -RepetitionInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 2) -StartWhenAvailable
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force:$Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Host 'Restauración desde la aplicación habilitada. La tarea comprobará solicitudes cada minuto.'
