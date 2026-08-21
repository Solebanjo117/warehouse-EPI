using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class CreateModel(WarehouseDbContext dbContext, CycleCountService cycleCountService) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<LocationOption> Locations { get; private set; } = [];
    public IReadOnlyList<string> Rows => Locations.Where(item => item.RowCode is not null).Select(item => item.RowCode!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToArray();
    public IReadOnlyList<short> Racks => Locations.Where(item => item.RackNumber is not null).Select(item => item.RackNumber!.Value).Distinct().OrderBy(item => item).ToArray();
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) { await LoadAsync(cancellationToken); return Page(); }
        var result = await cycleCountService.CreateAsync(new(Input.Pin, Input.Title, Input.Notes, Input.LocationIds, Input.RowCodes, Input.RackNumbers, Input.OperationId), cancellationToken);
        if (result.Status == CycleCountStatus.Success && result.CampaignId is Guid id) return RedirectToPage("Details", new { id });
        Error = result.Status == CycleCountStatus.InvalidPin ? "No fue posible validar el NIP." : string.Join(' ', result.ValidationErrors);
        await LoadAsync(cancellationToken);
        return Page();
    }
    private async Task LoadAsync(CancellationToken cancellationToken) => Locations = await dbContext.Locations.AsNoTracking().Where(item => item.IsActive && !item.IsBlocked && item.TracksInventory).OrderBy(item => item.RowCode).ThenBy(item => item.RackNumber).ThenBy(item => item.PalletNumber).ThenBy(item => item.Code).Select(item => new LocationOption(item.Id, item.Code, item.Description, item.RowCode, item.RackNumber)).ToListAsync(cancellationToken);
    public sealed class InputModel { public Guid OperationId { get; set; } [StringLength(160)] public string? Title { get; set; } [StringLength(500)] public string? Notes { get; set; } public List<Guid> LocationIds { get; set; } = []; public List<string> RowCodes { get; set; } = []; public List<short> RackNumbers { get; set; } = []; [Required] public string Pin { get; set; } = string.Empty; }
    public sealed record LocationOption(Guid Id, string Code, string? Description, string? RowCode, short? RackNumber);
}
