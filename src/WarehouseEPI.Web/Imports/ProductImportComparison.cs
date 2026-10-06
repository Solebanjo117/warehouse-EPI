using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Web.Imports;

public sealed partial class ProductImportService
{
    private static ProductImportValues Values(Product product) => new(product.Id, product.Description,
        product.ExternalReference, product.BaseUnitId, product.ProductClassId, product.UpdatedAt);

    private async Task<ProductImportPreview> PrepareComparisonAsync(WarehouseEPI.Infrastructure.Imports.ProductSpreadsheetReadResult read, string fileName, Guid owner, CancellationToken ct)
    {
        var units = await dbContext.Units.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var classes = await dbContext.ProductClasses.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var skus = read.Rows.Select(x => x.Sku).ToList();
        var products = await dbContext.Products.AsNoTracking().Where(x => skus.Contains(x.Sku)).ToDictionaryAsync(x => x.Sku, ct);
        var ids = products.Values.Select(x => x.Id).ToList();
        var moved = (await dbContext.InventoryMovementLines.Where(x => ids.Contains(x.ProductId))
            .Select(x => x.ProductId).Distinct().ToListAsync(ct)).ToHashSet();
        var rows = new List<ProductImportPreviewRow>();
        foreach (var row in read.Rows)
        {
            products.TryGetValue(row.Sku, out var product);
            var current = product is null ? null : Values(product);
            var unit = row.UnitWasBlank && product is not null ? units.GetValueOrDefault(product.BaseUnitId)
                : units.Values.SingleOrDefault(x => x.Code == row.UnitCode && x.IsActive);
            var productClass = row.ClassCode is null && product?.ProductClassId is { } classId
                ? classes.GetValueOrDefault(classId)
                : classes.Values.SingleOrDefault(x => x.Code == row.ClassCode && x.IsActive);
            var newClass = row.ClassCode is not null && !classes.Values.Any(x => x.Code == row.ClassCode);
            string? error = unit is null ? "La unidad no existe o está inactiva." : null;
            if (read.Issues.Any(issue => issue.Code == "invalid_unit" && issue.RowNumber is { } number && row.SourceRows.Contains(number)))
                error = "Selecciona la unidad correspondiente en Resolver unidades del Excel.";
            if (row.ClassCode is not null && productClass is null && !newClass)
                error = $"La clase {row.ClassCode} está inactiva.";
            if (row.ClassCode?.Length > 60)
                error = "El código de clase supera 60 caracteres.";
            if (product is not null && unit is not null && unit.Id != product.BaseUnitId && moved.Contains(product.Id))
                error = "No se puede cambiar la unidad base: el producto tiene movimientos.";
            var proposed = new ProductImportValues(product?.Id ?? Guid.Empty, row.Description ?? product?.Description,
                row.ExternalReference ?? product?.ExternalReference, unit?.Id ?? 0, productClass?.Id, product?.UpdatedAt ?? default);
            var changes = new List<ProductImportChange>();
            void Compare(string field, string? before, string? after)
            {
                if (before != after) changes.Add(new(field, before, after));
            }
            Compare("Descripción", product?.Description, proposed.Description);
            Compare("Referencia", product?.ExternalReference, proposed.Reference);
            Compare("Unidad", product is null ? null : units.GetValueOrDefault(product.BaseUnitId)?.Code, unit?.Code);
            Compare("Clase", product?.ProductClassId is { } oldClass ? classes.GetValueOrDefault(oldClass)?.Code : null, productClass?.Code ?? row.ClassCode);
            rows.Add(new(row.SourceRows, row.Sku, proposed.Description, proposed.Reference, unit?.Code ?? row.UnitCode,
                productClass?.Code ?? row.ClassCode, product is not null, row.IsConsolidated, row.UnitWasBlank || row.ClassCode is null || newClass,
                error is not null, error ?? (newClass ? $"Se creará la clase {row.ClassCode} al confirmar." : null))
            {
                Current = current,
                Proposed = proposed,
                Changes = changes,
                RequireActiveUnit = !row.UnitWasBlank || product is null,
                RequireActiveClass = row.ClassCode is not null,
                IsNewClass = newClass
            });
        }
        var issues = read.Issues.Select(issue => issue.Code is "missing_unit_defaulted" or "missing_class"
            ? issue with { Message = "Celda vacía: se conserva el valor existente. Los productos nuevos usan unidad Sin asignar y pueden quedar sin clase." }
            : issue).ToList();
        return store.Save(owner, Path.GetFileName(fileName), rows, issues, read.SourceRowCount,
            read.ConsolidatedGroupCount, read.MissingExternalReferenceCount, updateExisting: true, source: read,
            unitOptions: units.Values.Where(x => x.IsActive).OrderBy(x => x.Code).Select(x => new ProductImportUnitOption(x.Code, x.Name)).ToList());
    }

    private async Task<ProductImportConfirmation> ApplyComparisonAsync(ProductImportPreview preview, CancellationToken ct)
    {
        await using var transaction = dbContext.Database.IsRelational() ? await dbContext.Database.BeginTransactionAsync(ct) : null;
        try
        {
            // Serialize catalog edits and movement insertion while validating and writing the accepted snapshot.
            if (dbContext.Database.IsNpgsql())
                await dbContext.Database.ExecuteSqlRawAsync(
                    "LOCK TABLE products, inventory_movement_lines, units, product_classes IN SHARE ROW EXCLUSIVE MODE", ct);
            var accepted = preview.Rows.Where(x => !x.HasError).ToList();
            var skus = accepted.Select(x => x.Sku).ToList();
            var products = await dbContext.Products.Where(x => skus.Contains(x.Sku)).ToDictionaryAsync(x => x.Sku, ct);
            var units = await dbContext.Units.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            var classes = await dbContext.ProductClasses.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            var ids = products.Values.Select(x => x.Id).ToList();
            var moved = (await dbContext.InventoryMovementLines.Where(x => ids.Contains(x.ProductId))
                .Select(x => x.ProductId).Distinct().ToListAsync(ct)).ToHashSet();
            foreach (var row in accepted)
            {
                products.TryGetValue(row.Sku, out var product);
                var proposed = row.Proposed!;
                if ((product is null ? null : Values(product)) != row.Current ||
                    (row.IsNewClass && classes.Values.Any(x => x.Code == row.ClassCode)) ||
                    !units.TryGetValue(proposed.UnitId, out var unit) || unit.Code != row.UnitCode ||
                    (row.RequireActiveUnit && !unit.IsActive) ||
                    (proposed.ClassId is { } classId && (!classes.TryGetValue(classId, out var productClass) ||
                        productClass.Code != row.ClassCode || (row.RequireActiveClass && !productClass.IsActive))) ||
                    (product is not null && proposed.UnitId != product.BaseUnitId && moved.Contains(product.Id)))
                    return new(false, 0, 0, 0, "El catálogo cambió desde la comparación. Vuelve a analizar el archivo; no se aplicaron cambios.");
            }
            var newClasses = accepted.Where(x => x.IsNewClass && (x.IsCandidate || x.IsUpdate))
                .Select(x => x.ClassCode!).Distinct(StringComparer.Ordinal)
                .ToDictionary(code => code, code => new ProductClass { Code = code, Name = code, IsActive = true }, StringComparer.Ordinal);
            dbContext.ProductClasses.AddRange(newClasses.Values);
            foreach (var row in accepted.Where(x => x.IsCandidate || x.IsUpdate))
            {
                var proposed = row.Proposed!;
                if (!products.TryGetValue(row.Sku, out var product))
                {
                    product = new Product { Sku = row.Sku, BaseUnitId = proposed.UnitId, IsActive = true, MinimumStock = 0 };
                    dbContext.Products.Add(product);
                }
                product.Description = proposed.Description;
                product.ExternalReference = proposed.Reference;
                product.BaseUnitId = proposed.UnitId;
                product.ProductClassId = proposed.ClassId;
                if (row.IsNewClass) product.ProductClass = newClasses[row.ClassCode!];
            }
            await dbContext.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            store.Remove(preview.Token);
            return new(true, preview.NewCount, preview.UnchangedCount, preview.ConsolidatedCount) { Updated = preview.UpdatedCount };
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            dbContext.ChangeTracker.Clear();
            return new(false, 0, 0, 0, "La base de datos rechazó los cambios. No se aplicó ningún producto.");
        }
    }
}
