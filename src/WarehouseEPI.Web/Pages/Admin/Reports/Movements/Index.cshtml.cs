using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Reports.Movements;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(
    MovementReportService reportService,
    ReportExportService exportService,
    WarehouseClock clock,
    WarehouseDbContext dbContext,
    WarehouseSettingsService settingsService) : PageModel
{
    public EffectiveMovementPage Results { get; private set; } = new([], 0, 1, 25);

    public IReadOnlyDictionary<Guid, DateTimeOffset> LocalOccurredAt { get; private set; } =
        new Dictionary<Guid, DateTimeOffset>();

    public DateOnly? From { get; private set; }
    public DateOnly? To { get; private set; }
    public string Period { get; private set; } = "30";
    public string? Search { get; private set; }
    public string? Sku { get; private set; }
    public string? LocationCode { get; private set; }
    public InventoryMovementType? MovementType { get; private set; }
    public InventoryMovementPurpose? Purpose { get; private set; }
    public Guid? ResponsibleUserId { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int PageSize { get; private set; } = 25;
    public string TimeZoneId { get; private set; } = "UTC";

    public IReadOnlyList<SelectListItem> UserOptions { get; private set; } = [];

    public async Task OnGetAsync(
        string? search,
        string? sku,
        string? locationCode,
        DateOnly? from,
        DateOnly? to,
        string? period = "30",
        InventoryMovementType? movementType = null,
        InventoryMovementPurpose? purpose = null,
        Guid? responsibleUserId = null,
        int pageNumber = 1,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        Period = period ?? "30";
        TimeZoneId = (await settingsService.GetAsync(cancellationToken)).TimeZoneId;
        var today = await clock.GetDateAsync(DateTimeOffset.UtcNow, cancellationToken);

        if (from is null && to is null && Period != "all")
        {
            var days = Period switch
            {
                "today" => 0,
                "7" => 6,
                _ => 29
            };
            from = today.AddDays(-days);
            to = today;
        }

        From = from;
        To = to;
        Search = search?.Trim();
        Sku = sku?.Trim();
        LocationCode = locationCode?.Trim();
        MovementType = movementType;
        Purpose = purpose;
        ResponsibleUserId = responsibleUserId;
        PageNumber = Math.Max(1, pageNumber);
        PageSize = pageSize <= 0 ? 25 : Math.Min(pageSize, 100);

        var utcInterval = await clock.GetUtcIntervalAsync(From, To, cancellationToken);
        var filter = new MovementReportFilter(
            FromUtc: utcInterval.FromInclusive,
            ToUtc: utcInterval.ToExclusive,
            Search: Search,
            Sku: Sku,
            LocationCode: LocationCode,
            MovementType: MovementType,
            Purpose: Purpose,
            ResponsibleUserId: ResponsibleUserId,
            PageNumber: PageNumber,
            PageSize: PageSize);

        Results = await reportService.GetMovementsPageAsync(filter, cancellationToken);
        var localOccurredAt = new Dictionary<Guid, DateTimeOffset>();
        foreach (var item in Results.Items)
            localOccurredAt[item.Id] = await clock.ConvertAsync(item.OccurredAt, cancellationToken);
        LocalOccurredAt = localOccurredAt;
        await LoadUserOptionsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnGetExportAsync(
        string format,
        string? search,
        string? sku,
        string? locationCode,
        DateOnly? from,
        DateOnly? to,
        string? period = "30",
        InventoryMovementType? movementType = null,
        InventoryMovementPurpose? purpose = null,
        Guid? responsibleUserId = null,
        CancellationToken cancellationToken = default)
    {
        Period = period ?? "30";
        var today = await clock.GetDateAsync(DateTimeOffset.UtcNow, cancellationToken);

        if (from is null && to is null && Period != "all")
        {
            var days = Period switch
            {
                "today" => 0,
                "7" => 6,
                _ => 29
            };
            from = today.AddDays(-days);
            to = today;
        }

        var utcInterval = await clock.GetUtcIntervalAsync(from, to, cancellationToken);
        var filter = new MovementReportFilter(
            FromUtc: utcInterval.FromInclusive,
            ToUtc: utcInterval.ToExclusive,
            Search: search?.Trim(),
            Sku: sku?.Trim(),
            LocationCode: locationCode?.Trim(),
            MovementType: movementType,
            Purpose: purpose,
            ResponsibleUserId: responsibleUserId);

        var batch = await reportService.GetMovementsForExportAsync(filter, maxRows: 10000, cancellationToken);
        if (batch.ExceedsLimit)
        {
            return BadRequest(
                $"La exportación contiene {batch.TotalRows:N0} líneas en {batch.TotalOperations:N0} operaciones y supera el límite de {batch.MaximumRows:N0} líneas. Aplica filtros más específicos.");
        }

        var localNow = await clock.ConvertAsync(DateTimeOffset.UtcNow, cancellationToken);
        var fileBaseName = $"reporte-movimientos-efectivos-{localNow:yyyyMMdd-HHmmss}";

        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await exportService.ExportMovementsToExcelAsync(batch.Items, filter, cancellationToken);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{fileBaseName}.xlsx");
        }

        var csvBytes = await exportService.ExportMovementsToCsvAsync(batch.Items, filter, cancellationToken);
        return File(csvBytes, "text/csv; charset=utf-8", $"{fileBaseName}.csv");
    }

    private async Task LoadUserOptionsAsync(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users
            .AsNoTracking()
            .OrderBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName, u.IsActive })
            .ToListAsync(cancellationToken);

        UserOptions = users
            .Select(u => new SelectListItem(
                u.IsActive ? u.FullName : $"{u.FullName} (inactivo)",
                u.Id.ToString(),
                u.Id == ResponsibleUserId))
            .ToArray();
    }
}
