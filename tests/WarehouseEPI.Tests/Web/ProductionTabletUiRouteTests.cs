using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Production;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionTabletUiRouteTests
{
    private const string TestPin = "4826";
    private const string LongSku = "TABLET-LONG-SKU-ABCDEFGHIJKLMNOPQRSTUVWXYZ-0123456789-ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Order = "ORDER-TABLET-ABCDEFGHIJKLMNOPQRSTUVWXYZ-0123456789-ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string OriginalType = "ORIGINAL-TYPE-TABLET";
    private const string Notes = "NOTES-TABLET: verify the order and original type remain separate.";

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Schedule_cells_keep_their_labels_and_values_in_both_table_layouts(string language)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = Client(factory, language);
        var seed = await SeedAsync(factory);
        await LoginAsync(client);
        var english = language == "en";
        string[] dailyLabels = ["#", "SKU", english ? "Quantity" : "Cantidad", english ? "Destination" : "Destino",
            english ? "Original type" : "Tipo original", english ? "Orders and annotations" : "Pedidos y anotaciones"];
        string[] weeklyLabels = ["SKU", english ? "Day" : "Día", dailyLabels[2], dailyLabels[3], english ? "Details" : "Detalles"];
        var url = $"/Admin/Production/Schedule?WeekId={seed.WeekId}&SelectedDay=2026-09-21";
        var daily = await client.GetStringAsync(url);
        VerifyCells(daily, dailyLabels, false);
        VerifyHeader(daily, language, "program", ProductionScheduleWeekStatus.Draft, seed.WeekId);
        await FixtureAsync(language, "program-draft", daily);
        var weekly = await client.GetStringAsync(url + "&View=week");
        VerifyCells(weekly, weeklyLabels, true);
        VerifyHeader(weekly, language, "week", ProductionScheduleWeekStatus.Draft, seed.WeekId);
        await FixtureAsync(language, "products", weekly);
        foreach (var view in new[] { "summary", "review" })
        {
            var page = await client.GetStringAsync(url + "&View=" + view);
            VerifyHeader(page, language, view, ProductionScheduleWeekStatus.Draft, seed.WeekId);
            if (view == "review") Assert.Equal(string.Empty, Input(page, "Publish.Pin"));
            await FixtureAsync(language, view == "review" ? "review" : "summary-draft", page);
        }
        foreach (var status in new[] { ProductionScheduleWeekStatus.Open, ProductionScheduleWeekStatus.Closed })
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            (await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId)).Status = status;
            await db.SaveChangesAsync();
            var page = await client.GetStringAsync(url);
            VerifyCells(page, dailyLabels, false);
            VerifyHeader(page, language, "program", status, seed.WeekId);
            Assert.Contains($"data-week-status=\"{status}\"", page);
            await FixtureAsync(language, "program-" + status.ToString().ToLowerInvariant(), page);
            foreach (var view in new[] { "week", "summary" })
            {
                var other = await client.GetStringAsync(url + "&View=" + view);
                VerifyHeader(other, language, view, status, seed.WeekId);
                await FixtureAsync(language, (view == "week" ? "products-" : "summary-") + status.ToString().ToLowerInvariant(), other);
            }
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            (await db.ProductionDailyConfigurations.SingleAsync()).CuttingStageId = null;
            (await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId)).Status = ProductionScheduleWeekStatus.Draft;
            await db.SaveChangesAsync();
        }
        var blocked = await client.GetStringAsync(url + "&View=review");
        VerifyHeader(blocked, language, "review", ProductionScheduleWeekStatus.Draft, seed.WeekId);
        Assert.Contains("data-server-blocked=\"true\"", blocked);
        Assert.Contains("daily-configuration", blocked);
        Assert.Equal(string.Empty, Input(blocked, "Publish.Pin"));
        await FixtureAsync(language, "review-blocked", blocked);
        Guid sourceId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var target = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId);
            var source = new ProductionScheduleWeek
            {
                OperationId = Guid.NewGuid(),
                RequestFingerprint = "tablet-copy-fixture",
                WeekStart = new(2026, 9, 14),
                WeekEnd = new(2026, 9, 20),
                Status = ProductionScheduleWeekStatus.Open,
                CreatedByUserId = target.CreatedByUserId,
                CreatedAt = DateTimeOffset.UtcNow,
                Version = 1,
                ExplicitCarryover = true,
                Lines = [new() { PlannedDate = new(2026, 9, 14), ProductId = (await db.Products.SingleAsync(x => x.Sku == LongSku)).Id,
                    Quantity = 9, Sequence = 1, Version = 1, Notes = "SOURCE-NOTES" }]
            };
            db.Add(source); await db.SaveChangesAsync(); sourceId = source.Id;
        }
        var copy = await client.GetStringAsync(url + "&View=program");
        Assert.Contains($"value=\"{sourceId}\"", copy);
        await FixtureAsync(language, "program-copy", copy);
        var copyData = await client.GetStringAsync($"/Admin/Production/Schedule?handler=WorkspaceCopy&weekId={seed.WeekId}&sourceWeekId={sourceId}");
        Assert.Contains(LongSku, copyData);
        await FixtureAsync(language, "copy-source", copyData, ".json");
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Workspace_without_captures_allows_independent_row_deletion(string language)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = Client(factory, language);
        var seed = await SeedAsync(factory, includeCaptures: false);
        await LoginAsync(client);
        var page = await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={seed.WeekId}&SelectedDay=2026-09-21");
        Assert.Equal(2, Regex.Count(page, "&quot;CanRemove&quot;:true"));
        await FixtureAsync(language, "program-editable", page);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Empty_schedule_preserves_creation_fields_and_empty_pin(string language)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = Client(factory, language);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var admin = new User { FullName = "Tablet UI admin", RoleId = 1, PinLookup = "", PinHash = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(admin, TestPin);
            db.Add(admin); await db.SaveChangesAsync();
        }
        await LoginAsync(client);
        var page = await client.GetStringAsync("/Admin/Production/Schedule");
        Assert.Contains("data-new-week=\"true\"", page);
        Assert.Equal(1, Regex.Count(page, "data-week-picker data-options-url"));
        Assert.DoesNotContain("production-schedule-sections", page);
        Assert.Contains("handler=CreateWeek", page);
        Assert.Equal(string.Empty, Input(page, "NewWeek.Pin"));
        await FixtureAsync(language, "empty", page);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task History_labels_target_unique_fields_and_reverse_requests_admin_pin(string language)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = Client(factory, language);
        var seed = await SeedAsync(factory);
        var url = $"/Operations/Production?Tab=history&WeekId={seed.WeekId}";
        var anonymous = await client.GetStringAsync(url);
        Assert.Contains("name=\"Reverse.Pin\"", anonymous);
        await LoginAsync(client);
        var page = await client.GetStringAsync(url);
        var forms = Regex.Matches(page, "<form\\b[^>]*handler=Reverse[^>]*>.*?</form>", RegexOptions.Singleline);
        Assert.Equal(2, forms.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in seed.ActiveCaptureIds)
        {
            var form = Assert.Single(forms.Cast<Match>(), match => match.Value.Contains(id.ToString(), StringComparison.Ordinal)).Value;
            Assert.Equal(seed.WeekId.ToString(), Input(form, "WeekId"));
            Assert.True(Guid.TryParse(Input(form, "Reverse.OperationId"), out _));
            foreach (var field in new[] { "reason", "pin" })
            {
                var fieldId = $"reverse-{field}-{id}";
                Assert.True(ids.Add(fieldId));
                var label = Regex.Match(form, $"<label[^>]*for=\"{fieldId}\"[^>]*>(.*?)</label>", RegexOptions.Singleline);
                Assert.True(label.Success);
                var expected = field == "reason" ? language == "en" ? "Reason required" : "Motivo obligatorio"
                    : language == "en" ? "ADMIN PIN" : "NIP ADMIN";
                Assert.Equal(expected, WebUtility.HtmlDecode(label.Groups[1].Value));
                Assert.Contains($"id=\"{fieldId}\"", form);
                Assert.Contains($"name=\"Reverse.{(field == "reason" ? "Reason" : "Pin")}\"", form);
            }
            var pin = Regex.Match(form, "<input[^>]*name=\"Reverse.Pin\"[^>]*>").Value;
            Assert.Contains("type=\"password\"", pin);
            Assert.Contains("inputmode=\"numeric\"", pin);
            Assert.Contains("autocomplete=\"off\"", pin);
            Assert.Equal(string.Empty, Input(form, "Reverse.Pin"));
        }
        var reversedRow = Regex.Match(page, $"<tr id=\"capture-{seed.ReversedCaptureId}\">.*?</tr>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(reversedRow);
        Assert.DoesNotContain("Reverse.Pin", reversedRow);
        await FixtureAsync(language, "history", page);
    }

    private static void VerifyCells(string page, string[] labels, bool weekly)
    {
        if (!weekly)
        {
            Assert.True(Regex.Count(page, "<table\\b") == 1);
            Assert.Contains("production-week-workspace__table", page);
            Assert.Contains("data-workspace-rows", page);
            Assert.DoesNotContain("production-schedule-table", page);
            Assert.Contains(LongSku, page);
            Assert.Contains(OriginalType, page);
            Assert.Contains(Order, page);
            Assert.Contains(Notes, page);
            var english = labels.Contains("Quantity");
            Assert.Contains(english ? "Opening carryover" : "Arrastre inicial", page);
            Assert.Contains(english ? "Cutting" : "Corte", page);
            Assert.Contains(english ? "Sewing" : "Costura", page);
            Assert.Contains("Ready to Pack", page);
            Assert.Contains("data-workspace-day-head=\"0\"", page);
            return;
        }
        var table = Regex.Match(page, "<table[^>]*production-schedule-table[^>]*>(.*?)</table>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(table);
        var rows = Regex.Matches(table, "<tbody>(.*?)</tbody>", RegexOptions.Singleline).Single().Groups[1].Value;
        var row = Regex.Match(rows, "<tr>(.*?)</tr>", RegexOptions.Singleline).Groups[1].Value;
        var cells = Regex.Matches(row, "<td\\b([^>]*)>(.*?)</td>", RegexOptions.Singleline);
        Assert.Equal(labels.Length + 1, cells.Count);
        for (var i = 0; i < labels.Length; i++)
        {
            var actual = Regex.Match(cells[i].Groups[1].Value, "data-label=\"([^\"]*)\"").Groups[1].Value;
            Assert.Equal(labels[i], WebUtility.HtmlDecode(actual));
        }
        Assert.Contains(LongSku, cells[weekly ? 0 : 1].Value);
        Assert.Contains(OriginalType, cells[4].Value);
        Assert.Contains(Order, cells[weekly ? 4 : 5].Value);
        Assert.Contains(Notes, cells[weekly ? 4 : 5].Value);
        Assert.Contains("production-schedule-cell--sku", cells[weekly ? 0 : 1].Value);
        Assert.Contains("production-schedule-cell--details", cells[weekly ? 4 : 5].Value);
        if (weekly)
        {
            var details = cells[4].Value;
            Assert.Equal(5, Regex.Count(details, "<dt>"));
            Assert.Matches("<dt>[^<]+</dt><dd>" + Order, details);
            Assert.Matches("<dt>[^<]+</dt><dd>" + OriginalType, details);
            Assert.Matches("<dt>[^<]+</dt><dd>" + Regex.Escape(Notes), details);
            Assert.Contains("SelectedDay=2026-09-21", cells[^1].Value);
            Assert.Contains("View=program", cells[^1].Value);
            var emptyDetails = WebUtility.HtmlDecode(Regex.Matches(rows, "<tr>(.*?)</tr>", RegexOptions.Singleline)[1].Value);
            foreach (var value in labels[1] == "Day"
                ? new[] { "No orders", "Not specified at source", "No notes", "No annotation" }
                : new[] { "Sin pedidos", "Sin especificar en origen", "Sin notas", "Sin anotación" })
                Assert.Contains(value, emptyDetails);
        }
        Assert.Contains("production-schedule-cell--actions", cells[^1].Value);
        Assert.Equal(2, Regex.Count(rows, "<tr>"));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Summary_and_review_preserve_each_unit_daily_values_counts_and_carryover(string language)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = Client(factory, language);
        var seed = await SeedAsync(factory);
        IReadOnlyList<ProductionSchedulePlanSummaryRow> expected;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId);
            week.ExplicitCarryover = false;
            var unit = new Unit { Id = 99, Code = "M", Name = "Meters", AllowsDecimals = true };
            var product = new Product { Sku = "METER-SKU", BaseUnitId = unit.Id };
            db.Add(unit); db.Add(product);
            db.Add(new ProductionScheduleLine
            {
                WeekId = seed.WeekId,
                ProductId = product.Id,
                PlannedDate = week.WeekStart.AddDays(1),
                Quantity = 1.2345m,
                Sequence = 1,
                Notes = "DECIMAL-NOTES"
            });
            db.Add(new ProductionScheduleLine
            {
                WeekId = seed.WeekId,
                ProductId = product.Id,
                PlannedDate = week.WeekStart,
                Quantity = 3.25m,
                IsCarryover = true,
                Sequence = 3
            });
            db.Add(new ProductionCarryoverPlan
            {
                WeekId = seed.WeekId,
                ProductId = product.Id,
                PlannedDate = week.WeekStart.AddDays(2),
                Area = ProductionDailyArea.Cutting,
                Quantity = 7.75m
            });
            await db.SaveChangesAsync();
            expected = await scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>().GetPlanSummaryAsync(seed.WeekId);
        }
        await LoginAsync(client);
        foreach (var view in new[] { "summary", "review" })
        {
            var page = await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={seed.WeekId}&View={view}");
            var units = Regex.Matches(page, "<section[^>]*data-summary-unit=\"([^\"]+)\"[^>]*>(.*?)</section>", RegexOptions.Singleline);
            Assert.Equal(expected.Select(x => x.Unit).Distinct(), units.Cast<Match>().Select(x => x.Groups[1].Value));
            foreach (var group in expected.GroupBy(x => x.Unit))
            {
                var section = units.Cast<Match>().Single(x => x.Groups[1].Value == group.Key).Value;
                Assert.Equal(7, Regex.Count(section, "data-summary-day="));
                var totals = Regex.Match(section, "data-summary-totals>(.*?)</dl>", RegexOptions.Singleline).Value;
                Assert.Equal(new[] { group.Sum(x => x.NewQuantity), group.Sum(x => x.CustomerQuantity), group.Sum(x => x.StockQuantity) }
                    .Select(x => x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " " + group.Key),
                    Regex.Matches(totals, "<dd>(.*?)</dd>").Cast<Match>().Select(x => WebUtility.HtmlDecode(x.Groups[1].Value).Replace(',', '.')));
                var totalDetail = Regex.Match(section, "data-summary-total-detail>(.*?)</details>", RegexOptions.Singleline).Value;
                Assert.Equal(new[] { group.Sum(x => x.NewLines).ToString(), group.Sum(x => x.CustomerLines).ToString(), group.Sum(x => x.StockLines).ToString(),
                    group.Sum(x => x.ImportedOpening).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " " + group.Key,
                    group.Sum(x => x.PlannedCarryover).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " " + group.Key },
                    Regex.Matches(totalDetail, "<dd[^>]*>(.*?)</dd>").Cast<Match>().Select(x => WebUtility.HtmlDecode(x.Groups[1].Value).Replace(',', '.')));
                foreach (var row in group)
                {
                    var markup = Regex.Match(section, $"<tr data-summary-day=\"{row.Day:yyyy-MM-dd}\">(.*?)</tr>", RegexOptions.Singleline).Value;
                    var quantities = Regex.Matches(markup, "data-label=\"[^\"]+\">([^<]+)</td>").Cast<Match>().Select(x => WebUtility.HtmlDecode(x.Groups[1].Value).Replace(',', '.'));
                    Assert.Equal(new[] { row.NewQuantity, row.CustomerQuantity, row.StockQuantity }.Select(x => x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " " + row.Unit), quantities);
                    var details = Regex.Matches(markup, "<dd>(.*?)</dd>").Cast<Match>().Select(x => x.Groups[1].Value.Replace(',', '.'));
                    Assert.Equal(new[] { row.NewLines.ToString(), row.CustomerLines.ToString(), row.StockLines.ToString(),
                        row.ImportedOpening.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " " + row.Unit,
                        row.PlannedCarryover.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " " + row.Unit }, details);
                }
                Assert.Contains(language == "en" ? "View counts and carryover" : "Ver conteos y arrastres", section);
            }
            if (view == "review") Assert.Equal(string.Empty, Input(page, "Publish.Pin"));
            await FixtureAsync(language, view + "-units", page);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            db.RemoveRange(await db.ProductionScheduleLines.Where(x => x.WeekId == seed.WeekId).ToListAsync());
            db.RemoveRange(await db.ProductionCarryoverPlans.Where(x => x.WeekId == seed.WeekId).ToListAsync());
            await db.SaveChangesAsync();
        }
        var empty = await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={seed.WeekId}&View=summary");
        Assert.Contains("data-summary-empty", empty);
        Assert.DoesNotContain("data-summary-unit=", empty);
        await FixtureAsync(language, "summary-empty", empty);
    }

    private static void VerifyHeader(string page, string language, string view, ProductionScheduleWeekStatus status, Guid weekId)
    {
        Assert.Equal(1, Regex.Count(page, "data-schedule-context"));
        Assert.Equal(1, Regex.Count(page, "class=\"production-schedule__context\""));
        Assert.Equal(1, Regex.Count(page, "data-schedule-week-select"));
        Assert.Equal(1, Regex.Count(page, "data-schedule-week-status"));
        Assert.DoesNotContain("production-week-workspace__context", page);
        var selector = Regex.Match(page, "<select[^>]*data-schedule-week-select[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        Assert.Contains("21/09/2026", selector);
        Assert.Contains("27/09/2026", selector);
        Assert.DoesNotContain("Borrador", selector);
        Assert.DoesNotContain("Draft", selector);
        var nav = Regex.Match(page, "<nav[^>]*production-schedule-sections[^>]*>(.*?)</nav>", RegexOptions.Singleline).Value;
        var links = Regex.Matches(nav, "<a\\b[^>]*>.*?</a>", RegexOptions.Singleline).Cast<Match>().Select(x => x.Value).ToArray();
        Assert.Equal(3, links.Length);
        Assert.Equal(1, Regex.Count(nav, "aria-current=\"page\""));
        string[] labels = language == "en" ? ["Schedule", "Products", "Summary"] : ["Programa", "Productos", "Resumen"];
        string[] routes = ["program", "week", "summary"];
        for (var i = 0; i < 3; i++)
        {
            Assert.Contains($">{labels[i]}</a>", links[i]);
            Assert.Contains("View=" + routes[i], links[i]);
            Assert.Contains("WeekId=" + weekId, links[i]);
            Assert.Contains("SelectedDay=2026-09-21", links[i]);
            Assert.Equal(routes[i] == (view == "review" ? "program" : view), links[i].Contains("aria-current=\"page\""));
        }
        if (status == ProductionScheduleWeekStatus.Draft)
        {
            Assert.DoesNotContain("data-schedule-status-trigger", page);
            Assert.Equal(view != "review", page.Contains("data-schedule-open-review"));
            if (view == "review") Assert.Contains(language == "en" ? "Back to editing" : "Volver a editar", WebUtility.HtmlDecode(page));
        }
        else
        {
            Assert.Contains("data-schedule-status-trigger", page);
            Assert.Contains("Tab=balance", page);
            Assert.DoesNotContain("data-schedule-open-review", page);
        }
        Assert.Contains("ActionPanel=new", page);
        Assert.Contains("ActionPanel=config", page);
        Assert.Contains("ScheduleImport", page);
        Assert.Contains("handler=Export", page);
    }

    private static WebApplicationFactory<Program> Factory(AdminRouteTests.WarehouseApplicationFactory original) =>
        original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));

    private static HttpClient Client(WebApplicationFactory<Program> factory, string language)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        return client;
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.GetStringAsync("/Admin/Login");
        var response = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Pin"] = TestPin, ["__RequestVerificationToken"] = Input(login, "__RequestVerificationToken") }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<(Guid WeekId, Guid[] ActiveCaptureIds, Guid ReversedCaptureId)> SeedAsync(WebApplicationFactory<Program> factory, bool includeCaptures = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var admin = new User { FullName = "Tablet UI admin", RoleId = 1, PinLookup = "", PinHash = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(admin, TestPin);
        var product = new Product { Sku = LongSku, BaseUnitId = 1 };
        db.Add(admin); db.Add(product); await db.SaveChangesAsync();
        var schedule = scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
        var monday = new DateOnly(2026, 9, 21);
        var weekId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), monday, admin.Id))).Id!.Value;
        var week = (await schedule.GetWeekAsync(weekId))!;
        Assert.True((await schedule.SaveLineAsync(new(Guid.NewGuid(), weekId, null, week.Version, null,
            monday, product.Id, 30, Order, "ORDER-SECOND", null, Notes, admin.Id,
            OriginalType: OriginalType, OriginalAnnotation1: "ANNOTATION-TABLET"))).Success);
        week = (await schedule.GetWeekAsync(weekId))!;
        Assert.True((await schedule.SaveLineAsync(new(Guid.NewGuid(), weekId, null, week.Version, null,
            monday, product.Id, 10, null, null, null, null, admin.Id))).Success);
        if (!includeCaptures) return (weekId, [], Guid.Empty);
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var captures = Enumerable.Range(0, 3).Select(i => new ProductionDailyCapture
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString(),
            WeekId = weekId,
            EffectiveDate = monday,
            Area = ProductionDailyArea.Cutting,
            StageId = config.CuttingStageId!.Value,
            ShiftId = config.Shift1Id!.Value,
            ProductId = product.Id,
            Quantity = i + 1,
            ResponsibleUserId = admin.Id,
            RecordedAt = DateTimeOffset.UtcNow,
            Status = i == 2 ? ProductionDailyCaptureStatus.Reversed : ProductionDailyCaptureStatus.Active
        }).ToArray();
        db.AddRange(captures); await db.SaveChangesAsync();
        return (weekId, captures.Take(2).Select(x => x.Id).ToArray(), captures[2].Id);
    }

    private static string Input(string html, string name) => Regex.Match(html,
        $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;

    private static async Task FixtureAsync(string language, string name, string html, string extension = ".html")
    {
        if (Environment.GetEnvironmentVariable("WAREHOUSE_TABLET_UI_FIXTURES") is not string directory) return;
        Directory.CreateDirectory(directory);
        html = Regex.Replace(html, "(name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]*", "$1");
        await File.WriteAllTextAsync(Path.Combine(directory, $"{name}-{language}{extension}"), html);
    }
}
