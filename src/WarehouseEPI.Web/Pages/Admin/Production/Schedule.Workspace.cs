using WarehouseEPI.Web.Production;
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
    public async Task<IActionResult> OnGetWorkspaceOpeningsAsync(Guid? weekId, DateOnly? weekStart, CancellationToken token)
    {
        if (!weekId.HasValue && weekStart.HasValue && ValidNewStart(weekStart.Value))
            return new JsonResult(await new ProductionWeekOpeningService(db).OptionsAsync(weekStart.Value, token));
        if (!weekId.HasValue) return BadRequest();
        var week = await service.GetWeekAsync(weekId.Value, token);
        if (week is null || !week.ExplicitCarryover) return BadRequest();
        return new JsonResult(await new ProductionWeekOpeningService(db).OptionsAsync(weekId.Value, token));
    }

    public async Task<IActionResult> OnGetWorkspaceInitialBalancesAsync(Guid? weekId, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (!weekId.HasValue) return new JsonResult(Array.Empty<ProductionInitialBalanceView>());
        if (await service.GetWeekAsync(weekId.Value, token) is null) return NotFound();
        return new JsonResult(await new ProductionInitialBalanceService(db).GetAsync(weekId.Value, token));
    }

    public sealed record OpeningCorrectionInput(Guid OperationId, Guid WeekId, uint ExpectedWeekVersion,
        List<ProductionOpeningChange> Changes, string Reason, string Fingerprint = "", string Pin = "");

    public async Task<IActionResult> OnPostOpeningReviewAsync([FromBody] OpeningCorrectionInput input, CancellationToken token)
    {
        if (input?.Changes is null || input.OperationId == Guid.Empty) return BadRequest();
        var result = await service.PreviewOpeningCorrectionAsync(new(input.OperationId, input.WeekId,
            input.ExpectedWeekVersion, input.Changes, Actor(), input.Reason ?? ""), token);
        return new JsonResult(result with { Errors = result.Errors.Select(item => ProductionDailyText.Message(texts, item)).ToArray() });
    }

    public async Task<IActionResult> OnPostOpeningConfirmAsync([FromBody] OpeningCorrectionInput input, CancellationToken token)
    {
        if (input?.Changes is null || input.OperationId == Guid.Empty) return BadRequest();
        var result = await service.ConfirmOpeningCorrectionAsync(new(input.OperationId, input.WeekId,
            input.ExpectedWeekVersion, input.Changes, Actor(), input.Reason ?? "", input.Fingerprint, input.Pin), token);
        return new JsonResult(result with { Errors = result.Errors?.Select(item => ProductionDailyText.Message(texts, item)).ToArray() });
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

    public async Task<IActionResult> OnGetWorkspaceCopyAsync(Guid? weekId, Guid sourceWeekId, DateOnly? weekStart, CancellationToken token)
    {
        var target = weekId.HasValue ? await service.GetWeekAsync(weekId.Value, token) : null;
        var source = await service.GetWeekAsync(sourceWeekId, token);
        var start = target?.WeekStart ?? weekStart;
        if (weekId.HasValue && target is null || source is null || !start.HasValue || !ValidNewStart(start.Value) ||
            target?.Status == ProductionScheduleWeekStatus.Closed || source.WeekStart >= start.Value) return BadRequest();
        var ids = source.Lines.Where(x => !x.IsCarryover).Select(x => x.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id) && x.IsActive)
            .Select(x => new { x.Id, Unit = x.BaseUnit.Code, AllowsDecimals = x.BaseUnit.AllowsDecimals })
            .ToDictionaryAsync(x => x.Id, token);
        var sourceBalance = source.Status != ProductionScheduleWeekStatus.Draft
            ? await new ProductionDailyBalanceService(db).GetAsync(source.Id, token) : null;
        var carryIds = sourceBalance?.Rows.Where(x => x.Date == source.WeekEnd).Select(x => x.ProductId).Distinct().ToArray() ?? [];
        var carryProducts = await db.Products.AsNoTracking().Where(x => carryIds.Contains(x.Id) && x.IsActive)
            .Select(x => new { x.Id, Unit = x.BaseUnit.Code, x.BaseUnit.AllowsDecimals }).ToDictionaryAsync(x => x.Id, token);
        var initialBalances = sourceBalance?.Rows.Where(x => x.Date == source.WeekEnd && carryProducts.ContainsKey(x.ProductId))
            .SelectMany(row => new[] { row.Cutting, row.Sewing, row.ReadyToPack }.Select(area => new {
                row.ProductId, row.Sku, carryProducts[row.ProductId].Unit, carryProducts[row.ProductId].AllowsDecimals,
                area.Area, Quantity = Math.Max(0, area.SignedPending).ToString("0.####", global::System.Globalization.CultureInfo.InvariantCulture), SourceWeekId = source.Id, SourceStart = source.WeekStart }))
            .ToArray();
        return new JsonResult(new { source.Id, source.Version, Status = source.Status.ToString(), InitialBalances = initialBalances,
            Rows = source.Lines.Where(x => !x.IsCarryover && products.ContainsKey(x.ProductId))
                .Select(x => new { x.ProductId, x.Sku, products[x.ProductId].Unit,
                    products[x.ProductId].AllowsDecimals, Day = x.PlannedDate.DayNumber - source.WeekStart.DayNumber,
                    x.Quantity, x.Id, x.OrderReference1, x.OrderReference2, x.OrderReference3, x.Notes }).ToArray() });
    }

    public async Task<IActionResult> OnGetWorkspaceOperationAsync(Guid weekId, Guid operationId, CancellationToken token)
    {
        var saved = await db.ProductionScheduleRevisions.AsNoTracking().AnyAsync(x => x.WeekId == weekId &&
            x.OperationId == operationId && (x.Action == "draft-batch-changed" || x.Action == "open-batch-changed" || x.Action == "opening-corrected"), token);
        var version = saved ? await db.ProductionScheduleWeeks.AsNoTracking().Where(x => x.Id == weekId)
            .Select(x => (uint?)x.Version).SingleOrDefaultAsync(token) : null;
        return new JsonResult(new { saved, version });
    }

    public async Task<IActionResult> OnPostWorkspaceReviewAsync([FromBody] WorkspaceSaveInput input, CancellationToken token)
    {
        if (input is null || input.Changes is null) return BadRequest();
        var result = await service.PreviewWorkspaceChangesAsync(new(input.OperationId, input.WeekId,
            input.ExpectedWeekVersion, input.Changes, Actor(), input.Openings, input.Reason ?? "", InitialBalances: input.InitialBalances, SkuTotals: input.SkuTotals), token);
        return new JsonResult(result with { Errors = result.Errors.Select(item => ProductionDailyText.Message(texts, item)).ToArray() });
    }

    public async Task<IActionResult> OnPostWorkspaceSaveAsync([FromForm] string payload, [FromForm] string? adminPin, CancellationToken token)
    {
        WorkspaceSaveInput? input;
        try { input = JsonSerializer.Deserialize<WorkspaceSaveInput>(payload, WorkspaceJson); }
        catch (JsonException) { return BadRequest(new { errors = new[] { "La preparación no tiene un formato válido." } }); }
        if (input is null || input.OperationId == Guid.Empty || input.WeekId == Guid.Empty ||
            input.Changes is null || input.Changes.Count + (input.Openings?.Count ?? 0) + (input.InitialBalances?.Count ?? 0) + (input.SkuTotals?.Count ?? 0) is < 1)
            return BadRequest(new { errors = new[] { "Agrega al menos un cambio." } });
        var result = await service.SaveWorkspaceChangesAsync(new(input.OperationId, input.WeekId,
            input.ExpectedWeekVersion, input.Changes, Actor(), input.Openings, input.Reason ?? "", input.ReviewedFingerprint ?? "", input.InitialBalances, input.SkuTotals), adminPin ?? "", token);
        if (result.Success) return new JsonResult(new { saved = true, count = input.Changes.Count + (input.SkuTotals?.Count ?? 0) + (input.Openings?.Count ?? 0) + (input.InitialBalances?.Count ?? 0), version = (await service.GetWeekAsync(input.WeekId, token))!.Version });
        var current = result.Status == ProductionDailyCommandStatus.ConcurrencyConflict
            ? await service.GetWeekAsync(input.WeekId, token) : null;
        return new JsonResult(new { saved = false, status = result.Status.ToString(),
            errors = (result.Errors ?? [result.Status == ProductionDailyCommandStatus.ConcurrencyConflict
                ? "La semana cambió. Compara los datos actuales y vuelve a revisar." : "No se guardó ningún cambio."]).Select(error => ProductionDailyText.Message(texts, error)),
            current }) { StatusCode = result.Status == ProductionDailyCommandStatus.ConcurrencyConflict ? 409 : 400 };
    }

    public sealed class WorkspaceSaveInput
    {
        public DateOnly WeekStart { get; set; }
        public Guid OperationId { get; set; }
        public Guid WeekId { get; set; }
        public uint ExpectedWeekVersion { get; set; }
        public List<ProductionScheduleDraftChange> Changes { get; set; } = [];
        public List<ProductionOpeningChange>? Openings { get; set; }
        public List<ProductionInitialBalanceChange>? InitialBalances { get; set; }
        public List<ProductionScheduleBatchLine>? SkuTotals { get; set; }
        public string? Reason { get; set; }
        public string? ReviewedFingerprint { get; set; }
    }
}
