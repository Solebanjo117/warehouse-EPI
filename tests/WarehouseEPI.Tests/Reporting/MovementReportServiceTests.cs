using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;

namespace WarehouseEPI.Tests.Reporting;

public sealed class MovementReportServiceTests
{
    [Fact]
    public async Task GetMovementsPageAsync_paginates_and_sorts_chronologically_descending()
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Supervisor", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
        var product = new Product { Sku = "SKU-PAG", BaseUnitId = 1 };
        var loc = new Location { Code = "PAG-01", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(user, product, loc);

        var baseTime = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        for (var i = 1; i <= 35; i++)
        {
            var mov = new InventoryMovement
            {
                Type = InventoryMovementType.Entry,
                Purpose = InventoryMovementPurpose.Standard,
                ResponsibleUser = user,
                OccurredAt = baseTime.AddHours(i),
                Reference = $"REF-{i:00}",
                RequestFingerprint = $"fp-{i}"
            };
            mov.Lines.Add(new InventoryMovementLine { Product = product, UnitId = 1, DestinationLocation = loc, Quantity = i, LineNumber = 1 });
            db.InventoryMovements.Add(mov);
        }
        await db.SaveChangesAsync();

        var service = new MovementReportService(db);

        // Página 1 (tamaño 25) -> debe contener 25 items, el primero debe ser REF-35 (más reciente)
        var filterPage1 = new MovementReportFilter(PageNumber: 1, PageSize: 25);
        var page1 = await service.GetMovementsPageAsync(filterPage1);

        Assert.Equal(35, page1.TotalCount);
        Assert.Equal(25, page1.Items.Count);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal("REF-35", page1.Items[0].Reference);

        // Página 2 (tamaño 25) -> debe contener 10 items restantes, el último debe ser REF-01
        var filterPage2 = new MovementReportFilter(PageNumber: 2, PageSize: 25);
        var page2 = await service.GetMovementsPageAsync(filterPage2);

        Assert.Equal(35, page2.TotalCount);
        Assert.Equal(10, page2.Items.Count);
        Assert.Equal("REF-01", page2.Items[^1].Reference);
    }

    [Fact]
    public async Task GetMovementsForExportAsync_respects_max_rows_limit()
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Operador", PinLookup = "lk", PinHash = "ph", RoleId = 2 };
        var product = new Product { Sku = "SKU-EXP", BaseUnitId = 1 };
        var loc = new Location { Code = "EXP-01", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(user, product, loc);

        for (var i = 1; i <= 20; i++)
        {
            var mov = new InventoryMovement
            {
                Type = InventoryMovementType.Entry,
                Purpose = InventoryMovementPurpose.Standard,
                ResponsibleUser = user,
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(i),
                RequestFingerprint = $"fp-exp-{i}"
            };
            mov.Lines.Add(new InventoryMovementLine { Product = product, UnitId = 1, DestinationLocation = loc, Quantity = 1m, LineNumber = 1 });
            db.InventoryMovements.Add(mov);
        }
        await db.SaveChangesAsync();

        var service = new MovementReportService(db);
        var batch = await service.GetMovementsForExportAsync(new MovementReportFilter(), maxRows: 5);

        Assert.True(batch.ExceedsLimit);
        Assert.Empty(batch.Items);
        Assert.Equal(20, batch.TotalOperations);
        Assert.Equal(20, batch.TotalRows);
    }

    [Fact]
    public async Task GetMovementsForExportAsync_counts_detail_lines_instead_of_operations()
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Operador", PinLookup = "lk", PinHash = "ph", RoleId = 2 };
        var product = new Product { Sku = "SKU-MULTI", BaseUnitId = 1 };
        var location = new Location { Code = "MULTI-01", Kind = LocationKind.Rack };
        var movement = new InventoryMovement
        {
            Type = InventoryMovementType.Entry,
            ResponsibleUser = user,
            RequestFingerprint = "multi"
        };
        movement.Lines.Add(new InventoryMovementLine { Product = product, UnitId = 1, DestinationLocation = location, Quantity = 1m, LineNumber = 1 });
        movement.Lines.Add(new InventoryMovementLine { Product = product, UnitId = 1, DestinationLocation = location, Quantity = 2m, LineNumber = 2 });
        db.Add(movement);
        await db.SaveChangesAsync();

        var batch = await new MovementReportService(db)
            .GetMovementsForExportAsync(new MovementReportFilter(), maxRows: 1);

        Assert.True(batch.ExceedsLimit);
        Assert.Equal(1, batch.TotalOperations);
        Assert.Equal(2, batch.TotalRows);
        Assert.Empty(batch.Items);
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"MovementReportServiceTests-{Guid.NewGuid():N}")
            .Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
