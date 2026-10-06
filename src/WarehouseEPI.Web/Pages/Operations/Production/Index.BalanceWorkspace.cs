using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Production;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed partial class IndexModel
{
    public string BalanceShift1Name { get; private set; } = "—";
    public string BalanceShift2Name { get; private set; } = "—";
    public Dictionary<Guid, string> BalanceUnits { get; private set; } = [];
    public sealed record BalanceRestoreInput(BalanceEditInput Edit, List<Guid> Products);

    private async Task LoadBalanceMetadataAsync(CancellationToken token)
    {
        var ids = Daily!.Products.Select(x => x.ProductId).ToArray();
        BalanceUnits = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.BaseUnit.Code }).ToDictionaryAsync(x => x.Id, x => x.Code, token);
        ActiveBalanceProducts = (await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id) && x.IsActive)
            .Select(x => x.Id).ToArrayAsync(token)).ToHashSet();
    }

    public async Task<IActionResult> OnGetBalanceProductsAsync(DateOnly date, string? text, int? group, int offset, CancellationToken token) =>
        new JsonResult(await captures.SearchMatrixProductsAsync(date, text, group, offset, token));

    public async Task<IActionResult> OnGetBalanceRowsAsync(Guid weekId, DateOnly date, Guid productId, CancellationToken token)
    {
        if (!await db.Products.AnyAsync(x => x.Id == productId && x.IsActive, token)) return NotFound();
        return await BalanceRowsResponseAsync(weekId, date, [productId], token);
    }

    private async Task<IActionResult> BalanceRowsResponseAsync(Guid weekId, DateOnly date, Guid[] ids, CancellationToken token)
    {
        if (!await LoadBalanceWorkspaceAsync(weekId, date, ids, token)) return BadRequest();
        return new JsonResult(new { html = await RenderBalanceRowsAsync(), editable = ConfigurationReady && Daily!.Status == ProductionScheduleWeekStatus.Open });
    }

    private async Task<bool> LoadBalanceWorkspaceAsync(Guid weekId, DateOnly date, Guid[] ids, CancellationToken token)
    {
        Weeks = await schedules.ListWeeksAsync(token);
        var week = Weeks.SingleOrDefault(x => x.Id == weekId);
        if (week is null || date < week.WeekStart || date > week.WeekEnd) return false;
        Daily = await balances.GetEditableSummaryAsync(weekId, date, ids, token);
        if (Daily is null) return false;
        Daily = Daily with { Products = Daily.Products.Where(x => ids.Contains(x.ProductId)).ToArray() };
        var setup = await ProductionDailySetup.LoadAsync(schedules, db, token);
        ConfigurationReady = setup.IsReady;
        BalanceShift1Name = setup.Shifts.FirstOrDefault(x => x.Id == setup.Configuration.Shift1Id)?.Name ?? "—";
        BalanceShift2Name = setup.Shifts.FirstOrDefault(x => x.Id == setup.Configuration.Shift2Id)?.Name ?? "—";
        await LoadBalanceMetadataAsync(token);
        if (BalanceAdminActor() is Guid actor) BalancePlanLines = await captures.GetBalancePlanLinesAsync(weekId, date, null, actor, token);
        return true;
    }

    public async Task<IActionResult> OnPostBalanceEditRestoreAsync([FromBody] BalanceRestoreInput? input, CancellationToken token)
    {
        if (input?.Edit is not { } edit || input.Products is null || input.Products.Count > 1000 ||
            input.Products.Any(x => x == Guid.Empty) || input.Products.Distinct().Count() != input.Products.Count ||
            edit.OperationId == Guid.Empty || (edit.Reason?.Length ?? 0) > 500 ||
            (edit.Cells?.Count ?? 0) + (edit.PlanChanges?.Count ?? 0) + (edit.NewPlans?.Count ?? 0) > 1000 ||
            (edit.Cells ?? []).Any(x => x is null || !Enum.IsDefined(x.Area) || x.Shift is < 1 or > 2 ||
                !input.Products.Contains(x.ProductId) || (x.Requested?.Length ?? 0) > 100 || (x.Observed?.Length ?? 0) > 100) ||
            (edit.Cells ?? []).Select(x => (x.ProductId, x.Area, x.Shift)).Distinct().Count() != (edit.Cells?.Count ?? 0) ||
            (edit.PlanChanges ?? []).Any(x => x is null || x.LineId == Guid.Empty || (x.Requested?.Length ?? 0) > 100 || (x.Observed?.Length ?? 0) > 100) ||
            (edit.NewPlans ?? []).Any(x => x is null || x.OperationId == Guid.Empty || !input.Products.Contains(x.ProductId) || (x.Requested?.Length ?? 0) > 100) ||
            (edit.NewPlans ?? []).Select(x => x.ProductId).Distinct().Count() != (edit.NewPlans?.Count ?? 0) ||
            (edit.PlanChanges ?? []).Select(x => x.LineId).Distinct().Count() != (edit.PlanChanges?.Count ?? 0) ||
            (edit.NewPlans ?? []).Select(x => x.OperationId).Distinct().Count() != (edit.NewPlans?.Count ?? 0)) return BadRequest();

        // Check a lost confirmation response before current week/product state: it may have closed since then.
        var command = BalanceCommand(edit);
        var exists = await db.Set<ProductionBalanceEdit>().AsNoTracking().AnyAsync(x => x.OperationId == edit.OperationId, token);
        if (exists)
        {
            var prior = command is null ? null : await captures.FindBalanceEditAsync(command, token);
            return new JsonResult(new
            {
                registered = prior?.Success == true,
                conflict = prior?.Success != true,
                operationId = edit.OperationId,
                recordId = prior?.Id
            });
        }
        if (!await LoadBalanceWorkspaceAsync(edit.WeekId, edit.Date, input.Products.ToArray(), token)) return BadRequest();
        var rows = Daily!.Products.ToDictionary(x => x.ProductId);
        var cells = (edit.Cells ?? []).Select(cell =>
        {
            rows.TryGetValue(cell.ProductId, out var row);
            var area = cell.Area switch { ProductionDailyArea.Cutting => row?.Cutting, ProductionDailyArea.Sewing => row?.Sewing, _ => row?.ReadyToPack };
            var current = cell.Shift == 1 ? area?.CompletedShift1 : area?.CompletedShift2;
            var blocked = area?.Applies != true || !ActiveBalanceProducts.Contains(cell.ProductId);
            return new { cell, current = current?.ToString("0.####", CultureInfo.InvariantCulture), blocked,
                conflict = !decimal.TryParse(cell.Observed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var observed) || observed != current };
        }).ToArray();
        return new JsonResult(new
        {
            registered = false,
            conflict = false,
            html = await RenderBalanceRowsAsync(),
            cells,
            operationId = HasBalancePlanning(edit) ? Guid.NewGuid() : edit.OperationId,
            blocked = !ConfigurationReady || Daily.Status != ProductionScheduleWeekStatus.Open,
            unavailable = input.Products.Where(x => !ActiveBalanceProducts.Contains(x)).ToArray()
        });
    }

    private async Task<string> RenderBalanceRowsAsync()
    {
        var engine = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Mvc.ViewEngines.ICompositeViewEngine>();
        var view = engine.GetView(null, "/Pages/Operations/Production/_BalanceRows.cshtml", false);
        if (!view.Success) throw new InvalidOperationException("Balance rows view unavailable.");
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var context = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(PageContext, view.View,
            new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<IndexModel>(ViewData, this),
            TempData, writer, new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions());
        await view.View.RenderAsync(context);
        return writer.ToString();
    }
}
