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

public sealed class MaterialIncidentRouteTests
{
    [Theory]
    [InlineData("es")][InlineData("en")]
    public async Task Central_create_chooses_exact_historical_context_and_preserves_local_return(string language)
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{WarehouseEPI.Web.Localization.UiLanguage.CookieName}={language}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var product = new Product { Sku = "CENTRAL-PRODUCT", Description = "Rollo blanco histórico", BaseUnitId = 1, IsActive = false };
        var location = new Location { Code = "CENTRAL-LOCATION", IsActive = false, IsBlocked = true };
        var unrelated = new Location { Code = "CENTRAL-UNRELATED" };
        var wip = new Location { Code = "CENTRAL-WIP", OperationalRole = LocationOperationalRole.Wip };
        db.AddRange(product, location, unrelated, wip,
            new InventoryBalance { ProductId = product.Id, LocationId = location.Id, Quantity = -1 },
            new InventoryBalance { ProductId = product.Id, LocationId = wip.Id, Quantity = 10 });
        await db.SaveChangesAsync();
        const string returnUrl = "/Operations/Incidents?status=all&search=sample&pageNumber=2";
        var startUrl = "/Operations/Incidents/Create?returnUrl=" + Uri.EscapeDataString(returnUrl);
        var html = await client.GetStringAsync(startUrl);
        Assert.Contains("data-incident-context-picker", html);
        Assert.Equal("", Value(html, "ProductId")); Assert.Equal("", Value(html, "LocationId"));
        Assert.Equal(returnUrl, Value(html, "ReturnUrl"));
        Save($"initial-{language}", html);
        var listing = WebUtility.HtmlDecode(await client.GetStringAsync(returnUrl));
        Assert.Contains("/Operations/Incidents/Create?returnUrl=", listing);
        Assert.Contains("pageNumber%3D2", listing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(product.Sku, await client.GetStringAsync("/Operations/Incidents/Create?handler=Products&q=blanco%20rollo"));
        var locations = await client.GetStringAsync($"/Operations/Incidents/Create?handler=Locations&productId={product.Id}&q=CENTRAL");
        Assert.Contains(location.Code, locations); Assert.DoesNotContain(unrelated.Code, locations); Assert.DoesNotContain(wip.Code, locations);
        var captureUrl = startUrl + $"&productId={product.Id}&locationId={location.Id}";
        html = await client.GetStringAsync(captureUrl);
        Assert.Contains("data-incident-form", html); Assert.DoesNotContain("data-incident-context-picker", html);
        Assert.Contains("value=\"Undetermined\" selected=\"selected\"", html);
        Assert.Equal(returnUrl, Value(html, "ReturnUrl"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(startUrl + $"&productId={product.Id}&locationId={unrelated.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(startUrl + $"&productId={product.Id}&locationId={wip.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(startUrl + "&productId=invalid")).StatusCode);
        var external = await client.GetStringAsync("/Operations/Incidents/Create?returnUrl=https%3A%2F%2Fexample.com");
        Assert.Equal("/Operations/Incidents", Value(external, "ReturnUrl"));
        Assert.Empty(await db.MaterialIncidents.ToListAsync());
        var user = new User { FullName = "Central incident operator", RoleId = 2, PinHash = "", PinLookup = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "7854");
        db.Add(user); await db.SaveChangesAsync();
        var fields = new Dictionary<string, string> {
            ["__RequestVerificationToken"] = Value(html, "__RequestVerificationToken"),
            ["OperationId"] = Value(html, "OperationId"), ["ContextToken"] = Value(html, "ContextToken"),
            ["ProductId"] = product.Id.ToString(), ["LocationId"] = location.Id.ToString(), ["ReturnUrl"] = returnUrl,
            ["Scope"] = "Undetermined", ["Kind"] = "Damage", ["Description"] = "Historical damaged material", ["Pin"] = "0000"
        };
        var failed = await client.PostAsync(captureUrl, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var failedHtml = await failed.Content.ReadAsStringAsync();
        Assert.Contains(fields["Description"], failedHtml);
        Assert.Equal(returnUrl, Value(failedHtml, "ReturnUrl"));
        Assert.Equal(product.Id.ToString(), Value(failedHtml, "ProductId"));
        Assert.Equal(location.Id.ToString(), Value(failedHtml, "LocationId"));
        Assert.DoesNotContain("value=\"0000\"", failedHtml);
        fields["Pin"] = "7854";
        var saved = await client.PostAsync(captureUrl, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.StartsWith("/Operations/Incidents/Details", saved.Headers.Location!.ToString());
        var incident = await db.MaterialIncidents.SingleAsync();
        Assert.Null(incident.PlateId); Assert.Null(incident.ArrivalLineId);
        Assert.Equal(-1, await db.InventoryBalances.Where(b => b.LocationId == location.Id).SumAsync(b => b.Quantity));
    }

    [Fact]
    public async Task Single_stock_location_groups_lots_excludes_wip_and_preserves_historical_locations()
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var product = new Product { Sku = "ONE-LOCATION", BaseUnitId = 1, IsActive = false };
        var location = new Location { Code = "HISTORICAL", IsActive = false, IsBlocked = true };
        var other = new Location { Code = "OTHER" };
        var wip = new Location { Code = "WIP-TEST", OperationalRole = LocationOperationalRole.Wip };
        var lot = new ProductLot { ProductId = product.Id, Number = "L1", NormalizedNumber = "L1" };
        db.AddRange(product, location, other, wip, lot);
        await db.SaveChangesAsync();
        var url = $"/Operations/Incidents?handler=SingleStockLocation&productId={product.Id}";
        Assert.Equal("null", await client.GetStringAsync(url));
        var balance = new InventoryBalance { ProductId = product.Id, LocationId = location.Id, Quantity = 10 };
        var lotBalance = new InventoryBalance { ProductId = product.Id, LocationId = location.Id, LotId = lot.Id, Quantity = 20 };
        var otherBalance = new InventoryBalance { ProductId = product.Id, LocationId = other.Id, Quantity = -1 };
        db.AddRange(balance, lotBalance, otherBalance,
            new InventoryBalance { ProductId = product.Id, LocationId = wip.Id, Quantity = 100 });
        await db.SaveChangesAsync();
        Assert.Contains(location.Code, await client.GetStringAsync(url));
        otherBalance.Quantity = 1; await db.SaveChangesAsync();
        Assert.Equal("null", await client.GetStringAsync(url));
        otherBalance.Quantity = 0; lotBalance.Quantity = -10; await db.SaveChangesAsync();
        Assert.Equal("null", await client.GetStringAsync(url));
        lotBalance.Quantity = -11; await db.SaveChangesAsync();
        Assert.Equal("null", await client.GetStringAsync(url));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Operations/Incidents?handler=SingleStockLocation&productId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Single_stock_product_uses_net_stock_across_lots_and_keeps_historical_catalogs()
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var location = new Location { Code = "SINGLE", IsActive = false, IsBlocked = true };
        var product = new Product { Sku = "HIST-STOCK", BaseUnitId = 1, IsActive = false };
        var other = new Product { Sku = "OTHER-STOCK", BaseUnitId = 1 };
        var lot = new ProductLot { ProductId = product.Id, Number = "LOT", NormalizedNumber = "LOT" };
        db.AddRange(location, product, other, lot);
        await db.SaveChangesAsync();
        var url = $"/Operations/Incidents?handler=SingleStockProduct&locationId={location.Id}";
        Assert.Equal("null", await client.GetStringAsync(url));
        var first = new InventoryBalance { ProductId = product.Id, LocationId = location.Id, Quantity = 10 };
        var second = new InventoryBalance { ProductId = product.Id, LocationId = location.Id, LotId = lot.Id, Quantity = 20 };
        var another = new InventoryBalance { ProductId = other.Id, LocationId = location.Id, Quantity = -5 };
        db.AddRange(first, second, another);
        await db.SaveChangesAsync();
        Assert.Contains(product.Sku, await client.GetStringAsync(url));
        another.Quantity = 1; await db.SaveChangesAsync();
        Assert.Equal("null", await client.GetStringAsync(url));
        another.Quantity = 0; second.Quantity = -10; await db.SaveChangesAsync();
        Assert.Equal("null", await client.GetStringAsync(url));
        second.Quantity = -9; await db.SaveChangesAsync();
        Assert.Contains(product.Sku, await client.GetStringAsync(url));
        location.OperationalRole = LocationOperationalRole.Wip; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Operations/Incidents?handler=SingleStockProduct&locationId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Filters_suggest_historical_products_and_warehouse_locations_and_preserve_context()
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var product = new Product { Sku = "T6-HIST", Description = "Rollo histórico", BaseUnitId = 1, IsActive = false };
        var location = new Location { Code = "G2-HIST", Description = "Ubicación histórica", IsActive = false, IsBlocked = true };
        var wip = new Location { Code = "G2-WIP", OperationalRole = LocationOperationalRole.Wip };
        db.AddRange(product, location, wip);
        await db.SaveChangesAsync();
        var products = await client.GetStringAsync("/Operations/Incidents?handler=Products&q=histórico");
        Assert.Contains(product.Sku, products);
        Assert.Contains("\"isActive\":false", products);
        var locations = await client.GetStringAsync("/Operations/Incidents?handler=Locations&q=g2");
        Assert.Contains(location.Code, locations);
        Assert.Contains("\"isBlocked\":true", locations);
        Assert.DoesNotContain(wip.Code, locations);
        Assert.Equal("[]", await client.GetStringAsync("/Operations/Incidents?handler=Products&q="));
        var html = await client.GetStringAsync($"/Operations/Incidents?productId={product.Id}&locationId={location.Id}");
        Assert.Equal(product.Sku, Value(html, "ProductCode"));
        Assert.Equal(location.Code, Value(html, "LocationCode"));
        Assert.Contains("suggestion-lookup", html);
        Assert.Contains("data-incident-filter=\"location\"", html);
        html = await client.GetStringAsync($"/Operations/Incidents?locationId={location.Id}&relation=current");
        Assert.Equal("", Value(html, "LocationCode"));
    }

    [Theory]
    [InlineData("es")][InlineData("en")]
    public async Task Contexts_confirmation_photo_endpoint_recovery_and_public_operational_access(string language)
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{WarehouseEPI.Web.Localization.UiLanguage.CookieName}={language}");
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var pins = scope.ServiceProvider.GetRequiredService<UserPinService>();
        var user = new User { FullName = "Incident operator", RoleId = 2, PinHash = "", PinLookup = "" };
        await pins.AssignAsync(user, "7854");
        var product = new Product { Sku = "INCIDENT-TEST", Description = "Material with a damaged package", BaseUnitId = 1 };
        var location = new Location { Code = "STAGING", Kind = LocationKind.Area };
        db.AddRange(user, product, location); await db.SaveChangesAsync();
        var entry = await scope.ServiceProvider.GetRequiredService<InventoryMovementService>().ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "7854", [new(product.Id, 30, DestinationLocationId: location.Id)]));
        Assert.Equal(InventoryMovementStatus.Success, entry.Status);
        var plate = Assert.Single(entry.Plates!); var lineId = (await db.InventoryMovementLines.SingleAsync()).Id;
        var create = $"/Operations/Incidents/Create?arrivalLineId={lineId}&plateId={plate.PlateId}";
        var html = await client.GetStringAsync(create); Assert.Contains("data-incident-form", html); Save($"create-{language}", html);
        var picker = await client.GetStringAsync($"/Operations/Incidents/Create?locationId={location.Id}"); Assert.Contains("data-incident-picker", picker); Save($"picker-{language}", picker);
        Assert.Contains(product.Sku, await client.GetStringAsync($"/Operations/Incidents/Create?handler=Products&locationId={location.Id}&q=INCIDENT"));
        foreach (var (url, label) in new[] { ("/Operations/Staging", "staging"), ($"/Operations/PalletLabels/Tracking?id={plate.PlateId}", "tracking"),
            ($"/Inventory?locationId={location.Id}", "inventory"), ($"/Locations/{location.Id}", "location") })
        {
            var origin = await client.GetStringAsync(url); Assert.Contains("/Operations/Incidents/Create", origin); Save($"{label}-{language}", origin);
        }
        Assert.Contains("/Operations/Incidents", await client.GetStringAsync("/Modules/Operations"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Operations/Incidents/Create?plateId={Guid.NewGuid()}&productId={product.Id}")).StatusCode);
        var fields = new Dictionary<string, string> {
            ["__RequestVerificationToken"] = Value(html, "__RequestVerificationToken"), ["OperationId"] = Value(html, "OperationId"), ["ContextToken"] = Value(html, "ContextToken"),
            ["ArrivalLineId"] = lineId.ToString(), ["PlateId"] = plate.PlateId.ToString(), ["ProductId"] = product.Id.ToString(), ["LocationId"] = location.Id.ToString(),
            ["Scope"] = "Receiving", ["Kind"] = "Damage", ["Description"] = "Empaque mojado y roto", ["Quantity"] = "40", ["Pin"] = "0000"
        };
        var response = await client.PostAsync(create, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); html = await response.Content.ReadAsStringAsync();
        Assert.Contains(fields["Description"], html); Assert.DoesNotContain("value=\"0000\"", html); Save($"error-{language}", html);
        using (var multipart = new MultipartFormDataContent())
        {
            foreach (var field in fields) multipart.Add(new StringContent(field.Value), field.Key);
            var upload = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aTioAAAAASUVORK5CYII="));
            upload.Headers.ContentType = new("image/png"); multipart.Add(upload, "Photos", "evidence.png");
            var failedUpload = await client.PostAsync(create, multipart);
            Assert.Equal(HttpStatusCode.OK, failedUpload.StatusCode);
            var failedHtml = WebUtility.HtmlDecode(await failedUpload.Content.ReadAsStringAsync());
            Assert.Contains(language == "en" ? "Select the photos again before confirming." : "Vuelve a seleccionar las fotografías antes de confirmar.", failedHtml);
            Assert.DoesNotContain("value=\"0000\"", failedHtml);
        }
        fields["Pin"] = "7854";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(create, new FormUrlEncodedContent(fields.Where(k => k.Key != "__RequestVerificationToken")))).StatusCode);
        response = await client.PostAsync(create, new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var detailsUrl = response.Headers.Location!.ToString(); html = await client.GetStringAsync(detailsUrl); Save($"details-{language}", html);
        var item = await db.MaterialIncidents.AsNoTracking().SingleAsync();
        Assert.Equal(lineId, item.ArrivalLineId); Assert.Equal(40, item.Quantity);
        var stagingWithIncident = await client.GetStringAsync("/Operations/Staging");
        Assert.Contains("data-open-incidents", stagingWithIncident);
        Assert.Contains($"arrivalLineId={lineId}", stagingWithIncident);
        Assert.Equal(30, await db.InventoryBalances.SumAsync(b => b.Quantity));
        var photo = new IncidentPhotoInput("evidence.png", "image/png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aTioAAAAASUVORK5CYII="));
        var service = scope.ServiceProvider.GetRequiredService<MaterialIncidentService>();
        Assert.Null((await service.FollowUpAsync(new(Guid.NewGuid(), item.Id, 1, "Comment", "Fotografía", null, "7854", [photo]))).Error);
        var image = await db.MaterialIncidentPhotos.SingleAsync();
        response = await client.GetAsync($"/Operations/Incidents/Details?id={item.Id}&handler=Photo&photoId={image.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(photo.Content, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Operations/Incidents/Details?id={Guid.NewGuid()}&handler=Photo&photoId={image.Id}")).StatusCode);
        html = await client.GetStringAsync("/Operations/Incidents?status=all"); Save($"list-{language}", html);
        Assert.Contains(item.Folio, html);
        Assert.Contains(language == "en" ? "Material issues" : "Incidencias de material", WebUtility.HtmlDecode(html));
        Assert.Equal(2, await db.MaterialIncidentEvents.CountAsync());
    }

    private static string Value(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, $"name=\"{name}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private static void Save(string name, string html)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln"))) directory = directory.Parent;
        if (directory is null) return;
        var target = Path.Combine(directory.FullName, "artifacts", "incidents-ui", "fixtures"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, name + ".html"), html);
    }
}
