using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductCatalogExportRouteTests
{
    [Theory]
    [InlineData("ADMIN")]
    [InlineData("OPERATOR")]
    [InlineData("PRODUCTION")]
    [InlineData(null)]
    public async Task Only_admin_can_download_and_list_filters_never_limit_the_file(string? role)
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        for (var i = 0; i < 35; i++) db.Add(new Product { Sku = $"CAT-{i:000}", BaseUnitId = 1, IsActive = i % 2 == 0 });
        if (role is not null)
        {
            var user = new User
            {
                FullName = "Export test",
                RoleId = (await db.Roles.SingleAsync(x => x.Code == role)).Id,
                PinHash = "",
                PinLookup = ""
            };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.Add(user);
        }
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (role is not null)
        {
            var login = await client.GetStringAsync("/Admin/Login");
            using var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = ProductionCaptureRecoveryTests.Input(login, "__RequestVerificationToken") }));
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        }
        const string page = "/Admin/Catalogs/Products";
        using var response = await client.GetAsync(page + "?handler=Export&status=active&search=CAT-001&pageNumber=2");
        if (role != "ADMIN")
        {
            Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Forbidden);
            Assert.NotEqual("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
            return;
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Productos.xlsx", response.Content.Headers.ContentDisposition!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var workbook = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(36, workbook.Worksheet(1).LastRowUsed()!.RowNumber());
        var html = await client.GetStringAsync(page);
        Assert.Contains("handler=Export", html);
        var output = Environment.GetEnvironmentVariable("WAREHOUSE_PRODUCT_EXPORT_FIXTURES");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "catalog.html"),
                Regex.Replace(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", ""));
            await File.WriteAllBytesAsync(Path.Combine(output, "Productos-prueba.xlsx"), await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(35, await db.Products.CountAsync());
    }
}
