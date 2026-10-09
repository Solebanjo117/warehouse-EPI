using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Tests.Web;

public sealed class LocationLocalizationTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Location_map_racks_details_and_print_use_the_selected_language(string language)
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var unit = await db.Units.FirstAsync();
        var products = new[]
        {
            new Product { Sku = "LOC-ONE", Description = "Descripción humana", BaseUnitId = unit.Id },
            new Product { Sku = "LOC-TWO", BaseUnitId = unit.Id }
        };
        var positions = Enumerable.Range(1, 6).Select(pallet => new Location
        {
            Code = $"E-9-{pallet}",
            Kind = LocationKind.Rack,
            RowCode = "E",
            RackNumber = 9,
            PalletNumber = (short)pallet,
            IsBlocked = pallet == 3,
            IsActive = pallet != 4,
            BlockReason = pallet == 3 ? "Revisión humana" : null
        }).ToArray();
        db.AddRange(products);
        db.AddRange(positions);
        foreach (var product in products)
        {
            db.ProductLocationAssignments.Add(new ProductLocationAssignment { Product = product, Location = positions[0] });
            db.InventoryBalances.Add(new InventoryBalance { Product = product, Location = positions[1], Quantity = 12.5m });
        }
        db.InventoryBalances.Add(new InventoryBalance { Product = products[0], Location = positions[2], Quantity = -1 });
        db.InventoryBalances.Add(new InventoryBalance { Product = products[0], Location = positions[4], Quantity = 3 });
        db.ProductLocationAssignments.Add(new ProductLocationAssignment { Product = products[0], Location = positions[4] });
        db.WarehouseMapLayouts.Add(new WarehouseMapLayout
        {
            Elements = [new WarehouseMapElement
            {
                Kind = WarehouseMapElementKind.Rack, RowCode = "E", RackNumber = 9,
                X = 100, Y = 100, Width = 90, Height = 42
            }]
        });
        var role = await db.Roles.SingleAsync(item => item.Code == "ADMIN");
        var user = new User { FullName = "Location localization admin", RoleId = role.Id, PinLookup = "", PinHash = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var english = language == "en";
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new("https://localhost"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        foreach (var administrative in new[] { false, true })
        {
            if (administrative) await SignInAsync(client);
            var index = administrative ? "/Admin/Catalogs/Locations" : "/Locations";
            var map = WebUtility.HtmlDecode(await client.GetStringAsync(index));
            foreach (var (position, count) in new[] { (positions[0], 0), (positions[1], 2), (positions[2], 1), (positions[5], 0) })
            {
                var button = Regex.Match(map, $"<button[^>]*data-map-position=\"{position.Id}\"[^>]*>.*?</button>", RegexOptions.Singleline);
                Assert.True(button.Success);
                Assert.Contains($"<span>{count} {(english ? "product(s)" : "producto(s)")}</span>", button.Value);
            }

            var racks = WebUtility.HtmlDecode(await client.GetStringAsync(index + "?viewMode=racks&status=all"));
            foreach (var (filter, label) in new[]
            {
                ("all", english ? "All" : "Todos"),
                ("occupied", english ? "With stock" : "Con saldo"),
                ("empty", english ? "No stock" : "Sin saldo"),
                ("issues", english ? "With issues" : "Con incidencias")
            })
                Assert.Matches($"<a[^>]*class=\"rack-filter rack-filter-{filter}[^\"]*\"[^>]*>{label}</a>", racks);
            Assert.Contains($"<span class=\"sku-more\">+1 {(english ? "product(s)" : "producto(s)")}</span>", racks);
            Assert.Contains($"<strong>3</strong> {(english ? "no stock" : "sin saldo")}", racks);

            var details = index + "/";
            var empty = WebUtility.HtmlDecode(await client.GetStringAsync(details + positions[0].Id));
            Assert.Contains(english ? "Row E · Rack 9 · Pallet 1" : "Fila E · Rack 9 · Pallet 1", empty);
            Assert.Contains(english ? "No current stock · 2 assigned product(s)." : "Sin saldo actual · 2 producto(s) asignado(s).", empty);
            Assert.Contains($"<span class=\"sku-more\">{(english ? "+1 assigned" : "+1 asignado(s)")}</span>", empty);
            Assert.Contains(english ? "Current location: E-9-1, pallet 1, Active, Assigned without stock" : "Ubicación actual: E-9-1, pallet 1, Activa, Asignado sin saldo", empty);
            Assert.Contains(english ? "Pallet 7, position does not exist" : "Pallet 7, posición inexistente", empty);
            foreach (var state in english
                ? new[] { "Assigned without stock", "Unassigned stock", "Negative balance", "With stock", "No product" }
                : new[] { "Asignado sin saldo", "Saldo sin asignación", "Saldo negativo", "Con saldo", "Sin producto" })
                Assert.Contains($"<span class=\"rack-slot-state\">{state}</span>", empty);
            var occupied = WebUtility.HtmlDecode(await client.GetStringAsync(details + positions[1].Id));
            Assert.Contains($"<span class=\"sku-more\">+1 {(english ? "product(s)" : "producto(s)")}</span>", occupied);
            Assert.Contains("Descripción humana", occupied);
            Assert.Contains("LOC-ONE", occupied);

            var print = WebUtility.HtmlDecode(await client.GetStringAsync(index + "/Rack/Print?rowCode=E&rackNumber=9"));
            Assert.Contains($"<span class=\"sheet-cell-state\">{(english ? "Blocked: Revisión humana" : "Bloqueada: Revisión humana")}</span>", print);
            Assert.Contains($"<span class=\"sheet-cell-state\">{(english ? "Inactive" : "Inactiva")}</span>", print);
            foreach (var state in english
                ? new[] { "Assigned without stock", "Unassigned stock", "Assigned" }
                : new[] { "Asignado sin saldo", "Saldo sin asignación", "Asignado" })
                Assert.Contains($"<span class=\"sheet-product-rel\">{state}</span>", print);
            Assert.Contains("Revisión humana", print);
        }
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Rack_and_area_editors_localize_headings_and_confirmation_labels(string language)
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var area = new Location { Code = "LOC-AREA", Kind = LocationKind.Area, WarnOnMixedProducts = language == "en" };
        db.AddRange(area, new Location { Code = "E-2-1", Kind = LocationKind.Rack, RowCode = "E", RackNumber = 2, PalletNumber = 1 });
        var role = await db.Roles.SingleAsync(item => item.Code == "ADMIN");
        var user = new User { FullName = "Location editor admin", RoleId = role.Id, PinLookup = "", PinHash = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new("https://localhost"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        await SignInAsync(client);
        var rack = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Catalogs/Locations/Rack/Edit?rowCode=E&rackNumber=2"));
        Assert.Contains($"<h1 class=\"h2 mt-2 mb-1\">{(language == "en" ? "Edit rack E-2" : "Editar rack E-2")}</h1>", rack);
        Assert.Contains(language == "en" ? "Type E-2 to confirm" : "Escribe E-2 para confirmar", rack);
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/Admin/Catalogs/Locations/Area?locationId={area.Id}"));
        Assert.Contains(language == "en" ? "Type LOC-AREA to confirm" : "Escribe LOC-AREA para confirmar", html);
        Assert.Contains(language == "en" ? "Warn when adding a product while the area contains others" : "Avisar al agregar un producto cuando el área contiene otros", html);
        Assert.Contains("name=\"Input.WarnOnMixedProducts\"", html);
        var warningInput = Regex.Match(html, "<input[^>]*type=\"checkbox\"[^>]*name=\"Input.WarnOnMixedProducts\"[^>]*>");
        Assert.True(warningInput.Success);
        Assert.Equal(area.WarnOnMixedProducts, warningInput.Value.Contains("checked=\"checked\"", StringComparison.Ordinal));
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var login = await client.GetStringAsync("/Admin/Login");
        var token = Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success);
        using var response = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = "0123",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
