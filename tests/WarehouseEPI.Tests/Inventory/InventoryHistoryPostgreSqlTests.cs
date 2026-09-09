using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Reporting;

namespace WarehouseEPI.Tests.Inventory;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class InventoryHistoryPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Movement_traceability_and_receiving_relation_translate_on_postgresql()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"PG-TRACE-{suffix}", $"PGT-{suffix}", "5197");
        await using var db = fixture.CreateDbContext();
        var product = await db.Products.SingleAsync(item => item.Id == seed.ProductId);
        var location = await db.Locations.SingleAsync(item => item.Id == seed.LocationId);
        var user = await db.Users.SingleAsync(item => item.FullName == $"Operador PG-TRACE-{suffix}");
        var movement = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString("N"),
            Type = InventoryMovementType.Entry,
            Purpose = InventoryMovementPurpose.DocumentReceipt,
            ResponsibleUser = user,
            OccurredAt = DateTimeOffset.UtcNow,
            RecordedAt = DateTimeOffset.UtcNow
        };
        movement.Lines.Add(new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            DestinationLocation = location,
            Quantity = 3,
            LineNumber = 1
        });
        db.Add(movement);
        await db.SaveChangesAsync();

        var document = new ReceivingDocument
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString("N"),
            Type = ReceivingDocumentType.PurchaseOrder,
            Number = $"PO-{suffix}",
            NormalizedNumber = $"PO-{suffix}",
            Origin = $"Proveedor {suffix}",
            NormalizedOrigin = $"PROVEEDOR {suffix}",
            Status = ReceivingDocumentStatus.PartiallyReceived,
            OpenedByUser = user,
            OpenedAt = DateTimeOffset.UtcNow
        };
        var confirmation = new ReceivingConfirmation
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString("N"),
            ReceivingDocument = document,
            InventoryMovement = movement,
            ResponsibleUser = user,
            OccurredAt = DateTimeOffset.UtcNow,
            RecordedAt = DateTimeOffset.UtcNow
        };
        confirmation.Lines.Add(new ReceivingConfirmationLine
        {
            InventoryMovementLine = movement.Lines.Single(),
            ExternalLotReference = $"ROLL-{suffix}"
        });
        db.AddRange(document, confirmation);
        await db.SaveChangesAsync();

        var result = await new MovementTraceabilityService(db).GetAsync(movement.Id);

        Assert.NotNull(result);
        Assert.Contains(result.Resources, item => item.Label.Contains($"PO-{suffix}") && item.Summary.Contains($"ROLL-{suffix}"));
        Assert.Contains(result.Events, item => item.Category == "Recepción");
    }

    [Fact]
    public async Task Audit_partial_filters_pagination_and_export_count_translate_on_postgresql()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"PG-HIST-{suffix}", $"HIST-{suffix}", "5179");
        await using var db = fixture.CreateDbContext();
        var product = await db.Products.SingleAsync(item => item.Id == seed.ProductId);
        var location = await db.Locations.SingleAsync(item => item.Id == seed.LocationId);
        var user = await db.Users.SingleAsync(item => item.FullName == $"Operador PG-HIST-{suffix}");
        product.ExternalReference = $"REF-{suffix}-TRACE";
        location.Description = $"Ubicación histórica {suffix}";
        db.ProductBarcodes.Add(new ProductBarcode { Product = product, Barcode = $"BC-{suffix}-HISTORY" });
        var movement = new InventoryMovement
        {
            Type = InventoryMovementType.Entry,
            Purpose = InventoryMovementPurpose.Standard,
            ResponsibleUser = user,
            OccurredAt = DateTimeOffset.UtcNow,
            RequestFingerprint = Guid.NewGuid().ToString("N")
        };
        movement.Lines.Add(new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            DestinationLocation = location,
            Quantity = 2m,
            LineNumber = 1
        });
        db.Add(movement);
        await db.SaveChangesAsync();

        var filter = new InventoryHistoryFilter(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1),
            InventoryMovementType.Entry, null, null, null, user.Id,
            Purpose: InventoryMovementPurpose.Standard,
            ProductSearch: $"{suffix.ToLowerInvariant()}-trace",
            LocationSearch: $"histórica {suffix.ToLowerInvariant()}");
        var service = new InventoryHistoryService(db);
        var page = await service.SearchAsync(filter, 1, 25);
        var export = await service.GetTraceExportAsync(filter, "America/Matamoros", 10000);

        Assert.Contains(page.Items, item => item.Id == movement.Id);
        Assert.Contains(export.Items, item => item.MovementId == movement.Id);
        Assert.Equal(export.Items.Count, export.TotalRows);
    }
}
