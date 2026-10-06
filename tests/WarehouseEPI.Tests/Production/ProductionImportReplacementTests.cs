using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionImportReplacementTests
{
    private static byte[] Workbook(decimal quantity = 40) => ProductionOpeningImportTests.Bytes(w =>
    {
        foreach (var sheet in w.Worksheets.ToArray())
            if (sheet.Name != "09-21 to 09-27") w.Worksheets.Delete(sheet.Name);
        var target = w.Worksheet("09-21 to 09-27");
        target.Table("Plan4").DataRange.Clear(XLClearOptions.Contents);
        target.Cell(22, 3).Value = "Monday";
        target.Cell(22, 4).Value = "FG-100";
        target.Cell(22, 5).Value = quantity;
    });

    private static readonly ProductionScheduleImportResolutions Replace = ProductionScheduleImportResolutions.None with { ReplaceProgramming = true };

    [Fact]
    public async Task Replacement_ignores_invalid_execution_and_carryover_rows_and_rejects_non_admin()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await ProductionOpeningImportTests.Seed(db);
        var bytes = ProductionOpeningImportTests.Bytes(w =>
        {
            var sheet = w.Worksheet("09-21 to 09-27");
            sheet.Table("Plan4").Resize(sheet.Range(21, 3, 33, 10));
            sheet.Cell(21, 10).Value = "Tipo"; sheet.Cell(22, 10).Value = "Arrastre";
            sheet.Cell(22, 4).Value = "UNKNOWN-IGNORED";
            var prior = ProductionOpeningImportTests.Prior(w);
            prior.Cell(24, 15).Value = "UNKNOWN-EXECUTION";
        });
        var importer = new ProductionScheduleImportService(db, TimeProvider.System);
        var preview = await importer.PreviewAsync(new MemoryStream(bytes), "file.xlsx", Replace);
        Assert.True(preview.CanConfirm, string.Join(";", preview.Issues));
        Assert.Equal(0, preview.CaptureCount);
        Assert.DoesNotContain(preview.Weeks.SelectMany(x => x.FinalLines), x => x.IsCarryover);
        Assert.Equal(ProductionDailyCommandStatus.ValidationFailed, (await importer.ConfirmAsync(preview, Guid.NewGuid(), Guid.NewGuid())).Status);
        Assert.Empty(await db.ProductionScheduleWeeks.ToListAsync());
    }

    [Fact]
    public async Task Replacement_preserves_captures_openings_extras_history_and_other_weeks_and_is_idempotent()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var actor = await ProductionOpeningImportTests.Seed(db);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var week = new ProductionScheduleWeek
        {
            WeekStart = new(2026, 9, 21),
            WeekEnd = new(2026, 9, 27),
            RequestFingerprint = "before",
            Status = ProductionScheduleWeekStatus.Closed
        };
        var old = new ProductionScheduleLine { WeekId = week.Id, Sequence = 1, ProductId = product.Id, Quantity = 100, PlannedDate = week.WeekStart };
        var carry = new ProductionScheduleLine { WeekId = week.Id, Sequence = 2, ProductId = product.Id, Quantity = 5, PlannedDate = week.WeekStart, IsCarryover = true };
        var extra = new ProductionScheduleLine { WeekId = week.Id, Sequence = 3, ProductId = product.Id, Quantity = 2, PlannedDate = week.WeekStart, IsExtra = true };
        var other = new ProductionScheduleWeek { WeekStart = new(2026, 9, 28), RequestFingerprint = "other" };
        var capture = new ProductionDailyCapture
        {
            WeekId = week.Id,
            ProductId = product.Id,
            Quantity = 8,
            EffectiveDate = week.WeekStart,
            Area = ProductionDailyArea.Cutting,
            ShiftId = config.Shift1Id!.Value,
            StageId = config.CuttingStageId!.Value,
            RequestFingerprint = "capture",
            ResponsibleUserId = actor
        };
        var reversed = new ProductionDailyCapture
        {
            WeekId = week.Id,
            ProductId = product.Id,
            Quantity = 3,
            RequestFingerprint = "reversed",
            Status = ProductionDailyCaptureStatus.Reversed
        };
        db.AddRange(week, old, carry, extra, other, capture, reversed);
        await db.SaveChangesAsync();
        var captureBefore = JsonSerializer.Serialize(new { capture.Quantity, capture.Status, capture.OperationId, capture.RecordedAt, capture.WeekId });
        var service = ProductionOpeningImportTests.Service(db);
        var id = await service.CreateAsync("replacement.xlsx", Workbook(), actor);
        var draft = (await service.GetAsync(id, actor))!;
        Assert.False(draft.Preview.CanConfirm);
        Assert.True((await service.ReviseAsync(id, draft.Version, actor, Replace)).Success);
        draft = (await service.GetAsync(id, actor))!;
        Assert.True(draft.IsCurrent);
        Assert.True(draft.Preview.CanConfirm, string.Join(";", draft.Preview.Issues));
        var impact = Assert.Single(draft.Preview.ReplacementImpact);
        Assert.Equal(1, impact.ReplacedLines); Assert.Equal(2, impact.PreservedCaptures);
        Assert.Equal(0, draft.Preview.CaptureCount);
        var command = new ProductionImportConfirmCommand(id, draft.Version, draft.ReviewedFingerprint, Guid.NewGuid(), actor);
        Assert.True((await service.ConfirmAsync(command)).Success);
        Assert.True((await service.ConfirmAsync(command)).Success);
        Assert.True(old.IsCancelled); Assert.False(carry.IsCancelled); Assert.False(extra.IsCancelled);
        Assert.Equal(100, old.Quantity);
        Assert.Equal(40, (await db.ProductionScheduleLines.SingleAsync(x => x.WeekId == week.Id && !x.IsCancelled && !x.IsCarryover && !x.IsExtra)).Quantity);
        Assert.Equal(2, await db.ProductionDailyCaptures.CountAsync());
        Assert.Equal(captureBefore, JsonSerializer.Serialize(new { capture.Quantity, capture.Status, capture.OperationId, capture.RecordedAt, capture.WeekId }));
        Assert.Equal(ProductionDailyCaptureStatus.Reversed, reversed.Status);
        Assert.Equal(ProductionScheduleWeekStatus.Closed, week.Status);
        Assert.Equal("other", other.RequestFingerprint); Assert.Equal(0u, other.Version);
        var audit = await db.ProductionScheduleRevisions.SingleAsync();
        Assert.Contains(old.Id.ToString(), audit.BeforeJson, StringComparison.Ordinal);
        Assert.Equal("programming-replaced", audit.Action);
        Assert.Empty(await db.ProductionScheduleImportBatches.ToListAsync());
        var summary = await new ProductionDailyBalanceService(db).GetDailySummaryAsync(week.Id, new(week.WeekStart));
        var row = Assert.Single(summary!.Products);
        Assert.Equal(40, row.Planned);
        Assert.Equal(8, row.Cutting.CompletedShift1);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("capture")]
    [InlineData("status")]
    public async Task Changed_dependencies_require_review_before_any_write(string change)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var actor = await ProductionOpeningImportTests.Seed(db);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var week = new ProductionScheduleWeek { WeekStart = new(2026, 9, 21), RequestFingerprint = "before" };
        var line = new ProductionScheduleLine { WeekId = week.Id, ProductId = product.Id, Quantity = 10 };
        db.AddRange(week, line); await db.SaveChangesAsync();
        var importer = new ProductionScheduleImportService(db, TimeProvider.System);
        var preview = await importer.PreviewAsync(new MemoryStream(Workbook()), "file.xlsx", Replace);
        Assert.True(preview.CanConfirm);
        if (change == "line") line.Quantity = 20;
        if (change == "status") week.Status = ProductionScheduleWeekStatus.Open;
        if (change == "capture") db.Add(new ProductionDailyCapture { WeekId = week.Id, ProductId = product.Id, Quantity = 2, RequestFingerprint = "new" });
        await db.SaveChangesAsync();
        var result = await importer.ConfirmAsync(preview, Guid.NewGuid(), actor);
        Assert.Equal(ProductionDailyCommandStatus.ConcurrencyConflict, result.Status);
        Assert.False(line.IsCancelled); Assert.Empty(await db.ProductionScheduleRevisions.ToListAsync());
    }

    [Theory]
    [InlineData("published")]
    [InlineData("order")]
    [InlineData("carryover")]
    public async Task Published_weeks_and_compatible_carryovers_are_allowed_but_missing_orders_are_reported(string kind)
    {
        await using var db = ProductionOpeningImportTests.Context(); await ProductionOpeningImportTests.Seed(db);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var week = new ProductionScheduleWeek
        {
            WeekStart = new(2026, 9, 21),
            RequestFingerprint = "before",
            Status = kind == "published" ? ProductionScheduleWeekStatus.Open : ProductionScheduleWeekStatus.Draft
        };
        var line = new ProductionScheduleLine
        {
            WeekId = week.Id,
            ProductId = product.Id,
            Quantity = 10,
            WorkOrderId = kind == "order" ? Guid.NewGuid() : null
        };
        db.AddRange(week, line);
        if (kind == "carryover") db.Add(new ProductionWeekOpening { SourceWeekId = week.Id, SourceLineId = line.Id, Quantity = 5 });
        await db.SaveChangesAsync();
        var preview = await new ProductionScheduleImportService(db, TimeProvider.System).PreviewAsync(new MemoryStream(Workbook()), "file.xlsx", Replace);
        Assert.Equal(kind != "order", preview.CanConfirm);
        if (kind == "order") Assert.Contains(preview.Issues, x => x.Message.Contains("orden", StringComparison.Ordinal));
        Assert.False(line.IsCancelled);
    }

    [Fact]
    public async Task New_week_is_draft_without_importing_execution_and_repeated_file_can_be_reviewed_again()
    {
        await using var db = ProductionOpeningImportTests.Context(); var actor = await ProductionOpeningImportTests.Seed(db);
        var service = ProductionOpeningImportTests.Service(db);
        var bytes = Workbook();
        for (var i = 0; i < 2; i++)
        {
            var id = await service.CreateAsync("file.xlsx", bytes, actor);
            var draft = (await service.GetAsync(id, actor))!;
            await service.ReviseAsync(id, draft.Version, actor, Replace);
            draft = (await service.GetAsync(id, actor))!;
            Assert.True(draft.Preview.CanConfirm);
            Assert.True((await service.ConfirmAsync(new(id, draft.Version, draft.ReviewedFingerprint, Guid.NewGuid(), actor))).Success);
        }
        Assert.Equal(ProductionScheduleWeekStatus.Draft, (await db.ProductionScheduleWeeks.SingleAsync()).Status);
        Assert.Single(await db.ProductionScheduleLines.Where(x => !x.IsCancelled).ToListAsync());
        Assert.Equal(2, await db.ProductionScheduleRevisions.CountAsync());
        Assert.Empty(await db.ProductionDailyCaptures.ToListAsync());
    }
}
