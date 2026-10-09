using System.Net;
using WarehouseEPI.Infrastructure.Imports;

namespace WarehouseEPI.Tests.Imports;

public sealed class InternalInventoryReaderTests
{
    [Fact]
    public void Groups_are_excluded_but_real_parent_products_remain_and_source_rows_survive()
    {
        var result = InternalInventoryReader.Read(Html(
            Row("ASBESTOS"), Row("ASBESTOS:SUB"), Row("ASBESTOS:SUB:BAG", "Each", "Bag & cover"),
            Row("KIT", "Kit", "Complete kit"), Row("KIT:PART", "Each", "Part"),
            Row("STOCK", quantity: "-1"), Row("STOCK:CHILD"), Row("NO CHILD")));
        Assert.False(result.HasErrors);
        Assert.Equal(new InternalInventorySourceSummary(8, 2, 6), result.InternalInventory);
        var bag = Assert.Single(result.Rows, r => r.Sku == "BAG");
        Assert.Equal([4], bag.SourceRows);
        Assert.Equal("ASBESTOS:SUB:BAG", bag.ExternalReference);
        Assert.Equal("Bag & cover", bag.Description);
        Assert.Equal("EA", bag.UnitCode);
        Assert.All(result.Rows, row => Assert.Null(row.ClassCode));
        Assert.Contains(result.Rows, row => row.Sku == "KIT");
        Assert.Contains(result.Rows, row => row.Sku == "STOCK");
        Assert.Contains(result.Rows, row => row.Sku == "NO CHILD");
    }

    [Theory]
    [InlineData("Each", "EA")]
    [InlineData("Box", "BX")]
    [InlineData("Case", "CTN")]
    [InlineData("Lin. Foot", "FT")]
    [InlineData("Gallon", "GAL")]
    [InlineData("5Gal", "5GAL")]
    [InlineData("Gang of 3", "3GANG")]
    [InlineData("Kit", "KT")]
    [InlineData("Pairs", "PR")]
    [InlineData("Pound", "LB")]
    [InlineData("Roll", "RL")]
    [InlineData("Sq. Foot", "SQFT")]
    [InlineData("MSI", "MSI")]
    [InlineData("Lin. Yard", "YD")]
    [InlineData("inch", "IN")]
    [InlineData("OZ", "OZ")]
    [InlineData("", "UNASSIGNED")]
    public void Units_match_the_catalog(string source, string expected)
    {
        var result = InternalInventoryReader.Read(Html(Row("PART", source)));
        Assert.False(result.HasErrors);
        Assert.Equal(expected, Assert.Single(result.Rows).UnitCode);
    }

    [Fact]
    public void Conflicting_short_skus_and_invalid_lengths_are_not_silently_changed()
    {
        var result = InternalInventoryReader.Read(Html(Row("A:PART", "Each", "A"),
            Row("B:PART", "Each", "B"), Row(new string('X', 61)), Row(new string('P', 121) + ":SKU"), Row("UNKNOWN", "Mystery")));
        Assert.True(result.HasErrors);
        Assert.Equal("PART", Assert.Single(result.Conflicts).Sku);
        Assert.Contains(result.Issues, x => x.Code == "sku_too_long");
        Assert.Contains(result.Issues, x => x.Code == "reference_too_long");
        Assert.Contains(result.Issues, x => x.Code == "invalid_unit");
    }

    [Theory]
    [InlineData("missing-table")]
    [InlineData("truncated")]
    [InlineData("headers")]
    [InlineData("cells")]
    [InlineData("quantity")]
    [InlineData("empty")]
    public void Rejects_incomplete_or_changed_source(string scenario)
    {
        var html = Html(Row("PART"));
        html = scenario switch
        {
            "missing-table" => html.Replace("skuTable", "other", StringComparison.Ordinal),
            "truncated" => html.Replace("</body>", "", StringComparison.Ordinal),
            "headers" => html.Replace("wh_epi_fg", "wrong", StringComparison.Ordinal),
            "cells" => html.Replace("<td>PART</td>", "", StringComparison.Ordinal),
            "quantity" => Html(Row("PART", quantity: "unreadable")),
            "empty" => Html(),
            _ => html
        };
        Assert.Equal(InternalInventoryError.Structure, Assert.Throws<InternalInventoryException>(() => InternalInventoryReader.Read(html)).Error);
    }

    [Fact]
    public void Unknown_unit_cannot_impersonate_a_known_code()
    {
        var result = InternalInventoryReader.Read(Html(Row("PART", "Mystery (EA)")));
        Assert.Equal("Mystery (EA)", Assert.Single(result.Rows).UnitCode);
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_unit");
    }

    [Fact]
    public void Row_limit_applies_before_excluding_groups()
    {
        var html = Html(Enumerable.Range(0, 10001).Select(i => Row("P" + i)).ToArray());
        Assert.Equal(InternalInventoryError.TooManyRows, Assert.Throws<InternalInventoryException>(() => InternalInventoryReader.Read(html)).Error);
    }

    internal static string Html(params string[] rows) => "<html><body><table id=skuTable><thead><tr>" +
        string.Concat(new[] { "sku", "wh_epi_fg", "wh_epi_rm", "wh_ma", "wh_fl_mia", "wh_tx_hou", "wh_tx_mod", "unit", "description" }
            .Select(name => $"<th data-name='{name}'>{name}</th>")) + "</tr></thead><tbody>" + string.Concat(rows) + "</tbody></table></body></html>";

    internal static string Row(string sku, string unit = "", string description = "", string quantity = "") =>
        "<tr>" + string.Concat(new[] { sku, quantity, "", "", "", "", "", unit, description }
            .Select(value => "<td>" + WebUtility.HtmlEncode(value) + "</td>")) + "</tr>";
}
