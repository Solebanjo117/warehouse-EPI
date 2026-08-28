using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class CreateModel(WarehouseDbContext dbContext, CycleCountService cycleCountService) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<LocationOption> Locations { get; private set; } = [];
    public IReadOnlyList<LocationRowGroup> RowGroups { get; private set; } = [];
    public IReadOnlyList<LocationOption> AreaLocations { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Input.OperationId = Guid.NewGuid();
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var pin = Input.Pin;
        Input.Pin = string.Empty;
        if (!ModelState.IsValid) { await LoadAsync(cancellationToken); return Page(); }
        var result = await cycleCountService.CreateAsync(new(pin, Input.Title, Input.Notes, Input.LocationIds, Input.RowCodes, Input.RackNumbers, Input.OperationId), cancellationToken);
        if (result.Status == CycleCountStatus.Success && result.CampaignId is Guid id) return RedirectToPage("Details", new { id });
        Error = result.Status == CycleCountStatus.InvalidPin ? "No fue posible validar el NIP." : string.Join(' ', result.ValidationErrors);
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Locations = await dbContext.Locations.AsNoTracking()
            .Where(item => item.IsPhysicallyPresent && item.IsActive && !item.IsBlocked)
            .OrderBy(item => item.RowCode).ThenBy(item => item.RackNumber).ThenBy(item => item.PalletNumber).ThenBy(item => item.Code)
            .Select(item => new LocationOption(item.Id, item.Code, item.Description, item.Kind, item.RowCode, item.RackNumber, item.PalletNumber))
            .ToListAsync(cancellationToken);

        RowGroups = Locations
            .Where(item => item.Kind == LocationKind.Rack && item.RowCode is not null && item.RackNumber is not null)
            .GroupBy(item => item.RowCode!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(row => new LocationRowGroup(
                row.Key,
                row.GroupBy(item => item.RackNumber!.Value)
                    .OrderBy(rack => rack.Key)
                    .Select(rack => new LocationRackGroup(
                        row.Key,
                        rack.Key,
                        rack.OrderBy(item => PalletOrder(item.PalletNumber)).ThenBy(item => item.Code).ToArray()))
                    .ToArray()))
            .ToArray();

        AreaLocations = Locations
            .Where(item => item.Kind != LocationKind.Rack || item.RowCode is null || item.RackNumber is null)
            .OrderBy(item => item.Code)
            .ToArray();
    }

    private static int PalletOrder(short? palletNumber) => palletNumber switch
    {
        7 => 0, 8 => 1, 9 => 2,
        4 => 3, 5 => 4, 6 => 5,
        1 => 6, 2 => 7, 3 => 8,
        null => int.MaxValue,
        _ => 100 + palletNumber.Value
    };

    public sealed class InputModel { public Guid OperationId { get; set; } [StringLength(160)] public string? Title { get; set; } [StringLength(500)] public string? Notes { get; set; } public List<Guid> LocationIds { get; set; } = []; public List<string> RowCodes { get; set; } = []; public List<short> RackNumbers { get; set; } = []; [Required] public string Pin { get; set; } = string.Empty; }
    public sealed record LocationOption(Guid Id, string Code, string? Description, LocationKind Kind, string? RowCode, short? RackNumber, short? PalletNumber);
    public sealed record LocationRackGroup(string RowCode, short RackNumber, IReadOnlyList<LocationOption> Locations);
    public sealed record LocationRowGroup(string RowCode, IReadOnlyList<LocationRackGroup> Racks)
    {
        public int LocationCount => Racks.Sum(item => item.Locations.Count);
    }
}
