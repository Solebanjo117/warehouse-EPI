using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace WarehouseEPI.Infrastructure.Imports;

public static partial class InternalInventoryReader
{
    private static readonly string[] Columns =
        ["sku", "wh_epi_fg", "wh_epi_rm", "wh_ma", "wh_fl_mia", "wh_tx_hou", "wh_tx_mod", "unit", "description"];
    private static readonly Dictionary<string, string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Each"] = "EA", ["Box"] = "BX", ["Case"] = "CTN", ["Lin. Foot"] = "FT",
        ["Gallon"] = "GAL", ["5Gal"] = "5GAL", ["Gang of 3"] = "3GANG", ["Kit"] = "KT",
        ["Pairs"] = "PR", ["Pound"] = "LB", ["Roll"] = "RL", ["Sq. Foot"] = "SQFT",
        ["MSI"] = "MSI", ["Lin. Yard"] = "YD", ["inch"] = "IN", ["OZ"] = "OZ"
    };

    public static ProductSpreadsheetReadResult Read(string html)
    {
        // HTML parsers repair truncated documents; do not accept a repaired partial inventory.
        if (!html.Contains("</table>", StringComparison.OrdinalIgnoreCase) ||
            !html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
            throw new InternalInventoryException(InternalInventoryError.Structure);
        using var document = new HtmlParser().ParseDocument(html);
        var tables = document.QuerySelectorAll("table#skuTable");
        if (tables.Length != 1) throw new InternalInventoryException(InternalInventoryError.Structure);
        var table = tables[0];
        if (!table.QuerySelectorAll("thead th").Select(x => x.GetAttribute("data-name")).SequenceEqual(Columns))
            throw new InternalInventoryException(InternalInventoryError.Structure);
        var elements = table.QuerySelectorAll("tbody > tr");
        if (elements.Length == 0) throw new InternalInventoryException(InternalInventoryError.Structure);
        if (elements.Length > ProductSpreadsheetReader.MaxDataRows)
            throw new InternalInventoryException(InternalInventoryError.TooManyRows);

        var source = new List<string[]>();
        foreach (var element in elements)
        {
            var cells = element.Children;
            if (cells.Length != Columns.Length || cells.Any(c => c.LocalName != "td" || c.HasAttribute("colspan") || c.HasAttribute("rowspan")))
                throw new InternalInventoryException(InternalInventoryError.Structure);
            var values = cells.Select(c => Whitespace().Replace(c.TextContent, " ").Trim()).ToArray();
            if (values[0].Length == 0 || values.Skip(1).Take(6).Any(v => v.Length > 0 &&
                    !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
                throw new InternalInventoryException(InternalInventoryError.Structure);
            source.Add(values);
        }

        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in source)
            for (var index = row[0].IndexOf(':'); index >= 0; index = row[0].IndexOf(':', index + 1))
                prefixes.Add(row[0][..index]);
        var inputs = new List<ProductImportInputRow>();
        var excluded = 0;
        for (var index = 0; index < source.Count; index++)
        {
            var row = source[index];
            var isGroup = prefixes.Contains(row[0]) && (row[7].Length == 0 || row[8].Length == 0) &&
                row.Skip(1).Take(6).All(v => v.Length == 0 || decimal.Parse(v, NumberStyles.Number, CultureInfo.InvariantCulture) == 0);
            if (isGroup) { excluded++; continue; }
            // Unknown units stay unresolved in the existing unit-selection workflow.
            var unit = Units.TryGetValue(row[7], out var code) ? $"{row[7]} ({code})" : row[7];
            inputs.Add(new(index + 2, "", row[0][(row[0].LastIndexOf(':') + 1)..], row[8], unit, row[0],
                row[7].Length > 0 && !Units.ContainsKey(row[7])));
        }
        if (inputs.Count == 0) throw new InternalInventoryException(InternalInventoryError.Structure);
        return ProductImportRowProcessor.Read(inputs) with
        {
            InternalInventory = new(source.Count, excluded, inputs.Count)
        };
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public enum InternalInventoryError { Password, Connection, Timeout, Structure, TooLarge, TooManyRows }

public sealed class InternalInventoryException(InternalInventoryError error) : Exception(error.ToString())
{
    public InternalInventoryError Error { get; } = error;
}
