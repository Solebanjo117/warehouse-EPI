using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class HeatmapReportServiceTests
{
    [Fact]
    public async Task Heatmap_uses_all_racks_for_scale_regardless_of_detail_page()
    {
        await using var db = CreateDbContext();
        var user = CreateUser();
        var product = CreateProduct("SKU-MANY-RACKS");
        db.AddRange(user, product);
        var locations = Enumerable.Range(1, 55)
            .Select(number => CreateLocation($"U-{number}-1", "U", (short)number))
            .ToArray();
        db.AddRange(locations);
        AddBalanceChangeMovement(db, user, product, locations[0], new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero));
        for (var index = 0; index < 4; index++)
            AddBalanceChangeMovement(db, user, product, locations[^1], new DateTimeOffset(2026, 8, 10, 11, index, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var interval = new HeatmapReportFilter(
            Metric: HeatmapMetricType.AccessFrequency,
            FromUtc: new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            ToUtc: new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero),
            PageNumber: 1,
            PageSize: 10);
        var firstPage = await service.GetHeatmapPageAsync(interval);
        var secondPage = await service.GetHeatmapPageAsync(interval with { PageNumber = 2 });

        Assert.Equal(55, firstPage.TotalCount);
        Assert.Equal(10, firstPage.Racks.Count);
        Assert.Equal(55, firstPage.AllRacks.Count);
        Assert.Equal(55, secondPage.AllRacks.Count);
        Assert.Equal(1, firstPage.AllRacks.Single(rack => rack.RackNumber == 1).HeatLevel);
        Assert.Equal(4, firstPage.AllRacks.Single(rack => rack.RackNumber == 55).HeatLevel);
        Assert.Equal(
            firstPage.AllRacks.Select(rack => (rack.ElementId, rack.HeatLevel)),
            secondPage.AllRacks.Select(rack => (rack.ElementId, rack.HeatLevel)));
    }

    [Fact]
    public async Task Heatmap_calculates_access_frequency_and_levels_correctly()
    {
        await using var db = CreateDbContext();
        var user = CreateUser();
        var product = CreateProduct("SKU-HEAT");
        var locR1 = CreateLocation("R1-1-1", "R1", 1);
        var locR1Second = CreateLocation("R1-1-2", "R1", 1);
        var locR2 = CreateLocation("R2-2-1", "R2", 2);
        var locR3 = CreateLocation("R3-3-1", "R3", 3);
        db.AddRange(user, product, locR1, locR1Second, locR2, locR3);

        // Rack 1: 10 accesos (vía 10 balance changes)
        for (var i = 0; i < 10; i++)
        {
            var movement = AddBalanceChangeMovement(db, user, product, locR1, new DateTimeOffset(2026, 8, 10, 10, i, 0, TimeSpan.Zero));
            if (i == 0)
            {
                movement.Lines.Single().BalanceChanges.Add(new InventoryBalanceChange
                {
                    Location = locR1Second,
                    DeltaQuantity = -1m,
                    PreviousQuantity = 4m,
                    ResultingQuantity = 3m
                });
            }
        }

        // Rack 2: 5 accesos
        for (var i = 0; i < 5; i++)
            AddBalanceChangeMovement(db, user, product, locR2, new DateTimeOffset(2026, 8, 10, 11, i, 0, TimeSpan.Zero));

        // Rack 3: 0 accesos

        // Movimiento corregido en Rack 3 (debe quedar excluido)
        var orig = AddBalanceChangeMovement(db, user, product, locR3, new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var rev = AddBalanceChangeMovement(db, user, product, locR3, new DateTimeOffset(2026, 8, 10, 12, 5, 0, TimeSpan.Zero));
        db.InventoryMovementCorrections.Add(new InventoryMovementCorrection
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString(),
            OriginalMovementId = orig.Id,
            ReversalMovementId = rev.Id,
            AuthorizedByUserId = user.Id,
            Reason = "Error"
        });

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var filter = new HeatmapReportFilter(
            Metric: HeatmapMetricType.AccessFrequency,
            FromUtc: new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            ToUtc: new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));

        var report = await service.GetHeatmapPageAsync(filter);

        Assert.Equal(3, report.Summary.TotalRacks);
        Assert.Equal(10, report.Summary.MaxAccessCount);

        var r1 = report.Racks.Single(r => r.RowCode == "R1");
        Assert.Equal(10, r1.AccessCount);
        Assert.Equal(4, r1.HeatLevel);
        Assert.Equal("heat-4", r1.HeatClass);

        var r2 = report.Racks.Single(r => r.RowCode == "R2");
        Assert.Equal(5, r2.AccessCount);
        Assert.Equal(2, r2.HeatLevel);
        Assert.Equal("heat-2", r2.HeatClass);

        var r3 = report.Racks.Single(r => r.RowCode == "R3");
        Assert.Equal(0, r3.AccessCount);
        Assert.Equal(0, r3.HeatLevel);
        Assert.Equal("heat-0", r3.HeatClass);
        Assert.Equal(3, report.AllRacks.Count);
    }

    [Fact]
    public async Task Heatmap_calculates_occupancy_density_and_handles_empty_racks()
    {
        await using var db = CreateDbContext();
        var product = CreateProduct("SKU-OCC");
        var offsettingProduct = new Product { Sku = "SKU-OFFSET", BaseUnitId = 1, BaseUnit = product.BaseUnit, IsActive = true };
        var locA1 = CreateLocation("RA-1-1", "RA", 1);
        var locA2 = CreateLocation("RA-1-2", "RA", 1);
        var locB1 = CreateLocation("RB-2-1", "RB", 2);
        var locB2 = CreateLocation("RB-2-2", "RB", 2);
        var locB3 = CreateLocation("RB-2-3", "RB", 2);
        var locB4 = CreateLocation("RB-2-4", "RB", 2);
        var locC1 = CreateLocation("RC-3-1", "RC", 3, isActive: false); // Inactiva
        db.AddRange(product, offsettingProduct, locA1, locA2, locB1, locB2, locB3, locB4, locC1);

        // Rack A: 2 posiciones activas, ambas con saldo -> 100% -> HeatLevel 4
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = locA1.Id, Quantity = 10m });
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = locA2.Id, Quantity = 5m });

        // Rack B: 4 posiciones activas, 1 con saldo -> 25% -> HeatLevel 1
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = locB1.Id, Quantity = 2m });
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = locB2.Id, Quantity = 10m });
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = offsettingProduct.Id, LocationId = locB2.Id, Quantity = -10m });

        // Rack C: 0 posiciones activas -> 0% -> HeatLevel 0 (sin excepción)

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var filter = new HeatmapReportFilter(Metric: HeatmapMetricType.OccupancyDensity);

        var report = await service.GetHeatmapPageAsync(filter);

        var ra = report.Racks.Single(r => r.RowCode == "RA");
        Assert.Equal(100m, ra.OccupancyPercent);
        Assert.Equal(4, ra.HeatLevel);

        var rb = report.Racks.Single(r => r.RowCode == "RB");
        Assert.Equal(50m, rb.OccupancyPercent);
        Assert.Equal(2, rb.HeatLevel);
        Assert.Equal(1, rb.NegativePositions);

        var rc = report.Racks.Single(r => r.RowCode == "RC");
        Assert.Equal(0m, rc.OccupancyPercent);
        Assert.Equal(0, rc.HeatLevel);
    }

    [Fact]
    public async Task Heatmap_exports_excel_and_csv_with_proper_scale()
    {
        await using var db = CreateDbContext();
        var product = CreateProduct("SKU-EXP");
        var loc = CreateLocation("R1-1-1", "R1", 1);
        db.AddRange(product, loc);
        db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = loc.Id, Quantity = 50m });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var filter = new HeatmapReportFilter(Metric: HeatmapMetricType.OccupancyDensity);
        var (racks, summary, _, _, _, _) = await service.GetHeatmapExportAsync(filter);

        var exportService = new ReportExportService(new WarehouseSettingsService(db));

        // Excel
        var excelBytes = await exportService.ExportHeatmapToExcelAsync(racks, summary, filter, "Saldo actual");
        Assert.NotEmpty(excelBytes);

        using var stream = new MemoryStream(excelBytes);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Mapa de Calor");
        Assert.NotNull(sheet);
        Assert.Equal("R1", sheet.Cell(6, 1).GetString());
        // Cell 7 is percentage (scale 0-100 divided by 100 = 1.0)
        Assert.Equal(1.0, sheet.Cell(6, 7).GetValue<double>());
        Assert.Equal("0.00%", sheet.Cell(6, 7).Style.NumberFormat.Format);

        // CSV
        var csvBytes = await exportService.ExportHeatmapToCsvAsync(racks, summary, filter, "Saldo actual");
        var csvText = Encoding.UTF8.GetString(csvBytes);
        Assert.Contains("R1", csvText);
        Assert.Contains("100.00%", csvText);
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
        db.SaveChanges();
        return db;
    }

    private static HeatmapReportService CreateService(WarehouseDbContext db)
    {
        var mapService = new WarehouseMapService(db);
        return new HeatmapReportService(db, mapService, new WarehouseSettingsService(db));
    }

    private static User CreateUser() => new()
    {
        Id = Guid.NewGuid(),
        FullName = "Operador Test",
        PinLookup = Guid.NewGuid().ToString(),
        PinHash = "hash",
        RoleId = 1,
        Role = new Role { Id = 1, Code = "OPERATOR", Name = "Operador" }
    };

    private static Product CreateProduct(string sku) => new()
    {
        Id = Guid.NewGuid(),
        Sku = sku,
        Description = $"Producto {sku}",
        BaseUnitId = 1,
        BaseUnit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" },
        IsActive = true
    };

    private static Location CreateLocation(string code, string rowCode, short rackNumber, bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        RowCode = rowCode,
        RackNumber = rackNumber,
        Kind = LocationKind.Rack,
        OperationalRole = LocationOperationalRole.Storage,
        IsActive = isActive,
        IsBlocked = false,
        IsPhysicallyPresent = true
    };

    private static InventoryMovement AddBalanceChangeMovement(
        WarehouseDbContext db,
        User user,
        Product product,
        Location location,
        DateTimeOffset occurredAt)
    {
        var movement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString(),
            Type = InventoryMovementType.Exit,
            Purpose = InventoryMovementPurpose.GeneralExit,
            ResponsibleUserId = user.Id,
            ResponsibleUser = user,
            OccurredAt = occurredAt,
            RecordedAt = occurredAt
        };

        var line = new InventoryMovementLine
        {
            Id = Guid.NewGuid(),
            MovementId = movement.Id,
            ProductId = product.Id,
            Product = product,
            UnitId = product.BaseUnitId,
            Unit = product.BaseUnit,
            Quantity = 1m,
            SourceLocationId = location.Id,
            SourceLocation = location
        };

        line.BalanceChanges.Add(new InventoryBalanceChange
        {
            Id = Guid.NewGuid(),
            MovementLineId = line.Id,
            LocationId = location.Id,
            Location = location,
            DeltaQuantity = -1m,
            PreviousQuantity = 10m,
            ResultingQuantity = 9m
        });

        movement.Lines.Add(line);
        db.InventoryMovements.Add(movement);
        return movement;
    }
}
