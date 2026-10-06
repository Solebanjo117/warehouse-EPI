using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionInitialBalanceChange(Guid ProductId, ProductionDailyArea Area,
    decimal Quantity, uint ExpectedVersion);
public sealed record ProductionInitialBalanceView(Guid ProductId, string Sku, string Unit, bool AllowsDecimals,
    ProductionDailyArea Area, [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] decimal Quantity, uint Version);

public sealed class ProductionInitialBalanceService(WarehouseDbContext db)
{
    public async Task<IReadOnlyList<ProductionInitialBalanceView>> GetAsync(Guid weekId, CancellationToken token = default)
    {
        var balance = await new ProductionDailyBalanceService(db).GetAsync(weekId, token);
        if (balance is null) return [];
        var saved = await db.ProductionInitialBalances.AsNoTracking().Where(x => x.WeekId == weekId).ToListAsync(token);
        var ids = balance.Rows.Select(x => x.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Include(x => x.BaseUnit).Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        return balance.Rows.Where(x => x.Date == balance.WeekStart).SelectMany(row =>
            new[] { row.Cutting, row.Sewing, row.ReadyToPack }.Select(area =>
            {
                var product = products[row.ProductId];
                var total = saved.SingleOrDefault(x => x.ProductId == row.ProductId && x.Area == area.Area);
                return new ProductionInitialBalanceView(row.ProductId, row.Sku, product.BaseUnit.Code,
                    product.BaseUnit.AllowsDecimals, area.Area,
                    total?.Quantity ?? area.SignedPending + area.Completed - area.ProgrammedToday,
                    total?.Version ?? 0);
            })).ToArray();
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(Guid? weekId,
        IReadOnlyList<ProductionInitialBalanceChange> changes, CancellationToken token = default)
    {
        if (changes.Count == 0) return [];
        if (changes.Select(x => (x.ProductId, x.Area)).Distinct().Count() != changes.Count)
            return ["Hay arrastres repetidos para el mismo SKU y área."];
        if (weekId.HasValue && !await db.ProductionScheduleWeeks.AnyAsync(x => x.Id == weekId && x.Status != ProductionScheduleWeekStatus.Closed, token))
            return ["La semana no admite cambios de arrastre inicial."];
        var ids = changes.Select(x => x.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Include(x => x.BaseUnit).Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        var saved = await db.ProductionInitialBalances.AsNoTracking().Where(x => x.WeekId == weekId && ids.Contains(x.ProductId)).ToListAsync(token);
        var errors = new List<string>();
        foreach (var change in changes)
        {
            if (!Enum.IsDefined(change.Area) || !products.TryGetValue(change.ProductId, out var product) || !product.IsActive ||
                change.Quantity < 0 || change.Quantity > 99999999999999.9999m || decimal.Round(change.Quantity, 4) != change.Quantity ||
                !product.BaseUnit.AllowsDecimals && decimal.Truncate(change.Quantity) != change.Quantity)
                errors.Add("Revisa SKU, área y cantidad del arrastre inicial.");
            var current = saved.SingleOrDefault(x => x.ProductId == change.ProductId && x.Area == change.Area);
            if ((current?.Version ?? 0) != change.ExpectedVersion)
                errors.Add("El arrastre inicial cambió. Actualiza y revisa de nuevo.");
        }
        return errors;
    }

    public async Task ApplyAsync(Guid weekId, IReadOnlyList<ProductionInitialBalanceChange> changes, CancellationToken token)
    {
        foreach (var change in changes)
        {
            var row = await db.ProductionInitialBalances.SingleOrDefaultAsync(x => x.WeekId == weekId && x.ProductId == change.ProductId && x.Area == change.Area, token);
            if (row is null)
            {
                row = new() { WeekId = weekId, ProductId = change.ProductId, Area = change.Area };
                db.ProductionInitialBalances.Add(row);
            }
            row.Quantity = change.Quantity;
            row.Version++;
        }
    }
}
