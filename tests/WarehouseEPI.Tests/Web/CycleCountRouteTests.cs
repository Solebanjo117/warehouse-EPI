using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Pages.Operations.CycleCounts;

namespace WarehouseEPI.Tests.Web;

public sealed class CycleCountRouteTests
{
    [Fact]
    public async Task Empty_quantity_is_rejected_writes_nothing_and_keeps_the_capture()
    {
        await using var fixture = await CapturePage.CreateAsync("CC-PAGE-EMPTY", "Z-1-1", "4311");
        var page = fixture.NewPage();
        Assert.IsType<PageResult>(await page.OnGetAsync(fixture.CampaignId, fixture.CycleCountLocationId, null, default));
        var token = page.PreparationToken;

        var post = fixture.NewPage();
        post.Input = new()
        {
            PreparationToken = token,
            OperationId = Guid.NewGuid(),
            Pin = fixture.Pin,
            Entries = [new() { ProductId = fixture.ProductId, Quantity = null }]
        };
        var rejected = await post.OnPostAsync(fixture.CampaignId, fixture.CycleCountLocationId, default);

        Assert.IsType<PageResult>(rejected);
        Assert.Contains("CC-PAGE-EMPTY", post.Error, StringComparison.Ordinal);
        Assert.Contains(fixture.ProductId, post.MissingQuantityProductIds);
        Assert.Equal(token, post.PreparationToken);
        Assert.Empty(await fixture.Db.CycleCountAttempts.ToListAsync());
        Assert.Equal(CycleCountLocationStatus.Pending, (await fixture.Db.CycleCountLocations.SingleAsync()).Status);

        var resent = fixture.NewPage();
        resent.Input = new()
        {
            PreparationToken = token,
            OperationId = Guid.NewGuid(),
            Pin = fixture.Pin,
            Entries = [new() { ProductId = fixture.ProductId, Quantity = 6m }]
        };
        var accepted = await resent.OnPostAsync(fixture.CampaignId, fixture.CycleCountLocationId, default);

        Assert.Equal("Review", Assert.IsType<RedirectToPageResult>(accepted).PageName);
        Assert.Equal(CycleCountLocationStatus.Completed, (await fixture.Db.CycleCountLocations.SingleAsync()).Status);
    }

    [Fact]
    public async Task An_empty_location_may_be_confirmed_without_capturing_every_line()
    {
        await using var fixture = await CapturePage.CreateAsync("CC-PAGE-EMPTY-LOCATION", "Z-1-2", "4312");
        var page = fixture.NewPage();
        await page.OnGetAsync(fixture.CampaignId, fixture.CycleCountLocationId, null, default);

        var post = fixture.NewPage();
        post.Input = new()
        {
            PreparationToken = page.PreparationToken,
            OperationId = Guid.NewGuid(),
            Pin = fixture.Pin,
            IsLocationEmpty = true,
            Entries = [new() { ProductId = fixture.ProductId, Quantity = null }]
        };

        Assert.IsType<RedirectToPageResult>(await post.OnPostAsync(fixture.CampaignId, fixture.CycleCountLocationId, default));
        Assert.Equal(0m, (await fixture.Db.CycleCountEntries.SingleAsync()).CountedQuantity);
    }

    private sealed class CapturePage : IAsyncDisposable
    {
        private const string LookupKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        private readonly WarehouseEPI.Web.Security.CycleCountPreparationProtector protector = new(new EphemeralDataProtectionProvider());
        private CycleCountService cycleCounts = null!;
        public WarehouseDbContext Db { get; private set; } = null!;
        public string Pin { get; private set; } = string.Empty;
        public Guid CampaignId { get; private set; }
        public Guid CycleCountLocationId { get; private set; }
        public Guid ProductId { get; private set; }

        public static async Task<CapturePage> CreateAsync(string sku, string locationCode, string pin)
        {
            var fixture = new CapturePage();
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase($"CycleCountPage-{Guid.NewGuid():N}").Options);
            await db.Database.EnsureCreatedAsync();
            var pinService = new UserPinService(db, new PinProtector(LookupKey));
            var user = new User { FullName = "Contador", RoleId = 2, PinLookup = string.Empty, PinHash = string.Empty };
            Assert.Equal(PinAssignmentResult.Success, await pinService.AssignAsync(user, pin));
            db.Users.Add(user);
            var product = new Product { Sku = sku, Description = $"Producto {sku}", BaseUnitId = 1 };
            var location = new Location { Code = locationCode, Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage, RowCode = locationCode.Split('-')[0] };
            db.Products.Add(product);
            db.Locations.Add(location);
            await db.SaveChangesAsync();

            var movements = new InventoryMovementService(db, pinService, TimeProvider.System);
            Assert.Equal(InventoryMovementStatus.Success, (await movements.ConfirmAsync(
                new(Guid.NewGuid(), InventoryMovementType.Entry, pin, [new(product.Id, 6m, DestinationLocationId: location.Id)]))).Status);
            var cycleCounts = new CycleCountService(db, pinService, new InventoryQueryService(db), movements, TimeProvider.System);
            var created = await cycleCounts.CreateAsync(new(pin, $"Campaña {sku}", null, [location.Id], OperationId: Guid.NewGuid()));
            Assert.Equal(CycleCountStatus.Success, created.Status);
            Assert.Equal(CycleCountStatus.Success, (await cycleCounts.ReleaseAsync(created.CampaignId!.Value, Guid.NewGuid(), pin)).Status);
            var detail = await cycleCounts.GetCampaignAsync(created.CampaignId.Value);

            fixture.Db = db;
            fixture.Pin = pin;
            fixture.cycleCounts = cycleCounts;
            fixture.CampaignId = created.CampaignId.Value;
            fixture.CycleCountLocationId = Assert.Single(detail!.Locations).Id;
            fixture.ProductId = product.Id;
            return fixture;
        }

        public CountModel NewPage() => new(cycleCounts, Db, protector)
        {
            PageContext = new(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), new ModelStateDictionary()))
        };

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }


    [Fact]
    public void Cycle_count_pages_are_public_and_all_inventory_changes_require_pin()
    {
        var directory = RepositoryDirectory("src", "WarehouseEPI.Web", "Pages", "Operations", "CycleCounts");
        var pageModels = Directory.GetFiles(directory, "*.cshtml.cs").Select(File.ReadAllText).ToArray();
        var details = File.ReadAllText(Path.Combine(directory, "Details.cshtml"));
        var count = File.ReadAllText(Path.Combine(directory, "Count.cshtml"));
        var review = File.ReadAllText(Path.Combine(directory, "Review.cshtml"));

        Assert.All(pageModels, content => Assert.DoesNotContain("[Authorize", content, StringComparison.Ordinal));
        Assert.Contains("type=\"password\"", details, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", count, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", review, StringComparison.Ordinal);
        Assert.Contains("OperationId", count, StringComparison.Ordinal);
        Assert.Contains("OperationId", review, StringComparison.Ordinal);
        Assert.Contains("UnexpectedEntries", count, StringComparison.Ordinal);
        Assert.Contains("cycle-count.js", count, StringComparison.Ordinal);
        Assert.Contains("SharedApprovals", review, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-antiforgery=\"false\"", string.Join('\n', Directory.GetFiles(directory, "*.cshtml").Select(File.ReadAllText)), StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_draft_expires_never_stores_the_pin_and_rows_stay_bindable()
    {
        var script = File.ReadAllText(RepositoryFile("src", "WarehouseEPI.Web", "wwwroot", "js", "cycle-count.js"));
        var page = File.ReadAllText(Path.Combine(RepositoryDirectory("src", "WarehouseEPI.Web", "Pages", "Operations", "CycleCounts"), "Count.cshtml"));

        Assert.Contains("warehouseEpi.cycleCount.${capture.dataset.cycleCampaign}.${capture.dataset.cycleLocation}", script, StringComparison.Ordinal);
        Assert.Contains("draftLifetimeMs = 12 * 60 * 60 * 1000", script, StringComparison.Ordinal);
        Assert.Contains("element.type !== \"password\"", script, StringComparison.Ordinal);
        Assert.Contains("element.name !== \"Input.PreparationToken\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.Pin", script, StringComparison.Ordinal);
        // La restauración es explícita: nunca se rellena la captura sin que el operador lo pida.
        Assert.Contains("restoreButton.addEventListener", script, StringComparison.Ordinal);
        Assert.Contains("discardButton.addEventListener", script, StringComparison.Ordinal);
        Assert.Contains("`Input.UnexpectedEntries[${index}].${field}`", script, StringComparison.Ordinal);
        Assert.Contains("data-cycle-unexpected-row", page, StringComparison.Ordinal);
        Assert.Contains("data-cycle-campaign", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Counting_and_print_views_remain_blind_until_review()
    {
        var directory = RepositoryDirectory("src", "WarehouseEPI.Web", "Pages", "Operations", "CycleCounts");
        var count = File.ReadAllText(Path.Combine(directory, "Count.cshtml"));
        var print = File.ReadAllText(Path.Combine(directory, "Print.cshtml"));
        var review = File.ReadAllText(Path.Combine(directory, "Review.cshtml"));

        Assert.Contains("Conteo ciego", count, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpectedQuantity", count, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpectedQuantity", print, StringComparison.Ordinal);
        Assert.DoesNotContain("Difference", print, StringComparison.Ordinal);
        Assert.Contains("ExpectedQuantity", review, StringComparison.Ordinal);
        Assert.Contains("Difference", review, StringComparison.Ordinal);
        Assert.Contains("@@media print", print, StringComparison.Ordinal);
        var script = File.ReadAllText(RepositoryFile("src", "WarehouseEPI.Web", "wwwroot", "js", "cycle-count.js"));
        Assert.Contains("ZXingBrowser", script, StringComparison.Ordinal);
        Assert.Contains("capture", script, StringComparison.Ordinal);
        Assert.Contains("lector HID", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_scope_selection_and_exports_are_connected()
    {
        var directory = RepositoryDirectory("src", "WarehouseEPI.Web", "Pages", "Operations", "CycleCounts");
        var create = File.ReadAllText(Path.Combine(directory, "Create.cshtml"));
        var createModel = File.ReadAllText(Path.Combine(directory, "Create.cshtml.cs"));
        var details = File.ReadAllText(Path.Combine(directory, "Details.cshtml"));
        var index = File.ReadAllText(Path.Combine(directory, "Index.cshtml"));
        var createScript = File.ReadAllText(RepositoryFile("src", "WarehouseEPI.Web", "wwwroot", "js", "cycle-count-create.js"));
        var export = File.ReadAllText(Path.Combine(directory, "Export.cshtml.cs"));
        var layout = File.ReadAllText(RepositoryFile("src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml"));
        var analytics = File.ReadAllText(RepositoryFile("src", "WarehouseEPI.Web", "Pages", "Reports", "Inventory", "Index.cshtml"));

        Assert.Contains("name=\"Input.LocationIds\"", create, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.RowCodes\"", create, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.RackNumbers\"", create, StringComparison.Ordinal);
        Assert.Contains("Input.RowCodes", createModel, StringComparison.Ordinal);
        Assert.Contains("Input.RackNumbers", createModel, StringComparison.Ordinal);
        Assert.Contains("data-cycle-row-group", create, StringComparison.Ordinal);
        Assert.Contains("data-cycle-rack-group", create, StringComparison.Ordinal);
        Assert.Contains("data-cycle-location", create, StringComparison.Ordinal);
        Assert.Contains("data-cycle-summary-groups", create, StringComparison.Ordinal);
        Assert.Contains("cycle-count-create.js", create, StringComparison.Ordinal);
        Assert.Contains("indeterminate", createScript, StringComparison.Ordinal);
        Assert.Contains("item.dataset.row === toggle.dataset.row && item.dataset.rack === toggle.dataset.rack", createScript, StringComparison.Ordinal);
        Assert.DoesNotContain("item.OperationalRole != LocationOperationalRole.Wip", createModel, StringComparison.Ordinal);
        Assert.DoesNotContain("item.TracksInventory", createModel, StringComparison.Ordinal);
        Assert.Contains("CampaignStatusLabel", index, StringComparison.Ordinal);
        Assert.Contains("LocationStatusLabel", details, StringComparison.Ordinal);
        Assert.Contains("ActiveAttemptId", details, StringComparison.Ordinal);
        Assert.Contains("Continuar conteo", details, StringComparison.Ordinal);
        Assert.Contains("d-lg-none", index, StringComparison.Ordinal);
        Assert.Contains("name=\"from\"", index, StringComparison.Ordinal);
        Assert.Contains("name=\"to\"", index, StringComparison.Ordinal);
        Assert.Contains("10000", export, StringComparison.Ordinal);
        Assert.Contains("ExportCycleCountsToExcelAsync", export, StringComparison.Ordinal);
        Assert.Contains("ExportCycleCountsToCsvAsync", export, StringComparison.Ordinal);
        Assert.Contains("/Operations/CycleCounts/Index", layout, StringComparison.Ordinal);
        Assert.Contains("/Operations/CycleCounts/Index", analytics, StringComparison.Ordinal);
    }

    [Fact]
    public void Operational_statuses_are_presented_in_spanish()
    {
        Assert.Equal("Borrador", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.Draft));
        Assert.Equal("Lista para contar", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.Released));
        Assert.Equal("En conteo", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.InProgress));
        Assert.Equal("Requiere revisión", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.UnderReview));
        Assert.Equal("Completada", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.Completed));
        Assert.Equal("Cancelada", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.Cancelled));
        Assert.Equal("Reconteo solicitado", CycleCountPresentation.LocationStatusLabel(CycleCountLocationStatus.RecountRequested));
        Assert.Equal("Saldo cambió", CycleCountPresentation.LocationStatusLabel(CycleCountLocationStatus.Stale));
    }

    private static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }

    private static string RepositoryDirectory(params string[] parts) =>
        Path.GetDirectoryName(RepositoryFile([.. parts, "Index.cshtml"]))!;
}
