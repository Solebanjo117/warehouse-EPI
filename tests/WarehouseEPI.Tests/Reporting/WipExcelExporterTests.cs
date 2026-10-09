using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Tests.Imports;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Tests.Reporting;

public sealed class WipExcelExporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 7, 0, 0, TimeSpan.FromHours(-5));
    private static WipExcelContext Context => new("Almacén de prueba", Now, "America/Matamoros", null,
        new DateOnly(2026, 10, 9), null, "WIP-01", "Todos los documentos", "Más antiguas");
    private static WipInventoryRow Row(string unit, string sku, decimal quantity) => new(Guid.NewGuid(), "WIP-01",
        Guid.NewGuid(), sku, "Material de prueba con descripción extendida para revisar el ajuste de texto en el archivo exportado.",
        unit, quantity, null, Now.AddDays(-10), Guid.NewGuid(), Delivered: 12.3456m, Used: 1.2345m,
        Responsible: "Responsable de prueba con nombre y apellidos largos");

    [Fact]
    public void Workbook_preserves_detail_and_summarizes_only_positive_exported_rows()
    {
        var first = Row("EA", "000123", 1.2345m);
        WipInventoryRow[] rows = [first, first with { DocumentId = Guid.NewGuid(), Quantity = 2 },
            Row("EA", "ZERO", 0), Row("EA", "NEG", -10), Row("KG", "KG-1", 7.0001m),
            Row("UNASSIGNED", "U-1", 3), Row("UNASSIGNED", "U-2", 5) with { IsOpening = true }];
        var bytes = WipExcelExporter.Export(rows, Context);
        SaveFixture("wip-poblado.xlsx", bytes);
        using var book = Open(bytes);
        Assert.Equal(new[] { "Resumen", "WIP" }, book.Worksheets.Select(x => x.Name));
        Assert.True(book.Worksheet("Resumen").TabActive);
        var summary = book.Worksheet("Resumen");
        Assert.Equal(rows.Length, summary.Cell("B6").GetValue<int>());
        Assert.Equal(Now.DateTime, summary.Cell("B4").GetDateTime());
        Assert.Equal("Sin límite inicial", summary.Cell("B8").GetString());
        Assert.Equal("09/10/2026", summary.Cell("B9").GetString());
        Assert.Equal(3.2345m, summary.Cell("D21").GetValue<decimal>());
        Assert.Equal(7.0001m, summary.Cell("D22").GetValue<decimal>());
        Assert.Equal("U-1", summary.Cell("B23").GetString());
        Assert.Equal(3m, summary.Cell("D23").GetValue<decimal>());
        Assert.Equal("U-2", summary.Cell("B24").GetString());
        Assert.Equal(5m, summary.Cell("D24").GetValue<decimal>());
        Assert.Equal(24, summary.LastRowUsed()!.RowNumber());
        var sheet = book.Worksheet("WIP");
        Assert.Equal(14, sheet.LastColumnUsed()!.ColumnNumber());
        Assert.Equal(rows.Length + 1, sheet.LastRowUsed()!.RowNumber());
        Assert.Equal("Documento", sheet.Cell("A1").GetString());
        Assert.Equal("Pendiente documental", sheet.Cell("M1").GetString());
        Assert.True(sheet.AutoFilter.IsEnabled);
        Assert.Equal(rows.Length + 1, sheet.AutoFilter.Range.LastRow().RowNumber());
        Assert.Equal(1, sheet.SheetView.SplitRow);
        Assert.Equal("000123", sheet.Cell("C2").GetString());
        Assert.Equal(XLDataType.Text, sheet.Cell("C2").DataType);
        Assert.NotEqual(sheet.Cell("A2").Style.Fill.BackgroundColor, sheet.Cell("A3").Style.Fill.BackgroundColor);
        Assert.NotEqual(sheet.Cell("L2").Style.Fill.BackgroundColor, sheet.Cell("M2").Style.Fill.BackgroundColor);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(rows[i].DocumentId.ToString(), sheet.Cell(i + 2, 1).GetString());
            Assert.Equal(rows[i].UpdatedAt.DateTime, sheet.Cell(i + 2, 2).GetDateTime());
            Assert.Equal("dd/mm/yyyy hh:mm", sheet.Cell(i + 2, 2).Style.DateFormat.Format);
            Assert.Equal(rows[i].Quantity, sheet.Cell(i + 2, 13).GetValue<decimal>());
            Assert.Equal(XLDataType.Number, sheet.Cell(i + 2, 13).DataType);
            Assert.Equal("#,##0.####", sheet.Cell(i + 2, 13).Style.NumberFormat.Format);
            decimal[] quantities = [rows[i].Delivered, rows[i].Used, rows[i].Scrapped, rows[i].WarehouseReturned, rows[i].SupplierReturned, rows[i].Quantity];
            Assert.Equal(quantities, Enumerable.Range(8, 6).Select(c => sheet.Cell(i + 2, c).GetValue<decimal>()));
        }
        Assert.Equal("Apertura del corte", sheet.Cell(8, 7).GetString());
        Assert.True(sheet.Cell("D2").Style.Alignment.WrapText);
        Assert.Equal(XLAlignmentVerticalValues.Top, sheet.Cell("D2").Style.Alignment.Vertical);
        Assert.InRange(sheet.Column(4).Width, 20, 50);
        Assert.All(book.Worksheets.SelectMany(x => x.CellsUsed()), x => Assert.False(x.HasFormula));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+SUM(A1)")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\t=1+1")]
    [InlineData("\r=1+1")]
    [InlineData("\n=1+1")]
    [InlineData("  =1+1")]
    public void Untrusted_text_is_literal_and_numbers_remain_numeric(string value)
    {
        var row = Row("UNASSIGNED", value, 2) with { ProductDescription = value, Responsible = value, WipArea = value };
        using var book = Open(WipExcelExporter.Export([row], Context with { Search = value, WarehouseName = value, Area = value }));
        foreach (var address in new[] { "C2", "D2", "F2", "N2" })
        {
            var cell = book.Worksheet("WIP").Cell(address);
            Assert.Equal(value.Replace("\r", "\n"), cell.GetString());
            Assert.False(cell.HasFormula);
            Assert.True(cell.Style.IncludeQuotePrefix);
        }
        Assert.True(book.Worksheet("Resumen").Cell("B10").Style.IncludeQuotePrefix);
        Assert.True(book.Worksheet("Resumen").Cell("B21").Style.IncludeQuotePrefix);
        Assert.Equal(XLDataType.Number, book.Worksheet("WIP").Cell("M2").DataType);
    }

    [Fact]
    public void Empty_workbook_keeps_headers_open_bounds_and_context()
    {
        var bytes = WipExcelExporter.Export([], Context with { From = new(2026, 10, 1), To = null });
        SaveFixture("wip-vacio.xlsx", bytes);
        using var book = Open(bytes);
        var summary = book.Worksheet("Resumen");
        Assert.Equal(0, summary.Cell("B6").GetValue<int>());
        Assert.Equal("Sin límite final", summary.Cell("B9").GetString());
        Assert.Equal("WIP-01", summary.Cell("B11").GetString());
        Assert.Equal("Sin documentos para los filtros aplicados.", summary.Cell("A21").GetString());
        Assert.Equal(1, book.Worksheet("WIP").LastRowUsed()!.RowNumber());
        Assert.True(book.Worksheet("WIP").AutoFilter.IsEnabled);
    }

    [Theory]
    [InlineData("pending", "Más antiguas", "Documentos pendientes")]
    [InlineData("aged", "Más antiguas", "Pendientes antiguos — 7 días o más")]
    [InlineData(null, "Más recientes", "Todos los documentos")]
    public async Task Handler_resolves_empty_area_and_effective_filters(string? attention, string order, string followUp)
    {
        await using var db = WipTransferImportTests.Db();
        await db.Database.EnsureCreatedAsync();
        var area = new WarehouseEPI.Core.Entities.Location { Code = "SUMMARY-WIP", Kind = WarehouseEPI.Core.Entities.LocationKind.Area, OperationalRole = WarehouseEPI.Core.Entities.LocationOperationalRole.Wip };
        db.Locations.Add(area);
        await db.SaveChangesAsync();
        var settings = new WarehouseSettingsService(db);
        var clock = new WarehouseClock(settings);
        var handler = new WarehouseEPI.Web.Pages.Admin.Reports.Wip.ExportModel(new(db, clock), clock,
            new PassthroughStringLocalizer<CatalogTexts>(), settings, new FixedClock(), db);
        var result = Assert.IsType<FileContentResult>(await handler.OnGetAsync("xlsx", null, null, "NOT-FOUND", area.Id, attention));
        using var book = Open(result.FileContents);
        Assert.Equal("SUMMARY-WIP", book.Worksheet("Resumen").Cell("B11").GetString());
        Assert.Equal(followUp, book.Worksheet("Resumen").Cell("B12").GetString());
        Assert.Equal(order, book.Worksheet("Resumen").Cell("B13").GetString());
        var local = await clock.ConvertAsync(WipSummaryTests.Now);
        Assert.Equal(local.DateTime, book.Worksheet("Resumen").Cell("B4").GetDateTime());
        Assert.Equal($"reporte-wip-{local:yyyyMMddHHmmss}.xlsx", result.FileDownloadName);
    }

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => WipSummaryTests.Now; }
    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));
    private static void SaveFixture(string name, byte[] bytes)
    {
        var path = Environment.GetEnvironmentVariable("WAREHOUSE_WIP_EXCEL_FIXTURES");
        if (string.IsNullOrEmpty(path)) return;
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, name), bytes);
    }
}
