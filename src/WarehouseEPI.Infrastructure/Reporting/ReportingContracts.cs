using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Reporting;

/// <summary>Parámetros de filtrado para el reporte analítico de movimientos efectivos.</summary>
public sealed record MovementReportFilter(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? Search = null,
    string? Sku = null,
    string? LocationCode = null,
    InventoryMovementType? MovementType = null,
    InventoryMovementPurpose? Purpose = null,
    Guid? ResponsibleUserId = null,
    int PageNumber = 1,
    int PageSize = 25);

/// <summary>Fila de movimiento efectivo para visualización tabular y reportes.</summary>
public sealed record EffectiveMovementRowDto(
    Guid Id,
    Guid OperationId,
    DateTimeOffset OccurredAt,
    InventoryMovementType MovementType,
    InventoryMovementPurpose Purpose,
    string ResponsibleName,
    string? Reference,
    string? Notes,
    string? OperationalAreaCode,
    int LineCount,
    int DistinctSkuCount,
    IReadOnlyList<EffectiveMovementLineDto> Lines);

/// <summary>Línea individual de detalle para un movimiento efectivo.</summary>
public sealed record EffectiveMovementLineDto(
    Guid LineId,
    Guid MovementId,
    Guid ProductId,
    string Sku,
    string? ProductDescription,
    short UnitId,
    string UnitCode,
    Guid? SourceLocationId,
    string? SourceLocationCode,
    Guid? DestinationLocationId,
    string? DestinationLocationCode,
    decimal Quantity,
    decimal? PreviousQuantity,
    decimal? AdjustmentDelta,
    string AllocationMode,
    IReadOnlyList<EffectiveMovementBalanceChangeDto> BalanceChanges);

/// <summary>Cambio auditable de saldo y lote aplicado por una línea de movimiento.</summary>
public sealed record EffectiveMovementBalanceChangeDto(
    Guid LocationId,
    string LocationCode,
    Guid? LotId,
    string? LotNumber,
    DateOnly? LotDate,
    decimal PreviousQuantity,
    decimal DeltaQuantity,
    decimal ResultingQuantity);

/// <summary>Página de resultados paginados de movimientos efectivos.</summary>
public sealed record EffectiveMovementPage(
    IReadOnlyList<EffectiveMovementRowDto> Items,
    int TotalCount,
    int PageNumber,
    int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

/// <summary>Resultado de exportación con control explícito del límite de filas de detalle.</summary>
public sealed record EffectiveMovementExportBatch(
    IReadOnlyList<EffectiveMovementRowDto> Items,
    int TotalOperations,
    int TotalRows,
    int MaximumRows)
{
    public bool ExceedsLimit => TotalRows > MaximumRows;
}

/// <summary>Punto de actividad temporal (agrupado por fecha local del almacén).</summary>
public sealed record MovementActivityPointDto(
    DateOnly Date,
    string DayLabel,
    int EntryCount,
    int ExitCount,
    int TransferCount,
    int AdjustmentCount,
    int TotalEffectiveOperations,
    int DistinctSkusCount);

/// <summary>Métricas de utilización y ocupación física de posiciones de rack en 5 estados.</summary>
public sealed record LocationOccupancySummaryDto(
    int TotalStorageRacks,
    int OccupiedCount,
    int EmptyCount,
    int NegativeCount,
    int BlockedCount,
    int InactiveCount)
{
    public int ActiveAvailableCount => OccupiedCount + EmptyCount + NegativeCount;

    public decimal UtilizationPercentage =>
        ActiveAvailableCount <= 0
            ? 0m
            : Math.Round((decimal)OccupiedCount / ActiveAvailableCount * 100m, 2);
}

/// <summary>Métrica determinista de rotación por SKU (número de salidas, cantidad en su unidad y SKU).</summary>
public sealed record SkuRotationMetricDto(
    Guid ProductId,
    string Sku,
    string? Description,
    short UnitId,
    string UnitCode,
    int EffectiveExitMovementCount,
    decimal QuantityInBaseUnit,
    decimal CurrentStock,
    DateTimeOffset? LastExitDateUtc);

/// <summary>Categorías de antigüedad para productos sin movimiento reciente con saldo positivo.</summary>
public enum StagnantCategory
{
    Days30To59 = 1,
    Days60To89 = 2,
    Days90Plus = 3,
    NeverExited = 4
}

/// <summary>Detalle de producto estancado / sin salida reciente.</summary>
public sealed record StagnantProductDto(
    Guid ProductId,
    string Sku,
    string? Description,
    short UnitId,
    string UnitCode,
    decimal CurrentStock,
    DateTimeOffset? LastExitDateUtc,
    int? DaysWithoutExit,
    StagnantCategory Category);

/// <summary>Métricas consolidadas del tablero diario.</summary>
public sealed record DailyDashboardMetricsDto(
    int EffectiveMovementsToday,
    int NegativeBalancesCount,
    int LowStockProductsCount,
    int RecentAdjustmentsCount,
    int ActiveCycleCountsCount,
    int BlockedRacksCount,
    int StockWithoutAssignmentCount,
    LocationOccupancySummaryDto Occupancy,
    IReadOnlyList<MovementActivityPointDto> RecentActivityTrend);
