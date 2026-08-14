using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Imports;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Imports;

public sealed class ProductImportService(
    IProductSpreadsheetReader reader,
    WarehouseDbContext dbContext,
    ProductImportPreviewStore store)
{
    public async Task<ProductImportPreview> PrepareAsync(
        Stream stream,
        string fileName,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var read = reader.Read(stream);
        var units = await dbContext.Units.AsNoTracking().Where(unit => unit.IsActive)
            .ToDictionaryAsync(unit => unit.Code, unit => unit.Id, StringComparer.Ordinal, cancellationToken);
        var classes = await dbContext.ProductClasses.AsNoTracking().Where(productClass => productClass.IsActive)
            .ToDictionaryAsync(productClass => productClass.Code, productClass => productClass.Id, StringComparer.Ordinal, cancellationToken);
        var skus = read.Rows.Select(row => row.Sku).Distinct(StringComparer.Ordinal).ToList();
        var existing = skus.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await dbContext.Products.AsNoTracking().Where(product => skus.Contains(product.Sku))
                .Select(product => product.Sku).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var warningRows = read.Issues.Where(issue => !issue.IsError && issue.RowNumber is not null)
            .Select(issue => issue.RowNumber!.Value).ToHashSet();

        var rows = read.Rows.Select(row =>
        {
            var rowError = !units.ContainsKey(row.UnitCode) ||
                (row.ClassCode is not null && !classes.ContainsKey(row.ClassCode));
            var message = !units.ContainsKey(row.UnitCode)
                ? $"La unidad {row.UnitCode} no existe o está inactiva."
                : row.ClassCode is not null && !classes.ContainsKey(row.ClassCode)
                    ? $"La clase {row.ClassCode} no existe o está inactiva."
                    : row.ClassCode is null
                        ? "Se importará sin clase."
                        : string.Equals(row.UnitCode, WarehouseEPI.Core.CatalogDefaults.UnassignedUnitCode, StringComparison.Ordinal)
                            ? "U/M está vacía; se usará la unidad Sin asignar."
                        : null;
            return new ProductImportPreviewRow(row.SourceRows, row.Sku, row.Description, row.ExternalReference,
                row.UnitCode, row.ClassCode, existing.Contains(row.Sku), row.IsConsolidated,
                row.ClassCode is null || row.SourceRows.Any(warningRows.Contains), rowError, message);
        }).ToList();

        return store.Save(ownerUserId, Path.GetFileName(fileName), rows, read.Issues,
            read.SourceRowCount, read.ConsolidatedGroupCount, read.MissingExternalReferenceCount);
    }

    public bool TryGetPreview(string token, Guid ownerUserId, out ProductImportPreview? preview) =>
        store.TryGet(token, ownerUserId, out preview);

    public async Task<ProductImportConfirmation> ConfirmAsync(
        string token,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var importLock = await store.LockAsync(token, cancellationToken);
        if (!store.TryGet(token, ownerUserId, out var preview) || preview is null)
            return new(false, 0, 0, 0, "La vista previa expiró, ya fue utilizada o pertenece a otro administrador.");
        if (!preview.CanConfirm)
            return new(false, 0, 0, preview.ConsolidatedCount, "La vista previa contiene errores bloqueantes.");

        var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var units = await dbContext.Units.Where(unit => unit.IsActive)
                .ToDictionaryAsync(unit => unit.Code, unit => unit.Id, StringComparer.Ordinal, cancellationToken);
            var classes = await dbContext.ProductClasses.Where(productClass => productClass.IsActive)
                .ToDictionaryAsync(productClass => productClass.Code, productClass => productClass.Id, StringComparer.Ordinal, cancellationToken);
            var candidateSkus = preview.Rows.Where(row => row.IsCandidate).Select(row => row.Sku).ToList();
            var existing = candidateSkus.Count == 0
                ? new HashSet<string>(StringComparer.Ordinal)
                : (await dbContext.Products.AsNoTracking().Where(product => candidateSkus.Contains(product.Sku))
                    .Select(product => product.Sku).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

            var products = new List<Product>();
            foreach (var row in preview.Rows.Where(row => row.IsCandidate && !existing.Contains(row.Sku)))
            {
                if (!units.TryGetValue(row.UnitCode, out var unitId))
                    return await FailAsync(transaction, $"La unidad {row.UnitCode} ya no está disponible.", preview.ConsolidatedCount, cancellationToken);
                short? classId = null;
                if (row.ClassCode is not null)
                {
                    if (!classes.TryGetValue(row.ClassCode, out var resolvedClassId))
                        return await FailAsync(transaction, $"La clase {row.ClassCode} ya no está disponible.", preview.ConsolidatedCount, cancellationToken);
                    classId = resolvedClassId;
                }

                products.Add(new Product
                {
                    Sku = row.Sku,
                    Description = row.Description,
                    ExternalReference = row.ExternalReference,
                    BaseUnitId = unitId,
                    ProductClassId = classId,
                    ProductTypeId = null,
                    MinimumStock = 0m,
                    TracksLots = false,
                    TracksExpiration = false,
                    IsActive = true
                });
            }

            dbContext.Products.AddRange(products);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            store.Remove(token);
            return new(true, products.Count, preview.ExistingCount + existing.Count, preview.ConsolidatedCount);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return new(false, 0, 0, preview.ConsolidatedCount,
                "La base de datos rechazó la importación. No se insertó ningún producto.");
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private static async Task<ProductImportConfirmation> FailAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        string message,
        int consolidated,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
            await transaction.RollbackAsync(cancellationToken);
        return new(false, 0, 0, consolidated, message + " No se insertó ningún producto.");
    }
}
