using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class ExecutiveReportServiceTests
{
    [Fact]
    public async Task Executive_report_consolidates_inventory_capacity_and_flow_correctly()
    {
        await using var db = CreateDbContext();
        var user = CreateUser();
        var productHigh = CreateProduct("SKU-HIGH", 10m);
        var productLow = CreateProduct("SKU-LOW", 50m);
        var loc1 = CreateLocation("R1-01", "R1", 1);
        var loc2 = CreateLocation("R1-02", "R1", 1);
        var locEmpty = CreateLocation("R2-01", "R2", 2);
        var locBlocked = CreateLocation("R3-01", "R3", 3, isBlocked: true);
        db.AddRange(user, productHigh, productLow, loc1, loc2, locEmpty, locBlocked);

        // Stock: SKU-HIGH tiene 100 en loc1 (no bajo stock)
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = productHigh.Id, LocationId = loc1.Id, Quantity = 100m });
        // Stock: SKU-LOW tiene 5 en loc2 (menor a minimum 50 -> bajo stock)
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = productLow.Id, LocationId = loc2.Id, Quantity = 5m });
        // La ocupación no debe compensar productos ni unidades distintas dentro de una posición.
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = productHigh.Id, LocationId = locEmpty.Id, Quantity = 10m });
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = productLow.Id, LocationId = locEmpty.Id, Quantity = -10m });

        // Movimientos en período:
        // 1 Entrada para SKU-HIGH (1 op, 1 línea)
        AddMovement(db, user, InventoryMovementType.Entry, InventoryMovementPurpose.Standard,
            new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero), (productHigh, loc1, null, 100m));
        // 2 Salidas para SKU-HIGH (2 ops, 2 líneas, total 30 despachadas)
        AddMovement(db, user, InventoryMovementType.Exit, InventoryMovementPurpose.GeneralExit,
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero), (productHigh, null, loc1, 20m));
        AddMovement(db, user, InventoryMovementType.Exit, InventoryMovementPurpose.GeneralExit,
            new DateTimeOffset(2026, 8, 10, 14, 0, 0, TimeSpan.Zero), (productHigh, null, loc1, 10m));

        // Una transferencia confirmada con tres detalles cuenta como un movimiento y tres detalles.
        AddMovement(db, user, InventoryMovementType.Transfer, InventoryMovementPurpose.Standard,
            new DateTimeOffset(2026, 8, 10, 15, 0, 0, TimeSpan.Zero),
            (productHigh, loc2, loc1, 1m),
            (productLow, loc1, loc2, 2m),
            (productHigh, loc2, loc1, 3m));

        // El original y el reverso de una corrección se excluyen; el reemplazo vigente se conserva.
        var original = AddMovement(db, user, InventoryMovementType.Exit, InventoryMovementPurpose.GeneralExit,
            new DateTimeOffset(2026, 8, 10, 16, 0, 0, TimeSpan.Zero), (productHigh, null, loc1, 5m));
        var reversal = AddMovement(db, user, InventoryMovementType.Entry, InventoryMovementPurpose.Standard,
            new DateTimeOffset(2026, 8, 10, 16, 1, 0, TimeSpan.Zero), (productHigh, loc1, null, 5m));
        var replacement = AddMovement(db, user, InventoryMovementType.Exit, InventoryMovementPurpose.GeneralExit,
            new DateTimeOffset(2026, 8, 10, 16, 2, 0, TimeSpan.Zero), (productHigh, null, loc1, 4m));
        db.InventoryMovementCorrections.Add(new InventoryMovementCorrection
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString(),
            Type = InventoryMovementCorrectionType.Replacement,
            OriginalMovementId = original.Id,
            ReversalMovementId = reversal.Id,
            ReplacementMovementId = replacement.Id,
            Reason = "Corrección de prueba",
            RequestedByUserId = user.Id,
            AuthorizedByUserId = user.Id,
            RecordedAt = new DateTimeOffset(2026, 8, 10, 16, 3, 0, TimeSpan.Zero)
        });

        AddMovement(db, user, InventoryMovementType.Entry, InventoryMovementPurpose.Standard,
            new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero),
            (productHigh, loc1, null, 2m), (productLow, loc2, null, 1m));

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var filter = new ExecutiveReportFilter(
            FromUtc: new DateTimeOffset(2026, 8, 10, 6, 0, 0, TimeSpan.Zero),
            ToUtc: new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero),
            PeriodLabel: "Corte de prueba",
            PreviousFromUtc: new DateTimeOffset(2026, 8, 9, 6, 0, 0, TimeSpan.Zero),
            PreviousToUtc: new DateTimeOffset(2026, 8, 10, 6, 0, 0, TimeSpan.Zero),
            PreviousPeriodLabel: "Corte anterior");

        var report = await service.GetExecutiveReportAsync(filter);

        // 1. Salud de inventario
        Assert.Equal(2, report.InventoryHealth.TotalActiveSkus);
        Assert.Equal(1, report.InventoryHealth.LowStockSkus); // SKU-LOW

        // 2. Capacidad (4 ubicaciones tipo Rack Storage activas)
        Assert.Equal(4, report.Capacity.TotalRackPositions);
        Assert.Equal(2, report.Capacity.OccupiedPositions); // loc1 y loc2
        Assert.Equal(0, report.Capacity.EmptyPositions);
        Assert.Equal(1, report.Capacity.BlockedPositions);  // locBlocked tiene estado exclusivo
        Assert.Equal(1, report.Capacity.NegativePositions); // locEmpty: negativo precede a ocupado
        Assert.Equal(4, report.Capacity.BlockedPositions + report.Capacity.NegativePositions +
            report.Capacity.OccupiedPositions + report.Capacity.EmptyPositions);
        Assert.Equal(66.67m, report.Capacity.UtilizationPercent);

        // 3. Flujo operativo: movimientos y detalles son magnitudes distintas.
        Assert.Equal(5, report.OperationalFlow.TotalMovements);
        Assert.Equal(7, report.OperationalFlow.TotalDetails);
        Assert.Equal(1, report.OperationalFlow.EntryMovements);
        Assert.Equal(3, report.OperationalFlow.ExitMovements);
        Assert.Equal(1, report.OperationalFlow.TransferMovements);
        Assert.Equal(0, report.OperationalFlow.AdjustmentMovements);
        Assert.Equal(3, report.OperationalFlow.TransferDetails);
        Assert.Equal(1, report.Comparison.TotalMovements.Previous);
        Assert.Equal(4, report.Comparison.TotalMovements.Delta);
        Assert.Equal(400m, report.Comparison.TotalMovements.PercentChange);
        Assert.Equal(2, report.Comparison.TotalDetails.Previous);
        Assert.Contains("from=2026-08-10", report.EvidenceLinks.EffectiveMovementsUrl, StringComparison.Ordinal);
        Assert.Contains("to=2026-08-10", report.EvidenceLinks.EffectiveMovementsUrl, StringComparison.Ordinal);
        Assert.Contains("coverageClass=Critical", report.EvidenceLinks.CriticalCoverageUrl, StringComparison.Ordinal);
        Assert.Contains("stagnantCategory=90plus", report.EvidenceLinks.StagnantUrl, StringComparison.Ordinal);

        // 4. Top demandados
        Assert.Single(report.TopDemandedSkus);
        var top = report.TopDemandedSkus[0];
        Assert.Equal("SKU-HIGH", top.Sku);
        Assert.Equal(34m, top.TotalQuantity);
        Assert.Equal(3, top.MovementCount);
    }

    [Fact]
    public async Task Executive_report_exports_formal_excel_correctly()
    {
        await using var db = CreateDbContext();
        var user = CreateUser();
        var product = CreateProduct("SKU-REP", 5m);
        var loc = CreateLocation("R1-01", "R1", 1);
        db.AddRange(user, product, loc);
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = loc.Id, Quantity = 20m });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var filter = new ExecutiveReportFilter(PeriodLabel: "Mes de prueba");
        var report = await service.GetExecutiveReportAsync(filter);

        var exportService = new ReportExportService(new WarehouseSettingsService(db));
        var excelBytes = await exportService.ExportExecutiveToExcelAsync(report);
        Assert.NotEmpty(excelBytes);

        using var stream = new MemoryStream(excelBytes);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Resumen Ejecutivo");
        Assert.NotNull(sheet);
        Assert.Contains("Informe Ejecutivo", sheet.Cell(1, 1).GetString());
        Assert.Contains("Situación actual", sheet.Cell(2, 1).GetString());
        Assert.Contains("Mes de prueba", sheet.Cell(3, 1).GetString());
        Assert.Equal("Movimientos actuales", sheet.Cell(27, 2).GetString());
        Assert.Equal("Detalles actuales", sheet.Cell(27, 3).GetString());
        Assert.Equal("Sin base anterior", sheet.Cell(28, 7).GetString());
    }

    [Fact]
    public async Task Executive_report_handles_no_activity_and_no_usable_positions()
    {
        await using var db = CreateDbContext();
        var product = CreateProduct("SKU-BLOCKED", 0m);
        var blocked = CreateLocation("R9-01", "R", 9, isBlocked: true);
        blocked.BlockReason = "Mantenimiento";
        db.AddRange(product, blocked);
        db.InventoryBalances.Add(new InventoryBalance
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            LocationId = blocked.Id,
            Quantity = 10m
        });
        await db.SaveChangesAsync();

        var report = await CreateService(db).GetExecutiveReportAsync(new ExecutiveReportFilter(
            FromUtc: new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ToUtc: new DateTimeOffset(2040, 1, 2, 0, 0, 0, TimeSpan.Zero),
            PreviousFromUtc: new DateTimeOffset(2039, 12, 31, 0, 0, 0, TimeSpan.Zero),
            PreviousToUtc: new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(1, report.Capacity.TotalRackPositions);
        Assert.Equal(1, report.Capacity.BlockedPositions);
        Assert.Equal(0, report.Capacity.OccupiedPositions);
        Assert.Equal(0m, report.Capacity.UtilizationPercent);
        Assert.Equal(0, report.OperationalFlow.TotalMovements);
        Assert.Equal(0, report.OperationalFlow.TotalDetails);
        Assert.Equal(MetricComparisonState.NoActivity, report.Comparison.TotalMovements.State);
        Assert.Null(report.Comparison.TotalMovements.PercentChange);
    }

    [Fact]
    public async Task Executive_report_compares_lower_and_equal_activity_without_infinite_percentages()
    {
        await using var db = CreateDbContext();
        var user = CreateUser();
        var product = CreateProduct("SKU-COMP", 0m);
        var location = CreateLocation("R8-01", "R", 8);
        db.AddRange(user, product, location);
        AddMovement(db, user, InventoryMovementType.Entry, InventoryMovementPurpose.Standard,
            new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero), (product, location, null, 1m));
        AddMovement(db, user, InventoryMovementType.Exit, InventoryMovementPurpose.GeneralExit,
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero), (product, null, location, 1m));
        AddMovement(db, user, InventoryMovementType.Entry, InventoryMovementPurpose.Standard,
            new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.Zero), (product, location, null, 1m));
        AddMovement(db, user, InventoryMovementType.Exit, InventoryMovementPurpose.GeneralExit,
            new DateTimeOffset(2026, 8, 9, 9, 0, 0, TimeSpan.Zero), (product, null, location, 1m));
        AddMovement(db, user, InventoryMovementType.Exit, InventoryMovementPurpose.GeneralExit,
            new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero), (product, null, location, 1m));
        await db.SaveChangesAsync();

        var report = await CreateService(db).GetExecutiveReportAsync(new ExecutiveReportFilter(
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero),
            "Actual",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            "Anterior"));

        Assert.Equal(MetricComparisonState.Decreased, report.Comparison.TotalMovements.State);
        Assert.Equal(-1, report.Comparison.TotalMovements.Delta);
        Assert.Equal(-33.3m, report.Comparison.TotalMovements.PercentChange);
        Assert.Equal(MetricComparisonState.Decreased, report.Comparison.ExitMovements.State);
        Assert.Equal(-50m, report.Comparison.ExitMovements.PercentChange);
        Assert.Equal(MetricComparisonState.Unchanged, report.Comparison.EntryMovements.State);
        Assert.Equal(0m, report.Comparison.EntryMovements.PercentChange);
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new WarehouseDbContext(options);
        db.BusinessSettings.Add(new BusinessSettings
        {
            BusinessName = "EPI Global",
            WarehouseName = "Almacén Central",
            WarehouseCode = "WH-01",
            TimeZoneId = "America/Mexico_City"
        });
        db.Roles.AddRange(
            new Role { Id = 1, Code = "OPERATOR", Name = "OPERATOR" },
            new Role { Id = 2, Code = "ADMIN", Name = "ADMIN" }
        );
        db.Units.Add(new Unit { Id = 1, Code = "PZA", Name = "Pieza" });
        db.SaveChanges();
        return db;
    }

    private static ExecutiveReportService CreateService(WarehouseDbContext db)
    {
        var settingsService = new WarehouseSettingsService(db);
        var analyticsService = new InventoryAnalyticsService(db, settingsService);
        return new ExecutiveReportService(db, settingsService, analyticsService);
    }

    private static User CreateUser() => new()
    {
        Id = Guid.NewGuid(),
        FullName = "Operador Directivo",
        PinLookup = Guid.NewGuid().ToString(),
        PinHash = "hash",
        RoleId = 1
    };

    private static Product CreateProduct(string sku, decimal minimumStock) => new()
    {
        Id = Guid.NewGuid(),
        Sku = sku,
        Description = $"Producto {sku}",
        BaseUnitId = 1,
        MinimumStock = minimumStock,
        IsActive = true
    };

    private static Location CreateLocation(string code, string rowCode, short rackNumber, bool isBlocked = false) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        RowCode = rowCode,
        RackNumber = rackNumber,
        Kind = LocationKind.Rack,
        OperationalRole = LocationOperationalRole.Storage,
        IsActive = true,
        IsBlocked = isBlocked
    };

    private static InventoryMovement AddMovement(
        WarehouseDbContext db,
        User user,
        InventoryMovementType type,
        InventoryMovementPurpose purpose,
        DateTimeOffset occurredAt,
        params (Product Product, Location? Dest, Location? Src, decimal Qty)[] lines)
    {
        var movement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString(),
            Type = type,
            Purpose = purpose,
            ResponsibleUserId = user.Id,
            ResponsibleUser = user,
            OccurredAt = occurredAt,
            RecordedAt = occurredAt
        };

        foreach (var (product, dest, src, qty) in lines)
        {
            movement.Lines.Add(new InventoryMovementLine
            {
                Id = Guid.NewGuid(),
                MovementId = movement.Id,
                ProductId = product.Id,
                Product = product,
                UnitId = 1,
                Quantity = qty,
                SourceLocationId = src?.Id,
                SourceLocation = src,
                DestinationLocationId = dest?.Id,
                DestinationLocation = dest
            });
        }

        db.InventoryMovements.Add(movement);
        return movement;
    }
}
