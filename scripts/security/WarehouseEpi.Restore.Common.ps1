#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Uploaded dumps are executable SQL. They are NEVER restored or queried as postgres.
# This helper drains output with a bound, enforces time/disk limits, and does not expose stderr.
if (-not ('WarehouseEpiRestoreProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class WarehouseEpiRestoreProcess {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out FileTags tags, uint size);
    [StructLayout(LayoutKind.Sequential)] private struct FileTags { public uint Attributes; public uint ReparseTag; }
    public static SafeFileHandle LockDirectory(string path) {
        var handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var tags, 8) || (tags.Attributes & 0x400) != 0) {
            handle.Dispose(); throw new IOException("Unsafe restore directory.");
        }
        return handle;
    }
    public static string ReadRequest(string path) {
        using var handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var tags, 8) || (tags.Attributes & 0x410) != 0)
            throw new IOException("Unsafe restore request.");
        using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length > 4096) throw new IOException("Restore request limit.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public static async Task<string> Drain(StreamReader reader, bool retain) {
        var result = new StringBuilder(); var buffer = new char[8192]; int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0) {
            if (retain && result.Length + count > 1048576) throw new IOException("Process output limit.");
            if (retain) result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
'@
}

function Assert-WarehouseEpiRestoreName([string]$Name) {
    if ($Name -cnotmatch '^[A-Za-z][A-Za-z0-9_]{0,62}$') { throw 'Identificador PostgreSQL inválido.' }
}

function Invoke-WarehouseEpiRestoreTool([object]$Context, [string]$Tool, [string[]]$Arguments,
    [string]$PassFile, [int]$TimeoutSeconds = 120, [string]$MonitorDatabase) {
    $start = [Diagnostics.ProcessStartInfo]::new($Tool)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment.Remove('PGPASSWORD') | Out-Null
    $start.Environment['PGPASSFILE'] = $PassFile
    $start.Environment['PGOPTIONS'] = '-c statement_timeout=600000 -c lock_timeout=15000'
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        $null = $process.Start()
        $output = [WarehouseEpiRestoreProcess]::Drain($process.StandardOutput, $true)
        $errors = [WarehouseEpiRestoreProcess]::Drain($process.StandardError, $false)
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
        $nextProbe = [DateTimeOffset]::UtcNow
        while (-not $process.WaitForExit(500)) {
            if ([DateTimeOffset]::UtcNow -gt $deadline -or $output.IsFaulted) { throw 'La herramienta excedió su límite.' }
            if ($MonitorDatabase -and [DateTimeOffset]::UtcNow -ge $nextProbe) {
                $nextProbe = [DateTimeOffset]::UtcNow.AddSeconds(5)
                $size = Invoke-WarehouseEpiRestoreSql $Context postgres "SELECT pg_database_size('$MonitorDatabase');"
                $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($Context.WorkDirectory))
                if ([long]$size -gt 10GB -or $drive.AvailableFreeSpace -lt 2GB) { throw 'El respaldo excede el límite de almacenamiento.' }
            }
        }
        if ($process.ExitCode -ne 0) { throw 'La herramienta PostgreSQL rechazó la operación.' }
        $null = $errors.GetAwaiter().GetResult()
        return $output.GetAwaiter().GetResult().Trim()
    }
    finally {
        if ($process.Id -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

function Invoke-WarehouseEpiRestoreSql([object]$Context, [string]$Database, [string]$Sql,
    [string]$User, [string]$PassFile) {
    Assert-WarehouseEpiRestoreName $Database
    if (-not $User) { $User = $Context.AdminUser }
    if (-not $PassFile) { $PassFile = $Context.AdminPassFile }
    return Invoke-WarehouseEpiRestoreTool $Context $Context.Psql @('--no-psqlrc', "--host=$($Context.Host)",
        "--port=$($Context.Port)", "--username=$User", "--dbname=$Database", '--tuples-only', '--no-align',
        '--set=ON_ERROR_STOP=1', "--command=$Sql") $PassFile
}

function New-WarehouseEpiRestoreVerifier([string]$Password) {
    $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $salted = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2([Text.Encoding]::UTF8.GetBytes($Password),
        $salt, 4096, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    $hmac = [Security.Cryptography.HMACSHA256]::new($salted)
    try {
        $client = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes('Client Key'))
        $stored = [Security.Cryptography.SHA256]::HashData($client)
        $server = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes('Server Key'))
        return 'SCRAM-SHA-256$4096:' + [Convert]::ToBase64String($salt) + '$' +
            [Convert]::ToBase64String($stored) + ':' + [Convert]::ToBase64String($server)
    }
    finally { $hmac.Dispose(); [Array]::Clear($salted); [Array]::Clear($client) }
}

function New-WarehouseEpiRestoreCandidate([object]$Context, [string]$DumpPath) {
    foreach ($name in @($Context.Candidate, $Context.Owner)) { Assert-WarehouseEpiRestoreName $name }
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $verifier = New-WarehouseEpiRestoreVerifier $password
    $null = Invoke-WarehouseEpiRestoreSql $Context postgres "CREATE ROLE `"$($Context.Owner)`" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 2 PASSWORD '$verifier';"
    $null = Invoke-WarehouseEpiRestoreSql $Context postgres "CREATE DATABASE `"$($Context.Candidate)`" OWNER `"$($Context.Owner)`" TEMPLATE template0;"
    $null = Invoke-WarehouseEpiRestoreSql $Context postgres "REVOKE CONNECT ON DATABASE `"$($Context.Candidate)`" FROM PUBLIC;"
    $rolePass = Join-Path $Context.WorkDirectory 'candidate.pgpass'
    [IO.File]::WriteAllText($rolePass, "$($Context.Host):$($Context.Port):$($Context.Candidate):$($Context.Owner):$password`n")
    try {
        $null = Invoke-WarehouseEpiRestoreTool $Context $Context.PgRestore @('--no-owner', '--no-privileges',
            '--exit-on-error', '--single-transaction', "--host=$($Context.Host)", "--port=$($Context.Port)",
            "--username=$($Context.Owner)", "--dbname=$($Context.Candidate)", $DumpPath) $rolePass 1800 $Context.Candidate
    }
    catch { Remove-WarehouseEpiRestoreCandidate $Context; throw }
    return $rolePass
}

function Test-WarehouseEpiRestoreCandidate([object]$Context, [string]$RolePass,
    [string[]]$MigrationIds, [string]$AdminPinLookup, [string]$AssetDirectory) {
    if ($AdminPinLookup -cnotmatch '^[a-f0-9]{64}$') { throw 'La confirmación de NIP no es válida.' }
    $actual = Invoke-WarehouseEpiRestoreSql $Context $Context.Candidate 'SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId";' $Context.Owner $RolePass
    $actualIds = @($actual.Split("`n", [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
    if ($MigrationIds.Count -eq 0 -or $actualIds.Count -ne $MigrationIds.Count -or
        @($actualIds | Where-Object { $_ -cnotin $MigrationIds }).Count -gt 0) { throw 'El respaldo requiere otra versión de la aplicación.' }
    $admin = Invoke-WarehouseEpiRestoreSql $Context $Context.Candidate "SELECT count(*) FROM public.users u JOIN public.roles r ON r.id=u.role_id WHERE u.is_active AND r.code='ADMIN' AND u.pin_lookup='$AdminPinLookup';" $Context.Owner $RolePass
    if ($admin -ne '1') { throw 'El NIP no corresponde a un ADMIN activo del respaldo.' }
    $references = Invoke-WarehouseEpiRestoreSql $Context $Context.Candidate 'SELECT stored_file_name || ''|'' || sha256 FROM public.warehouse_map_reference_images ORDER BY stored_file_name;' $Context.Owner $RolePass
    $logos = Invoke-WarehouseEpiRestoreSql $Context $Context.Candidate 'SELECT logo_file_name || ''|'' || logo_hash FROM public.business_settings WHERE logo_file_name IS NOT NULL;' $Context.Owner $RolePass
    foreach ($group in @(@{ Text = $references; Folder = 'references'; Pattern = '^[a-f0-9]{64}\.(png|jpg|webp)$' },
                         @{ Text = $logos; Folder = 'branding'; Pattern = '^[a-f0-9]{32}\.(png|jpg|webp)$' })) {
        $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($line in $group.Text.Split("`n", [StringSplitOptions]::RemoveEmptyEntries)) {
            $parts = $line.Trim().Split('|')
            if ($parts.Count -ne 2 -or $parts[0] -cnotmatch $group.Pattern -or $parts[1] -cnotmatch '^[a-f0-9]{64}$') { throw 'La base contiene archivos no válidos.' }
            $file = Join-Path (Join-Path $AssetDirectory $group.Folder) $parts[0]
            if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
                (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $parts[1]) { throw 'Faltan archivos correspondientes a la base.' }
            $null = $expected.Add($parts[0])
        }
        $files = @(Get-ChildItem -LiteralPath (Join-Path $AssetDirectory $group.Folder) -File)
        if ($files.Count -ne $expected.Count) { throw 'Los archivos no corresponden a la base restaurada.' }
    }
}

function Switch-WarehouseEpiRestoreDatabase([object]$Context) {
    foreach ($name in @($Context.Database, $Context.Before, $Context.Candidate, $Context.AppRole)) { Assert-WarehouseEpiRestoreName $name }
    $null = Invoke-WarehouseEpiRestoreSql $Context postgres "REVOKE CONNECT ON DATABASE `"$($Context.Database)`" FROM PUBLIC, `"$($Context.AppRole)`";"
    $null = Invoke-WarehouseEpiRestoreSql $Context postgres "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname IN ('$($Context.Database)','$($Context.Candidate)') AND pid<>pg_backend_pid();"
    $null = Invoke-WarehouseEpiRestoreSql $Context postgres "ALTER DATABASE `"$($Context.Database)`" RENAME TO `"$($Context.Before)`";"
    $null = Invoke-WarehouseEpiRestoreSql $Context postgres "ALTER DATABASE `"$($Context.Candidate)`" RENAME TO `"$($Context.Database)`";"
    # Keep the temporary owner unprivileged; do not assign uploaded objects to postgres.
    $null = Invoke-WarehouseEpiRestoreSql $Context postgres "ALTER ROLE `"$($Context.Owner)`" NOLOGIN PASSWORD NULL; GRANT CONNECT ON DATABASE `"$($Context.Database)`" TO `"$($Context.AppRole)`";"
    $null = Invoke-WarehouseEpiRestoreSql $Context $Context.Database "REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA public TO `"$($Context.AppRole)`"; GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO `"$($Context.AppRole)`"; GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO `"$($Context.AppRole)`";"
}

function Undo-WarehouseEpiRestoreDatabase([object]$Context) {
    foreach ($name in @($Context.Database, $Context.Before, $Context.Failed, $Context.AppRole)) { Assert-WarehouseEpiRestoreName $name }
    $before = Invoke-WarehouseEpiRestoreSql $Context postgres "SELECT 1 FROM pg_database WHERE datname='$($Context.Before)';"
    if ($before -eq '1') {
        $null = Invoke-WarehouseEpiRestoreSql $Context postgres "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname IN ('$($Context.Database)','$($Context.Before)') AND pid<>pg_backend_pid();"
        $current = Invoke-WarehouseEpiRestoreSql $Context postgres "SELECT 1 FROM pg_database WHERE datname='$($Context.Database)';"
        if ($current -eq '1') {
            $null = Invoke-WarehouseEpiRestoreSql $Context postgres "REVOKE CONNECT ON DATABASE `"$($Context.Database)`" FROM PUBLIC, `"$($Context.AppRole)`";"
            $null = Invoke-WarehouseEpiRestoreSql $Context postgres "ALTER DATABASE `"$($Context.Database)`" RENAME TO `"$($Context.Failed)`";"
        }
        $null = Invoke-WarehouseEpiRestoreSql $Context postgres "ALTER DATABASE `"$($Context.Before)`" RENAME TO `"$($Context.Database)`";"
    }
    $null = Invoke-WarehouseEpiRestoreSql $Context postgres "GRANT CONNECT ON DATABASE `"$($Context.Database)`" TO `"$($Context.AppRole)`";"
}

function Remove-WarehouseEpiRestoreCandidate([object]$Context) {
    # Only the unique candidate may be dropped; the previous or active database is always retained.
    if ($Context.Candidate -cnotmatch '^warehouseEPI_restore_[a-f0-9]{32}$' -or
        $Context.Owner -cnotmatch '^epi_restore_[a-f0-9]{32}$') { throw 'No se puede limpiar una base fuera del candidato.' }
    $null = Invoke-WarehouseEpiRestoreSql $Context postgres "DROP DATABASE IF EXISTS `"$($Context.Candidate)`" WITH (FORCE);"
    $null = Invoke-WarehouseEpiRestoreSql $Context postgres "ALTER ROLE `"$($Context.Owner)`" NOLOGIN PASSWORD NULL;"
}

function Write-WarehouseEpiRestoreJson([string]$Path, [object]$Value) {
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).partial"
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 12))
        Sync-WarehouseEpiRestoreFile $temporary
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Sync-WarehouseEpiRestoreFile([string]$Path) {
    $file = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $file.Flush($true) } finally { $file.Dispose() }
}

function Write-WarehouseEpiRestoreConfiguration([string]$Path, [string]$Content) {
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).partial"
    try {
        $sections = [Security.AccessControl.AccessControlSections]::Access
        $original = [IO.FileSystemAclExtensions]::GetAccessControl([IO.FileInfo]::new($Path), $sections)
        $acl = [Security.AccessControl.FileSecurity]::new()
        $acl.SetSecurityDescriptorSddlForm($original.GetSecurityDescriptorSddlForm($sections), $sections)
        [IO.File]::WriteAllText($temporary, $Content)
        Sync-WarehouseEpiRestoreFile $temporary
        [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($temporary), $acl)
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Set-WarehouseEpiRestorePrivateAcl([string]$Path, [switch]$ServiceModify, [switch]$ServiceRead) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    if ($ServiceModify -or $ServiceRead) {
        $rights = if ($ServiceModify) { 'Modify' } else { 'ReadAndExecute' }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('NT SERVICE\WarehouseEPI', $rights,
            'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path), $acl)
}

function Expand-WarehouseEpiRestoreZip([string]$Path, [string]$Destination, [string]$AllowedPattern, [long]$Limit = 4GB) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$total = 0
        if ($archive.Entries.Count -gt 5000) { throw 'El respaldo contiene demasiados archivos.' }
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -cnotmatch $AllowedPattern -or -not $names.Add($entry.FullName) -or $entry.Length -lt 0) { throw 'El respaldo contiene rutas no permitidas.' }
            $total += $entry.Length
            if ($total -gt $Limit) { throw 'El respaldo excede el límite de tamaño.' }
            $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($Destination))
            if ($drive.AvailableFreeSpace -lt $entry.Length + 2GB) { throw 'No hay espacio para extraer el respaldo.' }
            $file = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName))
            if (-not $file.StartsWith(([IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Ruta fuera del directorio temporal.' }
            $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file)
            $input = $entry.Open(); $output = [IO.File]::Open($file, [IO.FileMode]::CreateNew)
            try {
                $buffer = [byte[]]::new(81920); [long]$copied = 0
                while (($count = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $copied += $count
                    if ($copied -gt $entry.Length) { throw 'El tamaño real del ZIP excede su declaración.' }
                    $output.Write($buffer, 0, $count)
                }
                if ($copied -ne $entry.Length) { throw 'Archivo ZIP incompleto.' }
            }
            finally { $input.Dispose(); $output.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}
