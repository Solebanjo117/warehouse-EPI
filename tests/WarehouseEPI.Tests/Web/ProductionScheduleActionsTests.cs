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

public sealed class ProductionScheduleActionsTests
{
    [Theory]
    [InlineData("es", "Revisar y abrir semana", "Confirmar apertura", "Confirmar cierre", "Confirmar reapertura")]
    [InlineData("en", "Review and open week", "Confirm opening", "Confirm closure", "Confirm reopening")]
    public async Task Actions_are_localized_and_get_confirmation_never_changes_week(
        string language, string open, string publish, string close, string reopen)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        Guid weekId, userId;
        var monday = new DateOnly(2026, 9, 21);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var user = new User { FullName = "Schedule actions admin",
                RoleId = (await db.Roles.SingleAsync(x => x.Code == "ADMIN")).Id, PinLookup = "", PinHash = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.Users.Add(user); await db.SaveChangesAsync(); userId = user.Id;
            var service = scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
            weekId = (await service.CreateWeekAsync(new(Guid.NewGuid(), monday, userId))).Id!.Value;
            var productId = (await db.Products.SingleAsync(x => x.Sku == "FG-100")).Id;
            Assert.True((await service.SaveLineAsync(new(Guid.NewGuid(), weekId, null, 0, null,
                monday, productId, 20, null, null, null, null, userId))).Success);
        }
        const string page = "/Admin/Production/Schedule";
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"{page}?WeekId={weekId}&ActionPanel=close")).StatusCode);
        var login = await client.GetStringAsync("/Admin/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Admin/Login", login, new() { ["Input.Pin"] = "0123" })).StatusCode);
        var draft = await client.GetStringAsync($"{page}?WeekId={weekId}");
        Assert.Contains(open, WebUtility.HtmlDecode(draft));
        Assert.Contains(language == "en" ? "Prepare copy" : "Preparar copia", WebUtility.HtmlDecode(draft));
        Assert.DoesNotContain("data-schedule-status-modal", draft);
        Assert.Contains("data-workspace-copy-toggle aria-expanded=\"false\"", draft);
        Assert.Contains("data-workspace-copy-panel hidden", draft);
        Assert.Matches("<button[^>]*data-workspace-copy[^>]*disabled", draft);
        await Fixture(language, "draft", draft);
        var review = await client.GetStringAsync($"{page}?WeekId={weekId}&View=review&SelectedDay=2026-09-22");
        Assert.Contains(publish, WebUtility.HtmlDecode(review));
        Assert.Contains("data-server-blocked=\"false\"", review);
        Assert.Contains("value=\"\"", Regex.Match(review, "<input[^>]*name=\"Publish.Pin\"[^>]*>").Value);
        await Fixture(language, "review", review);
        var operationId = Guid.NewGuid().ToString();
        var input = new Dictionary<string, string>
        {
            ["Publish.OperationId"] = operationId, ["Publish.WeekId"] = weekId.ToString(),
            ["Publish.ExpectedVersion"] = Input(review, "Publish.ExpectedVersion"), ["Publish.Pin"] = "0000"
        };
        var failed = await Post(client, page + "?handler=Publish", review, input);
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Contains("value=\"\"", Regex.Match(await failed.Content.ReadAsStringAsync(), "<input[^>]*name=\"Publish.Pin\"[^>]*>").Value);
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(ProductionScheduleWeekStatus.Draft, (await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionScheduleWeeks.SingleAsync(x => x.Id == weekId)).Status);
        input["Publish.Pin"] = "0123";
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, page + "?handler=Publish", review, input)).StatusCode);
        var openHtml = await client.GetStringAsync($"{page}?WeekId={weekId}&View=summary&SelectedDay=2026-09-22");
        await Fixture(language, "open", openHtml);
        var creating = await client.GetStringAsync($"{page}?WeekId={weekId}&View=summary&ActionPanel=new&SelectedDay=2026-09-22");
        var invalidCreation = await Post(client, page + "?handler=CreateWeek", creating, new()
        {
            ["NewWeek.OperationId"] = Guid.NewGuid().ToString(), ["NewWeek.WeekStart"] = "2026-09-22",
            ["WeekId"] = Input(creating, "WeekId"), ["View"] = Input(creating, "View"), ["SelectedDay"] = Input(creating, "SelectedDay")
        });
        Assert.Equal(HttpStatusCode.OK, invalidCreation.StatusCode);
        var creationError = await invalidCreation.Content.ReadAsStringAsync();
        Assert.Equal("summary", Input(creationError, "View"));
        Assert.Equal(weekId.ToString(), Input(creationError, "WeekId"));
        Assert.Equal("2026-09-22", Input(creationError, "SelectedDay"));
        var closing = await client.GetStringAsync($"{page}?WeekId={weekId}&View=summary&ActionPanel=close&SelectedDay=2026-09-22");
        Assert.Contains(close, WebUtility.HtmlDecode(closing));
        Assert.Contains("data-auto-show=\"true\"", closing);
        Assert.Single(Regex.Matches(closing, "<form[^>]*handler=Close[^>]*>").Cast<Match>());
        uint version;
        using (var scope = factory.Services.CreateScope())
        {
            var week = await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionScheduleWeeks.SingleAsync(x => x.Id == weekId);
            Assert.Equal(ProductionScheduleWeekStatus.Open, week.Status);
            version = week.Version;
        }
        var statusInput = new Dictionary<string, string>
        {
            ["weekId"] = weekId.ToString(), ["version"] = "0", ["View"] = "summary", ["SelectedDay"] = "2026-09-22"
        };
        Assert.Equal(HttpStatusCode.OK, (await Post(client, page + "?handler=Close", closing, statusInput)).StatusCode);
        statusInput["version"] = version.ToString();
        var closed = await Post(client, page + "?handler=Close", closing, statusInput);
        Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode);
        Assert.Contains("View=summary", closed.Headers.Location!.OriginalString);
        Assert.Contains("SelectedDay=2026-09-22", closed.Headers.Location.OriginalString);
        var closedHtml = await client.GetStringAsync($"{page}?WeekId={weekId}&View=summary");
        Assert.DoesNotContain("ActionPanel=copy", closedHtml);
        await Fixture(language, "closed", closedHtml);
        var reopening = await client.GetStringAsync($"{page}?WeekId={weekId}&View=summary&ActionPanel=reopen");
        Assert.Contains(reopen, WebUtility.HtmlDecode(reopening));
        Assert.Contains("data-auto-show=\"true\"", reopening);
        using (var scope = factory.Services.CreateScope())
        {
            var week = await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionScheduleWeeks.SingleAsync(x => x.Id == weekId);
            Assert.Equal(ProductionScheduleWeekStatus.Closed, week.Status); statusInput["version"] = week.Version.ToString();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, page + "?handler=Reopen", reopening, statusInput)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            Assert.Equal(ProductionScheduleWeekStatus.Open, (await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == weekId)).Status);
            Assert.Equal(1, await db.ProductionScheduleRevisions.CountAsync(x => x.WeekId == weekId && x.Action == "closed"));
            Assert.Equal(1, await db.ProductionScheduleRevisions.CountAsync(x => x.WeekId == weekId && x.Action == "reopened"));
            var config = await db.ProductionDailyConfigurations.SingleAsync(); config.CuttingStageId = null; await db.SaveChangesAsync();
            weekId = (await scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>().CreateWeekAsync(new(Guid.NewGuid(), monday.AddDays(7), userId))).Id!.Value;
        }
        var blocked = await client.GetStringAsync($"{page}?WeekId={weekId}&View=review");
        Assert.Contains("data-server-blocked=\"true\"", blocked);
        Assert.Matches("<button[^>]*data-schedule-publish-confirm[^>]*disabled", blocked);
        Assert.Equal(weekId.ToString(), Input(blocked, "WeekId"));
        Assert.Equal("review", Input(blocked, "View"));
        await Fixture(language, "blocked", blocked);
    }

    private static string Input(string html, string name) =>
        Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;
    private static Task<HttpResponseMessage> Post(HttpClient client, string url, string html, Dictionary<string, string> input)
    {
        input["__RequestVerificationToken"] = Input(html, "__RequestVerificationToken");
        return client.PostAsync(url, new FormUrlEncodedContent(input));
    }
    private static async Task Fixture(string language, string name, string html)
    {
        if (Environment.GetEnvironmentVariable("WAREHOUSE_SCHEDULE_FIXTURES") is not string directory) return;
        Directory.CreateDirectory(directory);
        html = Regex.Replace(html, "(name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]*", "$1");
        await File.WriteAllTextAsync(Path.Combine(directory, $"{name}-{language}.html"), html);
    }
}
