using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class WipProcessModel(
    WarehouseDbContext dbContext,
    InventoryMovementService movementService,
    OperationalInventoryQueryService operationalQuery,
    InventoryQueryService inventoryQuery) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<WipOption> WipLocations { get; private set; } = [];
    public IReadOnlyList<SharedLocationConflict> SharingConflicts { get; private set; } = [];
    public OperationalProductResult? Product { get; private set; }
    public OperationalLocationResult? Source { get; private set; }
    public OperationalLocationResult? Destination { get; private set; }
    public InventoryBalanceSnapshot? SourceBalance { get; private set; }

    public async Task OnGetAsync(string? action, string? wipCode, string? productCode, CancellationToken cancellationToken)
    {
        Input.OperationId = Guid.NewGuid();
        Input.Action = action?.ToLowerInvariant() switch
        {
            "return" => WipProcessAction.WarehouseReturn,
            "supplier" => WipProcessAction.SupplierReturn,
            _ => WipProcessAction.Consumption
        };
        Input.WipCode = wipCode?.Trim() ?? string.Empty;
        Input.ProductCode = productCode?.Trim() ?? string.Empty;
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var pin = Input.Pin;
        Input.Pin = string.Empty;
        await ResolveAsync(cancellationToken);
        ValidateResolved();
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var type = Input.Action == WipProcessAction.WarehouseReturn
            ? InventoryMovementType.Transfer
            : InventoryMovementType.Exit;
        var purpose = Input.Action switch
        {
            WipProcessAction.Consumption => InventoryMovementPurpose.WipConsumption,
            WipProcessAction.WarehouseReturn => InventoryMovementPurpose.WipWarehouseReturn,
            WipProcessAction.SupplierReturn => InventoryMovementPurpose.WipSupplierReturn,
            _ => InventoryMovementPurpose.WipConsumption
        };
        var line = type == InventoryMovementType.Transfer
            ? new InventoryMovementLineCommand(Product!.Id, Input.Quantity, Source!.Id, Destination!.Id)
            : new InventoryMovementLineCommand(Product!.Id, Input.Quantity, SourceLocationId: Source!.Id);
        var result = await movementService.ConfirmAsync(new(
            Input.OperationId,
            type,
            pin,
            [line],
            Input.Reference,
            Input.Notes,
            Input.ApprovedSharedLocationIds.Distinct()
                .Select(id => new SharedAssignmentApproval(Product.Id, id)).ToArray(),
            purpose,
            Source.Id), cancellationToken);

        if (result.Status == InventoryMovementStatus.Success && result.MovementId is Guid movementId)
            return RedirectToPage("/Operations/Receipt", new { id = movementId });

        if (result.Status == InventoryMovementStatus.InvalidPin)
            ModelState.AddModelError(string.Empty, "No fue posible validar el NIP o el usuario.");
        else if (result.Status == InventoryMovementStatus.RequiresLocationSharingConfirmation)
        {
            SharingConflicts = result.Conflicts;
            Input.ApprovedSharedLocationIds = [];
        }
        else
            foreach (var error in result.ValidationErrors.DefaultIfEmpty("No fue posible procesar el WIP."))
                ModelState.AddModelError(string.Empty, error);
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task ResolveAsync(CancellationToken cancellationToken)
    {
        Product = await operationalQuery.ResolveProductAsync(Input.ProductCode, cancellationToken: cancellationToken);
        Source = await operationalQuery.ResolveLocationAsync(Input.WipCode, cancellationToken: cancellationToken);
        Destination = Input.Action == WipProcessAction.WarehouseReturn
            ? await operationalQuery.ResolveLocationAsync(Input.DestinationCode, cancellationToken: cancellationToken)
            : null;
        if (Product is not null && Source is not null)
            SourceBalance = await inventoryQuery.GetBalanceAsync(Product.Id, Source.Id, cancellationToken);
    }

    private void ValidateResolved()
    {
        if (Input.OperationId == Guid.Empty)
            ModelState.AddModelError(string.Empty, "La operación no es válida.");
        if (Product is null)
            ModelState.AddModelError("Input.ProductCode", "El producto no existe o está inactivo.");
        if (Source is null || !Source.IsWip)
            ModelState.AddModelError("Input.WipCode", "Selecciona una ubicación WIP disponible.");
        if (Input.Action == WipProcessAction.WarehouseReturn && Destination is null)
            ModelState.AddModelError("Input.DestinationCode", "Selecciona la ubicación destino.");
        else if (Input.Action == WipProcessAction.WarehouseReturn && Destination!.IsWip)
            ModelState.AddModelError("Input.DestinationCode", "El regreso a bodega requiere un destino no WIP.");
        if (Input.Action == WipProcessAction.WarehouseReturn && Source?.Id == Destination?.Id)
            ModelState.AddModelError("Input.DestinationCode", "Origen y destino deben ser distintos.");
        if (Input.Quantity <= 0 || decimal.Round(Input.Quantity, 4) != Input.Quantity)
            ModelState.AddModelError("Input.Quantity", "La cantidad debe ser positiva y admitir como máximo cuatro decimales.");
        if (Input.Action == WipProcessAction.SupplierReturn && string.IsNullOrWhiteSpace(Input.Reference))
            ModelState.AddModelError("Input.Reference", "La referencia es obligatoria para devolver a proveedor.");
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        WipLocations = await dbContext.Locations.AsNoTracking()
            .Where(item => item.IsPhysicallyPresent && item.IsActive && !item.IsBlocked &&
                item.OperationalRole == LocationOperationalRole.Wip)
            .OrderBy(item => item.Code)
            .Select(item => new WipOption(item.Code, item.Description))
            .ToListAsync(cancellationToken);
    }

    public sealed class InputModel
    {
        public Guid OperationId { get; set; }
        public WipProcessAction Action { get; set; }
        [Required, StringLength(100)] public string WipCode { get; set; } = string.Empty;
        [Required, StringLength(160)] public string ProductCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        [StringLength(100)] public string? DestinationCode { get; set; }
        [StringLength(120)] public string? Reference { get; set; }
        [StringLength(500)] public string? Notes { get; set; }
        [Required] public string Pin { get; set; } = string.Empty;
        public List<Guid> ApprovedSharedLocationIds { get; set; } = [];
    }

    public sealed record WipOption(string Code, string? Description);
}

public enum WipProcessAction
{
    Consumption,
    WarehouseReturn,
    SupplierReturn
}
