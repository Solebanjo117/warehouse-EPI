using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WarehouseEPI.Web.Backups;

namespace WarehouseEPI.Tests.Web;

public sealed class DevelopmentRestoreConfigurationTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Development_restore_cannot_be_enabled_in_another_environment(string environment)
    {
        var host = new ManualBackupTests.TestEnvironment { EnvironmentName = environment };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DevelopmentRestoreConfiguration.ConfigurationKey] = @"C:\some-config.json"
        });
        Assert.Throws<InvalidOperationException>(() => DevelopmentRestoreConfiguration.Configure(host, configuration));
    }

    [Fact]
    public void Normal_production_does_not_load_or_change_development_settings()
    {
        var host = new ManualBackupTests.TestEnvironment { EnvironmentName = "Production" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Warehouse"] = "production fixture"
        });
        Assert.False(DevelopmentRestoreConfiguration.Configure(host, configuration));
        Assert.Equal("production fixture", configuration.Build().GetConnectionString("Warehouse"));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("database")]
    [InlineData("port")]
    [InlineData("host")]
    [InlineData("folder")]
    [InlineData("config-path")]
    public void Only_the_isolated_development_database_and_directories_are_accepted(string defect)
    {
        using var fixture = new ManualBackupTests.BackupFixture();
        var project = Path.Combine(fixture.Directory, "src", "WarehouseEPI.Web");
        var root = Path.Combine(fixture.Directory, "artifacts", "local-restore");
        Directory.CreateDirectory(project); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            ConnectionStrings = new { Warehouse = $"Host={(defect == "host" ? "remote.example.com" : "127.0.0.1")};Port={(defect == "port" ? 5432 : 55432)};Database={(defect == "database" ? "warehouseEPI" : "warehouse_epi_restore_dev")};Username=warehouse_epi_dev_app" },
            Development = new { PostgreSqlPort = 55432, UseEphemeralDataProtection = true },
            Backups = new { ManualDirectory = defect == "folder" ? @"C:\ProgramData\WarehouseEPI\ManualBackups" : Path.Combine(root, "ManualBackups") },
            Branding = new { StorageDirectory = Path.Combine(root, "Branding") },
            WarehouseMap = new { ReferenceStorageDirectory = Path.Combine(root, "WarehouseMapReferences") },
            Observability = new { LogDirectory = Path.Combine(root, "Logs") }
        }));
        var host = new ManualBackupTests.TestEnvironment { ContentRootPath = project };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DevelopmentRestoreConfiguration.ConfigurationKey] = defect == "config-path" ? Path.Combine(root, "..", "other.json") : path,
            ["ConnectionStrings:Warehouse"] = "ignored inherited production fixture"
        });
        if (defect == "valid")
        {
            Assert.True(DevelopmentRestoreConfiguration.Configure(host, configuration));
            Assert.Contains("warehouse_epi_restore_dev", configuration.Build().GetConnectionString("Warehouse"));
        }
        else Assert.Throws<InvalidOperationException>(() => DevelopmentRestoreConfiguration.Configure(host, configuration));
    }
}
