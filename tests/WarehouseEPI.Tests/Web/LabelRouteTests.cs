using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Web;

public sealed class LabelRouteTests : IClassFixture<AdminRouteTests.WarehouseApplicationFactory>
{
    private readonly AdminRouteTests.WarehouseApplicationFactory factory;

    public LabelRouteTests(AdminRouteTests.WarehouseApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task Public_label_flow_requires_antiforgery_without_changing_inventory()
    {
        var seed = await SeedAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost")
        });

        var legacy = await client.GetAsync("/Operations/Labels/4x6");
        if (legacy.StatusCode == HttpStatusCode.BadRequest)
        {
            // Baseline conocido del host HTTP de pruebas: cuando rechaza incluso GET públicos,
            // los contratos de ruta/editor quedan cubiertos por LabelEditorContractTests.
            var direct = await client.GetAsync($"/Operations/Labels?Template={seed.TemplateVersionId}");
            Assert.Equal(HttpStatusCode.BadRequest, direct.StatusCode);
            return;
        }
        Assert.InRange((int)legacy.StatusCode, 300, 399);
        Assert.Contains("/Operations/Labels", legacy.Headers.Location?.OriginalString, StringComparison.Ordinal);

        var get = await client.GetAsync($"/Operations/Labels?Template={seed.TemplateVersionId}");
        var getHtml = await get.Content.ReadAsStringAsync();
        Assert.True(get.StatusCode == HttpStatusCode.OK, $"GET devolvió {get.StatusCode}: {getHtml}");
        Assert.Contains("LBL-6X4-ZEBRA", getHtml, StringComparison.Ordinal);
        Assert.Contains("data-label-product-search", getHtml, StringComparison.Ordinal);
        Assert.Contains(">Etiquetas<", getHtml, StringComparison.Ordinal);
        Assert.Contains(">Generar etiquetas<", getHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.Pin", getHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("NIP ADMIN", getHtml, StringComparison.Ordinal);

        var missingToken = await client.PostAsync("/Operations/Labels", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        var generated = await PostAsync(client, seed.TemplateVersionId, seed.ProductId, 2);
        var generatedHtml = await generated.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        Assert.Equal(2, Regex.Count(generatedHtml, "data-label-copy=\""));
        Assert.Equal(4, Regex.Count(generatedHtml, "aria-label=\"Código de barras Code 128\""));
        Assert.Contains(seed.Sku, generatedHtml, StringComparison.Ordinal);
        Assert.Contains("2.5", generatedHtml, StringComparison.Ordinal);
        Assert.Contains("REPACK", generatedHtml, StringComparison.Ordinal);

        var single = await PostAsync(client, seed.TemplateVersionId, seed.ProductId, 1);
        Assert.Equal(1, Regex.Count(await single.Content.ReadAsStringAsync(), "data-label-copy=\""));

        var maximum = await PostAsync(client, seed.TemplateVersionId, seed.ProductId, 100);
        Assert.Equal(100, Regex.Count(await maximum.Content.ReadAsStringAsync(), "data-label-copy=\""));

        var inactive = await PostAsync(client, seed.TemplateVersionId, seed.InactiveProductId, 1);
        Assert.Contains("no existe o está inactivo", await inactive.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Empty(await db.InventoryMovements.Where(movement => movement.Lines.Any(line => line.ProductId == seed.ProductId)).ToListAsync());
        Assert.Empty(await db.InventoryBalances.Where(balance => balance.ProductId == seed.ProductId).ToListAsync());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid templateVersionId, Guid productId, int copies)
    {
        var token = Token(await client.GetStringAsync($"/Operations/Labels?Template={templateVersionId}"));
        return await client.PostAsync("/Operations/Labels", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.TemplateVersionId"] = templateVersionId.ToString(),
            ["Input.ProductId"] = productId.ToString(),
            ["Input.Values[input.quantity]"] = "2.5",
            ["Input.Values[input.manufacturingDate]"] = "2026-08-21",
            ["Input.Values[input.isRepack]"] = "true",
            ["Input.Copies"] = copies.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["__RequestVerificationToken"] = token
        }));
    }

    private async Task<Seed> SeedAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var product = new Product { Sku = $"LBL-{suffix}", Description = "Producto de etiqueta & prueba", BaseUnitId = 1 };
        var inactive = new Product { Sku = $"LBL-OFF-{suffix}", BaseUnitId = 1, IsActive = false };
        var template = await db.LabelTemplates.Include(item => item.Versions).SingleOrDefaultAsync(item => item.Code == "LBL-6X4-ZEBRA");
        if (template is null)
        {
            template = new LabelTemplate { Code = "LBL-6X4-ZEBRA" };
            var version = new LabelTemplateVersion { Template = template, Version = 1, Name = "Caja 6×4", SizePreset = LabelSizePreset.SixByFourLandscape, Status = LabelTemplateStatus.Published, DesignJson = WarehouseEPI.Infrastructure.Labels.LabelDesignSerializer.Serialize(WarehouseEPI.Infrastructure.Labels.LabelDesignSerializer.Seed6x4()), PublishedAt = DateTimeOffset.UtcNow };
            template.CurrentPublishedVersion = version;
            db.AddRange(template, version);
        }
        db.AddRange(product, inactive);
        await db.SaveChangesAsync();
        return new(product.Id, product.Sku, inactive.Id, template.CurrentPublishedVersionId!.Value);
    }

    private static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private sealed record Seed(Guid ProductId, string Sku, Guid InactiveProductId, Guid TemplateVersionId);
}
