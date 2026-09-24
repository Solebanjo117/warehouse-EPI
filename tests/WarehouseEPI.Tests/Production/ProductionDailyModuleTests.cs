using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionDailyModuleTests
{
    private const string PinKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Imported_tuesday_balance_carries_monday_plan_and_subtracts_both_shifts()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await SeedImportCatalogAsync(db);
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var actor = new User { FullName = "Imported", RoleId = 1, PinLookup = Guid.NewGuid().ToString("N"), PinHash = "x" };
        var monday = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek { WeekStart = monday, WeekEnd = monday.AddDays(6),
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('A', 64), CreatedByUser = actor,
            Origin = ProductionScheduleOrigin.ExcelImport };
        week.Lines.Add(new() { Product = product, Sequence = 1, PlannedDate = monday, Quantity = 300,
            Origin = ProductionScheduleOrigin.ExcelImport });
        week.Lines.Add(new() { Product = product, Sequence = 2, PlannedDate = monday.AddDays(1), Quantity = 200,
            Origin = ProductionScheduleOrigin.ExcelImport });
        foreach (var (day, shift, quantity) in new[]
                 { (monday, config.Shift1Id!.Value, 66m), (monday.AddDays(1), config.Shift1Id.Value, 30m),
                   (monday.AddDays(1), config.Shift2Id!.Value, 201m) })
            week.Captures.Add(new() { EffectiveDate = day, Product = product, Area = ProductionDailyArea.Cutting,
                StageId = config.CuttingStageId!.Value, ShiftId = shift, Quantity = quantity, ResponsibleUser = actor,
                OperationId = Guid.NewGuid(), RequestFingerprint = new string('C', 64),
                Origin = ProductionScheduleOrigin.ExcelImport });
        db.Add(week);
        await db.SaveChangesAsync();
        var tuesday = Assert.Single((await new ProductionDailyBalanceService(db)
            .GetDailySummaryAsync(week.Id, new(monday.AddDays(1))))!.Products);
        Assert.Equal(200, tuesday.Planned);
        Assert.Equal(234, tuesday.Cutting.Opening);
        Assert.Equal(30, tuesday.Cutting.CompletedShift1);
        Assert.Equal(404, tuesday.Cutting.PendingAfterShift1);
        Assert.Equal(201, tuesday.Cutting.CompletedShift2);
        Assert.Equal(203, tuesday.Cutting.NetPending);
    }

    [Fact]
    public async Task Balance_clamps_pending_and_reports_advance_with_skipped_process_as_not_applicable()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = "x", PinHash = "x" };
        var product = new Product { Sku = "FG-100", BaseUnitId = 1 };
        var cutting = new ProductionStage { Code = "CUT", Name = "Cutting" };
        var sewing = new ProductionStage { Code = "SEW", Name = "Sewing" };
        var ready = new ProductionStage { Code = "RTP", Name = "Ready to Pack" };
        var t1 = new ProductionShift { Code = "T1", Name = "Shift 1" };
        var t2 = new ProductionShift { Code = "T2", Name = "Shift 2" };
        db.AddRange(admin, product, cutting, sewing, ready, t1, t2);
        await db.SaveChangesAsync();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        config.CuttingStageId = cutting.Id; config.SewingStageId = sewing.Id; config.ReadyToPackStageId = ready.Id;
        config.Shift1Id = t1.Id; config.Shift2Id = t2.Id;
        db.ProductionRoutes.Add(new ProductionRoute { ProductId = product.Id, Name = "Ruta sin costura",
            Stages = { new ProductionRouteStage { StageId = cutting.Id, Sequence = 1 }, new ProductionRouteStage { StageId = ready.Id, Sequence = 2 } } });
        var week = new ProductionScheduleWeek { OperationId = Guid.NewGuid(), RequestFingerprint = "A".PadLeft(64, 'A'),
            WeekStart = new(2026, 9, 21), WeekEnd = new(2026, 9, 27), Status = ProductionScheduleWeekStatus.Open,
            CreatedByUserId = admin.Id, CreatedAt = DateTimeOffset.UtcNow };
        week.Lines.Add(new ProductionScheduleLine { Sequence = 1, PlannedDate = week.WeekStart, ProductId = product.Id,
            Quantity = 100, OrderReference1 = "=unsafe" });
        week.Captures.Add(Capture(week, product, cutting, t1, admin, ProductionDailyArea.Cutting, 120));
        week.Captures.Add(Capture(week, product, ready, t1, admin, ProductionDailyArea.ReadyToPack, 80));
        db.Add(week);
        await db.SaveChangesAsync();

        var view = await new ProductionDailyBalanceService(db).GetAsync(week.Id);
        var monday = Assert.Single(view!.Rows, x => x.Date == week.WeekStart);
        Assert.Equal(0, monday.Cutting.Pending);
        Assert.Equal(20, monday.Cutting.Advance);
        Assert.False(monday.Sewing.Applies);
        Assert.Equal(40, monday.ReadyToPack.Pending);
        Assert.Equal(80, monday.ProgressPercent);
        var exported = await new ProductionDailyExportService(db, new ProductionDailyBalanceService(db)).ExportAsync(week.Id);
        using var exportedWorkbook = new XLWorkbook(new MemoryStream(exported!));
        var exportedSheet = exportedWorkbook.Worksheet(1);
        Assert.Equal(XLDataType.Number, exportedSheet.Cell(4, 3).DataType);
        Assert.Equal("=unsafe", exportedSheet.Cell(4, 4).GetString());
        Assert.Equal(XLDataType.Text, exportedSheet.Cell(4, 4).DataType);
        Assert.False(exportedSheet.Cell(4, 4).HasFormula);
    }

    [Fact]
    public async Task Import_reads_shifted_table_variants_and_marks_current_week_as_draft()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await SeedImportCatalogAsync(db);
        using var workbook = BuildWorkbook("FG-100");
        using var stream = new MemoryStream();
        workbook.SaveAs(stream); stream.Position = 0;

        var preview = await new ProductionScheduleImportService(db, TimeProvider.System)
            .PreviewAsync(stream, "Production Schedule Report 2026.xlsx");

        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Issues.Select(x => x.Message)));
        Assert.Equal(5, preview.Weeks.Count);
        Assert.Equal(12, preview.Weeks.Single(x => x.WeekStart == new DateOnly(2026, 9, 21)).Lines.Count);
        Assert.Contains(preview.Weeks.Single(x => x.WeekStart == new DateOnly(2026, 9, 21)).Lines,
            x => x.Date.DayOfWeek == DayOfWeek.Sunday);
        Assert.True(preview.Weeks.Single(x => x.WeekStart == new DateOnly(2026, 9, 21)).IsDraft);
        Assert.All(preview.Weeks.Where(x => !x.IsDraft), x => Assert.Single(x.Captures));
        Assert.Equal(2, preview.Weeks.Single(x => x.IsDraft).OpeningCarryovers.Count);
        Assert.Empty(preview.Weeks.Single(x => x.WeekStart == new DateOnly(2026, 9, 14)).OpeningCarryovers);
        var admin = new User { FullName = "Import Admin", RoleId = 1, PinLookup = "import", PinHash = "import" };
        db.Users.Add(admin); await db.SaveChangesAsync();
        var operationId = Guid.NewGuid();
        var importer = new ProductionScheduleImportService(db, TimeProvider.System);
        var confirmed = await importer.ConfirmAsync(preview, operationId, admin.Id);
        var retried = await importer.ConfirmAsync(preview, operationId, admin.Id);
        Assert.True(confirmed.Success);
        Assert.True(retried.Success);
        var importedCurrent = await db.ProductionScheduleWeeks.Include(x => x.Lines)
            .SingleAsync(x => x.WeekStart == new DateOnly(2026, 9, 21));
        Assert.Equal(14, importedCurrent.Lines.Count);
        Assert.Equal(2, importedCurrent.Lines.Count(x => x.IsCarryover && x.StartArea.HasValue));
        Assert.Equal(preview.Weeks.Single(x => x.IsDraft).FinalLines.Sum(x => x.Quantity), importedCurrent.Lines.Sum(x => x.Quantity));
    }

    [Fact]
    public async Task Import_blocks_an_unknown_sku_before_confirmation()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await SeedImportCatalogAsync(db);
        using var workbook = BuildWorkbook("UNKNOWN-SKU");
        using var stream = new MemoryStream(); workbook.SaveAs(stream); stream.Position = 0;

        var preview = await new ProductionScheduleImportService(db, TimeProvider.System)
            .PreviewAsync(stream, "Production Schedule Report 2026.xlsx");

        Assert.False(preview.CanConfirm);
        Assert.Contains(preview.Issues, x => x.Message.Contains("SKU sin resolver", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("BP20")]
    [InlineData("BP-20")]
    [InlineData("BP 20")]
    [InlineData(" bp-20 ")]
    public async Task Import_preserves_distinct_catalog_skus_in_plan_captures_and_carryover(string sku)
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await SeedImportCatalogAsync(db);
        var products = new[] { "BP20", "BP-20", "BP 20" }
            .Select(value => new Product { Sku = value, BaseUnitId = 1 }).ToArray();
        var admin = new User { FullName = "Import Admin", RoleId = 1, PinLookup = "import", PinHash = "import" };
        db.Products.AddRange(products); db.Users.Add(admin); await db.SaveChangesAsync();
        foreach (var product in products) await AddImportRouteAsync(db, product.Id);
        using var workbook = BuildWorkbook(sku);
        using var stream = new MemoryStream(); workbook.SaveAs(stream); stream.Position = 0;
        var importer = new ProductionScheduleImportService(db, TimeProvider.System);

        var preview = await importer.PreviewAsync(stream, "schedule.xlsx");
        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Issues.Select(x => x.Message)));
        var operation = Guid.NewGuid();
        var result = await importer.ConfirmAsync(preview, operation, admin.Id);
        Assert.True(result.Success);
        var expected = products.Single(x => x.Sku == sku.Trim().ToUpperInvariant()).Id;
        Assert.All(await db.ProductionScheduleLines.ToListAsync(), x => Assert.Equal(expected, x.ProductId));
        Assert.All(await db.ProductionDailyCaptures.ToListAsync(), x => Assert.Equal(expected, x.ProductId));
        Assert.Equal(2, await db.ProductionScheduleLines.CountAsync(x => x.IsCarryover));
        Assert.True((await importer.ConfirmAsync(preview, operation, admin.Id)).Success);
        Assert.Equal(5, await db.ProductionScheduleWeeks.CountAsync());
    }

    [Fact]
    public async Task Import_reports_missing_daily_configuration_once_instead_of_each_known_shift()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await SeedImportCatalogAsync(db);
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        config.Shift1Id = null; config.Shift2Id = null; await db.SaveChangesAsync();
        using var workbook = BuildWorkbook("FG-100");
        using var stream = new MemoryStream(); workbook.SaveAs(stream); stream.Position = 0;

        var preview = await new ProductionScheduleImportService(db, TimeProvider.System).PreviewAsync(stream, "schedule.xlsx");

        Assert.False(preview.CanConfirm);
        Assert.Equal(ProductionScheduleImportIssueKind.Configuration,
            Assert.Single(preview.Issues, x => x.Sheet == "Configuración").Kind);
        Assert.DoesNotContain(preview.Issues, x => x.Message.StartsWith("Turno sin resolver", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_resolutions_link_texts_fix_or_skip_rows_and_audit_the_batch()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await SeedImportCatalogAsync(db);
        var product = await db.Products.SingleAsync();
        var admin = new User { FullName = "Import Admin", RoleId = 1, PinLookup = "import", PinHash = "import" };
        db.Users.Add(admin); await db.SaveChangesAsync();
        using var workbook = BuildWorkbook("FG-100");
        workbook.Worksheet("08-24 to 08-30").Cell(22, 10).Value = "Cuttin";
        workbook.Worksheet("08-24 to 08-30").Cell(22, 11).Value = "Turno A";
        workbook.Worksheet("08-31 to 09-06").Cell(22, 4).Value = "FG100X";
        var draftSheet = workbook.Worksheet("09-21 to 09-27");
        draftSheet.Cell(22, 5).Clear(XLClearOptions.Contents);
        draftSheet.Cell(23, 5).Clear(XLClearOptions.Contents);
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        var bytes = stream.ToArray();
        var importer = new ProductionScheduleImportService(db, TimeProvider.System);
        const string Draft = "09-21 to 09-27";

        var blocked = await importer.PreviewAsync(new MemoryStream(bytes), "schedule.xlsx");
        Assert.Contains(blocked.Issues, x => x.Kind == ProductionScheduleImportIssueKind.UnknownArea &&
            x.Value == "Cuttin" && x.Table == ProductionScheduleImportTable.Execution && x.Row == 22);
        Assert.Contains(blocked.Issues, x => x.Kind == ProductionScheduleImportIssueKind.UnknownShift && x.Value == "Turno A");
        Assert.Contains(blocked.Issues, x => x.Kind == ProductionScheduleImportIssueKind.UnknownSku &&
            x.Value == "FG100X" && x.Table == ProductionScheduleImportTable.Plan);
        Assert.Equal(2, blocked.Issues.Count(x => x.Kind == ProductionScheduleImportIssueKind.InvalidQuantity &&
            x.Sheet == Draft && x.Table == ProductionScheduleImportTable.Plan));

        var invalidFix = await importer.PreviewAsync(new MemoryStream(bytes), "schedule.xlsx",
            ProductionScheduleImportResolutions.None with { Rows = [new(Draft, ProductionScheduleImportTable.Plan, 22, Quantity: 0m)] });
        Assert.Contains(invalidFix.Issues, x => x.Kind == ProductionScheduleImportIssueKind.InvalidQuantity && x.Row == 22);

        var resolutions = new ProductionScheduleImportResolutions(
            new Dictionary<string, ProductionDailyArea> { ["Cuttin"] = ProductionDailyArea.Cutting },
            new Dictionary<string, int> { ["Turno A"] = 1 },
            new Dictionary<string, Guid> { ["FG100X"] = product.Id },
            [new(Draft, ProductionScheduleImportTable.Plan, 22, Quantity: 40m),
                new(Draft, ProductionScheduleImportTable.Plan, 23, Skip: true)]);
        var preview = await importer.PreviewAsync(new MemoryStream(bytes), "schedule.xlsx", resolutions);

        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Issues.Select(x => x.Message)));
        var draft = preview.Weeks.Single(x => x.IsDraft);
        Assert.Equal(11, draft.Lines.Count);
        Assert.Equal(40m, draft.Lines.Single(x => x.SourceRow == 22).Quantity);
        Assert.DoesNotContain(draft.Lines, x => x.SourceRow == 23);
        Assert.Equal(ProductionDailyArea.Cutting, preview.Weeks[0].Captures.Single().Area);
        Assert.Equal(5, preview.AppliedResolutions.Count);

        var result = await importer.ConfirmAsync(preview, Guid.NewGuid(), admin.Id);
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        var batch = await db.ProductionScheduleImportBatches.SingleAsync();
        Assert.Contains("fila 23: omitida", batch.ResolutionSummary, StringComparison.Ordinal);
        Assert.Contains("\"Cuttin\"", batch.ResolutionSummary, StringComparison.Ordinal);
        Assert.NotEqual(batch.FileHash, batch.RequestFingerprint);
        Assert.Equal(product.Id, (await db.ProductionScheduleLines.SingleAsync(x => x.SourceSheet == "08-31 to 09-06")).ProductId);
    }

    [Fact]
    public async Task Import_does_not_substitute_sku_with_different_punctuation()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await SeedImportCatalogAsync(db);
        using var workbook = BuildWorkbook("FG100");
        using var stream = new MemoryStream(); workbook.SaveAs(stream); stream.Position = 0;
        var preview = await new ProductionScheduleImportService(db, TimeProvider.System).PreviewAsync(stream, "schedule.xlsx");
        Assert.False(preview.CanConfirm);
        Assert.Contains(preview.Issues, x => x.Message == "SKU sin resolver: FG100.");
    }

    [Fact]
    public async Task Import_blocks_product_deactivated_after_preview_without_partial_writes()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await SeedImportCatalogAsync(db);
        var admin = new User { FullName = "Import Admin", RoleId = 1, PinLookup = "import", PinHash = "import" };
        db.Users.Add(admin); await db.SaveChangesAsync();
        using var workbook = BuildWorkbook("FG-100");
        using var stream = new MemoryStream(); workbook.SaveAs(stream); stream.Position = 0;
        var importer = new ProductionScheduleImportService(db, TimeProvider.System);
        var preview = await importer.PreviewAsync(stream, "schedule.xlsx");
        Assert.True(preview.CanConfirm);
        (await db.Products.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();

        var result = await importer.ConfirmAsync(preview, Guid.NewGuid(), admin.Id);
        Assert.Equal(ProductionDailyCommandStatus.ValidationFailed, result.Status);
        Assert.Contains("SKU sin resolver: FG-100.", result.Errors!);
        Assert.Empty(await db.ProductionScheduleWeeks.ToListAsync());
        Assert.Empty(await db.ProductionScheduleImportBatches.ToListAsync());
    }

    [Fact]
    public async Task Capture_preview_splits_repeated_sku_fifo_between_schedule_lines()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = "x", PinHash = "x" };
        var product = new Product { Sku = "FG-FIFO", BaseUnitId = 1 };
        var cutting = new ProductionStage { Code = "CUT", Name = "Cutting" };
        var sewing = new ProductionStage { Code = "SEW", Name = "Sewing" };
        var ready = new ProductionStage { Code = "RTP", Name = "Ready to Pack" };
        var t1 = new ProductionShift { Code = "T1", Name = "Shift 1" };
        var t2 = new ProductionShift { Code = "T2", Name = "Shift 2" };
        db.AddRange(admin, product, cutting, sewing, ready, t1, t2); await db.SaveChangesAsync();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        config.CuttingStageId = cutting.Id; config.SewingStageId = sewing.Id; config.ReadyToPackStageId = ready.Id;
        config.Shift1Id = t1.Id; config.Shift2Id = t2.Id;
        var monday = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek { OperationId = Guid.NewGuid(), RequestFingerprint = "B".PadLeft(64, 'B'),
            WeekStart = monday, WeekEnd = monday.AddDays(6), Status = ProductionScheduleWeekStatus.Open,
            CreatedByUserId = admin.Id, CreatedAt = DateTimeOffset.UtcNow };
        week.Lines.Add(LineWithOrder(week, product, cutting, admin, 1, monday, 5));
        week.Lines.Add(LineWithOrder(week, product, cutting, admin, 2, monday.AddDays(1), 7));
        db.Add(week); await db.SaveChangesAsync();
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var service = new ProductionDailyCaptureService(db, pins,
            new InventoryMovementService(db, pins, TimeProvider.System),
            new WarehouseClock(new WarehouseSettingsService(db)), TimeProvider.System);

        var preview = await service.PreviewAsync(new(monday, ProductionDailyArea.Cutting, t1.Id, product.Id, 8));

        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Blockers));
        Assert.Collection(preview.Allocations,
            first => Assert.Equal(5, first.Quantity), second => Assert.Equal(3, second.Quantity));
    }

    [Fact]
    public async Task Confirmed_daily_capture_is_immutable_except_for_reversal_fields()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var capture = new ProductionDailyCapture
        {
            OperationId = Guid.NewGuid(), RequestFingerprint = "C".PadLeft(64, 'C'), WeekId = Guid.NewGuid(),
            EffectiveDate = new(2026, 9, 21), Area = ProductionDailyArea.Cutting, StageId = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(), ProductId = Guid.NewGuid(), Quantity = 10, ResponsibleUserId = Guid.NewGuid(),
            RecordedAt = DateTimeOffset.UtcNow
        };
        db.ProductionDailyCaptures.Add(capture); await db.SaveChangesAsync();
        capture.Quantity = 11;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Balance_carries_prior_closed_week_pending_forward_by_process()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = "x", PinHash = "x" };
        var product = new Product { Sku = "FG-CARRY", BaseUnitId = 1 };
        var cutting = new ProductionStage { Code = "CUT", Name = "Cutting" };
        var sewing = new ProductionStage { Code = "SEW", Name = "Sewing" };
        var ready = new ProductionStage { Code = "RTP", Name = "Ready to Pack" };
        var t1 = new ProductionShift { Code = "T1", Name = "Shift 1" };
        var t2 = new ProductionShift { Code = "T2", Name = "Shift 2" };
        db.AddRange(admin, product, cutting, sewing, ready, t1, t2); await db.SaveChangesAsync();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        config.CuttingStageId = cutting.Id; config.SewingStageId = sewing.Id; config.ReadyToPackStageId = ready.Id;
        config.Shift1Id = t1.Id; config.Shift2Id = t2.Id;
        db.ProductionRoutes.Add(new ProductionRoute { ProductId = product.Id, Name = "Ruta completa",
            Stages = { new ProductionRouteStage { StageId = cutting.Id, Sequence = 1 },
                new ProductionRouteStage { StageId = sewing.Id, Sequence = 2 },
                new ProductionRouteStage { StageId = ready.Id, Sequence = 3 } } });
        var priorMonday = new DateOnly(2026, 9, 14);
        var prior = new ProductionScheduleWeek { OperationId = Guid.NewGuid(), RequestFingerprint = "D".PadLeft(64, 'D'),
            WeekStart = priorMonday, WeekEnd = priorMonday.AddDays(6), Status = ProductionScheduleWeekStatus.Closed,
            CreatedByUserId = admin.Id, CreatedAt = DateTimeOffset.UtcNow };
        prior.Lines.Add(LineWithOrder(prior, product, cutting, admin, 1, priorMonday, 100));
        var priorLine = prior.Lines.Single();
        var priorOrder = priorLine.WorkOrder!;
        var sewingStage = new ProductionWorkOrderStage { SourceStageId = sewing.Id, Sequence = 2, Code = sewing.Code, Name = sewing.Name };
        priorOrder.Stages.Add(sewingStage);
        priorOrder.Stages.Add(new ProductionWorkOrderStage { SourceStageId = ready.Id, Sequence = 3, Code = ready.Code, Name = ready.Name });
        prior.Captures.Add(Capture(prior, product, cutting, t1, admin, ProductionDailyArea.Cutting, 70));
        prior.Captures.Add(Capture(prior, product, sewing, t1, admin, ProductionDailyArea.Sewing, 30));
        foreach (var capture in prior.Captures)
            capture.Allocations.Add(new ProductionDailyCaptureAllocation
            {
                ScheduleLine = priorLine, WorkOrder = priorOrder,
                WorkOrderStage = priorOrder.Stages.Single(x => x.SourceStageId == capture.StageId),
                Quantity = capture.Quantity, ProcessOperationId = Guid.NewGuid()
            });
        var currentMonday = priorMonday.AddDays(7);
        var current = new ProductionScheduleWeek { OperationId = Guid.NewGuid(), RequestFingerprint = "E".PadLeft(64, 'E'),
            WeekStart = currentMonday, WeekEnd = currentMonday.AddDays(6), Status = ProductionScheduleWeekStatus.Open,
            CreatedByUserId = admin.Id, CreatedAt = DateTimeOffset.UtcNow };
        db.AddRange(prior, current); await db.SaveChangesAsync();

        var balance = await new ProductionDailyBalanceService(db).GetAsync(current.Id);
        var monday = Assert.Single(balance!.Rows, x => x.Date == currentMonday);
        Assert.Equal(30, monday.Cutting.Pending);
        Assert.Equal(40, monday.Sewing.Pending);
        Assert.Equal(30, monday.ReadyToPack.Pending);
    }

    [Fact]
    public async Task Publishing_creates_one_released_order_and_replaces_an_idle_sku_atomically()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = string.Empty, PinHash = string.Empty };
        await pins.AssignAsync(admin, "4826");
        var first = new Product { Sku = "FG-FIRST", BaseUnitId = 1 };
        var replacement = new Product { Sku = "FG-REPLACEMENT", BaseUnitId = 1 };
        var material = new Product { Sku = "MP-DAILY", BaseUnitId = 1 };
        var cutting = new ProductionStage { Code = "CUT", Name = "Cutting" };
        var sewing = new ProductionStage { Code = "SEW", Name = "Sewing" };
        var ready = new ProductionStage { Code = "RTP", Name = "Ready to Pack" };
        var t1 = new ProductionShift { Code = "T1", Name = "Shift 1" };
        var t2 = new ProductionShift { Code = "T2", Name = "Shift 2" };
        var wip = new Location { Code = "WIP-DAILY", Kind = LocationKind.Area,
            OperationalRole = LocationOperationalRole.Wip };
        cutting.WipTargets.Add(new ProductionProcessWipTarget { Location = wip });
        db.AddRange(admin, first, replacement, material, cutting, sewing, ready, t1, t2, wip);
        await db.SaveChangesAsync();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        config.CuttingStageId = cutting.Id; config.SewingStageId = sewing.Id; config.ReadyToPackStageId = ready.Id;
        config.Shift1Id = t1.Id; config.Shift2Id = t2.Id;
        foreach (var product in new[] { first, replacement })
        {
            db.ProductionRoutes.Add(new ProductionRoute { ProductId = product.Id, Name = $"Ruta {product.Sku}",
                Stages = { new ProductionRouteStage { StageId = cutting.Id, Sequence = 1 },
                    new ProductionRouteStage { StageId = sewing.Id, Sequence = 2 },
                    new ProductionRouteStage { StageId = ready.Id, Sequence = 3 } } });
            db.ProductionRecipes.Add(new ProductionRecipe { ProductId = product.Id, Version = 1, BaseQuantity = 1,
                Reason = "Receta diaria", CreatedByUserId = admin.Id,
                Lines = { new ProductionRecipeLine { MaterialProductId = material.Id, StageId = cutting.Id, Quantity = 1 } } });
        }
        var monday = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek { OperationId = Guid.NewGuid(), RequestFingerprint = "F".PadLeft(64, 'F'),
            WeekStart = monday, WeekEnd = monday.AddDays(6), CreatedByUserId = admin.Id, CreatedAt = DateTimeOffset.UtcNow };
        week.Lines.Add(new ProductionScheduleLine { Sequence = 1, PlannedDate = monday, ProductId = first.Id, Quantity = 10 });
        db.Add(week); await db.SaveChangesAsync();
        var service = new ProductionDailyScheduleService(db, pins,
            new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);

        var published = await service.PublishAsync(new(Guid.NewGuid(), week.Id, week.Version, "4826", admin.Id));

        Assert.True(published.Success, string.Join(" | ", published.Errors ?? []));
        var publishedWeek = await service.GetWeekAsync(week.Id);
        Assert.Equal(ProductionScheduleWeekStatus.Open, publishedWeek!.Status);
        var oldOrderId = Assert.IsType<Guid>(Assert.Single(publishedWeek.Lines).WorkOrderId);
        var oldOrder = await db.ProductionWorkOrders.AsNoTracking().Include(x => x.MaterialPlan).SingleAsync(x => x.Id == oldOrderId);
        Assert.Equal(ProductionWorkOrderStatus.Released, oldOrder.Status);
        // The daily program never snapshots the recipe, even when the product has one.
        Assert.Empty(oldOrder.MaterialPlan); Assert.Null(oldOrder.RecipeVersion);
        Assert.Single(await db.ProductionBatches.AsNoTracking().Where(x => x.WorkOrderId == oldOrderId).ToListAsync());

        var publishedLine = Assert.Single(publishedWeek.Lines);
        var replaced = await service.SaveLineAsync(new(Guid.NewGuid(), week.Id, publishedLine.Id,
            publishedWeek.Version, publishedLine.Version, monday, replacement.Id, 12,
            "REF-NEW", null, null, "SKU corregido", admin.Id));

        Assert.True(replaced.Success, string.Join(" | ", replaced.Errors ?? []));
        Assert.Equal(ProductionWorkOrderStatus.Cancelled,
            (await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Id == oldOrderId)).Status);
        var finalWeek = await service.GetWeekAsync(week.Id);
        var finalLine = Assert.Single(finalWeek!.Lines);
        Assert.Equal(replacement.Id, finalLine.ProductId);
        Assert.NotEqual(oldOrderId, finalLine.WorkOrderId);
        Assert.Equal(ProductionWorkOrderStatus.Released,
            (await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Id == finalLine.WorkOrderId)).Status);

        var nextMonday = monday.AddDays(7);
        var carryoverWeek = new ProductionScheduleWeek { OperationId = Guid.NewGuid(), RequestFingerprint = "G".PadLeft(64, 'G'),
            WeekStart = nextMonday, WeekEnd = nextMonday.AddDays(6), CreatedByUserId = admin.Id, CreatedAt = DateTimeOffset.UtcNow };
        carryoverWeek.Lines.Add(new ProductionScheduleLine { Sequence = 1, PlannedDate = nextMonday,
            ProductId = first.Id, Quantity = 7, IsCarryover = true, StartArea = ProductionDailyArea.ReadyToPack });
        db.Add(carryoverWeek); await db.SaveChangesAsync();
        var carryoverPublished = await service.PublishAsync(new(Guid.NewGuid(), carryoverWeek.Id,
            carryoverWeek.Version, "4826", admin.Id));
        Assert.True(carryoverPublished.Success, string.Join(" | ", carryoverPublished.Errors ?? []));
        var carryoverOrderId = (await service.GetWeekAsync(carryoverWeek.Id))!.Lines.Single().WorkOrderId!.Value;
        var carryoverOrder = await db.ProductionWorkOrders.AsNoTracking().Include(x => x.Stages)
            .Include(x => x.MaterialPlan).SingleAsync(x => x.Id == carryoverOrderId);
        Assert.Equal(ProductionWorkOrderStatus.Released, carryoverOrder.Status);
        Assert.Equal(ready.Id, Assert.Single(carryoverOrder.Stages).SourceStageId);
        Assert.Empty(carryoverOrder.MaterialPlan);
    }

    [Fact]
    public async Task Publishing_without_route_or_recipe_follows_daily_processes_and_captures_chain()
    {
        var database = Guid.NewGuid().ToString();
        await using var db = ProductionOpeningImportTests.Context(database);
        await db.Database.EnsureCreatedAsync();
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = string.Empty, PinHash = string.Empty };
        await pins.AssignAsync(admin, "4826");
        var plain = new Product { Sku = "FG-PLAIN", BaseUnitId = 1 };
        var noSewing = new Product { Sku = "FG-NOSEW", BaseUnitId = 1 };
        var cutting = new ProductionStage { Code = "CUT", Name = "Cutting" };
        var sewing = new ProductionStage { Code = "SEW", Name = "Sewing" };
        var ready = new ProductionStage { Code = "RTP", Name = "Ready to Pack" };
        var t1 = new ProductionShift { Code = "T1", Name = "Shift 1" };
        var t2 = new ProductionShift { Code = "T2", Name = "Shift 2" };
        db.AddRange(admin, plain, noSewing, cutting, sewing, ready, t1, t2);
        await db.SaveChangesAsync();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        config.CuttingStageId = cutting.Id; config.SewingStageId = sewing.Id; config.ReadyToPackStageId = ready.Id;
        config.Shift1Id = t1.Id; config.Shift2Id = t2.Id;
        // New daily orders use all three stages even if the catalog route skips Sewing.
        db.ProductionRoutes.Add(new ProductionRoute { ProductId = noSewing.Id, Name = "Ruta sin costura",
            Stages = { new ProductionRouteStage { StageId = cutting.Id, Sequence = 1 }, new ProductionRouteStage { StageId = ready.Id, Sequence = 2 } } });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monday = today.AddDays(-7 - ((int)today.DayOfWeek + 6) % 7);
        var week = new ProductionScheduleWeek { OperationId = Guid.NewGuid(), RequestFingerprint = "H".PadLeft(64, 'H'),
            WeekStart = monday, WeekEnd = monday.AddDays(6), CreatedByUserId = admin.Id, CreatedAt = DateTimeOffset.UtcNow };
        week.Lines.Add(new ProductionScheduleLine { Sequence = 1, PlannedDate = monday, ProductId = plain.Id, Quantity = 10 });
        week.Lines.Add(new ProductionScheduleLine { Sequence = 2, PlannedDate = monday, ProductId = noSewing.Id, Quantity = 5 });
        // The route lacks the carryover area, so the order starts there and follows the daily processes.
        week.Lines.Add(new ProductionScheduleLine { Sequence = 3, PlannedDate = monday, ProductId = noSewing.Id, Quantity = 3,
            IsCarryover = true, StartArea = ProductionDailyArea.Sewing });
        db.Add(week); await db.SaveChangesAsync();
        var movements = new InventoryMovementService(db, pins, TimeProvider.System);
        var schedule = new ProductionDailyScheduleService(db, pins, movements, TimeProvider.System);

        var published = await schedule.PublishAsync(new(Guid.NewGuid(), week.Id, week.Version, "4826", admin.Id));

        Assert.True(published.Success, string.Join(" | ", published.Errors ?? []));
        var orders = await db.ProductionWorkOrders.AsNoTracking().Include(x => x.Stages).Include(x => x.MaterialPlan)
            .Include(x => x.Batches).ToListAsync();
        var lines = (await schedule.GetWeekAsync(week.Id))!.Lines;
        Guid[] StagesOf(int sequence) => orders.Single(x => x.Id == lines.Single(l => l.Sequence == sequence).WorkOrderId)
            .Stages.OrderBy(x => x.Sequence).Select(x => x.SourceStageId).ToArray();
        Assert.Equal([cutting.Id, sewing.Id, ready.Id], StagesOf(1));
        Assert.Equal([cutting.Id, sewing.Id, ready.Id], StagesOf(2));
        Assert.Equal([sewing.Id, ready.Id], StagesOf(3));
        Assert.All(orders, order =>
        {
            Assert.Equal(ProductionWorkOrderStatus.Released, order.Status);
            Assert.True(order.UsesBatchTraceability); Assert.Null(order.RecipeVersion);
            Assert.Empty(order.MaterialPlan); Assert.Single(order.Batches);
        });
        var balance = Assert.Single((await new ProductionDailyBalanceService(db).GetAsync(week.Id))!.Rows,
            x => x.Date == monday && x.ProductId == plain.Id);
        Assert.True(balance.Cutting.Applies && balance.Sewing.Applies && balance.ReadyToPack.Applies);
        Assert.Equal(10, balance.Cutting.Pending);

        // Each capture runs in its own context, as each request does in the application.
        foreach (var area in new[] { ProductionDailyArea.Cutting, ProductionDailyArea.Sewing })
        {
            await using var request = ProductionOpeningImportTests.Context(database);
            var requestPins = new UserPinService(request, new PinProtector(PinKey));
            var captures = new ProductionDailyCaptureService(request, requestPins,
                new InventoryMovementService(request, requestPins, TimeProvider.System),
                new WarehouseClock(new WarehouseSettingsService(request)), TimeProvider.System);
            var captured = await captures.ConfirmAsync(new(Guid.NewGuid(), monday, area, t1.Id, plain.Id, 10, null, "4826"));
            Assert.True(captured.Success, string.Join(" | ", captured.Errors ?? []));
        }
        await using var edit = ProductionOpeningImportTests.Context(database);
        var editPins = new UserPinService(edit, new PinProtector(PinKey));
        var editor = new ProductionDailyScheduleService(edit, editPins,
            new InventoryMovementService(edit, editPins, TimeProvider.System), TimeProvider.System);
        var current = (await editor.GetWeekAsync(week.Id))!;
        var line = current.Lines.Single(x => x.Sequence == 1);
        // Ten pieces through two processes are still ten processed pieces, so the unchanged quantity is accepted.
        var saved = await editor.SaveLineAsync(new(Guid.NewGuid(), week.Id, line.Id, current.Version, line.Version,
            monday, plain.Id, 10, null, null, null, null, admin.Id));
        Assert.True(saved.Success, string.Join(" | ", saved.Errors ?? []));
    }

    [Fact]
    public async Task Publishing_requires_the_configured_daily_processes_to_be_active()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = string.Empty, PinHash = string.Empty };
        await pins.AssignAsync(admin, "4826");
        var product = new Product { Sku = "FG-INACTIVE-STAGE", BaseUnitId = 1 };
        var cutting = new ProductionStage { Code = "CUT", Name = "Cutting" };
        var sewing = new ProductionStage { Code = "SEW", Name = "Sewing", IsActive = false };
        var ready = new ProductionStage { Code = "RTP", Name = "Ready to Pack" };
        var t1 = new ProductionShift { Code = "T1", Name = "Shift 1" };
        var t2 = new ProductionShift { Code = "T2", Name = "Shift 2" };
        db.AddRange(admin, product, cutting, sewing, ready, t1, t2);
        await db.SaveChangesAsync();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        config.CuttingStageId = cutting.Id; config.SewingStageId = sewing.Id; config.ReadyToPackStageId = ready.Id;
        config.Shift1Id = t1.Id; config.Shift2Id = t2.Id;
        var monday = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek { OperationId = Guid.NewGuid(), RequestFingerprint = "I".PadLeft(64, 'I'),
            WeekStart = monday, WeekEnd = monday.AddDays(6), CreatedByUserId = admin.Id, CreatedAt = DateTimeOffset.UtcNow };
        week.Lines.Add(new ProductionScheduleLine { Sequence = 1, PlannedDate = monday, ProductId = product.Id, Quantity = 10 });
        db.Add(week); await db.SaveChangesAsync();
        var service = new ProductionDailyScheduleService(db, pins,
            new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);

        var published = await service.PublishAsync(new(Guid.NewGuid(), week.Id, week.Version, "4826", admin.Id));

        Assert.Equal(ProductionDailyCommandStatus.ValidationFailed, published.Status);
        Assert.Contains("Selecciona tres procesos activos y diferentes.", published.Errors!);
        Assert.Empty(await db.ProductionWorkOrders.ToListAsync());
    }

    private static ProductionDailyCapture Capture(ProductionScheduleWeek week, Product product,
        ProductionStage stage, ProductionShift shift, User user, ProductionDailyArea area, decimal quantity) => new()
    {
        OperationId = Guid.NewGuid(), RequestFingerprint = Guid.NewGuid().ToString("N").PadRight(64, '0'),
        EffectiveDate = week.WeekStart, Area = area, StageId = stage.Id, ShiftId = shift.Id,
        ProductId = product.Id, Quantity = quantity, ResponsibleUserId = user.Id, RecordedAt = DateTimeOffset.UtcNow
    };

    private static ProductionScheduleLine LineWithOrder(ProductionScheduleWeek week, Product product,
        ProductionStage stage, User admin, int sequence, DateOnly date, decimal quantity)
    {
        var order = new ProductionWorkOrder { CreateOperationId = Guid.NewGuid(), CreateFingerprint = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            Number = $"OT-{sequence}", ProductId = product.Id, UnitId = 1, OriginalTargetQuantity = quantity,
            TargetQuantity = quantity, AuthorizedQuantity = quantity, Status = ProductionWorkOrderStatus.Released,
            CreatedByUserId = admin.Id, CreatedAt = DateTimeOffset.UtcNow, UsesBatchTraceability = true };
        var orderStage = new ProductionWorkOrderStage { SourceStageId = stage.Id, Sequence = 1, Code = stage.Code, Name = stage.Name };
        order.Stages.Add(orderStage);
        order.Batches.Add(new ProductionBatch { CreateOperationId = Guid.NewGuid(), CreateFingerprint = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            Number = $"OT-{sequence}-L001", AssignedQuantity = quantity,
            FinishedProductLot = new ProductLot { ProductId = product.Id, Number = $"LOT-{sequence}", NormalizedNumber = $"LOT-{sequence}" },
            CreatedByUserId = admin.Id, CreatedAt = DateTimeOffset.UtcNow });
        return new ProductionScheduleLine { Sequence = sequence, PlannedDate = date, ProductId = product.Id,
            Quantity = quantity, WorkOrder = order };
    }

    internal static async Task SeedImportCatalogAsync(WarehouseDbContext db)
    {
        var product = new Product { Sku = "FG-100", BaseUnitId = 1 };
        var stages = new[] { new ProductionStage { Code = "CUT", Name = "Cutting" },
            new ProductionStage { Code = "SEW", Name = "Sewing" }, new ProductionStage { Code = "RTP", Name = "Ready to Pack" } };
        var shifts = new[] { new ProductionShift { Code = "T1", Name = "Shift 1" }, new ProductionShift { Code = "T2", Name = "Shift 2" } };
        db.Add(product); db.AddRange(stages); db.AddRange(shifts); await db.SaveChangesAsync();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        config.CuttingStageId = stages[0].Id; config.SewingStageId = stages[1].Id; config.ReadyToPackStageId = stages[2].Id;
        config.Shift1Id = shifts[0].Id; config.Shift2Id = shifts[1].Id; await db.SaveChangesAsync();
        await AddImportRouteAsync(db, product.Id);
    }

    internal static async Task AddImportRouteAsync(WarehouseDbContext db, Guid productId)
    {
        var stages = await db.ProductionStages.ToListAsync();
        db.ProductionRoutes.Add(new ProductionRoute { ProductId = productId, Name = "Import route",
            Stages = new[] { "CUT", "SEW", "RTP" }.Select((code, i) => new ProductionRouteStage
                { StageId = stages.Single(x => x.Code == code).Id, Sequence = i + 1 }).ToArray() });
        await db.SaveChangesAsync();
    }

    internal static XLWorkbook BuildWorkbook(string sku)
    {
        var workbook = new XLWorkbook();
        workbook.AddWorksheet("Daily Template"); workbook.AddWorksheet("ITEMS");
        var starts = new[] { new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 7),
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 21) };
        for (var index = 0; index < starts.Length; index++)
        {
            var start = starts[index]; var sheet = workbook.AddWorksheet($"{start:MM-dd} to {start.AddDays(6):MM-dd}");
            var planColumn = index == 0 ? 1 : 3; var planRow = index == 0 ? 19 : 21;
            var headers = new[] { "Day", "Part Number", "Qty", "Order 1", "Order 2", "Order 3", "Notes" };
            for (var column = 0; column < headers.Length; column++) sheet.Cell(planRow, planColumn + column).Value = headers[column];
            var count = index == starts.Length - 1 ? 12 : 1;
            for (var row = 1; row <= count; row++)
            {
                sheet.Cell(planRow + row, planColumn).Value = start.AddDays((row - 1) % 7).ToDateTime(TimeOnly.MinValue);
                sheet.Cell(planRow + row, planColumn + 1).Value = sku;
                sheet.Cell(planRow + row, planColumn + 2).Value = 10 + row;
            }
            sheet.Range(planRow, planColumn, planRow + count, planColumn + headers.Length - 1).CreateTable($"Plan{index}");
            if (index == starts.Length - 1) continue;
            var execColumn = index == 0 ? 9 : 12; var execRow = index == 0 ? 21 : 23;
            var executionHeaders = new[] { "Date", "Area", "Shift", "Part Number", "Pieces Completed", "Reported by", "Notes" };
            for (var column = 0; column < executionHeaders.Length; column++) sheet.Cell(execRow, execColumn + column).Value = executionHeaders[column];
            sheet.Cell(execRow + 1, execColumn).Value = start.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(execRow + 1, execColumn + 1).Value = "Cutting"; sheet.Cell(execRow + 1, execColumn + 2).Value = "Shift 1";
            sheet.Cell(execRow + 1, execColumn + 3).Value = sku; sheet.Cell(execRow + 1, execColumn + 4).Value = 5;
            sheet.Range(execRow, execColumn, execRow + 1, execColumn + executionHeaders.Length - 1).CreateTable($"Execution{index}");
            if (index == 3)
            {
                var closingHeaders = new[] { "Item", "Cutting pending final", "Sewing pending final", "Ready to Pack pending final" };
                for (var column = 0; column < closingHeaders.Length; column++) sheet.Cell(168, 20 + column).Value = closingHeaders[column];
                sheet.Cell(169, 20).Value = sku; sheet.Cell(169, 21).Value = 6;
                sheet.Cell(169, 22).Value = 11; sheet.Cell(169, 23).Value = 11;
                sheet.Range(168, 20, 169, 23).CreateTable("PendingNextWeek");
                var openingHeaders = new[] { "Item", "Arrastre Corte", "Arrastre Costura", "Arrastre Ready to Pack" };
                for (var column = 0; column < openingHeaders.Length; column++) sheet.Cell(6, 20 + column).Value = openingHeaders[column];
                sheet.Cell(7, 20).Value = sku;
                for (var column = 21; column <= 23; column++) sheet.Cell(7, column).Value = 0;
                sheet.Range(6, 20, 7, 23).CreateTable("ProduccionLunes");
            }
        }
        workbook.AddWorksheet("Weekly Entry Template");
        return workbook;
    }

    private static WarehouseDbContext Context() => new(new DbContextOptionsBuilder<WarehouseDbContext>()
        .UseInMemoryDatabase($"DailyProduction-{Guid.NewGuid():N}").Options);
}
