using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionReportFilter(Guid WeekId, DateOnly Date, bool Products = false,
    bool FullWeek = false, string? Sku = null, string? Reference = null, ProductionDailyArea? Area = null);
public sealed record ProductionReportArea(ProductionDailyArea Area, bool Applies, decimal Opening,
    decimal Shift1, decimal PendingAfterShift1, decimal Shift2, decimal Others, decimal Total,
    decimal Pending, decimal PlannedCarryover);
public sealed record ProductionReportRow(Guid? ProductId, string Sku, string? Description, string Unit,
    DateOnly? Date, decimal Planned, IReadOnlyList<ProductionReportArea> Areas);
public sealed record ProductionReport(ProductionReportFilter Filter, DateOnly WeekStart, DateOnly WeekEnd,
    ProductionScheduleWeekStatus Status, IReadOnlyList<ProductionReportRow> Rows,
    IReadOnlyList<ProductionReportRow> Totals, IReadOnlyList<ProductionDailyArea> Areas,
    IReadOnlySet<ProductionDailyArea> OtherShiftAreas, string Shift1Name, string Shift2Name)
{
    public const int OutputLimit = 10_000;
    public bool CanOutput => Rows.Count <= OutputLimit;
}

/// <summary>Read-only projections of the same effective quantities used by the balance editor.</summary>
public sealed class ProductionReportService(WarehouseDbContext db, ProductionDailyBalanceService balances)
{
    public async Task<ProductionReport?> GetAsync(ProductionReportFilter filter, CancellationToken token = default)
    {
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == filter.WeekId, token);
        if (week is null) return null;
        var end = week.WeekStart.AddDays(ProductionWeekCalendar.LastDayOffset);
        filter = filter with { Date = filter.Date < week.WeekStart ? week.WeekStart : filter.Date > end ? end : filter.Date,
            Sku = filter.Sku?.Trim(), Reference = filter.Reference?.Trim() };
        var query = new ProductionWeeklyFilter(end, filter.Sku, filter.Reference, filter.Area);
        var weekly = (await balances.GetWeeklyAsync(week.Id, query, token))!;
        var daily = filter.Products && !filter.FullWeek
            ? await balances.GetDailySummaryAsync(week.Id, query with { Through = filter.Date }, token) : null;
        var dailyProducts = daily?.Products.ToDictionary(x => x.ProductId);
        var ids = weekly.Products.Select(x => x.ProductId).ToArray();
        var units = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, Unit = x.BaseUnit.Code }).ToDictionaryAsync(x => x.Id, x => x.Unit, token);
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var shifts = await db.ProductionShifts.AsNoTracking().Where(x => x.Id == config.Shift1Id || x.Id == config.Shift2Id)
            .ToDictionaryAsync(x => x.Id, x => x.Name, token);
        var areas = Enum.GetValues<ProductionDailyArea>().Where(x => filter.Area is null || filter.Area == x).ToArray();
        var rows = new List<ProductionReportRow>();
        foreach (var product in weekly.Products)
        {
            var dates = filter.Products ? new DateOnly?[] { filter.FullWeek ? null : filter.Date }
                : ProductionWeekCalendar.Days(week.WeekStart).Select(x => (DateOnly?)x).ToArray();
            foreach (var date in dates)
            {
                var captures = product.Days.Where(x => date is null || x.Date == date).SelectMany(x => x.Shifts).ToArray();
                var day = dailyProducts?.GetValueOrDefault(product.ProductId);
                var values = areas.Select(area =>
                {
                    var balance = Select(area, day?.Cutting ?? product.Cutting, day?.Sewing ?? product.Sewing,
                        day?.ReadyToPack ?? product.ReadyToPack);
                    var quantities = captures.Where(x => x.Area == area).ToArray();
                    var t1 = quantities.Where(x => x.ShiftId == config.Shift1Id).Sum(x => x.Quantity);
                    var t2 = quantities.Where(x => x.ShiftId == config.Shift2Id).Sum(x => x.Quantity);
                    var others = quantities.Where(x => x.ShiftId != config.Shift1Id && x.ShiftId != config.Shift2Id).Sum(x => x.Quantity);
                    return new ProductionReportArea(area, balance.Applies || quantities.Length > 0, balance.Opening,
                        t1, balance.PendingAfterShift1, t2, others, t1 + t2 + others,
                        balance.SignedPending,
                        product.Intentions.Where(x => x.Area == area && (date is null || x.Date == date)).Sum(x => x.Quantity));
                }).ToArray();
                rows.Add(new(product.ProductId, product.Sku, product.Description, units[product.ProductId], date,
                    date is null ? product.Planned : product.Days.Single(x => x.Date == date).Planned, values));
            }
        }
        if (!filter.Products)
            rows = rows.GroupBy(x => x.Date).OrderBy(x => x.Key)
                .Select(x => Aggregate(x, "", x.Key, areas)).ToList();
        else
            rows = rows.OrderBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ProductId).ToList();
        var totals = filter.Products
            ? rows.GroupBy(x => x.Unit, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key)
                .Select(x => Aggregate(x, x.Key, null, areas)).ToArray()
            : rows.Count == 0 ? [] : new[] { Aggregate(rows, "", null, areas) };
        var otherAreas = rows.SelectMany(x => x.Areas).Where(x => x.Others != 0).Select(x => x.Area).ToHashSet();
        return new(filter, week.WeekStart, end, week.Status, rows, totals, areas, otherAreas,
            config.Shift1Id is Guid first ? shifts.GetValueOrDefault(first, "—") : "—",
            config.Shift2Id is Guid second ? shifts.GetValueOrDefault(second, "—") : "—");
    }

    private static ProductionDailyAreaBalance Select(ProductionDailyArea area, ProductionDailyAreaBalance cutting,
        ProductionDailyAreaBalance sewing, ProductionDailyAreaBalance ready) => area switch
        { ProductionDailyArea.Cutting => cutting, ProductionDailyArea.Sewing => sewing, _ => ready };

    private static ProductionReportRow Aggregate(IEnumerable<ProductionReportRow> source, string unit, DateOnly? date,
        IReadOnlyList<ProductionDailyArea> areas)
    {
        var rows = source.ToArray();
        return new(null, "", null, unit, date, rows.Sum(x => x.Planned), areas.Select(area =>
        {
            var values = rows.SelectMany(x => x.Areas).Where(x => x.Area == area && x.Applies).ToArray();
            return new ProductionReportArea(area, values.Length > 0, values.Sum(x => x.Opening), values.Sum(x => x.Shift1),
                values.Sum(x => x.PendingAfterShift1), values.Sum(x => x.Shift2), values.Sum(x => x.Others),
                values.Sum(x => x.Total), values.Sum(x => x.Pending), values.Sum(x => x.PlannedCarryover));
        }).ToArray());
    }
}
