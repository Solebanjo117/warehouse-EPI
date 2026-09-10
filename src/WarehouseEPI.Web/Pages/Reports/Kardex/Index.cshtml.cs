using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Reports.Kardex;

public sealed class IndexModel(
    KardexReportService kardexService,
    KardexExportService exportService,
    WarehouseClock clock,
    WarehouseSettingsService settingsService,
    WarehouseDbContext dbContext,
    TimeProvider? timeProvider = null) : PageModel
{
    private const int PageSize = 25;

    public KardexResult? Kardex { get; private set; }
    public string? Sku { get; private set; }
    public Guid? LocationId { get; private set; }
    public string Period { get; private set; } = "30";
    public DateOnly? From { get; private set; }
    public DateOnly? To { get; private set; }
    public string TimeZoneId { get; private set; } = string.Empty;
    public string? ErrorMessage { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public DateTimeOffset CurrentAtLocal { get; private set; }
    public IReadOnlyList<SelectListItem> LocationOptions { get; private set; } = [];
    public IReadOnlyList<Product> MatchingProducts { get; private set; } = [];

    public async Task OnGetAsync(
        string? sku,
        Guid? locationId,
        string? period = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        await LoadLocationsAsync(cancellationToken);
        await SetRequestStateAsync(sku, locationId, period, from, to, cancellationToken);
        PageNumber = Math.Max(1, pageNumber);

        if (string.IsNullOrWhiteSpace(Sku))
            return;

        var product = await ResolveProductAsync(Sku, cancellationToken);

        if (product is null)
        {
            // Try searching partial matches to help user
            MatchingProducts = await dbContext.Products
                .AsNoTracking()
                .Where(p => p.Sku.ToUpper().Contains(Sku) ||
                    (p.Description != null && p.Description.ToUpper().Contains(Sku)) ||
                    (p.ExternalReference != null && p.ExternalReference.ToUpper().Contains(Sku)) ||
                    p.Barcodes.Any(b => b.IsActive && b.Barcode.ToUpper().Contains(Sku)))
                .OrderBy(p => p.Sku)
                .Take(10)
                .ToListAsync(cancellationToken);

            ErrorMessage = $"No se encontró una coincidencia exacta para '{Sku}'. Elige un producto de la lista.";
            return;
        }

        Sku = product.Sku;
        var interval = await clock.GetUtcIntervalAsync(From, To, cancellationToken);
        Kardex = await kardexService.GetKardexPageAsync(
            new KardexFilter(product.Id, LocationId, interval.FromInclusive, interval.ToExclusive,
                PageNumber: PageNumber, PageSize: PageSize, TimeZoneId: TimeZoneId,
                IncludeCorrectionDetails: User.IsInRole("ADMIN")),
            cancellationToken);
        PageNumber = Kardex?.PageNumber ?? PageNumber;
        CurrentAtLocal = await clock.ConvertAsync((timeProvider ?? TimeProvider.System).GetUtcNow(), cancellationToken);
    }

    public async Task<IActionResult> OnGetProductsAsync(string? q, CancellationToken cancellationToken = default)
    {
        var original = q?.Trim() ?? string.Empty;
        if (original.Length == 0)
            return new JsonResult(Array.Empty<KardexProductSuggestion>());

        var normalized = original.ToUpperInvariant();
        var items = await dbContext.Products.AsNoTracking()
            .Where(p => p.Sku.ToUpper().Contains(normalized) ||
                (p.Description != null && p.Description.ToUpper().Contains(normalized)) ||
                (p.ExternalReference != null && p.ExternalReference.ToUpper().Contains(normalized)) ||
                p.Barcodes.Any(b => b.IsActive && b.Barcode.ToUpper().Contains(normalized)))
            .OrderByDescending(p => p.Sku.ToUpper() == normalized ||
                p.Barcodes.Any(b => b.IsActive && b.Barcode == original))
            .ThenBy(p => p.Sku)
            .Take(10)
            .Select(p => new KardexProductSuggestion(p.Sku, p.Description, p.ExternalReference, p.IsActive))
            .ToListAsync(cancellationToken);
        return new JsonResult(items);
    }

    public async Task<IActionResult> OnGetExportAsync(
        string format,
        string? sku,
        Guid? locationId,
        string? period = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        if (!User.IsInRole("ADMIN"))
            return Forbid();

        if (format is not ("xlsx" or "csv"))
            return BadRequest("El formato de exportación debe ser xlsx o csv.");

        await SetRequestStateAsync(sku, locationId, period, from, to, cancellationToken);

        if (string.IsNullOrWhiteSpace(Sku))
            return BadRequest("Debes especificar un SKU para exportar el Kardex.");

        var product = await ResolveProductAsync(Sku, cancellationToken);

        if (product is null)
            return NotFound($"Producto con SKU '{Sku}' no encontrado.");

        var interval = await clock.GetUtcIntervalAsync(From, To, cancellationToken);
        KardexResult? result;
        try
        {
            result = await kardexService.GetKardexExportAsync(
                new KardexFilter(product.Id, LocationId, interval.FromInclusive, interval.ToExclusive,
                    TimeZoneId: TimeZoneId, IncludeCorrectionDetails: true),
                token: cancellationToken);
        }
        catch (KardexExportLimitExceededException exception)
        {
            return BadRequest(exception.Message);
        }

        if (result is null)
            return NotFound("No fue posible generar el Kardex.");

        var nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var localNow = await clock.ConvertAsync(nowUtc, cancellationToken);
        var fileName = $"kardex-{product.Sku}-{localNow:yyyyMMdd-HHmmss}";

        if (format == "xlsx")
        {
            var bytes = await exportService.ToExcelAsync(result, nowUtc, cancellationToken);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{fileName}.xlsx");
        }
        else
        {
            var bytes = await exportService.ToCsvAsync(result, nowUtc, cancellationToken);
            return File(bytes, "text/csv; charset=utf-8", $"{fileName}.csv");
        }
    }

    private async Task<Product?> ResolveProductAsync(string code, CancellationToken cancellationToken)
    {
        var original = code.Trim();
        var normalized = original.ToUpperInvariant();
        var matches = await dbContext.Products.AsNoTracking().Include(p => p.BaseUnit)
            .Where(p => p.Sku.ToUpper() == normalized ||
                p.Barcodes.Any(b => b.IsActive && b.Barcode == original))
            .OrderBy(p => p.Sku)
            .Take(2)
            .ToListAsync(cancellationToken);
        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task LoadLocationsAsync(CancellationToken cancellationToken)
    {
        var locations = await dbContext.Locations
            .AsNoTracking()
            .Where(l => l.IsActive)
            .OrderBy(l => l.Code)
            .Select(l => new { l.Id, l.Code })
            .ToListAsync(cancellationToken);

        LocationOptions = locations
            .Select(l => new SelectListItem(l.Code, l.Id.ToString()))
            .ToList();
    }

    private async Task SetRequestStateAsync(
        string? sku,
        Guid? locationId,
        string? period,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        Sku = sku?.Trim().ToUpperInvariant();
        LocationId = locationId;
        TimeZoneId = (await settingsService.GetAsync(cancellationToken)).TimeZoneId;

        var normalizedPeriod = period is "today" or "yesterday" or "this-week" or "last-week" or "7" or "this-month" or "last-month" or "30" or "all" or "custom"
            ? period
            : null;
        Period = normalizedPeriod ?? (from is not null || to is not null ? "custom" : "30");

        var nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var today = await clock.GetDateAsync(nowUtc, cancellationToken);

        if (Period == "all")
        {
            from = null;
            to = null;
        }
        else if (Period != "custom")
        {
            if (Period == "today")
            {
                from = today;
                to = today;
            }
            else if (Period == "yesterday")
            {
                from = today.AddDays(-1);
                to = today.AddDays(-1);
            }
            else if (Period == "this-week")
            {
                var daysToMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
                from = today.AddDays(-daysToMonday);
                to = today;
            }
            else if (Period == "last-week")
            {
                var daysToMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
                var startOfThisWeek = today.AddDays(-daysToMonday);
                from = startOfThisWeek.AddDays(-7);
                to = startOfThisWeek.AddDays(-1);
            }
            else if (Period == "7")
            {
                from = today.AddDays(-6);
                to = today;
            }
            else if (Period == "this-month")
            {
                from = new DateOnly(today.Year, today.Month, 1);
                to = today;
            }
            else if (Period == "last-month")
            {
                var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
                var lastDayOfLastMonth = firstOfThisMonth.AddDays(-1);
                from = new DateOnly(lastDayOfLastMonth.Year, lastDayOfLastMonth.Month, 1);
                to = lastDayOfLastMonth;
            }
            else // "30"
            {
                from = today.AddDays(-29);
                to = today;
            }
        }

        From = from;
        To = to;
        if (From.HasValue && To.HasValue && From.Value > To.Value)
            (From, To) = (To, From);
    }
}

public sealed record KardexProductSuggestion(string Sku, string? Description, string? ExternalReference, bool IsActive);
