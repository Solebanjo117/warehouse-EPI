using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Locations;

public sealed record DisplayStock(string LocationCode, short RackNumber, string Sku,
    decimal Quantity, string Unit);

public sealed record DisplaySlide(string RowCode, int Part, int Parts, int RackCount,
    int OccupiedLocations, IReadOnlyList<DisplayStock> Stocks);

public sealed class DisplayModel(WarehouseDbContext db, WarehouseClock clock) : PageModel
{
    private const int ItemsPerSlide = 12;
    public IReadOnlyList<string> AvailableRows { get; private set; } = [];
    public IReadOnlySet<string> SelectedRows { get; private set; } = new HashSet<string>();
    public IReadOnlyList<DisplaySlide> Slides { get; private set; } = [];
    public int Seconds { get; private set; } = 20;
    public bool Play { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset GeneratedAt { get; private set; }
    public string ConfigurationUrl { get; private set; } = "/Locations/Display";

    public async Task OnGetAsync(string[]? rows, int? seconds, bool play = false,
        CancellationToken cancellationToken = default)
    {
        Seconds = seconds is >= 5 and <= 120 ? seconds.Value : 20;
        AvailableRows = await db.Locations.AsNoTracking()
            .Where(location => location.Kind == LocationKind.Rack && location.IsPhysicallyPresent &&
                location.RowCode != null)
            .Select(location => location.RowCode!).Distinct().OrderBy(row => row)
            .ToListAsync(cancellationToken);

        var requested = new HashSet<string>((rows ?? []).Select(row => row.Trim().ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);
        SelectedRows = new HashSet<string>(AvailableRows.Where(row =>
            !play && rows is null || requested.Contains(row)), StringComparer.OrdinalIgnoreCase);
        ConfigurationUrl = "/Locations/Display?seconds=" + Seconds + string.Concat(
            AvailableRows.Where(SelectedRows.Contains).Select(row => "&rows=" + Uri.EscapeDataString(row)));
        Play = play && SelectedRows.Count > 0;
        if (play && !Play) Error = "Selecciona al menos una fila para iniciar la exhibición.";
        if (!Play) return;

        var selectedRows = SelectedRows.ToArray();
        var locations = await db.Locations.AsNoTracking()
            .Where(location => location.Kind == LocationKind.Rack && location.IsPhysicallyPresent &&
                location.RowCode != null && selectedRows.Contains(location.RowCode))
            .Select(location => new { location.Id, location.Code, location.RowCode, location.RackNumber })
            .ToListAsync(cancellationToken);
        var locationIds = locations.Select(location => location.Id).ToArray();
        var balances = await db.InventoryBalances.AsNoTracking()
            .Where(balance => locationIds.Contains(balance.LocationId) && balance.Quantity != 0)
            .Select(balance => new
            {
                balance.LocationId,
                balance.ProductId,
                balance.Product.Sku,
                Unit = balance.Product.BaseUnit.Code,
                balance.Quantity
            }).ToListAsync(cancellationToken);

        var locationById = locations.ToDictionary(location => location.Id);
        var stock = balances.GroupBy(balance => new { balance.LocationId, balance.ProductId })
            .Select(group =>
            {
                var first = group.First();
                var location = locationById[group.Key.LocationId];
                return new
                {
                    Row = location.RowCode!,
                    Stock = new DisplayStock(location.Code, location.RackNumber ?? 0,
                        first.Sku, group.Sum(item => item.Quantity), first.Unit)
                };
            })
            .Where(item => item.Stock.Quantity != 0)
            .ToArray();

        var slides = new List<DisplaySlide>();
        foreach (var row in AvailableRows.Where(SelectedRows.Contains))
        {
            var rowLocations = locations.Where(location => location.RowCode == row).ToArray();
            var rowStock = stock.Where(item => item.Row == row).Select(item => item.Stock)
                .OrderBy(item => item.RackNumber).ThenBy(item => item.LocationCode)
                .ThenBy(item => item.Sku).ToArray();
            var parts = Math.Max(1, (int)Math.Ceiling(rowStock.Length / (double)ItemsPerSlide));
            for (var part = 0; part < parts; part++)
            {
                slides.Add(new DisplaySlide(row, part + 1, parts,
                    rowLocations.Select(location => location.RackNumber).Distinct().Count(),
                    rowStock.Select(item => item.LocationCode).Distinct().Count(),
                    rowStock.Skip(part * ItemsPerSlide).Take(ItemsPerSlide).ToArray()));
            }
        }
        Slides = slides;
        GeneratedAt = await clock.ConvertAsync(DateTimeOffset.UtcNow, cancellationToken);
    }
}
