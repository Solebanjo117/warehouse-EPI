using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed partial class IndexModel
{
    public sealed record BalanceCellInput(Guid ProductId, ProductionDailyArea Area, int Shift, string? Observed, string? Requested);
    public sealed record BalancePlanInput(Guid LineId, string? Observed, string? Requested, uint ExpectedLineVersion, uint ExpectedWeekVersion);
    public sealed record BalanceNewPlanInput(Guid OperationId, Guid ProductId, string? Requested, uint ExpectedWeekVersion);
    public sealed record BalanceEditInput(Guid OperationId, Guid WeekId, DateOnly Date, List<BalanceCellInput>? Cells,
        string? Reason, string? Fingerprint, string? Pin, List<BalancePlanInput>? PlanChanges = null, List<BalanceNewPlanInput>? NewPlans = null);

    private ProductionBalanceEditCommand? BalanceCommand(BalanceEditInput? input)
    {
        // Validate the JSON body here; unrelated capture forms share this PageModel.
        if (input is null || (input.Cells?.Count ?? 0) + (input.PlanChanges?.Count ?? 0) + (input.NewPlans?.Count ?? 0) is < 1 or > 100) return null;
        bool Parse(string? value, out decimal quantity)
        {
            quantity = 0;
            return value is not null && Regex.IsMatch(value, @"^\d{1,14}(?:\.\d{1,4})?$") &&
                decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quantity);
        }
        var cells = new List<ProductionBalanceCell>();
        foreach (var cell in input.Cells ?? [])
        {
            if (cell is null) return null;
            if (!Parse(cell.Requested, out var requested) || !Parse(cell.Observed, out var observed)) return null;
            cells.Add(new(cell.ProductId, cell.Area, cell.Shift, observed, requested));
        }
        var plans = new List<ProductionBalancePlanChange>();
        foreach (var plan in input.PlanChanges ?? [])
        {
            if (plan is null || !Parse(plan.Observed, out var observed) || !Parse(plan.Requested, out var requested)) return null;
            plans.Add(new(plan.LineId, observed, requested, plan.ExpectedLineVersion, plan.ExpectedWeekVersion));
        }
        var newPlans = new List<ProductionBalanceNewPlan>();
        foreach (var plan in input.NewPlans ?? [])
        {
            if (plan is null || !Parse(plan.Requested, out var requested)) return null;
            newPlans.Add(new(plan.OperationId, plan.ProductId, requested, plan.ExpectedWeekVersion));
        }
        return new(input.OperationId, input.WeekId, input.Date, cells, input.Reason, input.Fingerprint ?? "", input.Pin ?? "",
            plans, plans.Count + newPlans.Count > 0 ? BalanceAdminActor() : null, new(input.Date, Sku, Reference, Area), newPlans);
    }

    private Guid? BalanceAdminActor() => User.IsInRole("ADMIN") && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public async Task<IActionResult> OnGetBalancePlanLinesAsync(Guid weekId, DateOnly date, Guid productId, CancellationToken token)
    {
        if (BalanceAdminActor() is not Guid actor) return Forbid();
        return new JsonResult(await captures.GetBalancePlanLinesAsync(weekId, date, productId, actor, token));
    }

    public async Task<IActionResult> OnPostBalanceEditPreviewAsync([FromBody] BalanceEditInput? input, CancellationToken token)
    {
        var command = BalanceCommand(input);
        if (command is null) return new JsonResult(new { canConfirm = false, errors = new[] { texts["Indica una cantidad válida. Solo punto decimal."].Value } });
        if ((command.PlanChanges?.Count > 0 || command.NewPlans?.Count > 0) && command.AdminActorId is null) return Forbid();
        var preview = await captures.PreviewBalanceEditAsync(command with { Pin = "" }, token);
        string? weeklyHtml = null;
        if (preview.CanConfirm && preview.WeekClose is not null)
        {
            WeekClose = preview.WeekClose;
            Weeks = await schedules.ListWeeksAsync(token);
            WeekId = command.WeekId;
            Through = command.Date;
            PendingPage = Math.Clamp(PendingPage, 1, PendingPages);
            CompletionPage = Math.Clamp(CompletionPage, 1, CompletionPages);
            SummaryPage = Math.Clamp(SummaryPage, 1, SummaryPages);
            weeklyHtml = await RenderWeeklyPreviewAsync();
        }
        return new JsonResult(new { preview.CanConfirm, preview.RequiresAdmin, preview.RequiresReason, preview.Fingerprint,
            preview.Balance, weeklyHtml, Errors = preview.Errors.Select(BalanceError).ToArray(),
            Cells = preview.Cells.Select(x => x with { Errors = x.Errors.Select(BalanceError).ToArray() }).ToArray(),
            NewPlans = preview.NewPlans?.Select(x => x with { Errors = x.Errors.Select(BalanceError).ToArray() }).ToArray(),
            Plans = preview.Plans?.Select(x => x with { Errors = x.Errors.Select(BalanceError).ToArray() }).ToArray() });
    }

    public async Task<IActionResult> OnPostBalanceEditConfirmAsync([FromBody] BalanceEditInput? input, CancellationToken token)
    {
        var command = BalanceCommand(input);
        if (command is null) return BadRequest();
        if ((command.PlanChanges?.Count > 0 || command.NewPlans?.Count > 0) && command.AdminActorId is null) return Forbid();
        var result = await captures.ConfirmBalanceEditAsync(command, token);
        return new JsonResult(new { result.Success, Errors = result.Errors?.Select(BalanceError).ToArray(), status = result.Status.ToString() });
    }

    private async Task<string> RenderWeeklyPreviewAsync()
    {
        var engine = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Mvc.ViewEngines.ICompositeViewEngine>();
        var view = engine.GetView(null, "/Pages/Operations/Production/_WeekClose.cshtml", false);
        if (!view.Success) throw new InvalidOperationException("Weekly close view unavailable.");
        using var writer = new StringWriter(CultureInfo.CurrentCulture);
        var context = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(PageContext, view.View,
            new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<IndexModel>(ViewData, this),
            TempData, writer, new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions());
        await view.View.RenderAsync(context);
        return writer.ToString();
    }

    private string BalanceError(string error)
    {
        var localized = texts[error];
        if (!localized.ResourceNotFound) return localized.Value;
        var separator = error.IndexOf(": ", StringComparison.Ordinal);
        return separator < 0 ? error : error[..(separator + 2)] + texts[error[(separator + 2)..]].Value;
    }
}
