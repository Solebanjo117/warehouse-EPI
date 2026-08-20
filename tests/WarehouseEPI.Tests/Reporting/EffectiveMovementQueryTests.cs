using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;

namespace WarehouseEPI.Tests.Reporting;

public sealed class EffectiveMovementQueryTests
{
    [Fact]
    public async Task Excludes_corrected_original_and_reversal_movements_while_including_active_replacements()
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Operador", PinLookup = "lookup", PinHash = "hash", RoleId = 2 };
        var product = new Product { Sku = "SKU-001", Description = "Producto 1", BaseUnitId = 1 };
        var loc = new Location { Code = "A-1-1", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(user, product, loc);

        // Movimiento normal no corregido (vigente)
        var normal = new InventoryMovement
        {
            Type = InventoryMovementType.Entry,
            Purpose = InventoryMovementPurpose.Standard,
            ResponsibleUser = user,
            OccurredAt = DateTimeOffset.UtcNow.AddHours(-10),
            RequestFingerprint = "fp1"
        };
        normal.Lines.Add(new InventoryMovementLine { Product = product, UnitId = 1, DestinationLocation = loc, Quantity = 10m, LineNumber = 1 });

        // Movimiento original que fue corregido
        var original = new InventoryMovement
        {
            Type = InventoryMovementType.Entry,
            Purpose = InventoryMovementPurpose.Standard,
            ResponsibleUser = user,
            OccurredAt = DateTimeOffset.UtcNow.AddHours(-8),
            RequestFingerprint = "fp2"
        };
        original.Lines.Add(new InventoryMovementLine { Product = product, UnitId = 1, DestinationLocation = loc, Quantity = 5m, LineNumber = 1 });

        // Movimiento de reverso de la corrección
        var reversal = new InventoryMovement
        {
            Type = InventoryMovementType.Exit,
            Purpose = InventoryMovementPurpose.Standard,
            ResponsibleUser = user,
            OccurredAt = DateTimeOffset.UtcNow.AddHours(-7),
            RequestFingerprint = "fp3"
        };
        reversal.Lines.Add(new InventoryMovementLine { Product = product, UnitId = 1, SourceLocation = loc, Quantity = 5m, LineNumber = 1 });

        // Movimiento de reemplazo de la corrección (vigente)
        var replacement = new InventoryMovement
        {
            Type = InventoryMovementType.Entry,
            Purpose = InventoryMovementPurpose.Standard,
            ResponsibleUser = user,
            OccurredAt = DateTimeOffset.UtcNow.AddHours(-6),
            RequestFingerprint = "fp4"
        };
        replacement.Lines.Add(new InventoryMovementLine { Product = product, UnitId = 1, DestinationLocation = loc, Quantity = 8m, LineNumber = 1 });

        // Registro de corrección
        var correction = new InventoryMovementCorrection
        {
            Type = InventoryMovementCorrectionType.Replacement,
            OriginalMovement = original,
            ReversalMovement = reversal,
            ReplacementMovement = replacement,
            Reason = "Error en conteo de entrada",
            RequestedByUser = user,
            AuthorizedByUser = user,
            RequestFingerprint = "fp-corr-1"
        };

        db.AddRange(normal, original, reversal, replacement, correction);
        await db.SaveChangesAsync();

        var effectiveMovements = await db.InventoryMovements
            .AsNoTracking()
            .WhereEffective(db)
            .OrderBy(m => m.OccurredAt)
            .ToListAsync();

        Assert.Equal(2, effectiveMovements.Count);
        Assert.Contains(effectiveMovements, m => m.Id == normal.Id);
        Assert.Contains(effectiveMovements, m => m.Id == replacement.Id);
        Assert.DoesNotContain(effectiveMovements, m => m.Id == original.Id);
        Assert.DoesNotContain(effectiveMovements, m => m.Id == reversal.Id);
    }

    [Fact]
    public async Task Excludes_intermediate_replacements_in_chained_corrections()
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Operador", PinLookup = "lookup", PinHash = "hash", RoleId = 2 };
        var product = new Product { Sku = "SKU-CHAIN", BaseUnitId = 1 };
        var loc = new Location { Code = "B-1-1", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(user, product, loc);

        var m1 = new InventoryMovement { Type = InventoryMovementType.Entry, ResponsibleUser = user, RequestFingerprint = "fp1" };
        var rev1 = new InventoryMovement { Type = InventoryMovementType.Exit, ResponsibleUser = user, RequestFingerprint = "fp2" };
        var rep1 = new InventoryMovement { Type = InventoryMovementType.Entry, ResponsibleUser = user, RequestFingerprint = "fp3" };

        var corr1 = new InventoryMovementCorrection
        {
            Type = InventoryMovementCorrectionType.Replacement,
            OriginalMovement = m1,
            ReversalMovement = rev1,
            ReplacementMovement = rep1,
            Reason = "Corrección 1",
            RequestedByUser = user,
            AuthorizedByUser = user,
            RequestFingerprint = "c1"
        };

        // Segunda corrección sobre el reemplazo rep1
        var rev2 = new InventoryMovement { Type = InventoryMovementType.Exit, ResponsibleUser = user, RequestFingerprint = "fp4" };
        var rep2 = new InventoryMovement { Type = InventoryMovementType.Entry, ResponsibleUser = user, RequestFingerprint = "fp5" };

        var corr2 = new InventoryMovementCorrection
        {
            Type = InventoryMovementCorrectionType.Replacement,
            OriginalMovement = rep1, // rep1 ahora es original en la segunda corrección
            ReversalMovement = rev2,
            ReplacementMovement = rep2,
            Reason = "Corrección 2",
            RequestedByUser = user,
            AuthorizedByUser = user,
            RequestFingerprint = "c2"
        };

        db.AddRange(m1, rev1, rep1, corr1, rev2, rep2, corr2);
        await db.SaveChangesAsync();

        var effectiveMovements = await db.InventoryMovements
            .AsNoTracking()
            .WhereEffective(db)
            .ToListAsync();

        var single = Assert.Single(effectiveMovements);
        Assert.Equal(rep2.Id, single.Id);
    }

    [Fact]
    public async Task Pure_cancellation_excludes_both_original_and_reversal_with_no_replacement()
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Operador", PinLookup = "lookup", PinHash = "hash", RoleId = 2 };
        var product = new Product { Sku = "SKU-CANCEL", BaseUnitId = 1 };
        var loc = new Location { Code = "C-1-1", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        db.AddRange(user, product, loc);

        var m1 = new InventoryMovement { Type = InventoryMovementType.Entry, ResponsibleUser = user, RequestFingerprint = "fp1" };
        var rev1 = new InventoryMovement { Type = InventoryMovementType.Exit, ResponsibleUser = user, RequestFingerprint = "fp2" };

        var corr = new InventoryMovementCorrection
        {
            Type = InventoryMovementCorrectionType.Reversal,
            OriginalMovement = m1,
            ReversalMovement = rev1,
            ReplacementMovement = null, // Anulación pura
            Reason = "Movimiento cancelado por error",
            RequestedByUser = user,
            AuthorizedByUser = user,
            RequestFingerprint = "c-cancel"
        };

        db.AddRange(m1, rev1, corr);
        await db.SaveChangesAsync();

        var effective = await db.InventoryMovements
            .AsNoTracking()
            .WhereEffective(db)
            .ToListAsync();

        Assert.Empty(effective);
    }

    [Fact]
    public async Task ApplyFilter_applies_date_range_purpose_sku_and_location_criteria()
    {
        await using var db = CreateDbContext();
        var user1 = new User { FullName = "Juan Perez", PinLookup = "lk1", PinHash = "h1", RoleId = 2 };
        var user2 = new User { FullName = "Maria Gomez", PinLookup = "lk2", PinHash = "h2", RoleId = 2 };
        var prodA = new Product { Sku = "PROD-ALPHA", Description = "Tornillo 10mm", BaseUnitId = 1 };
        var prodB = new Product { Sku = "PROD-BETA", Description = "Tuerca 10mm", BaseUnitId = 1 };
        var loc1 = new Location { Code = "RACK-01", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        var loc2 = new Location { Code = "RACK-02", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
        var wipArea = new Location { Code = "WIP-2", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };

        db.AddRange(user1, user2, prodA, prodB, loc1, loc2, wipArea);

        var baseTime = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

        var mov1 = new InventoryMovement
        {
            Type = InventoryMovementType.Entry,
            Purpose = InventoryMovementPurpose.Standard,
            ResponsibleUser = user1,
            OccurredAt = baseTime.AddDays(1),
            Reference = "REF-100",
            Notes = "Carga normal",
            RequestFingerprint = "f1"
        };
        mov1.Lines.Add(new InventoryMovementLine { Product = prodA, UnitId = 1, DestinationLocation = loc1, Quantity = 50m, LineNumber = 1 });

        var mov2 = new InventoryMovement
        {
            Type = InventoryMovementType.Exit,
            Purpose = InventoryMovementPurpose.ProductionIssue,
            OperationalArea = wipArea,
            ResponsibleUser = user2,
            OccurredAt = baseTime.AddDays(5),
            Reference = "WO-500",
            Notes = "Surtimiento a linea",
            RequestFingerprint = "f2"
        };
        mov2.Lines.Add(new InventoryMovementLine { Product = prodB, UnitId = 1, SourceLocation = loc2, Quantity = 20m, LineNumber = 1 });

        var mov3 = new InventoryMovement
        {
            Type = InventoryMovementType.Transfer,
            Purpose = InventoryMovementPurpose.Standard,
            ResponsibleUser = user1,
            OccurredAt = baseTime.AddDays(10),
            Reference = "TR-200",
            RequestFingerprint = "f3"
        };
        mov3.Lines.Add(new InventoryMovementLine { Product = prodA, UnitId = 1, SourceLocation = loc1, DestinationLocation = loc2, Quantity = 10m, LineNumber = 1 });

        db.AddRange(mov1, mov2, mov3);
        await db.SaveChangesAsync();

        // 1. Filtrar por rango de fechas [baseTime + 2 días, baseTime + 8 días) -> solo mov2
        var dateFilter = new MovementReportFilter(FromUtc: baseTime.AddDays(2), ToUtc: baseTime.AddDays(8));
        var dateResults = await db.InventoryMovements.ApplyFilter(db, dateFilter).ToListAsync();
        Assert.Single(dateResults);
        Assert.Equal(mov2.Id, dateResults[0].Id);

        // 2. Filtrar por fragmento de SKU sin distinguir mayúsculas -> mov1 y mov3
        var skuFilter = new MovementReportFilter(Sku: "prod-alph");
        var skuResults = await db.InventoryMovements.ApplyFilter(db, skuFilter).ToListAsync();
        Assert.Equal(2, skuResults.Count);

        // 3. Filtrar por Propósito ProductionIssue -> mov2
        var purposeFilter = new MovementReportFilter(Purpose: InventoryMovementPurpose.ProductionIssue);
        var purposeResults = await db.InventoryMovements.ApplyFilter(db, purposeFilter).ToListAsync();
        Assert.Single(purposeResults);
        Assert.Equal(mov2.Id, purposeResults[0].Id);

        // 4. Filtrar por fragmento de ubicación -> mov2 (source) y mov3 (destination)
        var locFilter = new MovementReportFilter(LocationCode: "ack-02");
        var locResults = await db.InventoryMovements.ApplyFilter(db, locFilter).ToListAsync();
        Assert.Equal(2, locResults.Count);

        // 5. Búsqueda por texto "Tornillo" -> mov1 y mov3 (ambos contienen prodA)
        var searchFilter = new MovementReportFilter(Search: "tornillo");
        var searchResults = await db.InventoryMovements.ApplyFilter(db, searchFilter).ToListAsync();
        Assert.Equal(2, searchResults.Count);

        // 6. Búsqueda por nota única "Carga normal" -> solo mov1
        var noteFilter = new MovementReportFilter(Search: "Carga normal");
        var noteResults = await db.InventoryMovements.ApplyFilter(db, noteFilter).ToListAsync();
        Assert.Single(noteResults);
        Assert.Equal(mov1.Id, noteResults[0].Id);

        // 7. Los filtros dedicados se combinan con AND
        var combinedFilter = new MovementReportFilter(Sku: "alpha", LocationCode: "rack-02");
        var combinedResults = await db.InventoryMovements.ApplyFilter(db, combinedFilter).ToListAsync();
        Assert.Single(combinedResults);
        Assert.Equal(mov3.Id, combinedResults[0].Id);

        var unrelatedResults = await db.InventoryMovements
            .ApplyFilter(db, new MovementReportFilter(Sku: "NO-EXISTE"))
            .ToListAsync();
        Assert.Empty(unrelatedResults);
    }

    [Theory]
    [InlineData("widget-alpha")]
    [InlineData("protección térmica")]
    [InlineData("ext-420")]
    [InlineData("789012")]
    public async Task Product_filter_matches_partial_product_identifiers_case_insensitively(string term)
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Operador", PinLookup = "lookup", PinHash = "hash", RoleId = 2 };
        var product = new Product
        {
            Sku = "SKU-WIDGET-ALPHA",
            Description = "Protección Térmica Industrial",
            ExternalReference = "EXT-420-Z",
            BaseUnitId = 1
        };
        product.Barcodes.Add(new ProductBarcode { Barcode = "7507890123456", IsPrimary = true });
        var location = new Location { Code = "RACK-PRODUCT", Kind = LocationKind.Rack };
        var movement = new InventoryMovement
        {
            Type = InventoryMovementType.Entry,
            ResponsibleUser = user,
            RequestFingerprint = "partial-product"
        };
        movement.Lines.Add(new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            DestinationLocation = location,
            Quantity = 1m,
            LineNumber = 1
        });
        db.Add(movement);
        await db.SaveChangesAsync();

        var results = await db.InventoryMovements
            .ApplyFilter(db, new MovementReportFilter(Sku: term))
            .Select(item => item.Id)
            .ToListAsync();

        Assert.Equal(movement.Id, Assert.Single(results));
    }

    [Theory]
    [InlineData("src-part")]
    [InlineData("materia prima")]
    [InlineData("dst-part")]
    [InlineData("producto terminado")]
    [InlineData("wip-part")]
    [InlineData("línea pintura")]
    [InlineData("audit-part")]
    [InlineData("reserva especial")]
    public async Task Location_filter_matches_partial_codes_descriptions_and_balance_change_locations(string term)
    {
        await using var db = CreateDbContext();
        var user = new User { FullName = "Operador", PinLookup = "lookup", PinHash = "hash", RoleId = 2 };
        var product = new Product { Sku = "SKU-LOCATION", BaseUnitId = 1 };
        var source = new Location { Code = "SRC-PART-01", Description = "Materia Prima", Kind = LocationKind.Rack };
        var destination = new Location { Code = "DST-PART-02", Description = "Producto Terminado", Kind = LocationKind.Rack };
        var operationalArea = new Location { Code = "WIP-PART-03", Description = "Línea Pintura", Kind = LocationKind.Area };
        var balanceLocation = new Location { Code = "AUDIT-PART-04", Description = "Reserva Especial", Kind = LocationKind.Rack };
        var movement = new InventoryMovement
        {
            Type = InventoryMovementType.Transfer,
            ResponsibleUser = user,
            OperationalArea = operationalArea,
            RequestFingerprint = "partial-location"
        };
        var line = new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            SourceLocation = source,
            DestinationLocation = destination,
            Quantity = 1m,
            LineNumber = 1
        };
        line.BalanceChanges.Add(new InventoryBalanceChange
        {
            Location = balanceLocation,
            PreviousQuantity = 5m,
            DeltaQuantity = -1m,
            ResultingQuantity = 4m
        });
        movement.Lines.Add(line);
        db.Add(movement);
        await db.SaveChangesAsync();

        var results = await db.InventoryMovements
            .ApplyFilter(db, new MovementReportFilter(LocationCode: term))
            .Select(item => item.Id)
            .ToListAsync();

        Assert.Equal(movement.Id, Assert.Single(results));
    }

    [Fact]
    public void ProjectToRowDto_extracts_distinct_skus_and_lines_correctly()
    {
        var user = new User { FullName = "Supervisor", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
        var unit = new Unit { Id = 1, Code = "EA", Name = "Pieza", AllowsDecimals = false, IsActive = true };
        var prodA = new Product { Sku = "SKU-A", Description = "Item A", BaseUnit = unit, BaseUnitId = 1 };
        var prodB = new Product { Sku = "SKU-B", Description = "Item B", BaseUnit = unit, BaseUnitId = 1 };
        var loc = new Location { Code = "LOC-10", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };

        var movement = new InventoryMovement
        {
            Type = InventoryMovementType.Entry,
            Purpose = InventoryMovementPurpose.Standard,
            ResponsibleUser = user,
            OccurredAt = DateTimeOffset.UtcNow,
            Reference = "REF-MULTI",
            Notes = "Multiples lineas",
            RequestFingerprint = "fp-multi"
        };
        movement.Lines.Add(new InventoryMovementLine { Product = prodA, Unit = unit, UnitId = 1, DestinationLocation = loc, Quantity = 10m, LineNumber = 1 });
        movement.Lines.Add(new InventoryMovementLine { Product = prodA, Unit = unit, UnitId = 1, DestinationLocation = loc, Quantity = 5m, LineNumber = 2 });
        movement.Lines.Add(new InventoryMovementLine { Product = prodB, Unit = unit, UnitId = 1, DestinationLocation = loc, Quantity = 20m, LineNumber = 3 });

        var dto = EffectiveMovementQuery.ProjectToRowDto(movement);

        Assert.Equal(3, dto.LineCount);
        Assert.Equal(2, dto.DistinctSkuCount);
        Assert.Equal("Supervisor", dto.ResponsibleName);
        Assert.Equal("REF-MULTI", dto.Reference);
        Assert.Equal(3, dto.Lines.Count);
        Assert.Equal(10m, dto.Lines[0].Quantity);
        Assert.Equal(5m, dto.Lines[1].Quantity);
        Assert.Equal(20m, dto.Lines[2].Quantity);
    }

    [Fact]
    public void ProjectToRowDto_preserves_adjustment_and_lot_audit_data()
    {
        var user = new User { FullName = "Auditor", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
        var unit = new Unit { Id = 2, Code = "KG", Name = "Kilogramo", AllowsDecimals = true };
        var product = new Product { Sku = "SKU-AUDIT", BaseUnit = unit, BaseUnitId = 2 };
        var location = new Location { Code = "A-1-1", Kind = LocationKind.Rack };
        var movement = new InventoryMovement
        {
            Type = InventoryMovementType.Adjustment,
            ResponsibleUser = user,
            RequestFingerprint = "audit"
        };
        var line = new InventoryMovementLine
        {
            Product = product,
            Unit = unit,
            UnitId = unit.Id,
            Quantity = 100m,
            PreviousQuantity = 98m,
            AdjustmentDelta = 2m,
            LineNumber = 1
        };
        line.BalanceChanges.Add(new InventoryBalanceChange
        {
            Location = location,
            LotNumberSnapshot = "AUTO-20260820",
            LotDateSnapshot = new DateOnly(2026, 8, 20),
            PreviousQuantity = 98m,
            DeltaQuantity = 2m,
            ResultingQuantity = 100m
        });
        movement.Lines.Add(line);

        var dto = EffectiveMovementQuery.ProjectToRowDto(movement).Lines[0];

        Assert.Equal("KG", dto.UnitCode);
        Assert.Equal(98m, dto.PreviousQuantity);
        Assert.Equal(2m, dto.AdjustmentDelta);
        var change = Assert.Single(dto.BalanceChanges);
        Assert.Equal("AUTO-20260820", change.LotNumber);
        Assert.Equal(2m, change.DeltaQuantity);
    }

    [Fact]
    public void LocationOccupancySummaryDto_computes_utilization_with_zero_division_safeguard()
    {
        // 1. Caso normal: 10 ocupadas, 10 vacías, 0 negativas -> Utilización = 10 / 20 * 100 = 50%
        var normal = new LocationOccupancySummaryDto(
            TotalStoragePositions: 25,
            OccupiedCount: 10,
            EmptyCount: 10,
            NegativeCount: 0,
            BlockedCount: 3,
            InactiveCount: 2);

        Assert.Equal(20, normal.ActiveAvailableCount);
        Assert.Equal(50.00m, normal.UtilizationPercentage);

        // 2. Con negativos en el denominador: 10 ocupadas, 5 vacías, 5 negativas -> 10 / 20 * 100 = 50%
        var withNegatives = new LocationOccupancySummaryDto(
            TotalStoragePositions: 20,
            OccupiedCount: 10,
            EmptyCount: 5,
            NegativeCount: 5,
            BlockedCount: 0,
            InactiveCount: 0);

        Assert.Equal(20, withNegatives.ActiveAvailableCount);
        Assert.Equal(50.00m, withNegatives.UtilizationPercentage);

        // 3. Caso salvaguarda: 100% de racks bloqueados o inactivos (ActiveAvailable = 0) -> Utilización = 0%
        var allBlocked = new LocationOccupancySummaryDto(
            TotalStoragePositions: 10,
            OccupiedCount: 0,
            EmptyCount: 0,
            NegativeCount: 0,
            BlockedCount: 10,
            InactiveCount: 0);

        Assert.Equal(0, allBlocked.ActiveAvailableCount);
        Assert.Equal(0m, allBlocked.UtilizationPercentage);
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"EffectiveMovementTests-{Guid.NewGuid():N}")
            .Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
