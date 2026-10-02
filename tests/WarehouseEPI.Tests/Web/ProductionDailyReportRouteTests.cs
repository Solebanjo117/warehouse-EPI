using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using static WarehouseEPI.Tests.Web.ProductionCaptureRecoveryTests;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionDailyReportRouteTests
{
    [Theory]
    [InlineData("2026-09-21")]
    [InlineData("2026-09-28")]
    [InlineData("2026-12-28")]
    public async Task Daily_detail_navigation_preserves_filters_and_resets_pagination(string start)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        var monday = DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var week = await db.ProductionScheduleWeeks.Include(x => x.Lines).SingleAsync(x => x.Id == seed.WeekId);
        week.WeekStart = monday;
        week.WeekEnd = ProductionWeekCalendar.End(monday);
        foreach (var line in week.Lines) { line.PlannedDate = monday; line.OrderReference1 = "REF & 1"; }
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var summaryUrl = $"/Operations/Production?Tab=reports&WeekId={seed.WeekId}&Through={monday:yyyy-MM-dd}&Sku=RECOVER&Reference=REF%20%26%201&Area=Cutting";
        var summary = await client.GetStringAsync(summaryUrl);
        if (start == "2026-09-21") await Fixture("navigation-summary", summary);
        Assert.DoesNotContain("data-report-days", summary);
        var mondayLink = Regex.Match(summary, "<a[^>]*href=\"([^\"]+)\"[^>]*>Lunes</a>").Groups[1].Value;
        Assert.NotEmpty(mondayLink);
        var detail = await client.GetStringAsync(WebUtility.HtmlDecode(mondayLink));
        foreach (var offset in new[] { 0, 1, 6 })
        {
            var date = monday.AddDays(offset);
            var link = ReportLink(detail, "data-report-day=\"" + date.ToString("yyyy-MM-dd") + "\"");
            var query = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, link).Query);
            Assert.Equal(seed.WeekId.ToString(), query["WeekId"]);
            Assert.Equal("RECOVER", query["Sku"]);
            Assert.Equal("REF & 1", query["Reference"]);
            Assert.Equal("Cutting", query["Area"]);
            Assert.Equal("products", query["ReportKind"]);
            Assert.Equal("day", query["ReportPeriod"]);
            Assert.Equal("1", query["ReportPage"]);
            Assert.Equal(date.ToString("yyyy-MM-dd"), query["Through"]);
            detail = await client.GetStringAsync(link);
            if (start == "2026-09-21") await Fixture("navigation-" + offset, detail);
            Assert.Equal(7, Regex.Count(detail, "data-report-day="));
            Assert.Single(Regex.Matches(detail, "aria-current=\"date\""));
            var active = Regex.Match(detail, "<a(?=[^>]*aria-current=\"date\")[^>]*>").Value;
            Assert.Contains("data-report-day=\"" + date.ToString("yyyy-MM-dd") + "\"", active);
            var export = ReportLink(detail, "handler=ReportExport");
            Assert.Contains("Through=" + date.ToString("yyyy-MM-dd"), export);
            var print = await client.GetStringAsync(ReportLink(detail, "handler=ReportPrint"));
            Assert.Contains(date.ToString("dd/MM/yyyy"), print);
            Assert.DoesNotContain("data-report-days", print);
        }
        var back = ReportLink(detail, "data-report-back");
        var backQuery = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, back).Query);
        Assert.Equal("daily", backQuery["ReportKind"]);
        Assert.Equal(monday.AddDays(6).ToString("yyyy-MM-dd"), backQuery["Through"]);
        Assert.Equal("1", backQuery["ReportPage"]);
        Assert.Equal("REF & 1", backQuery["Reference"]);
        Assert.DoesNotContain("data-report-days", await client.GetStringAsync(back));
        var empty = await client.GetStringAsync(summaryUrl.Replace("Sku=RECOVER", "Sku=DOES-NOT-EXIST", StringComparison.Ordinal) + "&ReportKind=products&ReportPeriod=day&ReportPage=2");
        Assert.Contains("No hay resultados", empty);
        Assert.Equal(7, Regex.Count(empty, "data-report-day="));
        if (start == "2026-09-21") await Fixture("empty", empty);
        var fullWeek = await client.GetStringAsync(summaryUrl + "&ReportKind=products&ReportPeriod=week");
        Assert.DoesNotContain("data-report-days", fullWeek);
        if (start == "2026-09-21") await Fixture("week", fullWeek);
        Assert.False(await db.ProductionDailyCaptures.AnyAsync());
    }

    private static string ReportLink(string html, string marker)
    {
        var tag = Regex.Matches(html, "<a\\b[^>]*>").Select(x => x.Value).Single(x => x.Contains(marker, StringComparison.Ordinal));
        return WebUtility.HtmlDecode(Regex.Match(tag, "href=\"([^\"]+)\"").Groups[1].Value);
    }

    [Theory]
    [InlineData("balance")]
    [InlineData("history")]
    [InlineData("capture")]
    [InlineData("reports")]
    public async Task Report_filters_are_optional_across_production_tabs(string tab)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        foreach (var filters in new[] { "", "&ReportKind=&ReportPeriod=" })
        {
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(
                $"/Operations/Production?Tab={tab}&WeekId={seed.WeekId}&Through={seed.Date:yyyy-MM-dd}{filters}"));
            Assert.DoesNotContain("The ReportKind field is required", html);
            Assert.DoesNotContain("The ReportPeriod field is required", html);
            Assert.DoesNotContain("Por conciliar", html);
            if (tab == "balance")
            {
                var closing = html[html.IndexOf("<section id=\"week-close\"", StringComparison.Ordinal)..];
                Assert.DoesNotContain(">Unidad</th>", closing);
                Assert.DoesNotContain("data-label=\"Unidad\"", closing);
            }
            if (tab == "reports") Assert.Contains("Resumen diario por proceso", html);
        }
    }

    [Fact]
    public async Task Reports_export_and_print_share_filters_and_do_not_write_captures()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var week = await db.ProductionScheduleWeeks.Include(x => x.Lines).SingleAsync(x => x.Id == seed.WeekId);
        for (var i = 4; i <= 28; i++)
            db.ProductionScheduleLines.Add(new() { WeekId = week.Id, Product = new Product { Sku = $"RECOVER-{i:000}", Description = "Report product", BaseUnitId = 1 },
                Sequence = i, PlannedDate = seed.Date, Quantity = 30 });
        await db.SaveChangesAsync();
        var query = $"WeekId={seed.WeekId}&Through={seed.Date:yyyy-MM-dd}&Tab=reports";
        var summary = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/Production?" + query));
        Assert.Contains("Reportes de producción", summary);
        Assert.Contains("Resumen diario por proceso", summary);
        Assert.DoesNotContain(">Unidad</th>", summary);
        Assert.DoesNotContain("data-edit-confirm", summary);
        Assert.DoesNotContain("data-capture-group", summary);
        Assert.Contains("ReportKind=products", summary);
        Assert.Contains("scope=\"colgroup\" colspan=\"3\"", summary);
        await Fixture("daily", await client.GetStringAsync("/Operations/Production?" + query));
        var productsQuery = query + "&ReportKind=products&ReportPeriod=day&Sku=RECOVER";
        var products = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/Production?" + productsQuery));
        Assert.Contains("RECOVER-025", products);
        Assert.DoesNotContain("RECOVER-026", products);
        Assert.Contains("Página 1 / 2", products);
        Assert.Contains("Pendiente para T2", products);
        Assert.DoesNotContain(">Descripción</th>", products);
        Assert.DoesNotContain(">Unidad</th>", products);
        Assert.DoesNotContain(">Por conciliar</th>", products);
        Assert.DoesNotContain("Hidden description", products);
        Assert.Contains("data-report-scroll-top", products);
        var second = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/Production?" + productsQuery + "&ReportPage=2"));
        Assert.Contains("RECOVER-028", second);
        Assert.DoesNotContain("RECOVER-001", second);
        Assert.Contains(">840<", second);
        await Fixture("products", await client.GetStringAsync("/Operations/Production?" + productsQuery));
        var print = await client.GetStringAsync("/Operations/Production?handler=ReportPrint&" + productsQuery + "&ReportPage=2");
        Assert.Contains("RECOVER-001", print);
        Assert.Contains("RECOVER-028", print);
        Assert.DoesNotContain("data-production-daily", print);
        Assert.DoesNotContain("nav-pills", print);
        Assert.DoesNotContain(">Por conciliar</th>", print);
        Assert.Contains("production-reports-a3.", print);
        await Fixture("print", print);
        var bytes = await client.GetByteArrayAsync("/Operations/Production?handler=ReportExport&" + productsQuery + "&ReportPage=2");
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal("RECOVER-001", workbook.Worksheet(1).Cell(9, 1).GetString());
        Assert.Equal("RECOVER-028", workbook.Worksheet(1).Cell(36, 1).GetString());
        Assert.Equal("Programa nuevo", workbook.Worksheet(1).Cell(8, 2).GetString());
        Assert.Equal(840m, workbook.Worksheet(1).Cell(37, 2).GetValue<decimal>());
        var filtered = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/Production?" + productsQuery + "&Area=Cutting"));
        Assert.DoesNotContain(">Costura</th>", filtered);
        Assert.DoesNotContain(">Ready to Pack</th>", filtered);
        var fullWeek = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/Production?" + query + "&ReportKind=products&ReportPeriod=week"));
        Assert.DoesNotContain("Pendiente para T2", fullWeek);
        Assert.False(await db.ProductionDailyCaptures.AnyAsync());
        Assert.False(await db.Set<ProductionBalanceEdit>().AnyAsync());
        var missing = await client.GetAsync($"/Operations/Production?handler=ReportExport&WeekId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static async Task Fixture(string name, string html)
    {
        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_REPORT_FIXTURES");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".html"), html);
    }
}
