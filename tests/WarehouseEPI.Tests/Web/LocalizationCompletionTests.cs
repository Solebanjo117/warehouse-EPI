using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Localization;
using WarehouseEPI.Web.Locations;
using WarehouseEPI.Web.Pages.Locations;
using WarehouseEPI.Web.Production;

namespace WarehouseEPI.Tests.Web;

public sealed class LocalizationCompletionTests
{
    [Fact]
    public void Razor_literal_keys_exist_in_the_catalog_used_by_the_view()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var web = Path.Combine(directory.FullName, "src", "WarehouseEPI.Web");
        var catalogs = Directory.GetFiles(Path.Combine(web, "Resources", "Localization"), "*.resx")
            .Where(file => !file.EndsWith(".en.resx", StringComparison.Ordinal))
            .ToDictionary(file => Path.GetFileNameWithoutExtension(file)!, file => XDocument.Load(file).Root!.Elements("data")
                .Select(entry => entry.Attribute("name")!.Value).ToHashSet(StringComparer.Ordinal));
        var missing = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(web, "Pages"), "*.cshtml", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            var aliases = new Dictionary<string, string> { ["SharedTexts"] = "SharedTexts", ["ClientTexts"] = "ClientTexts" };
            foreach (Match injection in Regex.Matches(source, @"@inject\s+(?:[\w.]+\.)?IStringLocalizer<(?:[\w.]+\.)?([A-Za-z]+Texts)>\s+(\w+)"))
                aliases[injection.Groups[2].Value] = injection.Groups[1].Value;
            foreach (var (alias, catalog) in aliases)
                foreach (Match usage in Regex.Matches(source, @"\b" + Regex.Escape(alias) + "\\[\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                {
                    var key = usage.Groups[1].Value.Replace("\\\"", "\"", StringComparison.Ordinal);
                    if (!catalogs[catalog].Contains(key)) missing.Add($"{Path.GetRelativePath(web, file)}: {catalog} / {key}");
                }
        }
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("es", "Exhibición de filas", "Pausar", " y ", "Selecciona al menos una fila para iniciar la exhibición.")]
    [InlineData("en", "Row display", "Pause", " and ", "Select at least one row to start the display.")]
    public async Task Display_uses_selected_language_in_configuration_playback_and_refresh(
        string language, string title, string pause, string conjunction, string error)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        for (short rack = 1; rack <= 2; rack++)
            db.Locations.Add(new Location { Code = $"A-{rack}-1", Kind = LocationKind.Rack, RowCode = "A", RackNumber = rack, PalletNumber = 1 });
        db.WarehouseMapLayouts.Add(new WarehouseMapLayout { Elements = [
            new WarehouseMapElement { Kind = WarehouseMapElementKind.Rack, RowCode = "A", RackNumber = 1, X = 100, Y = 300, Width = 90, Height = 42 },
            new WarehouseMapElement { Kind = WarehouseMapElementKind.Rack, RowCode = "A", RackNumber = 2, X = 300, Y = 300, Width = 90, Height = 42 }] });
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        var config = WebUtility.HtmlDecode(await client.GetStringAsync("/Locations/Display"));
        Assert.Contains($"<html lang=\"{language}\">", config);
        Assert.Contains(title, config);
        var invalid = WebUtility.HtmlDecode(await client.GetStringAsync("/Locations/Display?play=true"));
        Assert.Contains(error, invalid);
        foreach (var suffix in new[] { "", "&refresh=true" })
        {
            var html = await client.GetStringAsync("/Locations/Display?play=true&rows=A&racks=2&here=500,300" + suffix);
            var decoded = WebUtility.HtmlDecode(html);
            Assert.Contains($"<html lang=\"{language}\">", decoded);
            Assert.Contains("A-1" + conjunction + "A-2", decoded);
            Assert.Contains($"data-display-pause aria-pressed=\"false\">{pause}</button>", decoded);
            var dictionary = Regex.Match(html, "data-ui-texts=\"([^\"]+)\"");
            Assert.True(dictionary.Success);
            var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(WebUtility.HtmlDecode(dictionary.Groups[1].Value))!;
            Assert.Equal(pause, texts["Pausar"]);
            Assert.Contains("{0}", texts["Sigue el rack {0}"]);
            foreach (var script in new[] { "location-display.js", "location-display-config.js" })
            {
                var source = File.ReadAllText(Path.Combine(factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>()
                    .WebRootPath, "js", script));
                foreach (Match key in Regex.Matches(source, "translate\\(\\s*\"([^\"]+)\""))
                    Assert.True(texts.ContainsKey(key.Groups[1].Value), $"Missing standalone text: {script} / {key.Groups[1].Value}");
            }
            Assert.DoesNotContain("/js/site.js", html);
        }
    }

    [Theory]
    [InlineData("es", "Últimos 7 días", "01/09/2026 a 02/09/2026", "Selecciona una métrica válida para el mapa de calor.")]
    [InlineData("en", "Last 7 days", "01/09/2026 to 02/09/2026", "Select a valid metric for the heatmap.")]
    public async Task Heatmap_json_localizes_periods_and_errors_without_changing_filter_formats(
        string language, string period, string custom, string error)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        const string route = "/Locations?handler=HeatmapData";
        using var invalid = await client.GetAsync(route + "&mapMetric=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidJson = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        Assert.Equal(error, invalidJson.RootElement.GetProperty("error").GetString());
        using var recent = JsonDocument.Parse(await client.GetStringAsync(route + "&mapMetric=activity&period=7"));
        Assert.Equal(period, recent.RootElement.GetProperty("periodLabel").GetString());
        Assert.Equal("7", recent.RootElement.GetProperty("period").GetString());
        using var range = JsonDocument.Parse(await client.GetStringAsync(route + "&mapMetric=activity&period=custom&from=2026-09-02&to=2026-09-01"));
        Assert.Equal(custom, range.RootElement.GetProperty("periodLabel").GetString());
        Assert.Equal("2026-09-01", range.RootElement.GetProperty("from").GetString());
        Assert.Equal("2026-09-02", range.RootElement.GetProperty("to").GetString());
        Assert.Equal("activity", range.RootElement.GetProperty("metric").GetString());
        Assert.Equal(JsonValueKind.Array, range.RootElement.GetProperty("racks").ValueKind);
    }

    [Theory]
    [InlineData("es", "Últimos 7 días", "01/09/2026 a 02/09/2026", "Corte", "Dos racks se superponen.")]
    [InlineData("en", "Last 7 days", "01/09/2026 to 02/09/2026", "Cutting", "Two racks overlap.")]
    public async Task Web_message_boundaries_preserve_identifiers_and_operational_formats(
        string language, string period, string custom, string area, string warning)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalization(options => options.ResourcesPath = "Resources");
            using var provider = services.BuildServiceProvider();
            var catalog = provider.GetRequiredService<IStringLocalizer<CatalogTexts>>();
            var production = provider.GetRequiredService<IStringLocalizer<ProductionTexts>>();
            await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase($"LocalizationCompletion-{Guid.NewGuid():N}").Options);
            db.Database.EnsureCreated();
            var clock = new WarehouseClock(new WarehouseSettingsService(db));
            var result = await HeatmapQueryNormalizer.BuildAsync("activity", "7", null, null, clock, texts: catalog);
            Assert.Equal(period, result.PeriodLabel);
            Assert.Equal("7", result.Period);
            result = await HeatmapQueryNormalizer.BuildAsync("activity", "custom", new(2026, 9, 2), new(2026, 9, 1), clock, texts: catalog);
            Assert.Equal(custom, result.PeriodLabel);
            Assert.Equal(new DateOnly(2026, 9, 1), result.From);
            Assert.Equal(warning, WarehouseMapText.Message(catalog, "Dos racks se superponen."));
            var translated = ProductionDailyText.Message(production, "SKU-ES · Cutting: el pendiente cambió o la cantidad no es válida. Revisa el origen.");
            Assert.StartsWith("SKU-ES · " + area + ":", translated, StringComparison.Ordinal);
            var aisle = WarehouseMapText.Message(catalog, "El pasillo mide 31.5 in de ancho; 32 in es una referencia editorial, no normativa.");
            Assert.Contains("31.5 in", aisle);
            if (language == "en") Assert.StartsWith("The aisle", aisle, StringComparison.Ordinal);
            Assert.Equal("Descripción humana", WarehouseMapText.Message(catalog, "Descripción humana"));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}
