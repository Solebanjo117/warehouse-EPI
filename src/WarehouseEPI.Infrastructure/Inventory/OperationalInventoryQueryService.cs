using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record OperationalProductResult(
    Guid Id,
    string Sku,
    string? Description,
    string? ExternalReference,
    string UnitCode,
    bool AllowsDecimals);

public sealed record OperationalLocationResult(
    Guid Id,
    string Code,
    string? Description,
    bool IsActive,
    bool IsBlocked);

public sealed record OperationalCodeResolution(
    OperationalProductResult? Product,
    OperationalLocationResult? Location);

public sealed record OperationalProductLocationResult(
    Guid Id,
    string Code,
    string? Description,
    decimal Quantity,
    bool HasActiveAssignment,
    bool HasNonZeroBalance);

public sealed record OperationalLocationProductResult(
    Guid Id,
    string Sku,
    string? Description,
    string? ExternalReference,
    string UnitCode,
    bool AllowsDecimals,
    decimal Quantity,
    bool HasActiveAssignment,
    bool HasNonZeroBalance);

public sealed record InventoryReceiptChange(
    string LocationCode,
    decimal PreviousQuantity,
    decimal DeltaQuantity,
    decimal ResultingQuantity);

public sealed record InventoryReceiptLine(
    string ProductSku,
    string? ProductDescription,
    string UnitCode,
    decimal Quantity,
    string? SourceLocationCode,
    string? DestinationLocationCode,
    decimal? PreviousQuantity,
    decimal? AdjustmentDelta,
    IReadOnlyList<InventoryReceiptChange> Changes);

public sealed record InventoryReceipt(
    Guid MovementId,
    Guid OperationId,
    InventoryMovementType Type,
    string ResponsibleName,
    string? Reference,
    string? Notes,
    DateTimeOffset OccurredAt,
    IReadOnlyList<InventoryReceiptLine> Lines,
    InventoryReceiptCorrection? Correction = null)
{
    public bool HasNegativeBalance => Lines.SelectMany(line => line.Changes)
        .Any(change => change.ResultingQuantity < 0);
}

public sealed record InventoryReceiptCorrection(Guid OriginalMovementId, Guid ReversalMovementId, Guid? ReplacementMovementId, string Reason);

public sealed class OperationalInventoryQueryService(WarehouseDbContext dbContext)
{
    private const int ResultLimit = 10;

    public async Task<OperationalProductResult?> ResolveProductAsync(
        string? code,
        bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var original = code?.Trim() ?? string.Empty;
        var normalized = CatalogNormalization.NormalizeCode(original);
        if (normalized.Length == 0)
            return null;

        return await ProductQuery(activeOnly)
            .Where(product => product.Sku == normalized ||
                product.Barcodes.Any(barcode => barcode.IsActive && barcode.Barcode == original))
            .Select(ToProductResult())
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<OperationalProductResult?> GetProductAsync(
        Guid id,
        bool activeOnly = true,
        CancellationToken cancellationToken = default) =>
        await ProductQuery(activeOnly).Where(product => product.Id == id)
            .Select(ToProductResult()).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<OperationalProductResult>> SearchProductsAsync(
        string? search,
        CancellationToken cancellationToken = default)
    {
        var term = search?.Trim();
        if (string.IsNullOrWhiteSpace(term))
            return [];

        var normalized = CatalogNormalization.NormalizeCode(term);
        return await ProductQuery(true)
            .Where(product => product.Sku.Contains(normalized) ||
                (product.Description != null && product.Description.ToUpper().Contains(normalized)) ||
                (product.ExternalReference != null && product.ExternalReference.ToUpper().Contains(normalized)) ||
                product.Barcodes.Any(barcode => barcode.IsActive && barcode.Barcode.ToUpper().Contains(normalized)))
            .OrderBy(product => product.Sku)
            .Take(ResultLimit)
            .Select(ToProductResult())
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationalLocationResult?> ResolveLocationAsync(
        string? code,
        bool operationalOnly = true,
        CancellationToken cancellationToken = default)
    {
        var normalized = LocationNormalization.NormalizeForLookup(code);
        if (normalized.Length == 0)
            return null;

        return await LocationQuery(operationalOnly).Where(location => location.Code == normalized)
            .Select(ToLocationResult()).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<OperationalCodeResolution> ResolveCodeAsync(
        string? code,
        CancellationToken cancellationToken = default)
    {
        // The same DbContext cannot execute these queries concurrently. Keeping them together
        // still gives a scanner one HTTP request and preserves each resolver's exact-match rules.
        var product = await ResolveProductAsync(code, cancellationToken: cancellationToken);
        var location = await ResolveLocationAsync(code, cancellationToken: cancellationToken);
        return new(product, location);
    }

    public async Task<OperationalLocationResult?> GetLocationAsync(
        Guid id,
        bool operationalOnly = true,
        CancellationToken cancellationToken = default) =>
        await LocationQuery(operationalOnly).Where(location => location.Id == id)
            .Select(ToLocationResult()).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<OperationalLocationResult>> SearchLocationsAsync(
        string? search,
        CancellationToken cancellationToken = default)
    {
        var term = search?.Trim();
        if (string.IsNullOrWhiteSpace(term))
            return [];

        var normalized = LocationNormalization.NormalizeForLookup(term);
        return await LocationQuery(true)
            .Where(location => location.Code.Contains(normalized) ||
                (location.Description != null && location.Description.ToUpper().Contains(normalized)))
            .OrderBy(location => location.Code)
            .Take(ResultLimit)
            .Select(ToLocationResult())
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationalProductLocationResult>> GetProductLocationsAsync(
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        var assignments = await dbContext.ProductLocationAssignments.AsNoTracking()
            .Where(assignment => assignment.ProductId == productId && assignment.IsActive &&
                assignment.Product.IsActive && assignment.Product.BaseUnit.IsActive &&
                assignment.Location.IsActive && !assignment.Location.IsBlocked)
            .Select(assignment => new OperationalProductLocationResult(
                assignment.LocationId,
                assignment.Location.Code,
                assignment.Location.Description,
                0m,
                true,
                false))
            .ToListAsync(cancellationToken);
        var balances = await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => balance.ProductId == productId && balance.Quantity != 0 &&
                balance.Product.IsActive && balance.Product.BaseUnit.IsActive &&
                balance.Location.IsActive && !balance.Location.IsBlocked)
            .Select(balance => new OperationalProductLocationResult(
                balance.LocationId,
                balance.Location.Code,
                balance.Location.Description,
                balance.Quantity,
                false,
                true))
            .ToListAsync(cancellationToken);

        return MergeProductLocations(assignments, balances);
    }

    public async Task<IReadOnlyList<OperationalLocationProductResult>> GetLocationProductsAsync(
        Guid locationId,
        CancellationToken cancellationToken = default)
    {
        var assignments = await dbContext.ProductLocationAssignments.AsNoTracking()
            .Where(assignment => assignment.LocationId == locationId && assignment.IsActive &&
                assignment.Location.IsActive && !assignment.Location.IsBlocked &&
                assignment.Product.IsActive && assignment.Product.BaseUnit.IsActive)
            .Select(assignment => new OperationalLocationProductResult(
                assignment.ProductId,
                assignment.Product.Sku,
                assignment.Product.Description,
                assignment.Product.ExternalReference,
                assignment.Product.BaseUnit.Code,
                assignment.Product.BaseUnit.AllowsDecimals,
                0m,
                true,
                false))
            .ToListAsync(cancellationToken);
        var balances = await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => balance.LocationId == locationId && balance.Quantity != 0 &&
                balance.Location.IsActive && !balance.Location.IsBlocked &&
                balance.Product.IsActive && balance.Product.BaseUnit.IsActive)
            .Select(balance => new OperationalLocationProductResult(
                balance.ProductId,
                balance.Product.Sku,
                balance.Product.Description,
                balance.Product.ExternalReference,
                balance.Product.BaseUnit.Code,
                balance.Product.BaseUnit.AllowsDecimals,
                balance.Quantity,
                false,
                true))
            .ToListAsync(cancellationToken);

        return MergeLocationProducts(assignments, balances);
    }

    public async Task<InventoryReceipt?> GetReceiptAsync(
        Guid movementId,
        CancellationToken cancellationToken = default)
    {
        var movement = await dbContext.InventoryMovements.AsNoTracking()
            .Include(item => item.ResponsibleUser)
            .Include(item => item.Lines).ThenInclude(line => line.Product)
            .Include(item => item.Lines).ThenInclude(line => line.Unit)
            .Include(item => item.Lines).ThenInclude(line => line.SourceLocation)
            .Include(item => item.Lines).ThenInclude(line => line.DestinationLocation)
            .Include(item => item.Lines).ThenInclude(line => line.BalanceChanges)
                .ThenInclude(change => change.Location)
            .SingleOrDefaultAsync(item => item.Id == movementId, cancellationToken);
        if (movement is null)
            return null;

        var correction = await dbContext.InventoryMovementCorrections.AsNoTracking()
            .Where(item => item.OriginalMovementId == movementId || item.ReversalMovementId == movementId || item.ReplacementMovementId == movementId)
            .Select(item => new InventoryReceiptCorrection(item.OriginalMovementId, item.ReversalMovementId, item.ReplacementMovementId, item.Reason))
            .SingleOrDefaultAsync(cancellationToken);
        return new(
            movement.Id,
            movement.OperationId,
            movement.Type,
            movement.ResponsibleUser.FullName,
            movement.Reference,
            movement.Notes,
            movement.OccurredAt,
            movement.Lines.OrderBy(line => line.LineNumber).Select(line => new InventoryReceiptLine(
                line.Product.Sku,
                line.Product.Description,
                line.Unit.Code,
                line.Quantity,
                line.SourceLocation?.Code,
                line.DestinationLocation?.Code,
                line.PreviousQuantity,
                line.AdjustmentDelta,
                line.BalanceChanges.OrderBy(change => change.Location.Code).Select(change =>
                    new InventoryReceiptChange(
                        change.Location.Code,
                        change.PreviousQuantity,
                        change.DeltaQuantity,
                        change.ResultingQuantity)).ToArray())).ToArray(), correction);
    }

    private IQueryable<Product> ProductQuery(bool activeOnly)
    {
        var query = dbContext.Products.AsNoTracking();
        return activeOnly ? query.Where(product => product.IsActive && product.BaseUnit.IsActive) : query;
    }

    private IQueryable<Location> LocationQuery(bool operationalOnly)
    {
        var query = dbContext.Locations.AsNoTracking();
        return operationalOnly ? query.Where(location => location.IsActive && !location.IsBlocked) : query;
    }

    private static System.Linq.Expressions.Expression<Func<Product, OperationalProductResult>> ToProductResult() =>
        product => new(
            product.Id,
            product.Sku,
            product.Description,
            product.ExternalReference,
            product.BaseUnit.Code,
            product.BaseUnit.AllowsDecimals);

    private static System.Linq.Expressions.Expression<Func<Location, OperationalLocationResult>> ToLocationResult() =>
        location => new(location.Id, location.Code, location.Description, location.IsActive, location.IsBlocked);

    private static IReadOnlyList<OperationalProductLocationResult> MergeProductLocations(
        IEnumerable<OperationalProductLocationResult> assignments,
        IEnumerable<OperationalProductLocationResult> balances)
    {
        var results = assignments.ToDictionary(item => item.Id);
        foreach (var balance in balances)
        {
            results[balance.Id] = results.TryGetValue(balance.Id, out var existing)
                ? existing with { Quantity = existing.Quantity + balance.Quantity }
                : balance;
        }

        return results.Values
            .Where(item => item.HasActiveAssignment || item.Quantity != 0)
            .Select(item => item with { HasNonZeroBalance = item.Quantity != 0 })
            .OrderBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<OperationalLocationProductResult> MergeLocationProducts(
        IEnumerable<OperationalLocationProductResult> assignments,
        IEnumerable<OperationalLocationProductResult> balances)
    {
        var results = assignments.ToDictionary(item => item.Id);
        foreach (var balance in balances)
        {
            results[balance.Id] = results.TryGetValue(balance.Id, out var existing)
                ? existing with { Quantity = existing.Quantity + balance.Quantity }
                : balance;
        }

        return results.Values
            .Where(item => item.HasActiveAssignment || item.Quantity != 0)
            .Select(item => item with { HasNonZeroBalance = item.Quantity != 0 })
            .OrderBy(item => item.Sku, StringComparer.Ordinal)
            .ToArray();
    }
}
