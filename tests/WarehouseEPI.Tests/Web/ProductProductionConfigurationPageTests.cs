using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductProductionConfigurationPageTests
{
    [Theory]
    [InlineData("details", "Details", null)]
    [InlineData("production", "Edit", "product-production")]
    [InlineData("https://example.invalid", "Details", null)]
    public async Task Create_product_uses_only_the_allowed_post_save_destinations(
        string nextStep, string expectedPage, string? expectedFragment)
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = fixture.CreatePage();
        page.Input = new ProductInputModel { Sku = $"PT-{nextStep}", BaseUnitId = 1, IsActive = true };

        var result = Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(nextStep, CancellationToken.None));

        Assert.Equal(expectedPage, result.PageName);
        Assert.Equal(expectedFragment, result.Fragment);
        var expectedSku = $"PT-{nextStep}".ToUpperInvariant();
        Assert.Single(await fixture.Db.Products.Where(x => x.Sku == expectedSku).ToListAsync());
    }

    [Fact]
    public async Task Embedded_route_normalizes_stage_order_and_cannot_replace_an_active_route()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = fixture.EditPage();
        page.Route = new EditModel.RouteInputModel
        {
            Name = "Ruta integrada",
            Pin = Fixture.AdminPin,
            Stages =
            [
                new() { StageId = fixture.FirstStage.Id, Order = 20 },
                new() { StageId = fixture.SecondStage.Id, Order = 5 }
            ]
        };

        var result = Assert.IsType<RedirectToPageResult>(
            await page.OnPostRouteAsync(fixture.Product.Id, CancellationToken.None));
        Assert.Equal("product-production-route", result.Fragment);
        var route = await fixture.Db.ProductionRoutes.Include(x => x.Stages).SingleAsync();
        Assert.Equal([fixture.SecondStage.Id, fixture.FirstStage.Id],
            route.Stages.OrderBy(x => x.Sequence).Select(x => x.StageId));
        Assert.Equal([1, 2], route.Stages.OrderBy(x => x.Sequence).Select(x => x.Sequence));

        var replacement = fixture.EditPage();
        replacement.Route = new EditModel.RouteInputModel
        {
            Name = "Ruta reemplazo", Pin = Fixture.AdminPin,
            Stages = [new() { StageId = fixture.FirstStage.Id, Order = 1 }]
        };
        Assert.IsType<PageResult>(await replacement.OnPostRouteAsync(fixture.Product.Id, CancellationToken.None));
        Assert.Single(await fixture.Db.ProductionRoutes.ToListAsync());
    }

    [Fact]
    public async Task Route_post_validates_only_route_fields()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = fixture.EditPage();
        page.ModelState.AddModelError("Input.Sku", "El SKU es obligatorio.");
        page.ModelState.AddModelError("Input.BaseUnitId", "Seleccione una unidad base.");
        page.ModelState.AddModelError("Recipe.Pin", "The Pin field is required.");
        page.Route = new EditModel.RouteInputModel
        {
            Name = "Ruta aislada", Pin = Fixture.AdminPin,
            Stages = [new() { StageId = fixture.FirstStage.Id, Order = 1 }]
        };

        Assert.IsType<RedirectToPageResult>(
            await page.OnPostRouteAsync(fixture.Product.Id, CancellationToken.None));
        Assert.DoesNotContain(page.ModelState.Keys, key => key.StartsWith("Input.", StringComparison.Ordinal));
        Assert.DoesNotContain(page.ModelState.Keys, key => key.StartsWith("Recipe.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recipe_can_be_versioned_before_route_and_completed_afterwards()
    {
        await using var fixture = await Fixture.CreateAsync();
        var material = new Product { Sku = "MP-BORRADOR", BaseUnitId = 1 };
        fixture.Db.Products.Add(material);
        await fixture.Db.SaveChangesAsync();
        var first = fixture.EditPage();
        first.Recipe = new EditModel.RecipeInputModel
        {
            BaseQuantity = 10, Reason = "Lista inicial", Pin = Fixture.AdminPin,
            Lines = [new() { MaterialProductId = material.Id, MaterialSearch = material.Sku, Quantity = 2 }]
        };

        Assert.IsType<RedirectToPageResult>(
            await first.OnPostRecipeAsync(fixture.Product.Id, CancellationToken.None));
        var versionOne = await fixture.Db.ProductionRecipes.Include(x => x.Lines).SingleAsync();
        Assert.Null(Assert.Single(versionOne.Lines).StageId);

        var route = fixture.EditPage();
        route.Route = new EditModel.RouteInputModel
        {
            Name = "Ruta posterior", Pin = Fixture.AdminPin,
            Stages = [new() { StageId = fixture.FirstStage.Id, Order = 1 }]
        };
        Assert.IsType<RedirectToPageResult>(
            await route.OnPostRouteAsync(fixture.Product.Id, CancellationToken.None));

        var completed = fixture.EditPage();
        completed.Recipe = new EditModel.RecipeInputModel
        {
            BaseQuantity = 10, Reason = "Asignación de etapa", Pin = Fixture.AdminPin,
            Lines = [new() { MaterialProductId = material.Id, MaterialSearch = material.Sku,
                StageId = fixture.FirstStage.Id, Quantity = 2 }]
        };
        Assert.IsType<RedirectToPageResult>(
            await completed.OnPostRecipeAsync(fixture.Product.Id, CancellationToken.None));

        var versions = await fixture.Db.ProductionRecipes.Include(x => x.Lines).OrderBy(x => x.Version).ToListAsync();
        Assert.Equal([1, 2], versions.Select(x => x.Version));
        Assert.False(versions[0].IsActive);
        Assert.Null(Assert.Single(versions[0].Lines).StageId);
        Assert.Equal(fixture.FirstStage.Id, Assert.Single(versions[1].Lines).StageId);
    }

    [Fact]
    public async Task Recipe_editor_starts_with_one_line_and_does_not_pad_existing_or_failed_input()
    {
        await using var fixture = await Fixture.CreateAsync();
        var empty = fixture.EditPage();

        Assert.IsType<PageResult>(await empty.OnGetAsync(fixture.Product.Id, null, CancellationToken.None));
        Assert.Single(empty.Recipe.Lines);

        var firstMaterial = new Product { Sku = "MP-PROGRESIVO-1", BaseUnitId = 1 };
        var secondMaterial = new Product { Sku = "MP-PROGRESIVO-2", BaseUnitId = 1 };
        fixture.Db.AddRange(firstMaterial, secondMaterial);
        await fixture.Db.SaveChangesAsync();
        var save = fixture.EditPage();
        save.Recipe = new EditModel.RecipeInputModel
        {
            BaseQuantity = 5, Reason = "Dos materiales", Pin = Fixture.AdminPin,
            Lines =
            [
                new() { MaterialProductId = firstMaterial.Id, MaterialSearch = firstMaterial.Sku, Quantity = 1 },
                new() { MaterialProductId = secondMaterial.Id, MaterialSearch = secondMaterial.Sku, Quantity = 2 }
            ]
        };
        Assert.IsType<RedirectToPageResult>(
            await save.OnPostRecipeAsync(fixture.Product.Id, CancellationToken.None));

        var existing = fixture.EditPage();
        Assert.IsType<PageResult>(await existing.OnGetAsync(fixture.Product.Id, null, CancellationToken.None));
        Assert.Equal(2, existing.Recipe.Lines.Count);

        var failed = fixture.EditPage();
        failed.Recipe = new EditModel.RecipeInputModel
        {
            BaseQuantity = 5, Reason = "Error conservado", Pin = "0000",
            Lines =
            [
                new() { MaterialProductId = firstMaterial.Id, MaterialSearch = firstMaterial.Sku, Quantity = 1 },
                new() { MaterialProductId = secondMaterial.Id, MaterialSearch = secondMaterial.Sku, Quantity = 2 }
            ]
        };
        Assert.IsType<PageResult>(await failed.OnPostRecipeAsync(fixture.Product.Id, CancellationToken.None));
        Assert.Equal(2, failed.Recipe.Lines.Count);
        Assert.Equal(string.Empty, failed.Recipe.Pin);
    }

    [Fact]
    public async Task Embedded_route_rejects_duplicate_orders_invalid_pin_and_inactive_process()
    {
        await using var emptyFixture = await Fixture.CreateAsync();
        var empty = emptyFixture.EditPage();
        empty.Route = new EditModel.RouteInputModel
        {
            Name = "Ruta vacía", Pin = Fixture.AdminPin,
            Stages =
            [
                new() { StageId = emptyFixture.FirstStage.Id },
                new() { StageId = emptyFixture.SecondStage.Id }
            ]
        };
        Assert.IsType<PageResult>(await empty.OnPostRouteAsync(emptyFixture.Product.Id, CancellationToken.None));
        Assert.Contains(empty.ModelState.Values.SelectMany(x => x.Errors),
            x => x.ErrorMessage.Contains("al menos un proceso", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await emptyFixture.Db.ProductionRoutes.ToListAsync());

        await using var duplicateFixture = await Fixture.CreateAsync();
        var duplicate = duplicateFixture.EditPage();
        duplicate.Route = new EditModel.RouteInputModel
        {
            Name = "Ruta duplicada", Pin = Fixture.AdminPin,
            Stages =
            [
                new() { StageId = duplicateFixture.FirstStage.Id, Order = 1 },
                new() { StageId = duplicateFixture.SecondStage.Id, Order = 1 }
            ]
        };
        Assert.IsType<PageResult>(await duplicate.OnPostRouteAsync(duplicateFixture.Product.Id, CancellationToken.None));
        Assert.Contains(duplicate.ModelState.Values.SelectMany(x => x.Errors),
            x => x.ErrorMessage.Contains("repitas", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await duplicateFixture.Db.ProductionRoutes.ToListAsync());

        await using var invalidPinFixture = await Fixture.CreateAsync();
        var invalidPin = invalidPinFixture.EditPage();
        invalidPin.Route = new EditModel.RouteInputModel
        {
            Name = "Ruta sin autorización", Pin = "0000",
            Stages = [new() { StageId = invalidPinFixture.FirstStage.Id, Order = 1 }]
        };
        Assert.IsType<PageResult>(await invalidPin.OnPostRouteAsync(invalidPinFixture.Product.Id, CancellationToken.None));
        Assert.Empty(await invalidPinFixture.Db.ProductionRoutes.ToListAsync());

        await using var inactiveFixture = await Fixture.CreateAsync();
        inactiveFixture.FirstStage.IsActive = false;
        await inactiveFixture.Db.SaveChangesAsync();
        var inactive = inactiveFixture.EditPage();
        inactive.Route = new EditModel.RouteInputModel
        {
            Name = "Ruta con proceso inactivo", Pin = Fixture.AdminPin,
            Stages = [new() { StageId = inactiveFixture.FirstStage.Id, Order = 1 }]
        };
        Assert.IsType<PageResult>(await inactive.OnPostRouteAsync(inactiveFixture.Product.Id, CancellationToken.None));
        Assert.Empty(await inactiveFixture.Db.ProductionRoutes.ToListAsync());
    }

    [Fact]
    public async Task Assigned_locations_are_filtered_and_paginated_in_server()
    {
        await using var fixture = await Fixture.CreateAsync();
        var locations = Enumerable.Range(1, 29).Select(index => new Location
        {
            Code = $"PAG-{index:00}",
            Description = index == 29 ? "Destino especial" : "Ubicación",
            Kind = LocationKind.Area
        }).ToArray();
        fixture.Db.Locations.AddRange(locations);
        fixture.Db.ProductLocationAssignments.AddRange(locations.Select((location, index) =>
            new ProductLocationAssignment { ProductId = fixture.Product.Id, Location = location, IsActive = index < 27 }));
        await fixture.Db.SaveChangesAsync();

        var first = fixture.EditPage();
        Assert.IsType<PageResult>(await first.OnGetAsync(fixture.Product.Id, null, CancellationToken.None));
        Assert.Equal(25, first.LocationAssignments.Count);
        Assert.Equal(27, first.AssignmentTotalCount);
        Assert.Equal(2, first.AssignmentTotalPages);

        var second = fixture.EditPage();
        Assert.IsType<PageResult>(await second.OnGetAsync(fixture.Product.Id, null, CancellationToken.None,
            assignmentStatus: "active", assignmentPage: 2));
        Assert.Equal(2, second.LocationAssignments.Count);

        var inactive = fixture.EditPage();
        Assert.IsType<PageResult>(await inactive.OnGetAsync(fixture.Product.Id, null, CancellationToken.None,
            assignedLocationSearch: "especial", assignmentStatus: "inactive"));
        Assert.Single(inactive.LocationAssignments);
        Assert.False(inactive.LocationAssignments[0].IsActive);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        public WarehouseDbContext Db { get; }
        public Product Product { get; }
        public ProductionStage FirstStage { get; }
        public ProductionStage SecondStage { get; }
        public const string AdminPin = "4826";
        private readonly ProductionService production;
        private readonly ProductionTraceabilityService traceability;
        private readonly ProductionWipDefaultService wipDefaults;

        private Fixture(WarehouseDbContext db, Product product, ProductionStage firstStage,
            ProductionStage secondStage, ProductionService production, ProductionTraceabilityService traceability,
            ProductionWipDefaultService wipDefaults) =>
            (Db, Product, FirstStage, SecondStage, this.production, this.traceability,
                this.wipDefaults) =
            (db, product, firstStage, secondStage, production, traceability, wipDefaults);

        public static async Task<Fixture> CreateAsync()
        {
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            await db.Database.EnsureCreatedAsync();
            var pins = new UserPinService(db, new PinProtector(Key));
            var admin = new User { FullName = "Admin receta producto", RoleId = 1, PinLookup = "", PinHash = "" };
            await pins.AssignAsync(admin, "4826");
            var product = new Product { Sku = "PT-INTEGRADO", BaseUnitId = 1 };
            var first = new ProductionStage { Code = "P10", Name = "Preparación" };
            var second = new ProductionStage { Code = "P20", Name = "Ensamble" };
            db.AddRange(admin, product, first, second);
            await db.SaveChangesAsync();
            var movements = new InventoryMovementService(db, pins, TimeProvider.System);
            var production = new ProductionService(db, pins, movements, TimeProvider.System);
            var materials = new ProductionMaterialService(db, pins, movements, TimeProvider.System);
            var traceability = new ProductionTraceabilityService(db, pins, materials, TimeProvider.System);
            var wip = new ProductionWipDefaultService(db, pins, TimeProvider.System);
            return new(db, product, first, second, production, traceability, wip);
        }

        public CreateModel CreatePage() => Attach(new CreateModel(
            Db,
            wipDefaults,
            new PassthroughStringLocalizer<WarehouseEPI.Web.Localization.CatalogTexts>()));

        public EditModel EditPage() => Attach(new EditModel(Db, new ProductLocationAssignmentService(Db),
            traceability, wipDefaults, production,
            new PassthroughStringLocalizer<WarehouseEPI.Web.Localization.CatalogTexts>()));

        private static T Attach<T>(T page) where T : PageModel
        {
            var http = new DefaultHttpContext();
            page.PageContext = new PageContext { HttpContext = http };
            page.TempData = new TempDataDictionary(http, new EmptyTempDataProvider());
            return page;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
