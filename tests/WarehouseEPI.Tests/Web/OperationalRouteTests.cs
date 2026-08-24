using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class OperationalRouteTests : IClassFixture<AdminRouteTests.WarehouseApplicationFactory>
{
    private readonly AdminRouteTests.WarehouseApplicationFactory factory;

    public OperationalRouteTests(AdminRouteTests.WarehouseApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Operational_pages_and_lookup_are_public_and_posts_require_antiforgery()
    {
        using var client = CreateClient();
        foreach (var path in new[]
        {
            "/Operations/Entry", "/Operations/Exit", "/Operations/Transfer",
            "/Operations/Adjustment", "/Inventory"
        })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var lookup = await client.GetAsync("/Operations/Lookup?handler=Products&q=missing");
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        Assert.Equal("application/json", lookup.Content.Headers.ContentType?.MediaType);

        var missingToken = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
    }

    [Theory]
    [InlineData("/Operations/Entry", 2)]
    [InlineData("/Operations/Exit", 3)]
    [InlineData("/Operations/Transfer", 3)]
    [InlineData("/Operations/Adjustment", 2)]
    public async Task Operational_pages_render_camera_scanners_for_every_product_and_location_lookup(string path, int expectedButtons)
    {
        using var client = CreateClient();

        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedButtons, Regex.Count(html, "data-camera-scan(?=\\s|>)"));
        Assert.Equal(1, Regex.Count(html, "data-camera-scanner"));
        Assert.Contains("zxing-browser.min", html, StringComparison.Ordinal);
        Assert.Contains("data-guided-workstation", html, StringComparison.Ordinal);
        Assert.Contains("data-entry-summary", html, StringComparison.Ordinal);
        Assert.Contains("data-entry-additional", html, StringComparison.Ordinal);
        if (path == "/Operations/Entry")
        {
            Assert.Equal(3, Regex.Count(html, "data-entry-step=\"(?:product|destination|quantity)\""));
        }
    }

    [Fact]
    public async Task Camera_scanner_library_is_served_locally()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/lib/zxing/zxing-browser.min.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Entry_requires_valid_pin_is_idempotent_and_receipt_is_reloadable()
    {
        var seed = await SeedAsync("WEB-ENTRY", "WEB-ENTRY-AREA", "4201", barcode: "WEB-BAR-ENTRY");
        using var client = CreateClient();
        var operationId = Guid.NewGuid();
        var token = await GetTokenAsync(client, "/Operations/Entry");
        var values = EntryValues(seed, operationId, "9999", token);

        var invalid = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(values));
        var invalidBody = await invalid.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("No fue posible validar el NIP o el usuario", invalidBody);
        Assert.DoesNotContain("value=\"9999\"", invalidBody);
        Assert.Matches("<details[^>]*open[^>]*data-entry-additional", invalidBody);

        values["Input.Pin"] = seed.Pin;
        var success = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.Redirect, success.StatusCode);
        Assert.StartsWith("/Operations/Receipt/", success.Headers.Location?.OriginalString);

        var retry = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        Assert.Equal(success.Headers.Location?.OriginalString, retry.Headers.Location?.OriginalString);

        var receipt = await client.GetAsync(success.Headers.Location);
        var receiptBody = await receipt.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Contains("Entrada confirmada", receiptBody);
        Assert.Contains(seed.Sku, receiptBody);
        Assert.DoesNotContain($"value=\"{seed.Pin}\"", receiptBody);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Equal(1, await db.InventoryMovements.CountAsync(movement => movement.OperationId == operationId));
    }

    [Fact]
    public async Task Exit_transfer_and_adjustment_work_over_http_and_show_negative_receipt()
    {
        var seed = await SeedAsync("WEB-FLOWS", "WEB-FLOW-A", "4202", secondLocationCode: "WEB-FLOW-B");
        using var client = CreateClient();

        await PostSuccessAsync(client, "/Operations/Entry", EntryValues(
            seed, Guid.NewGuid(), seed.Pin, await GetTokenAsync(client, "/Operations/Entry"), 10m));

        var exitValues = CommonValues(seed, Guid.NewGuid(), seed.Pin, await GetTokenAsync(client, "/Operations/Exit"), 12m);
        exitValues["Input.ExitMode"] = "General";
        exitValues["Input.SourceLocationId"] = seed.LocationId.ToString();
        var exit = await PostSuccessAsync(client, "/Operations/Exit", exitValues);
        Assert.Contains("saldo negativo", await (await client.GetAsync(exit)).Content.ReadAsStringAsync());

        var transferValues = CommonValues(seed, Guid.NewGuid(), seed.Pin, await GetTokenAsync(client, "/Operations/Transfer"), 3m);
        transferValues["Input.SourceLocationId"] = seed.LocationId.ToString();
        transferValues["Input.DestinationLocationId"] = seed.SecondLocationId!.Value.ToString();
        await PostSuccessAsync(client, "/Operations/Transfer", transferValues);

        uint version;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            version = (await db.InventoryBalances.SingleAsync(balance =>
                balance.ProductId == seed.ProductId && balance.LocationId == seed.SecondLocationId)).Version;
        }
        var adjustmentValues = CommonValues(seed, Guid.NewGuid(), seed.Pin,
            await GetTokenAsync(client, "/Operations/Adjustment"), 1m);
        adjustmentValues["Input.LocationId"] = seed.SecondLocationId.Value.ToString();
        adjustmentValues["Input.ExpectedBalanceVersion"] = version.ToString();
        adjustmentValues["Input.Notes"] = "Conteo físico de prueba";
        await PostSuccessAsync(client, "/Operations/Adjustment", adjustmentValues);

        var query = await client.GetAsync($"/Inventory?productId={seed.ProductId}");
        var queryBody = await query.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        Assert.Contains(seed.Sku, queryBody);
        Assert.Contains(seed.LocationCode, queryBody);
        Assert.Contains(seed.SecondLocationCode!, queryBody);
    }

    [Fact]
    public async Task Shared_pallet_requires_specific_checkbox_and_a_new_pin_submission()
    {
        var seed = await SeedAsync("WEB-SHARED", "WEB-SHARED-AREA", "4203", assignOtherProduct: true);
        using var client = CreateClient();
        var values = EntryValues(seed, Guid.NewGuid(), seed.Pin, await GetTokenAsync(client, "/Operations/Entry"));

        var conflict = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(values));
        var body = await conflict.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, conflict.StatusCode);
        Assert.Contains("El pallet ya contiene otros productos", body);
        Assert.Contains("OTHER-WEB-SHARED", body);
        Assert.DoesNotContain($"value=\"{seed.Pin}\"", body);

        values["Input.ApprovedSharedLocationIds"] = seed.LocationId.ToString();
        var success = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.Redirect, success.StatusCode);
    }

    [Fact]
    public async Task Admin_cookie_does_not_replace_operational_pin()
    {
        var seed = await SeedAsync("WEB-ADMIN-PIN", "WEB-ADMIN-PIN-AREA", "4204", createAdmin: true);
        using var client = CreateClient();
        var loginToken = await GetTokenAsync(client, "/Admin/Login");
        var login = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = "5204",
            ["__RequestVerificationToken"] = loginToken
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var token = await GetTokenAsync(client, "/Operations/Entry");
        var values = EntryValues(seed, Guid.NewGuid(), string.Empty, token);
        var response = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.False(await db.InventoryMovements.AnyAsync(movement =>
            movement.Lines.Any(line => line.ProductId == seed.ProductId)));
    }

    [Fact]
    public async Task Bidirectional_lookup_handlers_are_public_read_only_and_return_relationships()
    {
        var seed = await SeedAsync("WEB-REL", "WEB-REL-AREA", "4205");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            db.Locations.Add(new Location { Code = seed.Sku, Kind = LocationKind.Area });
            db.ProductLocationAssignments.Add(new ProductLocationAssignment
            {
                ProductId = seed.ProductId,
                LocationId = seed.LocationId
            });
            await db.SaveChangesAsync();
        }

        using var client = CreateClient();
        var locations = Assert.IsType<List<RelationshipLocation>>(
            await client.GetFromJsonAsync<List<RelationshipLocation>>(
                $"/Operations/Lookup?handler=ProductLocations&productId={seed.ProductId}"));
        var products = Assert.IsType<List<RelationshipProduct>>(
            await client.GetFromJsonAsync<List<RelationshipProduct>>(
                $"/Operations/Lookup?handler=LocationProducts&locationId={seed.LocationId}"));

        Assert.Single(locations);
        Assert.Equal(seed.LocationCode, locations[0].Code);
        Assert.True(locations[0].HasActiveAssignment);
        Assert.Single(products);
        Assert.Equal(seed.Sku, products[0].Sku);
        Assert.True(products[0].HasActiveAssignment);

        var resolution = Assert.IsType<CodeResolution>(await client.GetFromJsonAsync<CodeResolution>(
            $"/Operations/Lookup?handler=ResolveCode&code={seed.Sku}"));
        Assert.Equal(seed.ProductId, resolution.Product?.Id);
        Assert.Equal(seed.Sku, resolution.Location?.Code);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Equal(1, await verificationDb.ProductLocationAssignments.CountAsync(assignment =>
            assignment.ProductId == seed.ProductId && assignment.LocationId == seed.LocationId));
        Assert.Empty(await verificationDb.InventoryMovements.Where(movement =>
            movement.Lines.Any(line => line.ProductId == seed.ProductId)).ToListAsync());
    }

    [Fact]
    public async Task Public_inventory_shows_active_assignments_without_a_balance_in_both_directions()
    {
        var seed = await SeedAsync("WEB-PUBLIC-ASSIGNMENT", "WEB-PUBLIC-AREA", "4206");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            db.ProductLocationAssignments.Add(new ProductLocationAssignment
            {
                ProductId = seed.ProductId,
                LocationId = seed.LocationId
            });
            await db.SaveChangesAsync();
        }

        using var client = CreateClient();
        var byProduct = await (await client.GetAsync($"/Inventory?productCode={seed.Sku}")).Content.ReadAsStringAsync();
        var byLocation = await (await client.GetAsync($"/Inventory?locationCode={seed.LocationCode}")).Content.ReadAsStringAsync();

        Assert.Contains(seed.LocationCode, byProduct);
        Assert.Contains("Asignado", byProduct);
        Assert.Contains(seed.Sku, byLocation);
        Assert.Contains("Asignado", byLocation);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Empty(await verificationDb.InventoryBalances.Where(balance =>
            balance.ProductId == seed.ProductId && balance.LocationId == seed.LocationId).ToListAsync());
        Assert.Empty(await verificationDb.InventoryMovements.Where(movement =>
            movement.Lines.Any(line => line.ProductId == seed.ProductId)).ToListAsync());
    }

    [Fact]
    public async Task Inventory_keeps_admin_consultation_actions_out_of_the_public_view()
    {
        const string adminPin = "5299";
        var seed = await SeedAsync(
            "WEB-INVENTORY-ACTIONS",
            "WEB-INVENTORY-ACTIONS-AREA",
            "4299",
            createAdmin: true,
            adminPin: adminPin);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            db.ProductLocationAssignments.Add(new ProductLocationAssignment
            {
                ProductId = seed.ProductId,
                LocationId = seed.LocationId
            });
            await db.SaveChangesAsync();
        }

        using var client = CreateClient();
        var publicHtml = await client.GetStringAsync($"/Inventory?productId={seed.ProductId}");
        Assert.Contains("Asignado · saldo cero", publicHtml, StringComparison.Ordinal);
        Assert.Contains("Saldo sin asignación", publicHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("/Admin/Catalogs/Products/Details", publicHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("/Admin/Catalogs/Products/Edit", publicHtml, StringComparison.Ordinal);

        var loginToken = await GetTokenAsync(client, "/Admin/Login");
        var login = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = adminPin,
            ["__RequestVerificationToken"] = loginToken
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var adminHtml = await client.GetStringAsync($"/Inventory?productId={seed.ProductId}");
        Assert.Contains($"/Admin/Catalogs/Products/Details/{seed.ProductId}", adminHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Movimientos", adminHtml, StringComparison.Ordinal);
        Assert.Contains("Croquis", adminHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("/Admin/Catalogs/Products/Edit", adminHtml, StringComparison.Ordinal);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Empty(await verificationDb.InventoryMovements.Where(movement =>
            movement.Lines.Any(line => line.ProductId == seed.ProductId)).ToListAsync());
    }

    [Fact]
    public async Task Inventory_opens_the_page_containing_an_alert_highlight_only_on_initial_navigation()
    {
        var seed = await SeedAsync("WEB-HIGHLIGHT-PRODUCT", "WEB-HIGHLIGHT-00", "4298");
        Guid highlightedLocationId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var locations = Enumerable.Range(1, 29)
                .Select(number => new Location { Code = $"WEB-HIGHLIGHT-{number:00}", Kind = LocationKind.Rack })
                .ToArray();
            db.AddRange(locations);
            db.ProductLocationAssignments.Add(new ProductLocationAssignment
            {
                ProductId = seed.ProductId,
                LocationId = seed.LocationId
            });
            db.ProductLocationAssignments.AddRange(locations.Select(location => new ProductLocationAssignment
            {
                ProductId = seed.ProductId,
                Location = location
            }));
            await db.SaveChangesAsync();
            highlightedLocationId = locations[^1].Id;
        }

        using var client = CreateClient();
        var initialHtml = await client.GetStringAsync(
            $"/Inventory?productId={seed.ProductId}&highlightLocationId={highlightedLocationId}");
        Assert.Contains("WEB-HIGHLIGHT-29", initialHtml, StringComparison.Ordinal);
        Assert.Contains("2 de 2", initialHtml, StringComparison.Ordinal);
        Assert.Contains("data-inventory-highlighted=\"true\"", initialHtml, StringComparison.Ordinal);

        var explicitPageHtml = await client.GetStringAsync(
            $"/Inventory?productId={seed.ProductId}&highlightLocationId={highlightedLocationId}&pageNumber=1");
        Assert.DoesNotContain("WEB-HIGHLIGHT-29", explicitPageHtml, StringComparison.Ordinal);
        Assert.Contains("1 de 2", explicitPageHtml, StringComparison.Ordinal);
    }

    private HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<string> GetTokenAsync(HttpClient client, string path)
    {
        var html = await (await client.GetAsync(path)).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"No se encontró antiforgery en {path}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static Dictionary<string, string> CommonValues(
        Seed seed, Guid operationId, string pin, string token, decimal quantity) => new()
        {
            ["Input.OperationId"] = operationId.ToString(),
            ["Input.ProductId"] = seed.ProductId.ToString(),
            ["Input.Quantity"] = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Input.Reference"] = "WEB-REF",
            ["Input.Notes"] = "Prueba HTTP",
            ["Input.Pin"] = pin,
            ["__RequestVerificationToken"] = token
        };

    private static Dictionary<string, string> EntryValues(
        Seed seed, Guid operationId, string pin, string token, decimal quantity = 2.5m)
    {
        var values = CommonValues(seed, operationId, pin, token, quantity);
        values["Input.DestinationLocationId"] = seed.LocationId.ToString();
        return values;
    }

    private static async Task<Uri> PostSuccessAsync(
        HttpClient client, string path, Dictionary<string, string> values)
    {
        var response = await client.PostAsync(path, new FormUrlEncodedContent(values));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Redirect,
            $"{path} devolvió {response.StatusCode}: {body}");
        return response.Headers.Location!;
    }

    private async Task<Seed> SeedAsync(
        string sku,
        string locationCode,
        string pin,
        string? barcode = null,
        string? secondLocationCode = null,
        bool assignOtherProduct = false,
        bool createAdmin = false,
        string adminPin = "5204")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var pinService = scope.ServiceProvider.GetRequiredService<UserPinService>();
        var user = new User
        {
            FullName = $"Operador {sku}",
            RoleId = 2,
            PinLookup = string.Empty,
            PinHash = string.Empty
        };
        Assert.Equal(PinAssignmentResult.Success, await pinService.AssignAsync(user, pin));
        var product = new Product { Sku = sku, Description = $"Producto {sku}", BaseUnitId = 1 };
        if (barcode is not null)
            product.Barcodes.Add(new ProductBarcode { Barcode = barcode, IsPrimary = true });
        var location = new Location { Code = locationCode, Kind = LocationKind.Area };
        Location? secondLocation = secondLocationCode is null
            ? null
            : new Location { Code = secondLocationCode, Kind = LocationKind.Area };
        db.AddRange(user, product, location);
        if (secondLocation is not null)
            db.Add(secondLocation);

        if (assignOtherProduct)
        {
            var other = new Product { Sku = $"OTHER-{sku}", BaseUnitId = 1 };
            db.Add(other);
            db.ProductLocationAssignments.Add(new ProductLocationAssignment
            {
                Product = other,
                Location = location
            });
        }

        if (createAdmin)
        {
            var admin = new User
            {
                FullName = $"Administrador {sku}",
                RoleId = 1,
                PinLookup = string.Empty,
                PinHash = string.Empty
            };
            Assert.Equal(PinAssignmentResult.Success, await pinService.AssignAsync(admin, adminPin));
            db.Add(admin);
        }

        await db.SaveChangesAsync();
        return new(product.Id, product.Sku, location.Id, location.Code,
            secondLocation?.Id, secondLocation?.Code, pin);
    }

    private sealed record Seed(
        Guid ProductId,
        string Sku,
        Guid LocationId,
        string LocationCode,
        Guid? SecondLocationId,
        string? SecondLocationCode,
        string Pin);

    private sealed record RelationshipLocation(Guid Id, string Code, bool HasActiveAssignment);
    private sealed record RelationshipProduct(Guid Id, string Sku, bool HasActiveAssignment);
    private sealed record CodeResolution(RelationshipProduct? Product, RelationshipLocation? Location);
}
