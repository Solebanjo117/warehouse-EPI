using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Observability;

public sealed record SanitizedFailure(DateTimeOffset OccurredAt, string CorrelationId, string Category, int StatusCode);

public sealed class RecentFailureStore
{
    private const int Capacity = 20;
    private readonly ConcurrentQueue<SanitizedFailure> failures = new();

    public void Record(string correlationId, string category, int statusCode)
    {
        failures.Enqueue(new(DateTimeOffset.UtcNow, correlationId, category, statusCode));
        while (failures.Count > Capacity && failures.TryDequeue(out _)) { }
    }

    public IReadOnlyList<SanitizedFailure> GetRecent() => failures.Reverse().ToArray();
}

internal sealed class DatabaseHealthCheck(WarehouseDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var connected = await dbContext.Database.CanConnectAsync(cancellationToken);
            stopwatch.Stop();
            return connected
                ? HealthCheckResult.Healthy(data: new Dictionary<string, object> { ["latencyMs"] = stopwatch.ElapsedMilliseconds })
                : HealthCheckResult.Unhealthy(data: new Dictionary<string, object> { ["latencyMs"] = stopwatch.ElapsedMilliseconds });
        }
        catch (Exception)
        {
            stopwatch.Stop();
            return HealthCheckResult.Unhealthy(data: new Dictionary<string, object> { ["latencyMs"] = stopwatch.ElapsedMilliseconds });
        }
    }
}

public sealed record SystemStatusSnapshot(
    bool DatabaseHealthy, long? DatabaseLatencyMilliseconds, DateTimeOffset CheckedAt, DateTimeOffset StartedAt,
    string Version, IReadOnlyList<MovementActivity> Activity, IReadOnlyList<SanitizedFailure> RecentFailures);
public sealed record MovementActivity(string Type, int Count);

public sealed class ApplicationLifetimeInfo(TimeProvider timeProvider)
{
    public DateTimeOffset StartedAt { get; } = timeProvider.GetUtcNow();
}

public sealed class SystemStatusService(
    HealthCheckService healthChecks, WarehouseDbContext dbContext, RecentFailureStore failures, TimeProvider timeProvider,
    ApplicationLifetimeInfo applicationLifetime)
{
    public async Task<SystemStatusSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(registration => registration.Name == "postgresql", cancellationToken);
        var hasDatabase = report.Entries.TryGetValue("postgresql", out var database);
        long? latency = hasDatabase && database.Data.TryGetValue("latencyMs", out var value) && value is long milliseconds
            ? milliseconds
            : null;
        var since = timeProvider.GetUtcNow().AddHours(-24);
        List<InventoryMovementType> activity;
        try
        {
            activity = await dbContext.InventoryMovements.AsNoTracking()
                .Where(movement => movement.OccurredAt >= since)
                .Select(movement => movement.Type)
                .ToListAsync(cancellationToken);
        }
        catch (Exception)
        {
            activity = [];
        }
        var grouped = activity.GroupBy(type => type).OrderBy(group => group.Key)
            .Select(group => new MovementActivity(group.Key.ToString(), group.Count())).ToArray();
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "desconocida";

        if (report.Status != HealthStatus.Healthy)
            failures.Record("system", "DatabaseHealth", StatusCodes.Status503ServiceUnavailable);

        return new(report.Status == HealthStatus.Healthy, latency, timeProvider.GetUtcNow(), applicationLifetime.StartedAt, version, grouped, failures.GetRecent());
    }
}
