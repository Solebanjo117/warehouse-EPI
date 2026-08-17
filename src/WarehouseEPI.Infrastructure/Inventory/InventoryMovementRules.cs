using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Inventory;

internal static class InventoryMovementRules
{
    internal const decimal MaximumQuantity = 99_999_999_999_999.9999m;

    internal static InventoryMovementCommand Normalize(InventoryMovementCommand command) => command with
    {
        Reference = NormalizeOptional(command.Reference),
        Notes = NormalizeOptional(command.Notes),
        ApprovedSharedAssignments = (command.ApprovedSharedAssignments ?? [])
            .Distinct().OrderBy(item => item.ProductId).ThenBy(item => item.LocationId).ToArray()
    };

    internal static List<string> ValidateStructure(InventoryMovementCommand command)
    {
        var errors = new List<string>();
        if (command.OperationId == Guid.Empty)
            errors.Add("El identificador de operación es obligatorio.");
        if (command.Lines.Count == 0)
            errors.Add("El movimiento debe contener al menos una línea.");
        if (command.Reference?.Length > 120)
            errors.Add("La referencia no puede superar 120 caracteres.");
        if (command.Notes?.Length > 500)
            errors.Add("Las observaciones no pueden superar 500 caracteres.");

        foreach (var (line, index) in command.Lines.Select((line, index) => (line, index)))
        {
            var label = $"Línea {index + 1}";
            if (line.ProductId == Guid.Empty)
                errors.Add($"{label}: el producto es obligatorio.");
            if (decimal.Round(line.Quantity, 4) != line.Quantity || Math.Abs(line.Quantity) > MaximumQuantity)
                errors.Add($"{label}: la cantidad excede la precisión numeric(18,4).");

            switch (command.Type)
            {
                case InventoryMovementType.Entry:
                    if (line.Quantity <= 0 || line.DestinationLocationId is null ||
                        line.SourceLocationId is not null || line.LocationId is not null)
                        errors.Add($"{label}: una entrada requiere cantidad positiva y únicamente ubicación destino.");
                    break;
                case InventoryMovementType.Exit:
                    if (line.Quantity <= 0 || line.SourceLocationId is null ||
                        line.DestinationLocationId is not null || line.LocationId is not null)
                        errors.Add($"{label}: una salida requiere cantidad positiva y únicamente ubicación origen.");
                    break;
                case InventoryMovementType.Transfer:
                    if (line.Quantity <= 0 || line.SourceLocationId is null || line.DestinationLocationId is null ||
                        line.LocationId is not null || line.SourceLocationId == line.DestinationLocationId)
                        errors.Add($"{label}: una transferencia requiere cantidad positiva y ubicaciones distintas.");
                    break;
                case InventoryMovementType.Adjustment:
                    if (line.LocationId is null || line.SourceLocationId is not null ||
                        line.DestinationLocationId is not null || line.ExpectedBalanceVersion is null)
                        errors.Add($"{label}: un ajuste requiere ubicación y versión del saldo consultado.");
                    break;
                default:
                    errors.Add($"{label}: tipo de movimiento no soportado.");
                    break;
            }
        }

        return errors;
    }

    internal static List<string> ValidateProductsAndQuantities(
        InventoryMovementCommand command,
        IReadOnlyDictionary<Guid, Product> products)
    {
        var errors = new List<string>();
        foreach (var (line, index) in command.Lines.Select((line, index) => (line, index)))
        {
            if (!products.TryGetValue(line.ProductId, out var product))
            {
                errors.Add($"Línea {index + 1}: el producto no existe.");
                continue;
            }

            if (!product.IsActive)
                errors.Add($"Línea {index + 1}: el producto está inactivo.");
            if (!product.BaseUnit.IsActive)
                errors.Add($"Línea {index + 1}: la unidad base está inactiva.");
            if (!product.BaseUnit.AllowsDecimals && decimal.Truncate(line.Quantity) != line.Quantity)
                errors.Add($"Línea {index + 1}: la unidad base no permite cantidades decimales.");
        }

        return errors;
    }

    internal static List<string> ValidateLocations(
        IReadOnlyCollection<Guid> requestedIds,
        IReadOnlyDictionary<Guid, Location> locations)
    {
        var errors = new List<string>();
        foreach (var id in requestedIds)
        {
            if (!locations.TryGetValue(id, out var location))
                errors.Add("Una ubicación indicada no existe.");
            else if (!location.IsActive)
                errors.Add($"La ubicación {location.Code} está inactiva.");
            else if (location.IsBlocked)
                errors.Add($"La ubicación {location.Code} está bloqueada.");
        }

        return errors;
    }

    internal static IEnumerable<Guid> GetLocations(InventoryMovementLineCommand line, InventoryMovementType type) => type switch
    {
        InventoryMovementType.Entry => [line.DestinationLocationId!.Value],
        InventoryMovementType.Exit => [line.SourceLocationId!.Value],
        InventoryMovementType.Transfer => [line.SourceLocationId!.Value, line.DestinationLocationId!.Value],
        _ => [line.LocationId!.Value]
    };

    internal static IEnumerable<InventoryAssignmentKey> GetLocationPairs(InventoryMovementCommand command) => command.Lines
        .SelectMany(line => GetLocations(line, command.Type).Select(location => new InventoryAssignmentKey(line.ProductId, location)))
        .Distinct();

    internal static string CreateFingerprint(InventoryMovementCommand command, Guid userId)
    {
        var builder = new StringBuilder();
        builder.Append(userId.ToString("N")).Append('|')
            .Append(command.Type).Append('|')
            .Append(command.Reference ?? string.Empty).Append('|')
            .Append(command.Notes ?? string.Empty);
        foreach (var line in command.Lines)
        {
            builder.Append("|L:").Append(line.ProductId.ToString("N"))
                .Append(':').Append(line.Quantity.ToString("G29", CultureInfo.InvariantCulture))
                .Append(':').Append(line.SourceLocationId?.ToString("N") ?? string.Empty)
                .Append(':').Append(line.DestinationLocationId?.ToString("N") ?? string.Empty)
                .Append(':').Append(line.LocationId?.ToString("N") ?? string.Empty)
                .Append(':').Append(line.ExpectedBalanceVersion?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }

        foreach (var approval in command.ApprovedSharedAssignments ?? [])
        {
            builder.Append("|A:").Append(approval.ProductId.ToString("N"))
                .Append(':').Append(approval.LocationId.ToString("N"));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal readonly record struct InventoryBalanceKey(Guid ProductId, Guid LocationId, Guid? LotId);
internal readonly record struct InventoryAssignmentKey(Guid ProductId, Guid LocationId);
internal sealed class InventoryQuantityOutOfRangeException(string message) : Exception(message);
