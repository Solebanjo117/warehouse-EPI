#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$DestinationDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PackagePath).Path
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Use una carpeta nueva para la recuperación.' }
for ($parent = [IO.DirectoryInfo]::new($destination); $null -ne $parent; $parent = $parent.Parent) {
    if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'La carpeta no puede usar enlaces.' }
}
if (-not $IsWindows) { throw 'Esta herramienta requiere Windows para proteger los archivos recuperados mediante ACL.' }

if (-not ('WarehouseEpi.ManualBackupRecovery' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
namespace WarehouseEpi {
    public static class ManualBackupRecovery {
        public static void Open(string inputPath, string outputPath, string password) {
            byte[] keys = null;
            using (var input = File.OpenRead(inputPath)) {
                var header = new byte[44];
                input.ReadExactly(header);
                long count = input.Length - 44 - 32;
                if (Encoding.ASCII.GetString(header, 0, 8) != "WEPBK001" ||
                    BitConverter.ToInt32(header, 40) != 600000 || count < 16 || count % 16 != 0)
                    throw new InvalidDataException("Formato de respaldo no reconocido.");
                keys = Rfc2898DeriveBytes.Pbkdf2(password, header.AsSpan(8,16).ToArray(), 600000, HashAlgorithmName.SHA256, 64);
                try {
                    using (var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, keys.AsSpan(32,32))) {
                        mac.AppendData(header);
                        var buffer = new byte[81920];
                        long remaining = count;
                        while (remaining > 0) {
                            int read = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                            if (read == 0) throw new InvalidDataException("Respaldo incompleto.");
                            mac.AppendData(buffer,0,read);
                            remaining -= read;
                        }
                        var tag = new byte[32]; input.ReadExactly(tag);
                        if (!CryptographicOperations.FixedTimeEquals(tag, mac.GetHashAndReset()))
                            throw new CryptographicException("Contraseña incorrecta o respaldo alterado.");
                    }
                    // Never decrypt or create a plaintext output until authentication has passed.
                    input.Position = 44;
                    using (var aes = Aes.Create()) {
                        aes.Key = keys.AsSpan(0,32).ToArray(); aes.IV = header.AsSpan(24,16).ToArray();
                        using (var output = new FileStream(outputPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        using (var decrypt = new CryptoStream(output,aes.CreateDecryptor(),CryptoStreamMode.Write)) {
                            var buffer = new byte[81920]; long remaining = count;
                            while (remaining > 0) {
                                int read = input.Read(buffer,0,(int)Math.Min(remaining,buffer.Length));
                                if (read == 0) throw new InvalidDataException("Respaldo incompleto.");
                                decrypt.Write(buffer,0,read); remaining -= read;
                            }
                            decrypt.FlushFinalBlock();
                        }
                    }
                } finally { if (keys != null) CryptographicOperations.ZeroMemory(keys); }
            }
        }
    }
}
'@
}

$securePassword = Read-Host 'Contraseña del respaldo' -AsSecureString
$password = [Net.NetworkCredential]::new('', $securePassword).Password
$outerZip = Join-Path $destination '.recovery.zip'
$migrationPath = Join-Path $destination 'WarehouseEPI-migration.zip'
$pinPath = Join-Path $destination 'pinlookupkey.json'
$completed = $false
try {
    New-Item -ItemType Directory -Path $destination | Out-Null
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @($sid, 'S-1-5-18', 'S-1-5-32-544') | Select-Object -Unique) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($identity), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $destination -AclObject $acl
    [WarehouseEpi.ManualBackupRecovery]::Open($source, $outerZip, $password)
    $archive = [IO.Compression.ZipFile]::OpenRead($outerZip)
    try {
        if ($archive.Entries.Count -ne 2 -or @($archive.Entries | Where-Object FullName -CEQ 'migration.zip').Count -ne 1 -or
            @($archive.Entries | Where-Object FullName -CEQ 'secrets.json').Count -ne 1) { throw 'Contenido de respaldo no reconocido.' }
        $secretEntry = $archive.GetEntry('secrets.json')
        if ($secretEntry.Length -gt 4096) { throw 'Contenido de clave no válido.' }
        $reader = [IO.StreamReader]::new($secretEntry.Open())
        try { $secrets = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        if ($secrets.SchemaVersion -ne 1 -or [Convert]::FromBase64String($secrets.PinLookupKey).Length -lt 32) {
            throw 'Contenido de clave no válido.'
        }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('migration.zip'), $migrationPath)
        $hash = (Get-FileHash -LiteralPath $migrationPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne $secrets.MigrationPackageSha256) { throw 'La clave no corresponde al paquete recuperado.' }
        [IO.File]::WriteAllText($pinPath, ($secrets | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText("$migrationPath.sha256", "$hash  WarehouseEPI-migration.zip`n", [Text.ASCIIEncoding]::new())
    }
    finally { $archive.Dispose() }
    $null = & (Join-Path $PSScriptRoot 'Test-WarehouseEpiMigrationBackup.ps1') -PackagePath $migrationPath -RequireExternalHash
    $completed = $true
    Write-Host "Recuperado en: $destination"
    Write-Host 'La carpeta contiene datos y PinLookupKey en claro, protegidos por ACL. Consérvela privada. IT debe probar una restauración aislada.'
}
finally {
    $password = $null
    $securePassword.Dispose()
    if (Test-Path -LiteralPath $outerZip) { Remove-Item -LiteralPath $outerZip -Force }
    if (-not $completed) {
        foreach ($file in @($migrationPath, "$migrationPath.sha256", $pinPath)) {
            if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
        }
    }
}
