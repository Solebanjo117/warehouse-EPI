using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class ReportExportServiceTests
{
    [Fact]
    public async Task ExportMovementsToExcelAsync_generates_valid_workbook_with_typed_cells_and_sanitization()
    {
        await using var db = CreateDbContext();
        var settingsService = new WarehouseSettingsService(db);
        var exportService = new ReportExportService(settingsService);

        var movementId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 8, 15, 14, 30, 0, TimeSpan.Zero);

        var lines = new List<EffectiveMovementLineDto>
        {
            new(
                LineId: lineId,
                MovementId: movementId,
                ProductId: Guid.NewGuid(),
                Sku: "=SUM(1,2)", // Texto malicioso con fórmula
                ProductDescription: "-Cmd Injection", // Texto malicioso que inicia con '-'
                UnitId: 1,
                UnitCode: "EA",
                SourceLocationId: null,
                SourceLocationCode: null,
                DestinationLocationId: Guid.NewGuid(),
                DestinationLocationCode: "+RACK-A", // Texto malicioso que inicia con '+'
                Quantity: 42.5000m,
                PreviousQuantity: null,
                AdjustmentDelta: null,
                AllocationMode: "Strict",
                BalanceChanges:
                [
                    new(
                        Guid.NewGuid(),
                        "+RACK-A",
                        Guid.NewGuid(),
                        "@LOT-01",
                        new DateOnly(2026, 12, 31),
                        0m,
                        42.5m,
                        42.5m)
                ])
        };

        var rows = new List<EffectiveMovementRowDto>
        {
            new(
                Id: movementId,
                OperationId: Guid.NewGuid(),
                OccurredAt: occurredAt,
                MovementType: InventoryMovementType.Entry,
                Purpose: InventoryMovementPurpose.Standard,
                ResponsibleName: "Admin Test",
                Reference: "REF-001",
                Notes: "Nota normal",
                OperationalAreaCode: null,
                LineCount: 1,
                DistinctSkuCount: 1,
                Lines: lines)
        };

        var filter = new MovementReportFilter(Sku: "SKU-AUDIT", LocationCode: "RACK-A");
        var bytes = await exportService.ExportMovementsToExcelAsync(rows, filter);
        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);

        // Validar que el archivo Excel se abre y contiene los datos y tipos correctos
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheet("Movimientos");

        Assert.NotNull(worksheet);
        Assert.Contains("producto=SKU-AUDIT", worksheet.Cell(3, 1).GetString());
        Assert.Contains("ubicación=RACK-A", worksheet.Cell(3, 1).GetString());

        // Fila 6 es la primera fila de datos (después de títulos, filtros y encabezados en fila 5)
        var row = worksheet.Row(6);

        // 1. Folio
        Assert.Equal(movementId.ToString(), row.Cell(1).GetString());

        // 2. Fecha (tipo DateTime)
        Assert.Equal(XLDataType.DateTime, row.Cell(2).DataType);

        // 3. Tipo
        Assert.Equal("ENTRADA", row.Cell(3).GetString());

        // 8. SKU sanitizado como texto sin ejecución de fórmula
        Assert.False(row.Cell(8).HasFormula);
        Assert.Equal(XLDataType.Text, row.Cell(8).DataType);

        // 9. Descripción sanitizada como texto
        Assert.False(row.Cell(9).HasFormula);
        Assert.Equal(XLDataType.Text, row.Cell(9).DataType);

        // 10. Cantidad (tipo numérico con valor 42.5)
        Assert.Equal(XLDataType.Number, row.Cell(10).DataType);
        Assert.Equal(42.5000m, row.Cell(10).GetValue<decimal>());

        // 13. Destino sanitizado como texto
        Assert.False(row.Cell(13).HasFormula);
        Assert.Equal(XLDataType.Text, row.Cell(13).DataType);

        // 18. Cambios de lote sanitizados como texto
        Assert.False(row.Cell(18).HasFormula);
        Assert.Equal(XLDataType.Text, row.Cell(18).DataType);
        Assert.Contains("@LOT-01", row.Cell(18).GetString());
    }

    [Fact]
    public async Task ExportMovementsToCsvAsync_generates_utf8_bom_and_preserves_numeric_values_without_quotes()
    {
        await using var db = CreateDbContext();
        var settingsService = new WarehouseSettingsService(db);
        var exportService = new ReportExportService(settingsService);

        var movementId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 8, 15, 14, 30, 0, TimeSpan.Zero);

        var lines = new List<EffectiveMovementLineDto>
        {
            new(
                LineId: lineId,
                MovementId: movementId,
                ProductId: Guid.NewGuid(),
                Sku: "=DANGEROUS_FORMULA()",
                ProductDescription: "Guante, \"Especial\", Protección",
                UnitId: 1,
                UnitCode: "PAR",
                SourceLocationId: null,
                SourceLocationCode: null,
                DestinationLocationId: null,
                DestinationLocationCode: "RACK-01",
                Quantity: 15.7500m,
                PreviousQuantity: null,
                AdjustmentDelta: null,
                AllocationMode: "Strict",
                BalanceChanges: [])
        };

        var rows = new List<EffectiveMovementRowDto>
        {
            new(
                Id: movementId,
                OperationId: Guid.NewGuid(),
                OccurredAt: occurredAt,
                MovementType: InventoryMovementType.Exit,
                Purpose: InventoryMovementPurpose.ProductionIssue,
                ResponsibleName: "Operador",
                Reference: "WO-99",
                Notes: "Línea con \"comillas\" y comas, normales",
                OperationalAreaCode: "WIP-1",
                LineCount: 1,
                DistinctSkuCount: 1,
                Lines: lines)
        };

        var bytes = await exportService.ExportMovementsToCsvAsync(rows, new MovementReportFilter(Sku: "SKU-CSV"));
        Assert.NotNull(bytes);

        // 1. Validar UTF-8 BOM (0xEF, 0xBB, 0xBF)
        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);

        var csvContent = Encoding.UTF8.GetString(bytes[3..]);

        // 2. Validar que la fórmula textual fue neutralizada con comilla simple
        Assert.Contains("\"'=DANGEROUS_FORMULA()\"", csvContent);

        // 3. Validar que la cantidad numérica se escribe como 15.7500
        Assert.Contains(",15.7500,", csvContent);

        // 4. Validar que comillas en textos son escapadas según RFC 4180 (""comillas"")
        Assert.Contains("\"Línea con \"\"comillas\"\" y comas, normales\"", csvContent);
        Assert.Contains("\"Guante, \"\"Especial\"\", Protección\"", csvContent);
        Assert.Contains("\"producto=SKU-CSV\"", csvContent);
    }

    [Fact]
    public async Task Inventory_analytics_exports_preserve_types_dates_metadata_and_formula_defense()
    {
        await using var db = CreateDbContext();
        var exportService = new ReportExportService(new WarehouseSettingsService(db));
        var lastExit = new DateTimeOffset(2026, 8, 15, 14, 30, 0, TimeSpan.Zero);
        var filter = new InventoryAnalyticsFilter(
            new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero),
            ProductStatus: "all",
            Search: "=peligroso",
            UnitId: 1);
        var rotation = new SkuRotationMetricDto(
            Guid.NewGuid(), "=ROT-1", "+Descripción", 1, "EA", 3, 12.5m, 7m, lastExit, false);
        var stagnant = new StagnantProductDto(
            Guid.NewGuid(), "@STG-1", "-Descripción", 1, "EA", 9.25m, lastExit, 95,
            StagnantCategory.Days90Plus, true);

        var rotationXlsx = await exportService.ExportRotationToExcelAsync([rotation], filter);
        using (var workbook = new XLWorkbook(new MemoryStream(rotationXlsx)))
        {
            var row = workbook.Worksheet("Rotación").Row(6);
            Assert.False(row.Cell(1).HasFormula);
            Assert.Equal(XLDataType.Text, row.Cell(1).DataType);
            Assert.Equal(XLDataType.Number, row.Cell(5).DataType);
            Assert.Equal(XLDataType.Number, row.Cell(6).DataType);
            Assert.Equal(XLDataType.Number, row.Cell(7).DataType);
            Assert.Equal(XLDataType.DateTime, row.Cell(8).DataType);
            Assert.Contains("estado=all", workbook.Worksheet("Rotación").Cell(3, 1).GetString());
        }

        var stagnantXlsx = await exportService.ExportStagnantToExcelAsync([stagnant], filter);
        using (var workbook = new XLWorkbook(new MemoryStream(stagnantXlsx)))
        {
            var row = workbook.Worksheet("Estancamiento").Row(6);
            Assert.False(row.Cell(1).HasFormula);
            Assert.Equal(XLDataType.Number, row.Cell(5).DataType);
            Assert.Equal(XLDataType.DateTime, row.Cell(6).DataType);
            Assert.Equal(XLDataType.Number, row.Cell(7).DataType);
            Assert.Equal("90+ días", row.Cell(8).GetString());
        }

        var csv = await exportService.ExportRotationToCsvAsync([rotation], filter);
        Assert.Equal([0xEF, 0xBB, 0xBF], csv[..3]);
        var content = Encoding.UTF8.GetString(csv[3..]);
        Assert.Contains("\"'=ROT-1\"", content);
        Assert.Contains(",3,12.5000,7.0000,", content);
        Assert.Contains("estado=all", content);
        Assert.Contains("America/Matamoros", content);
    }

    [Fact]
    public async Task Cycle_count_exports_preserve_numeric_dates_bom_and_formula_defense()
    {
        await using var db = CreateDbContext();
        var exportService = new ReportExportService(new WarehouseSettingsService(db));
        var started = new DateTimeOffset(2026, 8, 21, 14, 30, 0, TimeSpan.Zero);
        var row = new CycleCountExportRow(
            "=CC-000001", "+A-1-1", 2, "@SKU-1", "-Descripción", "EA",
            7m, 5m, -2m, true, CycleCountLocationStatus.Completed, started, started.AddMinutes(4));

        var xlsx = await exportService.ExportCycleCountsToExcelAsync([row]);
        using (var workbook = new XLWorkbook(new MemoryStream(xlsx)))
        {
            var data = workbook.Worksheet("Conteos cíclicos").Row(6);
            Assert.False(data.Cell(1).HasFormula);
            Assert.False(data.Cell(4).HasFormula);
            Assert.Equal(XLDataType.Number, data.Cell(7).DataType);
            Assert.Equal(XLDataType.Number, data.Cell(8).DataType);
            Assert.Equal(XLDataType.Number, data.Cell(9).DataType);
            Assert.Equal(XLDataType.DateTime, data.Cell(12).DataType);
            Assert.Equal(XLDataType.DateTime, data.Cell(13).DataType);
        }

        var csv = await exportService.ExportCycleCountsToCsvAsync([row]);
        Assert.Equal([0xEF, 0xBB, 0xBF], csv[..3]);
        var content = Encoding.UTF8.GetString(csv[3..]);
        Assert.Contains("\"'=CC-000001\"", content);
        Assert.Contains(",7.0000,5.0000,-2.0000,", content);
        Assert.Contains("America/Matamoros", content);
    }

    [Fact]
    public void SanitizeText_neutralizes_formula_prefixes_on_strings()
    {
        Assert.Equal(string.Empty, ReportExportService.SanitizeText(null));
        Assert.Equal(string.Empty, ReportExportService.SanitizeText(string.Empty));
        Assert.Equal("SKU-100", ReportExportService.SanitizeText("SKU-100"));

        Assert.Equal("'=1+1", ReportExportService.SanitizeText("=1+1"));
        Assert.Equal("'+100", ReportExportService.SanitizeText("+100"));
        Assert.Equal("'-200", ReportExportService.SanitizeText("-200"));
        Assert.Equal("'@cmd", ReportExportService.SanitizeText("@cmd"));
        Assert.Equal("'\tTab", ReportExportService.SanitizeText("\tTab"));
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"ReportExportServiceTests-{Guid.NewGuid():N}")
            .Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
