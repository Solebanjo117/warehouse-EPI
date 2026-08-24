using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Inventory;

public sealed class InventoryHistoryServiceTests
{
    [Fact]
    public async Task Audit_filters_keep_the_full_correction_chain_and_combine_partial_terms()
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Auditora", PinLookup = "audit", PinHash = "hash", RoleId = 1 };
        var product = new Product { Sku = "SKU-AUDIT", Description = "Guante térmico", ExternalReference = "EXT-TRACE-77", BaseUnitId = 1 };
        product.Barcodes.Add(new ProductBarcode { Barcode = "BC-AUDIT-001" });
        var location = new Location { Code = "RACK-AUDIT-01", Description = "Zona fría especial", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(user, product, location);

        var original = Movement("original", InventoryMovementType.Entry, user, product, location);
        var reversal = Movement("reversal", InventoryMovementType.Exit, user, product, location);
        var replacement = Movement("replacement", InventoryMovementType.Entry, user, product, location);
        var current = Movement("current", InventoryMovementType.Entry, user, product, location);
        db.AddRange(original, reversal, replacement, current, new InventoryMovementCorrection
        {
            Type = InventoryMovementCorrectionType.Replacement,
            OriginalMovement = original,
            ReversalMovement = reversal,
            ReplacementMovement = replacement,
            Reason = "Corrección de prueba",
            RequestedByUser = user,
            AuthorizedByUser = user,
            RequestFingerprint = "audit-correction"
        });
        await db.SaveChangesAsync();

        var service = new InventoryHistoryService(db);
        var common = new InventoryHistoryFilter(null, null, null, null, null, null, user.Id,
            Purpose: InventoryMovementPurpose.Standard, ProductSearch: "trace-7", LocationSearch: "FRÍA");

        var all = await service.SearchAsync(common, 1, 25);
        Assert.Equal(4, all.TotalCount);
        Assert.Contains(all.Items, row => row.Status == "Original corregido");
        Assert.Contains(all.Items, row => row.Status == "Reverso");
        Assert.Contains(all.Items, row => row.Status == "Reemplazo");
        Assert.Contains(all.Items, row => row.Status == "Sin relación con corrección");

        var originalOnly = await service.SearchAsync(common with { State = InventoryHistoryCorrectionState.CorrectedOriginal }, 1, 25);
        Assert.Equal(original.Id, Assert.Single(originalOnly.Items).Id);
        var reversalOnly = await service.SearchAsync(common with { State = InventoryHistoryCorrectionState.Reversal }, 1, 25);
        Assert.Equal(reversal.Id, Assert.Single(reversalOnly.Items).Id);
        var replacementOnly = await service.SearchAsync(common with { State = InventoryHistoryCorrectionState.Replacement }, 1, 25);
        Assert.Equal(replacement.Id, Assert.Single(replacementOnly.Items).Id);
        var unrelatedOnly = await service.SearchAsync(common with { State = InventoryHistoryCorrectionState.Current }, 1, 25);
        Assert.Equal(current.Id, Assert.Single(unrelatedOnly.Items).Id);
    }

    [Fact]
    public async Task Audit_export_counts_real_rows_and_rejects_the_complete_file_above_the_limit()
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Auditora", PinLookup = "audit-limit", PinHash = "hash", RoleId = 1 };
        var product = new Product { Sku = "SKU-LIMIT", BaseUnitId = 1 };
        var location = new Location { Code = "LIMIT-01", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(user, product, location);
        db.Add(Movement("limit-1", InventoryMovementType.Entry, user, product, location));
        db.Add(Movement("limit-2", InventoryMovementType.Entry, user, product, location));
        await db.SaveChangesAsync();

        var service = new InventoryHistoryService(db);
        var rejected = await service.GetTraceExportAsync(new(null, null, null, null, null, null, null), "America/Matamoros", 1);
        Assert.True(rejected.ExceedsLimit);
        Assert.Empty(rejected.Items);
        Assert.Equal(2, rejected.TotalOperations);
        Assert.Equal(2, rejected.TotalRows);

        var accepted = await service.GetTraceExportAsync(new(null, null, null, null, null, null, null), "America/Matamoros", 2);
        Assert.False(accepted.ExceedsLimit);
        Assert.Equal(2, accepted.Items.Count);
    }

    private static InventoryMovement Movement(string fingerprint, InventoryMovementType type, User user, Product product, Location location)
    {
        var movement = new InventoryMovement
        {
            Type = type,
            Purpose = InventoryMovementPurpose.Standard,
            ResponsibleUser = user,
            OccurredAt = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero),
            RequestFingerprint = fingerprint
        };
        movement.Lines.Add(new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            SourceLocation = type == InventoryMovementType.Exit ? location : null,
            DestinationLocation = type == InventoryMovementType.Entry ? location : null,
            Quantity = 1m,
            LineNumber = 1
        });
        return movement;
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"InventoryHistoryServiceTests-{Guid.NewGuid():N}").Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
