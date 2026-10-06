using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class RackFormatTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Editor_reviews_saves_and_renders_two_by_two_in_every_rack_view(string language)
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var role = await db.Roles.SingleAsync(item => item.Code == "ADMIN");
        var user = new User { FullName = "Rack format admin", RoleId = role.Id, PinLookup = "", PinHash = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
        db.Users.Add(user);
        var positions = Enumerable.Range(1, 9).Select(number => new Location
        {
            Code = $"Q-2-{number}",
            Kind = LocationKind.Rack,
            RowCode = "Q",
            RackNumber = 2,
            PalletNumber = (short)number
        }).ToArray();
        db.AddRange(positions);
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new("https://localhost"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("Cookie", $"WarehouseEPI.Language={language}");
        var login = await client.GetStringAsync("/Admin/Login");
        using var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = "0123",
            ["__RequestVerificationToken"] = Antiforgery(login)
        }));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        const string editorUrl = "/Admin/Catalogs/Locations/Rack/Edit?rowCode=Q&rackNumber=2";
        var initial = await client.GetStringAsync(editorUrl);
        Assert.Contains(language == "en" ? "Rack columns" : "Columnas del rack", initial);
        await WriteFixtureAsync($"editor-{language}.html", initial);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", Antiforgery(initial)),
            new("Input.OperationId", Guid.NewGuid().ToString()),
            new("Input.RowCode", "Q"), new("Input.RackNumber", "2"),
            new("Input.Columns", "2"), new("Input.Levels", "2"),
            new("Input.ProcessConfigurationVersion", "0"),
            new("Input.Reason", "Rack físico de dos por dos"), new("Input.WipPallets", "1")
        };
        fields.AddRange(Enumerable.Range(1, 4).Select(number => new KeyValuePair<string, string>("Input.PresentPallets", number.ToString())));
        using var reviewResponse = await client.PostAsync(editorUrl + "&handler=Review", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, reviewResponse.StatusCode);
        var review = await reviewResponse.Content.ReadAsStringAsync();
        Assert.Contains("data-rack-pin", review);
        Assert.Contains("data-rack-columns=\"2\"", review);
        Assert.Equal(new[] { "3", "4", "1", "2" }, Regex.Matches(review, "data-rack-position data-pallet=\"([1-4])\"").Select(match => match.Groups[1].Value));
        await WriteFixtureAsync($"review-{language}.html", review);
        fields.RemoveAll(field => field.Key == "__RequestVerificationToken");
        fields.Add(new("__RequestVerificationToken", Antiforgery(review)));
        fields.Add(new("Input.Pin", "0123"));
        using var saved = await client.PostAsync(editorUrl + "&handler=Save", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var reopened = await client.GetStringAsync(editorUrl);
        Assert.Contains("data-rack-columns=\"2\"", reopened);
        await WriteFixtureAsync($"saved-{language}.html", reopened);

        foreach (var (name, url) in new[]
        {
            ("racks", "/Locations?viewMode=racks"), ("map", "/Locations?viewMode=map"),
            ("details", $"/Locations/{positions[0].Id}"), ("admin-details", $"/Admin/Catalogs/Locations/{positions[0].Id}"),
            ("print", "/Locations/Rack/Print?rowCode=Q&rackNumber=2"), ("display", "/Locations/Display?play=true&rows=Q&racks=1")
        })
        {
            var html = await client.GetStringAsync(url);
            Assert.Contains("data-rack-columns=\"2\"", html);
            await WriteFixtureAsync($"view-{name}-{language}.html", html);
        }

        using var invalid = await client.PostAsync(editorUrl + "&handler=Save", new FormUrlEncodedContent(
            fields.Select(field => field.Key == "Input.Columns" ? new(field.Key, "invalid") : field)));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.DoesNotContain("data-rack-save", await invalid.Content.ReadAsStringAsync());

        // A protected fifth position prevents shrinking the fixture on the client and server.
        db.ChangeTracker.Clear();
        var stored = await db.LocationRackFormats.SingleAsync();
        stored.Columns = 3;
        stored.Levels = 3;
        var protectedPositions = await db.Locations.Where(item => item.RowCode == "Q" && item.RackNumber == 2).ToArrayAsync();
        foreach (var position in protectedPositions) { position.IsPhysicallyPresent = true; position.IsActive = true; }
        var product = new Product { Sku = "PROTECTED-FORMAT", BaseUnitId = await db.Units.Select(unit => unit.Id).FirstAsync() };
        db.InventoryBalances.Add(new InventoryBalance
        {
            Product = product,
            Location = protectedPositions.Single(item => item.PalletNumber == 5),
            Quantity = 1
        });
        await db.SaveChangesAsync();
        await WriteFixtureAsync($"protected-{language}.html", await client.GetStringAsync(editorUrl));
    }

    private static string Antiforgery(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task WriteFixtureAsync(string name, string html)
    {
        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_RACK_FORMAT_FIXTURES");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name), html);
    }
}
