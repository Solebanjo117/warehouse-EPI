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
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Production;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionInitialBalanceRouteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Imported_and_current_weeks_render_and_save_independent_totals(bool explicitWeek)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Guid weekId, productId, sourceId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var admin = new User { FullName = "Initial balance admin", RoleId = 1, PinHash = "", PinLookup = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(admin, "0123");
            db.Users.Add(admin); await db.SaveChangesAsync();
            productId = (await db.Products.SingleAsync(x => x.Sku == "FG-100")).Id;
            var week = new ProductionScheduleWeek { CreatedByUserId = admin.Id, OperationId = Guid.NewGuid(), RequestFingerprint = "initial-route",
                WeekStart = new(2026, 10, 5), WeekEnd = new(2026, 10, 11), Status = ProductionScheduleWeekStatus.Open, ExplicitCarryover = explicitWeek };
            for (var i = 1; i <= 2; i++) week.Lines.Add(new() { ProductId = productId, PlannedDate = week.WeekStart, Quantity = 25, Sequence = i });
            var source = new ProductionScheduleWeek { CreatedByUserId = admin.Id, OperationId = Guid.NewGuid(), RequestFingerprint = "initial-source",
                WeekStart = week.WeekStart.AddDays(-7), WeekEnd = week.WeekStart.AddDays(-1), Status = ProductionScheduleWeekStatus.Open, ExplicitCarryover = true };
            source.Lines.Add(new() { ProductId = productId, PlannedDate = source.WeekStart, Quantity = 20 });
            var config = await db.ProductionDailyConfigurations.SingleAsync();
            foreach (var area in Enum.GetValues<ProductionDailyArea>()) source.Captures.Add(new() { ProductId = productId, ResponsibleUserId = admin.Id,
                OperationId = Guid.NewGuid(), RequestFingerprint = "source-capture", EffectiveDate = source.WeekStart, Area = area, Quantity = 20,
                StageId = area switch { ProductionDailyArea.Cutting => config.CuttingStageId!.Value, ProductionDailyArea.Sewing => config.SewingStageId!.Value, _ => config.ReadyToPackStageId!.Value },
                ShiftId = config.Shift1Id!.Value });
            db.AddRange(week, source); await db.SaveChangesAsync(); weekId = week.Id; sourceId = source.Id;
        }
        var route = $"/Admin/Production/Schedule?WeekId={weekId}&View=program";
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(route)).StatusCode);
        var login = await client.GetStringAsync("/Admin/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = Token(login) }))).StatusCode);
        var html = await client.GetStringAsync(route);
        Assert.Contains("data-totals=\"true\"", html); Assert.DoesNotContain("data-workspace-carry-unavailable", html);
        var initialRoute = $"/Admin/Production/Schedule?handler=WorkspaceInitialBalances&weekId={weekId}";
        var initials = (await client.GetFromJsonAsync<ProductionInitialBalanceView[]>(initialRoute))!;
        Assert.Equal(3, initials.Length); Assert.All(initials, x => Assert.Equal(0, x.Quantity));
        var copy = await client.GetStringAsync($"/Admin/Production/Schedule?handler=WorkspaceCopy&weekId={weekId}&sourceWeekId={sourceId}");
        using (var copyJson = JsonDocument.Parse(copy)) Assert.All(copyJson.RootElement.GetProperty("initialBalances").EnumerateArray(), x => Assert.Equal("0", x.GetProperty("quantity").GetString()));
        if (!explicitWeek && Environment.GetEnvironmentVariable("WAREHOUSE_INITIAL_BALANCE_FIXTURES") is string directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "before.html"), StripToken(html));
            await File.WriteAllTextAsync(Path.Combine(directory, "initials.json"), JsonSerializer.Serialize(initials, JsonSerializerOptions.Web));
            await File.WriteAllTextAsync(Path.Combine(directory, "source.json"), copy);
        }
        var input = new { operationId = Guid.NewGuid(), weekId, expectedWeekVersion = 0, changes = Array.Empty<object>(),
            initialBalances = new[] { new ProductionInitialBalanceChange(productId, ProductionDailyArea.Cutting, 135, 0),
                new(productId, ProductionDailyArea.Sewing, 0, 0), new(productId, ProductionDailyArea.ReadyToPack, 23, 0) }, reason = "Corregir arrastre" };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/Production/Schedule?handler=WorkspaceReview") { Content = JsonContent.Create(input) };
        request.Headers.Add("RequestVerificationToken", Token(html));
        var review = (await (await client.SendAsync(request)).Content.ReadFromJsonAsync<ProductionScheduleWorkspaceReview>())!;
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        var payload = JsonSerializer.SerializeToNode(input, JsonSerializerOptions.Web)!; payload["reviewedFingerprint"] = review.Fingerprint;
        var response = await client.PostAsync("/Admin/Production/Schedule?handler=WorkspaceSave", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["payload"] = payload.ToJsonString(), ["adminPin"] = "0123", ["__RequestVerificationToken"] = Token(html) }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await client.GetFromJsonAsync<ProductionInitialBalanceView[]>(initialRoute))!;
        Assert.Equal(135, Assert.Single(saved, x => x.Area == ProductionDailyArea.Cutting).Quantity);
        var nextCopy = await client.GetStringAsync($"/Admin/Production/Schedule?handler=WorkspaceCopy&weekStart=2026-10-12&sourceWeekId={weekId}");
        using (var nextJson = JsonDocument.Parse(nextCopy))
        {
            var next = nextJson.RootElement.GetProperty("initialBalances").EnumerateArray().ToArray();
            Assert.Equal("185", Assert.Single(next, x => x.GetProperty("area").GetInt32() == (int)ProductionDailyArea.Cutting).GetProperty("quantity").GetString());
            Assert.Equal("50", Assert.Single(next, x => x.GetProperty("area").GetInt32() == (int)ProductionDailyArea.Sewing).GetProperty("quantity").GetString());
            Assert.Equal("73", Assert.Single(next, x => x.GetProperty("area").GetInt32() == (int)ProductionDailyArea.ReadyToPack).GetProperty("quantity").GetString());
        }
        using var scopeAfter = factory.Services.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Equal(3, await dbAfter.ProductionInitialBalances.CountAsync());
        Assert.Equal(2, await dbAfter.ProductionScheduleLines.CountAsync(x => x.WeekId == weekId));
        Assert.Empty(await dbAfter.InventoryMovements.ToListAsync());
        if (!explicitWeek && Environment.GetEnvironmentVariable("WAREHOUSE_INITIAL_BALANCE_FIXTURES") is string savedDirectory)
        {
            await File.WriteAllTextAsync(Path.Combine(savedDirectory, "saved.html"), StripToken(await client.GetStringAsync(route)));
            (await dbAfter.ProductionScheduleWeeks.SingleAsync(x => x.Id == weekId)).Status = ProductionScheduleWeekStatus.Closed;
            await dbAfter.SaveChangesAsync();
            await File.WriteAllTextAsync(Path.Combine(savedDirectory, "closed.html"), StripToken(await client.GetStringAsync(route)));
        }
    }

    private static string StripToken(string html) => Regex.Replace(html, "(name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]*", "$1");
    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
}
