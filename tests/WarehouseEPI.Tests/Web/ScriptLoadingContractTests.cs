namespace WarehouseEPI.Tests.Web;

public sealed class ScriptLoadingContractTests
{
    private const string Zxing = "zxing-browser.min.js";
    private const string JQuery = "jquery/dist/jquery.min.js";
    private const string Operations = "js/operations.js";

    [Fact]
    public void Layout_only_ships_what_every_page_uses()
    {
        var layout = Page("Shared", "_Layout.cshtml");

        // ZXing (395 KB), jQuery (88 KB) y operations.js (56 KB) no pueden viajar en cada
        // página: las tablets son el dispositivo objetivo.
        Assert.DoesNotContain(Zxing, layout, StringComparison.Ordinal);
        Assert.DoesNotContain(JQuery, layout, StringComparison.Ordinal);
        Assert.DoesNotContain(Operations, layout, StringComparison.Ordinal);
        Assert.Contains("js/site.js", layout, StringComparison.Ordinal);
        Assert.Contains("js/operational-notifications.js", layout, StringComparison.Ordinal);
        Assert.Contains("bootstrap.bundle.min.js", layout, StringComparison.Ordinal);
        Assert.Contains(JQuery, Page("Shared", "_ValidationScriptsPartial.cshtml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Operations", "Entry.cshtml")]
    [InlineData("Operations", "Exit.cshtml")]
    [InlineData("Operations", "Transfer.cshtml")]
    [InlineData("Operations", "Adjustment.cshtml")]
    [InlineData("Inventory", "Index.cshtml")]
    public void Capture_and_query_stations_load_their_own_scanner(string folder, string file)
    {
        var page = Page(folder, file);

        Assert.Contains(Zxing, page, StringComparison.Ordinal);
        Assert.Contains(Operations, page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Count.cshtml", true)]
    [InlineData("Details.cshtml", true)]
    [InlineData("Print.cshtml", false)]
    public void Cycle_count_pages_load_their_script_and_only_the_scanner_they_need(string file, bool needsScanner)
    {
        var page = Page("Operations", "CycleCounts", file);

        Assert.Contains("js/cycle-count.js", page, StringComparison.Ordinal);
        Assert.Equal(needsScanner, page.Contains(Zxing, StringComparison.Ordinal));
        Assert.DoesNotContain(Operations, page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Reports", "Wip", "Index.cshtml")]
    [InlineData("Reports", "Dashboard", "Index.cshtml")]
    [InlineData("Admin", "Users", "Index.cshtml")]
    [InlineData("Admin", "Inventory", "Alerts.cshtml")]
    public void Pages_without_capture_download_none_of_the_heavy_libraries(params string[] parts)
    {
        var page = Page(parts);

        Assert.DoesNotContain(Zxing, page, StringComparison.Ordinal);
        Assert.DoesNotContain(JQuery, page, StringComparison.Ordinal);
        Assert.DoesNotContain(Operations, page, StringComparison.Ordinal);
    }

    private static string Page(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string[] segments = ["src", "WarehouseEPI.Web", "Pages", .. parts];
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"No se encontró la página {string.Join('/', parts)}.");
    }
}
