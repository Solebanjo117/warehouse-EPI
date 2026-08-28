using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Labels;

public sealed class PalletLicensePlateRecentTests
{
    [Fact]
    public async Task Recent_lists_only_eligible_entries_newest_first_and_honours_the_limit()
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Operadora", PinLookup = "plt-recent", PinHash = "hash", RoleId = 1 };
        var product = new Product { Sku = "SKU-PLT", Description = "Caja maestra", BaseUnitId = 1 };
        var location = new Location { Code = "A-1-8", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(user, product, location);

        var older = Movement("plt-older", InventoryMovementType.Entry, InventoryMovementPurpose.Standard, user, product, location, 1);
        var newer = Movement("plt-newer", InventoryMovementType.Entry, InventoryMovementPurpose.Standard, user, product, location, 2);
        var exit = Movement("plt-exit", InventoryMovementType.Exit, InventoryMovementPurpose.Standard, user, product, location, 3);
        var wip = Movement("plt-wip", InventoryMovementType.Entry, InventoryMovementPurpose.WipWarehouseReturn, user, product, location, 4);
        var corrected = Movement("plt-corrected", InventoryMovementType.Entry, InventoryMovementPurpose.Standard, user, product, location, 5);
        var reversal = Movement("plt-reversal", InventoryMovementType.Entry, InventoryMovementPurpose.Standard, user, product, location, 6);
        db.AddRange(older, newer, exit, wip, corrected, reversal);
        db.Add(new InventoryMovementCorrection
        {
            OriginalMovement = corrected,
            ReversalMovement = reversal,
            RequestedByUser = user,
            AuthorizedByUser = user,
            RequestFingerprint = "plt-correction",
            Reason = "Cantidad equivocada"
        });
        await db.SaveChangesAsync();

        var service = new PalletLicensePlateService(db);
        var recent = await service.RecentAsync(8);

        Assert.Equal([newer.Id, older.Id], recent.Select(item => item.MovementId).ToArray());
        Assert.Equal($"PLT-{newer.Id:N}".ToUpperInvariant(), recent[0].Identifier);
        Assert.Equal("SKU-PLT", recent[0].Sku);
        Assert.Equal("A-1-8", recent[0].Destination);
        Assert.Equal("Operadora", recent[0].Responsible);

        Assert.Single(await service.RecentAsync(1));
    }

    private static InventoryMovement Movement(string fingerprint, InventoryMovementType type, InventoryMovementPurpose purpose,
        User user, Product product, Location location, int day)
    {
        var movement = new InventoryMovement
        {
            Type = type,
            Purpose = purpose,
            ResponsibleUser = user,
            OccurredAt = new DateTimeOffset(2026, 8, day, 12, 0, 0, TimeSpan.Zero),
            RequestFingerprint = fingerprint
        };
        movement.Lines.Add(new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            SourceLocation = type == InventoryMovementType.Exit ? location : null,
            DestinationLocation = type == InventoryMovementType.Entry ? location : null,
            Quantity = 25m,
            LineNumber = 1
        });
        return movement;
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"PalletLicensePlateRecentTests-{Guid.NewGuid():N}").Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
