using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

internal static class ProductPageSupport
{
    public static void Normalize(ProductInputModel input)
    {
        input.Sku = CatalogNormalization.NormalizeCode(input.Sku);
        input.Description = CatalogNormalization.NormalizeOptional(input.Description);
        input.ExternalReference = CatalogNormalization.NormalizeOptional(input.ExternalReference);
    }

    public static async Task ValidateAsync(WarehouseDbContext db, ProductInputModel input, ModelStateDictionary state, CancellationToken token)
    {
        Guid? currentDefaultEntryLocationId = null;
        if (input.Id != Guid.Empty)
        {
            var current = await db.Products.AsNoTracking()
                .Where(product => product.Id == input.Id)
                .Select(product => new { product.BaseUnitId, product.DefaultEntryLocationId })
                .SingleOrDefaultAsync(token);
            if (current is not null)
            {
                currentDefaultEntryLocationId = current.DefaultEntryLocationId;
                if (current.BaseUnitId != input.BaseUnitId && await db.InventoryMovementLines.AsNoTracking()
                        .AnyAsync(line => line.ProductId == input.Id, token))
                    state.AddModelError("Input.BaseUnitId", "No se puede cambiar la unidad base después de registrar movimientos.");
            }
        }

        if (await db.Products.AnyAsync(x => x.Sku == input.Sku && x.Id != input.Id, token))
            state.AddModelError("Input.Sku", "El SKU ya está asignado a otro producto, incluso si está inactivo.");

        var unit = await db.Units.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.BaseUnitId, token);
        if (unit is null || (input.IsActive && !unit.IsActive))
            state.AddModelError("Input.BaseUnitId", "Seleccione una unidad activa.");

        if (input.ProductTypeId.HasValue)
        {
            var valid = await db.ProductTypes.AnyAsync(x => x.Id == input.ProductTypeId && (!input.IsActive || x.IsActive), token);
            if (!valid) state.AddModelError("Input.ProductTypeId", "Seleccione un tipo activo.");
        }
        if (input.ProductClassId.HasValue)
        {
            var valid = await db.ProductClasses.AnyAsync(x => x.Id == input.ProductClassId && (!input.IsActive || x.IsActive), token);
            if (!valid) state.AddModelError("Input.ProductClassId", "Seleccione una clase activa.");
        }

        if (input.DefaultEntryLocationId.HasValue && input.DefaultEntryLocationId != currentDefaultEntryLocationId)
        {
            if (!input.IsActive)
            {
                state.AddModelError("Input.DefaultEntryLocationId", "Active el producto antes de asignar una ubicación principal de entrada.");
            }
            else
            {
                var valid = await db.Locations.AsNoTracking().AnyAsync(location =>
                    location.Id == input.DefaultEntryLocationId && location.IsPhysicallyPresent &&
                    location.IsActive && !location.IsBlocked, token);
                if (!valid)
                    state.AddModelError("Input.DefaultEntryLocationId", "Seleccione una ubicación física activa y no bloqueada.");
            }
        }
    }

    public static void Apply(Product product, ProductInputModel input)
    {
        product.Sku = input.Sku;
        product.Description = input.Description;
        product.ExternalReference = input.ExternalReference;
        product.ProductTypeId = input.ProductTypeId;
        product.ProductClassId = input.ProductClassId;
        product.BaseUnitId = input.BaseUnitId;
        product.MinimumStock = input.MinimumStock;
        product.DefaultEntryLocationId = input.DefaultEntryLocationId;
        product.IsActive = input.IsActive;
        product.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static async Task EnsureDefaultEntryAssignmentAsync(
        WarehouseDbContext db, Product product, CancellationToken token)
    {
        if (!product.DefaultEntryLocationId.HasValue) return;

        var locationId = product.DefaultEntryLocationId.Value;
        var assignment = await db.ProductLocationAssignments.SingleOrDefaultAsync(candidate =>
            candidate.ProductId == product.Id && candidate.LocationId == locationId, token);
        if (assignment is null)
        {
            db.ProductLocationAssignments.Add(new ProductLocationAssignment
            {
                ProductId = product.Id,
                LocationId = locationId
            });
        }
        else if (!assignment.IsActive)
        {
            assignment.IsActive = true;
            assignment.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public static IQueryable<Product> ApplySearch(IQueryable<Product> query, string search)
    {
        var term = search.Trim().ToUpperInvariant();
        return query.Where(product =>
            product.Sku.ToUpper().Contains(term) ||
            (product.Description != null && product.Description.ToUpper().Contains(term)) ||
            (product.ExternalReference != null && product.ExternalReference.ToUpper().Contains(term)) ||
            product.Barcodes.Any(barcode => barcode.Barcode.ToUpper().Contains(term)) ||
            product.LocationAssignments.Any(assignment =>
                assignment.IsActive && assignment.Location.Code.ToUpper().Contains(term)));
    }

    public static async Task<(IReadOnlyList<SelectListItem> Units, IReadOnlyList<SelectListItem> Types, IReadOnlyList<SelectListItem> Classes, ProductEntryLocationOption? SelectedEntryLocation)> LoadOptionsAsync(
        WarehouseDbContext db, ProductInputModel input, CancellationToken token)
    {
        var units = await db.Units.AsNoTracking().Where(x => x.IsActive || x.Id == input.BaseUnitId).OrderBy(x => x.Code)
            .Select(x => new SelectListItem($"{x.Code} - {x.Name}", x.Id.ToString())).ToListAsync(token);
        var types = await db.ProductTypes.AsNoTracking().Where(x => x.IsActive || x.Id == input.ProductTypeId).OrderBy(x => x.Code)
            .Select(x => new SelectListItem($"{x.Code} - {x.Name}", x.Id.ToString())).ToListAsync(token);
        var classes = await db.ProductClasses.AsNoTracking().Where(x => x.IsActive || x.Id == input.ProductClassId).OrderBy(x => x.Code)
            .Select(x => new SelectListItem($"{x.Code} - {x.Name}", x.Id.ToString())).ToListAsync(token);
        var selectedEntryLocation = input.DefaultEntryLocationId.HasValue
            ? await db.Locations.AsNoTracking()
                .Where(location => location.Id == input.DefaultEntryLocationId)
                .Select(location => new ProductEntryLocationOption(
                    location.Id,
                    location.Code,
                    location.Description,
                    location.IsPhysicallyPresent && location.IsActive && !location.IsBlocked))
                .SingleOrDefaultAsync(token)
            : null;
        return (units, types, classes, selectedEntryLocation);
    }

    public static async Task<(IReadOnlyList<SelectListItem> Units, IReadOnlyList<SelectListItem> Types, IReadOnlyList<SelectListItem> Classes)> LoadFilterOptionsAsync(
        WarehouseDbContext db, short? unitId, short? typeId, short? classId, CancellationToken token)
    {
        var units = await db.Units.AsNoTracking().Where(x => x.IsActive || x.Id == unitId).OrderBy(x => x.Code)
            .Select(x => new SelectListItem($"{x.Code} - {x.Name}", x.Id.ToString())).ToListAsync(token);
        var types = await db.ProductTypes.AsNoTracking().Where(x => x.IsActive || x.Id == typeId).OrderBy(x => x.Code)
            .Select(x => new SelectListItem($"{x.Code} - {x.Name}", x.Id.ToString())).ToListAsync(token);
        var classes = await db.ProductClasses.AsNoTracking().Where(x => x.IsActive || x.Id == classId).OrderBy(x => x.Code)
            .Select(x => new SelectListItem($"{x.Code} - {x.Name}", x.Id.ToString())).ToListAsync(token);
        return (units, types, classes);
    }
}
