using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Imports;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductImportRouteTests : IClassFixture<AdminRouteTests.WarehouseApplicationFactory>
{
    private readonly AdminRouteTests.WarehouseApplicationFactory factory;

    public ProductImportRouteTests(AdminRouteTests.WarehouseApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Operator_does_not_satisfy_admin_policy()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "OPERATOR")],
            "test"));

        var result = await authorization.AuthorizeAsync(principal, null, "AdminOnly");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Product_list_uses_page_number_query_without_route_binding_errors()
    {
        await EnsureAdminAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            if (!await db.Products.AnyAsync(product => product.Sku.StartsWith("PAGE-")))
            {
                db.Products.AddRange(Enumerable.Range(1, 60).Select(number => new Product
                {
                    Sku = $"PAGE-{number:000}",
                    Description = $"Producto de paginación {number}",
                    BaseUnitId = 1
                }));
                await db.SaveChangesAsync();
            }
        }

        using var client = await SignInAsync();
        var firstResponse = await client.GetAsync("/Admin/Catalogs/Products?status=all");
        var firstHtml = WebUtility.HtmlDecode(await firstResponse.Content.ReadAsStringAsync());
        var secondResponse = await client.GetAsync("/Admin/Catalogs/Products?status=all&pageNumber=2");
        var secondHtml = WebUtility.HtmlDecode(await secondResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Contains("pageNumber=2", firstHtml);
        Assert.DoesNotContain("is not valid", firstHtml);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Contains("2 de 2", secondHtml);
        Assert.Contains("PAGE-060", secondHtml);
        Assert.DoesNotContain("is not valid", secondHtml);
    }

    [Fact]
    public async Task Admin_can_preview_and_confirm_with_antiforgery_and_token_is_one_use()
    {
        await EnsureAdminAsync();
        using var client = await SignInAsync();
        var before = await ProductCountAsync();
        var importPage = await client.GetAsync("/Admin/Catalogs/Products/Import");
        var importHtml = await importPage.Content.ReadAsStringAsync();
        Assert.DoesNotContain("is not valid", WebUtility.HtmlDecode(importHtml));
        var uploadToken = Antiforgery(importHtml);

        using var workbook = Workbook("WEB-IMPORT-UNIQUE");
        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent(uploadToken), "__RequestVerificationToken");
        upload.Add(new ByteArrayContent(workbook.ToArray()), "Upload", "products.xlsx");
        var uploadResponse = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=Upload", upload);

        Assert.Equal(HttpStatusCode.Redirect, uploadResponse.StatusCode);
        Assert.NotNull(uploadResponse.Headers.Location);
        var previewResponse = await client.GetAsync(uploadResponse.Headers.Location);
        var previewHtml = await previewResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.Contains("WEB-IMPORT-UNIQUE", previewHtml);
        Assert.Contains("Productos nuevos", previewHtml);
        Assert.Equal(before, await ProductCountAsync());

        var previewToken = HiddenValue(previewHtml, "token");
        var confirmToken = Antiforgery(previewHtml);
        var confirmation = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=Confirm",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = previewToken,
                ["__RequestVerificationToken"] = confirmToken
            }));
        Assert.Equal(HttpStatusCode.Redirect, confirmation.StatusCode);
        Assert.Equal(before + 1, await ProductCountAsync());

        var reused = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=Confirm",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = previewToken,
                ["__RequestVerificationToken"] = confirmToken
            }));
        Assert.Equal(HttpStatusCode.Redirect, reused.StatusCode);
        Assert.Equal(before + 1, await ProductCountAsync());
    }

    [Fact]
    public async Task Upload_rejects_wrong_extension_and_missing_antiforgery()
    {
        await EnsureAdminAsync();
        using var client = await SignInAsync();
        using var workbook = Workbook("NOT-IMPORTED");

        using (var missingToken = new MultipartFormDataContent())
        {
            missingToken.Add(new ByteArrayContent(workbook.ToArray()), "Upload", "products.xlsx");
            var response = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=Upload", missingToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var page = await client.GetStringAsync("/Admin/Catalogs/Products/Import");
        using var wrongExtension = new MultipartFormDataContent();
        wrongExtension.Add(new StringContent(Antiforgery(page)), "__RequestVerificationToken");
        wrongExtension.Add(new ByteArrayContent(workbook.ToArray()), "Upload", "products.xlsm");
        var rejected = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=Upload", wrongExtension);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Contains("Solo se aceptan archivos con extensión .xlsx", WebUtility.HtmlDecode(body));
        Assert.DoesNotContain("NOT-IMPORTED", await ProductsAsync());

        var freshPage = await client.GetStringAsync("/Admin/Catalogs/Products/Import");
        using var oversized = new MultipartFormDataContent();
        oversized.Add(new StringContent(Antiforgery(freshPage)), "__RequestVerificationToken");
        oversized.Add(new ByteArrayContent(new byte[ProductImportLimits.MaxFileBytes + 1]), "Upload", "oversized.xlsx");
        var oversizedResponse = await client.PostAsync("/Admin/Catalogs/Products/Import?handler=Upload", oversized);
        Assert.Equal(HttpStatusCode.OK, oversizedResponse.StatusCode);
        Assert.Contains("El archivo no puede superar 10 MB", WebUtility.HtmlDecode(await oversizedResponse.Content.ReadAsStringAsync()));

        await using var scope = factory.Services.CreateAsyncScope();
        var formOptions = scope.ServiceProvider.GetRequiredService<IOptions<FormOptions>>().Value;
        Assert.True(formOptions.MemoryBufferThreshold >= ProductImportLimits.MaxRequestBytes);
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

    private static MemoryStream Workbook(string sku)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("ITEMS");
        sheet.Cell(1, 1).Value = "CLASS";
        sheet.Cell(1, 3).Value = "ITEM (Short)";
        sheet.Cell(1, 4).Value = "DESCRIPTION";
        sheet.Cell(1, 5).Value = "U/M";
        sheet.Cell(1, 12).Value = "COMPLETE PART #";
        sheet.Cell(2, 1).Value = "RM";
        sheet.Cell(2, 3).Value = sku;
        sheet.Cell(2, 4).Value = "Producto web";
        sheet.Cell(2, 5).Value = "Each (EA)";
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}
