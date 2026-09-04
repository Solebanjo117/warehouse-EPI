using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductDefaultEntryLocationTests
{
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
        var current = Assert.Single(options.EntryLocations);
        Assert.Equal(location.Id.ToString(), current.Value);
        Assert.Contains("no disponible", current.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static WarehouseDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
