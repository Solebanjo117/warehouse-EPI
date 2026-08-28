using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations;

public abstract class OperationPageModel(
    InventoryMovementService movementService,
    InventoryQueryService inventoryQuery,
    OperationalInventoryQueryService operationalQuery) : PageModel
{
    [BindProperty]
    public OperationInput Input { get; set; } = new();

    public OperationalProductResult? SelectedProduct { get; private set; }
    public OperationalLocationResult? SelectedSource { get; private set; }
    public OperationalLocationResult? SelectedDestination { get; private set; }
    public OperationalLocationResult? SelectedLocation { get; private set; }
    public InventoryBalanceSnapshot? SourceBalance { get; private set; }
    public InventoryBalanceSnapshot? DestinationBalance { get; private set; }
    public InventoryBalanceSnapshot? LocationBalance { get; private set; }
    public IReadOnlyList<SharedLocationConflict> SharingConflicts { get; private set; } = [];
    public string? PrefillWarning { get; private set; }
    public bool NeedsSharingApproval => SharingConflicts.Count > 0;

    public abstract InventoryMovementType MovementType { get; }
    public virtual InventoryMovementPurpose MovementPurpose => InventoryMovementPurpose.Standard;
    protected virtual InventoryMovementType CommandMovementType => MovementType;
    public virtual string OperationKey => MovementType.ToString().ToLowerInvariant();
    public abstract string PageTitle { get; }
    public abstract string PageHelp { get; }

    public async Task OnGetAsync(Guid? productId, Guid? sourceLocationId,
        Guid? destinationLocationId, Guid? locationId, string? mode,
        CancellationToken cancellationToken)
    {
        Input.OperationId = Guid.NewGuid();
        if (this is ExitModel)
            Input.ExitMode = mode?.ToLowerInvariant() switch
            {
                "general" => ExitMode.General,
                "wip" => ExitMode.Wip,
                _ => null
            };

        Input.ProductId = productId;
        Input.SourceLocationId = sourceLocationId;
        Input.DestinationLocationId = destinationLocationId;
        Input.LocationId = locationId;
        await LoadSelectionAsync(cancellationToken);

        var invalidFields = ModelState.Where(item => item.Value?.Errors.Count > 0)
            .Select(item => item.Key).ToArray();
        if (invalidFields.Length == 0)
        {
            if (MovementType == InventoryMovementType.Adjustment && LocationBalance is not null)
                Input.ExpectedBalanceVersion = LocationBalance.Version;
            return;
        }

        foreach (var field in invalidFields)
        {
            if (field.EndsWith(nameof(Input.ProductId), StringComparison.Ordinal)) Input.ProductId = null;
            if (field.EndsWith(nameof(Input.SourceLocationId), StringComparison.Ordinal)) Input.SourceLocationId = null;
            if (field.EndsWith(nameof(Input.DestinationLocationId), StringComparison.Ordinal)) Input.DestinationLocationId = null;
            if (field.EndsWith(nameof(Input.LocationId), StringComparison.Ordinal)) Input.LocationId = null;
        }
        ModelState.Clear();
        PrefillWarning = "Parte de la precarga ya no está disponible o no es compatible; vuelve a seleccionarla.";
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        ValidateInput();
        await LoadSelectionAsync(cancellationToken);
        if (!ModelState.IsValid)
        {
            ClearPin();
            return Page();
        }

        var command = new InventoryMovementCommand(
            Input.OperationId,
            CommandMovementType,
            Input.Pin,
            [BuildLine()],
            Input.Reference,
            Input.Notes,
            Input.ApprovedSharedLocationIds
                .Distinct()
                .Select(locationId => new SharedAssignmentApproval(Input.ProductId!.Value, locationId))
                .ToArray(),
            MovementPurpose,
            MovementPurpose == InventoryMovementPurpose.ProductionIssue ? Input.DestinationLocationId : null);

        var result = await movementService.ConfirmAsync(command, cancellationToken);
        ClearPin();

        if (result.Status == InventoryMovementStatus.Success && result.MovementId is Guid movementId)
            return RedirectToPage("/Operations/Receipt", new { id = movementId });

        switch (result.Status)
        {
            case InventoryMovementStatus.InvalidPin:
                ModelState.AddModelError(string.Empty, "No fue posible validar el NIP o el usuario.");
                break;
            case InventoryMovementStatus.ValidationFailed:
                foreach (var error in result.ValidationErrors)
                    ModelState.AddModelError(string.Empty, error);
                break;
            case InventoryMovementStatus.RequiresLocationSharingConfirmation:
                SharingConflicts = result.Conflicts;
                Input.ApprovedSharedLocationIds = [];
                ModelState.Remove("Input.ApprovedSharedLocationIds");
                break;
            case InventoryMovementStatus.BalanceChanged:
                await RefreshAdjustmentBalanceAsync(cancellationToken);
                ModelState.AddModelError(string.Empty,
                    "El saldo cambió. Se recargó el conteo actual; revísalo y vuelve a introducir tu NIP.");
                break;
            case InventoryMovementStatus.IdempotencyConflict:
                ModelState.AddModelError(string.Empty,
                    "La operación ya fue utilizada con otro contenido o responsable. Inicia una operación nueva.");
                break;
            default:
                ModelState.AddModelError(string.Empty, "No fue posible confirmar la operación.");
                break;
        }

        return Page();
    }

    private void ValidateInput()
    {
        if (Input.OperationId == Guid.Empty)
            ModelState.AddModelError("Input.OperationId", "La operación no es válida.");
        if (Input.ProductId is null || Input.ProductId == Guid.Empty)
            ModelState.AddModelError("Input.ProductId", "Selecciona un producto.");
        if (string.IsNullOrWhiteSpace(Input.Pin))
            ModelState.AddModelError(string.Empty, "Introduce el NIP para confirmar.");

        if (MovementType != InventoryMovementType.Adjustment && Input.Quantity <= 0)
            ModelState.AddModelError("Input.Quantity", "La cantidad debe ser mayor que cero.");
        if (decimal.Round(Input.Quantity, 4) != Input.Quantity)
            ModelState.AddModelError("Input.Quantity", "La cantidad admite como máximo cuatro decimales.");

        if (this is ExitModel)
        {
            if (Input.ExitMode is null)
                ModelState.AddModelError("Input.ExitMode", "Selecciona el tipo de salida.");
            else if (Input.ExitMode == ExitMode.General && Input.DestinationLocationId is not null)
                ModelState.AddModelError("Input.DestinationLocationId", "La salida general no utiliza un rack WIP destino.");
        }

        switch (CommandMovementType)
        {
            case InventoryMovementType.Entry when Input.DestinationLocationId is null:
                ModelState.AddModelError("Input.DestinationLocationId", "Selecciona la ubicación destino.");
                break;
            case InventoryMovementType.Exit when Input.SourceLocationId is null:
                ModelState.AddModelError("Input.SourceLocationId", "Selecciona la ubicación origen.");
                break;
            case InventoryMovementType.Transfer:
                if (Input.SourceLocationId is null)
                    ModelState.AddModelError("Input.SourceLocationId", "Selecciona la ubicación origen.");
                if (Input.DestinationLocationId is null)
                    ModelState.AddModelError("Input.DestinationLocationId", "Selecciona la ubicación destino.");
                if (Input.SourceLocationId is not null && Input.SourceLocationId == Input.DestinationLocationId)
                    ModelState.AddModelError("Input.DestinationLocationId", "Origen y destino deben ser distintos.");
                break;
            case InventoryMovementType.Adjustment:
                if (Input.LocationId is null)
                    ModelState.AddModelError("Input.LocationId", "Selecciona la ubicación.");
                if (Input.ExpectedBalanceVersion is null)
                    ModelState.AddModelError("Input.ExpectedBalanceVersion", "Consulta nuevamente el saldo.");
                if (string.IsNullOrWhiteSpace(Input.Notes))
                    ModelState.AddModelError("Input.Notes", "El motivo del ajuste es obligatorio.");
                break;
        }
        if (MovementPurpose == InventoryMovementPurpose.ProductionIssue && Input.DestinationLocationId is null)
            ModelState.AddModelError("Input.DestinationLocationId", "Selecciona el rack WIP destino.");
    }

    private async Task LoadSelectionAsync(CancellationToken cancellationToken)
    {
        if (Input.ProductId is Guid productId)
        {
            SelectedProduct = await operationalQuery.GetProductAsync(productId, cancellationToken: cancellationToken);
            if (SelectedProduct is null)
                ModelState.AddModelError("Input.ProductId", "El producto no existe o está inactivo.");
        }

        if (Input.SourceLocationId is Guid sourceId)
        {
            SelectedSource = await operationalQuery.GetLocationAsync(sourceId, cancellationToken: cancellationToken);
            if (SelectedSource is null)
                ModelState.AddModelError("Input.SourceLocationId", "La ubicación origen no está disponible.");
            else if (MovementPurpose == InventoryMovementPurpose.ProductionIssue &&
                (SelectedSource.Kind != LocationKind.Rack || SelectedSource.IsWip))
                ModelState.AddModelError("Input.SourceLocationId", "Selecciona un rack de inventario como origen.");
        }
        if (Input.DestinationLocationId is Guid destinationId)
        {
            SelectedDestination = await operationalQuery.GetLocationAsync(destinationId, cancellationToken: cancellationToken);
            if (SelectedDestination is null)
                ModelState.AddModelError("Input.DestinationLocationId", "La ubicación destino no está disponible.");
            else if (MovementPurpose == InventoryMovementPurpose.ProductionIssue &&
                SelectedDestination.OperationalRole != LocationOperationalRole.Wip)
                ModelState.AddModelError("Input.DestinationLocationId", "Selecciona un rack WIP.");
        }
        if (Input.LocationId is Guid locationId)
        {
            SelectedLocation = await operationalQuery.GetLocationAsync(locationId, cancellationToken: cancellationToken);
            if (SelectedLocation is null)
                ModelState.AddModelError("Input.LocationId", "La ubicación no está disponible.");
        }

        if (Input.ProductId is not Guid selectedProductId)
            return;
        if (Input.SourceLocationId is Guid selectedSourceId)
            SourceBalance = await inventoryQuery.GetBalanceAsync(selectedProductId, selectedSourceId, cancellationToken);
        if (Input.DestinationLocationId is Guid selectedDestinationId)
            DestinationBalance = await inventoryQuery.GetBalanceAsync(selectedProductId, selectedDestinationId, cancellationToken);
        if (Input.LocationId is Guid selectedLocationId)
            LocationBalance = await inventoryQuery.GetBalanceAsync(selectedProductId, selectedLocationId, cancellationToken);
    }

    private InventoryMovementLineCommand BuildLine() => CommandMovementType switch
    {
        InventoryMovementType.Entry => new(
            Input.ProductId!.Value, Input.Quantity, DestinationLocationId: Input.DestinationLocationId),
        InventoryMovementType.Exit => new(
            Input.ProductId!.Value, Input.Quantity, SourceLocationId: Input.SourceLocationId),
        InventoryMovementType.Transfer => new(
            Input.ProductId!.Value, Input.Quantity,
            SourceLocationId: Input.SourceLocationId,
            DestinationLocationId: Input.DestinationLocationId),
        InventoryMovementType.Adjustment => new(
            Input.ProductId!.Value, Input.Quantity,
            LocationId: Input.LocationId,
            ExpectedBalanceVersion: Input.ExpectedBalanceVersion),
        _ => throw new InvalidOperationException("Tipo de operación no soportado.")
    };

    private async Task RefreshAdjustmentBalanceAsync(CancellationToken cancellationToken)
    {
        if (MovementType != InventoryMovementType.Adjustment ||
            Input.ProductId is not Guid productId || Input.LocationId is not Guid locationId)
            return;

        LocationBalance = await inventoryQuery.GetBalanceAsync(productId, locationId, cancellationToken);
        Input.ExpectedBalanceVersion = LocationBalance.Version;
        ModelState.Remove("Input.ExpectedBalanceVersion");
    }

    private void ClearPin()
    {
        Input.Pin = string.Empty;
        ModelState.SetModelValue("Input.Pin", string.Empty, string.Empty);
    }

    public sealed class OperationInput
    {
        public Guid OperationId { get; set; }
        public Guid? ProductId { get; set; }
        public Guid? SourceLocationId { get; set; }
        public Guid? DestinationLocationId { get; set; }
        public Guid? LocationId { get; set; }
        public ExitMode? ExitMode { get; set; }
        public uint? ExpectedBalanceVersion { get; set; }
        public decimal Quantity { get; set; }
        [StringLength(120)] public string? Reference { get; set; }
        [StringLength(500)] public string? Notes { get; set; }
        public string Pin { get; set; } = string.Empty;
        public List<Guid> ApprovedSharedLocationIds { get; set; } = [];
    }
}

public enum ExitMode
{
    General,
    Wip
}
