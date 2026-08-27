using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Labels;

public enum PalletLicensePlateStatus { Success, InvalidFolio, NotFound, NotEligible, TemplateUnavailable, ValidationFailed }

public sealed record PalletLicensePlateEntry(Guid MovementId, string Sku, string? Description, string? ExternalReference,
    string UnitCode, bool AllowsDecimals, decimal Quantity, string? Destination, string? Reference,
    string Responsible, DateTimeOffset OccurredAt)
{
    public string Identifier => $"PLT-{MovementId:N}".ToUpperInvariant();
}

public sealed record PalletLicensePlateLoad(PalletLicensePlateStatus Status, PalletLicensePlateEntry? Entry = null, string? Error = null);

public sealed class PalletLicensePlateService(WarehouseDbContext db)
{
    public static bool IsEligible(
        InventoryMovementType type,
        InventoryMovementPurpose purpose,
        int lineCount,
        bool isOriginalOrReversal = false) =>
        type == InventoryMovementType.Entry &&
        purpose == InventoryMovementPurpose.Standard &&
        lineCount == 1 &&
        !isOriginalOrReversal;

    public static bool TryParseFolio(string? folio, out Guid movementId)
    {
        movementId = Guid.Empty;
        var value = folio?.Trim() ?? string.Empty;
        if (value.StartsWith("PLT-", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        return Guid.TryParse(value, out movementId);
    }

    public async Task<PalletLicensePlateLoad> LoadAsync(Guid movementId, CancellationToken token = default)
    {
        var movement = await db.InventoryMovements.AsNoTracking()
            .Include(item => item.ResponsibleUser)
            .Include(item => item.Lines).ThenInclude(item => item.Product).ThenInclude(item => item.BaseUnit)
            .Include(item => item.Lines).ThenInclude(item => item.Unit)
            .Include(item => item.Lines).ThenInclude(item => item.DestinationLocation)
            .SingleOrDefaultAsync(item => item.Id == movementId, token);
        if (movement is null) return new(PalletLicensePlateStatus.NotFound, Error: "No existe una Entrada con ese folio.");
        if (!IsEligible(movement.Type, movement.Purpose, movement.Lines.Count))
            return new(PalletLicensePlateStatus.NotEligible, Error: "La placa requiere una Entrada confirmada de una sola línea.");
        var isOriginalOrReversal = await db.InventoryMovementCorrections.AsNoTracking()
            .AnyAsync(item => item.OriginalMovementId == movementId || item.ReversalMovementId == movementId, token);
        if (!IsEligible(movement.Type, movement.Purpose, movement.Lines.Count, isOriginalOrReversal))
            return new(PalletLicensePlateStatus.NotEligible, Error: "La Entrada fue corregida o es un reverso; usa la Entrada de reemplazo vigente.");

        var line = movement.Lines.Single();
        return new(PalletLicensePlateStatus.Success, new(movement.Id, line.Product.Sku, line.Product.Description,
            line.Product.ExternalReference, line.Unit.Code, line.Unit.AllowsDecimals, line.Quantity,
            line.DestinationLocation?.Code, movement.Reference, movement.ResponsibleUser.FullName, movement.OccurredAt));
    }

    public static OperationalProductResult Product(PalletLicensePlateEntry entry) => new(Guid.Empty, entry.Sku,
        entry.Description, entry.ExternalReference, entry.UnitCode, entry.AllowsDecimals, true);

    public static IReadOnlyDictionary<string, string> SystemValues(PalletLicensePlateEntry entry, DateOnly localDate) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plate.identifier"] = entry.Identifier,
            ["entry.reference"] = entry.Reference ?? string.Empty,
            ["entry.occurredDate"] = localDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            ["entry.responsible"] = entry.Responsible,
            ["entry.destination"] = entry.Destination ?? "—",
            ["entry.quantity"] = entry.Quantity.ToString("0.####", CultureInfo.InvariantCulture),
            ["entry.unit"] = entry.UnitCode
        };
}
