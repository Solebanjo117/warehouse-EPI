using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class WarehouseMapArchitectureRenderTests
    : IClassFixture<WarehouseMapArchitectureRenderTests.WarehouseApplicationFactory>
{
    private readonly WarehouseApplicationFactory factory;

    public WarehouseMapArchitectureRenderTests(WarehouseApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Map_query_renders_architecture_labels_inside_real_svg_text_elements()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Locations?viewMode=map");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-map-architecture", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Preparar racks", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Editar croquis", html, StringComparison.Ordinal);
        Assert.DoesNotContain("method=\"post\"", html, StringComparison.OrdinalIgnoreCase);
        var publicLocationNavigation = LocationNavigation(html);
        Assert.Contains("href=\"/Locations\"", publicLocationNavigation, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\"", publicLocationNavigation, StringComparison.Ordinal);

        var textElement = Regex.Match(
            html,
            "<g class=\"map-architecture-element[^>]*data-kind=\"Text\"[\\s\\S]*?</g>");
        Assert.True(textElement.Success, "El croquis no rindió ningún elemento arquitectónico de tipo Text.");
        Assert.Contains("<text", textElement.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("<tspan", html, StringComparison.Ordinal);
        Assert.Contains("KPA / Breakroom", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_map_search_keeps_the_full_layout_and_marks_only_the_matching_pallet()
    {
        Guid matchingLocationId;
        Guid siblingLocationId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var locations = new[]
            {
                new Location { Code = "G-6-1", Kind = LocationKind.Rack, RowCode = "G", RackNumber = 6, PalletNumber = 1 },
                new Location { Code = "G-6-3", Kind = LocationKind.Rack, RowCode = "G", RackNumber = 6, PalletNumber = 3 },
                new Location { Code = "A-2-1", Kind = LocationKind.Rack, RowCode = "A", RackNumber = 2, PalletNumber = 1 }
            };
            foreach (var location in locations)
            {
                if (!await dbContext.Locations.AnyAsync(candidate => candidate.Code == location.Code))
                    dbContext.Locations.Add(location);
            }
            await dbContext.SaveChangesAsync();
            matchingLocationId = await dbContext.Locations.Where(location => location.Code == "G-6-3")
                .Select(location => location.Id).SingleAsync();
            siblingLocationId = await dbContext.Locations.Where(location => location.Code == "G-6-1")
                .Select(location => location.Id).SingleAsync();
        }

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/Locations?viewMode=map&search=G-6-3&status=active&kind=all");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"data-map-position=\"{matchingLocationId}\" data-map-position-match=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-map-position=\"{siblingLocationId}\" data-map-position-match=\"false\"", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "data-map-position-match=\"true\"").Cast<Match>());
        Assert.Contains("<text x=\"6\" y=\"18\">A-2</text>", html, StringComparison.Ordinal);
        Assert.Matches("<g class=\"map-element[^\"]*is-match[^\"]*\"[^>]*data-map-target=\"true\"[^>]*>[\\s\\S]*?<text x=\"6\" y=\"18\">G-6</text>", html);
    }

    [Fact]
    public async Task Public_location_detail_and_rack_sheet_render_without_admin_session()
    {
        Guid locationId;
        string rowCode;
        short rackNumber;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            if (!await dbContext.Locations.AnyAsync(item => item.Kind == LocationKind.Rack && item.IsPhysicallyPresent))
            {
                dbContext.Locations.Add(new Location
                {
                    Code = "Q-1-1",
                    Kind = LocationKind.Rack,
                    RowCode = "Q",
                    RackNumber = 1,
                    PalletNumber = 1
                });
                await dbContext.SaveChangesAsync();
            }
            var location = await dbContext.Locations.AsNoTracking()
                .Where(item => item.Kind == LocationKind.Rack && item.IsPhysicallyPresent)
                .OrderBy(item => item.Code)
                .FirstAsync();
            locationId = location.Id;
            rowCode = location.RowCode!;
            rackNumber = location.RackNumber!.Value;
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var detail = await client.GetAsync($"/Locations/{locationId}");
        var sheet = await client.GetAsync($"/Locations/Rack/Print?rowCode={rowCode}&rackNumber={rackNumber}");
        var detailHtml = await detail.Content.ReadAsStringAsync();
        var sheetHtml = await sheet.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sheet.StatusCode);
        Assert.Contains("Existencias actuales", detailHtml, StringComparison.Ordinal);
        Assert.Contains("Productos asignados", detailHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("method=\"post\"", detailHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Editar rack", detailHtml, StringComparison.Ordinal);
        Assert.Contains("Hoja de verificación de rack", sheetHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Location_administration_and_write_handlers_still_require_admin_session()
    {
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var get = await anonymous.GetAsync("/Admin/Catalogs/Locations");
        var post = await anonymous.PostAsync(
            "/Admin/Catalogs/Locations?handler=Toggle",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = Guid.NewGuid().ToString() }));

        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        Assert.Equal("/Admin/Login", get.Headers.Location?.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/Admin/Login", post.Headers.Location?.AbsolutePath);

        using var admin = await SignedInAdminAsync();
        var adminPage = await admin.GetAsync("/Admin/Catalogs/Locations?viewMode=map");
        var adminHtml = await adminPage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, adminPage.StatusCode);
        Assert.Contains("Administrar ubicaciones", adminHtml, StringComparison.Ordinal);
        Assert.Contains("Preparar racks", adminHtml, StringComparison.Ordinal);
        Assert.Contains("Editar croquis", adminHtml, StringComparison.Ordinal);
        var adminLocationNavigation = LocationNavigation(adminHtml);
        Assert.Contains("href=\"/Admin/Catalogs/Locations\"", adminLocationNavigation, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\"", adminLocationNavigation, StringComparison.Ordinal);
        Assert.DoesNotContain("<span>Administrar ubicaciones</span>", adminHtml, StringComparison.Ordinal);

        var publicPage = await admin.GetAsync("/Locations?viewMode=map");
        var publicHtml = await publicPage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, publicPage.StatusCode);
        Assert.DoesNotContain("Preparar racks", publicHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Editar croquis", publicHtml, StringComparison.Ordinal);
    }

    private static string LocationNavigation(string html)
    {
        var links = Regex.Matches(html, "<a[^>]*title=\"Ubicaciones\"[^>]*>");
        Assert.Single(links.Cast<Match>());
        return links[0].Value;
    }

    private async Task<HttpClient> SignedInAdminAsync()
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            if (!await dbContext.Users.AnyAsync(user => user.FullName == "Administrador de croquis"))
            {
                var role = await dbContext.Roles.SingleAsync(candidate => candidate.Code == "ADMIN");
                var user = new User
                {
                    FullName = "Administrador de croquis",
                    RoleId = role.Id,
                    PinLookup = string.Empty,
                    PinHash = string.Empty
                };
                var pinService = scope.ServiceProvider.GetRequiredService<UserPinService>();
                Assert.Equal(PinAssignmentResult.Success, await pinService.AssignAsync(user, "0123"));
                dbContext.Users.Add(user);
                await dbContext.SaveChangesAsync();
            }
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var loginPage = await client.GetAsync("/Admin/Login");
        var loginHtml = await loginPage.Content.ReadAsStringAsync();
        var tokenMatch = Regex.Match(
            loginHtml,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(tokenMatch.Success, "No se encontró el token antiforgery.");

        var login = await client.PostAsync(
            "/Admin/Login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Pin"] = "0123",
                ["ReturnUrl"] = string.Empty,
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(tokenMatch.Groups[1].Value)
            }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        return client;
    }

    public sealed class WarehouseApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"WarehouseMapRenderTests-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:PinLookupKey"] =
                        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
                    ["ConnectionStrings:Warehouse"] = "Host=unused;Database=unused",
                    ["AllowedHosts"] = "*"
                });
            });
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.RemoveAll<DbContextOptions<WarehouseDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<WarehouseDbContext>>();
                services.AddDbContext<WarehouseDbContext>(options =>
                    options.UseInMemoryDatabase(databaseName));

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                scope.ServiceProvider.GetRequiredService<WarehouseDbContext>()
                    .Database.EnsureCreated();
            });
        }
    }
}
