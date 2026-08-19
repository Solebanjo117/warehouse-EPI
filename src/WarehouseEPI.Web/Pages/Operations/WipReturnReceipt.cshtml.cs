using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class WipReturnReceiptModel(WarehouseDbContext dbContext, WarehouseClock clock) : PageModel
{
    public ReceiptRow Receipt { get; private set; } = null!;
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        var row = await dbContext.WipDispositions.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new ReceiptRow(item.Id, item.OriginalMovementLine.Product.Sku,
                item.OriginalMovementLine.Unit.Code, item.OriginalMovementLine.Movement.OperationalArea!.Code,
                item.Quantity, item.ResponsibleUser.FullName, item.Reference, item.Notes, item.OccurredAt))
            .SingleOrDefaultAsync(token);
        if (row is null) return NotFound();
        Receipt = row with { OccurredAt = await clock.ConvertAsync(row.OccurredAt, token) };
        return Page();
    }
    public sealed record ReceiptRow(Guid Id, string Sku, string Unit, string Wip, decimal Quantity,
        string Responsible, string? Reference, string? Notes, DateTimeOffset OccurredAt);
}
