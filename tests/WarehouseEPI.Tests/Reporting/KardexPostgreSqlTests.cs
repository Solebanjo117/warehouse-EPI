using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Reporting;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class KardexPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Kardex_aggregates_and_paginates_on_postgresql()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"PG-KDX-{suffix}", $"KDX-{suffix}", "4388");
        await using var db = fixture.CreateDbContext();
        var product = await db.Products.SingleAsync(p => p.Id == seed.ProductId);
        var location = await db.Locations.SingleAsync(l => l.Id == seed.LocationId);
        var user = await db.Users.SingleAsync(u => u.FullName == $"Operador PG-KDX-{suffix}");
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        AddEntry(10m, occurredAt, "pg-kdx-1");
        AddExit(4m, occurredAt.AddMinutes(1), "pg-kdx-2");
        db.InventoryBalances.Add(new InventoryBalance { Product = product, Location = location, Quantity = 6m });
        await db.SaveChangesAsync();

        var result = await new KardexReportService(db).GetKardexPageAsync(new KardexFilter(
            product.Id, location.Id, occurredAt.AddMinutes(-1), occurredAt.AddMinutes(2),
            PageNumber: 2, PageSize: 1, TimeZoneId: "UTC"));

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalLines);
        Assert.Equal(10m, result.BalanceBeforePage);
        Assert.Equal(10m, result.Summary.TotalEntries);
        Assert.Equal(4m, result.Summary.TotalExits);
        Assert.Equal(6m, Assert.Single(result.Rows).RunningBalance);

        void AddEntry(decimal quantity, DateTimeOffset at, string fingerprint)
        {
            var movement = Movement(InventoryMovementType.Entry, InventoryMovementPurpose.Standard, at, fingerprint);
            var line = new InventoryMovementLine { Movement = movement, Product = product, UnitId = 1,
                DestinationLocation = location, Quantity = quantity, LineNumber = 1 };
            line.BalanceChanges.Add(new InventoryBalanceChange { MovementLine = line, Location = location,
                DeltaQuantity = quantity, PreviousQuantity = 0m, ResultingQuantity = quantity });
            movement.Lines.Add(line);
            db.Add(movement);
        }

        void AddExit(decimal quantity, DateTimeOffset at, string fingerprint)
        {
            var movement = Movement(InventoryMovementType.Exit, InventoryMovementPurpose.GeneralExit, at, fingerprint);
            var line = new InventoryMovementLine { Movement = movement, Product = product, UnitId = 1,
                SourceLocation = location, Quantity = quantity, LineNumber = 1 };
            line.BalanceChanges.Add(new InventoryBalanceChange { MovementLine = line, Location = location,
                DeltaQuantity = -quantity, PreviousQuantity = 10m, ResultingQuantity = 6m });
            movement.Lines.Add(line);
            db.Add(movement);
        }

        InventoryMovement Movement(InventoryMovementType type, InventoryMovementPurpose purpose,
            DateTimeOffset at, string fingerprint) => new()
        {
            OperationId = Guid.NewGuid(), RequestFingerprint = $"{fingerprint}-{suffix}", Type = type,
            Purpose = purpose, ResponsibleUser = user, OccurredAt = at, RecordedAt = at
        };
    }
}
