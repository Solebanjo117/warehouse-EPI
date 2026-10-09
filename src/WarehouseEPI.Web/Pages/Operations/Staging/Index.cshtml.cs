using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations.Staging;

public sealed class IndexModel(WarehouseDbContext db, PalletTrackingService tracking, WarehouseClock clock, TimeProvider timeProvider,
    IStringLocalizer<OperationsTexts> texts, MaterialIncidentQuery incidentQuery) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? Priority { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public Guid? Receipt { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? ArrivalLineId { get; set; }
    [BindProperty] public Guid Arrival { get; set; }
    [BindProperty] public Guid OperationId { get; set; }
    [BindProperty] public decimal? PhysicalQuantity { get; set; }
    public StagingArrivalPage Arrivals { get; private set; } = new([], false);
    public IReadOnlyDictionary<Guid, IncidentCounts> Incidents { get; private set; } = new Dictionary<Guid, IncidentCounts>();
    public Dictionary<Guid, DateTimeOffset> LocalTimes { get; } = [];
    public Dictionary<Guid, string> Ages { get; } = [];
    public Dictionary<Guid, StagingPriority> Priorities { get; } = [];
    public StagingPendingSummary Summary { get; private set; } = new(0, 0, 0);
    public static string PriorityText(StagingPriority priority) => priority switch
    { StagingPriority.Urgent => "Urgente", StagingPriority.Soon => "Atender pronto", _ => "Normal" };
    public static string PriorityClass(StagingPriority priority) => priority switch
    { StagingPriority.Urgent => "text-bg-danger", StagingPriority.Soon => "text-bg-warning", _ => "text-bg-secondary" };

    public async Task<IActionResult> OnGetAsync(CancellationToken token) =>
        await LoadAsync(token) ? Page() : NotFound();

    public async Task<IActionResult> OnPostIdentifyAsync(CancellationToken token)
    {
        var row = await new StagingArrivalQuery(db).GetAsync(Arrival, token);
        if (row is null || (ArrivalLineId.HasValue && ArrivalLineId != Arrival)) return NotFound();
        if (ModelState.IsValid)
        {
            var result = await tracking.IdentifyStagingEntryAsync(new(OperationId, row.MovementId, row.LocationId,
                PhysicalQuantity ?? 0, row.LineId), token);
            if (result.Status == InventoryMovementStatus.Success)
                return RedirectToPage("Putaway", new { arrival = row.LineId });
            foreach (var error in result.ValidationErrors.DefaultIfEmpty("No fue posible identificar la entrada. Consulta nuevamente."))
                ModelState.AddModelError(string.Empty, texts[error]);
        }
        return await LoadAsync(token) ? Page() : NotFound();
    }

    private async Task<bool> LoadAsync(CancellationToken token)
    {
        var asOf = timeProvider.GetUtcNow();
        var priority = StagingArrivalAge.Parse(Priority);
        Priority = priority?.ToString().ToLowerInvariant();
        ModelState.Remove(nameof(Priority));
        PageNumber = Math.Clamp(PageNumber, 1, 100000);
        if (ArrivalLineId is Guid lineId)
        {
            var row = await new StagingArrivalQuery(db).GetAsync(lineId, token);
            if (row is null) return false;
            Arrivals = new([row], false);
            PageNumber = 1;
        }
        else
        {
            var overview = await new StagingArrivalQuery(db).OverviewAsync(Search, priority, PageNumber, asOf, token);
            Arrivals = overview.Page; Summary = overview.Summary;
        }
        Incidents = await incidentQuery.CountsAsync(Arrivals.Items.Select(r => new IncidentLinkContext(r.LineId, r.ProductId, ArrivalLineId: r.LineId)).ToArray(), token);
        var localTimes = await clock.ConvertManyAsync(Arrivals.Items.Select(r => r.OccurredAt), token);
        foreach (var row in Arrivals.Items)
        {
            LocalTimes[row.LineId] = localTimes[row.OccurredAt];
            var age = StagingArrivalAge.Elapsed(row.OccurredAt, asOf);
            Ages[row.LineId] = age.TotalHours < 1 ? texts["Menos de 1 h"].Value
                : age.Days == 0 ? texts["Hace {0} h", age.Hours].Value
                : texts[age.Days == 1 ? "Hace {0} día y {1} h" : "Hace {0} días y {1} h", age.Days, age.Hours].Value;
            if (row.NeedsIdentification || row.Pending > 0) Priorities[row.LineId] = StagingArrivalAge.Priority(row.OccurredAt, asOf);
        }
        return true;
    }
}
