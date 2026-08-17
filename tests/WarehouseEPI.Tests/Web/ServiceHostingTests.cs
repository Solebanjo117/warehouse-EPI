using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Hosting;

namespace WarehouseEPI.Tests.Web;

public sealed class ServiceHostingTests
{
    [Theory]
    [InlineData("service-settings.json")]
    [InlineData("C:\\Temp\\service-settings.json")]
    [InlineData("C:\\ProgramData\\WarehouseEPI\\Config\\service-settings.txt")]
    public void Service_configuration_rejects_unprotected_or_non_json_paths(string path) =>
        Assert.Throws<InvalidOperationException>(() => ServiceConfigurationLoader.ValidatePath(path));

    [Fact]
    public void Service_configuration_accepts_the_protected_programdata_location()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = @"C:\ProgramData\WarehouseEPI\Config\service-settings.json";

        Assert.Equal(path, ServiceConfigurationLoader.ValidatePath(path));
    }

    [Fact]
    public async Task Production_preflight_accepts_a_connectable_database()
    {
        await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"preflight-{Guid.NewGuid():N}").Options);

        await ProductionPreflightValidator.ValidateDatabaseAsync(db);
    }

    [Fact]
    public async Task Production_preflight_degrades_without_exposing_connection_details()
    {
        const string secret = "do-not-expose";
        await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseNpgsql($"Host=127.0.0.1;Port=1;Database=missing;Username=missing;Password={secret};Timeout=1;Command Timeout=1").Options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProductionPreflightValidator.ValidateDatabaseAsync(db));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.Equal("La comprobación de conectividad PostgreSQL falló.", exception.Message);
    }
}
