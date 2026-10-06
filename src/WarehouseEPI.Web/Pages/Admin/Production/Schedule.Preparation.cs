using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Pages.Operations.Production;
using WarehouseEPI.Web.Production;

namespace WarehouseEPI.Web.Pages.Admin.Production;

public sealed partial class ScheduleModel
{
    public bool IsPreparingNew => ActionPanel == "new" || Weeks.Count == 0;
    public sealed record WorkspaceContext(Guid? Id, DateOnly WeekStart, DateOnly WeekEnd,
        ProductionScheduleWeekStatus Status, uint Version, IReadOnlyList<ProductionScheduleLineView> Lines, bool ExplicitCarryover);
    public WorkspaceContext? Workspace => IsPreparingNew
        ? new(null, NewWeek.WeekStart, NewWeek.WeekStart <= DateOnly.MaxValue.AddDays(-6) ? NewWeek.WeekStart.AddDays(6) : NewWeek.WeekStart,
            ProductionScheduleWeekStatus.Draft, 0, [], true)
        : Week is { } week ? new(week.Id, week.WeekStart, week.WeekEnd, week.Status, week.Version, week.Lines, week.ExplicitCarryover) : null;
    public sealed record PreparedCopySource(Guid Id, DateOnly WeekStart, DateOnly WeekEnd);
    public IReadOnlyList<PreparedCopySource> NewWeekCopySources { get; private set; } = [];
    private static bool ValidNewStart(DateOnly start) => start != DateOnly.MinValue && start.DayOfWeek == DayOfWeek.Monday && start <= DateOnly.MaxValue.AddDays(-6);
    private Task<List<PreparedCopySource>> CopySourcesAsync(DateOnly start, CancellationToken token) => db.ProductionScheduleWeeks.AsNoTracking()
        .Where(x => x.WeekStart < start).OrderByDescending(x => x.WeekStart)
        .Select(x => new PreparedCopySource(x.Id, x.WeekStart, x.WeekEnd)).ToListAsync(token);

    public async Task<IActionResult> OnGetNewWeekContextAsync(DateOnly weekStart, CancellationToken token)
    {
        if (!ValidNewStart(weekStart)) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        var existing = await db.ProductionScheduleWeeks.AsNoTracking().Where(x => x.WeekStart == weekStart).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        var setup = await ProductionDailySetup.LoadAsync(service, db, token);
        return new JsonResult(new
        {
            weekStart = IsoDay(weekStart),
            weekEnd = IsoDay(weekStart.AddDays(6)),
            configurationReady = setup.IsReady,
            sources = await CopySourcesAsync(weekStart, token),
            viewUrl = existing.HasValue ? Url.Page("Schedule", new { WeekId = existing.Value, View = "program" }) : null
        });
    }

    public async Task<IActionResult> OnGetNewWeekOperationAsync(Guid operationId, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var actor = Actor();
        var week = await db.ProductionScheduleWeeks.AsNoTracking().Where(x => x.OperationId == operationId && x.CreatedByUserId == actor)
            .Select(x => new { x.Id }).SingleOrDefaultAsync(token);
        return new JsonResult(new { saved = week is not null, url = week is null ? null : Url.Page("Schedule", new { WeekId = week.Id, View = "program" }) });
    }

    public async Task<IActionResult> OnPostNewWeekReviewAsync([FromBody] WorkspaceSaveInput input, CancellationToken token)
    {
        if (!NewPreparationValid(input)) return BadRequest();
        var result = await service.PreviewNewWeekAsync(PreparedCommand(input, input.WeekStart, input.OperationId), token);
        return new JsonResult(result with { Errors = result.Errors.Select(x => ProductionDailyText.Message(texts, x)).ToArray() });
    }

    private static bool NewPreparationValid(WorkspaceSaveInput? input) => input is { Changes: not null } &&
        input.Changes.All(x => x is { Kind: "add", Line: not null, LineId: null, ExpectedLineVersion: null });
    private CreatePreparedProductionScheduleWeekCommand PreparedCommand(WorkspaceSaveInput input, DateOnly start, Guid operationId) =>
        new(operationId, start, input.Changes.Select(x => x.Line!).ToArray(), Actor(), input.Openings, input.ReviewedFingerprint ?? "", input.InitialBalances);

    private async Task<IActionResult> CreatePreparedAsync(string payload, CancellationToken token)
    {
        WorkspaceSaveInput? input;
        try { input = JsonSerializer.Deserialize<WorkspaceSaveInput>(payload, WorkspaceJson); }
        catch (JsonException) { input = null; }
        if (!NewPreparationValid(input) || input!.WeekStart != NewWeek.WeekStart || input.OperationId != NewWeek.OperationId)
            return BadRequest(new { saved = false, errors = new[] { texts["La preparación no tiene un formato válido."].Value } });
        var valid = ProductionCapture.ValidateOnly(this, nameof(NewWeek));
        var pin = NewWeek.Pin;
        NewWeek.Pin = "";
        ProductionCapture.ClearPins(this);
        if (!valid) return BadRequest(new { saved = false, errors = new[] { texts["Usa un NIP de 4 a 8 dígitos."].Value } });
        var result = await service.CreatePreparedWeekAsync(PreparedCommand(input, NewWeek.WeekStart, NewWeek.OperationId), pin, token);
        if (result.Success) return new JsonResult(new
        {
            saved = true,
            count = input.Changes.Count + (input.Openings?.Count ?? 0) + (input.InitialBalances?.Count ?? 0),
            url = Url.Page("Schedule", new { WeekId = result.Id, View = "program" })
        });
        var existing = await db.ProductionScheduleWeeks.AsNoTracking().Where(x => x.WeekStart == NewWeek.WeekStart).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        return new JsonResult(new
        {
            saved = false,
            status = result.Status.ToString(),
            errors = (result.Errors ?? ["No se guardó ningún cambio."]).Select(x => ProductionDailyText.Message(texts, x)),
            viewUrl = existing.HasValue ? Url.Page("Schedule", new { WeekId = existing.Value, View = "program" }) : null
        })
        { StatusCode = existing.HasValue || result.Status is ProductionDailyCommandStatus.ConcurrencyConflict or ProductionDailyCommandStatus.IdempotencyConflict ? 409 : 400 };
    }
}
