using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class CountModel(CycleCountService cycleCountService, WarehouseDbContext dbContext) : PageModel
{
    public CycleCountAttemptView? Attempt { get; private set; }
    [BindProperty] public InputModel Input { get; set; } = new();
    public Guid CampaignId { get; private set; }
    public Guid LocationId { get; private set; }
    public string? Error { get; private set; }
    public async Task<IActionResult> OnGetAsync(Guid id, Guid locationId, Guid attemptId, CancellationToken cancellationToken) { CampaignId=id; LocationId=locationId; Attempt=await cycleCountService.GetAttemptAsync(attemptId, false, cancellationToken); return Attempt is null ? NotFound() : Page(); }
    public async Task<IActionResult> OnPostAsync(Guid id, Guid locationId, CancellationToken cancellationToken)
    {
        CampaignId=id; LocationId=locationId; Attempt=await cycleCountService.GetAttemptAsync(Input.AttemptId, false, cancellationToken); if(Attempt is null) return NotFound();
        var entries = Input.Entries.Select(item => new CycleCountQuantityCommand(item.ProductId, item.Quantity)).ToList();
        var unexpected = Input.UnexpectedEntries.Where(item => !string.IsNullOrWhiteSpace(item.Code)).ToArray();
        foreach (var item in unexpected)
        {
            var code = item.Code!.Trim();
            if (item.Quantity is null)
            {
                Error = $"Captura la cantidad física del producto inesperado {code}, incluso si es cero.";
                return Page();
            }
            var normalized = code.ToUpperInvariant();
            var productId = await dbContext.Products.AsNoTracking()
                .Where(product => product.Sku.ToUpper() == normalized || product.Barcodes.Any(barcode => barcode.IsActive && barcode.Barcode == code))
                .Select(product => (Guid?)product.Id).SingleOrDefaultAsync(cancellationToken);
            if (productId is null)
            {
                Error = $"No se encontró el producto inesperado {code}.";
                return Page();
            }
            entries.Add(new(productId.Value, item.Quantity.Value));
        }
        var result=await cycleCountService.SubmitAsync(new(Input.AttemptId, Input.OperationId, Input.Pin, entries,Input.IsLocationEmpty),cancellationToken);
        if(result.Status==CycleCountStatus.Success) return RedirectToPage("Review",new { id, locationId });
        Error=result.Status==CycleCountStatus.InvalidPin?"NIP no válido.":string.Join(' ',result.ValidationErrors); return Page();
    }
    public sealed class InputModel { public Guid AttemptId {get;set;} public Guid OperationId {get;set;} [Required] public string Pin {get;set;}=string.Empty; public bool IsLocationEmpty {get;set;} public List<EntryInput> Entries {get;set;}=[]; public List<UnexpectedEntryInput> UnexpectedEntries { get; set; } = []; }
    public sealed class EntryInput { public Guid ProductId {get;set;} public decimal Quantity {get;set;} }
    public sealed class UnexpectedEntryInput { public string? Code { get; set; } public decimal? Quantity { get; set; } }
}
