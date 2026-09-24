using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionScheduleLineCancellationTests
{
    private const string PinKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_confirmation_keeps_duplicate_skus_and_is_idempotent(bool publish)
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(admin, "4826");
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var service = new ProductionDailyScheduleService(db, pins,
            new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var monday = new DateOnly(2026, 9, 21);
        var weekId = (await service.CreateWeekAsync(new(Guid.NewGuid(), monday, admin.Id))).Id!.Value;
        var week = (await service.GetWeekAsync(weekId))!;
        if (publish)
        {
            Assert.True((await service.PublishAsync(new(Guid.NewGuid(), weekId, week.Version,
                "4826", admin.Id))).Success);
            week = (await service.GetWeekAsync(weekId))!;
        }
        var command = new SaveProductionScheduleBatchCommand(Guid.NewGuid(), weekId, week.Version,
            [new(monday, product.Id, 10, "ORDER-1", null, null, "First"),
                new(monday.AddDays(1), product.Id, 20, null, "ORDER-2", null, "Second")],
            admin.Id, publish ? "4826" : "");
        var invalid = command with { Lines = [command.Lines[0], command.Lines[1] with { Quantity = 0 }] };
        Assert.Equal(ProductionDailyCommandStatus.ValidationFailed,
            (await service.SaveBatchAsync(invalid)).Status);
        Assert.Empty((await service.GetWeekAsync(weekId))!.Lines);
        if (publish)
            Assert.Equal(ProductionDailyCommandStatus.InvalidPin,
                (await service.SaveBatchAsync(command with { AdminPin = "0000" })).Status);
        Assert.True((await service.SaveBatchAsync(command)).Success);
        Assert.True((await service.SaveBatchAsync(command)).Success);
        Assert.Equal(ProductionDailyCommandStatus.IdempotencyConflict,
            (await service.SaveBatchAsync(command with { ExpectedWeekVersion = week.Version + 1 })).Status);
        var lines = (await service.GetWeekAsync(weekId))!.Lines.OrderBy(x => x.PlannedDate).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal([10m, 20m], lines.Select(x => x.Quantity).ToArray());
        Assert.Equal("ORDER-1", lines[0].OrderReference1);
        Assert.Null(lines[0].OrderReference2);
        Assert.Null(lines[1].OrderReference1);
        Assert.Equal("ORDER-2", lines[1].OrderReference2);
        Assert.Single(await db.ProductionScheduleRevisions.Where(x => x.OperationId == command.OperationId).ToListAsync());
        Assert.Equal(publish, lines.All(x => x.WorkOrderId.HasValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_idle_line_hides_it_and_preserves_audit(bool publish)
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(admin, "4826");
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var service = new ProductionDailyScheduleService(db, pins,
            new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var day = new DateOnly(2026, 9, 21);
        var weekId = (await service.CreateWeekAsync(new(Guid.NewGuid(), day, admin.Id))).Id!.Value;
        var week = (await service.GetWeekAsync(weekId))!;
        Assert.True((await service.SaveLineAsync(new(Guid.NewGuid(), weekId, null, week.Version,
            null, day, product.Id, 10, null, null, null, null, admin.Id))).Success);
        week = (await service.GetWeekAsync(weekId))!;
        if (publish)
        {
            Assert.True((await service.PublishAsync(new(Guid.NewGuid(), weekId, week.Version,
                "4826", admin.Id))).Success);
            week = (await service.GetWeekAsync(weekId))!;
        }
        var line = Assert.Single(week.Lines);
        var operation = Guid.NewGuid();
        var command = new CancelProductionScheduleLineCommand(operation, weekId, line.Id,
            week.Version, line.Version, admin.Id);
        Assert.Equal(ProductionDailyCommandStatus.ConcurrencyConflict,
            (await service.CancelLineAsync(command with
            { OperationId = Guid.NewGuid(), ExpectedLineVersion = line.Version + 1 })).Status);
        Assert.True((await service.CancelLineAsync(command)).Success);
        Assert.True((await service.CancelLineAsync(command)).Success);
        Assert.Equal(ProductionDailyCommandStatus.IdempotencyConflict,
            (await service.CancelLineAsync(command with { ExpectedLineVersion = line.Version + 1 })).Status);
        Assert.Empty((await service.GetWeekAsync(weekId))!.Lines);
        Assert.True((await db.ProductionScheduleLines.SingleAsync(x => x.Id == line.Id)).IsCancelled);
        Assert.Single(await db.ProductionScheduleRevisions.Where(x => x.OperationId == operation).ToListAsync());
        if (publish)
            Assert.Equal(ProductionWorkOrderStatus.Cancelled,
                (await db.ProductionWorkOrders.SingleAsync(x => x.Id == line.WorkOrderId)).Status);
        var summary = await service.GetPlanSummaryAsync(weekId);
        Assert.All(summary, x => Assert.Equal(0, x.NewQuantity));
        using var workbook = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db,
            new ProductionDailyBalanceService(db)).ExportAsync(weekId))!));
        Assert.True(workbook.Worksheet(1).Cell(4, 2).IsEmpty());
    }

    [Theory]
    [InlineData(ProductionDailyArea.Cutting)]
    [InlineData(ProductionDailyArea.Sewing)]
    [InlineData(ProductionDailyArea.ReadyToPack)]
    public async Task Historical_unallocated_capture_blocks_every_line_of_the_same_sku(ProductionDailyArea area)
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(admin, "4826");
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var service = new ProductionDailyScheduleService(db, pins,
            new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var day = new DateOnly(2026, 9, 21);
        var weekId = (await service.CreateWeekAsync(new(Guid.NewGuid(), day, admin.Id))).Id!.Value;
        foreach (var quantity in new[] { 10m, 20m })
        {
            var week = (await service.GetWeekAsync(weekId))!;
            Assert.True((await service.SaveLineAsync(new(Guid.NewGuid(), weekId, null, week.Version,
                null, day, product.Id, quantity, null, null, null, null, admin.Id))).Success);
        }
        db.ProductionDailyCaptures.Add(new ProductionDailyCapture
        {
            WeekId = weekId, ProductId = product.Id, EffectiveDate = day,
            Area = area, StageId = area switch
            {
                ProductionDailyArea.Cutting => config.CuttingStageId!.Value,
                ProductionDailyArea.Sewing => config.SewingStageId!.Value,
                _ => config.ReadyToPackStageId!.Value
            },
            ShiftId = config.Shift1Id!.Value, Quantity = 10, ResponsibleUserId = admin.Id,
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('C', 64),
            Status = ProductionDailyCaptureStatus.Reversed
        });
        await db.SaveChangesAsync();
        var current = (await service.GetWeekAsync(weekId))!;
        var eligibility = await service.GetDeletionEligibilityAsync(weekId, current.Lines.Select(x => x.Id).ToArray());
        Assert.All(eligibility.Values, x => Assert.False(x.Allowed));
        foreach (var line in current.Lines)
        {
            var result = await service.CancelLineAsync(new(Guid.NewGuid(), weekId, line.Id,
                current.Version, line.Version, admin.Id));
            Assert.Equal(ProductionDailyCommandStatus.ValidationFailed, result.Status);
        }
        Assert.Equal(2, (await service.GetWeekAsync(weekId))!.Lines.Count);
    }

    private static WarehouseDbContext Context() => new(new DbContextOptionsBuilder<WarehouseDbContext>()
        .UseInMemoryDatabase($"ProductionCancel-{Guid.NewGuid():N}").Options);
}
