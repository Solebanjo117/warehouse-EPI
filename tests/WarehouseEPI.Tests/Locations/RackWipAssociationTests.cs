using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Tests.Imports;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Tests.Locations;

public sealed class RackWipAssociationTests
{
    internal static async Task<(User User, Location Area, Location Other, Location Position, Product Product)> SeedAsync(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        var user = new User { FullName = "Rack association admin", RoleId = 1, PinHash = "", PinLookup = "" };
        await WipTransferImportTests.Pins(db).AssignAsync(user, "1234");
        var area = await db.Locations.SingleOrDefaultAsync(x => x.Code == "WIP-2")
            ?? new Location { Code = "WIP-2", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var other = await db.Locations.SingleOrDefaultAsync(x => x.Code == "WIP-3")
            ?? new Location { Code = "WIP-3", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        if (db.Entry(area).State == EntityState.Detached) db.Add(area);
        if (db.Entry(other).State == EntityState.Detached) db.Add(other);
        var position = Position(5, 1, LocationOperationalRole.Wip);
        var storage = Position(5, 2, LocationOperationalRole.Storage);
        var product = new Product { Sku = "ASSOCIATION-PART", BaseUnitId = 1 };
        db.AddRange(user, position, storage, Position(6, 1, LocationOperationalRole.Wip),
            Position(6, 2, LocationOperationalRole.Storage), product);
        db.Add(new InventoryBalance { Product = product, Location = storage, Quantity = 42 });
        db.Add(new ProductLocationAssignment { Product = product, Location = storage });
        await db.SaveChangesAsync();
        return (user, area, other, position, product);
    }

    private static Location Position(short rack, short pallet, LocationOperationalRole role) => new()
    { Code = $"M-{rack}-{pallet}", RowCode = "M", RackNumber = rack, PalletNumber = pallet, Kind = LocationKind.Rack, OperationalRole = role };

    internal static LocationRackEditCommand Command(Guid user, Guid? area, short rack = 5, Guid? previous = null) =>
        new(Guid.NewGuid(), user, "M", rack, LocationOperationalRole.Storage, [1, 2], "Connect shared WIP", "1234",
            WipPallets: [1], WipAssociation: new(area, previous));

    [Fact]
    public async Task Shared_report_and_exports_count_delivery_once_and_keep_position_history_separate()
    {
        await using var db = WipTransferImportTests.Db();
        var (user, area, other, position, product) = await SeedAsync(db);
        var service = new LocationRackAdministrationService(db, WipTransferImportTests.Pins(db), TimeProvider.System);
        foreach (var rack in new short[] { 5, 6 })
            Assert.Equal(LocationRackSaveStatus.Success, (await service.SaveAsync(Command(user.Id, area.Id, rack))).Status);
        db.WipDocuments.AddRange(new WipDocument { Product = product, WipLocation = area, ResponsibleUser = user, OccurredAt = new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero), Quantity = 100 },
            new WipDocument { Product = product, WipLocation = position, ResponsibleUser = user, OccurredAt = new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero), Quantity = 7 });
        await db.SaveChangesAsync();
        var clock = new WarehouseClock(new WarehouseSettingsService(db));
        var reports = new WipReportService(db, clock);
        foreach (var rack in new short[] { 5, 6 })
        {
            var association = await LocationRackWipAssociations.GetAsync(db, "M", rack);
            var report = await reports.GetTrackedPageAsync(new(null, null, WipAreaId: association!.WipAreaId), 1, 25);
            Assert.Equal(100, Assert.Single(report.Inventory).Delivered);
        }
        Assert.Equal(7, Assert.Single((await reports.GetTrackedPageAsync(new(null, null, WipAreaId: position.Id), 1, 25)).Inventory).Delivered);
        var export = new WarehouseEPI.Web.Pages.Admin.Reports.Wip.ExportModel(reports, clock, new PassthroughStringLocalizer<CatalogTexts>(), new WarehouseSettingsService(db), TimeProvider.System, db);
        var csv = Assert.IsType<FileContentResult>(await export.OnGetAsync("csv", null, null, null, area.Id, default));
        Assert.Contains("\"100\"", Encoding.UTF8.GetString(csv.FileContents));
        var xlsx = Assert.IsType<FileContentResult>(await export.OnGetAsync("xlsx", null, null, null, area.Id, default));
        using var book = new XLWorkbook(new MemoryStream(xlsx.FileContents));
        Assert.Equal(2, book.Worksheet("WIP").LastRowUsed()!.RowNumber());
        Assert.Equal(100, book.Worksheet("WIP").Cell(2, 8).GetValue<decimal>());
        Assert.Equal(LocationRackSaveStatus.Success, (await service.SaveAsync(Command(user.Id, other.Id, previous: area.Id))).Status);
        Assert.Equal(100, Assert.Single((await reports.GetTrackedPageAsync(new(null, null, WipAreaId: area.Id), 1, 25)).Inventory).Delivered);
        Assert.Equal(42, await db.InventoryBalances.SumAsync(x => x.Quantity));
        Assert.True((await db.ProductLocationAssignments.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Association_is_audited_idempotent_and_rejects_stale_edits()
    {
        await using var db = WipTransferImportTests.Db();
        var (user, area, other, _, _) = await SeedAsync(db);
        var service = new LocationRackAdministrationService(db, WipTransferImportTests.Pins(db), TimeProvider.System);
        var command = Command(user.Id, area.Id);
        Assert.Equal(LocationRackSaveStatus.InvalidPin, (await service.SaveAsync(command with { Pin = "0000" })).Status);
        Assert.Equal(LocationRackSaveStatus.Unauthorized, (await service.SaveAsync(command with { RequestedByUserId = Guid.NewGuid() })).Status);
        Assert.Equal(LocationRackSaveStatus.Success, (await service.SaveAsync(command)).Status);
        Assert.Equal(LocationRackSaveStatus.Success, (await service.SaveAsync(command)).Status);
        Assert.Equal(LocationRackSaveStatus.IdempotencyConflict, (await service.SaveAsync(command with { WipAssociation = new(other.Id, null) })).Status);
        Assert.Equal(LocationRackSaveStatus.ValidationFailed, (await service.SaveAsync(Command(user.Id, other.Id))).Status);
        var revision = await db.LocationRackRevisions.SingleAsync();
        Assert.Contains(area.Id.ToString(), revision.AfterJson);
        var removal = Command(user.Id, area.Id, previous: area.Id) with { WipPallets = [] };
        var review = await service.ReviewAsync(removal);
        Assert.Empty(review.Errors);
        Assert.Equal(area.Id, review.Summary.PreviousWipAreaId);
        Assert.Null(review.Summary.RequestedWipAreaId);
        Assert.Equal(LocationRackSaveStatus.Success, (await service.SaveAsync(removal)).Status);
        Assert.Empty(await db.LocationRackWipAssociations.ToListAsync());
    }

    [Fact]
    public async Task Invalid_targets_are_rejected_and_unavailable_current_target_remains_visible()
    {
        await using var db = WipTransferImportTests.Db();
        var (user, area, _, position, _) = await SeedAsync(db);
        var service = new LocationRackAdministrationService(db, WipTransferImportTests.Pins(db), TimeProvider.System);
        foreach (var id in new[] { Guid.NewGuid(), position.Id })
            Assert.Equal(LocationRackSaveStatus.ValidationFailed, (await service.SaveAsync(Command(user.Id, id))).Status);
        area.IsBlocked = true; await db.SaveChangesAsync();
        Assert.Equal(LocationRackSaveStatus.ValidationFailed, (await service.SaveAsync(Command(user.Id, area.Id))).Status);
        area.IsBlocked = false; await db.SaveChangesAsync();
        Assert.Equal(LocationRackSaveStatus.ValidationFailed, (await service.SaveAsync(Command(user.Id, area.Id) with { WipPallets = [] })).Status);
        Assert.Equal(LocationRackSaveStatus.Success, (await service.SaveAsync(Command(user.Id, area.Id))).Status);
        area.IsActive = false; await db.SaveChangesAsync();
        Assert.False((await service.GetAsync("M", 5))!.WipAssociation!.IsOperational);
        Assert.Equal(LocationRackSaveStatus.Success, (await service.SaveAsync(Command(user.Id, area.Id, previous: area.Id))).Status);
        Assert.NotNull(await LocationWipRules.ValidateChangeAsync(db, area.Id, area.OperationalRole, LocationOperationalRole.Storage, default));
        var areas = new LocationAreaAdministrationService(db, WipTransferImportTests.Pins(db), TimeProvider.System);
        Assert.False((await areas.GetDeletionStateAsync(area.Id))!.CanDelete);
        Assert.Equal(LocationAreaDeleteStatus.ValidationFailed, (await areas.DeleteAsync(
            new(Guid.NewGuid(), user.Id, area.Id, "Remove associated area", "1234", area.Code))).Status);
        Assert.Equal(LocationRackSaveStatus.Success, (await service.SaveAsync(Command(user.Id, null, previous: area.Id))).Status);
        Assert.True((await areas.GetDeletionStateAsync(area.Id))!.CanDelete);
        Assert.Empty(await db.WipDocuments.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }
}
