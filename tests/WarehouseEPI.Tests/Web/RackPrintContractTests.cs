namespace WarehouseEPI.Tests.Web;

public sealed class RackPrintContractTests
{
    [Fact]
    public void Croquis_and_rack_view_link_to_a_printable_rack_sheet()
    {
        var index = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Locations", "_LocationIndex.cshtml"));
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Locations", "Rack", "_RackPrint.cshtml"));
        var model = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Locations", "Rack", "RackPrintPageModel.cs"));
        var styles = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "css", "rack-print.css"));
        var siteScript = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "site.js"));

        Assert.Contains("var printPage = Model.IsAdministrativeView", index, StringComparison.Ordinal);
        Assert.Contains("\"/Admin/Catalogs/Locations/Rack/Print\"", index, StringComparison.Ordinal);
        Assert.Contains("\"/Locations/Rack/Print\"", index, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"@printPage\"", index, StringComparison.Ordinal);
        Assert.Contains("asp-route-rowCode", index, StringComparison.Ordinal);
        Assert.Contains("asp-route-rackNumber", index, StringComparison.Ordinal);
        Assert.Contains("data-print-page", page, StringComparison.Ordinal);
        Assert.Contains("Una asignación sin saldo no confirma existencia física", page, StringComparison.Ordinal);
        Assert.Contains("position.Products.Take(3)", page, StringComparison.Ordinal);
        Assert.Contains("producto(s) más en esta posición", page, StringComparison.Ordinal);
        Assert.Contains("[7, 8, 9, 4, 5, 6, 1, 2, 3]", model, StringComparison.Ordinal);
        Assert.Contains("group.Sum(item => item.Quantity)", model, StringComparison.Ordinal);
        Assert.Contains("Saldo sin asignación", model, StringComparison.Ordinal);
        Assert.Contains("Asignado sin saldo", model, StringComparison.Ordinal);
        Assert.Contains("@media print", styles, StringComparison.Ordinal);
        Assert.Contains("size:letter landscape", styles, StringComparison.Ordinal);
        Assert.Contains("height:194mm", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows:repeat(3,minmax(0,1fr))", styles, StringComparison.Ordinal);
        Assert.Contains("break-inside:avoid-page", styles, StringComparison.Ordinal);
        Assert.Contains("[data-print-page]", siteScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Rack_sheet_is_a_verification_instrument_with_one_layout_for_screen_and_paper()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Locations", "Rack", "_RackPrint.cshtml"));
        var styles = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "css", "rack-print.css"));

        // Cada posición ofrece dónde marcar el recorrido, porque el pie pide firma.
        Assert.Contains("sheet-check", page, StringComparison.Ordinal);
        Assert.Contains("Verificado por", page, StringComparison.Ordinal);
        // El suelo del bastidor fija la orientación igual que en la vista de racks.
        Assert.Contains(".rack-sheet-frame::after", styles, StringComparison.Ordinal);
        // Una sola maqueta: print solo reexpresa las medidas, no rehace el diseño.
        Assert.Contains("--pallet: 1.3rem;", styles, StringComparison.Ordinal);
        Assert.Contains("--pallet:13pt", styles, StringComparison.Ordinal);
        // El numeral va perfilado; nueve recuadros macizos por hoja gastan tóner.
        Assert.DoesNotContain("background:var(--bs-primary);color:#fff", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Sheet_survives_the_global_print_rule_that_hides_header_and_footer_elements()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Locations", "Rack", "_RackPrint.cshtml"));
        var styles = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "css", "rack-print.css"));
        var siteStyles = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "css", "site.css"));

        // La hoja usa <header>/<footer> semánticos...
        Assert.Contains("<header class=\"rack-sheet-header\">", page, StringComparison.Ordinal);
        Assert.Contains("<header class=\"sheet-cell-head\">", page, StringComparison.Ordinal);
        Assert.Contains("<footer class=\"rack-sheet-footer\">", page, StringComparison.Ordinal);

        // ...y site.css los oculta al imprimir con un selector de elemento desnudo.
        // Mientras esa regla siga ahí, estos overrides sostienen la hoja impresa:
        // sin ellos desaparecen numeral, código, casilla y firmas, y el bastidor
        // pierde su fila de 1fr.
        Assert.Contains("header, footer { display: none !important; }", siteStyles, StringComparison.Ordinal);
        Assert.Contains(".rack-sheet-header{display:grid!important}", styles, StringComparison.Ordinal);
        Assert.Contains(".sheet-cell-head{display:grid!important}", styles, StringComparison.Ordinal);
        Assert.Contains(".rack-sheet-footer{display:grid!important}", styles, StringComparison.Ordinal);

        // El bastidor tiene su fila fijada, no depende de la autocolocación.
        Assert.Contains(".rack-sheet-frame { grid-row: 2; }", styles, StringComparison.Ordinal);
    }

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
