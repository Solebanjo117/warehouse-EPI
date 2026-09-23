using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class InventoryAnalyticsCoverageTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 20, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Coverage_calculates_net_consumption_excluding_transfers_and_adjustments()
    {
        await using var db = CreateDbContext();
        var user = User();
        var product = Product("COV-SKU-1");
        var storage = Location("RACK-01", "R", LocationKind.Rack, LocationOperationalRole.Storage);
        var otherStorage = Location("RACK-02", "R", LocationKind.Rack, LocationOperationalRole.Storage);
        db.AddRange(user, product, storage, otherStorage);
        db.InventoryBalances.Add(Balance(product, storage, 30m));

        // Effective production exit: +20
        AddMovement(db, user, product, InventoryMovementType.Transfer, InventoryMovementPurpose.ProductionIssue, NowUtc.AddDays(-10), 20m);
        // Effective general exit: +15
        AddMovement(db, user, product, InventoryMovementType.Exit, InventoryMovementPurpose.GeneralExit, NowUtc.AddDays(-5), 15m);
        // Effective WIP warehouse return: -5
        AddMovement(db, user, product, InventoryMovementType.Transfer, InventoryMovementPurpose.WipWarehouseReturn, NowUtc.AddDays(-3), 5m);
        // Internal transfer (should be ignored): 50m
        AddMovement(db, user, product, InventoryMovementType.Transfer, InventoryMovementPurpose.Standard, NowUtc.AddDays(-8), 50m);
        // Cycle count adjustment (should be ignored): 10m
        AddMovement(db, user, product, InventoryMovementType.Adjustment, InventoryMovementPurpose.CycleCountAdjustment, NowUtc.AddDays(-2), 10m);

        // Correction chain: original 100 exit, reversed 100, replacement 2 exit
        var original = AddMovement(db, user, product, InventoryMovementType.Transfer, InventoryMovementPurpose.ProductionIssue, NowUtc.AddDays(-12), 100m);
        var reversal = AddMovement(db, user, product, InventoryMovementType.Transfer, InventoryMovementPurpose.ProductionIssue, NowUtc.AddDays(-11), 100m);
        var replacement = AddMovement(db, user, product, InventoryMovementType.Transfer, InventoryMovementPurpose.ProductionIssue, NowUtc.AddDays(-10), 2m);
        db.InventoryMovementCorrections.Add(Correction(original, reversal, replacement, user));

        await db.SaveChangesAsync();

        var service = Service(db);
        var filter = new InventoryAnalyticsFilter(FromUtc: NowUtc.AddDays(-30), ToUtc: NowUtc);
        var report = await service.GetCoveragePageAsync(filter, NowUtc);

        Assert.Single(report.Page.Items);
        var item = report.Page.Items[0];
        // Net consumption = (20 + 15 + 2) - 5 = 32m
        Assert.Equal(32m, item.NetConsumption);
        // Daily average = 32 / 30 = 1.0667m
        Assert.Equal(Math.Round(32m / 30m, 4), item.DailyAverageConsumption);
        // Available stock = 30m
        Assert.Equal(30m, item.AvailableStock);
        // Coverage days = 30 / (32 / 30) = 28.1 days
        Assert.Equal(Math.Round(30m / (32m / 30m), 1), item.CoverageDays);
        Assert.Equal(CoverageClassification.Normal, item.Classification);
    }

    [Fact]
    public async Task Coverage_considers_only_active_unblocked_storage_racks()
    {
        await using var db = CreateDbContext();
        var user = User();
        var product = Product("COV-STOCK");
        var activeStorage = Location("RACK-OK", "R", LocationKind.Rack, LocationOperationalRole.Storage, isActive: true, isBlocked: false);
        var blockedStorage = Location("RACK-BLK", "R", LocationKind.Rack, LocationOperationalRole.Storage, isActive: true, isBlocked: true);
        var inactiveStorage = Location("RACK-INACT", "R", LocationKind.Rack, LocationOperationalRole.Storage, isActive: false, isBlocked: false);
        var wipLocation = Location("WIP-01", "W", LocationKind.Rack, LocationOperationalRole.Wip, isActive: true, isBlocked: false);
        var areaLocation = Location("AREA-01", "A", LocationKind.Area, LocationOperationalRole.Storage, isActive: true, isBlocked: false);

        db.AddRange(user, product, activeStorage, blockedStorage, inactiveStorage, wipLocation, areaLocation);
        db.InventoryBalances.AddRange(
            Balance(product, activeStorage, 50m),
            Balance(product, blockedStorage, 20m),
            Balance(product, inactiveStorage, 30m),
            Balance(product, wipLocation, 40m),
            Balance(product, areaLocation, 10m));

        // Net exit = 30 in 30 days (1/day)
        AddMovement(db, user, product, InventoryMovementType.Exit, InventoryMovementPurpose.ProductionIssue, NowUtc.AddDays(-15), 30m);
        await db.SaveChangesAsync();

        var service = Service(db);
        var report = await service.GetCoveragePageAsync(
            new InventoryAnalyticsFilter(FromUtc: NowUtc.AddDays(-30), ToUtc: NowUtc), NowUtc);

        Assert.Single(report.Page.Items);
        var item = report.Page.Items[0];
        // Only activeStorage count (50m)
        Assert.Equal(50m, item.AvailableStock);
        Assert.Equal(50m, item.CoverageDays);
        Assert.Equal(CoverageClassification.Excess, item.Classification);
    }

    [Fact]
    public async Task Coverage_protects_against_division_by_zero_and_classifies_thresholds()
    {
        await using var db = CreateDbContext();
        var user = User();
        var storage = Location("RACK-01", "R", LocationKind.Rack, LocationOperationalRole.Storage);

        var pNoConsumption = Product("P-NOCONS");
        var pExhausted = Product("P-EXHAUST");
        var pCritical = Product("P-CRIT");
        var pLow = Product("P-LOW");
        var pNormal = Product("P-NORM");
        var pExcess = Product("P-EXCESS");

        db.AddRange(user, storage, pNoConsumption, pExhausted, pCritical, pLow, pNormal, pExcess);

        // Balances:
        db.InventoryBalances.AddRange(
            Balance(pNoConsumption, storage, 100m),
            Balance(pExhausted, storage, 0m),
            Balance(pCritical, storage, 5m),
            Balance(pLow, storage, 10m),
            Balance(pNormal, storage, 30m),
            Balance(pExcess, storage, 100m));

        // Movements (30 days window, 30m consumed = 1/day):
        AddMovement(db, user, pExhausted, InventoryMovementType.Exit, InventoryMovementPurpose.ProductionIssue, NowUtc.AddDays(-5), 30m);
        AddMovement(db, user, pCritical, InventoryMovementType.Exit, InventoryMovementPurpose.ProductionIssue, NowUtc.AddDays(-5), 30m);
        AddMovement(db, user, pLow, InventoryMovementType.Exit, InventoryMovementPurpose.ProductionIssue, NowUtc.AddDays(-5), 30m);
        AddMovement(db, user, pNormal, InventoryMovementType.Exit, InventoryMovementPurpose.ProductionIssue, NowUtc.AddDays(-5), 30m);
        AddMovement(db, user, pExcess, InventoryMovementType.Exit, InventoryMovementPurpose.ProductionIssue, NowUtc.AddDays(-5), 30m);
        // pNoConsumption has 0 movements!

        await db.SaveChangesAsync();

        var service = Service(db);
        var filter = new InventoryAnalyticsFilter(FromUtc: NowUtc.AddDays(-30), ToUtc: NowUtc);
        var report = await service.GetCoveragePageAsync(filter, NowUtc);

        var summary = report.Summary;
        Assert.Equal(6, summary.TotalSkus);
        Assert.Equal(1, summary.CriticalCount);
        Assert.Equal(1, summary.LowCount);
        Assert.Equal(1, summary.NormalCount);
        Assert.Equal(1, summary.ExcessCount);
        Assert.Equal(1, summary.NoRecentConsumptionCount);
        Assert.Equal(1, summary.ExhaustedCount);

        var itemsBySku = report.Page.Items.ToDictionary(x => x.Sku);

        // 1. Sin consumo reciente: CoverageDays is null, no division by zero
        var noCons = itemsBySku["P-NOCONS"];
        Assert.Null(noCons.CoverageDays);
        Assert.Equal(CoverageClassification.NoRecentConsumption, noCons.Classification);
        Assert.Equal("Sin consumo reciente", noCons.ClassificationLabel);

        // 2. Agotado: stock 0, consumption > 0
        var exhaust = itemsBySku["P-EXHAUST"];
        Assert.Equal(0m, exhaust.CoverageDays);
        Assert.Equal(CoverageClassification.Exhausted, exhaust.Classification);
        Assert.Equal("Agotado", exhaust.ClassificationLabel);

        // 3. Crítico: 5 days (< 7)
        var crit = itemsBySku["P-CRIT"];
        Assert.Equal(5m, crit.CoverageDays);
        Assert.Equal(CoverageClassification.Critical, crit.Classification);

        // 4. Bajo: 10 days (7 to 14)
        var low = itemsBySku["P-LOW"];
        Assert.Equal(10m, low.CoverageDays);
        Assert.Equal(CoverageClassification.Low, low.Classification);

        // 5. Normal: 30 days (15 to 45)
        var norm = itemsBySku["P-NORM"];
        Assert.Equal(30m, norm.CoverageDays);
        Assert.Equal(CoverageClassification.Normal, norm.Classification);

        // 6. Exceso: 100 days (> 45)
        var exc = itemsBySku["P-EXCESS"];
        Assert.Equal(100m, exc.CoverageDays);
        Assert.Equal(CoverageClassification.Excess, exc.Classification);
    }

    [Fact]
    public async Task Coverage_export_to_excel_and_csv_handles_null_coverage_and_alerts()
    {
        await using var db = CreateDbContext();
        var user = User();
        var product = Product("EXP-COV", description: "Bota de seguridad");
        var storage = Location("RACK-01", "R", LocationKind.Rack, LocationOperationalRole.Storage);
        db.AddRange(user, product, storage);
        db.InventoryBalances.Add(Balance(product, storage, 50m));
        // No movements -> CoverageDays is null
        await db.SaveChangesAsync();

        var service = Service(db);
        var settingsService = new WarehouseSettingsService(db);
        var exportService = new ReportExportService(settingsService);
        var filter = new InventoryAnalyticsFilter(FromUtc: NowUtc.AddDays(-30), ToUtc: NowUtc);

        var page = await service.GetCoveragePageAsync(filter, NowUtc);
        var batch = await service.GetCoverageExportAsync(filter, NowUtc);

        // Excel export
        var excelBytes = await exportService.ExportCoverageToExcelAsync(batch.Items, page.Summary, filter);
        Assert.NotNull(excelBytes);
        Assert.NotEmpty(excelBytes);

        using var stream = new MemoryStream(excelBytes);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Cobertura");
        Assert.NotNull(sheet);

        // Title and alert summary in header
        Assert.Contains("Cobertura estimada por consumo", sheet.Cell(1, 1).GetString());
        Assert.Contains("Sin consumo: 1", sheet.Cell(3, 1).GetString());

        // Row 7 is first data row
        var row = sheet.Row(7);
        Assert.Equal("EXP-COV", row.Cell(1).GetString());
        Assert.Equal(50m, row.Cell(4).GetValue<decimal>());
        Assert.Equal(0m, row.Cell(5).GetValue<decimal>());
        // Days column contains text label for null
        Assert.Equal("Sin consumo reciente", row.Cell(7).GetString());
        Assert.Equal("Sin consumo reciente", row.Cell(8).GetString());

        // CSV export
        var csvBytes = await exportService.ExportCoverageToCsvAsync(batch.Items, filter);
        Assert.NotNull(csvBytes);
        var csvText = Encoding.UTF8.GetString(csvBytes);
        Assert.Contains("EXP-COV", csvText);
        Assert.Contains("Sin consumo reciente", csvText);
    }

    private static InventoryAnalyticsService Service(WarehouseDbContext db) =>
        new(db, new WarehouseSettingsService(db));

    private static InventoryMovement AddMovement(
        WarehouseDbContext db,
        User user,
        Product product,
        InventoryMovementType type,
        InventoryMovementPurpose purpose,
        DateTimeOffset occurredAt,
        decimal quantity)
    {
        var movement = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString("N"),
            Type = type,
            Purpose = purpose,
            ResponsibleUser = user,
            OccurredAt = occurredAt
        };
        movement.Lines.Add(new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            Quantity = quantity,
            LineNumber = 1
        });
        db.InventoryMovements.Add(movement);
        return movement;
    }

    private static InventoryMovementCorrection Correction(
        InventoryMovement original,
        InventoryMovement reversal,
        InventoryMovement replacement,
        User user) => new()
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString("N"),
            Type = InventoryMovementCorrectionType.Replacement,
            OriginalMovement = original,
            ReversalMovement = reversal,
            ReplacementMovement = replacement,
            Reason = "Corrección cobertura",
            RequestedByUser = user,
            AuthorizedByUser = user
        };

    private static InventoryBalance Balance(Product product, Location location, decimal quantity) => new()
    {
        Product = product,
        Location = location,
        LotId = Guid.NewGuid(),
        Quantity = quantity
    };

    private static Location Location(
        string code,
        string row,
        LocationKind kind,
        LocationOperationalRole role,
        bool isActive = true,
        bool isBlocked = false) => new()
        {
            Code = code,
            Kind = kind,
            OperationalRole = role,
            RowCode = row,
            IsActive = isActive,
            IsBlocked = isBlocked
        };

    private static Product Product(
        string sku,
        bool isActive = true,
        string? description = null,
        short unitId = 1) => new()
        {
            Sku = sku,
            Description = description,
            BaseUnitId = unitId,
            IsActive = isActive
        };

    private static User User() => new()
    {
        FullName = $"Supervisor {Guid.NewGuid():N}",
        PinLookup = Guid.NewGuid().ToString("N"),
        PinHash = "hash",
        RoleId = 2
    };

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"InventoryAnalyticsCoverageTests-{Guid.NewGuid():N}")
            .Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
