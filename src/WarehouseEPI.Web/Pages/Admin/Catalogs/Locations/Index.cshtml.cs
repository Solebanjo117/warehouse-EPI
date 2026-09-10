using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Locations;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(
    WarehouseDbContext dbContext,
    WarehouseMapService mapService,
    WipReportService wipReportService,
    HeatmapReportService heatmapReportService,
    ReportExportService reportExportService,
    WarehouseClock clock)
    : LocationIndexPageModel(dbContext, mapService, wipReportService, heatmapReportService, reportExportService, clock)
{
    public override bool IsAdministrativeView => true;

    internal IndexModel(WarehouseDbContext dbContext) : this(
        dbContext,
        new WarehouseMapService(dbContext),
        new WipReportService(dbContext, new WarehouseClock(new WarehouseSettingsService(dbContext))),
        new HeatmapReportService(dbContext, new WarehouseMapService(dbContext), new WarehouseSettingsService(dbContext)),
        new ReportExportService(new WarehouseSettingsService(dbContext)),
        new WarehouseClock(new WarehouseSettingsService(dbContext)))
    { }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, CancellationToken cancellationToken)
    {
        var location = await DbContext.Locations.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (location is null) return NotFound();
        if (!location.IsPhysicallyPresent) { Error = "Una posición retirada solo puede restaurarse desde Editar rack."; return RedirectToPage(); }
        location.IsActive = !location.IsActive;
        location.IsBlocked = false;
        location.BlockReason = null;
        location.UpdatedAt = DateTimeOffset.UtcNow;
        await DbContext.SaveChangesAsync(cancellationToken);
        Message = location.IsActive ? $"{location.Code} fue activada." : $"{location.Code} fue desactivada.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBlockAsync(Guid id, string? reason, CancellationToken cancellationToken)
    {
        var location = await DbContext.Locations.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (location is null) return NotFound();
        if (!location.IsPhysicallyPresent) { Error = "No se puede bloquear una posición retirada."; return RedirectToPage(); }
        var normalizedReason = reason?.Trim();
        if (!location.IsActive) Error = "No se puede bloquear una ubicación inactiva.";
        else if (string.IsNullOrWhiteSpace(normalizedReason) || normalizedReason.Length > 200)
            Error = "Escribe un motivo de bloqueo de hasta 200 caracteres.";
        else
        {
            location.IsBlocked = true;
            location.BlockReason = normalizedReason;
            location.UpdatedAt = DateTimeOffset.UtcNow;
            await DbContext.SaveChangesAsync(cancellationToken);
            Message = $"{location.Code} fue bloqueada.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUnblockAsync(Guid id, CancellationToken cancellationToken)
    {
        var location = await DbContext.Locations.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (location is null) return NotFound();
        if (!location.IsPhysicallyPresent) { Error = "Una posición retirada solo puede restaurarse desde Editar rack."; return RedirectToPage(); }
        location.IsBlocked = false;
        location.BlockReason = null;
        location.UpdatedAt = DateTimeOffset.UtcNow;
        await DbContext.SaveChangesAsync(cancellationToken);
        Message = $"{location.Code} fue desbloqueada.";
        return RedirectToPage();
    }
}
