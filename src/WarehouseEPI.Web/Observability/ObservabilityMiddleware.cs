using System.Diagnostics;
using System.Net;

namespace WarehouseEPI.Web.Observability;

internal sealed class CorrelationAndRequestLoggingMiddleware(RequestDelegate next)
{
    internal const string HeaderName = "X-Correlation-ID";
    private static readonly Action<ILogger, string, string, int, long, string, string, Exception?> RequestCompleted =
        LoggerMessage.Define<string, string, int, long, string, string>(LogLevel.Information, new EventId(10501, "RequestCompleted"),
            "Solicitud completada. {RequestMethod} {RequestPath} {StatusCode} {ElapsedMilliseconds}ms {CorrelationId} {FailureCategory}");

    public async Task InvokeAsync(HttpContext context, ILoggerFactory loggerFactory, RecentFailureStore failures)
    {
        var correlationId = ReadOrCreateCorrelationId(context.Request.Headers[HeaderName]);
        context.Response.Headers[HeaderName] = correlationId;
        var logger = loggerFactory.CreateLogger("WarehouseEPI.Observability.Request");
        using var scope = logger
            .BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId });
        var stopwatch = Stopwatch.StartNew();
        var failureRecorded = false;
        var failureCategory = "None";

        try
        {
            await next(context);
        }
        catch (Exception)
        {
            failures.Record(correlationId, "Unhandled", StatusCodes.Status500InternalServerError);
            failureRecorded = true;
            failureCategory = "Unhandled";
            throw;
        }
        finally
        {
            stopwatch.Stop();
            var statusCode = context.Response.StatusCode;
            if (statusCode >= StatusCodes.Status500InternalServerError && !failureRecorded)
            {
                failures.Record(correlationId, "HttpServerError", statusCode);
                failureCategory = "HttpServerError";
            }

            RequestCompleted(logger, context.Request.Method, context.Request.Path.Value ?? "/", statusCode,
                stopwatch.ElapsedMilliseconds, correlationId, failureCategory, null);
        }
    }

    internal static string ReadOrCreateCorrelationId(string? value) =>
        Guid.TryParse(value, out var parsed) ? parsed.ToString("D") : Guid.NewGuid().ToString("D");
}

internal static class LoopbackHealthEndpoint
{
    internal static bool IsLoopback(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }
}
