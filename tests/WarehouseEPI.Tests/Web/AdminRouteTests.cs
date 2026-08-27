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

public sealed class AdminRouteTests : IClassFixture<AdminRouteTests.WarehouseApplicationFactory>
{
    private readonly WarehouseApplicationFactory factory;

    public AdminRouteTests(WarehouseApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Html_responses_include_security_headers_and_no_inline_script_handlers()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("upgrade-insecure-requests", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var correlations));
        Assert.True(Guid.TryParse(correlations.Single(), out _));
    }

    [Fact]
    public async Task Login_posts_are_rate_limited_by_remote_address_when_enabled()
    {
        using var limitedFactory = new WarehouseApplicationFactory();
        using var client = limitedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var page = await client.GetStringAsync("/Admin/Login");
            var token = Antiforgery(page);
            var response = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Pin"] = "9999",
                ["__RequestVerificationToken"] = token
            }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var blockedPage = await client.GetStringAsync("/Admin/Login");
        var blocked = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = "9999",
            ["__RequestVerificationToken"] = Antiforgery(blockedPage)
        }));

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
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
        var productTypes = await client.GetAsync("/Admin/Catalogs/ProductTypes");
        var productClasses = await client.GetAsync("/Admin/Catalogs/ProductClasses");
        var productImport = await client.GetAsync("/Admin/Catalogs/Products/Import");
        var locations = await client.GetAsync("/Admin/Catalogs/Locations");
        var locationGeneration = await client.GetAsync("/Admin/Catalogs/Locations/Generate");
        var locationDetails = await client.GetAsync($"/Admin/Catalogs/Locations/{Guid.NewGuid()}");
        var system = await client.GetAsync("/Admin/System");
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
        Assert.Equal(HttpStatusCode.Redirect, productTypes.StatusCode);
        Assert.Equal("/Admin/Login", productTypes.Headers.Location?.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, productClasses.StatusCode);
        Assert.Equal("/Admin/Login", productClasses.Headers.Location?.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, productImport.StatusCode);
        Assert.Equal("/Admin/Login", productImport.Headers.Location?.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, locations.StatusCode);
        Assert.Equal("/Admin/Login", locations.Headers.Location?.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, locationGeneration.StatusCode);
        Assert.Equal("/Admin/Login", locationGeneration.Headers.Location?.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, locationDetails.StatusCode);
        Assert.Equal("/Admin/Login", locationDetails.Headers.Location?.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, system.StatusCode);
        Assert.Equal("/Admin/Login", system.Headers.Location?.AbsolutePath);
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
        var productsHtml = await products.Content.ReadAsStringAsync();
        Assert.Contains("Crear producto", productsHtml);
        Assert.Contains("href=\"/Admin/Catalogs/ProductTypes\"", productsHtml);
        Assert.Contains("href=\"/Admin/Catalogs/ProductClasses\"", productsHtml);
        Assert.Contains("placeholder=\"SKU, descripción, referencia o ubicación\"", productsHtml);
        Assert.DoesNotContain("código(s)", productsHtml, StringComparison.OrdinalIgnoreCase);

        var productTypes = await client.GetAsync("/Admin/Catalogs/ProductTypes");
        var productTypesHtml = await productTypes.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, productTypes.StatusCode);
        Assert.Contains("Tipos registrados", productTypesHtml);
        Assert.Contains("aria-current=\"page\"", productTypesHtml);

        var productClasses = await client.GetAsync("/Admin/Catalogs/ProductClasses");
        var productClassesHtml = await productClasses.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, productClasses.StatusCode);
        Assert.Contains("Clases registradas", productClassesHtml);
        Assert.Contains("aria-current=\"page\"", productClassesHtml);

        var createPage = await client.GetAsync("/Admin/Catalogs/Products/Create");
        var createHtml = await createPage.Content.ReadAsStringAsync();
        var createToken = Regex.Match(
            createHtml,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(createToken.Success, "No se encontró el token antiforgery del producto.");
        Assert.Contains("Información del producto", createHtml);
        Assert.Contains("Configuración", createHtml);
        Assert.Contains("Guardar producto", createHtml);
        Assert.Contains("href=\"/Admin/Catalogs/Products\"", createHtml);
        Assert.Contains("name=\"Input.Sku\"", createHtml);
        Assert.Contains("name=\"Input.BaseUnitId\"", createHtml);
        Assert.DoesNotContain("Códigos de barras", createHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BarcodeInput", createHtml, StringComparison.Ordinal);

        var invalidCreateResponse = await client.PostAsync(
            "/Admin/Catalogs/Products/Create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Sku"] = string.Empty,
                ["Input.BaseUnitId"] = "0",
                ["Input.MinimumStock"] = "0",
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(createToken.Groups[1].Value)
            }));
        var invalidCreateHtml = await invalidCreateResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, invalidCreateResponse.StatusCode);
        Assert.Contains("role=\"alert\"", invalidCreateHtml);
        Assert.Contains("El SKU es obligatorio.", invalidCreateHtml);

        var createResponse = await client.PostAsync(
            "/Admin/Catalogs/Products/Create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Sku"] = "WEB-NO-DESCRIPTION",
                ["Input.BaseUnitId"] = "1",
                ["Input.MinimumStock"] = "0",
                ["Input.IsActive"] = "true",
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(createToken.Groups[1].Value)
            }));

        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var savedProduct = await verificationDb.Products.SingleAsync(product => product.Sku == "WEB-NO-DESCRIPTION");
        Assert.Null(savedProduct.Description);

        var editPage = await client.GetAsync($"/Admin/Catalogs/Products/Edit/{savedProduct.Id}");
        var editHtml = await editPage.Content.ReadAsStringAsync();
        var editToken = Regex.Match(
            editHtml,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.Equal(HttpStatusCode.OK, editPage.StatusCode);
        Assert.True(editToken.Success, "No se encontró el token antiforgery de edición del producto.");
        Assert.Contains("Guardar cambios", editHtml);
        Assert.Contains("Ver ficha", editHtml);
        Assert.Contains("Ubicaciones asignadas", editHtml);
        Assert.DoesNotContain("Códigos de barras", editHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BarcodeInput", editHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=AddBarcode", editHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=ToggleBarcode", editHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=SetPrimary", editHtml, StringComparison.Ordinal);
        Assert.Contains($"href=\"/Admin/Catalogs/Products/Details/{savedProduct.Id}\"", editHtml);

        var detailsPage = await client.GetAsync($"/Admin/Catalogs/Products/Details/{savedProduct.Id}");
        var detailsHtml = await detailsPage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, detailsPage.StatusCode);
        Assert.Contains("Lotes internos", detailsHtml);
        Assert.DoesNotContain("Códigos de barras", detailsHtml, StringComparison.OrdinalIgnoreCase);

        var editResponse = await client.PostAsync(
            $"/Admin/Catalogs/Products/Edit/{savedProduct.Id}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Id"] = savedProduct.Id.ToString(),
                ["Input.Sku"] = "WEB-EDITED",
                ["Input.Description"] = "Producto actualizado desde el formulario",
                ["Input.BaseUnitId"] = "1",
                ["Input.MinimumStock"] = "3.5",
                ["__Invariant"] = "Input.MinimumStock",
                ["Input.IsActive"] = "true",
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(editToken.Groups[1].Value)
            }));

        Assert.True(editResponse.StatusCode == HttpStatusCode.Redirect,
            $"Edición devolvió {editResponse.StatusCode}: {await editResponse.Content.ReadAsStringAsync()}");
        verificationDb.ChangeTracker.Clear();
        var editedProduct = await verificationDb.Products.SingleAsync(product => product.Id == savedProduct.Id);
        Assert.Equal("WEB-EDITED", editedProduct.Sku);
        Assert.Equal("Producto actualizado desde el formulario", editedProduct.Description);
        Assert.Equal(3.5m, editedProduct.MinimumStock);

        var locationsPage = await client.GetAsync("/Admin/Catalogs/Locations");
        Assert.Equal(HttpStatusCode.OK, locationsPage.StatusCode);
        Assert.Contains("Preparar racks", await locationsPage.Content.ReadAsStringAsync());

        var areaPage = await client.GetAsync("/Admin/Catalogs/Locations/Area");
        var areaHtml = await areaPage.Content.ReadAsStringAsync();
        var areaToken = Regex.Match(areaHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(areaToken.Success);
        var areaResponse = await client.PostAsync("/Admin/Catalogs/Locations/Area", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Code"] = " shipping-test ",
            ["Input.Description"] = "Área de embarque de prueba",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(areaToken.Groups[1].Value)
        }));
        Assert.Equal(HttpStatusCode.Redirect, areaResponse.StatusCode);
        Assert.Equal("SHIPPING-TEST", (await verificationDb.Locations.SingleAsync(location => location.Code == "SHIPPING-TEST")).Code);

        var generationPage = await client.GetAsync("/Admin/Catalogs/Locations/Generate");
        var generationHtml = await generationPage.Content.ReadAsStringAsync();
        var generationToken = Regex.Match(generationHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(generationToken.Success);
        var prepareResponse = await client.PostAsync("/Admin/Catalogs/Locations/Generate?handler=Prepare", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Manifest"] = "Z,1,1,1;5;9",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(generationToken.Groups[1].Value)
        }));
        Assert.Equal(HttpStatusCode.Redirect, prepareResponse.StatusCode);
        var previewResponse = await client.GetAsync(prepareResponse.Headers.Location);
        var previewHtml = await previewResponse.Content.ReadAsStringAsync();
        Assert.Contains("Z-1-9", previewHtml);
        Assert.False(await verificationDb.Locations.AnyAsync(location => location.Code == "Z-1-9"));

        var confirmToken = Regex.Match(previewHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(confirmToken.Success);
        var confirmResponse = await client.PostAsync(prepareResponse.Headers.Location!.OriginalString + "&handler=Confirm",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["SelectedCodes"] = "Z-1-9",
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(confirmToken.Groups[1].Value)
            }));
        Assert.Equal(HttpStatusCode.Redirect, confirmResponse.StatusCode);
        verificationDb.ChangeTracker.Clear();
        Assert.True(await verificationDb.Locations.AnyAsync(location => location.Code == "Z-1-9"));

        var generatedLocation = await verificationDb.Locations.SingleAsync(location => location.Code == "Z-1-9");
        var assignmentPage = await client.GetAsync($"/Admin/Catalogs/Products/Edit/{savedProduct.Id}?locationSearch=Z-1-9");
        var assignmentHtml = await assignmentPage.Content.ReadAsStringAsync();
        var assignmentToken = Regex.Match(assignmentHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(assignmentToken.Success);
        Assert.Contains("handler=AssignLocation", assignmentHtml);
        var assignmentResponse = await client.PostAsync(
            $"/Admin/Catalogs/Products/Edit/{savedProduct.Id}?handler=AssignLocation",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["locationId"] = generatedLocation.Id.ToString(),
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(assignmentToken.Groups[1].Value)
            }));
        Assert.Equal(HttpStatusCode.Redirect, assignmentResponse.StatusCode);
        verificationDb.ChangeTracker.Clear();
        Assert.True(await verificationDb.ProductLocationAssignments.AnyAsync(assignment =>
            assignment.ProductId == savedProduct.Id && assignment.LocationId == generatedLocation.Id && assignment.IsActive));

        var detailResponse = await client.GetAsync($"/Admin/Catalogs/Locations/{generatedLocation.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Contains("WEB-EDITED", await detailResponse.Content.ReadAsStringAsync());

        var locationSearch = await client.GetAsync("/Admin/Catalogs/Locations?status=all&search=WEB-EDITED");
        Assert.Equal(HttpStatusCode.OK, locationSearch.StatusCode);
        Assert.Contains("Z-1-9", await locationSearch.Content.ReadAsStringAsync());

        var productSearch = await client.GetAsync("/Admin/Catalogs/Products?search=WEB-EDITED");
        var productSearchBody = await productSearch.Content.ReadAsStringAsync();
        Assert.True(productSearch.StatusCode == HttpStatusCode.OK,
            $"Productos devolvió {productSearch.StatusCode}: {productSearchBody}");
        Assert.Contains("Z-1-9", productSearchBody);

        var productRackSearch = await client.GetAsync("/Admin/Catalogs/Products?search=Z-1-9");
        var productRackSearchBody = await productRackSearch.Content.ReadAsStringAsync();
        Assert.True(productRackSearch.StatusCode == HttpStatusCode.OK,
            $"Búsqueda por rack devolvió {productRackSearch.StatusCode}: {productRackSearchBody}");
        Assert.Contains("WEB-EDITED", productRackSearchBody);
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

    private static string Antiforgery(string html)
    {
        var tokenMatch = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(tokenMatch.Success, "No se encontró el token antiforgery.");
        return WebUtility.HtmlDecode(tokenMatch.Groups[1].Value);
    }
}
