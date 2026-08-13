using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
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

public sealed class AdminRouteTests : IClassFixture<AdminRouteTests.WarehouseApplicationFactory>
{
    private readonly WarehouseApplicationFactory factory;

    public AdminRouteTests(WarehouseApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Public_pages_render_and_users_require_admin_session()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var home = await client.GetAsync("/");
        var login = await client.GetAsync("/Admin/Login");
        var users = await client.GetAsync("/Admin/Users");
        var products = await client.GetAsync("/Admin/Catalogs/Products");
        var productImport = await client.GetAsync("/Admin/Catalogs/Products/Import");
        var loginBody = await login.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Warehouse EPI", await home.Content.ReadAsStringAsync());
        Assert.True(
            login.StatusCode == System.Net.HttpStatusCode.OK,
            $"Login devolvió {login.StatusCode}: {loginBody}");
        Assert.Contains("Acceso administrativo", loginBody);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, users.StatusCode);
        Assert.Equal("/Admin/Login", users.Headers.Location?.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, products.StatusCode);
        Assert.Equal("/Admin/Login", products.Headers.Location?.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, productImport.StatusCode);
        Assert.Equal("/Admin/Login", productImport.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task Admin_can_sign_in_with_pin_and_open_user_list()
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            if (!await dbContext.Users.AnyAsync(user => user.FullName == "Administrador de prueba"))
            {
                var role = await dbContext.Roles.SingleAsync(candidate => candidate.Code == "ADMIN");
                var user = new User
                {
                    FullName = "Administrador de prueba",
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

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
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

        var response = await client.PostAsync(
            "/Admin/Login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Pin"] = "0123",
                ["ReturnUrl"] = string.Empty,
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(tokenMatch.Groups[1].Value)
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Admin/Users", response.Headers.Location?.OriginalString);

        var users = await client.GetAsync("/Admin/Users");
        var usersHtml = await users.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        Assert.Contains("Administrador de prueba", usersHtml);

        var products = await client.GetAsync("/Admin/Catalogs/Products");
        Assert.Equal(HttpStatusCode.OK, products.StatusCode);
        Assert.Contains("Crear producto", await products.Content.ReadAsStringAsync());

        var createPage = await client.GetAsync("/Admin/Catalogs/Products/Create");
        var createHtml = await createPage.Content.ReadAsStringAsync();
        var createToken = Regex.Match(
            createHtml,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(createToken.Success, "No se encontró el token antiforgery del producto.");

        var createResponse = await client.PostAsync(
            "/Admin/Catalogs/Products/Create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Sku"] = "WEB-NO-DESCRIPTION",
                ["Input.BaseUnitId"] = "1",
                ["Input.MinimumStock"] = "0",
                ["Input.AllowsNegativeStock"] = "true",
                ["Input.IsActive"] = "true",
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(createToken.Groups[1].Value)
            }));

        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var savedProduct = await verificationDb.Products.SingleAsync(product => product.Sku == "WEB-NO-DESCRIPTION");
        Assert.Null(savedProduct.Description);
    }

    public sealed class WarehouseApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"WarehouseWebTests-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:PinLookupKey"] =
                        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
                    ["ConnectionStrings:Warehouse"] = "Host=unused;Database=unused"
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
