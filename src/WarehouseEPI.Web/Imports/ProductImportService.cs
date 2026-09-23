using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Imports;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Imports;

public sealed partial class ProductImportService(
    IProductSpreadsheetReader reader,
    WarehouseDbContext dbContext,
    ProductImportPreviewStore store)
{
    public Task<ProductImportPreview> PrepareAsync(Stream stream, string fileName, Guid ownerUserId,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(stream, fileName, ownerUserId, false, cancellationToken);

    public async Task<ProductImportPreview> PrepareAsync(
        Stream stream,
        string fileName,
        Guid ownerUserId,
        bool updateExisting,
        CancellationToken cancellationToken = default)
    {
        return await PrepareReadAsync(reader.Read(stream), fileName, ownerUserId, updateExisting, cancellationToken);
    }

    private async Task<ProductImportPreview> PrepareReadAsync(ProductSpreadsheetReadResult read, string fileName,
        Guid ownerUserId, bool updateExisting, CancellationToken cancellationToken)
    {
        if (updateExisting)
            return await PrepareComparisonAsync(read, fileName, ownerUserId, cancellationToken);
        var unitOptions = await dbContext.Units.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Code)
            .Select(x => new ProductImportUnitOption(x.Code, x.Name)).ToListAsync(cancellationToken);
        var units = await dbContext.Units.AsNoTracking().Where(unit => unit.IsActive)
            .ToDictionaryAsync(unit => unit.Code, unit => unit.Id, StringComparer.Ordinal, cancellationToken);
        var classes = await dbContext.ProductClasses.AsNoTracking()
            .ToDictionaryAsync(productClass => productClass.Code, StringComparer.Ordinal, cancellationToken);
        var skus = read.Rows.Select(row => row.Sku).Distinct(StringComparer.Ordinal).ToList();
        var existing = skus.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await dbContext.Products.AsNoTracking().Where(product => skus.Contains(product.Sku))
                .Select(product => product.Sku).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var warningRows = read.Issues.Where(issue => !issue.IsError && issue.RowNumber is not null)
            .Select(issue => issue.RowNumber!.Value).ToHashSet();

        var rows = read.Rows.Select(row =>
        {
            var newClass = row.ClassCode is not null && !classes.ContainsKey(row.ClassCode) && !existing.Contains(row.Sku);
            var classError = row.ClassCode is not null &&
                (row.ClassCode.Length > 60 || (classes.TryGetValue(row.ClassCode, out var knownClass) && !knownClass.IsActive));
            var rowError = !units.ContainsKey(row.UnitCode) || classError;
            var message = !units.ContainsKey(row.UnitCode)
                ? $"La unidad {row.UnitCode} no existe o está inactiva."
                : classError
                    ? "La clase está inactiva o su código supera 60 caracteres."
                    : newClass ? $"Se creará la clase {row.ClassCode} al confirmar."
                    : row.ClassCode is null
                        ? "Se importará sin clase."
                        : string.Equals(row.UnitCode, WarehouseEPI.Core.CatalogDefaults.UnassignedUnitCode, StringComparison.Ordinal)
                            ? "U/M está vacía; se usará la unidad Sin asignar."
                        : null;
            return new ProductImportPreviewRow(row.SourceRows, row.Sku, row.Description, row.ExternalReference,
                row.UnitCode, row.ClassCode, existing.Contains(row.Sku), row.IsConsolidated,
                newClass || row.ClassCode is null || row.SourceRows.Any(warningRows.Contains), rowError, message) { IsNewClass = newClass };
        }).ToList();

        return store.Save(ownerUserId, Path.GetFileName(fileName), rows, read.Issues,
            read.SourceRowCount, read.ConsolidatedGroupCount, read.MissingExternalReferenceCount, source: read, unitOptions: unitOptions);
    }

    public async Task<ProductImportPreview?> ResolveUnitAsync(string token, Guid owner, string sourceUnit, string targetUnit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(sourceUnit) || string.IsNullOrWhiteSpace(targetUnit))
            return null;
        await using var held = await store.LockAsync(token, ct);
        if (!store.TryGet(token, owner, out var preview) || preview?.Source is not { } source ||
            !preview.UnresolvedUnits.Contains(sourceUnit, StringComparer.Ordinal) ||
            !await dbContext.Units.AnyAsync(x => x.Code == targetUnit && x.IsActive, ct))
            return null;
        var affected = source.Rows.Where(x => x.UnitCode == sourceUnit).SelectMany(x => x.SourceRows).ToHashSet();
        var revised = source with
        {
            Rows = source.Rows.Select(row => row.UnitCode == sourceUnit ? row with { UnitCode = targetUnit, UnitWasBlank = false } : row).ToList(),
            Issues = source.Issues.Where(issue => issue.Code != "invalid_unit" || issue.RowNumber is null || !affected.Contains(issue.RowNumber.Value)).ToList()
        };
        var updated = await PrepareReadAsync(revised, preview.FileName, owner, preview.UpdateExisting, ct);
        store.Remove(token);
        return updated;
    }

    public async Task<ProductImportPreview?> ResolveDuplicateAsync(string token, Guid owner, string sku,
        IReadOnlyDictionary<string, string> choices, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(sku)) return null;
        await using var held = await store.LockAsync(token, ct);
        if (!store.TryGet(token, owner, out var preview) || preview?.Source is not { } source) return null;
        var conflict = source.Conflicts.SingleOrDefault(x => x.Sku == sku);
        if (conflict is null) return null;
        var selected = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in conflict.Fields)
        {
            if (field.Value.Length <= 1) selected[field.Key] = field.Value.SingleOrDefault();
            else
            {
                if (!choices.TryGetValue(field.Key, out var value) || !field.Value.Contains(value, StringComparer.Ordinal))
                    return null;
                selected[field.Key] = value;
            }
        }
        var numbers = conflict.Rows.SelectMany(x => x.SourceRows).OrderBy(x => x).ToList();
        var merged = new ProductSpreadsheetRow(numbers, sku, selected["Descripción"], selected["Referencia completa"],
            selected["Unidad"] ?? WarehouseEPI.Core.CatalogDefaults.UnassignedUnitCode, selected["Clase"], true)
        { UnitWasBlank = selected["Unidad"] is null };
        var revised = source with
        {
            Rows = source.Rows.Append(merged).OrderBy(x => x.SourceRows[0]).ToList(),
            Conflicts = source.Conflicts.Where(x => x.Sku != sku).ToList(),
            ConsolidatedGroupCount = source.ConsolidatedGroupCount + 1,
            Issues = source.Issues.Where(x => !(x.Code == "duplicate_conflict" && x.RowNumber == numbers[0]))
                .Append(new ProductSpreadsheetIssue(numbers[0], "duplicate_resolved",
                    $"El SKU {sku} se consolidó con los valores elegidos de las filas {string.Join(", ", numbers)}.", false)).ToList()
        };
        var updated = await PrepareReadAsync(revised, preview.FileName, owner, preview.UpdateExisting, ct);
        store.Remove(token);
        return updated;
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
        if (preview.UpdateExisting)
            return await ApplyComparisonAsync(preview, cancellationToken);

        var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            if (dbContext.Database.IsNpgsql())
                await dbContext.Database.ExecuteSqlRawAsync(
                    "LOCK TABLE products, inventory_movement_lines, units, product_classes IN SHARE ROW EXCLUSIVE MODE", cancellationToken);
            var units = await dbContext.Units.Where(unit => unit.IsActive)
                .ToDictionaryAsync(unit => unit.Code, unit => unit.Id, StringComparer.Ordinal, cancellationToken);
            var classes = await dbContext.ProductClasses
                .ToDictionaryAsync(productClass => productClass.Code, StringComparer.Ordinal, cancellationToken);
            var candidateSkus = preview.Rows.Where(row => row.IsCandidate).Select(row => row.Sku).ToList();
            var existing = candidateSkus.Count == 0
                ? new HashSet<string>(StringComparer.Ordinal)
                : (await dbContext.Products.AsNoTracking().Where(product => candidateSkus.Contains(product.Sku))
                    .Select(product => product.Sku).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

            var products = new List<Product>();
            var newClasses = new Dictionary<string, ProductClass>(StringComparer.Ordinal);
            foreach (var row in preview.Rows.Where(row => row.IsCandidate && !existing.Contains(row.Sku)))
            {
                if (!units.TryGetValue(row.UnitCode, out var unitId))
                    return await FailAsync(transaction, $"La unidad {row.UnitCode} ya no está disponible.", preview.ConsolidatedCount, cancellationToken);
                ProductClass? resolvedClass = null;
                if (row.ClassCode is not null)
                {
                    if (classes.TryGetValue(row.ClassCode, out resolvedClass))
                    {
                        if (!resolvedClass.IsActive)
                            return await FailAsync(transaction, $"La clase {row.ClassCode} está inactiva.", preview.ConsolidatedCount, cancellationToken);
                    }
                    else
                    {
                        if (!row.IsNewClass)
                            return await FailAsync(transaction, $"La clase {row.ClassCode} ya no está disponible.", preview.ConsolidatedCount, cancellationToken);
                        if (!newClasses.TryGetValue(row.ClassCode, out resolvedClass))
                            newClasses[row.ClassCode] = resolvedClass = new ProductClass { Code = row.ClassCode, Name = row.ClassCode, IsActive = true };
                    }
                }

                products.Add(new Product
                {
                    Sku = row.Sku,
                    Description = row.Description,
                    ExternalReference = row.ExternalReference,
                    BaseUnitId = unitId,
                    ProductClass = resolvedClass,
                    ProductTypeId = null,
                    MinimumStock = 0m,
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
