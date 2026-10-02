namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionReportColumn(string Label, string? Group = null);
public sealed record ProductionReportTableRow(DateOnly? Date, IReadOnlyList<object> Cells);

/// <summary>Shared column contract for the HTML, print and spreadsheet renderers.</summary>
public sealed record ProductionReportTable(IReadOnlyList<ProductionReportColumn> Columns,
    IReadOnlyList<ProductionReportTableRow> Rows, IReadOnlyList<ProductionReportTableRow> Totals)
{
    public static ProductionReportTable Create(ProductionReport report)
    {
        var columns = report.Filter.Products
            ? new List<ProductionReportColumn> { new("SKU"), new("Programa nuevo") }
            : [new("Día"), new("Fecha")];
        foreach (var area in report.Areas)
        {
            var group = AreaName(area);
            if (report.Filter.Products)
            {
                columns.Add(new("Arrastre programado", group));
                columns.Add(new("Pendiente inicial", group));
            }
            columns.Add(new("T1", group));
            if (report.Filter.Products && !report.Filter.FullWeek) columns.Add(new("Pendiente para T2", group));
            columns.Add(new("T2", group));
            if (report.OtherShiftAreas.Contains(area)) columns.Add(new("Otros turnos", group));
            columns.Add(new("Total producido", group));
            if (report.Filter.Products)
            {
                columns.Add(new("Pendiente final", group));
            }
        }
        ProductionReportTableRow Row(ProductionReportRow row, bool total)
        {
            var cells = report.Filter.Products
                ? new List<object> { total ? $"Total ({row.Unit})" : row.Sku, row.Planned }
                : new List<object> { total ? "Total semanal" : DayName(row.Date!.Value.DayOfWeek),
                    row.Date is DateOnly date ? date : "" };
            foreach (var area in row.Areas)
            {
                object Value(decimal number) => area.Applies ? number : "No aplica";
                if (report.Filter.Products)
                {
                    cells.Add(Value(area.PlannedCarryover));
                    cells.Add(Value(area.Opening));
                }
                cells.Add(Value(area.Shift1));
                if (report.Filter.Products && !report.Filter.FullWeek) cells.Add(Value(area.PendingAfterShift1));
                cells.Add(Value(area.Shift2));
                if (report.OtherShiftAreas.Contains(area.Area)) cells.Add(Value(area.Others));
                cells.Add(Value(area.Total));
                if (report.Filter.Products)
                {
                    cells.Add(Value(area.Pending));
                }
            }
            return new(total ? null : row.Date, cells);
        }
        return new(columns, report.Rows.Select(x => Row(x, false)).ToArray(), report.Totals.Select(x => Row(x, true)).ToArray());
    }

    public static string AreaName(Core.Entities.ProductionDailyArea area) => area switch
    {
        Core.Entities.ProductionDailyArea.Cutting => "Corte",
        Core.Entities.ProductionDailyArea.Sewing => "Costura", _ => "Ready to Pack"
    };
    private static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Lunes", DayOfWeek.Tuesday => "Martes", DayOfWeek.Wednesday => "Miércoles",
        DayOfWeek.Thursday => "Jueves", DayOfWeek.Friday => "Viernes", DayOfWeek.Saturday => "Sábado", _ => "Domingo"
    };
}
