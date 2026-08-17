namespace WarehouseEPI.Tests.Security;

public sealed class ReleaseScriptContractTests
{
    [Theory]
    [InlineData("Publish-WarehouseEpiRelease.ps1")]
    [InlineData("Initialize-WarehouseEpiServiceConfiguration.ps1")]
    [InlineData("Install-WarehouseEpiService.ps1")]
    [InlineData("Update-WarehouseEpiService.ps1")]
    [InlineData("Rollback-WarehouseEpiService.ps1")]
    [InlineData("WarehouseEpi.Release.Common.ps1")]
    public void Required_release_scripts_exist(string scriptName) =>
        Assert.True(File.Exists(ScriptPath(scriptName)), $"Falta {scriptName}.");

    [Fact]
    public void Publisher_requires_clean_git_semver_self_contained_win_x64_and_hash_manifest()
    {
        var content = File.ReadAllText(ScriptPath("Publish-WarehouseEpiRelease.ps1"));

        Assert.Contains("status --porcelain", content, StringComparison.Ordinal);
        Assert.Contains("--self-contained true", content, StringComparison.Ordinal);
        Assert.Contains("--runtime win-x64", content, StringComparison.Ordinal);
        Assert.Contains("release-manifest.json", content, StringComparison.Ordinal);
        Assert.Contains("SHA256", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_has_preflight_health_check_and_automatic_rollback_without_migrations()
    {
        var update = File.ReadAllText(ScriptPath("Update-WarehouseEpiService.ps1"));
        var common = File.ReadAllText(ScriptPath("WarehouseEpi.Release.Common.ps1"));

        Assert.Contains("Invoke-WarehouseEpiPreflight", update, StringComparison.Ordinal);
        Assert.Contains("Set-WarehouseEpiServiceBinary $previousExecutable", update, StringComparison.Ordinal);
        Assert.Contains("health/live", common, StringComparison.Ordinal);
        Assert.Contains("--contentRoot", common, StringComparison.Ordinal);
        Assert.Contains("NT SERVICE\\WarehouseEPI", common, StringComparison.Ordinal);
        Assert.DoesNotContain("database update", update, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet ef", update, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Service_configuration_migration_never_prints_secret_values()
    {
        var content = File.ReadAllText(ScriptPath("Initialize-WarehouseEpiServiceConfiguration.ps1"));

        Assert.Contains("user-secrets list --json", content, StringComparison.Ordinal);
        Assert.Contains("icacls", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host $secrets", content, StringComparison.OrdinalIgnoreCase);
    }

    private static string ScriptPath(string scriptName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "release", scriptName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
