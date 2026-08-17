using Microsoft.Extensions.Configuration;
using WarehouseEPI.Web.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionSecuritySettingsTests
{
    [Fact]
    public void Production_settings_reject_wildcard_allowed_hosts()
    {
        var keysPath = Path.Combine(Path.GetTempPath(), $"WarehouseEPI-Security-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysPath);
        try
        {
            var configuration = Configuration(keysPath, "*");

            var exception = Assert.Throws<InvalidOperationException>(() => ProductionSecuritySettings.Load(configuration));

            Assert.Contains("AllowedHosts", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(keysPath, recursive: true);
        }
    }

    [Fact]
    public void Production_settings_accept_reserved_host_and_valid_rate_limits()
    {
        var keysPath = Path.Combine(Path.GetTempPath(), $"WarehouseEPI-Security-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysPath);
        try
        {
            var settings = ProductionSecuritySettings.Load(Configuration(keysPath, "warehouse-epi;192.168.1.50"));

            Assert.Equal(keysPath, settings.DataProtectionKeysPath);
            Assert.Equal("ABC123", settings.ServerCertificateThumbprint);
            Assert.Equal(5, settings.RateLimits.AdminLoginPermitLimit);
        }
        finally
        {
            Directory.Delete(keysPath, recursive: true);
        }
    }

    private static IConfiguration Configuration(string keysPath, string allowedHosts) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = allowedHosts,
            ["Security:DataProtectionKeysPath"] = keysPath,
            ["Security:ServerCertificateThumbprint"] = "ab c1 23",
            ["Security:RateLimits:AdminLoginPermitLimit"] = "5",
            ["Security:RateLimits:AdminLoginWindowMinutes"] = "5",
            ["Security:RateLimits:AdminPostPermitLimit"] = "10",
            ["Security:RateLimits:AdminPostWindowMinutes"] = "1",
            ["Security:RateLimits:OperationPostPermitLimit"] = "30",
            ["Security:RateLimits:OperationPostWindowMinutes"] = "1"
        }).Build();
}
