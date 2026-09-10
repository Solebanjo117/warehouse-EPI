using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class InventoryAnalyticsLotAgingTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 20, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Lot_aging_classifies_buckets_correctly_and_excludes_zero_stock()
    {
        await using var db = CreateDbContext();
        var product = Product("LOT-SKU-1");
        var loc = Location("LOC-A", "A");
        db.AddRange(product, loc);

        var date10 = DateOnly.FromDateTime(NowUtc.AddDays(-10).DateTime);
        var date45 = DateOnly.FromDateTime(NowUtc.AddDays(-45).DateTime);
        var date75 = DateOnly.FromDateTime(NowUtc.AddDays(-75).DateTime);
        var date120 = DateOnly.FromDateTime(NowUtc.AddDays(-120).DateTime);
        var dateZero = DateOnly.FromDateTime(NowUtc.AddDays(-50).DateTime);

        var lot10 = Lot(product, $"AUTO-{date10:yyyyMMdd}", date10);
        var lot45 = Lot(product, $"AUTO-{date45:yyyyMMdd}", date45);
        var lot75 = Lot(product, $"AUTO-{date75:yyyyMMdd}", date75);
        var lot120 = Lot(product, $"AUTO-{date120:yyyyMMdd}", date120);
        var lotZero = Lot(product, $"AUTO-{dateZero:yyyyMMdd}", dateZero);

        db.AddRange(lot10, lot45, lot75, lot120, lotZero);
        db.InventoryBalances.AddRange(
            Balance(product, loc, lot10, 10m),
            Balance(product, loc, lot45, 20m),
            Balance(product, loc, lot75, 30m),
            Balance(product, loc, lot120, 40m),
            Balance(product, loc, lotZero, 0m));
        await db.SaveChangesAsync();

        var service = Service(db);
        var report = await service.GetLotAgingPageAsync(new InventoryAnalyticsFilter(), NowUtc);

        Assert.Equal(4, report.Summary.TotalActiveLots);
        Assert.Equal(1, report.Summary.Days0To30LotCount);
        Assert.Equal(1, report.Summary.Days31To60LotCount);
        Assert.Equal(1, report.Summary.Days61To90LotCount);
        Assert.Equal(1, report.Summary.Days90PlusLotCount);

        Assert.Equal(4, report.Page.TotalCount);
        var items = report.Page.Items;
        Assert.Equal(LotAgeBucket.Days90Plus, items[0].AgeBucket);
        Assert.Equal(120, items[0].AgeDays);
        Assert.Equal(40m, items[0].Quantity);

        Assert.Equal(LotAgeBucket.Days61To90, items[1].AgeBucket);
        Assert.Equal(75, items[1].AgeDays);

        Assert.Equal(LotAgeBucket.Days31To60, items[2].AgeBucket);
        Assert.Equal(45, items[2].AgeDays);

        Assert.Equal(LotAgeBucket.Days0To30, items[3].AgeBucket);
        Assert.Equal(10, items[3].AgeDays);
    }

    [Fact]
    public async Task Lot_aging_aggregates_multiple_locations_and_identifies_primary_location()
    {
        await using var db = CreateDbContext();
        var product = Product("LOT-MULTI");
        var loc1 = Location("RACK-01", "R");
        var loc2 = Location("RACK-02", "R");
        var loc3 = Location("RACK-03", "R");
        var lotDate = DateOnly.FromDateTime(NowUtc.AddDays(-15).DateTime);
        var lot = Lot(product, "AUTO-SPLIT", lotDate);

        db.AddRange(product, loc1, loc2, loc3, lot);
        db.InventoryBalances.AddRange(
            Balance(product, loc1, lot, 25m),
            Balance(product, loc2, lot, 50m),
            Balance(product, loc3, lot, 10m));
        await db.SaveChangesAsync();

        var service = Service(db);
        var report = await service.GetLotAgingPageAsync(new InventoryAnalyticsFilter(), NowUtc);

        Assert.Single(report.Page.Items);
        var item = report.Page.Items[0];
        Assert.Equal(85m, item.Quantity);
        Assert.Equal(3, item.LocationCount);
        Assert.Equal("RACK-02", item.PrimaryLocationCode);
        Assert.Equal(LotAgeBucket.Days0To30, item.AgeBucket);
    }

    [Fact]
    public async Task Lot_aging_filters_by_bucket_search_and_product_status()
    {
        await using var db = CreateDbContext();
        var active = Product("SKU-ACTIVE", isActive: true);
        var inactive = Product("SKU-INACTIVE", isActive: false);
        var loc = Location("RACK-01", "R");
        var lotOld = Lot(active, "AUTO-OLD", DateOnly.FromDateTime(NowUtc.AddDays(-100).DateTime));
        var lotRecent = Lot(active, "AUTO-RECENT", DateOnly.FromDateTime(NowUtc.AddDays(-5).DateTime));
        var lotInactive = Lot(inactive, "AUTO-INACT", DateOnly.FromDateTime(NowUtc.AddDays(-15).DateTime));

        db.AddRange(active, inactive, loc, lotOld, lotRecent, lotInactive);
        db.InventoryBalances.AddRange(
            Balance(active, loc, lotOld, 10m),
            Balance(active, loc, lotRecent, 5m),
            Balance(inactive, loc, lotInactive, 8m));
        await db.SaveChangesAsync();

        var service = Service(db);

        // Filter by bucket
        var bucketFilter = await service.GetLotAgingPageAsync(
            new InventoryAnalyticsFilter(AgeBucket: LotAgeBucket.Days90Plus), NowUtc);
        Assert.Single(bucketFilter.Page.Items);
        Assert.Equal("AUTO-OLD", bucketFilter.Page.Items[0].LotNumber);
        Assert.Equal(2, bucketFilter.Summary.TotalActiveLots);

        // Filter by search
        var searchFilter = await service.GetLotAgingPageAsync(
            new InventoryAnalyticsFilter(Search: "recent"), NowUtc);
        Assert.Single(searchFilter.Page.Items);
        Assert.Equal("AUTO-RECENT", searchFilter.Page.Items[0].LotNumber);

        // Filter by inactive product status
        var inactiveFilter = await service.GetLotAgingPageAsync(
            new InventoryAnalyticsFilter(ProductStatus: "inactive"), NowUtc);
        Assert.Single(inactiveFilter.Page.Items);
        Assert.Equal("AUTO-INACT", inactiveFilter.Page.Items[0].LotNumber);
    }

    [Fact]
    public async Task Lot_aging_export_to_excel_and_csv_includes_notice_and_formats()
    {
        await using var db = CreateDbContext();
        var product = Product("EXP-SKU", description: "Cinta industrial");
        var loc = Location("RACK-EXP", "R");
        var lot = Lot(product, "AUTO-EXP", DateOnly.FromDateTime(NowUtc.AddDays(-35).DateTime));
        db.AddRange(product, loc, lot);
        db.InventoryBalances.Add(Balance(product, loc, lot, 120m));
        await db.SaveChangesAsync();

        var service = Service(db);
        var settingsService = new WarehouseSettingsService(db);
        var exportService = new ReportExportService(settingsService);
        var filter = new InventoryAnalyticsFilter();

        var page = await service.GetLotAgingPageAsync(filter, NowUtc);
        var batch = await service.GetLotAgingExportAsync(filter, NowUtc);

        // Excel validation
        var excelBytes = await exportService.ExportLotAgingToExcelAsync(batch.Items, page.Summary, filter);
        Assert.NotNull(excelBytes);
        Assert.NotEmpty(excelBytes);

        using var stream = new MemoryStream(excelBytes);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Antigüedad lotes");
        Assert.NotNull(sheet);
        var noticeCell = sheet.Cell(3, 1).GetString();
        Assert.Contains("tiempo de permanencia", noticeCell, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no representa fecha de caducidad", noticeCell, StringComparison.OrdinalIgnoreCase);

        var dataRow = sheet.Row(7);
        Assert.Equal("EXP-SKU", dataRow.Cell(1).GetString());
        Assert.Equal("AUTO-EXP", dataRow.Cell(4).GetString());
        Assert.Equal(35, dataRow.Cell(6).GetValue<int>());
        Assert.Equal(120m, dataRow.Cell(8).GetValue<decimal>());

        // CSV validation
        var csvBytes = await exportService.ExportLotAgingToCsvAsync(batch.Items, filter);
        Assert.NotNull(csvBytes);
        var csvText = Encoding.UTF8.GetString(csvBytes);
        Assert.Contains("Aviso permanencia", csvText);
        Assert.Contains("EXP-SKU", csvText);
        Assert.Contains("AUTO-EXP", csvText);
        Assert.Contains("tiempo de permanencia", csvText, StringComparison.OrdinalIgnoreCase);
    }

    private static InventoryAnalyticsService Service(WarehouseDbContext db) =>
        new(db, new WarehouseSettingsService(db));

    private static ProductLot Lot(Product product, string lotNumber, DateOnly lotDate) => new()
    {
        Product = product,
        ProductId = product.Id,
        Number = lotNumber,
        NormalizedNumber = lotNumber.Trim().ToUpperInvariant(),
        LotDate = lotDate,
        CreatedAt = new DateTimeOffset(lotDate.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)
    };

    private static InventoryBalance Balance(Product product, Location location, ProductLot? lot, decimal quantity) => new()
    {
        Product = product,
        Location = location,
        Lot = lot,
        LotId = lot?.Id,
        Quantity = quantity
    };

    private static Location Location(string code, string row, bool isActive = true, bool isBlocked = false) => new()
    {
        Code = code,
        Kind = LocationKind.Rack,
        OperationalRole = LocationOperationalRole.Storage,
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

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"InventoryAnalyticsLotAgingTests-{Guid.NewGuid():N}")
            .Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
