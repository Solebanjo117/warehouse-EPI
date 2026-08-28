namespace WarehouseEPI.Tests.Security;

public sealed class BackupScriptContractTests
{
    [Theory]
    [InlineData("Initialize-WarehouseEpiBackupDirectory.ps1")]
    [InlineData("Initialize-WarehouseEpiBackupCredentials.ps1")]
    [InlineData("Invoke-WarehouseEpiBackup.ps1")]
    [InlineData("Test-WarehouseEpiBackupRestore.ps1")]
    [InlineData("Invoke-WarehouseEpiRecoveryValidation.ps1")]
    [InlineData("Install-WarehouseEpiBackupTasks.ps1")]
    [InlineData("New-WarehouseEpiMigrationBackup.ps1")]
    [InlineData("Restore-WarehouseEpiMigrationBackup.ps1")]
    public void Backup_scripts_are_present_and_keep_data_under_programdata(string scriptName)
    {
        var content = File.ReadAllText(ScriptPath(scriptName));

        Assert.Contains("C:\\ProgramData\\WarehouseEPI", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings:Warehouse", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_script_validates_before_publishing_and_removes_only_its_old_files()
    {
        var content = File.ReadAllText(ScriptPath("Invoke-WarehouseEpiBackup.ps1"));

        Assert.Contains("pg_dump.exe", content, StringComparison.Ordinal);
        Assert.Contains("--format=custom", content, StringComparison.Ordinal);
        Assert.Contains("pg_restore.exe", content, StringComparison.Ordinal);
        Assert.Contains("--list", content, StringComparison.Ordinal);
        Assert.Contains(".partial", content, StringComparison.Ordinal);
        Assert.Contains("warehouseEPI-*.dump", content, StringComparison.Ordinal);
        Assert.Contains("RetentionDays", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_validation_uses_a_generated_temporary_database_and_drops_it()
    {
        var content = File.ReadAllText(ScriptPath("Test-WarehouseEpiBackupRestore.ps1"));

        Assert.Contains("warehouse_epi_restore_validation_", content, StringComparison.Ordinal);
        Assert.Contains("CREATE DATABASE", content, StringComparison.Ordinal);
        Assert.Contains("DROP DATABASE IF EXISTS", content, StringComparison.Ordinal);
        Assert.Contains("ruta insegura", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("archivo no declarado", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--dbname=warehouseEPI", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_package_is_fresh_validated_and_excludes_secrets()
    {
        var create = File.ReadAllText(ScriptPath("New-WarehouseEpiMigrationBackup.ps1"));
        var validate = File.ReadAllText(ScriptPath("Test-WarehouseEpiMigrationBackup.ps1"));

        Assert.Contains("Invoke-WarehouseEpiBackup.ps1", create, StringComparison.Ordinal);
        Assert.Contains("Test-WarehouseEpiBackupRestore.ps1", create, StringComparison.Ordinal);
        Assert.Contains("Test-WarehouseEpiMigrationBackup.ps1", create, StringComparison.Ordinal);
        Assert.Contains("BrandingDirectory", create, StringComparison.Ordinal);
        Assert.Contains("RequiredExternalSecrets", create, StringComparison.Ordinal);
        Assert.Contains("ContainsSecrets = $false", create, StringComparison.Ordinal);
        Assert.DoesNotContain("user-secrets list", create, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service-settings.json", create, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SHA256", validate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RequireExternalHash", validate, StringComparison.Ordinal);
        Assert.Contains("ruta insegura", validate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("archivo no declarado", validate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Migration_restore_never_replaces_an_existing_database_and_revalidates_first()
    {
        var content = File.ReadAllText(ScriptPath("Restore-WarehouseEpiMigrationBackup.ps1"));

        Assert.Contains("SupportsShouldProcess", content, StringComparison.Ordinal);
        Assert.Contains("Test-WarehouseEpiMigrationBackup.ps1", content, StringComparison.Ordinal);
        Assert.Contains("Test-WarehouseEpiBackupRestore.ps1", content, StringComparison.Ordinal);
        Assert.Contains("warehouseEPI ya existe", content, StringComparison.Ordinal);
        Assert.Contains("CREATE DATABASE", content, StringComparison.Ordinal);
        Assert.Contains("--exit-on-error", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--clean", content, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP DATABASE IF EXISTS", content[..content.IndexOf("catch", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    private static string ScriptPath(string scriptName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "security", scriptName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio para validar scripts de respaldo.");
    }
}
