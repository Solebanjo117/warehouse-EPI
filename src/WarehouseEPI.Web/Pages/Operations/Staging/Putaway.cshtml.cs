using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations.Staging;

public sealed class PutawayModel(InventoryMovementService movements, InventoryQueryService inventory,
    OperationalInventoryQueryService operational, WarehouseDbContext db, IStringLocalizer<OperationsTexts> texts)
    : OperationPageModel(movements, inventory, operational, texts)
{
    [BindProperty(SupportsGet = true)] public Guid Arrival { get; set; }
    [BindProperty] public string Version { get; set; } = "";
    public override InventoryMovementType MovementType => InventoryMovementType.Transfer;
    public override bool FixedTransfer => true;
    public override bool ShowDestinationProposals => true;
    public override string ReturnPage => "/Operations/Staging/Index";
    public override Guid? ArrivalLineId => Arrival;
    public override string ArrivalVersion => Version;
    public override string PageTitle => "Acomodar llegada de staging";
    public override string PageHelp => "Selecciona el destino para mover todo el pendiente de esta llegada.";

    public override async Task<IActionResult> OnGetAsync(Guid? productId, Guid? sourceLocationId, Guid? destinationLocationId,
        Guid? locationId, string? mode, CancellationToken cancellationToken = default)
    {
        var arrival = await new StagingArrivalQuery(db).GetAsync(Arrival, cancellationToken);
        if (arrival is null || arrival.NeedsIdentification || arrival.Pending <= 0) return RedirectToPage(ReturnPage);
        Version = arrival.Version;
        var result = await base.OnGetAsync(arrival.ProductId, arrival.LocationId, null, null, null, cancellationToken);
        Input.Quantity = arrival.Pending;
        await LoadProposalsAsync(arrival.ProductId, arrival.LocationId, cancellationToken);
        return result;
    }

    public override async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var arrival = await new StagingArrivalQuery(db).GetAsync(Arrival, cancellationToken);
        var snapshot = StagingArrivalQuery.DecodeVersion(Version);
        if (arrival is null || snapshot is null) return BadRequest();
        // Posted product, source and quantity never decide which material will move.
        Input.ProductId = arrival.ProductId;
        Input.SourceLocationId = arrival.LocationId;
        Input.Quantity = snapshot.Sum(p => p.Quantity);
        Input.Reference = null;
        Input.Notes = null;
        foreach (var field in new[] { "ProductId", "SourceLocationId", "Quantity", "Reference", "Notes" })
            ModelState.Remove($"Input.{field}");
        var result = await base.OnPostAsync(cancellationToken);
        if (result is PageResult)
            await LoadProposalsAsync(arrival.ProductId, arrival.LocationId, cancellationToken);
        return result;
    }

    private async Task LoadProposalsAsync(Guid productId, Guid sourceId, CancellationToken token)
    {
        DestinationProposals = (await OperationalQuery.GetProductLocationsAsync(productId, cancellationToken: token))
            .Where(location => location.Id != sourceId && location.Quantity > 0)
            .OrderBy(location => location.Code, StringComparer.Ordinal).ToArray();
    }

    protected override Task<InventoryMovementResult> ConfirmMovementAsync(InventoryMovementCommand command, CancellationToken token) =>
        MovementService.ConfirmStagingAsync(new(Input.OperationId, Arrival, Input.DestinationLocationId!.Value,
            Version, Input.Pin, Input.ApprovedSharedLocationIds), token);

    protected override IActionResult MovementCompleted(Guid movementId) => RedirectToPage(ReturnPage, new { receipt = movementId });
}
