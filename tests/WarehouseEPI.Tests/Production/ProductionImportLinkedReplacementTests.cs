using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionImportLinkedReplacementTests
{
    private static ProductionDailyScheduleService Schedule(WarehouseDbContext db)
    {
        var pins = new UserPinService(db, new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="));
        return new(db, pins, new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
    }

    internal static MemoryStream File(params (DateOnly Date, string Sku, decimal Quantity)[] rows)
    {
        using var workbook = new XLWorkbook();
        var table = 0;
        foreach (var group in rows.GroupBy(x => x.Date))
        {
            var sheet = workbook.AddWorksheet(group.Key.ToString("MM-dd") + " to " + group.Key.AddDays(6).ToString("MM-dd"));
            sheet.Cell(1, 1).Value = "Day"; sheet.Cell(1, 2).Value = "Part Number"; sheet.Cell(1, 3).Value = "Qty";
            var row = 2;
            foreach (var item in group)
            {
                sheet.Cell(row, 1).Value = "Monday"; sheet.Cell(row, 2).Value = item.Sku; sheet.Cell(row++, 3).Value = item.Quantity;
            }
            sheet.Range(1, 1, row - 1, 3).CreateTable("Plan" + ++table);
        }
        var stream = new MemoryStream(); workbook.SaveAs(stream); stream.Position = 0; return stream;
    }

    [Fact]
    public async Task Replaces_two_published_weeks_preserving_orders_captures_allocations_and_forwarded_openings()
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        await VerifyPublishedReplacementAsync(db);
    }

    internal static async Task VerifyPublishedReplacementAsync(WarehouseDbContext db)
    {
        var first = await ProductionDailyFlexibleTests.SeedAsync(db, weekStart: new(2026, 9, 21));
        var second = await ProductionDailyFlexibleTests.SeedAsync(db, weekStart: new(2026, 9, 28));
        first.Week.ExplicitCarryover = second.Week.ExplicitCarryover = true;
        await db.SaveChangesAsync();
        var captures = ProductionDailyFlexibleTests.Capture(db);
        var command = new ProductionCaptureGroupCommand(Guid.NewGuid(), first.Date, ProductionDailyArea.Cutting,
            first.Shift, [new(first.Product.Id, 20, null)], Pin: "4826");
        var review = await captures.PreviewGroupAsync(command);
        Assert.True(review.CanConfirm, string.Join(";", review.Errors));
        Assert.True((await captures.ConfirmGroupAsync(command with { ReviewedFingerprint = review.Fingerprint })).Success);
        var firstLine = await db.ProductionScheduleLines.SingleAsync(x => x.WeekId == first.Week.Id && !x.IsExtra);
        var secondLine = await db.ProductionScheduleLines.SingleAsync(x => x.WeekId == second.Week.Id && !x.IsExtra);
        var orderIds = new[] { firstLine.WorkOrderId, secondLine.WorkOrderId };
        var initialOption = (await new ProductionWeekOpeningService(db).OptionsAsync(second.Week.Id))
            .Single(x => x.SourceLineId == firstLine.Id && x.Area == ProductionDailyArea.Cutting);
        var opening = new ProductionWeekOpening
        {
            WeekId = second.Week.Id,
            SourceWeekId = first.Week.Id,
            SourceLineId = firstLine.Id,
            ProductId = first.Product.Id,
            Area = ProductionDailyArea.Cutting,
            Quantity = 10,
            SourceFingerprint = initialOption.Fingerprint
        };
        db.Add(opening); await db.SaveChangesAsync();
        var before = await CaptureSnapshot(db);
        var allocationIds = await db.ProductionDailyCaptureAllocations.Select(x => x.Id).ToArrayAsync();
        using var file = File((first.Date, first.Product.Sku, 120), (first.Date, "FG-100", 5), (second.Date, second.Product.Sku, 80));
        var service = new ProductionScheduleImportService(db, TimeProvider.System, Schedule(db));
        var preview = await service.PreviewAsync(file, "published.xlsx", ProductionScheduleImportResolutions.None with { ReplaceProgramming = true });
        Assert.True(preview.CanConfirm, string.Join(";", preview.Issues));
        var operation = Guid.NewGuid();
        var result = await service.ConfirmAsync(preview, operation, first.User.Id);
        Assert.True(result.Success, string.Join(";", result.Errors ?? []));
        Assert.True((await service.ConfirmAsync(preview, operation, first.User.Id)).Success);
        Assert.Equal(orderIds, new[] { firstLine.WorkOrderId, secondLine.WorkOrderId });
        Assert.False(firstLine.IsCancelled); Assert.False(secondLine.IsCancelled);
        Assert.Equal(120, firstLine.Quantity); Assert.Equal(80, secondLine.Quantity);
        Assert.Equal(before, await CaptureSnapshot(db));
        Assert.Equal(allocationIds, await db.ProductionDailyCaptureAllocations.Select(x => x.Id).ToArrayAsync());
        Assert.Equal(10, opening.Quantity); Assert.Equal(firstLine.Id, opening.SourceLineId);
        Assert.NotEqual(initialOption.Fingerprint, opening.SourceFingerprint);
        Assert.Empty(await new ProductionWeekOpeningService(db).RevalidateAsync(second.Week.Id));
        Assert.Single(await db.ProductionScheduleRevisions.Where(x => x.Action == "import-openings-reviewed").ToArrayAsync());
        Assert.Equal(ProductionScheduleWeekStatus.Open, first.Week.Status); Assert.Equal(ProductionScheduleWeekStatus.Open, second.Week.Status);
        var added = await db.ProductionScheduleLines.Include(x => x.WorkOrder).SingleAsync(x => x.WeekId == first.Week.Id && x.Product.Sku == "FG-100");
        Assert.NotNull(added.WorkOrderId); Assert.Equal(ProductionWorkOrderStatus.Released, added.WorkOrder!.Status);
        Assert.Single(await db.ProductionBatches.Where(x => x.WorkOrderId == added.WorkOrderId).ToListAsync());
        Assert.Equal(120, (await db.ProductionWorkOrders.SingleAsync(x => x.Id == firstLine.WorkOrderId)).AuthorizedQuantity);
        var balance = await new ProductionDailyBalanceService(db).GetDailySummaryAsync(first.Week.Id, new(first.Date));
        var product = balance!.Products.Single(x => x.ProductId == first.Product.Id);
        Assert.Equal(120, product.Planned); Assert.Equal(20, product.Cutting.CompletedShift1);
        using var reducedFile = File((first.Date, first.Product.Sku, 5), (first.Date, "FG-100", 5));
        var reduced = await service.PreviewAsync(reducedFile, "reduced.xlsx", ProductionScheduleImportResolutions.None with { ReplaceProgramming = true });
        Assert.False(reduced.CanConfirm);
        Assert.Contains(reduced.Issues, x => x.Message.Contains("ya producidas", StringComparison.Ordinal));
        Assert.False((await service.ConfirmAsync(reduced, Guid.NewGuid(), first.User.Id)).Success);
        Assert.Equal(before, await CaptureSnapshot(db));
    }

    [Fact]
    public async Task Missing_committed_sku_is_a_specific_blocker_and_work_order_changes_invalidate_review()
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        var setup = await ProductionDailyFlexibleTests.SeedAsync(db, weekStart: new(2026, 9, 21));
        var line = await db.ProductionScheduleLines.SingleAsync(x => x.WeekId == setup.Week.Id);
        db.Add(new ProductionWeekOpening { SourceLineId = line.Id, SourceWeekId = setup.Week.Id, WeekId = Guid.NewGuid(), Quantity = 10 });
        await db.SaveChangesAsync();
        var importer = new ProductionScheduleImportService(db, TimeProvider.System, Schedule(db));
        using var removed = File((setup.Date, "FG-100", 20));
        var blocked = await importer.PreviewAsync(removed, "removed.xlsx", ProductionScheduleImportResolutions.None with { ReplaceProgramming = true });
        Assert.False(blocked.CanConfirm);
        Assert.Contains(blocked.Issues, x => x.Message.Contains(setup.Product.Sku, StringComparison.Ordinal) && x.Message.Contains("elimina", StringComparison.Ordinal));
        using var file = File((setup.Date, setup.Product.Sku, 120));
        var preview = await importer.PreviewAsync(file, "file.xlsx", ProductionScheduleImportResolutions.None with { ReplaceProgramming = true });
        Assert.True(preview.CanConfirm);
        (await db.ProductionWorkOrders.SingleAsync(x => x.Id == line.WorkOrderId)).Version++;
        await db.SaveChangesAsync();
        var result = await importer.ConfirmAsync(preview, Guid.NewGuid(), setup.User.Id);
        Assert.Equal(ProductionDailyCommandStatus.ConcurrencyConflict, result.Status);
        Assert.Equal(100, line.Quantity);
    }

    private static async Task<string> CaptureSnapshot(WarehouseDbContext db) => JsonSerializer.Serialize(await db.ProductionDailyCaptures.AsNoTracking()
        .OrderBy(x => x.Id).Select(x => new { x.Id, x.OperationId, x.Quantity, x.Status, x.RecordedAt, x.WeekId, x.Area, x.ShiftId }).ToArrayAsync());

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Missing_captured_line_leaves_plan_without_losing_history_or_cancelling_its_order(bool explicitWeek, bool reverseBeforeImport)
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        await VerifyDetachedCaptureAsync(db, explicitWeek, reverseBeforeImport);
    }

    internal static async Task VerifyDetachedCaptureAsync(WarehouseDbContext db, bool explicitWeek, bool reverseBeforeImport)
    {
        var setup = await ProductionDailyFlexibleTests.SeedAsync(db, weekStart: new(2026, 9, 28));
        setup.Week.ExplicitCarryover = explicitWeek;
        setup.Product.Sku = "M6-E-50UP-8M-NOHDL-US";
        var line = await db.ProductionScheduleLines.SingleAsync(x => x.WeekId == setup.Week.Id);
        line.Sequence = 4;
        await db.SaveChangesAsync();
        var captures = ProductionDailyFlexibleTests.Capture(db);
        foreach (var qty in new[] { 20m, 10m })
        {
            var command = new ProductionCaptureGroupCommand(Guid.NewGuid(), setup.Date, ProductionDailyArea.Cutting,
                setup.Shift, [new(setup.Product.Id, qty, null)], Pin: "4826");
            var review = await captures.PreviewGroupAsync(command);
            Assert.True(review.CanConfirm, string.Join(";", review.Errors));
            Assert.True((await captures.ConfirmGroupAsync(command with { ReviewedFingerprint = review.Fingerprint })).Success);
        }
        var ten = await db.ProductionDailyCaptures.SingleAsync(x => x.Quantity == 10);
        if (reverseBeforeImport)
            Assert.True((await captures.ReverseAsync(new(Guid.NewGuid(), ten.Id, "Before import", "4826"))).Success);
        var before = await CaptureSnapshot(db);
        var allocations = await db.ProductionDailyCaptureAllocations.OrderBy(x => x.Id).Select(x => new { x.Id, x.CaptureId, x.ScheduleLineId, x.WorkOrderId, x.Quantity }).ToArrayAsync();
        var order = await db.ProductionWorkOrders.SingleAsync(x => x.Id == line.WorkOrderId);
        var originalState = order.Status;
        using var file = File((setup.Date, "FG-100", 15));
        var service = new ProductionScheduleImportService(db, TimeProvider.System, Schedule(db));
        var preview = await service.PreviewAsync(file, "replacement.xlsx", ProductionScheduleImportResolutions.None with { ReplaceProgramming = true });
        Assert.True(preview.CanConfirm, string.Join(";", preview.Issues));
        var detached = Assert.Single(Assert.Single(preview.ReplacementImpact).OffProgramLines);
        Assert.Equal(4, detached.Sequence); Assert.Equal(setup.Product.Sku, detached.Sku);
        var operation = Guid.NewGuid();
        var saved = await service.ConfirmAsync(preview, operation, setup.User.Id);
        Assert.True(saved.Success, string.Join(";", saved.Errors ?? []));
        Assert.True((await service.ConfirmAsync(preview, operation, setup.User.Id)).Success);
        Assert.True(line.IsExtra); Assert.False(line.IsCancelled); Assert.Equal(100, line.Quantity);
        Assert.Equal(originalState, order.Status); Assert.Equal(order.Id, line.WorkOrderId);
        Assert.Equal(before, await CaptureSnapshot(db));
        Assert.Equal(allocations, await db.ProductionDailyCaptureAllocations.OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.CaptureId, x.ScheduleLineId, x.WorkOrderId, x.Quantity }).ToArrayAsync());
        Assert.Single(await db.ProductionScheduleRevisions.Where(x => x.LineId == line.Id && x.Action == "programming-detached").ToArrayAsync());
        var balance = await new ProductionDailyBalanceService(db).GetDailySummaryAsync(setup.Week.Id, new(setup.Date));
        var retained = balance!.Products.Single(x => x.ProductId == setup.Product.Id);
        Assert.Equal(0, retained.Planned); Assert.Equal(reverseBeforeImport ? 20 : 30, retained.Cutting.CompletedShift1);
        Assert.Equal(15, balance.Products.Sum(x => x.Planned));
        Assert.DoesNotContain((await Schedule(db).GetWeekAsync(setup.Week.Id))!.Lines, x => x.Id == line.Id);
        if (!explicitWeek)
        {
            var next = new ProductionScheduleWeek { WeekStart = setup.Date.AddDays(7), WeekEnd = setup.Date.AddDays(13), RequestFingerprint = "next", CreatedByUserId = setup.User.Id };
            db.Add(next); await db.SaveChangesAsync();
            var nextBalance = await new ProductionDailyBalanceService(db).GetDailySummaryAsync(next.Id, new(next.WeekStart));
            Assert.Equal(0, nextBalance!.Products.Single(x => x.ProductId == setup.Product.Id).Cutting.Opening);
        }
        // A later correction must not apply the disposable-extra cancellation rule to this retained order.
        var twenty = await db.ProductionDailyCaptures.SingleAsync(x => x.Quantity == 20);
        var reversal = await captures.ReverseAsync(new(Guid.NewGuid(), reverseBeforeImport ? twenty.Id : ten.Id, "After import", "4826"));
        Assert.True(reversal.Success, string.Join(";", reversal.Errors ?? []));
        Assert.False(line.IsCancelled); Assert.NotEqual(ProductionWorkOrderStatus.Cancelled, order.Status);
        if (!reverseBeforeImport) Assert.Equal(ProductionDailyCaptureStatus.Active, twenty.Status);
    }
}
