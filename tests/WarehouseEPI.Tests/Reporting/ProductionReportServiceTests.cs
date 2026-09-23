using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class ProductionReportServiceTests
{
    [Fact]
    public async Task Orders_report_uses_warehouse_receipts_and_marks_overdue_target()
    {
        await using var db = CreateDb();
        var unit = new Unit { Id = 1, Code = "EA", Name = "Pieza" };
        var product = new Product { Id = Guid.NewGuid(), Sku = "PT-01", Description = "Terminado", BaseUnitId = 1, BaseUnit = unit, IsActive = true };
        var user = new User { Id = Guid.NewGuid(), FullName = "Operador", RoleId = 1, PinLookup = "lookup", PinHash = "hash" };
        var order = new ProductionWorkOrder
        {
            Id = Guid.NewGuid(), CreateOperationId = Guid.NewGuid(), CreateFingerprint = "fingerprint", Number = "ORD-001",
            Product = product, ProductId = product.Id, Unit = unit, UnitId = 1, OriginalTargetQuantity = 100,
            TargetQuantity = 100, AuthorizedQuantity = 100, DueDate = new DateOnly(2026, 9, 14),
            Status = ProductionWorkOrderStatus.InProgress, CreatedByUserId = user.Id, CreatedByUser = user,
            CreatedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)
        };
        order.Events.Add(new ProductionEvent
        {
            OperationId = Guid.NewGuid(), RequestFingerprint = "receipt", WorkOrder = order,
            Type = ProductionEventType.WarehouseReceived, ResponsibleUser = user, ResponsibleUserId = user.Id,
            Quantity = 40, RecordedAt = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero)
        });
        db.AddRange(unit, product, user, order);
        await db.SaveChangesAsync();

        var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var service = new ProductionReportService(db, new WarehouseSettingsService(db), new FixedTimeProvider(now));
        var report = await service.GetAsync("orders", new ProductionReportFilter(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero)), "Periodo");

        var row = Assert.Single(report.Orders);
        Assert.Equal(40, row.Received);
        Assert.True(row.IsOverdue);
        Assert.Equal(1, report.Summary.OverdueOrders);
        Assert.Equal(1, report.Summary.OrdersWithAlerts);
    }

    [Fact]
    public void Export_preserves_numeric_cells_and_sanitizes_text()
    {
        var row = new ProductionOrderReportRow(Guid.NewGuid(), "=ORD-1", "PT-1", "Terminado", "EA",
            100, 90, 80, ProductionWorkOrderStatus.InProgress, new DateOnly(2026, 9, 16), false, 0, null);
        var page = new ProductionReportPage(DateTimeOffset.UtcNow, "America/Matamoros", "Hoy",
            new(1, 1, 0, 0, 0, 80), [row], [], [], [], 1, 1, 25);

        var bytes = ProductionReportExportService.ToExcel("orders", page, "Hoy");
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet("Producción");
        Assert.False(sheet.Cell(6, 1).HasFormula);
        Assert.Equal("=ORD-1", sheet.Cell(6, 1).GetString());
        Assert.Equal(XLDataType.Text, sheet.Cell(6, 1).DataType);
        Assert.Equal(XLDataType.Number, sheet.Cell(6, 5).DataType);
        Assert.Equal(100m, sheet.Cell(6, 5).GetValue<decimal>());
    }

    [Fact]
    public void Export_rejects_more_than_limit_instead_of_truncating()
    {
        var page = new ProductionReportPage(DateTimeOffset.UtcNow, "America/Matamoros", "Hoy",
            new(0, 0, 0, 0, 0, 0), [], [], [], [], ProductionReportExportService.RowLimit + 1, 1, 25);
        var error = Assert.Throws<InvalidOperationException>(() => ProductionReportExportService.ToCsv("orders", page, "Hoy"));
        Assert.Contains("10,000", error.Message);
    }

    private static WarehouseDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new WarehouseDbContext(options);
        db.Roles.Add(new Role { Id = 1, Code = "OPERATOR", Name = "Operador" });
        db.BusinessSettings.Add(new BusinessSettings
        {
            BusinessName = "EPI", WarehouseName = "Almacén", WarehouseCode = "EPI",
            TimeZoneId = "America/Matamoros"
        });
        db.SaveChanges();
        return db;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
