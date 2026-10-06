using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Web.Production;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed partial class IndexModel
{
    [BindProperty(SupportsGet = true)] public int OverviewShift { get; set; }
    [BindProperty(SupportsGet = true)] public int AttentionPage { get; set; } = 1;
    public ProductionDayOverview? Overview { get; private set; }
    public int AttentionPages => Math.Max(1, (int)Math.Ceiling((Overview?.Attention.Count ?? 0) / 25d));
    public IEnumerable<ProductionOverviewAttention> AttentionRows => Overview?.Attention.Skip((AttentionPage - 1) * 25).Take(25) ?? [];

    private async Task LoadOverviewAsync(CancellationToken token)
    {
        Day ??= Through ?? Today;
        if (OverviewShift is < 0 or > 2 || !ModelState.IsValid)
        {
            ModelState.AddModelError(string.Empty, texts["Revisa la fecha y el turno seleccionados."]);
            return;
        }
        var setup = await ProductionDailySetup.LoadAsync(schedules, db, token);
        ConfigurationReady = setup.IsReady;
        BalanceShift1Name = setup.Shifts.FirstOrDefault(x => x.Id == setup.Configuration.Shift1Id)?.Name ?? "—";
        BalanceShift2Name = setup.Shifts.FirstOrDefault(x => x.Id == setup.Configuration.Shift2Id)?.Name ?? "—";
        ShiftId = OverviewShift == 1 ? setup.Configuration.Shift1Id : OverviewShift == 2 ? setup.Configuration.Shift2Id : null;
        // Date governs this view; a stale week from another tab must not clamp the day.
        WeekId = Weeks.FirstOrDefault(x => x.WeekStart <= Day && x.WeekEnd >= Day)?.Id;
        Through = Day;
        if (WeekId is not Guid weekId) return;
        Daily = await balances.GetDailySummaryAsync(weekId, new(Day.Value), token);
        if (Daily is null) return;
        var ids = Daily.Products.Select(x => x.ProductId).ToArray();
        var metadata = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new ProductionOverviewProduct(x.Id, x.Description, x.BaseUnitId, x.BaseUnit.Code))
            .ToDictionaryAsync(x => x.Id, token);
        Overview = ProductionDayOverview.Create(Daily, metadata, OverviewShift);
        AttentionPage = Math.Clamp(AttentionPage, 1, AttentionPages);
    }
}
