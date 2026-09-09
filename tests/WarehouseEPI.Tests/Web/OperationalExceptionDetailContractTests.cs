namespace WarehouseEPI.Tests.Web;

public sealed class OperationalExceptionDetailContractTests
{
    [Fact]
    public void Detail_explains_the_condition_and_keeps_contextual_actions_for_all_categories()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Alerts", "Details.cshtml");

        Assert.Contains("Por qué apareció", page, StringComparison.Ordinal);
        Assert.Contains("Siguiente paso", page, StringComparison.Ordinal);
        Assert.Contains("@item.ReasonText", page, StringComparison.Ordinal);
        Assert.Contains("Revisar y ajustar saldo", page, StringComparison.Ordinal);
        Assert.Contains("Preparar transferencia", page, StringComparison.Ordinal);
        Assert.Contains("Revisar asignación", page, StringComparison.Ordinal);
        Assert.Contains("Revisar ubicación", page, StringComparison.Ordinal);
        Assert.Contains("Registrar salida", page, StringComparison.Ordinal);
        Assert.Contains("Abrir conteo", page, StringComparison.Ordinal);
        Assert.Contains("Procesar WIP", page, StringComparison.Ordinal);
        Assert.Contains("href=\"@item.TargetUrl\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">Actuar sobre condición<", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Follow_up_is_accessible_and_history_exposes_transitions_newest_first()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Alerts", "Details.cshtml");
        var pageModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Alerts", "Details.cshtml.cs");
        var service = Read("src", "WarehouseEPI.Infrastructure", "Reporting", "OperationalExceptionService.cs");

        Assert.Contains("<textarea asp-for=\"Input.Notes\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-validation-for=\"Input.Notes\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"exception-note-help\"", page, StringComparison.Ordinal);
        Assert.Contains("history.PreviousStatus", page, StringComparison.Ordinal);
        Assert.Contains("history.PreviousAssignedUserName", page, StringComparison.Ordinal);
        Assert.Contains("Más reciente primero", page, StringComparison.Ordinal);
        Assert.Contains("WarehouseClock", pageModel, StringComparison.Ordinal);
        Assert.Contains("RelativeTime", pageModel, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = \"AdminOnly\")]", pageModel, StringComparison.Ordinal);
        Assert.Contains("OrderByDescending(history => history.RecordedAt)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-antiforgery=\"false\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_styles_use_a_responsive_local_component()
    {
        var styles = Read("src", "WarehouseEPI.Web", "wwwroot", "css", "site.css");

        Assert.Contains(".exception-detail-guidance", styles, StringComparison.Ordinal);
        Assert.Contains(".exception-timeline", styles, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 991.98px)", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 575.98px)", styles, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var configuredCandidate = Path.Combine([configuredRoot, .. parts]);
            if (File.Exists(configuredCandidate)) return File.ReadAllText(configuredCandidate);
        }

        var workingDirectoryCandidate = Path.Combine([Directory.GetCurrentDirectory(), .. parts]);
        if (File.Exists(workingDirectoryCandidate)) return File.ReadAllText(workingDirectoryCandidate);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
