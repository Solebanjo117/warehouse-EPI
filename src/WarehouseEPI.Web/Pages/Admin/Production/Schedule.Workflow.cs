using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Pages.Operations.Production;

namespace WarehouseEPI.Web.Pages.Admin.Production;

public sealed partial class ScheduleModel
{
    [BindProperty] public CopyInput Copy { get; set; } = new();
    [BindProperty] public CarryoverInput Carryover { get; set; } = new();
    [BindProperty(SupportsGet = true)] public Guid? CarryProduct { get; set; }
    [BindProperty(SupportsGet = true)] public ProductionDailyArea? CarryArea { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? CarryPlanId { get; set; }
    public ProductionScheduleWeekView? CopySource { get; private set; }
    public IReadOnlyList<ProductionCarryoverSuggestion> Suggestions { get; private set; } = [];
    public IReadOnlyList<ProductionCarryoverPlan> CarryoverPlans { get; private set; } = [];
    public Dictionary<Guid, string> CarryoverSkus { get; private set; } = [];

    private async Task LoadWorkflowAsync(string handler, CancellationToken token)
    {
        if (Week is null) return;
        var sourceId = await db.ProductionScheduleWeeks.AsNoTracking().Where(x => x.WeekStart < Week.WeekStart)
            .OrderByDescending(x => x.WeekStart).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
        CopySource = sourceId.HasValue ? await service.GetWeekAsync(sourceId.Value, token) : null;
        if (handler != "Copy" && CopySource is not null)
            Copy = new()
            {
                WeekId = Week.Id,
                ExpectedVersion = Week.Version,
                SourceWeekId = CopySource.Id,
                SourceVersion = CopySource.Version,
                Rows = CopySource.Lines.Where(x => !x.IsCarryover).Select(x => new CopyRowInput
                { SourceLineId = x.Id, Sku = x.Sku, Date = Week.WeekStart.AddDays(x.PlannedDate.DayNumber - CopySource.WeekStart.DayNumber) }).ToList()
            };
        Suggestions = await service.GetCarryoverSuggestionsAsync(Week.Id, token);
        CarryoverPlans = await db.ProductionCarryoverPlans.AsNoTracking().Where(x => x.WeekId == Week.Id).OrderBy(x => x.PlannedDate).ToListAsync(token);
        var productIds = CarryoverPlans.Select(x => x.ProductId).Concat(Suggestions.Select(x => x.ProductId)).Distinct().ToArray();
        CarryoverSkus = await db.Products.AsNoTracking().Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Sku, token);
        if (handler != "Carryover")
        {
            var plan = CarryoverPlans.SingleOrDefault(x => x.Id == CarryPlanId);
            var suggestion = Suggestions.FirstOrDefault(x => x.ProductId == (plan?.ProductId ?? CarryProduct) && x.Area == (plan?.Area ?? CarryArea));
            Carryover = new()
            {
                WeekId = Week.Id,
                WeekVersion = Week.Version,
                Id = plan?.Id,
                Version = plan?.Version ?? 0,
                ProductId = plan?.ProductId ?? suggestion?.ProductId ?? Guid.Empty,
                Area = plan?.Area ?? suggestion?.Area ?? ProductionDailyArea.Cutting,
                Date = plan?.PlannedDate ?? Week.WeekStart,
                Quantity = plan?.Quantity.ToString("0.####", global::System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ExpectedAvailable = suggestion?.Available ?? 0
            };
        }
    }

    public async Task<IActionResult> OnPostCopyAsync(CancellationToken token)
    {
        WeekId = Copy.WeekId;
        var selected = Copy.Rows.Select((row, index) => (row, index)).Where(x => x.row.Selected).ToArray();
        var rows = new List<ProductionCopyRow>();
        ProductionCapture.ValidateOnly(this, nameof(Copy));
        foreach (var (row, index) in selected)
        {
            if (!ProductionQuantityBinder.TryParse(row.Quantity ?? "", out var quantity) || quantity <= 0)
                ModelState.AddModelError($"Copy.Rows[{index}].Quantity", texts[ProductionQuantityBinder.Error]);
            else rows.Add(new(row.SourceLineId, row.Date, quantity));
        }
        if (!ModelState.IsValid) { Copy.Pin = ""; ProductionCapture.ClearPins(this); await LoadAsync(token); return Page(); }
        var result = await service.CopyWeekAsync(new(Copy.OperationId, Copy.WeekId, Copy.ExpectedVersion, Copy.SourceWeekId,
            Copy.SourceVersion, rows, Actor(), Copy.Pin), token);
        Copy.Pin = ""; ProductionCapture.ClearPins(this);
        return await FinishAsync(result, "Productos copiados al programa.", Copy.WeekId, token);
    }

    public async Task<IActionResult> OnPostCarryoverAsync(CancellationToken token)
    {
        WeekId = Carryover.WeekId;
        ProductionCapture.ValidateOnly(this, nameof(Carryover));
        if (!ProductionQuantityBinder.TryParse(Carryover.Quantity ?? "", out var quantity))
            ModelState.AddModelError("Carryover.Quantity", texts[ProductionQuantityBinder.Error]);
        if (!ModelState.IsValid) { await LoadAsync(token); return Page(); }
        var result = await service.SaveCarryoverPlanAsync(new(Carryover.OperationId, Carryover.WeekId, Carryover.WeekVersion,
            Carryover.Id, Carryover.Version, Carryover.Date, Carryover.ProductId, Carryover.Area, quantity,
            Carryover.ExpectedAvailable, Actor()), token);
        if (!result.Success)
        {
            Carryover.ExpectedAvailable = (await service.GetCarryoverSuggestionsAsync(Carryover.WeekId, token))
                .FirstOrDefault(x => x.ProductId == Carryover.ProductId && x.Area == Carryover.Area)?.Available ?? 0;
            ModelState.Remove("Carryover.ExpectedAvailable");
        }
        return await FinishAsync(result, "Arrastre programado guardado; el pendiente real no cambia.", Carryover.WeekId, token);
    }

    public sealed class CopyInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        public Guid WeekId { get; set; }
        public uint ExpectedVersion { get; set; }
        public Guid SourceWeekId { get; set; }
        public uint SourceVersion { get; set; }
        public List<CopyRowInput> Rows { get; set; } = [];
        public string Pin { get; set; } = "";
    }
    public sealed class CopyRowInput
    {
        public Guid SourceLineId { get; set; }
        public string? Sku { get; set; }
        public bool Selected { get; set; }
        public DateOnly Date { get; set; }
        public string? Quantity { get; set; }
    }
    public sealed class CarryoverInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        public Guid WeekId { get; set; }
        public uint WeekVersion { get; set; }
        public Guid? Id { get; set; }
        public uint Version { get; set; }
        public Guid ProductId { get; set; }
        public ProductionDailyArea Area { get; set; }
        public DateOnly Date { get; set; }
        public string? Quantity { get; set; }
        public decimal ExpectedAvailable { get; set; }
    }
}
