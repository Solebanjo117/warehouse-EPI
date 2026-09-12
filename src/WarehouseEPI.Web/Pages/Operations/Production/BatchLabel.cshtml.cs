using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed class BatchLabelModel(WarehouseDbContext db, BarcodeRenderingService barcodes) : PageModel
{
    public ProductionBatch Batch { get; private set; } = null!;
    public BarcodeSvg Barcode { get; private set; } = null!;
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        var batch = await db.ProductionBatches.AsNoTracking().Include(x => x.WorkOrder).ThenInclude(x => x.Product)
            .SingleOrDefaultAsync(x => x.Id == id, token);
        if (batch is null) return NotFound();
        Batch = batch;
        Barcode = barcodes.RenderCode128Svg(Batch.Number, new Code128RenderOptions(2500, 220));
        return Page();
    }
}
