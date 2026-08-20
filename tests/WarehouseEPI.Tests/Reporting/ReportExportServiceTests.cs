using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
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
