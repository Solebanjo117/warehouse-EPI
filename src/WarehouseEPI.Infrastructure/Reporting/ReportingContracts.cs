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
    int TotalStoragePositions,
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

/// <summary>Ocupación consolidada de posiciones físicas para una fila del almacén.</summary>
public sealed record LocationOccupancyRowDto(
    string RowCode,
    LocationOccupancySummaryDto Summary);

/// <summary>Resumen global y por fila de la ocupación física del almacén.</summary>
public sealed record LocationOccupancyReportDto(
    LocationOccupancySummaryDto Summary,
    IReadOnlyList<LocationOccupancyRowDto> Rows);

/// <summary>Actividad determinista de salidas por SKU, sin presentarla como tasa de rotación.</summary>
public sealed record SkuExitActivityMetricDto(
    Guid ProductId,
    string Sku,
    string? Description,
    short UnitId,
    string UnitCode,
    int EffectiveExitMovementCount,
    decimal QuantityInBaseUnit,
    decimal CurrentStock,
    DateTimeOffset? LastExitDateUtc,
    bool IsActive);

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
    StagnantCategory Category,
    bool IsActive);

/// <summary>Filtros normalizados para actividad de salidas, estancamiento, lotes y cobertura de inventario.</summary>
public sealed record InventoryAnalyticsFilter(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string ProductStatus = "active",
    string? Search = null,
    short? UnitId = null,
    int PageNumber = 1,
    int PageSize = 25,
    StagnantCategory? StagnantCategory = null,
    LotAgeBucket? AgeBucket = null,
    CoverageClassification? CoverageClassification = null);

/// <summary>Rangos de antigüedad de permanencia para lotes internos en almacén local.</summary>
public enum LotAgeBucket
{
    All = 0,
    Days0To30 = 1,
    Days31To60 = 2,
    Days61To90 = 3,
    Days90Plus = 4
}

/// <summary>Fila detallada de antigüedad de lote con saldo positivo en el almacén.</summary>
public sealed record LotAgingItemDto(
    Guid LotId,
    Guid ProductId,
    string Sku,
    string? Description,
    short UnitId,
    string UnitCode,
    string LotNumber,
    DateOnly? LotDate,
    int AgeDays,
    LotAgeBucket AgeBucket,
    string AgeBucketLabel,
    decimal Quantity,
    int LocationCount,
    string PrimaryLocationCode);

/// <summary>Resumen consolidado de lotes activos por rangos de tiempo de permanencia.</summary>
public sealed record LotAgingSummaryDto(
    int TotalActiveLots,
    int Days0To30LotCount,
    int Days31To60LotCount,
    int Days61To90LotCount,
    int Days90PlusLotCount);

/// <summary>Reporte consolidado de antigüedad de lotes con resumen y página de detalle.</summary>
public sealed record LotAgingReportDto(
    LotAgingSummaryDto Summary,
    InventoryAnalyticsPage<LotAgingItemDto> Page);

/// <summary>Niveles de clasificación de días de inventario disponible según ritmo de consumo real.</summary>
public enum CoverageClassification
{
    All = 0,
    Critical = 1,
    Low = 2,
    Normal = 3,
    Excess = 4,
    NoRecentConsumption = 5,
    Exhausted = 6
}

/// <summary>Métricas de cobertura estimada por consumo para un SKU individual.</summary>
public sealed record SkuCoverageItemDto(
    Guid ProductId,
    string Sku,
    string? Description,
    short UnitId,
    string UnitCode,
    decimal AvailableStock,
    decimal NetConsumption,
    decimal DailyAverageConsumption,
    decimal? CoverageDays,
    CoverageClassification Classification,
    string ClassificationLabel,
    DateTimeOffset? LastExitDateUtc);

/// <summary>Resumen consolidado de alertas de cobertura por consumo para el almacén.</summary>
public sealed record SkuCoverageSummaryDto(
    int TotalSkus,
    int CriticalCount,
    int LowCount,
    int NormalCount,
    int ExcessCount,
    int NoRecentConsumptionCount,
    int ExhaustedCount);

/// <summary>Reporte consolidado de cobertura por consumo con resumen de alertas y página de productos.</summary>
public sealed record SkuCoverageReportDto(
    SkuCoverageSummaryDto Summary,
    InventoryAnalyticsPage<SkuCoverageItemDto> Page);

/// <summary>Página genérica de resultados analíticos de inventario.</summary>
public sealed record InventoryAnalyticsPage<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int PageNumber,
    int PageSize)
{
    public int TotalPages => PageSize <= 0
        ? 1
        : Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

/// <summary>Lote completo para exportar sin truncamiento silencioso.</summary>
public sealed record InventoryAnalyticsExportBatch<T>(
    IReadOnlyList<T> Items,
    int TotalRows,
    int MaximumRows)
{
    public bool ExceedsLimit => TotalRows > MaximumRows;
}

/// <summary>Métricas consolidadas del tablero diario.</summary>
public sealed record DailyDashboardMetricsDto(
    int EffectiveMovementsToday,
    int NegativePositionsCount,
    int LowStockProductsCount,
    int EffectiveAdjustmentsToday,
    IReadOnlyList<MovementActivityPointDto> RecentActivityTrend);

/// <summary>Snapshot inmutable del tablero generado en la zona horaria del almacén.</summary>
public sealed record DailyDashboardSnapshotDto(
    DateOnly WarehouseDate,
    DateTimeOffset GeneratedAtLocal,
    DailyDashboardMetricsDto Metrics,
    OperationalComparisonDto? Comparison = null);

public enum OperationalAlertAudience { Public, Admin }
public enum OperationalAlertSeverity { Critical, Warning, Information }
public enum OperationalAlertCategory
{
    NegativeInventory, BelowMinimum, UnassignedBalance, RestrictedInventory,
    StagnantInventory, CycleCountStale, CycleCountPending, AgedWip
}

public sealed record OperationalAlertItemDto(
    OperationalAlertCategory Category,
    OperationalAlertSeverity Severity,
    int Count,
    string Title,
    string Description,
    string TargetUrl);

public sealed record OperationalAlertSnapshotDto(
    OperationalAlertAudience Audience,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset GeneratedAtLocal,
    int CriticalCount,
    int WarningCount,
    int InformationCount,
    int TotalVisible,
    IReadOnlyList<OperationalAlertItemDto> Items);

public sealed record OperationalAlertDetailRowDto(
    OperationalAlertCategory Category,
    OperationalAlertSeverity Severity,
    string PrimaryText,
    string SecondaryText,
    string? ValueText,
    string TargetUrl,
    Guid? ProductId = null,
    Guid? LocationId = null,
    DateTimeOffset? OccurredAt = null);

public sealed record OperationalAlertPageDto(
    IReadOnlyList<OperationalAlertDetailRowDto> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

/// <summary>Condición vigente reutilizable por alertas de solo lectura y el centro ADMIN.</summary>
public sealed record OperationalAlertConditionDto(
    OperationalExceptionCategory Category,
    OperationalExceptionSeverity Severity,
    string ConditionKey,
    string PrimaryText,
    string SecondaryText,
    string ReasonText,
    string? ValueText,
    string TargetUrl,
    Guid? ProductId,
    Guid? LocationId,
    Guid? CycleCountLocationId,
    DateTimeOffset? OccurredAt);

public enum MetricComparisonState { NoActivity, New, Increased, Decreased, Unchanged }
public sealed record MetricComparisonDto(int Current, int Previous, int Delta, decimal? PercentChange, MetricComparisonState State);
public sealed record OperationalDriverDto(string Code, string? Description, int Current, int Previous, int Delta, string TargetUrl);
public sealed record OperationalComparisonDto(
    MetricComparisonDto TodayOperations,
    MetricComparisonDto TodayAdjustments,
    MetricComparisonDto SevenDayOperations,
    MetricComparisonDto SevenDayAdjustments,
    MetricComparisonDto SevenDayDistinctSkus,
    IReadOnlyList<OperationalDriverDto> Products,
    IReadOnlyList<OperationalDriverDto> Rows,
    IReadOnlyList<OperationalDriverDto> Locations);

// =========================================================================
// ETAPA 4: SUPERVISIÓN OPERATIVA Y REPORTES EJECUTIVOS
// =========================================================================

public enum WorkloadShift
{
    All = 0,
    Morning = 1,
    Afternoon = 2,
    Night = 3
}

public sealed record WorkloadReportFilter(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    WorkloadShift Shift = WorkloadShift.All,
    Guid? UserId = null,
    InventoryMovementType? MovementType = null,
    string? Search = null,
    int PageNumber = 1,
    int PageSize = 25);

public sealed record WorkloadOperatorDto(
    Guid UserId,
    string FullName,
    string RoleName,
    int TotalOperations,
    int TotalLines,
    int EntryOperations,
    int EntryLines,
    int ExitOperations,
    int ExitLines,
    int TransferOperations,
    int TransferLines,
    int AdjustmentOperations,
    int AdjustmentLines,
    DateTimeOffset? FirstActivityLocal,
    DateTimeOffset? LastActivityLocal);

public sealed record WorkloadSummaryDto(
    int TotalOperations,
    int TotalLines,
    int ActiveOperatorsCount,
    string? TopOperatorName,
    int TopOperatorOperations);

public sealed record WorkloadBreakdownDto(
    string Code,
    string Label,
    int Operations,
    int Lines);

public sealed record WorkloadDayDto(
    DateOnly Date,
    string Label,
    int Operations,
    int Lines);

public sealed record WorkloadReportDto(
    string PeriodLabel,
    string ShiftLabel,
    DateTimeOffset GeneratedAtLocal,
    string TimeZoneId,
    WorkloadSummaryDto Summary,
    IReadOnlyList<WorkloadOperatorDto> Operators,
    int TotalCount,
    int PageNumber,
    int PageSize,
    bool IncludesOperatorDetails,
    IReadOnlyList<WorkloadBreakdownDto> MovementBreakdown,
    IReadOnlyList<WorkloadBreakdownDto> PurposeBreakdown,
    IReadOnlyList<WorkloadDayDto> DailyBreakdown,
    IReadOnlyList<WorkloadBreakdownDto> TimeBandBreakdown);

public sealed record WorkQueueFilter(string? Search = null, int PreviewSize = 8);

public sealed record WorkQueueSectionDto<T>(IReadOnlyList<T> Items, int TotalCount)
{
    public bool HasMore => TotalCount > Items.Count;
}

public sealed record ProductionWorkItemDto(
    Guid Id,
    string Number,
    string? ExternalReference,
    string Sku,
    string? Description,
    decimal TargetQuantity,
    decimal ReceivedQuantity,
    string UnitCode,
    ProductionWorkOrderStatus Status,
    DateOnly? DueDate,
    bool IsOverdue,
    bool IsDueToday,
    string ActionLabel,
    string TargetUrl);

public sealed record CycleCountWorkItemDto(
    Guid Id,
    Guid CampaignId,
    string Folio,
    string? CampaignTitle,
    string LocationCode,
    string? LocationDescription,
    CycleCountLocationStatus Status,
    string ActionLabel,
    string TargetUrl);

public sealed record ExceptionWorkItemDto(
    Guid Id,
    OperationalExceptionCategory Category,
    OperationalExceptionSeverity Severity,
    OperationalExceptionStatus Status,
    string PrimaryText,
    string SecondaryText,
    string? ValueText,
    string? AssignedUserName,
    string ActionLabel,
    string TargetUrl);

public sealed record WorkQueueSnapshotDto(
    DateTimeOffset GeneratedAtLocal,
    string TimeZoneId,
    string? Search,
    WorkQueueSectionDto<ProductionWorkItemDto> Production,
    WorkQueueSectionDto<CycleCountWorkItemDto> CycleCounts,
    WorkQueueSectionDto<ExceptionWorkItemDto>? Exceptions);

public enum HeatmapMetricType
{
    AccessFrequency = 1,
    OccupancyDensity = 2
}

public sealed record HeatmapReportFilter(
    HeatmapMetricType Metric = HeatmapMetricType.AccessFrequency,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? RowCode = null,
    string? Search = null,
    int PageNumber = 1,
    int PageSize = 50);

public sealed record RackHeatmapItemDto(
    Guid ElementId,
    string Label,
    string RowCode,
    short? RackNumber,
    int AccessCount,
    int TotalPositions,
    int OccupiedPositions,
    decimal OccupancyPercent,
    int HeatLevel,
    string HeatClass)
{
    public int NegativePositions { get; init; }
    public int BlockedPositions { get; init; }
}

public sealed record HeatmapSummaryDto(
    int TotalRacks,
    int ActiveRacks,
    int MaxAccessCount,
    decimal AverageOccupancyPercent,
    int HighHeatRacksCount);

public sealed record HeatmapReportDto(
    HeatmapMetricType Metric,
    string PeriodLabel,
    DateTimeOffset GeneratedAtLocal,
    string TimeZoneId,
    decimal CanvasWidth,
    decimal CanvasHeight,
    HeatmapSummaryDto Summary,
    IReadOnlyList<RackHeatmapItemDto> Racks,
    int TotalCount,
    int PageNumber,
    int PageSize)
{
    public IReadOnlyList<RackHeatmapItemDto> AllRacks { get; init; } = [];
}

public sealed record ExecutiveReportFilter(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string PeriodLabel = "",
    DateTimeOffset? PreviousFromUtc = null,
    DateTimeOffset? PreviousToUtc = null,
    string PreviousPeriodLabel = "");

public sealed record ExecutiveInventoryHealthDto(
    int TotalActiveSkus,
    int LowStockSkus,
    int Stagnant90PlusSkus,
    int CriticalCoverageSkus);

public sealed record ExecutiveCapacityDto(
    int TotalRackPositions,
    int OccupiedPositions,
    int EmptyPositions,
    decimal UtilizationPercent,
    int BlockedPositions,
    int NegativePositions);

public sealed record ExecutiveOperationalFlowDto(
    int TotalMovements,
    int TotalDetails,
    int EntryMovements,
    int EntryDetails,
    int ExitMovements,
    int ExitDetails,
    int TransferMovements,
    int TransferDetails,
    int AdjustmentMovements,
    int AdjustmentDetails);

public sealed record ExecutiveOperationalComparisonDto(
    MetricComparisonDto TotalMovements,
    MetricComparisonDto TotalDetails,
    MetricComparisonDto EntryMovements,
    MetricComparisonDto ExitMovements,
    MetricComparisonDto TransferMovements,
    MetricComparisonDto AdjustmentMovements);

public sealed record ExecutiveTopSkuDemandDto(
    string Sku,
    string? Description,
    string Unit,
    decimal TotalQuantity,
    int MovementCount);

public sealed record ExecutiveEvidenceLinksDto(
    string BelowMinimumUrl,
    string CriticalCoverageUrl,
    string StagnantUrl,
    string NegativeInventoryUrl,
    string OccupiedLocationsUrl,
    string EmptyLocationsUrl,
    string BlockedLocationsUrl,
    string EffectiveMovementsUrl);

public sealed record ExecutiveStagnantSkuDto(
    string Sku,
    string? Description,
    string Unit,
    decimal CurrentStock,
    int? DaysWithoutExit);

public sealed record ExecutiveReportDto(
    string PeriodLabel,
    string PreviousPeriodLabel,
    DateTimeOffset GeneratedAtLocal,
    string TimeZoneId,
    DateOnly ActivityFrom,
    DateOnly ActivityTo,
    DateOnly PreviousActivityFrom,
    DateOnly PreviousActivityTo,
    ExecutiveInventoryHealthDto InventoryHealth,
    ExecutiveCapacityDto Capacity,
    ExecutiveOperationalFlowDto OperationalFlow,
    ExecutiveOperationalComparisonDto Comparison,
    ExecutiveEvidenceLinksDto EvidenceLinks,
    IReadOnlyList<ExecutiveTopSkuDemandDto> TopDemandedSkus,
    IReadOnlyList<ExecutiveStagnantSkuDto> StagnantSkus);
