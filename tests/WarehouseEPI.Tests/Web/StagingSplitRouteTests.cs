using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class StagingSplitRouteTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Entry_and_later_split_preserve_inventory_and_require_antiforgery_and_pin(string language)
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{WarehouseEPI.Web.Localization.UiLanguage.CookieName}={language}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var user = new User { FullName = "Split operator", RoleId = 2, PinLookup = "", PinHash = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "7854");
        var product = new Product { Sku = "SPLIT-WEB", BaseUnitId = 1 };
        var staging = new Location { Code = "STAGING", Kind = LocationKind.Area };
        var other = new Location { Code = "OTHER", Kind = LocationKind.Area };
        db.AddRange(user, product, staging, other); await db.SaveChangesAsync();
        var entryHtml = await client.GetStringAsync($"/Operations/Entry?mode=staging&productId={product.Id}");
        Assert.Contains("data-pallet-distribution", entryHtml);
        Assert.DoesNotContain("data-pallet-distribution", await client.GetStringAsync("/Operations/Entry"));
        var payload = new Dictionary<string, string> {
            ["Mode"] = "staging", ["Input.OperationId"] = Guid.NewGuid().ToString(), ["Input.ProductId"] = product.Id.ToString(),
            ["Input.DestinationLocationId"] = staging.Id.ToString(), ["Input.Quantity"] = "100", ["Input.Pin"] = "7854",
            ["Distribution.Enabled"] = "true", ["Distribution.Quantities[0]"] = "40", ["Distribution.Quantities[1]"] = "35", ["Distribution.Quantities[2]"] = "25"
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(payload))).StatusCode);
        payload["__RequestVerificationToken"] = Value(entryHtml, "__RequestVerificationToken");
        payload["Mode"] = "other";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(payload))).StatusCode);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        payload["Mode"] = "staging"; payload["Input.DestinationLocationId"] = other.Id.ToString();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(payload))).StatusCode);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        payload["Input.DestinationLocationId"] = staging.Id.ToString();
        payload["Distribution.Quantities[2]"] = "0";
        var unbalanced = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(payload));
        Assert.Equal(HttpStatusCode.OK, unbalanced.StatusCode);
        var unbalancedHtml = await unbalanced.Content.ReadAsStringAsync();
        Assert.Equal("0", Value(unbalancedHtml, "Distribution.Quantities[2]"));
        Assert.Equal("75", Value(unbalancedHtml, "Input.Quantity"));
        Assert.Equal(staging.Id.ToString(), Value(unbalancedHtml, "Input.DestinationLocationId"));
        Assert.Contains("data-pallet-distribution", unbalancedHtml);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        payload["Distribution.Quantities[2]"] = "25";
        payload["Input.Quantity"] = "tampered";
        payload["Input.Pin"] = "0000";
        var badPin = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(payload));
        Assert.Equal(HttpStatusCode.OK, badPin.StatusCode);
        var badPinHtml = await badPin.Content.ReadAsStringAsync();
        Assert.Equal("100", Value(badPinHtml, "Input.Quantity"));
        Assert.Equal("25", Value(badPinHtml, "Distribution.Quantities[2]"));
        Assert.Equal("", Value(badPinHtml, "Input.Pin"));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        payload["Input.Pin"] = "7854";
        if (language == "es") payload.Remove("Input.Quantity");
        var confirmed = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(payload));
        Assert.True(confirmed.StatusCode == HttpStatusCode.Redirect,
            Regex.Match(await confirmed.Content.ReadAsStringAsync(), "<div[^>]*validation-summary[\\s\\S]*?</div>").Value);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(payload))).StatusCode);
        Assert.Equal(3, await db.PalletPlates.CountAsync()); Assert.Equal(1, await db.InventoryMovements.CountAsync());
        Assert.Equal(100, (await db.InventoryMovementLines.SingleAsync()).Quantity);
        var arrival = Assert.Single((await new StagingArrivalQuery(db).ListAsync(null)).Items);
        var plate = await db.PalletPlates.SingleAsync(p => p.Quantity == 40);
        var url = $"/Operations/Staging/Split?arrivalLineId={arrival.LineId}&plateId={plate.Id}";
        var splitHtml = await client.GetStringAsync(url);
        var split = new Dictionary<string, string> { ["ArrivalLineId"] = arrival.LineId.ToString(), ["PlateId"] = plate.Id.ToString(),
            ["OperationId"] = Value(splitHtml, "OperationId"), ["ArrivalVersion"] = Value(splitHtml, "ArrivalVersion"),
            ["ExpectedVersion"] = Value(splitHtml, "ExpectedVersion"), ["Pin"] = "0000", ["Distribution.Quantities[0]"] = "15", ["Distribution.Quantities[1]"] = "25" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, new FormUrlEncodedContent(split))).StatusCode);
        split["__RequestVerificationToken"] = Value(splitHtml, "__RequestVerificationToken");
        var failed = await client.PostAsync(url, new FormUrlEncodedContent(split));
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal("", Value(await failed.Content.ReadAsStringAsync(), "Pin"));
        Assert.Equal(3, await db.PalletPlates.CountAsync());
        split["Pin"] = "7854";
        var result = await client.PostAsync(url, new FormUrlEncodedContent(split));
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(url, new FormUrlEncodedContent(split))).StatusCode);
        var successHtml = await client.GetStringAsync(result.Headers.Location!);
        Assert.Contains("handler=StagingArrival", successHtml);
        Assert.Equal(5, await db.PalletPlates.CountAsync()); Assert.Equal(1, await db.InventoryMovements.CountAsync());
        Assert.Equal(100, await db.InventoryBalances.SumAsync(b => b.Quantity));
        Assert.Equal(100, (await new StagingArrivalQuery(db).GetAsync(arrival.LineId))!.Pending);
        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_STAGING_SPLIT_FIXTURES");
        if (!string.IsNullOrWhiteSpace(directory)) {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"entry-{language}.html"), entryHtml);
            await File.WriteAllTextAsync(Path.Combine(directory, $"split-{language}.html"), splitHtml);
            await File.WriteAllTextAsync(Path.Combine(directory, $"success-{language}.html"), successHtml);
        }
    }
    private static string Value(string html, string name) => WebUtility.HtmlDecode(Regex.Match(
        Regex.Match(html, $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*>").Value, "value=\"([^\"]*)\"").Groups[1].Value);
}
