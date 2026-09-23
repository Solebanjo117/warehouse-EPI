using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Production;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionScheduleImportRouteTests
{
    private const string Page = "/Admin/Production/ScheduleImport";

    [Theory]
    [InlineData("es", "El cierre contiene pendientes vacíos o no numéricos.")]
    [InlineData("en", "The closing contains blank or non-numeric pending quantities.")]
    public async Task Opening_blockers_explain_causes_and_link_to_product_outside_current_page(string culture, string expected)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Guid draftId, targetId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var user = new User { FullName = "Review admin", RoleId = (await db.Roles.SingleAsync(x => x.Code == "ADMIN")).Id, PinLookup = "", PinHash = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.Add(user);
            var products = Enumerable.Range(0, 26).Select(i => new Product { Sku = $"AA-{i:00}", BaseUnitId = 1 }).ToArray();
            db.AddRange(products); await db.SaveChangesAsync();
            targetId = (await db.Products.SingleAsync(x => x.Sku == "FG-100")).Id;
            var bytes = ProductionOpeningImportTests.Bytes(w =>
            {
                var s = ProductionOpeningImportTests.Prior(w);
                s.Table("Plan3").Resize(s.Range(21, 3, 48, 9));
                // A closing without a readable pending keeps every product blocked; the AA products fill the first page.
                s.Table("PendingNextWeek").Resize(s.Range(168, 20, 169 + products.Length, 23));
                for (var i = 0; i < products.Length; i++)
                {
                    s.Cell(23 + i, 3).Value = "Monday"; s.Cell(23 + i, 4).Value = products[i].Sku;
                    s.Cell(23 + i, 5).Value = 10;
                    s.Cell(170 + i, 20).Value = products[i].Sku;
                    s.Cell(170 + i, 22).Value = 1; s.Cell(170 + i, 23).Value = 1;
                }
                s.Cell(169, 21).Clear(XLClearOptions.Contents);
            });
            draftId = await scope.ServiceProvider.GetRequiredService<ProductionImportDraftService>().CreateAsync("file.xlsx", bytes, user.Id);
        }
        var login = await client.GetStringAsync("/Admin/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Admin/Login", login, new() { ["Input.Pin"] = "0123" })).StatusCode);
        var preferences = await client.GetStringAsync(Page);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Preferences/Language", preferences,
            new() { ["language"] = culture, ["returnUrl"] = Page })).StatusCode);
        var html = await Html(await client.GetAsync(Page + "?PreviewToken=" + draftId));
        Assert.Contains(expected, html, StringComparison.Ordinal);
        Assert.DoesNotContain("fila 0", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("row 0", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"data-opening-product=\"{targetId}\"", html, StringComparison.Ordinal);
        var link = Regex.Match(html, "href=\"([^\"]*#product-" + targetId + ")\"").Groups[1].Value;
        Assert.NotEmpty(link);
        Assert.Contains("PreviewToken=" + draftId, link, StringComparison.Ordinal);
        var target = await Html(await client.GetAsync(link));
        Assert.Contains($"data-opening-product=\"{targetId}\"", target, StringComparison.Ordinal);
        Assert.Contains(expected, target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Importer_configures_links_fixes_rows_revalidates_and_confirms_with_audit()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var role = await db.Roles.SingleAsync(x => x.Code == "ADMIN");
            var user = new User { FullName = "Import admin", RoleId = role.Id, PinLookup = "", PinHash = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.AddRange(user, new Product { Sku = "FG-100", BaseUnitId = 1 },
                new ProductionStage { Code = "CUT", Name = "Cutting" }, new ProductionStage { Code = "SEW", Name = "Sewing" },
                new ProductionStage { Code = "RTP", Name = "Ready to Pack" },
                new ProductionShift { Code = "T1", Name = "Shift 1" }, new ProductionShift { Code = "T2", Name = "Shift 2" });
            await db.SaveChangesAsync();
            await ProductionDailyModuleTests.AddImportRouteAsync(db, (await db.Products.SingleAsync(x => x.Sku == "FG-100")).Id);
        }
        var login = await client.GetStringAsync("/Admin/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Admin/Login", login, new() { ["Input.Pin"] = "0123" })).StatusCode);
        using var workbook = ProductionDailyModuleTests.BuildWorkbook("FG-100");
        workbook.Worksheet("08-24 to 08-30").Cell(22, 10).Value = "Cuttin";
        workbook.Worksheet("09-21 to 09-27").Cell(22, 5).Clear(XLClearOptions.Contents);
        using var file = new MemoryStream(); workbook.SaveAs(file);

        var form = await client.GetStringAsync(Page);
        using var upload = new MultipartFormDataContent
        {
            { new StringContent(Input(form, "__RequestVerificationToken")), "__RequestVerificationToken" },
            { new ByteArrayContent(file.ToArray()), "Upload", "Production Schedule Report 2026.xlsx" }
        };
        var html = await Html(await client.PostAsync(Page + "?handler=Preview", upload));
        Assert.Contains("data-import-configuration", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        var token = Input(html, "PreviewToken");
        var configuration = new Dictionary<string, string>
        {
            ["PreviewToken"] = token,
            ["Configuration.OperationId"] = Input(html, "Configuration.OperationId"),
            ["Configuration.ExpectedVersion"] = Input(html, "Configuration.ExpectedVersion")
        };
        foreach (var field in new[] { "CuttingStageId", "SewingStageId", "ReadyToPackStageId", "Shift1Id", "Shift2Id" })
            configuration["Configuration." + field] = Regex.Match(Select(html, "Configuration." + field),
                "<option(?=[^>]*selected=\"selected\")[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        html = await Html(await Post(client, Page + "?handler=Configure", html, configuration));
        Assert.Contains("Configuración diaria guardada", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-import-configuration", html, StringComparison.Ordinal);
        Assert.Contains("Área sin resolver: Cuttin.", html, StringComparison.Ordinal);
        Assert.Contains("“Cuttin”", html, StringComparison.Ordinal);
        var rowKey = Input(html, "Resolution.Rows[0].Key");
        Assert.Equal("Plan|22|09-21 to 09-27", rowKey);
        Assert.Matches("name=\"__Invariant\"[^>]*value=\"Resolution\\.Rows\\[0\\]\\.Quantity\"", html);
        var resolve = new Dictionary<string, string>
        {
            ["PreviewToken"] = token,
            ["Resolution.Areas[0].Text"] = "Cuttin",
            ["Resolution.Areas[0].Value"] = "Cutting",
            ["Resolution.Rows[0].Key"] = rowKey,
            ["Resolution.Rows[0].Quantity"] = "40",
            ["__Invariant"] = "Resolution.Rows[0].Quantity"
        };

        html = await Html(await Post(client, Page + "?handler=Resolve", html, resolve));
        Assert.Contains("Lista para confirmar", html, StringComparison.Ordinal);
        Assert.Contains("Área \"Cuttin\" → Corte", html, StringComparison.Ordinal);
        Assert.Contains("09-21 to 09-27 · Programa fila 22: cantidad 40", html, StringComparison.Ordinal);

        html = await Html(await Post(client, Page + "?handler=Discard", html, new() { ["PreviewToken"] = token }));
        Assert.Contains("Requiere corrección", html, StringComparison.Ordinal);
        html = await Html(await Post(client, Page + "?handler=Resolve", html, resolve));
        Assert.Contains("Lista para confirmar", html, StringComparison.Ordinal);

        var confirmed = await Post(client, Page + "?handler=Confirm", html,
            new() { ["PreviewToken"] = token, ["OperationId"] = Input(html, "OperationId") });
        Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var batch = await db.ProductionScheduleImportBatches.SingleAsync();
            Assert.Contains("programa fila 22: cantidad 40", batch.ResolutionSummary, StringComparison.Ordinal);
            Assert.Contains("Área \"Cuttin\" → Corte", batch.ResolutionSummary, StringComparison.Ordinal);
            Assert.NotNull((await db.ProductionDailyConfigurations.SingleAsync()).Shift1Id);
        }

        var expired = await Html(await Post(client, Page + "?handler=Resolve", html, resolve));
        Assert.Contains("La revisión cambió o no está disponible", expired, StringComparison.Ordinal);
        Assert.Contains("Confirmado", expired, StringComparison.Ordinal);
        Assert.Contains("Ver programa creado", expired, StringComparison.Ordinal);
        Assert.Contains("WeekId=", expired, StringComparison.Ordinal);
        Assert.DoesNotContain("Requiere corrección", expired, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saved_drafts_list_deletes_unconfirmed_drafts_and_keeps_confirmed_imports()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Guid deleted, kept, confirmed;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var user = new User { FullName = "Draft admin", RoleId = (await db.Roles.SingleAsync(x => x.Code == "ADMIN")).Id, PinLookup = "", PinHash = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.Add(user); await db.SaveChangesAsync();
            var service = scope.ServiceProvider.GetRequiredService<ProductionImportDraftService>();
            var bytes = ProductionOpeningImportTests.Bytes();
            deleted = await service.CreateAsync("delete-me.xlsx", bytes, user.Id);
            kept = await service.CreateAsync("keep-me.xlsx", bytes, user.Id);
            confirmed = await service.CreateAsync("confirmed.xlsx", bytes, user.Id);
            (await db.ProductionImportDrafts.SingleAsync(x => x.Id == confirmed)).Status = ProductionImportDraftStatus.Confirmed;
            await db.SaveChangesAsync();
        }
        var login = await client.GetStringAsync("/Admin/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Admin/Login", login, new() { ["Input.Pin"] = "0123" })).StatusCode);

        var html = await Html(await client.GetAsync(Page + "?PreviewToken=" + kept));
        Assert.Equal(2, Regex.Count(html, "data-import-draft-delete"));
        Assert.Contains("data-confirm=\"¿Eliminar el borrador delete-me.xlsx?", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"name=\"draftId\" value=\"{confirmed}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);

        var response = await Post(client, Page + "?handler=DeleteDraft", html,
            new() { ["draftId"] = deleted.ToString(), ["PreviewToken"] = kept.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("PreviewToken=" + kept, response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        html = await Html(await client.GetAsync(response.Headers.Location));
        Assert.Contains("Borrador eliminado.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("delete-me.xlsx", html, StringComparison.Ordinal);
        Assert.Contains("keep-me.xlsx", html, StringComparison.Ordinal);

        response = await Post(client, Page + "?handler=DeleteDraft", html, new() { ["draftId"] = confirmed.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        html = await Html(await client.GetAsync(response.Headers.Location));
        Assert.Contains("Las importaciones confirmadas se conservan como registro y no se pueden eliminar.", html, StringComparison.Ordinal);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            Assert.Equal(new[] { confirmed, kept }.Order(), (await db.ProductionImportDrafts.Select(x => x.Id).ToListAsync()).Order());
            Assert.DoesNotContain(await db.ProductionImportRevisions.Select(x => x.DraftId).ToListAsync(), x => x == deleted);
        }
    }

    private static async Task<string> Html(HttpResponseMessage response)
    {
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return body;
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string url, string html, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = Input(html, "__RequestVerificationToken");
        fields["ExpectedRevision"] = Input(html, "ExpectedRevision");
        fields["Fingerprint"] = Input(html, "Fingerprint");
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    private static string Input(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html,
        "<input[^>]*name=\"" + Regex.Escape(name) + "\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private static string Select(string html, string name) =>
        Regex.Match(html, "<select[^>]*name=\"" + Regex.Escape(name) + "\"[^>]*>[\\s\\S]*?</select>").Value;
}
