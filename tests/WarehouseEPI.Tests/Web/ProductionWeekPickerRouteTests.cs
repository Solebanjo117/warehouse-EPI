using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Tests.Production;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionWeekPickerRouteTests
{
    private const string Page = "/Admin/Production/Schedule";
    private static readonly DateOnly Monday = new(2026, 9, 21);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(65)]
    public async Task Initial_selection_is_first_free_warehouse_week_without_the_60_week_limit(int occupied)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = await AdminClientAsync(factory, "es");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            for (var index = 0; index < occupied; index++)
                db.Add(Week(Monday.AddDays(index * 7), (ProductionScheduleWeekStatus)(index % 3)));
            // A later week must not cause the free gap to be skipped.
            db.Add(Week(Monday.AddDays((occupied + 1) * 7), ProductionScheduleWeekStatus.Closed));
            await db.SaveChangesAsync();
        }
        var html = await client.GetStringAsync(Page + "?ActionPanel=new");
        var expected = Monday.AddDays(occupied * 7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(expected, Selected(html));
        Assert.Contains("data-current-week=\"2026-09-21\"", html); // UTC Monday is Sunday locally.
        Assert.Contains("data-month=\"" + expected[..7] + "\"", html);
        using var verification = factory.Services.CreateScope();
        Assert.Equal(occupied + 1, await verification.ServiceProvider.GetRequiredService<WarehouseDbContext>()
            .ProductionScheduleWeeks.CountAsync());
    }

    [Theory]
    [InlineData("2026-02", 4, "2026-02-02", "2026-03-01")]
    [InlineData("2026-03", 5, "2026-03-02", "2026-04-05")]
    [InlineData("2026-12", 4, "2026-12-07", "2027-01-03")]
    public async Task Month_options_contain_only_full_monday_sunday_ranges(string month, int count, string first, string lastEnd)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = await AdminClientAsync(factory, "en");
        var response = await client.GetAsync(Page + "?handler=WeekOptions&month=" + month);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var options = json.RootElement.GetProperty("options").EnumerateArray().ToArray();
        Assert.Equal(count, options.Length);
        Assert.Equal(first, options[0].GetProperty("weekStart").GetString());
        Assert.Equal(lastEnd, options[^1].GetProperty("weekEnd").GetString());
        foreach (var option in options)
        {
            var start = DateOnly.Parse(option.GetProperty("weekStart").GetString()!, CultureInfo.InvariantCulture);
            var end = DateOnly.Parse(option.GetProperty("weekEnd").GetString()!, CultureInfo.InvariantCulture);
            Assert.Equal(DayOfWeek.Monday, start.DayOfWeek);
            Assert.Equal(start.AddDays(6), end);
            Assert.StartsWith("Monday ", option.GetProperty("label").GetString());
        }
        if (month == "2026-12") Assert.Contains("2027", options[^1].GetProperty("label").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-2")]
    [InlineData("2026-13")]
    [InlineData("2026-09-01")]
    public async Task Invalid_month_is_rejected(string month)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = await AdminClientAsync(factory, "es");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Page + "?handler=WeekOptions&month=" + month)).StatusCode);
    }

    [Fact]
    public async Task Options_require_admin_authentication()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        foreach (var query in new[] { "WeekOptions&month=2026-09", "NewWeekContext&weekStart=2026-09-21",
            "NewWeekOperation&operationId=" + Guid.NewGuid(), "WorkspaceOpenings&weekStart=2026-09-21" })
        {
            var response = await client.GetAsync(Page + "?handler=" + query);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("Login", response.Headers.Location!.OriginalString);
        }
    }

    [Theory]
    [InlineData("es", true)]
    [InlineData("en", true)]
    [InlineData("es", false)]
    [InlineData("en", false)]
    public async Task Selector_renders_available_and_existing_weeks_and_exports_browser_fixtures(string language, bool ready)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = await AdminClientAsync(factory, language, ready);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            db.AddRange(Week(Monday.AddDays(-14), ProductionScheduleWeekStatus.Closed),
                Week(Monday.AddDays(7), ProductionScheduleWeekStatus.Draft));
            await db.SaveChangesAsync();
        }
        var html = await client.GetStringAsync(Page + "?ActionPanel=new&View=summary&SelectedDay=2026-09-21");
        Assert.Equal("2026-09-21", Selected(html));
        Assert.DoesNotContain("type=\"date\"", Picker(html));
        Assert.Contains(language == "en" ? "Week to create" : "Semana a crear", html);
        Assert.Contains(language == "en" ? "Already created" : "Ya creada", html);
        Assert.Matches("<input(?=[^>]*value=\"2026-09-28\")(?=[^>]*disabled)[^>]*>", html);
        var pin = Regex.Match(html, "<input[^>]*name=\"NewWeek.Pin\"[^>]*>").Value;
        Assert.Contains("disabled", pin);
        Assert.Contains("data-new-week=\"true\"", html);
        Assert.DoesNotContain("data-workspace-view=", html);
        if (Environment.GetEnvironmentVariable("WAREHOUSE_WEEK_PICKER_FIXTURES") is string directory)
        {
            Directory.CreateDirectory(directory);
            html = Regex.Replace(html, "(name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]*", "$1");
            await File.WriteAllTextAsync(Path.Combine(directory, $"{(ready ? "ready" : "blocked")}-{language}.html"), html);
            await File.WriteAllTextAsync(Path.Combine(directory, $"products-{(ready ? "ready" : "blocked")}-{language}.json"), await client.GetStringAsync(Page + "?handler=WorkspaceProducts&q=FG"));
            using (var scope = factory.Services.CreateScope())
            {
                var id = await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionScheduleWeeks
                    .Where(x => x.Status == ProductionScheduleWeekStatus.Closed).Select(x => x.Id).FirstAsync();
                await File.WriteAllTextAsync(Path.Combine(directory, $"closed-{language}.html"), await client.GetStringAsync(Page + "?WeekId=" + id));
            }
            foreach (var month in new[] { "2026-08", "2026-09", "2026-10", "2026-11", "2026-12", "2027-01" })
            {
                await File.WriteAllTextAsync(Path.Combine(directory, $"{month}-{language}.json"),
                    await client.GetStringAsync(Page + "?handler=WeekOptions&month=" + month));
                using var options = JsonDocument.Parse(await client.GetStringAsync(Page + "?handler=WeekOptions&month=" + month));
                foreach (var option in options.RootElement.GetProperty("options").EnumerateArray())
                {
                    var date = option.GetProperty("weekStart").GetString();
                    await File.WriteAllTextAsync(Path.Combine(directory, $"context-{(ready ? "ready" : "blocked")}-{date}-{language}.json"),
                        await client.GetStringAsync(Page + "?handler=NewWeekContext&weekStart=" + date));
                }
            }
        }
    }

    [Fact]
    public async Task New_preparation_reviews_without_writes_preserves_errors_and_opens_in_one_confirmation()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = await AdminClientAsync(factory, "es");
        var html = await client.GetStringAsync(Page + "?ActionPanel=new");
        Assert.Contains("data-workspace-form", html);
        Assert.DoesNotContain("data-week-picker-create", html);
        var operation = Guid.NewGuid();
        var start = new DateOnly(2026, 10, 5);
        Guid product;
        using (var scope = factory.Services.CreateScope()) product = await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>()
            .Products.Where(x => x.Sku == "FG-100").Select(x => x.Id).SingleAsync();
        var changes = new[] { new ProductionScheduleDraftChange("add", null, null, new(start.AddDays(6), product, 12, "ORDER-1", null, null, "Sunday")) };
        var input = new { operationId = operation, weekStart = start, changes };
        using var request = new HttpRequestMessage(HttpMethod.Post, Page + "?handler=NewWeekReview") { Content = JsonContent.Create(input) };
        request.Headers.Add("RequestVerificationToken", Token(html));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var review = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(review.RootElement.GetProperty("canConfirm").GetBoolean());
        using (var scope = factory.Services.CreateScope()) Assert.Empty(await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionScheduleWeeks.ToListAsync());
        var payload = JsonSerializer.Serialize(new { operationId = operation, weekStart = start, changes,
            reviewedFingerprint = review.RootElement.GetProperty("fingerprint").GetString() }, JsonSerializerOptions.Web);
        async Task<HttpResponseMessage> Confirm(string pin) => await client.PostAsync(Page + "?handler=CreateWeek", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["NewWeek.OperationId"] = operation.ToString(), ["NewWeek.WeekStart"] = "2026-10-05", ["NewWeek.Pin"] = pin,
            ["payload"] = payload, ["__RequestVerificationToken"] = Token(html), ["View"] = "program" }));
        using var rejected = await Confirm("0000"); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var saved = await Confirm("0123"); Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var savedJson = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        var url = savedJson.RootElement.GetProperty("url").GetString();
        var program = await client.GetStringAsync(url);
        Assert.DoesNotContain("data-new-week=\"true\"", program);
        Assert.DoesNotContain("data-workspace-view=", program);
        using var repeated = await Confirm("0123"); Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        using var status = JsonDocument.Parse(await client.GetStringAsync(Page + "?handler=NewWeekOperation&operationId=" + operation));
        Assert.True(status.RootElement.GetProperty("saved").GetBoolean());
        using var verification = factory.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Equal(ProductionScheduleWeekStatus.Open, (await db.ProductionScheduleWeeks.SingleAsync()).Status);
        Assert.Equal(start.AddDays(6), (await db.ProductionScheduleLines.SingleAsync()).PlannedDate);
    }

    [Fact]
    public async Task Creation_preserves_valid_selection_rejects_tuesday_and_handles_a_new_duplicate_idempotently()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Factory(original);
        using var client = await AdminClientAsync(factory, "es");
        var html = await client.GetStringAsync(Page + "?ActionPanel=new");
        var operation = Guid.NewGuid().ToString();
        var rejected = await Post(client, html, operation, "2026-10-05", "0000");
        var failed = await rejected.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("2026-10-05", Selected(failed));
        Assert.Contains(operation, failed);
        Assert.Matches("<input(?=[^>]*name=\"NewWeek.Pin\")(?=[^>]*value=\"\")[^>]*>", failed);
        var tuesday = await Post(client, failed, operation, "2026-10-06", "0123");
        var invalid = await tuesday.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, tuesday.StatusCode);
        Assert.Contains("La semana debe iniciar en lunes.", invalid);
        Assert.Empty(Selected(invalid));
        using (var scope = factory.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionScheduleWeeks.ToListAsync());
        var created = await Post(client, invalid, operation, "2026-10-05", "0123");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var repeated = await Post(client, invalid, operation, "2026-10-05", "0123");
        Assert.Equal(HttpStatusCode.Redirect, repeated.StatusCode);
        Assert.Equal(created.Headers.Location!.OriginalString.Split('&')[0], repeated.Headers.Location!.OriginalString.Split('&')[0]);
        // The week did not exist when this form was rendered; checking again prevents a duplicate.
        var concurrent = await Post(client, html, Guid.NewGuid().ToString(), "2026-10-05", "");
        Assert.Equal(HttpStatusCode.Redirect, concurrent.StatusCode);
        using var verification = factory.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Single(await db.ProductionScheduleWeeks.ToListAsync());
        Assert.Single(await db.ProductionScheduleRevisions.ToListAsync());
    }

    private static WebApplicationFactory<Program> Factory(AdminRouteTests.WarehouseApplicationFactory original) =>
        original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new CalendarClock());
                services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
                    Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
                    options => options.TimeProvider = TimeProvider.System);
            });
        });

    private static async Task<HttpClient> AdminClientAsync(WebApplicationFactory<Program> factory, string language, bool ready = true)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            if (ready) await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var actor = new User { FullName = "Week picker admin", RoleId = (await db.Roles.SingleAsync(role => role.Code == "ADMIN")).Id,
                PinLookup = "", PinHash = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(actor, "0123");
            db.Add(actor); await db.SaveChangesAsync();
        }
        var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        var html = await client.GetStringAsync("/Admin/Login");
        var result = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = Token(html) }));
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        return client;
    }

    private static ProductionScheduleWeek Week(DateOnly start, ProductionScheduleWeekStatus status) => new()
    {
        WeekStart = start, WeekEnd = start.AddDays(6), Status = status,
        RequestFingerprint = "week-picker-fixture", OperationId = Guid.NewGuid(), Version = 1
    };
    private static Task<HttpResponseMessage> Post(HttpClient client, string html, string operation, string start, string pin) =>
        client.PostAsync(Page + "?handler=CreateWeek", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["NewWeek.OperationId"] = operation, ["NewWeek.WeekStart"] = start, ["NewWeek.Pin"] = pin,
            ["View"] = "summary", ["SelectedDay"] = "2026-09-21", ["__RequestVerificationToken"] = Token(html)
        }));
    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html,
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private static string Selected(string html) => Regex.Match(html,
        "<input(?=[^>]*checked)[^>]*name=\"NewWeek.WeekStart\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;
    private static string Picker(string html) => Regex.Match(html, "<form[^>]*data-week-picker[\\s\\S]*?</form>").Value;
    private sealed class CalendarClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 28, 2, 0, 0, TimeSpan.Zero);
    }
}
