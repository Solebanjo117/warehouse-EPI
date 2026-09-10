using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

/// <summary>
/// Servicio de consulta y agregación de actividad y carga de trabajo por responsable.
/// Mide operaciones efectivas y líneas procesadas por tipo de movimiento y turno horario.
/// </summary>
public sealed class WorkloadReportService(
    WarehouseDbContext dbContext,
    WarehouseSettingsService settingsService,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private static readonly TimeSpan MorningStart = new(6, 0, 0);
    private static readonly TimeSpan AfternoonStart = new(14, 0, 0);
    private static readonly TimeSpan NightStart = new(22, 0, 0);

    public async Task<WorkloadReportDto> GetWorkloadPageAsync(
        WorkloadReportFilter filter,
        string periodLabel = "",
        CancellationToken cancellationToken = default)
        => await GetWorkloadPageAsync(filter, periodLabel, true, cancellationToken);

    public async Task<WorkloadReportDto> GetWorkloadPageAsync(
        WorkloadReportFilter filter,
        string periodLabel,
        bool includeOperatorDetails,
        CancellationToken cancellationToken = default)
    {
        var (timeZone, generatedAtLocal) = await GetTimeZoneAndLocalNowAsync(cancellationToken);
        var calculation = await ComputeWorkloadAsync(filter, timeZone, cancellationToken);

        var pageNumber = Math.Max(1, filter.PageNumber);
        var pageSize = filter.PageSize <= 0 ? 25 : Math.Clamp(filter.PageSize, 1, 100);
        var pagedItems = includeOperatorDetails
            ? calculation.Operators
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize).ToArray()
            : [];
        var summary = includeOperatorDetails
            ? calculation.Summary
            : calculation.Summary with { TopOperatorName = null, TopOperatorOperations = 0 };

        return new WorkloadReportDto(
            periodLabel,
            FormatShiftLabel(filter.Shift),
            generatedAtLocal,
            timeZone.Id,
            summary,
            pagedItems,
            includeOperatorDetails ? calculation.Operators.Count : 0,
            pageNumber,
            pageSize,
            includeOperatorDetails,
            calculation.MovementBreakdown,
            calculation.PurposeBreakdown,
            calculation.DailyBreakdown,
            calculation.TimeBandBreakdown);
    }

    public async Task<(IReadOnlyList<WorkloadOperatorDto> Operators, WorkloadSummaryDto Summary, DateTimeOffset GeneratedAtLocal, string TimeZoneId, string ShiftLabel)> GetWorkloadExportAsync(
        WorkloadReportFilter filter,
        CancellationToken cancellationToken = default)
    {
        var (timeZone, generatedAtLocal) = await GetTimeZoneAndLocalNowAsync(cancellationToken);
        var calculation = await ComputeWorkloadAsync(filter, timeZone, cancellationToken);

        return (calculation.Operators, calculation.Summary, generatedAtLocal, timeZone.Id, FormatShiftLabel(filter.Shift));
    }

    private async Task<WorkloadCalculation> ComputeWorkloadAsync(
        WorkloadReportFilter filter,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var query = dbContext.InventoryMovements
            .AsNoTracking()
            .WhereEffective(dbContext);

        if (filter.FromUtc.HasValue)
            query = query.Where(m => m.OccurredAt >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(m => m.OccurredAt < filter.ToUtc.Value);

        if (filter.UserId.HasValue)
            query = query.Where(m => m.ResponsibleUserId == filter.UserId.Value);

        if (filter.MovementType.HasValue)
            query = query.Where(m => m.Type == filter.MovementType.Value);

        var rawRows = await query
            .Select(m => new
            {
                m.Id,
                m.OccurredAt,
                m.ResponsibleUserId,
                m.ResponsibleUser.FullName,
                RoleName = m.ResponsibleUser.Role.Name,
                m.Type,
                m.Purpose,
                LineCount = m.Lines.Count
            })
            .ToListAsync(cancellationToken);

        // Convertir a hora local y filtrar por turno
        var inMemoryRows = rawRows
            .Select(row => new
            {
                row.Id,
                LocalTime = TimeZoneInfo.ConvertTime(row.OccurredAt, timeZone),
                row.ResponsibleUserId,
                row.FullName,
                row.RoleName,
                row.Type,
                row.Purpose,
                row.LineCount
            })
            .Where(row => MatchesShift(row.LocalTime.TimeOfDay, filter.Shift));

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            inMemoryRows = inMemoryRows.Where(row =>
                row.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.RoleName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var rows = inMemoryRows.ToArray();
        var operators = rows
            .GroupBy(row => row.ResponsibleUserId)
            .Select(group =>
            {
                var first = group.First();
                var distinctOperations = group.Select(x => x.Id).Distinct().Count();
                var totalLines = group.Sum(x => x.LineCount);

                var entryOperations = group.Where(x => x.Type == InventoryMovementType.Entry).Select(x => x.Id).Distinct().Count();
                var entryLines = group.Where(x => x.Type == InventoryMovementType.Entry).Sum(x => x.LineCount);

                var exitOperations = group.Where(x => x.Type == InventoryMovementType.Exit).Select(x => x.Id).Distinct().Count();
                var exitLines = group.Where(x => x.Type == InventoryMovementType.Exit).Sum(x => x.LineCount);

                var transferOperations = group.Where(x => x.Type == InventoryMovementType.Transfer).Select(x => x.Id).Distinct().Count();
                var transferLines = group.Where(x => x.Type == InventoryMovementType.Transfer).Sum(x => x.LineCount);

                var adjustmentOperations = group.Where(x => x.Type == InventoryMovementType.Adjustment).Select(x => x.Id).Distinct().Count();
                var adjustmentLines = group.Where(x => x.Type == InventoryMovementType.Adjustment).Sum(x => x.LineCount);

                var firstActivity = group.Min(x => x.LocalTime);
                var lastActivity = group.Max(x => x.LocalTime);

                return new WorkloadOperatorDto(
                    group.Key,
                    first.FullName,
                    first.RoleName,
                    distinctOperations,
                    totalLines,
                    entryOperations,
                    entryLines,
                    exitOperations,
                    exitLines,
                    transferOperations,
                    transferLines,
                    adjustmentOperations,
                    adjustmentLines,
                    firstActivity,
                    lastActivity);
            })
            .OrderByDescending(x => x.TotalOperations)
            .ThenByDescending(x => x.TotalLines)
            .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalOps = operators.Sum(x => x.TotalOperations);
        var totalLns = operators.Sum(x => x.TotalLines);
        var activeOpsCount = operators.Count;
        var topOperator = operators.FirstOrDefault();

        var summary = new WorkloadSummaryDto(
            totalOps,
            totalLns,
            activeOpsCount,
            topOperator?.FullName,
            topOperator?.TotalOperations ?? 0);

        var movementBreakdown = rows
            .GroupBy(row => row.Type)
            .OrderBy(group => group.Key)
            .Select(group => new WorkloadBreakdownDto(
                group.Key.ToString(), MovementLabel(group.Key), group.Count(), group.Sum(row => row.LineCount)))
            .ToArray();
        var purposeBreakdown = rows
            .GroupBy(row => row.Purpose)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => new WorkloadBreakdownDto(
                group.Key.ToString(), PurposeLabel(group.Key), group.Count(), group.Sum(row => row.LineCount)))
            .ToArray();
        var dailyBreakdown = rows
            .GroupBy(row => DateOnly.FromDateTime(row.LocalTime.DateTime))
            .OrderBy(group => group.Key)
            .Select(group => new WorkloadDayDto(
                group.Key, group.Key.ToString("dd/MM/yyyy"), group.Count(), group.Sum(row => row.LineCount)))
            .ToArray();
        var timeBandBreakdown = Enum.GetValues<WorkloadShift>()
            .Where(shift => shift != WorkloadShift.All)
            .Select(shift =>
            {
                var bandRows = rows.Where(row => MatchesShift(row.LocalTime.TimeOfDay, shift)).ToArray();
                return new WorkloadBreakdownDto(
                    shift.ToString(), FormatShiftLabel(shift), bandRows.Length, bandRows.Sum(row => row.LineCount));
            })
            .ToArray();

        return new(operators, summary, movementBreakdown, purposeBreakdown, dailyBreakdown, timeBandBreakdown);
    }

    private static bool MatchesShift(TimeSpan timeOfDay, WorkloadShift shift) =>
        shift switch
        {
            WorkloadShift.Morning => timeOfDay >= MorningStart && timeOfDay < AfternoonStart,
            WorkloadShift.Afternoon => timeOfDay >= AfternoonStart && timeOfDay < NightStart,
            WorkloadShift.Night => timeOfDay >= NightStart || timeOfDay < MorningStart,
            _ => true
        };

    public static string FormatShiftLabel(WorkloadShift shift) =>
        shift switch
        {
            WorkloadShift.Morning => "Matutina (06:00 - 14:00)",
            WorkloadShift.Afternoon => "Vespertina (14:00 - 22:00)",
            WorkloadShift.Night => "Nocturna (22:00 - 06:00)",
            _ => "Día completo"
        };

    private static string MovementLabel(InventoryMovementType type) => type switch
    {
        InventoryMovementType.Entry => "Entradas",
        InventoryMovementType.Exit => "Salidas",
        InventoryMovementType.Transfer => "Transferencias",
        InventoryMovementType.Adjustment => "Ajustes",
        _ => type.ToString()
    };

    private static string PurposeLabel(InventoryMovementPurpose purpose) => purpose switch
    {
        InventoryMovementPurpose.Standard => "Operación estándar",
        InventoryMovementPurpose.GeneralExit => "Salida general",
        InventoryMovementPurpose.ProductionIssue => "Surtimiento WIP",
        InventoryMovementPurpose.WipWarehouseReturn => "Devolución WIP a bodega",
        InventoryMovementPurpose.WipConsumption => "Consumo WIP",
        InventoryMovementPurpose.WipSupplierReturn => "Devolución WIP a proveedor",
        InventoryMovementPurpose.CycleCountAdjustment => "Ajuste por conteo cíclico",
        InventoryMovementPurpose.DocumentReceipt => "Recepción por documento",
        InventoryMovementPurpose.ProductionReceipt => "Recepción de producción",
        _ => purpose.ToString()
    };

    private async Task<(TimeZoneInfo TimeZone, DateTimeOffset GeneratedAtLocal)> GetTimeZoneAndLocalNowAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var nowUtc = _timeProvider.GetUtcNow();
        var generatedAtLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        return (timeZone, generatedAtLocal);
    }

    private sealed record WorkloadCalculation(
        List<WorkloadOperatorDto> Operators,
        WorkloadSummaryDto Summary,
        IReadOnlyList<WorkloadBreakdownDto> MovementBreakdown,
        IReadOnlyList<WorkloadBreakdownDto> PurposeBreakdown,
        IReadOnlyList<WorkloadDayDto> DailyBreakdown,
        IReadOnlyList<WorkloadBreakdownDto> TimeBandBreakdown);
}
