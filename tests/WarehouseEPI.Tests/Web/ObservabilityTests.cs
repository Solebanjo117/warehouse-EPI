using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarehouseEPI.Web.Observability;

namespace WarehouseEPI.Tests.Web;

public sealed class ObservabilityTests
{
    private static readonly Action<ILogger, string, string, string, string, Exception?> SensitiveTestLog =
        LoggerMessage.Define<string, string, string, string>(LogLevel.Information, new EventId(1, "SensitiveTest"),
            "Solicitud {RequestPath} {CorrelationId} {Pin} {Cookie}");

    [Fact]
    public void Production_observability_requires_an_absolute_existing_writable_directory()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:LogDirectory"] = "relative-logs"
        }).Build();

        var exception = Assert.Throws<InvalidOperationException>(() => ObservabilitySettings.Load(configuration, true));

        Assert.Contains("ruta absoluta", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Correlation_identifier_is_propagated_only_when_valid()
    {
        var expected = Guid.NewGuid();

        Assert.Equal(expected.ToString("D"), CorrelationAndRequestLoggingMiddleware.ReadOrCreateCorrelationId(expected.ToString("N")));
        Assert.NotEqual("not-a-guid", CorrelationAndRequestLoggingMiddleware.ReadOrCreateCorrelationId("not-a-guid"));
        Assert.True(Guid.TryParse(CorrelationAndRequestLoggingMiddleware.ReadOrCreateCorrelationId(null), out _));
    }

    [Fact]
    public void Json_request_log_omits_sensitive_properties()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"warehouse-epi-observability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new ObservabilitySettings
            {
                LogDirectory = directory,
                RetentionDays = 30,
                FileSizeLimitBytes = 1024 * 1024
            };
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(new JsonRollingFileLoggerProvider(settings)));
            SensitiveTestLog(factory.CreateLogger("WarehouseEPI.Observability.Request"), "/Operations", "safe-correlation",
                "0123", "secret-cookie", null);

            var content = File.ReadAllText(Directory.GetFiles(directory, "*.jsonl").Single());
            Assert.Contains("safe-correlation", content, StringComparison.Ordinal);
            Assert.DoesNotContain("0123", content, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-cookie", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Request_log_uses_path_without_query_string_and_returns_correlation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"warehouse-epi-request-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new ObservabilitySettings { LogDirectory = directory, RetentionDays = 30, FileSizeLimitBytes = 1024 * 1024 };
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(new JsonRollingFileLoggerProvider(settings)));
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/Operations/Entry";
            context.Request.QueryString = new QueryString("?nip=0123&token=secret");
            context.Response.StatusCode = StatusCodes.Status200OK;

            await new CorrelationAndRequestLoggingMiddleware(_ => Task.CompletedTask)
                .InvokeAsync(context, factory, new RecentFailureStore());

            var content = File.ReadAllText(Directory.GetFiles(directory, "*.jsonl").Single());
            Assert.Contains("/Operations/Entry", content, StringComparison.Ordinal);
            Assert.DoesNotContain("nip=0123", content, StringComparison.Ordinal);
            Assert.DoesNotContain("token=secret", content, StringComparison.Ordinal);
            Assert.True(Guid.TryParse(context.Response.Headers[CorrelationAndRequestLoggingMiddleware.HeaderName], out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.20", false)]
    public void Live_health_accepts_only_loopback_addresses(string address, bool expected) =>
        Assert.Equal(expected, LoopbackHealthEndpoint.IsLoopback(System.Net.IPAddress.Parse(address)));
}
