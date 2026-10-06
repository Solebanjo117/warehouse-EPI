using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Tests.Web;

public sealed class AreaAdministrationTests
{
    private const string Editor = "/Admin/Catalogs/Locations/Area";
    private const string Listing = "/Admin/Catalogs/Locations/Areas";

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Admin_can_find_create_edit_and_requery_area_description(string language)
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = await SignInAsync(factory, language);
        var html = await client.GetStringAsync(Editor);
        var create = Fields(html);
        create["Input.Code"] = "WIP-AREA-EDIT";
        create["Input.Description"] = "Descripción original";
        create["Input.OperationalRole"] = "Wip";
        using var created = await client.PostAsync(Editor, new FormUrlEncodedContent(create));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var area = await db.Locations.AsNoTracking().SingleAsync(x => x.Code == "WIP-AREA-EDIT");
        Assert.Equal("Descripción original", area.Description);
        html = await client.GetStringAsync($"{Editor}?locationId={area.Id}");
        await CaptureAsync(html, $"area-editor-{language}");
        var edit = Fields(html);
        edit["Input.Description"] = "Descripción corregida";
        using var edited = await client.PostAsync(Editor, new FormUrlEncodedContent(edit));
        Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);
        Assert.Contains(area.Id.ToString(), edited.Headers.Location!.ToString());
        var detail = WebUtility.HtmlDecode(await client.GetStringAsync(edited.Headers.Location));
        Assert.Contains("Descripción corregida", detail);
        Assert.Contains(language == "en" ? "was saved" : "Se guardó", detail);
        var suggestions = await scope.ServiceProvider.GetRequiredService<OperationalInventoryQueryService>()
            .SearchWipLocationsAsync("WIP-AREA-EDIT");
        Assert.Equal("Descripción corregida", Assert.Single(suggestions).Description);
        Assert.Equal(area.Id, suggestions[0].Id);
        var listingHtml = await client.GetStringAsync(Listing + "?search=WIP-AREA-EDIT&kind=rack&rowCode=Z");
        var listing = WebUtility.HtmlDecode(listingHtml);
        Assert.Contains("Descripción corregida", listing);
        Assert.Contains($"locationId={area.Id}", listing);
        Assert.Contains(language == "en" ? "Search areas" : "Buscar área", listing);
        await CaptureAsync(listingHtml, $"area-list-{language}");
    }

    [Fact]
    public async Task State_changes_and_stale_edits_preserve_current_data()
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = await SignInAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var area = new Location { Code = "WIP-STATE", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        db.Add(area);
        await db.SaveChangesAsync();
        var url = $"{Editor}?locationId={area.Id}";
        var stale = Fields(await client.GetStringAsync(url));
        var block = Fields(await client.GetStringAsync(url));
        block["Input.IsBlocked"] = "true";
        block["Input.BlockReason"] = "Revisión";
        using var blocked = await client.PostAsync(Editor, new FormUrlEncodedContent(block));
        Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
        stale["Input.Description"] = "No sobrescribir";
        using var conflict = await client.PostAsync(Editor, new FormUrlEncodedContent(stale));
        Assert.Equal(HttpStatusCode.OK, conflict.StatusCode);
        Assert.Contains("El área cambió", WebUtility.HtmlDecode(await conflict.Content.ReadAsStringAsync()));
        await db.Entry(area).ReloadAsync();
        Assert.True(area.IsBlocked);
        Assert.Equal("Revisión", area.BlockReason);
        Assert.Null(area.Description);
        var deactivate = Fields(await client.GetStringAsync(url));
        deactivate["Input.IsActive"] = "false";
        deactivate["Input.IsBlocked"] = "true";
        using var deactivated = await client.PostAsync(Editor, new FormUrlEncodedContent(deactivate));
        Assert.Equal(HttpStatusCode.Redirect, deactivated.StatusCode);
        await db.Entry(area).ReloadAsync();
        Assert.False(area.IsActive);
        Assert.False(area.IsBlocked);
        Assert.Null(area.BlockReason);
        Assert.Contains(area.Code, await client.GetStringAsync(Listing));
        Assert.DoesNotContain(area.Code, await client.GetStringAsync(Listing + "?status=active"));
    }

    [Theory]
    [InlineData("Input.Code", "DUPLICATE")]
    [InlineData("Input.OperationalRole", "99")]
    [InlineData("Input.IsBlocked", "true")]
    [InlineData("Input.Description", "LONG")]
    [InlineData("Input.Code", "LONG")]
    [InlineData("Input.BlockReason", "LONG")]
    public async Task Invalid_edits_do_not_change_the_area(string field, string value)
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = await SignInAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var area = new Location { Code = "AREA-VALIDATION", Kind = LocationKind.Area, Description = "Original" };
        db.AddRange(area, new Location { Code = "DUPLICATE", Kind = LocationKind.Area });
        await db.SaveChangesAsync();
        var fields = Fields(await client.GetStringAsync($"{Editor}?locationId={area.Id}"));
        fields[field] = value == "LONG" ? new string('X', 201) : value;
        if (field == "Input.BlockReason") fields["Input.IsBlocked"] = "true";
        using var response = await client.PostAsync(Editor, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("validation", await response.Content.ReadAsStringAsync());
        await db.Entry(area).ReloadAsync();
        Assert.Equal("AREA-VALIDATION", area.Code);
        Assert.Equal("Original", area.Description);
        Assert.False(area.IsBlocked);
    }

    [Fact]
    public async Task Listing_paginates_areas_and_filters_description_and_state()
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        db.AddRange(Enumerable.Range(1, 27).Select(i => new Location
        {
            Code = $"PAGING-{i:00}",
            Kind = LocationKind.Area,
            Description = i == 27 ? "Inspección especial" : null,
            IsActive = i != 27,
            IsBlocked = i == 26
        }));
        db.Add(new Location { Code = "PAGING-RACK", Kind = LocationKind.Rack, RowCode = "Z", RackNumber = 1, PalletNumber = 1 });
        await db.SaveChangesAsync();
        var page = new WarehouseEPI.Web.Pages.Admin.Catalogs.Locations.AreasModel(db);
        await page.OnGetAsync("PAGING");
        Assert.Equal(25, page.Areas.Count);
        Assert.Equal(2, page.TotalPages);
        await page.OnGetAsync("PAGING", pageNumber: 99);
        Assert.Equal(2, page.Areas.Count);
        Assert.Contains(page.Areas, x => !x.IsActive);
        Assert.Contains(page.Areas, x => x.IsBlocked);
        await page.OnGetAsync("inspección", "inactive");
        Assert.Equal("PAGING-27", Assert.Single(page.Areas).Code);
    }

    [Fact]
    public async Task Process_selection_is_saved_and_stale_configuration_is_rejected()
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = await SignInAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var area = new Location { Code = "WIP-PROCESSES", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var stage = new ProductionStage { Code = "AREA-PROCESS", Name = "Proceso del área" };
        db.AddRange(area, stage);
        await db.SaveChangesAsync();
        var url = $"{Editor}?locationId={area.Id}";
        var fields = Fields(await client.GetStringAsync(url));
        fields["Input.ProcessIds"] = stage.Id.ToString();
        using var saved = await client.PostAsync(Editor, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.True(await db.ProductionProcessWipTargets.AnyAsync(x => x.LocationId == area.Id && x.ProductionStageId == stage.Id));
        fields = Fields(await client.GetStringAsync(url));
        fields["Input.ProcessIds"] = stage.Id.ToString();
        fields["Input.Description"] = "No guardar";
        var config = await db.ProductionProcessConfigurations.SingleAsync();
        config.Version++;
        await db.SaveChangesAsync();
        using var conflict = await client.PostAsync(Editor, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, conflict.StatusCode);
        var html = WebUtility.HtmlDecode(await conflict.Content.ReadAsStringAsync());
        Assert.Contains("La configuración de procesos cambió", html);
        Assert.Equal(fields["Input.ProcessConfigurationVersion"], Value(html, "Input.ProcessConfigurationVersion"));
        await db.Entry(area).ReloadAsync();
        Assert.Null(area.Description);
    }

    [Fact]
    public async Task Area_routes_require_admin_and_posts_require_antiforgery()
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var route in new[] { Listing, Editor })
        {
            using var response = await anonymous.GetAsync(route);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/Admin/Login", response.Headers.Location!.AbsolutePath);
        }
        using var admin = await SignInAsync(factory);
        using var invalid = await admin.PostAsync(Editor, new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Code"] = "NO-TOKEN" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static Dictionary<string, string> Fields(string html)
    {
        var fields = new Dictionary<string, string>();
        foreach (var name in new[] { "__RequestVerificationToken", "Input.Id", "Input.OriginalUpdatedAt", "Input.ProcessConfigurationVersion", "Input.Code", "Input.Description" })
            fields[name] = Value(html, name);
        fields["Input.IsActive"] = "true";
        fields["Input.OperationalRole"] = Regex.Match(html, "<option[^>]*selected=\"selected\"[^>]*>").Value.Contains("Wip", StringComparison.Ordinal) ? "Wip" : "Storage";
        return fields;
    }

    private static string Value(string html, string name)
    {
        var input = Regex.Match(html, "<input[^>]*name=\"" + Regex.Escape(name) + "\"[^>]*>").Value;
        return WebUtility.HtmlDecode(Regex.Match(input, "value=\"([^\"]*)\"").Groups[1].Value);
    }

    private static async Task CaptureAsync(string html, string name)
    {
        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_AREA_UI_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var sanitized = Regex.Replace(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", "");
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".html"), sanitized);
    }

    private static async Task<HttpClient> SignInAsync(AdminRouteTests.WarehouseApplicationFactory factory, string language = "es")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var role = await db.Roles.SingleAsync(x => x.Code == "ADMIN");
        var user = new User { FullName = "Area test admin", RoleId = role.Id, PinLookup = "", PinHash = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
        db.Add(user);
        await db.SaveChangesAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        var login = await client.GetStringAsync("/Admin/Login");
        using var response = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = "0123",
            ["__RequestVerificationToken"] = Value(login, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }
}
