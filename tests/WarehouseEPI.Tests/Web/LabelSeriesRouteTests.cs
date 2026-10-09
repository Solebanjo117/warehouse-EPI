using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Tests.Labels;

namespace WarehouseEPI.Tests.Web;

public sealed class LabelSeriesRouteTests
{
    [Fact]
    public async Task Series_preview_orders_copies_and_rejects_over_limit_without_writes()
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var version = LabelSeriesTests.Version();
        version.Template.Code = "SERIES-TEST";
        version.Template.CurrentPublishedVersionId = version.Id;
        version.Template.CurrentPublishedVersion = version;
        var product = new Product { Sku = "SERIES-SKU", BaseUnitId = 1 };
        db.AddRange(version.Template, version, product);
        await db.SaveChangesAsync();
        var before = await db.InventoryMovements.CountAsync();
        var html = await client.GetStringAsync($"/Operations/Labels?Template={version.Id}");
        Assert.Matches("<option[^>]*(?:selected[^>]*value=\"rollNumber\"|value=\"rollNumber\"[^>]*selected)", html);
        Assert.Contains("data-label-device-date=\"true\"", html);
        var output = Environment.GetEnvironmentVariable("WAREHOUSE_LABEL_SERIES_FIXTURES");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "initial.html"), Regex.Replace(html,
                "(<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]+", "$1fixture"));
        }
        var data = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value),
            ["Input.TemplateVersionId"] = version.Id.ToString(), ["Input.ProductId"] = product.Id.ToString(),
            ["Input.SeriesField"] = "rollNumber", ["Input.SeriesEnd"] = "003", ["Input.Copies"] = "2",
            ["Input.Values[rollNumber]"] = "001", ["Input.Values[yards]"] = "1000",
            ["Input.Values[receivingMfgDate]"] = "2025-01-02"
        };
        var response = await client.PostAsync("/Operations/Labels", new FormUrlEncodedContent(data));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-label-device-date=\"false\"", html);
        Assert.Contains("value=\"2025-01-02\"", html);
        Assert.Equal(6, Regex.Count(html, "data-label-copy=\""));
        var pages = Regex.Matches(html, "<article class=\"label-dynamic\".*?</article>", RegexOptions.Singleline);
        Assert.Equal(new[] { "001", "001", "002", "002", "003", "003" }, pages.Select(page =>
            Regex.Match(page.Value, ">(00[123])</div>").Groups[1].Value));
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "series.html"), Regex.Replace(html,
                "(<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]+", "$1fixture"));
        }
        data["Input.SeriesEnd"] = "051";
        response = await client.PostAsync("/Operations/Labels", new FormUrlEncodedContent(data));
        html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("data-label-copy=\"", html);
        Assert.Contains("100 etiquetas", html);
        Assert.Contains("value=\"051\"", html);
        Assert.Equal(before, await db.InventoryMovements.CountAsync());
        data["Input.SeriesField"] = "";
        response = await client.PostAsync("/Operations/Labels", new FormUrlEncodedContent(data));
        html = await response.Content.ReadAsStringAsync();
        Assert.Equal(2, Regex.Count(html, "data-label-copy=\""));
    }
}
