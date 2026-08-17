[CmdletBinding()]
param(
    [string]$BackupDirectory = 'C:\ProgramData\WarehouseEPI\Backups',
    [string]$PgPassFile = 'C:\ProgramData\WarehouseEPI\BackupCredentials\postgresql-backup.pgpass',
    [string]$TaskFolder = '\WarehouseEPI\',
    [string]$ServiceIdentity = 'SYSTEM',
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') { throw 'Este script requiere Windows.' }
if ($ServiceIdentity -ne 'SYSTEM') { throw 'Esta primera versión programa las tareas exclusivamente como SYSTEM.' }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecute PowerShell como administrador para registrar tareas programadas.'
}

$scheduler = New-Object -ComObject 'Schedule.Service'
$scheduler.Connect()
try {
    $null = $scheduler.GetFolder($TaskFolder)
}
catch {
    $taskFolderName = $TaskFolder.Trim('\')
    if ([string]::IsNullOrWhiteSpace($taskFolderName) -or $taskFolderName.Contains('\')) {
        throw 'TaskFolder debe ser una carpeta simple debajo de la raíz del Programador de tareas.'
    }
    $null = $scheduler.GetFolder('\').CreateFolder($taskFolderName)
}

$backupScript = Join-Path $PSScriptRoot 'Invoke-WarehouseEpiBackup.ps1'
$validationScript = Join-Path $PSScriptRoot 'Invoke-WarehouseEpiRecoveryValidation.ps1'
$taskNames = @('WarehouseEPI-DailyBackup', 'WarehouseEPI-WeeklyRestoreValidation')
foreach ($taskName in $taskNames) {
    $existing = Get-ScheduledTask -TaskPath $TaskFolder -TaskName $taskName -ErrorAction SilentlyContinue
    if ($null -ne $existing -and -not $Force) { throw "La tarea '$TaskFolder$taskName' ya existe. Use -Force para actualizarla deliberadamente." }
}

$principalDefinition = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)
$dailyAction = New-ScheduledTaskAction -Execute 'PowerShell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$backupScript`" -BackupDirectory `"$BackupDirectory`" -PgPassFile `"$PgPassFile`""
$weeklyAction = New-ScheduledTaskAction -Execute 'PowerShell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$validationScript`" -BackupDirectory `"$BackupDirectory`" -PgPassFile `"$PgPassFile`""

Register-ScheduledTask -TaskPath $TaskFolder -TaskName 'WarehouseEPI-DailyBackup' -Action $dailyAction `
    -Trigger (New-ScheduledTaskTrigger -Daily -At 2:00AM) -Principal $principalDefinition -Settings $settings -Force:$Force | Out-Null
Register-ScheduledTask -TaskPath $TaskFolder -TaskName 'WarehouseEPI-WeeklyRestoreValidation' -Action $weeklyAction `
    -Trigger (New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At 3:00AM) -Principal $principalDefinition -Settings $settings -Force:$Force | Out-Null

Write-Host 'Las tareas diaria y semanal de respaldo de Warehouse EPI quedaron registradas.'
