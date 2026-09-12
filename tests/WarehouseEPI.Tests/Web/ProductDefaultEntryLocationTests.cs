using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductDefaultEntryLocationTests
{
    [Fact]
    public void Product_form_supports_keyboard_selection_and_exact_location_resolution()
    {
        var page = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "_ProductForm.cshtml"));
        var script = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "wwwroot", "js", "cycle-count.js"));

        Assert.Contains("type=\"hidden\" data-cycle-plan-id", page, StringComparison.Ordinal);
        Assert.Contains("role=\"combobox\"", page, StringComparison.Ordinal);
        Assert.Contains("role=\"listbox\"", page, StringComparison.Ordinal);
        Assert.Contains("data-cycle-plan-camera", page, StringComparison.Ordinal);
        Assert.Contains("data-cycle-plan-clear", page, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-items=\"Model.EntryLocations\"", page, StringComparison.Ordinal);
        Assert.Contains("field.type === \"product\" ? \"Products\" : \"Locations\"", script, StringComparison.Ordinal);
        Assert.Contains("handler: \"ResolveCode\"", script, StringComparison.Ordinal);
        Assert.Contains("window.setTimeout(() => void search(field), 250)", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"ArrowDown\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Escape\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Enter\"", script, StringComparison.Ordinal);
        Assert.Contains("resolveCode(field, field.input.value)", script, StringComparison.Ordinal);
        Assert.Contains("clearSelection(field, true)", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_change_and_clear_default_entry_location_preserves_assignments()
    {
        await using var db = CreateDbContext();
        db.Units.Add(new Unit { Id = 1, Code = "EA", Name = "Each" });
        var first = new Location { Code = "ENTRY-A", Kind = LocationKind.Area };
        var second = new Location { Code = "ENTRY-B", Kind = LocationKind.Area };
        db.Locations.AddRange(first, second);
        await db.SaveChangesAsync();

        var input = new ProductInputModel
        {
            Sku = "DEFAULT-ENTRY",
            BaseUnitId = 1,
            DefaultEntryLocationId = first.Id,
            IsActive = true
        };
        var state = new ModelStateDictionary();
        await ProductPageSupport.ValidateAsync(db, input, state, default);
        Assert.True(state.IsValid);
        var product = new Product { Sku = input.Sku };
        ProductPageSupport.Apply(product, input);
        db.Products.Add(product);
        await ProductPageSupport.EnsureDefaultEntryAssignmentAsync(db, product, default);
        await db.SaveChangesAsync();

        Assert.Equal(first.Id, product.DefaultEntryLocationId);
        Assert.True((await db.ProductLocationAssignments.FindAsync(product.Id, first.Id))!.IsActive);

        db.ProductLocationAssignments.Add(new ProductLocationAssignment
        {
            ProductId = product.Id,
            LocationId = second.Id,
            IsActive = false
        });
        await db.SaveChangesAsync();
        input.Id = product.Id;
        input.DefaultEntryLocationId = second.Id;
        state = new ModelStateDictionary();
        await ProductPageSupport.ValidateAsync(db, input, state, default);
        Assert.True(state.IsValid);
        ProductPageSupport.Apply(product, input);
        await ProductPageSupport.EnsureDefaultEntryAssignmentAsync(db, product, default);
        await db.SaveChangesAsync();

        Assert.Equal(second.Id, product.DefaultEntryLocationId);
        Assert.True((await db.ProductLocationAssignments.FindAsync(product.Id, first.Id))!.IsActive);
        Assert.True((await db.ProductLocationAssignments.FindAsync(product.Id, second.Id))!.IsActive);

        input.DefaultEntryLocationId = null;
        ProductPageSupport.Apply(product, input);
        await ProductPageSupport.EnsureDefaultEntryAssignmentAsync(db, product, default);
        await db.SaveChangesAsync();

        Assert.Null(product.DefaultEntryLocationId);
        Assert.Equal(2, await db.ProductLocationAssignments.CountAsync(item => item.ProductId == product.Id && item.IsActive));
    }

    [Fact]
    public async Task New_default_must_be_available_and_requires_an_active_product()
    {
        await using var db = CreateDbContext();
        db.Units.Add(new Unit { Id = 1, Code = "EA", Name = "Each" });
        var blocked = new Location
        {
            Code = "ENTRY-BLOCKED",
            Kind = LocationKind.Area,
            IsBlocked = true,
            BlockReason = "Conteo"
        };
        db.Locations.Add(blocked);
        await db.SaveChangesAsync();

        var blockedInput = new ProductInputModel
        {
            Sku = "BLOCKED-DEFAULT",
            BaseUnitId = 1,
            DefaultEntryLocationId = blocked.Id,
            IsActive = true
        };
        var blockedState = new ModelStateDictionary();
        await ProductPageSupport.ValidateAsync(db, blockedInput, blockedState, default);
        Assert.False(blockedState.IsValid);
        Assert.True(blockedState.ContainsKey("Input.DefaultEntryLocationId"));

        blockedInput.IsActive = false;
        var inactiveState = new ModelStateDictionary();
        await ProductPageSupport.ValidateAsync(db, blockedInput, inactiveState, default);
        Assert.False(inactiveState.IsValid);
        Assert.Contains(inactiveState["Input.DefaultEntryLocationId"]!.Errors,
            error => error.ErrorMessage.Contains("Active el producto", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Existing_unavailable_default_is_preserved_and_shown_in_options()
    {
        await using var db = CreateDbContext();
        db.Units.Add(new Unit { Id = 1, Code = "EA", Name = "Each" });
        var location = new Location { Code = "ENTRY-LATER-BLOCKED", Kind = LocationKind.Area };
        var product = new Product
        {
            Sku = "EXISTING-DEFAULT",
            BaseUnitId = 1,
            DefaultEntryLocation = location
        };
        db.AddRange(location, product, new ProductLocationAssignment { Product = product, Location = location });
        await db.SaveChangesAsync();
        location.IsBlocked = true;
        location.BlockReason = "Mantenimiento";
        await db.SaveChangesAsync();

        var input = new ProductInputModel
        {
            Id = product.Id,
            Sku = product.Sku,
            BaseUnitId = 1,
            DefaultEntryLocationId = location.Id,
            IsActive = true
        };
        var state = new ModelStateDictionary();
        await ProductPageSupport.ValidateAsync(db, input, state, default);
        var options = await ProductPageSupport.LoadOptionsAsync(db, input, default);

        Assert.True(state.IsValid);
        var current = Assert.IsType<ProductEntryLocationOption>(options.SelectedEntryLocation);
        Assert.Equal(location.Id, current.Id);
        Assert.Equal(location.Code, current.Code);
        Assert.False(current.IsAvailable);
    }

    private static WarehouseDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static string RepositoryPath(params string[] parts)
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
}
