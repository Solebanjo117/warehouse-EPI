using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Imports;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Imports;
using WarehouseEPI.Web.Imports;
using WarehouseEPI.Web.Pages.Admin.Inventory;

namespace WarehouseEPI.Tests.Web;

public sealed class WipTransferImportRouteTests : IClassFixture<AdminRouteTests.WarehouseApplicationFactory>
{
    private readonly AdminRouteTests.WarehouseApplicationFactory factory;
    public WipTransferImportRouteTests(AdminRouteTests.WarehouseApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task Search_covers_all_pages_and_scopes_bulk_corrections_and_single_delivery_review()
    {
        await using var db = WipTransferImportTests.Db(); await db.Database.EnsureCreatedAsync();
        var origin = WipTransferImportTests.Rack("A-1-1");
        var alternative = WipTransferImportTests.Rack("A-1-2");
        var wip = new Location { Code = "WIP A", OperationalRole = LocationOperationalRole.Wip };
        db.Locations.AddRange(origin, alternative, wip);
        var product = new Product { Sku = "PART", BaseUnitId = 1 };
        db.Products.Add(product);
        db.InventoryBalances.AddRange(new InventoryBalance { ProductId = product.Id, LocationId = origin.Id, Quantity = 500m },
            new InventoryBalance { ProductId = product.Id, LocationId = alternative.Id, Quantity = 500m });
        await db.SaveChangesAsync();
        using var stream = WipTransferImportTests.Workbook(Enumerable.Range(1, 27)
            .Select(i => ("PART", (decimal?)i, "WIP A")).Append(("UNKNOWN", 99m, "WIP A")).ToArray());
        var file = WipTransferSpreadsheetReader.Read(stream, "report.xlsx");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var store = new WipTransferPreviewStore(cache, TimeProvider.System);
        var owner = Guid.NewGuid();
        var draft = store.Create(owner, file, file.Rows.ToDictionary(x => x.Number, _ => new WipTransferResolution(origin.Id, wip.Id)));
        var page = new WipImportModel(store, WipTransferImportTests.Service(db))
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner.ToString())], "test"))
                }
            }
        };
        await page.OnGetAsync(draft.Token, search: "PART", pageNumber: 2);
        Assert.Equal(27, page.Review!.Rows.Count);
        Assert.True(page.Review.CanConfirm); // Errors in UNKNOWN do not block PART.
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(2, page.TotalPages);
        Assert.All(page.Rows, row => Assert.Equal("PART", row.Source.Sku));
        Assert.Equal(-378m, page.Review.Effects.Single(x => x.Location == origin.Code).Change);
        await page.OnPostResolveAsync(draft.Token, 1, "all", null, null, null, alternative.Id, null, false, null, search: "PART");
        Assert.Equal(origin.Id, store.Get(draft.Token, owner)!.Resolutions[29].SourceId);
        Assert.All(store.Get(draft.Token, owner)!.Resolutions.Where(x => x.Key != 29), item => Assert.Equal(alternative.Id, item.Value.SourceId));
        await page.OnPostResolveAsync(draft.Token, 2, "row", 29, null, null, alternative.Id, null, false, null);
        Assert.Equal(2, store.Get(draft.Token, owner)!.Revision);
        Assert.Equal(origin.Id, store.Get(draft.Token, owner)!.Resolutions[29].SourceId);
        Assert.Contains("stock positivo", page.Error);
        await page.OnGetAsync(draft.Token, search: "PART", delivery: 5);
        Assert.Equal(5, Assert.Single(page.Rows).Source.Number);
        Assert.Equal(-4m, page.Review!.Effects.Single(x => x.Location == alternative.Code).Change);
        var empty = await page.OnPostConfirmAsync(draft.Token, 2, "1234", true, false, false, search: "PART", delivery: 29);
        Assert.IsType<RedirectToPageResult>(empty);
        Assert.Contains("No hay entregas", page.Error);
        await page.OnGetAsync(draft.Token, search: "PART", from: new DateOnly(2026, 9, 9));
        Assert.Empty(page.Review!.Rows);
        Assert.False(page.Review.CanConfirm);
        await page.OnPostResolveAsync(draft.Token, 2, "all", null, null, null, origin.Id, null, false, null,
            search: "PART", from: new DateOnly(2026, 9, 9));
        Assert.Equal(2, store.Get(draft.Token, owner)!.Revision);
        await page.OnPostConfirmAsync(draft.Token, 2, "1234", true, false, false,
            search: "PART", from: new DateOnly(2026, 9, 9));
        Assert.Contains("No hay entregas", page.Error);
        await page.OnGetAsync(draft.Token, from: new DateOnly(2026, 9, 9), to: new DateOnly(2026, 9, 8));
        Assert.Empty(page.Review!.Rows);
        Assert.Contains("Revisa las fechas", page.Error);
        page.ModelState.AddModelError("from", "Invalid date");
        await page.OnGetAsync(draft.Token);
        Assert.Empty(page.Review!.Rows);
        Assert.Empty(db.InventoryMovements);
    }

    [Fact]
    public async Task Bulk_and_individual_resolutions_preserve_other_fields_and_reject_stale_or_foreign_drafts()
    {
        await using var db = WipTransferImportTests.Db(); await db.Database.EnsureCreatedAsync();
        var origin = WipTransferImportTests.Rack("A-1-1");
        var alternative = WipTransferImportTests.Rack("A-1-2");
        var wip = new Location { Code = "WIP A", OperationalRole = LocationOperationalRole.Wip };
        db.Locations.AddRange(origin, alternative, wip);
        var product = new Product { Sku = "PART", BaseUnitId = 1 };
        db.Products.Add(product);
        db.InventoryBalances.AddRange(new InventoryBalance { ProductId = product.Id, LocationId = origin.Id, Quantity = 500m },
            new InventoryBalance { ProductId = product.Id, LocationId = alternative.Id, Quantity = 500m });
        await db.SaveChangesAsync();
        using var stream = WipTransferImportTests.Workbook(("PART", 4m, "Unknown"), ("PART", 3m, "Unknown"));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var store = new WipTransferPreviewStore(cache, TimeProvider.System);
        var owner = Guid.NewGuid();
        var draft = store.Create(owner, WipTransferSpreadsheetReader.Read(stream, "report.xlsx"), new Dictionary<int, WipTransferResolution>());
        WipImportModel Page(Guid actor) => new(store, WipTransferImportTests.Service(db))
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, "ADMIN")], "test"))
                }
            }
        };
        var page = Page(owner);
        await page.OnPostResolveAsync(draft.Token, 1, "area", null, null, "Unknown", null, wip.Id, false, null);
        Assert.All(store.Get(draft.Token, owner)!.Resolutions.Values, x => Assert.Equal(wip.Id, x.DestinationId));
        await page.OnPostResolveAsync(draft.Token, 2, "row", 2, null, null, alternative.Id, null, false, null);
        await page.OnPostResolveAsync(draft.Token, 3, "skuMissing", null, "PART", null, origin.Id, null, false, null);
        var revised = store.Get(draft.Token, owner)!;
        Assert.Equal(alternative.Id, revised.Resolutions[2].SourceId);
        Assert.Equal(origin.Id, revised.Resolutions[3].SourceId);
        Assert.All(revised.Resolutions.Values, x => Assert.Equal(wip.Id, x.DestinationId));
        await page.OnPostResolveAsync(draft.Token, 1, "all", null, null, null, alternative.Id, null, false, null);
        Assert.Equal(4, store.Get(draft.Token, owner)!.Revision);
        Assert.NotNull(page.Error);
        await Page(Guid.NewGuid()).OnPostResolveAsync(draft.Token, 4, "all", null, null, null, alternative.Id, null, false, null);
        Assert.Equal(4, store.Get(draft.Token, owner)!.Revision);
        Assert.Empty(db.InventoryMovements);
    }

    [Fact]
    public async Task Admin_preview_renders_single_source_and_multiple_choice_with_antiforgery()
    {
        Guid firstId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var pins = scope.ServiceProvider.GetRequiredService<UserPinService>();
            var admin = new User { FullName = "WIP web admin", RoleId = 1, PinLookup = "", PinHash = "" };
            Assert.Equal(PinAssignmentResult.Success, await pins.AssignAsync(admin, "7531"));
            db.Users.Add(admin);
            var first = WipTransferImportTests.Rack("IMP-A-1"); firstId = first.Id;
            var second = WipTransferImportTests.Rack("IMP-A-2");
            var single = new Product { Sku = "IMPORT-SINGLE", BaseUnitId = 1 };
            var multi = new Product { Sku = "IMPORT-MULTI", BaseUnitId = 1 };
            db.Products.AddRange(single, multi); db.Locations.AddRange(first, second,
                new Location { Code = "WIP A", OperationalRole = LocationOperationalRole.Wip });
            db.ProductLocationAssignments.AddRange(new ProductLocationAssignment { ProductId = single.Id, LocationId = first.Id },
                new ProductLocationAssignment { ProductId = multi.Id, LocationId = first.Id },
                new ProductLocationAssignment { ProductId = multi.Id, LocationId = second.Id });
            db.InventoryBalances.AddRange(new InventoryBalance { ProductId = single.Id, LocationId = first.Id, Quantity = 10m },
                new InventoryBalance { ProductId = multi.Id, LocationId = first.Id, Quantity = 10m },
                new InventoryBalance { ProductId = multi.Id, LocationId = second.Id, Quantity = 10m });
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Admin/Inventory/WipImport")).StatusCode);
        var login = await client.GetStringAsync("/Admin/Login");
        var signIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = "7531",
            ["ReturnUrl"] = "",
            ["__RequestVerificationToken"] = Hidden(login, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        var html = await client.GetStringAsync("/Admin/Inventory/WipImport");
        using var stream = WipTransferImportTests.Workbook(("IMPORT-SINGLE", 4m, "WIP A"), ("IMPORT-MULTI", 3m, "WIP A"));
        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent(Hidden(html, "__RequestVerificationToken")), "__RequestVerificationToken");
        upload.Add(new ByteArrayContent(stream.ToArray()), "upload", "report.xlsx");
        var response = await client.PostAsync("/Admin/Inventory/WipImport?handler=Upload", upload);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var preview = await client.GetStringAsync(response.Headers.Location);
        Assert.Contains("Origen prellenado", preview);
        Assert.Contains("Varias ubicaciones", preview);
        Assert.Contains("name=\"scope\" value=\"skuMissing\"", preview);
        var fields = new Dictionary<string, string>
        {
            ["token"] = Hidden(preview, "token"),
            ["revision"] = Hidden(preview, "revision"),
            ["scope"] = "skuMissing",
            ["sku"] = "IMPORT-MULTI",
            ["sourceId"] = firstId.ToString()
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Admin/Inventory/WipImport?handler=Resolve", new FormUrlEncodedContent(fields))).StatusCode);
        fields["__RequestVerificationToken"] = Hidden(preview, "__RequestVerificationToken");
        var resolved = await client.PostAsync("/Admin/Inventory/WipImport?handler=Resolve", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, resolved.StatusCode);
        var corrected = await client.GetStringAsync(resolved.Headers.Location);
        Assert.DoesNotContain("name=\"scope\" value=\"skuMissing\"", corrected);
        Assert.Contains("name=\"pin\"", corrected);
        var editHtml = await client.GetStringAsync($"/Admin/Inventory/WipImport?token={fields["token"]}&editRow=2");
        var sourceSelect = Regex.Match(editHtml, "<select[^>]*id=\"wip-row-source\"[^>]*>(.*?)</select>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("IMP-A-1", sourceSelect);
        Assert.DoesNotContain("IMP-A-2", sourceSelect);
        Assert.DoesNotContain("WIP A", sourceSelect);
        var filtered = WebUtility.HtmlDecode(await client.GetStringAsync($"/Admin/Inventory/WipImport?token={fields["token"]}&search=IMPORT-SINGLE"));
        Assert.Contains("Confirmar 1 entregas de esta selección", filtered);
        Assert.Contains("name=\"search\" value=\"IMPORT-SINGLE\"", filtered);
        Assert.Contains("delivery=2", filtered);
        Assert.DoesNotContain("Entrega · fila 3", filtered);
        await using var check = factory.Services.CreateAsyncScope();
        var checkDb = check.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.Empty(await checkDb.InventoryMovements.ToListAsync());
        stream.Position = 0;
        var importedFile = WipTransferSpreadsheetReader.Read(stream, "report.xlsx");
        var saved = new InventoryMovement
        {
            OperationId = importedFile.Rows[0].OperationId,
            RequestFingerprint = "saved-import-fixture",
            Type = InventoryMovementType.Exit,
            Purpose = InventoryMovementPurpose.ProductionIssue,
            ResponsibleUserId = (await checkDb.Users.SingleAsync(x => x.FullName == "WIP web admin")).Id,
            OperationalAreaId = (await checkDb.Locations.SingleAsync(x => x.Code == "WIP A")).Id,
            OccurredAt = new(2026, 9, 8, 5, 0, 0, TimeSpan.Zero),
            RecordedAt = DateTimeOffset.UtcNow,
            Lines = [new() { ProductId = (await checkDb.Products.SingleAsync(x => x.Sku == "IMPORT-SINGLE")).Id,
                UnitId = 1, SourceLocationId = firstId, Quantity = 4 }]
        };
        var document = new WipDocument
        {
            MovementLine = saved.Lines.Single(),
            ProductId = saved.Lines.Single().ProductId,
            WipLocationId = saved.OperationalAreaId.Value,
            ResponsibleUserId = saved.ResponsibleUserId,
            OccurredAt = saved.OccurredAt,
            Quantity = 4
        };
        checkDb.AddRange(saved, document); await checkDb.SaveChangesAsync();
        var reopened = WebUtility.HtmlDecode(await client.GetStringAsync($"/Admin/Inventory/WipImport?token={fields["token"]}&search=IMPORT-SINGLE"));
        Assert.Contains($"/Admin/Inventory/Movements/Details/{saved.Id}", reopened);
        Assert.Contains($"/Reports/Wip/Document?id={document.Id}", reopened);
        Assert.Contains("Capturado el", reopened);
        Assert.Contains("IMP-A-1 → WIP A", reopened);
        Assert.Contains("Ya importada; no se descuenta otra vez.", reopened);
        var notImported = WebUtility.HtmlDecode(await client.GetStringAsync($"/Admin/Inventory/WipImport?token={fields["token"]}&filter=notImported"));
        Assert.DoesNotContain("Entrega · fila 2", notImported);
        Assert.Contains("Entrega · fila 3", notImported);
        Assert.DoesNotContain("name=\"importMode\"", reopened);
        Assert.DoesNotContain("name=\"cycleReference\"", reopened);
    }

    private static string Hidden(string html, string name)
    {
        var match = Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
