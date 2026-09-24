using System.Net;
using System.Text.Json;
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

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionScheduleWorkspaceRouteTests
{
    [Fact]
    public async Task Draft_workspace_requires_admin_and_saves_a_reviewed_group_idempotently()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Guid weekId, sourceId, productId;
        var monday = new DateOnly(2026, 9, 21);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var user = new User { FullName = "Workspace admin", RoleId =
                (await db.Roles.SingleAsync(x => x.Code == "ADMIN")).Id, PinLookup = "", PinHash = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.Users.Add(user); await db.SaveChangesAsync();
            productId = (await db.Products.SingleAsync(x => x.Sku == "FG-100")).Id;
            var schedule = scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
            sourceId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), monday.AddDays(-7), user.Id))).Id!.Value;
            var source = (await schedule.GetWeekAsync(sourceId))!;
            Assert.True((await schedule.SaveLineAsync(new(Guid.NewGuid(), sourceId, null, source.Version,
                null, monday.AddDays(-7), productId, 40, "OLD-ORDER", null, null, null, user.Id))).Success);
            source = (await schedule.GetWeekAsync(sourceId))!;
            Assert.True((await schedule.PublishAsync(new(Guid.NewGuid(), sourceId, source.Version, "0123", user.Id))).Success);
            weekId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), monday, user.Id))).Id!.Value;
        }
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/Admin/Production/Schedule?WeekId={weekId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/Admin/Production/Schedule?handler=WorkspaceOpenings&weekId={weekId}")).StatusCode);
        var login = await client.GetStringAsync("/Admin/Login");
        var token = Token(login);
        var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var page = await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={weekId}");
        Assert.Contains("data-week-workspace", page);
        Assert.Contains("data-workspace-review", page);
        Assert.Contains("data-opening-editor", page);
        Assert.Contains("data-opening-panel", page);
        Assert.Contains("data-opening-summary", page);
        Assert.Contains("data-workspace-copy-products", page);
        Assert.Contains("data-workspace-copy-openings", page);
        Assert.DoesNotContain("data-workspace-opening-preview", page);
        Assert.Contains("/js/production-week-openings.", page);
        var optionsJson = await client.GetStringAsync($"/Admin/Production/Schedule?handler=WorkspaceOpenings&weekId={weekId}");
        var opening = JsonSerializer.Deserialize<ProductionOpeningOption[]>(optionsJson, JsonSerializerOptions.Web)!
            .Single(x => x.Area == ProductionDailyArea.Cutting);
        Assert.Equal(40, opening.Available); Assert.Equal(0, opening.Selected);
        var pastePanel = Regex.Match(page, "<details[^>]*data-workspace-paste-panel[\\s\\S]*?</details>").Value;
        Assert.NotEmpty(pastePanel);
        Assert.DoesNotContain("data-workspace-paste-preview", pastePanel);
        Assert.Contains("<section data-workspace-paste-preview", page);
        Assert.Contains("/js/production-week-workspace.", page);
        var copied = await client.GetStringAsync($"/Admin/Production/Schedule?handler=WorkspaceCopy&weekId={weekId}&sourceWeekId={sourceId}");
        Assert.Contains("\"quantity\":40", copied);
        Assert.DoesNotContain("OLD-ORDER", copied);
        var operationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            operationId, weekId, expectedWeekVersion = 0,
            openings = new[] { new { sourceWeekId = sourceId, sourceLineId = opening.SourceLineId, area = 0, quantity = "10", expectedFingerprint = opening.Fingerprint } },
            changes = new[] { new
            {
                kind = "add", lineId = (Guid?)null, expectedLineVersion = (uint?)null,
                line = new { plannedDate = monday.ToString("yyyy-MM-dd"), productId, quantity = "20",
                    orderReference1 = "ORDER-1", orderReference2 = (string?)null,
                    orderReference3 = (string?)null, notes = (string?)null }
            } }
        });
        async Task<HttpResponseMessage> Save(string body) => await client.PostAsync(
            "/Admin/Production/Schedule?handler=WorkspaceSave",
            new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = Token(page), ["payload"] = body }));
        Assert.Equal(HttpStatusCode.OK, (await Save(payload)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Save(payload)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var week = (await scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>()
                .GetWeekAsync(weekId))!;
            Assert.Single(week.Lines);
            Assert.Equal(20, week.Lines[0].Quantity);
            Assert.Equal("ORDER-1", week.Lines[0].OrderReference1);
            Assert.Null(week.Lines[0].WorkOrderId);
            Assert.Equal(10, (await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionWeekOpenings.SingleAsync()).Quantity);
        }
        var status = await client.GetStringAsync($"/Admin/Production/Schedule?handler=WorkspaceOperation&weekId={weekId}&operationId={operationId}");
        Assert.Contains("\"saved\":true", status);
        var stale = payload.Replace(operationId.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await Save(stale)).StatusCode);
    }

    private static string Token(string html) => Regex.Match(html,
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
}
