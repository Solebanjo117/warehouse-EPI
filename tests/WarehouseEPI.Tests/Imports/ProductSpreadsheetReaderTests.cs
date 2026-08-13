using ClosedXML.Excel;
using WarehouseEPI.Core;
using WarehouseEPI.Infrastructure.Imports;

namespace WarehouseEPI.Tests.Imports;

public sealed class ProductSpreadsheetReaderTests
{
    private readonly ProductSpreadsheetReader reader = new();

    [Fact]
    public void Valid_row_is_normalized_and_optional_values_are_preserved()
    {
        using var stream = Workbook((" rm ", " sku-1 ", new string('D', 240), " Pound ( lb ) ", " REF-1 "));

        var result = reader.Read(stream);

        var row = Assert.Single(result.Rows);
        Assert.False(result.HasErrors);
        Assert.Equal("SKU-1", row.Sku);
        Assert.Equal(new string('D', 240), row.Description);
        Assert.Equal("LB", row.UnitCode);
        Assert.Equal("RM", row.ClassCode);
        Assert.Equal("REF-1", row.ExternalReference);
    }

    [Fact]
    public void Empty_mapped_row_is_ignored_and_blank_optional_values_are_null()
    {
        using var stream = Workbook(("", "", "", "", ""), ("", "SKU-2", "", "Each (EA)", ""));

        var result = reader.Read(stream);

        var row = Assert.Single(result.Rows);
        Assert.Equal(1, result.SourceRowCount);
        Assert.Null(row.Description);
        Assert.Null(row.ExternalReference);
        Assert.Null(row.ClassCode);
        Assert.Equal(1, result.MissingExternalReferenceCount);
        Assert.Contains(result.Issues, issue => issue.Code == "missing_class" && !issue.IsError);
    }

    [Fact]
    public void Blank_unit_uses_unassigned_with_a_warning()
    {
        using var stream = Workbook(("RM", "SKU-UNASSIGNED", "Sin unidad en la fuente", "", ""));

        var result = reader.Read(stream);

        var row = Assert.Single(result.Rows);
        Assert.False(result.HasErrors);
        Assert.Equal(CatalogDefaults.UnassignedUnitCode, row.UnitCode);
        Assert.Contains(result.Issues, issue => issue.Code == "missing_unit_defaulted" && !issue.IsError);
    }

    [Theory]
    [InlineData("missing-sheet", "missing_sheet")]
    [InlineData("moved-header", "invalid_header")]
    [InlineData("missing-sku", "missing_sku")]
    [InlineData("long-sku", "sku_too_long")]
    [InlineData("bad-unit", "invalid_unit")]
    [InlineData("long-reference", "reference_too_long")]
    public void Invalid_structures_and_values_are_blocking(string scenario, string expectedCode)
    {
        using var stream = scenario switch
        {
            "missing-sheet" => Workbook(("RM", "SKU", "D", "Each (EA)", "R"), "OTHER"),
            "moved-header" => Workbook(("RM", "SKU", "D", "Each (EA)", "R"), headerOverride: "ITEM"),
            "missing-sku" => Workbook(("RM", "", "D", "Each (EA)", "R")),
            "long-sku" => Workbook(("RM", new string('S', 61), "D", "Each (EA)", "R")),
            "bad-unit" => Workbook(("RM", "SKU", "D", "EA", "R")),
            "long-reference" => Workbook(("RM", "SKU", "D", "Each (EA)", new string('R', 121))),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var result = reader.Read(stream);

        Assert.Contains(result.Issues, issue => issue.Code == expectedCode && issue.IsError);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Compatible_duplicate_keeps_nonempty_reference_and_known_sku()
    {
        using var stream = Workbook(
            ("RM", "THREAD-TK92-BURGUNDY", "TK92 BURGUNDY NYLON", "Pound (LB)", ""),
            ("RM", "THREAD-TK92-BURGUNDY", "TK92 BURGUNDY NYLON", "Pound (LB)", "YY-RM-BBAG:THREAD-TK92-BURGUNDY"));

        var result = reader.Read(stream);

        var row = Assert.Single(result.Rows);
        Assert.False(result.HasErrors);
        Assert.True(row.IsConsolidated);
        Assert.Equal([2, 3], row.SourceRows);
        Assert.Equal("YY-RM-BBAG:THREAD-TK92-BURGUNDY", row.ExternalReference);
        Assert.Equal(1, result.ConsolidatedGroupCount);
    }

    [Fact]
    public void Contradictory_duplicate_is_blocking()
    {
        using var stream = Workbook(
            ("RM", "DUP", "Primera", "Each (EA)", "R1"),
            ("RM", "DUP", "Segunda", "Each (EA)", "R1"));

        var result = reader.Read(stream);

        Assert.Empty(result.Rows);
        Assert.Contains(result.Issues, issue => issue.Code == "duplicate_conflict" && issue.IsError);
    }

    [Fact]
    public void Invalid_binary_is_reported_without_throwing()
    {
        using var stream = new MemoryStream("not an xlsx"u8.ToArray());
        var result = reader.Read(stream);
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_workbook" && issue.IsError);
    }

    [Fact]
    public void More_than_ten_thousand_data_rows_is_blocking()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("ITEMS");
        sheet.Cell(1, 1).Value = "CLASS";
        sheet.Cell(1, 3).Value = "ITEM (Short)";
        sheet.Cell(1, 4).Value = "DESCRIPTION";
        sheet.Cell(1, 5).Value = "U/M";
        sheet.Cell(1, 12).Value = "COMPLETE PART #";
        sheet.Cell(10_002, 3).Value = "TOO-MANY";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var result = reader.Read(stream);

        Assert.Contains(result.Issues, issue => issue.Code == "too_many_rows" && issue.IsError);
    }

    [Fact]
    public void Configured_real_workbook_matches_the_current_source_audit()
    {
        var path = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_PRODUCT_WORKBOOK");
        if (string.IsNullOrWhiteSpace(path))
            return;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var result = reader.Read(stream);

        Assert.Equal(1_613, result.SourceRowCount);
        Assert.Equal(1_612, result.Rows.Count);
        Assert.Equal(1, result.ConsolidatedGroupCount);
        Assert.Equal(123, result.MissingExternalReferenceCount);
        Assert.Equal(65, result.Issues.Count(issue => issue.Code == "missing_unit_defaulted" && !issue.IsError));
        Assert.DoesNotContain(result.Issues, issue => issue.IsError);
        Assert.Contains(result.Rows, row => row.Sku == "THREAD-TK92-BURGUNDY" && row.IsConsolidated);
    }

    private static MemoryStream Workbook(
        (string Class, string Sku, string Description, string Unit, string Reference) row,
        string sheetName = "ITEMS",
        string? headerOverride = null) => Workbook([row], sheetName, headerOverride);

    private static MemoryStream Workbook(
        (string Class, string Sku, string Description, string Unit, string Reference) first,
        (string Class, string Sku, string Description, string Unit, string Reference) second) =>
        Workbook([first, second]);

    private static MemoryStream Workbook(
        (string Class, string Sku, string Description, string Unit, string Reference)[] rows,
        string sheetName = "ITEMS",
        string? headerOverride = null)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(sheetName);
        sheet.Cell(1, 1).Value = "CLASS";
        sheet.Cell(1, 3).Value = headerOverride ?? "ITEM (Short)";
        sheet.Cell(1, 4).Value = "DESCRIPTION";
        sheet.Cell(1, 5).Value = "U/M";
        sheet.Cell(1, 12).Value = "COMPLETE PART #";
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var number = index + 2;
            sheet.Cell(number, 1).Value = row.Class;
            sheet.Cell(number, 3).Value = row.Sku;
            sheet.Cell(number, 4).Value = row.Description;
            sheet.Cell(number, 5).Value = row.Unit;
            sheet.Cell(number, 12).Value = row.Reference;
        }
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}
