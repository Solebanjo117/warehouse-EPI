using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class WipProcessModel(
    WarehouseDbContext dbContext,
    WipDocumentService documents,
    OperationalInventoryQueryService operationalQuery,
    ProductionMaterialService productionMaterials, IStringLocalizer<OperationsTexts> texts) : PageModel
{
    [TempData] public string? Message { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<WipOption> WipLocations { get; private set; } = [];
    public IReadOnlyList<SharedLocationConflict> SharingConflicts { get; private set; } = [];
    public OperationalProductResult? Product { get; private set; }
    public OperationalLocationResult? Source { get; private set; }
    public OperationalLocationResult? Destination { get; private set; }
    public InventoryBalanceSnapshot? SourceBalance { get; private set; }
    public ProductionMaterialAvailability? Availability { get; private set; }

    public async Task OnGetAsync(string? action, string? wipCode, string? productCode, CancellationToken cancellationToken, Guid? documentId = null)
    {
        Input.OperationId = Guid.NewGuid();
        Input.DocumentId = documentId;
        Input.Action = action?.ToLowerInvariant() switch
        {
            "return" => WipProcessAction.WarehouseReturn,
            "supplier" => WipProcessAction.SupplierReturn,
            "scrap" => WipProcessAction.Scrap,
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

        var kind = Input.Action switch
        {
            WipProcessAction.WarehouseReturn => WipDocumentApplicationKind.WarehouseReturn,
            WipProcessAction.SupplierReturn => WipDocumentApplicationKind.SupplierReturn,
            WipProcessAction.Scrap => WipDocumentApplicationKind.Scrap,
            _ => WipDocumentApplicationKind.Consumption
        };
        var result = await documents.ConfirmAsync(new(Input.OperationId, Product!.Id, Source!.Id,
            Input.Quantity, kind, pin, Destination?.Id, DocumentId: Input.DocumentId, Reference: Input.Reference, Notes: Input.Notes,
            ApproveSharedDestination: Destination is not null && Input.ApprovedSharedLocationIds.Contains(Destination.Id)), cancellationToken);
        if (result.Status == InventoryMovementStatus.Success)
        {
            if (result.MovementId is Guid movementId) return RedirectToPage("/Operations/Receipt", new { id = movementId });
            Message = "Operación documental registrada. Las existencias del almacén no cambiaron.";
            return RedirectToPage(new { wipCode = Source.Code, productCode = Product.Sku, documentId = Input.DocumentId });
        }

        if (result.Status == InventoryMovementStatus.InvalidPin)
            ModelState.AddModelError(string.Empty, texts["No fue posible validar el NIP o el usuario."]);
        else if (result.Status == InventoryMovementStatus.RequiresLocationSharingConfirmation)
        {
            SharingConflicts = result.Conflicts;
            Input.ApprovedSharedLocationIds = [];
        }
        else
            foreach (var error in result.ValidationErrors.DefaultIfEmpty("No fue posible procesar el WIP."))
                ModelState.AddModelError(string.Empty, texts[error]);
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
        {
            Availability = await productionMaterials.GetAvailabilityAsync(Product.Id, Source.Id, cancellationToken);
        }
    }

    private void ValidateResolved()
    {
        if (!Enum.IsDefined(Input.Action))
            ModelState.AddModelError("Input.Action", texts["La operación no es válida."]);
        if (Input.OperationId == Guid.Empty)
            ModelState.AddModelError(string.Empty, texts["La operación no es válida."]);
        if (Product is null)
            ModelState.AddModelError("Input.ProductCode", texts["El producto no existe o está inactivo."]);
        if (Source is null || !Source.IsWip)
            ModelState.AddModelError("Input.WipCode", texts["Selecciona una ubicación WIP disponible."]);
        if (Input.Action == WipProcessAction.WarehouseReturn && Destination is null)
            ModelState.AddModelError("Input.DestinationCode", texts["Selecciona la ubicación destino."]);
        else if (Input.Action == WipProcessAction.WarehouseReturn && Destination!.IsWip)
            ModelState.AddModelError("Input.DestinationCode", texts["El regreso a bodega requiere un destino no WIP."]);
        if (Input.Action == WipProcessAction.WarehouseReturn && Source?.Id == Destination?.Id)
            ModelState.AddModelError("Input.DestinationCode", texts["Origen y destino deben ser distintos."]);
        if (Input.Quantity <= 0 || decimal.Round(Input.Quantity, 4) != Input.Quantity)
            ModelState.AddModelError("Input.Quantity", texts["La cantidad debe ser positiva y admitir como máximo cuatro decimales."]);
        if (Input.Action == WipProcessAction.SupplierReturn && string.IsNullOrWhiteSpace(Input.Reference))
            ModelState.AddModelError("Input.Reference", texts["La referencia es obligatoria para devolver a proveedor."]);
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
        public Guid? DocumentId { get; set; }
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
    SupplierReturn,
    Scrap
}
