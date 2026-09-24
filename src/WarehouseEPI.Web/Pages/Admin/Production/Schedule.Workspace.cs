using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Admin.Production;

public sealed partial class ScheduleModel
{
    public async Task<IActionResult> OnGetWorkspaceOpeningsAsync(Guid weekId, CancellationToken token)
    {
        var week = await service.GetWeekAsync(weekId, token);
        if (week is null || !week.ExplicitCarryover) return BadRequest();
        return new JsonResult(await new ProductionWeekOpeningService(db).OptionsAsync(weekId, token));
    }

    public sealed record OpeningCorrectionInput(Guid OperationId, Guid WeekId, uint ExpectedWeekVersion,
        List<ProductionOpeningChange> Changes, string Reason, string Fingerprint = "", string Pin = "");

    public async Task<IActionResult> OnPostOpeningReviewAsync([FromBody] OpeningCorrectionInput input, CancellationToken token)
    {
        if (input?.Changes is null || input.OperationId == Guid.Empty) return BadRequest();
        return new JsonResult(await service.PreviewOpeningCorrectionAsync(new(input.OperationId, input.WeekId,
            input.ExpectedWeekVersion, input.Changes, Actor(), input.Reason ?? ""), token));
    }

    public async Task<IActionResult> OnPostOpeningConfirmAsync([FromBody] OpeningCorrectionInput input, CancellationToken token)
    {
        if (input?.Changes is null || input.OperationId == Guid.Empty) return BadRequest();
        return new JsonResult(await service.ConfirmOpeningCorrectionAsync(new(input.OperationId, input.WeekId,
            input.ExpectedWeekVersion, input.Changes, Actor(), input.Reason ?? "", input.Fingerprint, input.Pin), token));
    }

    private static readonly JsonSerializerOptions WorkspaceJson = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public async Task<IActionResult> OnGetWorkspaceProductsAsync(string? q, CancellationToken token)
    {
        var search = q?.Trim() ?? "";
        if (search.Length < 2) return new JsonResult(Array.Empty<object>());
        var products = (await productsQuery.SearchProductsAsync(search, token))
            .Select(x => new { x.Id, x.Sku, Unit = x.UnitCode,
                x.AllowsDecimals }).ToArray();
        return new JsonResult(products);
    }

    public async Task<IActionResult> OnGetWorkspaceResolveProductsAsync(string? skus, CancellationToken token)
    {
        var requested = (skus ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CatalogNormalization.NormalizeCode)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToArray();
        if (requested.Length == 0) return new JsonResult(Array.Empty<object>());
        var products = await db.Products.AsNoTracking().Where(x => x.IsActive && requested.Contains(x.Sku))
            .Select(x => new { x.Id, x.Sku, Unit = x.BaseUnit.Code,
                AllowsDecimals = x.BaseUnit.AllowsDecimals }).ToArrayAsync(token);
        return new JsonResult(products);
    }

    public async Task<IActionResult> OnGetWorkspaceCopyAsync(Guid weekId, Guid sourceWeekId, CancellationToken token)
    {
        var target = await service.GetWeekAsync(weekId, token);
        var source = await service.GetWeekAsync(sourceWeekId, token);
        if (target is null || source is null || target.Status != ProductionScheduleWeekStatus.Draft ||
            source.WeekStart >= target.WeekStart) return BadRequest();
        var ids = source.Lines.Where(x => !x.IsCarryover).Select(x => x.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id) && x.IsActive)
            .Select(x => new { x.Id, Unit = x.BaseUnit.Code, AllowsDecimals = x.BaseUnit.AllowsDecimals })
            .ToDictionaryAsync(x => x.Id, token);
        return new JsonResult(new { source.Id, source.Version,
            Rows = source.Lines.Where(x => !x.IsCarryover && products.ContainsKey(x.ProductId))
                .Select(x => new { x.ProductId, x.Sku, products[x.ProductId].Unit,
                    products[x.ProductId].AllowsDecimals, Day = x.PlannedDate.DayNumber - source.WeekStart.DayNumber,
                    x.Quantity, x.Id }).ToArray() });
    }

    public async Task<IActionResult> OnGetWorkspaceOperationAsync(Guid weekId, Guid operationId, CancellationToken token)
    {
        var saved = await db.ProductionScheduleRevisions.AsNoTracking().AnyAsync(x => x.WeekId == weekId &&
            x.OperationId == operationId && (x.Action == "draft-batch-changed" || x.Action == "opening-corrected"), token);
        return new JsonResult(new { saved });
    }

    public async Task<IActionResult> OnPostWorkspaceSaveAsync([FromForm] string payload, CancellationToken token)
    {
        WorkspaceSaveInput? input;
        try { input = JsonSerializer.Deserialize<WorkspaceSaveInput>(payload, WorkspaceJson); }
        catch (JsonException) { return BadRequest(new { errors = new[] { "La preparación no tiene un formato válido." } }); }
        if (input is null || input.OperationId == Guid.Empty || input.WeekId == Guid.Empty ||
            input.Changes is null || input.Changes.Count + (input.Openings?.Count ?? 0) is < 1 or > 100)
            return BadRequest(new { errors = new[] { "Revisa entre 1 y 100 cambios." } });
        var result = await service.SaveDraftChangesAsync(new(input.OperationId, input.WeekId,
            input.ExpectedWeekVersion, input.Changes, Actor(), input.Openings), token);
        if (result.Success) return new JsonResult(new { saved = true, count = input.Changes.Count + (input.Openings?.Count ?? 0) });
        var current = result.Status == ProductionDailyCommandStatus.ConcurrencyConflict
            ? await service.GetWeekAsync(input.WeekId, token) : null;
        return new JsonResult(new { saved = false, status = result.Status.ToString(),
            errors = result.Errors ?? [result.Status == ProductionDailyCommandStatus.ConcurrencyConflict
                ? "La semana cambió. Compara los datos actuales y vuelve a revisar." : "No se guardó ningún cambio."],
            current }) { StatusCode = result.Status == ProductionDailyCommandStatus.ConcurrencyConflict ? 409 : 400 };
    }

    public sealed class WorkspaceSaveInput
    {
        public Guid OperationId { get; set; }
        public Guid WeekId { get; set; }
        public uint ExpectedWeekVersion { get; set; }
        public List<ProductionScheduleDraftChange> Changes { get; set; } = [];
        public List<ProductionOpeningChange>? Openings { get; set; }
    }
}
