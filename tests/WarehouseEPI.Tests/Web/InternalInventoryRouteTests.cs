using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Imports;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Imports;

namespace WarehouseEPI.Tests.Web;

public sealed class InternalInventoryRouteTests : IDisposable
{
    private readonly AdminRouteTests.WarehouseApplicationFactory rootFactory = new();
    private readonly WebApplicationFactory<Program> factory;
    private readonly Source source = new();

    public InternalInventoryRouteTests()
    {
        factory = rootFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IInternalInventoryClient>(source)));
    }
    public void Dispose() { factory.Dispose(); rootFactory.Dispose(); }

    [Fact]
    public async Task Preview_and_confirmation_update_existing_products_despite_tampered_mode()
    {
        await EnsureAdminAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            db.Products.Add(new Product { Sku = "OLD", Description = "Keep description", BaseUnitId = 1 });
            await db.SaveChangesAsync();
        }
        using var client = await SignInAsync();
        var before = await ProductCountAsync();
        var html = await client.GetStringAsync("/Admin/Catalogs/Products/Import");
        var response = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=InternalInventory", new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["inventoryPassword"] = "test-secret", ["UpdateExisting"] = "false", ["__RequestVerificationToken"] = Antiforgery(html)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var preview = await client.GetStringAsync(response.Headers.Location);
        Assert.Contains("NEW", preview);
        Assert.Contains("OLD", preview);
        Assert.Contains("Origen: Inventario interno", WebUtility.HtmlDecode(preview));
        Assert.Equal(before, await ProductCountAsync());
        Assert.DoesNotContain("test-secret", preview);
        Assert.Equal(1, source.Calls);
        var confirm = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=Confirm", new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["token"] = HiddenValue(preview, "token"), ["__RequestVerificationToken"] = Antiforgery(preview), ["UpdateExisting"] = "true"
        }));
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        Assert.Equal(before + 1, await ProductCountAsync());
        await using var verify = factory.Services.CreateAsyncScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Equal("Updated description", (await dbVerify.Products.SingleAsync(p => p.Sku == "OLD")).Description);
        var added = await dbVerify.Products.SingleAsync(p => p.Sku == "NEW");
        Assert.Null(added.ProductClassId);
        Assert.Equal("GROUP:NEW", added.ExternalReference);
        Assert.Equal(0, added.MinimumStock);
    }

    [Fact]
    public async Task Requires_admin_antiforgery_and_password_and_does_not_echo_secrets_on_error()
    {
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var denied = await anonymous.GetAsync("/Admin/Catalogs/Products/Import");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        await EnsureAdminAsync();
        using var client = await SignInAsync();
        var rejected = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=InternalInventory", new FormUrlEncodedContent(new Dictionary<string,string> { ["inventoryPassword"] = "test-secret" }));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(0, source.Calls);
        var html = await client.GetStringAsync("/Admin/Catalogs/Products/Import");
        var blank = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=InternalInventory", new FormUrlEncodedContent(new Dictionary<string,string> { ["__RequestVerificationToken"] = Antiforgery(html) }));
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
        Assert.Equal(0, source.Calls);
        source.Fail = true;
        var error = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=InternalInventory", new FormUrlEncodedContent(new Dictionary<string,string>
        { ["inventoryPassword"] = "test-secret", ["__RequestVerificationToken"] = Antiforgery(await blank.Content.ReadAsStringAsync()) }));
        var body = await error.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, error.StatusCode);
        Assert.Contains("incorrecta", body);
        Assert.DoesNotContain("test-secret", body);
        Assert.Contains("collapse show", body);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Unit_conflict_is_localized_in_preview_and_confirmation(string culture)
    {
        await EnsureAdminAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var product = new Product { Sku = "OLD", Description = "Before", BaseUnitId = 2 };
            db.Products.Add(product);
            db.InventoryMovementLines.Add(new InventoryMovementLine { ProductId = product.Id, UnitId = 2, Quantity = 1 });
            await db.SaveChangesAsync();
        }
        using var client = await SignInAsync();
        client.DefaultRequestHeaders.Add("Cookie", "WarehouseEPI.Language=" + culture);
        var html = await client.GetStringAsync("/Admin/Catalogs/Products/Import");
        var response = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=InternalInventory",
            new FormUrlEncodedContent(new Dictionary<string, string>
            { ["inventoryPassword"] = "test-secret", ["__RequestVerificationToken"] = Antiforgery(html) }));
        var preview = await client.GetStringAsync(response.Headers.Location);
        Assert.Contains(culture == "es" ? "U/M pendiente: Inventario interno indica EA; se conserva BX"
            : "Pending unit: internal inventory specifies EA; BX is preserved", WebUtility.HtmlDecode(preview));
        var confirm = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=Confirm",
            new FormUrlEncodedContent(new Dictionary<string, string>
            { ["token"] = HiddenValue(preview, "token"), ["__RequestVerificationToken"] = Antiforgery(preview) }));
        var completed = WebUtility.HtmlDecode(await client.GetStringAsync(confirm.Headers.Location));
        Assert.Contains(culture == "es" ? "1 conflictos de U/M pendientes; se conservaron las unidades locales."
            : "1 pending unit conflicts; local units were preserved.", completed);
        await using var verify = factory.Services.CreateAsyncScope();
        var saved = await verify.ServiceProvider.GetRequiredService<WarehouseDbContext>().Products.SingleAsync(x => x.Sku == "OLD");
        Assert.Equal("Updated description", saved.Description);
        Assert.Equal(2, saved.BaseUnitId);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Form_is_localized_and_can_export_sanitized_visual_fixture(string culture)
    {
        await EnsureAdminAsync();
        using var client = await SignInAsync();
        client.DefaultRequestHeaders.Add("Cookie", "WarehouseEPI.Language=" + culture);
        var html = await client.GetStringAsync("/Admin/Catalogs/Products/Import");
        Assert.Contains(culture == "es" ? "Desde Inventario interno" : "From internal inventory", WebUtility.HtmlDecode(html));
        Assert.Contains("name=\"inventoryPassword\" type=\"password\"", html);
        var output = Environment.GetEnvironmentVariable("WAREHOUSE_INTERNAL_INVENTORY_FIXTURES");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            html = Regex.Replace(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", "");
            await File.WriteAllTextAsync(Path.Combine(output, "import-" + culture + ".html"), html);
        }
    }

    private sealed class Source : IInternalInventoryClient
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<ProductSpreadsheetReadResult> ReadAsync(string password, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail) throw new InternalInventoryException(InternalInventoryError.Password);
            return Task.FromResult(InternalInventoryReader.Read(InternalInventoryReaderTests.Html(
                InternalInventoryReaderTests.Row("GROUP"), InternalInventoryReaderTests.Row("GROUP:NEW", "Each", "New product"),
                InternalInventoryReaderTests.Row("GROUP:OLD", "Each", "Updated description"))));
        }
    }

    private async Task EnsureAdminAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        if (await db.Users.AnyAsync(user => user.FullName == "Administrador importador"))
            return;
        var role = await db.Roles.SingleAsync(candidate => candidate.Code == "ADMIN");
        var user = new User { FullName = "Administrador importador", RoleId = role.Id, PinLookup = "", PinHash = "" };
        var pinService = scope.ServiceProvider.GetRequiredService<UserPinService>();
        Assert.Equal(PinAssignmentResult.Success, await pinService.AssignAsync(user, "9876"));
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    private async Task<HttpClient> SignInAsync()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var html = await client.GetStringAsync("/Admin/Login");
        var response = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = "9876",
            ["ReturnUrl"] = string.Empty,
            ["__RequestVerificationToken"] = Antiforgery(html)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private async Task<int> ProductCountAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().Products.CountAsync();
    }

    private async Task<string> ProductsAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return string.Join(',', await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().Products.Select(product => product.Sku).ToListAsync());
    }

    private static string Antiforgery(string html) => HiddenValue(html, "__RequestVerificationToken");

    private static string HiddenValue(string html, string name)
    {
        var match = Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"No se encontró el campo oculto {name}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

}
