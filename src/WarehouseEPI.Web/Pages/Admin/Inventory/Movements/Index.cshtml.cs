using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Movements;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(
    InventoryHistoryService history,
    MovementReportService reportService,
    ReportExportService exportService,
    WarehouseClock clock,
    WarehouseDbContext dbContext,
    WarehouseSettingsService settingsService) : PageModel
{
    private const int PageSize = 25;

    public EffectiveMovementPage EffectiveResults { get; private set; } = new([], 0, 1, PageSize);
    public InventoryMovementHistoryPage AuditResults { get; private set; } = new([], 0);
    public IReadOnlyDictionary<Guid, DateTimeOffset> LocalOccurredAt { get; private set; } = new Dictionary<Guid, DateTimeOffset>();
    public IReadOnlyList<SelectListItem> UserOptions { get; private set; } = [];
    public string View { get; private set; } = "effective";
    public bool IsAudit => View == "audit";
    public int TotalCount => IsAudit ? AuditResults.TotalCount : EffectiveResults.TotalCount;
    public int TotalPages => IsAudit ? Math.Max(1, (int)Math.Ceiling(AuditResults.TotalCount / (double)PageSize)) : EffectiveResults.TotalPages;
    public string? Search { get; private set; }
    public string? Sku { get; private set; }
    public string? LocationCode { get; private set; }
    public DateOnly? From { get; private set; }
    public DateOnly? To { get; private set; }
    public string Period { get; private set; } = "30";
    public InventoryMovementType? MovementType { get; private set; }
    public InventoryMovementPurpose? Purpose { get; private set; }
    public Guid? ResponsibleUserId { get; private set; }
    public InventoryHistoryCorrectionState State { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public string TimeZoneId { get; private set; } = "UTC";

    public bool CanGeneratePalletPlate(EffectiveMovementRowDto item) =>
        PalletLicensePlateService.IsEligible(item.MovementType, item.Purpose, item.LineCount);

    public async Task OnGetAsync(
        string? view, string? search, string? sku, string? locationCode,
        DateOnly? from, DateOnly? to, string? period = "30",
        InventoryMovementType? movementType = null, InventoryMovementType? type = null,
        InventoryMovementPurpose? purpose = null, Guid? responsibleUserId = null,
        InventoryHistoryCorrectionState state = InventoryHistoryCorrectionState.All,
        int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        await SetRequestStateAsync(view, search, sku, locationCode, from, to, period, movementType ?? type, purpose, responsibleUserId, state, pageNumber, cancellationToken);
        var interval = await clock.GetUtcIntervalAsync(From, To, cancellationToken);
        if (IsAudit)
        {
            AuditResults = await history.SearchAsync(BuildAuditFilter(interval.FromInclusive, interval.ToExclusive), PageNumber, PageSize, cancellationToken);
            PageNumber = Math.Min(PageNumber, TotalPages);
            await LoadLocalDatesAsync(AuditResults.Items.Select(item => (item.Id, item.OccurredAt)), cancellationToken);
        }
        else
        {
            EffectiveResults = await reportService.GetMovementsPageAsync(BuildEffectiveFilter(interval.FromInclusive, interval.ToExclusive, PageNumber), cancellationToken);
            PageNumber = Math.Min(PageNumber, TotalPages);
            await LoadLocalDatesAsync(EffectiveResults.Items.Select(item => (item.Id, item.OccurredAt)), cancellationToken);
        }
        await LoadUserOptionsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnGetExportAsync(
        string format, string? view, string? search, string? sku, string? locationCode,
        DateOnly? from, DateOnly? to, string? period = "30",
        InventoryMovementType? movementType = null, InventoryMovementType? type = null,
        InventoryMovementPurpose? purpose = null, Guid? responsibleUserId = null,
        InventoryHistoryCorrectionState state = InventoryHistoryCorrectionState.All,
        CancellationToken cancellationToken = default)
    {
        await SetRequestStateAsync(view, search, sku, locationCode, from, to, period, movementType ?? type, purpose, responsibleUserId, state, 1, cancellationToken);
        var interval = await clock.GetUtcIntervalAsync(From, To, cancellationToken);
        var localNow = await clock.ConvertAsync(DateTimeOffset.UtcNow, cancellationToken);
        if (IsAudit)
        {
            var filter = BuildAuditFilter(interval.FromInclusive, interval.ToExclusive);
            var batch = await history.GetTraceExportAsync(filter, TimeZoneId, 10000, cancellationToken);
            if (batch.ExceedsLimit)
                return BadRequest($"La exportación contiene {batch.TotalRows:N0} filas en {batch.TotalOperations:N0} operaciones y supera el límite de {batch.MaximumRows:N0} filas. Aplica filtros más específicos.");
            var auditName = $"auditoria-movimientos-{localNow:yyyyMMdd-HHmmss}";
            if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
                return File(await exportService.ExportMovementAuditToExcelAsync(batch.Items, filter, cancellationToken), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{auditName}.xlsx");
            return File(await exportService.ExportMovementAuditToCsvAsync(batch.Items, filter, cancellationToken), "text/csv; charset=utf-8", $"{auditName}.csv");
        }

        var effectiveFilter = BuildEffectiveFilter(interval.FromInclusive, interval.ToExclusive, 1);
        var effectiveBatch = await reportService.GetMovementsForExportAsync(effectiveFilter, 10000, cancellationToken);
        if (effectiveBatch.ExceedsLimit)
            return BadRequest($"La exportación contiene {effectiveBatch.TotalRows:N0} líneas en {effectiveBatch.TotalOperations:N0} operaciones y supera el límite de {effectiveBatch.MaximumRows:N0} líneas. Aplica filtros más específicos.");
        var effectiveName = $"movimientos-efectivos-{localNow:yyyyMMdd-HHmmss}";
        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
            return File(await exportService.ExportMovementsToExcelAsync(effectiveBatch.Items, effectiveFilter, cancellationToken), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{effectiveName}.xlsx");
        return File(await exportService.ExportMovementsToCsvAsync(effectiveBatch.Items, effectiveFilter, cancellationToken), "text/csv; charset=utf-8", $"{effectiveName}.csv");
    }

    private async Task SetRequestStateAsync(
        string? view, string? search, string? sku, string? locationCode,
        DateOnly? from, DateOnly? to, string? period, InventoryMovementType? movementType,
        InventoryMovementPurpose? purpose, Guid? responsibleUserId,
        InventoryHistoryCorrectionState state, int pageNumber, CancellationToken cancellationToken)
    {
        View = string.Equals(view, "audit", StringComparison.OrdinalIgnoreCase) ? "audit" : "effective";
        Period = period ?? "30";
        TimeZoneId = (await settingsService.GetAsync(cancellationToken)).TimeZoneId;
        var today = await clock.GetDateAsync(DateTimeOffset.UtcNow, cancellationToken);
        if (from is null && to is null && Period != "all")
        {
            var days = Period switch { "today" => 0, "7" => 6, _ => 29 };
            from = today.AddDays(-days);
            to = today;
        }
        Search = search?.Trim();
        Sku = sku?.Trim();
        LocationCode = locationCode?.Trim();
        From = from;
        To = to;
        MovementType = movementType;
        Purpose = purpose;
        ResponsibleUserId = responsibleUserId;
        State = state;
        PageNumber = Math.Max(1, pageNumber);
    }

    private MovementReportFilter BuildEffectiveFilter(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int pageNumber) =>
        new(fromUtc, toUtc, Search, Sku, LocationCode, MovementType, Purpose, ResponsibleUserId, pageNumber, PageSize);

    private InventoryHistoryFilter BuildAuditFilter(DateTimeOffset? fromUtc, DateTimeOffset? toUtc) =>
        new(fromUtc, toUtc, MovementType, Search, null, null, ResponsibleUserId, State, Purpose, Sku, LocationCode);

    private async Task LoadLocalDatesAsync(IEnumerable<(Guid Id, DateTimeOffset OccurredAt)> values, CancellationToken cancellationToken)
    {
        var dates = new Dictionary<Guid, DateTimeOffset>();
        foreach (var value in values)
            dates[value.Id] = await clock.ConvertAsync(value.OccurredAt, cancellationToken);
        LocalOccurredAt = dates;
    }

    private async Task LoadUserOptionsAsync(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users.AsNoTracking().OrderBy(user => user.FullName)
            .Select(user => new { user.Id, user.FullName, user.IsActive }).ToListAsync(cancellationToken);
        UserOptions = users.Select(user => new SelectListItem(
            user.IsActive ? user.FullName : $"{user.FullName} (inactivo)", user.Id.ToString(), user.Id == ResponsibleUserId)).ToArray();
    }
}
