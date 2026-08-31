using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

public sealed class OperationalAlertService(
    WarehouseDbContext dbContext,
    WarehouseSettingsService settingsService,
    WarehouseClock warehouseClock,
    TimeProvider timeProvider)
{
    public async Task<OperationalAlertSnapshotDto> GetSnapshotAsync(
        OperationalAlertAudience audience,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var generatedAtUtc = timeProvider.GetUtcNow();
        var generatedAtLocal = await warehouseClock.ConvertAsync(generatedAtUtc, cancellationToken);
        var counts = await GetCountsAsync(settings.WipReminderDays, generatedAtUtc, cancellationToken);
        var items = CreateItems(audience, counts);
        return new(audience, generatedAtUtc, generatedAtLocal,
            items.Where(x => x.Severity == OperationalAlertSeverity.Critical).Sum(x => x.Count),
            items.Where(x => x.Severity == OperationalAlertSeverity.Warning).Sum(x => x.Count),
            items.Where(x => x.Severity == OperationalAlertSeverity.Information).Sum(x => x.Count),
            items.Sum(x => x.Count), items);
    }

    public async Task<OperationalAlertPageDto> GetPageAsync(
        OperationalAlertCategory category,
        string? search,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var query = DetailRows(category, settings.WipReminderDays, now);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            if (category is OperationalAlertCategory.CycleCountStale or OperationalAlertCategory.CycleCountPending)
                term = term.TrimStart('C', '-').TrimStart('0');
            query = query.Where(x => x.PrimaryText.ToUpper().Contains(term) || x.SecondaryText.ToUpper().Contains(term));
        }
        var size = Math.Clamp(pageSize, 1, 100);
        var total = await query.CountAsync(cancellationToken);
        var page = Math.Clamp(Math.Max(1, pageNumber), 1, Math.Max(1, (int)Math.Ceiling(total / (double)size)));
        var rows = await query.OrderBy(x => x.PrimaryText).ThenBy(x => x.SecondaryText)
            .Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new(rows.Select(x => ToDetailDto(category, x)).ToArray(), total, page, size);
    }

    /// <summary>
    /// Materializa las ocho condiciones actuales antes de que el centro ADMIN
    /// realice cualquier escritura. Las rutas de alertas siguen siendo de solo lectura.
    /// </summary>
    public async Task<IReadOnlyList<OperationalAlertConditionDto>> GetActiveConditionsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var conditions = new List<OperationalAlertConditionDto>();
        foreach (var category in Enum.GetValues<OperationalAlertCategory>())
        {
            var rows = await DetailRows(category, settings.WipReminderDays, now)
                .OrderBy(row => row.PrimaryText).ThenBy(row => row.SecondaryText)
                .ToListAsync(cancellationToken);
            conditions.AddRange(rows.Select(row => ToCondition(category, row)));
        }

        return conditions.OrderBy(item => item.Category).ThenBy(item => item.ConditionKey, StringComparer.Ordinal).ToArray();
    }

    private async Task<AlertCounts> GetCountsAsync(int wipDays, DateTimeOffset now, CancellationToken token)
    {
        var positions = PositionTotals();
        var negative = await positions.CountAsync(x => x.Quantity < 0, token);
        var unassigned = await positions.CountAsync(x => x.Quantity != 0 && !x.HasAssignment, token);
        var restricted = await positions.CountAsync(x => x.Quantity != 0 && (!x.LocationIsActive || x.LocationIsBlocked), token);
        var minimum = await BelowMinimumProducts().CountAsync(token);
        var stagnant = await StagnantProducts(now.AddDays(-90)).CountAsync(token);
        var stale = await dbContext.CycleCountLocations.AsNoTracking().CountAsync(x => x.Status == CycleCountLocationStatus.Stale, token);
        var pending = await dbContext.CycleCountLocations.AsNoTracking().CountAsync(x => x.Status == CycleCountLocationStatus.UnderReview || x.Status == CycleCountLocationStatus.RecountRequested, token);
        var agedWip = await AgedWipBalances(DateOnly.FromDateTime(now.AddDays(-wipDays).UtcDateTime)).CountAsync(token);
        return new(negative, minimum, unassigned, restricted, stagnant, stale, pending, agedWip, wipDays);
    }

    private static IReadOnlyList<OperationalAlertItemDto> CreateItems(OperationalAlertAudience audience, AlertCounts c)
    {
        var items = new List<OperationalAlertItemDto>
        {
            Item(OperationalAlertCategory.NegativeInventory, OperationalAlertSeverity.Critical, c.Negative,
                "Saldos negativos", "Posiciones producto-ubicación con saldo neto negativo.", audience == OperationalAlertAudience.Admin ? "/Admin/Inventory/Alerts?view=negative" : "/Reports/Inventory?view=exceptions&exception=negative"),
            Item(OperationalAlertCategory.BelowMinimum, OperationalAlertSeverity.Warning, c.Minimum,
                "Productos bajo mínimo", "Productos activos cuya existencia no alcanza el mínimo configurado.", audience == OperationalAlertAudience.Admin ? "/Admin/Inventory/Alerts?view=minimum" : "/Reports/Inventory?view=exceptions&exception=minimum")
        };
        if (audience == OperationalAlertAudience.Admin)
        {
            items.Add(Item(OperationalAlertCategory.UnassignedBalance, OperationalAlertSeverity.Warning, c.Unassigned, "Saldos sin asignación", "Existencia real sin relación activa producto-ubicación.", "/Admin/Inventory/Alerts?view=unassigned"));
            items.Add(Item(OperationalAlertCategory.RestrictedInventory, OperationalAlertSeverity.Critical, c.Restricted, "Inventario restringido", "Existencia en posiciones bloqueadas o inactivas.", "/Admin/Inventory/Alerts?view=restricted"));
            items.Add(Item(OperationalAlertCategory.StagnantInventory, OperationalAlertSeverity.Warning, c.Stagnant, "Inventario estancado", "Productos con existencia y 90 días o más sin salida efectiva.", "/Admin/Inventory/Alerts?view=stagnant"));
            items.Add(Item(OperationalAlertCategory.CycleCountStale, OperationalAlertSeverity.Critical, c.Stale, "Conteos obsoletos", "El saldo cambió durante el conteo y requiere reconteo.", "/Admin/Inventory/Alerts?view=cycle&attention=stale"));
            items.Add(Item(OperationalAlertCategory.CycleCountPending, OperationalAlertSeverity.Warning, c.Pending, "Conteos pendientes", "Ubicaciones en revisión o con reconteo solicitado.", "/Admin/Inventory/Alerts?view=cycle&attention=review"));
            items.Add(Item(OperationalAlertCategory.AgedWip, OperationalAlertSeverity.Information, c.AgedWip, "Saldo WIP estancado", $"Posiciones WIP positivas con lote de {c.WipDays} días o más.", "/Admin/Inventory/Alerts?view=wip"));
        }
        return items.Where(x => x.Count > 0).OrderBy(x => x.Severity).ThenBy(x => x.Category).ToArray();
    }

    private IQueryable<AlertDetailProjection> DetailRows(OperationalAlertCategory category, int wipDays, DateTimeOffset now)
    {
        if (category is OperationalAlertCategory.NegativeInventory or OperationalAlertCategory.UnassignedBalance or OperationalAlertCategory.RestrictedInventory)
        {
            var query = PositionTotals();
            query = category switch
            {
                OperationalAlertCategory.NegativeInventory => query.Where(x => x.Quantity < 0),
                OperationalAlertCategory.UnassignedBalance => query.Where(x => x.Quantity != 0 && !x.HasAssignment),
                _ => query.Where(x => x.Quantity != 0 && (!x.LocationIsActive || x.LocationIsBlocked))
            };
            return query.Select(x => new AlertDetailProjection
            {
                PrimaryText = x.ProductSku,
                SecondaryText = x.LocationCode,
                ProductId = x.ProductId,
                LocationId = x.LocationId,
                Quantity = x.Quantity,
                Unit = x.UnitCode
            });
        }
        if (category == OperationalAlertCategory.BelowMinimum)
            return BelowMinimumProducts().Select(x => new AlertDetailProjection
            {
                PrimaryText = x.Sku,
                SecondaryText = x.Description ?? "Sin descripción",
                ProductId = x.Id,
                Quantity = x.Minimum - x.Total,
                Unit = x.Unit
            });
        if (category == OperationalAlertCategory.StagnantInventory)
            return StagnantProducts(now.AddDays(-90)).Select(x => new AlertDetailProjection
            {
                PrimaryText = x.Sku,
                SecondaryText = x.Description ?? "Sin descripción",
                ProductId = x.Id,
                Quantity = x.Stock,
                Unit = x.Unit,
                OccurredAt = x.LastExit
            });
        if (category is OperationalAlertCategory.CycleCountStale or OperationalAlertCategory.CycleCountPending)
        {
            var cycle = dbContext.CycleCountLocations.AsNoTracking().Where(x => category == OperationalAlertCategory.CycleCountStale
                ? x.Status == CycleCountLocationStatus.Stale
                : x.Status == CycleCountLocationStatus.UnderReview || x.Status == CycleCountLocationStatus.RecountRequested);
            return cycle.Select(x => new AlertDetailProjection
            {
                PrimaryText = x.Campaign.Number.ToString(),
                SecondaryText = x.Location.Code,
                LocationId = x.LocationId,
                TargetId = x.CampaignId,
                CycleCountLocationId = x.Id,
                CycleStatus = x.Status,
                OccurredAt = x.CreatedAt
            });
        }
        return AgedWipBalances(DateOnly.FromDateTime(now.AddDays(-wipDays).UtcDateTime)).Select(x => new AlertDetailProjection
        {
            PrimaryText = x.Sku,
            SecondaryText = x.WipCode,
            ProductId = x.ProductId,
            LocationId = x.WipId,
            TargetId = x.LineId,
            Quantity = x.Quantity,
            Unit = x.Unit,
            OccurredAt = x.OccurredAt
        });
    }

    private static OperationalAlertDetailRowDto ToDetailDto(OperationalAlertCategory category, AlertDetailProjection row)
    {
        var severity = category switch
        {
            OperationalAlertCategory.NegativeInventory or OperationalAlertCategory.RestrictedInventory or OperationalAlertCategory.CycleCountStale => OperationalAlertSeverity.Critical,
            OperationalAlertCategory.AgedWip => OperationalAlertSeverity.Information,
            _ => OperationalAlertSeverity.Warning
        };
        var primary = category is OperationalAlertCategory.CycleCountStale or OperationalAlertCategory.CycleCountPending
            ? $"CC-{long.Parse(row.PrimaryText, System.Globalization.CultureInfo.InvariantCulture):D6}"
            : row.PrimaryText;
        var value = category switch
        {
            OperationalAlertCategory.BelowMinimum => $"Faltan {row.Quantity:0.####} {row.Unit}",
            OperationalAlertCategory.StagnantInventory => $"Existencia {row.Quantity:0.####} {row.Unit}",
            OperationalAlertCategory.CycleCountStale or OperationalAlertCategory.CycleCountPending => row.CycleStatus.ToString(),
            OperationalAlertCategory.AgedWip => $"Existencia {row.Quantity:0.####} {row.Unit}",
            _ => $"{row.Quantity:0.####} {row.Unit}"
        };
        var target = category switch
        {
            OperationalAlertCategory.BelowMinimum => $"/Inventory?productId={row.ProductId}",
            OperationalAlertCategory.StagnantInventory => $"/Reports/Inventory?view=stagnant&stagnantCategory=90plus&search={Uri.EscapeDataString(row.PrimaryText)}",
            OperationalAlertCategory.CycleCountStale or OperationalAlertCategory.CycleCountPending => $"/Operations/CycleCounts/Details?id={row.TargetId}",
            OperationalAlertCategory.AgedWip => $"/Reports/Wip?attention=aged&wipAreaId={row.LocationId}&search={Uri.EscapeDataString(row.PrimaryText)}",
            _ => $"/Inventory?productId={row.ProductId}&highlightLocationId={row.LocationId}"
        };
        return new(category, severity, primary, row.SecondaryText, value, target, row.ProductId, row.LocationId, row.OccurredAt);
    }

    private static OperationalAlertConditionDto ToCondition(OperationalAlertCategory category, AlertDetailProjection row)
    {
        var detail = ToDetailDto(category, row);
        var exceptionCategory = category switch
        {
            OperationalAlertCategory.NegativeInventory => OperationalExceptionCategory.NegativeInventory,
            OperationalAlertCategory.BelowMinimum => OperationalExceptionCategory.BelowMinimum,
            OperationalAlertCategory.UnassignedBalance => OperationalExceptionCategory.UnassignedBalance,
            OperationalAlertCategory.RestrictedInventory => OperationalExceptionCategory.RestrictedInventory,
            OperationalAlertCategory.StagnantInventory => OperationalExceptionCategory.StagnantInventory,
            OperationalAlertCategory.CycleCountStale => OperationalExceptionCategory.CycleCountStale,
            OperationalAlertCategory.CycleCountPending => OperationalExceptionCategory.CycleCountPending,
            _ => OperationalExceptionCategory.AgedWip
        };
        var severity = detail.Severity switch
        {
            OperationalAlertSeverity.Critical => OperationalExceptionSeverity.Critical,
            OperationalAlertSeverity.Information => OperationalExceptionSeverity.Information,
            _ => OperationalExceptionSeverity.Warning
        };
        var subject = category switch
        {
            OperationalAlertCategory.NegativeInventory or OperationalAlertCategory.UnassignedBalance or OperationalAlertCategory.RestrictedInventory
                => $"{row.ProductId:N}:{row.LocationId:N}",
            OperationalAlertCategory.BelowMinimum or OperationalAlertCategory.StagnantInventory => row.ProductId?.ToString("N") ?? string.Empty,
            OperationalAlertCategory.CycleCountStale or OperationalAlertCategory.CycleCountPending => row.CycleCountLocationId?.ToString("N") ?? string.Empty,
            _ => $"{row.ProductId:N}:{row.LocationId:N}"
        };
        var target = category switch
        {
            OperationalAlertCategory.NegativeInventory => $"/Operations/Adjustment?productId={row.ProductId}&locationId={row.LocationId}",
            OperationalAlertCategory.BelowMinimum => $"/Operations/Transfer?productId={row.ProductId}",
            OperationalAlertCategory.UnassignedBalance => $"/Admin/Catalogs/Locations/Details?id={row.LocationId}&productSearch={Uri.EscapeDataString(row.PrimaryText)}",
            OperationalAlertCategory.RestrictedInventory => $"/Admin/Catalogs/Locations/Details?id={row.LocationId}",
            OperationalAlertCategory.StagnantInventory => $"/Operations/Exit?productId={row.ProductId}&mode=general",
            OperationalAlertCategory.AgedWip => $"/Operations/Wip?action=consume&wipCode={Uri.EscapeDataString(row.SecondaryText)}&productCode={Uri.EscapeDataString(row.PrimaryText)}",
            _ => detail.TargetUrl
        };
        return new(exceptionCategory, severity, $"{exceptionCategory}:{subject}", detail.PrimaryText, detail.SecondaryText,
            detail.ValueText, target, row.ProductId, row.LocationId, row.CycleCountLocationId, row.OccurredAt);
    }

    private IQueryable<PositionTotal> PositionTotals()
    {
        var totals = from balance in dbContext.InventoryBalances.AsNoTracking()
                     where balance.Location.IsPhysicallyPresent
                     group balance by new
                     {
                         balance.ProductId,
                         balance.Product.Sku,
                         UnitCode = balance.Product.BaseUnit.Code,
                         balance.LocationId,
                         balance.Location.Code,
                         balance.Location.IsActive,
                         balance.Location.IsBlocked
                     } into balances
                     select new
                     {
                         balances.Key.ProductId,
                         balances.Key.Sku,
                         balances.Key.UnitCode,
                         balances.Key.LocationId,
                         balances.Key.Code,
                         balances.Key.IsActive,
                         balances.Key.IsBlocked,
                         Quantity = balances.Sum(x => x.Quantity)
                     };
        var assignments = dbContext.ProductLocationAssignments.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new { x.ProductId, x.LocationId });
        return from total in totals
               join assignment in assignments on new { total.ProductId, total.LocationId }
                   equals new { assignment.ProductId, assignment.LocationId } into activeAssignments
               select new PositionTotal
               {
                   ProductId = total.ProductId,
                   ProductSku = total.Sku,
                   UnitCode = total.UnitCode,
                   LocationId = total.LocationId,
                   LocationCode = total.Code,
                   LocationIsActive = total.IsActive,
                   LocationIsBlocked = total.IsBlocked,
                   Quantity = total.Quantity,
                   HasAssignment = activeAssignments.Any()
               };
    }

    private IQueryable<MinimumProduct> BelowMinimumProducts() =>
        from product in dbContext.Products.AsNoTracking()
        where product.IsActive
        join balance in dbContext.InventoryBalances.AsNoTracking() on product.Id equals balance.ProductId into balances
        let total = balances.Sum(x => (decimal?)x.Quantity) ?? 0m
        where total < product.MinimumStock
        select new MinimumProduct
        {
            Id = product.Id,
            Sku = product.Sku,
            Description = product.Description,
            Unit = product.BaseUnit.Code,
            Total = total,
            Minimum = product.MinimumStock
        };

    private IQueryable<StagnantProduct> StagnantProducts(DateTimeOffset cutoff)
    {
        var effectiveExits = dbContext.InventoryMovements.AsNoTracking().WhereEffective(dbContext).Where(x => x.Type == InventoryMovementType.Exit);
        return from product in dbContext.Products.AsNoTracking()
               where product.IsActive
               join balance in dbContext.InventoryBalances.AsNoTracking() on product.Id equals balance.ProductId into balances
               let stock = balances.Sum(x => (decimal?)x.Quantity) ?? 0m
               let lastExit = effectiveExits.Where(m => m.Lines.Any(l => l.ProductId == product.Id)).Max(m => (DateTimeOffset?)m.OccurredAt)
               where stock > 0 && (lastExit == null || lastExit <= cutoff)
               select new StagnantProduct
               {
                   Id = product.Id,
                   Sku = product.Sku,
                   Description = product.Description,
                   Unit = product.BaseUnit.Code,
                   Stock = stock,
                   LastExit = lastExit
               };
    }

    private IQueryable<AgedWipLine> AgedWipBalances(DateOnly cutoff) =>
        from balance in dbContext.InventoryBalances.AsNoTracking()
        where balance.Location.OperationalRole == LocationOperationalRole.Wip
        group balance by new
        {
            balance.ProductId,
            Sku = balance.Product.Sku,
            WipId = balance.LocationId,
            WipCode = balance.Location.Code,
            Unit = balance.Product.BaseUnit.Code
        }
        into position
        where position.Sum(item => item.Quantity) > 0 && position.Any(item => item.Quantity > 0 && item.Lot != null &&
            item.Lot.LotDate != null && item.Lot.LotDate <= cutoff)
        select new AgedWipLine
        {
            // PostgreSQL has no min(uuid); the line is only a deterministic
            // contextual subject, so choose the first UUID by ordering instead.
            LineId = position.OrderBy(item => item.Id).Select(item => item.Id).First(),
            ProductId = position.Key.ProductId,
            Sku = position.Key.Sku,
            WipId = position.Key.WipId,
            WipCode = position.Key.WipCode,
            Quantity = position.Sum(item => item.Quantity),
            Unit = position.Key.Unit,
            OccurredAt = position.Min(item => item.UpdatedAt)
        };

    private static OperationalAlertItemDto Item(OperationalAlertCategory category, OperationalAlertSeverity severity,
        int count, string title, string description, string targetUrl) => new(category, severity, count, title, description, targetUrl);
    private sealed record AlertCounts(int Negative, int Minimum, int Unassigned, int Restricted, int Stagnant, int Stale, int Pending, int AgedWip, int WipDays);
    private sealed class AlertDetailProjection
    {
        public required string PrimaryText { get; init; }
        public required string SecondaryText { get; init; }
        public Guid? ProductId { get; init; }
        public Guid? LocationId { get; init; }
        public Guid? TargetId { get; init; }
        public Guid? CycleCountLocationId { get; init; }
        public decimal? Quantity { get; init; }
        public string? Unit { get; init; }
        public CycleCountLocationStatus? CycleStatus { get; init; }
        public DateTimeOffset? OccurredAt { get; init; }
    }
    private sealed class PositionTotal
    {
        public required Guid ProductId { get; init; }
        public required string ProductSku { get; init; }
        public required string UnitCode { get; init; }
        public required Guid LocationId { get; init; }
        public required string LocationCode { get; init; }
        public required bool LocationIsActive { get; init; }
        public required bool LocationIsBlocked { get; init; }
        public required decimal Quantity { get; init; }
        public required bool HasAssignment { get; init; }
    }
    private sealed class MinimumProduct
    {
        public required Guid Id { get; init; }
        public required string Sku { get; init; }
        public string? Description { get; init; }
        public required string Unit { get; init; }
        public required decimal Total { get; init; }
        public required decimal Minimum { get; init; }
    }
    private sealed class StagnantProduct
    {
        public required Guid Id { get; init; }
        public required string Sku { get; init; }
        public string? Description { get; init; }
        public required string Unit { get; init; }
        public required decimal Stock { get; init; }
        public DateTimeOffset? LastExit { get; init; }
    }
    private sealed class AgedWipLine
    {
        public required Guid LineId { get; init; }
        public required Guid ProductId { get; init; }
        public required string Sku { get; init; }
        public required Guid WipId { get; init; }
        public required string WipCode { get; init; }
        public required decimal Quantity { get; init; }
        public required string Unit { get; init; }
        public required DateTimeOffset OccurredAt { get; init; }
    }
}
