using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Web;

public sealed class SupplierSheetRouteTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Public_sheet_revalidates_selection_and_prints_without_writes(string culture)
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("Cookie", $"WarehouseEPI.Language={culture}");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var products = Enumerable.Range(0, 103).Select(i => new Product
        {
            Sku = $"SUP-{i:D3}", BaseUnitId = 1,
            Description = "Material de prueba para proveedor con descripción extensa / long supplier product description"
        }).ToList();
        products[98].Sku = new string('W', 80);
        products[99].Sku = "SKU-Ñ";
        products[100].IsActive = false;
        db.Products.AddRange(products);
        await db.SaveChangesAsync();
        var before = await db.InventoryMovements.CountAsync();
        const string url = "/Operations/Labels/SupplierSheet";
        var html = await client.GetStringAsync(url);
        Assert.Contains($"<html lang=\"{culture}\">", html);
        Assert.Contains("suggestion-lookup", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        await Fixture("initial", html);
        var denied = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["ProductIds"] = products[0].Id.ToString() }));
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);

        async Task<string> Post(IEnumerable<Guid> ids, string supplier = "Proveedor de prueba & Asociados", bool? preview = null, bool allProducts = false)
        {
            var values = ids.Select(id => new KeyValuePair<string, string>("ProductIds", id.ToString())).ToList();
            values.Add(new("__RequestVerificationToken", token));
            values.Add(new("SupplierName", supplier));
            values.Add(new("SearchText", "búsqueda pendiente"));
            values.Add(new("AllProducts", allProducts.ToString()));
            if (preview.HasValue) values.Add(new("preview", preview.Value.ToString()));
            var response = await client.PostAsync(preview.HasValue ? url + "?handler=Restore" : url, new FormUrlEncodedContent(values));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }
        html = await Post([products[1].Id, products[0].Id]);
        Assert.Equal(2, Regex.Count(html, "data-sheet-row"));
        var printed = html[html.IndexOf("<tbody>", StringComparison.Ordinal)..];
        Assert.True(printed.IndexOf("SUP-001", StringComparison.Ordinal) < printed.IndexOf("SUP-000", StringComparison.Ordinal));
        Assert.Contains("<svg", printed);
        Assert.DoesNotContain("Material de prueba", printed);
        Assert.Contains("data-printed-supplier", html);
        Assert.Contains("Proveedor de prueba &amp; Asociados", html);
        await Fixture("preview", html);
        html = await Post(products.Take(40).Select(p => p.Id));
        Assert.Equal(40, Regex.Count(html, "data-sheet-row"));
        await Fixture("multipage", html);
        html = await Post(products.Where(p => p.IsActive && p.Sku != "SKU-Ñ" && p.Sku.Length < 80).Select(p => p.Id));
        Assert.Equal(100, Regex.Count(html, "data-sheet-row"));
        html = await Post([products[0].Id], "");
        Assert.Contains("data-sheet-preview", html);
        Assert.DoesNotContain("data-printed-supplier", html);
        html = await Post([products[0].Id], new string('X', 201));
        Assert.Contains("validation-summary-errors", html);
        Assert.DoesNotContain("data-sheet-preview", html);

        foreach (var ids in new[] {
            Array.Empty<Guid>(), new[] { products[0].Id, products[0].Id },
            new[] { Guid.NewGuid() }, new[] { products[100].Id },
            new[] { products[98].Id }, new[] { products[99].Id },
            products.Select(p => p.Id).ToArray()
        })
        {
            html = await Post(ids);
            Assert.DoesNotContain("data-sheet-preview", html);
            Assert.Contains("validation-summary-errors", html);
            Assert.Equal(ids.Length, Regex.Count(html, "data-product-id="));
            Assert.Contains("Proveedor de prueba &amp; Asociados", html);
        }
        Assert.Equal(before, await db.InventoryMovements.CountAsync());
        client.DefaultRequestHeaders.Remove("Cookie");
        var targetLanguage = culture == "es" ? "en" : "es";
        var languageResponse = await client.PostAsync("/Preferences/Language", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["language"] = targetLanguage, ["returnUrl"] = url
        }));
        Assert.Equal(HttpStatusCode.OK, languageResponse.StatusCode);
        html = await Post([products[1].Id, products[0].Id], preview: true);
        Assert.Contains($"<html lang=\"{targetLanguage}\">", html);
        Assert.Contains(targetLanguage == "en" ? "Supplier barcode sheet" : "Hoja de códigos para proveedores", WebUtility.HtmlDecode(html));
        Assert.Equal(2, Regex.Count(html, "data-sheet-row"));
        Assert.Contains("Proveedor de prueba &amp; Asociados", html);
        html = await Post([products[0].Id], preview: false);
        Assert.DoesNotContain("data-sheet-preview", html);
        Assert.Contains("búsqueda pendiente", WebUtility.HtmlDecode(html));
        Assert.Equal(1, Regex.Count(html, "data-product-id="));
        html = await Post([], preview: false);
        Assert.DoesNotContain("validation-summary-errors", html);
        html = await Post([], allProducts: true);
        Assert.Contains("validation-summary-errors", html);
        Assert.DoesNotContain("data-sheet-preview", html);
        // Invalid inactive SKUs must neither appear nor prevent the active catalog from printing.
        products[98].Sku = "#2 CREPED 24\" X 100YD 121F PK22";
        products[99].Sku = "SUP-099";
        products[100].Sku = new string('W', 80);
        await db.SaveChangesAsync();
        html = await Post([], allProducts: true);
        Assert.Equal(102, Regex.Count(html, "data-sheet-row"));
        Assert.DoesNotContain(products[100].Sku, html);
        Assert.DoesNotContain("data-product-id=", html);
        await Fixture("all", html, targetLanguage);
        html = await Post([], preview: true, allProducts: true);
        Assert.Equal(102, Regex.Count(html, "data-sheet-row"));
        Assert.Contains("checked=\"checked\"", html);
        foreach (var product in products) product.IsActive = false;
        await db.SaveChangesAsync();
        html = await Post([], allProducts: true);
        Assert.DoesNotContain("data-sheet-preview", html);
        Assert.Contains("validation-summary-errors", html);

        async Task Fixture(string name, string content, string? language = null)
        {
            var output = Environment.GetEnvironmentVariable("WAREHOUSE_SUPPLIER_FIXTURES");
            if (string.IsNullOrEmpty(output)) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"{name}-{language ?? culture}.html"), Regex.Replace(content,
                "(<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]+", "$1fixture"));
        }
    }
}
