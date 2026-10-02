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

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionCaptureRecoveryTests
{
    [Fact]
    public async Task Recover_review_confirm_and_lost_response_preserve_one_operation_and_three_products()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var seed = await SeedAsync(factory.Services);
        var page = await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}&Area=Cutting&ShiftId={seed.ShiftId}");
        await SaveVisualFixtureAsync("capture", page);
        var fields = Fields(page, seed);
        fields["Group.Pin"] = "0123";
        fields["Group.Fingerprint"] = "not-a-review";
        fields["Group.Rows[0].Quantity"] = "4,5";
        var restored = await PostAsync(client, "GroupRestore", page, fields);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var html = await restored.Content.ReadAsStringAsync();
        Assert.Equal("4,5", Input(html, "Group.Rows[0].Quantity"));
        Assert.Equal("  keep exact draft notes  ", Input(html, "Group.Rows[0].Notes"));
        Assert.Equal(fields["Group.OperationId"], Input(html, "Group.OperationId"));
        Assert.Equal("", Input(html, "Group.Fingerprint"));
        Assert.DoesNotContain("value=\"0123\"", html);
        Assert.DoesNotContain("data-group-confirm", html);
        Assert.DoesNotContain("Hidden description", html);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.False(await db.ProductionDailyCaptures.AnyAsync());

        fields["Group.Rows[0].Quantity"] = "4";
        fields.Remove("Group.Pin");
        var review = await PostAsync(client, "GroupPreview", html, fields);
        var reviewed = await review.Content.ReadAsStringAsync();
        await SaveVisualFixtureAsync("review", reviewed);
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.Contains("data-group-confirm", reviewed);
        Assert.Contains("data-back-to-capture", reviewed);
        fields["Group.Fingerprint"] = Input(reviewed, "Group.Fingerprint");
        fields["Group.Pin"] = "0123";
        var confirmed = await PostAsync(client, "GroupConfirm", reviewed, fields);
        Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        Assert.Equal(3, await db.ProductionDailyCaptures.CountAsync());
        Assert.Equal(18m, await db.ProductionDailyCaptures.SumAsync(x => x.Quantity));

        // Simulate a lost confirmation response: no redirect was followed, no PIN is recovered.
        fields.Remove("Group.Pin");
        fields.Remove("Group.Fingerprint");
        var recoveredReceipt = await PostAsync(client, "GroupRestore", reviewed, fields);
        Assert.Equal(HttpStatusCode.Redirect, recoveredReceipt.StatusCode);
        var receipt = WebUtility.HtmlDecode(await client.GetStringAsync(recoveredReceipt.Headers.Location));
        Assert.Contains("Producción registrada", receipt);
        Assert.Contains($"data-receipt-operation=\"{fields["Group.OperationId"]}\"", receipt);
        Assert.Equal(3, await db.ProductionDailyCaptures.CountAsync());
        Assert.Single(await db.ProductionCaptureSubmissions.ToListAsync());

        fields["Group.Rows[0].Quantity"] = "99";
        var conflict = await PostAsync(client, "GroupRestore", receipt, fields);
        var conflictHtml = WebUtility.HtmlDecode(await conflict.Content.ReadAsStringAsync());
        Assert.Contains("data-restore-conflict=\"true\"", conflictHtml);
        Assert.Contains("el borrador se conserva", conflictHtml);
        Assert.Equal("99", Input(conflictHtml, "Group.Rows[0].Quantity"));
        Assert.Equal(3, await db.ProductionDailyCaptures.CountAsync());

        var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId);
        week.Status = ProductionScheduleWeekStatus.Closed;
        await db.SaveChangesAsync();
        var closed = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}&ShiftId={seed.ShiftId}"));
        await SaveVisualFixtureAsync("closed", await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}&ShiftId={seed.ShiftId}"));
        Assert.Contains("data-context-state=\"Closed\"", closed);
        Assert.Contains("Ver balance", closed);
        Assert.DoesNotContain("data-group-preview", closed);
        Assert.DoesNotContain("data-group-quantity", closed);
        fields["Group.Rows[0].Quantity"] = "4";
        var closedReceipt = await PostAsync(client, "GroupRestore", closed, fields);
        Assert.Equal(HttpStatusCode.Redirect, closedReceipt.StatusCode);
        Assert.Equal(3, await db.ProductionDailyCaptures.CountAsync());
    }

    [Fact]
    public async Task Recovery_keeps_inactive_products_and_invalid_quantities_visible_without_writing()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var seed = await SeedAsync(factory.Services);
        var page = await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}&ShiftId={seed.ShiftId}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        (await db.Products.SingleAsync(x => x.Id == seed.Products[0])).IsActive = false;
        await db.SaveChangesAsync();
        var fields = Fields(page, seed);
        fields["Group.Rows[0].Sku"] = "UNTRUSTED-SKU";
        var response = await PostAsync(client, "GroupRestore", page, fields);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("El producto ya no está activo", html);
        Assert.Contains("data-inactive=\"true\"", html);
        Assert.DoesNotContain("UNTRUSTED-SKU", html);
        Assert.Equal("4", Input(html, "Group.Rows[0].Quantity"));
        Assert.False(await db.ProductionDailyCaptures.AnyAsync());
    }

    [Theory]
    [InlineData("duplicates")]
    [InlineData("too-many")]
    [InlineData("notes")]
    [InlineData("area")]
    [InlineData("date")]
    [InlineData("antiforgery")]
    public async Task Recovery_rejects_invalid_shapes_without_writing(string scenario)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var seed = await SeedAsync(factory.Services);
        var page = await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}&ShiftId={seed.ShiftId}");
        var fields = Fields(page, seed);
        if (scenario == "duplicates") fields["Group.Rows[1].ProductId"] = fields["Group.Rows[0].ProductId"];
        if (scenario == "notes") fields["Group.Rows[0].Notes"] = new string('x', 501);
        if (scenario == "area") fields["Group.Area"] = "999";
        if (scenario == "date") fields["Group.Date"] = "not-a-date";
        if (scenario == "too-many")
            for (var i = 3; i <= 100; i++) fields[$"Group.Rows[{i}].ProductId"] = Guid.NewGuid().ToString();
        fields["__RequestVerificationToken"] = scenario == "antiforgery" ? "bad-token" : Input(page, "__RequestVerificationToken");
        var response = await client.PostAsync("/Operations/Production?handler=GroupRestore", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionDailyCaptures.AnyAsync());
    }

    internal static WebApplicationFactory<Program> Configure(AdminRouteTests.WarehouseApplicationFactory original) =>
        original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));

    internal static async Task<(Guid WeekId, DateOnly Date, Guid ShiftId, Guid[] Products)> SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var user = new User { FullName = "Capture recovery operator", RoleId = 1, PinHash = "", PinLookup = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
        db.Users.Add(user);
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var products = Enumerable.Range(1, 3).Select(i => new Product { Sku = $"RECOVER-{i:000}", Description = "Hidden description", BaseUnitId = 1 }).ToArray();
        db.Products.AddRange(products);
        await db.SaveChangesAsync();
        var schedule = scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
        var date = new DateOnly(2026, 9, 21);
        var weekId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), date, user.Id))).Id!.Value;
        foreach (var product in products)
        {
            var week = (await schedule.GetWeekAsync(weekId))!;
            Assert.True((await schedule.SaveLineAsync(new(Guid.NewGuid(), weekId, null, week.Version, null,
                date, product.Id, 30, null, null, null, null, user.Id))).Success);
        }
        var draft = (await schedule.GetWeekAsync(weekId))!;
        Assert.True((await schedule.PublishAsync(new(Guid.NewGuid(), weekId, draft.Version, "0123", user.Id))).Success);
        var shift = (await db.ProductionDailyConfigurations.SingleAsync()).Shift1Id!.Value;
        return (weekId, date, shift, products.Select(x => x.Id).ToArray());
    }

    private static Dictionary<string, string> Fields(string page, (Guid WeekId, DateOnly Date, Guid ShiftId, Guid[] Products) seed)
    {
        var fields = new Dictionary<string, string>
        {
            ["Group.OperationId"] = Input(page, "Group.OperationId"),
            ["Group.Date"] = seed.Date.ToString("yyyy-MM-dd"),
            ["Group.Area"] = "Cutting",
            ["Group.ShiftId"] = seed.ShiftId.ToString(),
            ["Group.Mode"] = "list"
        };
        for (var i = 0; i < seed.Products.Length; i++)
        {
            fields[$"Group.Rows[{i}].ProductId"] = seed.Products[i].ToString();
            fields[$"Group.Rows[{i}].Quantity"] = (4 + i * 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        fields["Group.Rows[0].Notes"] = "  keep exact draft notes  ";
        return fields;
    }

    internal static Task<HttpResponseMessage> PostAsync(HttpClient client, string handler, string page, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = Input(page, "__RequestVerificationToken");
        return client.PostAsync("/Operations/Production?handler=" + handler, new FormUrlEncodedContent(fields));
    }

    internal static string Input(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html,
        "<input(?=[^>]*name=\"" + Regex.Escape(name) + "\")[^>]*value=\"([^\"]*)\"").Groups[1].Value);

    private static async Task SaveVisualFixtureAsync(string name, string html)
    {
        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_CAPTURE_VISUAL_FIXTURES");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".html"), html);
    }
}
