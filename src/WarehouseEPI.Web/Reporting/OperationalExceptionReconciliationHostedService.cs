using WarehouseEPI.Infrastructure.Reporting;

namespace WarehouseEPI.Web.Reporting;

/// <summary>Reconciles derived conditions without allowing report GET requests to write state.</summary>
public sealed class OperationalExceptionReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<OperationalExceptionReconciliationHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly Action<ILogger, Exception?> ReconciliationFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(2101, "OperationalExceptionReconciliationFailed"),
        "No fue posible reconciliar el centro de excepciones.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await ReconcileSafelyAsync(stoppingToken);
    }

    private async Task ReconcileSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<OperationalExceptionService>();
            await service.ReconcileAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReconciliationFailed(logger, exception);
        }
    }
}
