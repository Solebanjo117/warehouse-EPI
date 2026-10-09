using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class EntryModel(
    InventoryMovementService movementService,
    InventoryQueryService inventoryQuery,
    OperationalInventoryQueryService operationalQuery, IStringLocalizer<OperationsTexts> texts,
    StagingArrivalQuery stagingArrivals)
    : OperationPageModel(movementService, inventoryQuery, operationalQuery, texts)
{
    private readonly OperationalInventoryQueryService entryQuery = operationalQuery;
    [BindProperty(SupportsGet = true)] public string? Mode { get; set; }
    [BindProperty] public PalletDistributionInput Distribution { get; set; } = new();
    public bool IsStagingMode => string.Equals(Mode, "staging", StringComparison.OrdinalIgnoreCase);
    public int StagingPendingCount { get; private set; }
    public override InventoryMovementType MovementType => InventoryMovementType.Entry;
    public override string PageTitle => T("Entrada");
    public override string PageHelp => T("Registra material recibido en una ubicación.");

    public override async Task<IActionResult> OnGetAsync(Guid? productId, Guid? sourceLocationId,
        Guid? destinationLocationId, Guid? locationId, string? mode,
        CancellationToken cancellationToken = default)
    {
        Mode = mode;
        if (!IsStagingMode)
            return await base.OnGetAsync(productId, sourceLocationId, destinationLocationId, locationId, mode, cancellationToken);

        StagingPendingCount = await stagingArrivals.CountPendingAsync(cancellationToken);
        var staging = await entryQuery.ResolveLocationAsync("STAGING", cancellationToken: cancellationToken);
        var destinationId = staging is { IsWip: false } ? staging.Id : (Guid?)null;
        var result = await base.OnGetAsync(productId, sourceLocationId, destinationId, locationId, mode, cancellationToken);
        if (destinationId is null || Input.DestinationLocationId is null)
            PrefillWarning = T("STAGING no está disponible. Selecciona otra ubicación destino");
        return result;
    }

    public override async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!Distribution.Enabled)
        {
            foreach (var key in ModelState.Keys.Where(k => k.StartsWith("Distribution.Quantities", StringComparison.Ordinal)).ToArray()) ModelState.Remove(key);
        }
        else
        {
            var destination = Input.DestinationLocationId is Guid id ? await entryQuery.GetLocationAsync(id, cancellationToken: cancellationToken) : null;
            var product = Input.ProductId is Guid productId ? await entryQuery.GetProductAsync(productId, cancellationToken: cancellationToken) : null;
            if (!IsStagingMode || destination is not { Code: "STAGING", Kind: LocationKind.Area, IsWip: false })
                ModelState.AddModelError("Distribution", T("La división solo está disponible para entradas a STAGING en modo staging."));
            var validParts = PalletDistribution.TryCalculateTotal(Distribution.Quantities.Select(q => q ?? 0).ToArray(), product?.AllowsDecimals ?? false, out var total);
            Input.Quantity = total;
            ModelState.Remove("Input.Quantity");
            var totalText = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ModelState.SetModelValue("Input.Quantity", totalText, totalText);
            ModelState.MarkFieldValid("Input.Quantity");
            if (product is null || !validParts)
                ModelState.AddModelError("Distribution", T("Captura entre 2 y 100 partes válidas sin superar la cantidad máxima permitida."));
        }
        var result = await base.OnPostAsync(cancellationToken);
        if (IsStagingMode && result is Microsoft.AspNetCore.Mvc.RazorPages.PageResult)
            StagingPendingCount = await stagingArrivals.CountPendingAsync(cancellationToken);
        return result;
    }

    protected override Task<InventoryMovementResult> ConfirmMovementAsync(InventoryMovementCommand command, CancellationToken token) =>
        base.ConfirmMovementAsync(Distribution.Enabled ? command with {
            Lines = command.Lines.Select(line => line with { PalletQuantities = Distribution.Quantities.Select(q => q!.Value).ToArray() }).ToArray()
        } : command, token);
}
