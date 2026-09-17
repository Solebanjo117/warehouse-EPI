using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

/// <summary>
/// Consulta de solo lectura para el trabajo operativo que ya existe en producción,
/// conteos cíclicos y el centro administrativo de excepciones.
/// </summary>
public sealed class WorkQueueService(
    WarehouseDbContext dbContext,
    WarehouseSettingsService settingsService,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<WorkQueueSnapshotDto> GetSnapshotAsync(
        WorkQueueFilter filter,
        bool includeAdmin,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var generatedAtLocal = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), timeZone);
        var today = DateOnly.FromDateTime(generatedAtLocal.DateTime);
        var previewSize = Math.Clamp(filter.PreviewSize, 1, 25);
        var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();

        var production = await GetProductionAsync(search, today, previewSize, cancellationToken);
        var counts = await GetCycleCountsAsync(search, includeAdmin, previewSize, cancellationToken);
        var exceptions = includeAdmin
            ? await GetExceptionsAsync(search, previewSize, cancellationToken)
            : null;

        return new(generatedAtLocal, timeZone.Id, search, production, counts, exceptions);
    }

    private async Task<WorkQueueSectionDto<ProductionWorkItemDto>> GetProductionAsync(
        string? search,
        DateOnly today,
        int previewSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.ProductionWorkOrders.AsNoTracking()
            .Where(item => item.Status == ProductionWorkOrderStatus.Released ||
                           item.Status == ProductionWorkOrderStatus.InProgress ||
                           item.Status == ProductionWorkOrderStatus.Paused ||
                           item.Status == ProductionWorkOrderStatus.PrincipalClosed);

        if (search is not null)
        {
            var term = search.ToUpperInvariant();
            query = query.Where(item =>
                item.Number.ToUpper().Contains(term) ||
                item.ExternalReference != null && item.ExternalReference.ToUpper().Contains(term) ||
                item.Product.Sku.ToUpper().Contains(term) ||
                item.Product.Description != null && item.Product.Description.ToUpper().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(item => item.DueDate < today ? 0 :
                item.DueDate == today ? 1 :
                item.Status == ProductionWorkOrderStatus.InProgress ? 2 :
                item.Status == ProductionWorkOrderStatus.Released ? 3 : 4)
            .ThenBy(item => item.DueDate == null)
            .ThenBy(item => item.DueDate)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(previewSize)
            .Select(item => new
            {
                item.Id,
                item.Number,
                item.ExternalReference,
                item.Product.Sku,
                item.Product.Description,
                item.TargetQuantity,
                ReceivedQuantity = item.Events
                    .Where(entry => entry.Type == ProductionEventType.WarehouseReceived)
                    .Sum(entry => entry.Quantity),
                UnitCode = item.Unit.Code,
                item.Status,
                item.DueDate
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(item => new ProductionWorkItemDto(
            item.Id,
            item.Number,
            item.ExternalReference,
            item.Sku,
            item.Description,
            item.TargetQuantity,
            item.ReceivedQuantity,
            item.UnitCode,
            item.Status,
            item.DueDate,
            item.DueDate < today,
            item.DueDate == today,
            item.Status switch
            {
                ProductionWorkOrderStatus.Released => "Trabajar",
                ProductionWorkOrderStatus.InProgress => "Continuar",
                ProductionWorkOrderStatus.PrincipalClosed => "Atender pendientes",
                _ => "Ver orden"
            },
            item.Status == ProductionWorkOrderStatus.PrincipalClosed
                ? $"/Operations/Production/Execution?id={item.Id}"
                : $"/Operations/Production/Work?id={item.Id}"))
            .ToArray();

        return new(items, total);
    }

    private async Task<WorkQueueSectionDto<CycleCountWorkItemDto>> GetCycleCountsAsync(
        string? search,
        bool includeAdmin,
        int previewSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.CycleCountLocations.AsNoTracking()
            .Where(item =>
                item.Campaign.Status != CycleCountCampaignStatus.Draft &&
                item.Campaign.Status != CycleCountCampaignStatus.Completed &&
                item.Campaign.Status != CycleCountCampaignStatus.Cancelled &&
                (item.Status == CycleCountLocationStatus.Pending ||
                 item.Status == CycleCountLocationStatus.Counting ||
                 item.Status == CycleCountLocationStatus.RecountRequested ||
                 item.Status == CycleCountLocationStatus.Stale ||
                 includeAdmin && item.Status == CycleCountLocationStatus.UnderReview));

        if (search is not null)
        {
            var term = search.ToUpperInvariant();
            var numericText = search.Trim();
            if (numericText.StartsWith("CC-", StringComparison.OrdinalIgnoreCase))
                numericText = numericText[3..];
            var hasNumber = long.TryParse(numericText, out var campaignNumber);
            query = query.Where(item =>
                hasNumber && item.Campaign.Number == campaignNumber ||
                item.Campaign.Title != null && item.Campaign.Title.ToUpper().Contains(term) ||
                item.Location.Code.ToUpper().Contains(term) ||
                item.Location.Description != null && item.Location.Description.ToUpper().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(item => item.Status == CycleCountLocationStatus.Stale ? 0 :
                item.Status == CycleCountLocationStatus.RecountRequested ? 1 :
                item.Status == CycleCountLocationStatus.Counting ? 2 :
                item.Status == CycleCountLocationStatus.Pending ? 3 : 4)
            .ThenBy(item => item.Campaign.Number)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Take(previewSize)
            .Select(item => new
            {
                item.Id,
                item.CampaignId,
                item.Campaign.Number,
                CampaignTitle = item.Campaign.Title,
                LocationCode = item.Location.Code,
                LocationDescription = item.Location.Description,
                item.Status
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(item => new CycleCountWorkItemDto(
            item.Id,
            item.CampaignId,
            $"CC-{item.Number:D6}",
            item.CampaignTitle,
            item.LocationCode,
            item.LocationDescription,
            item.Status,
            item.Status switch
            {
                CycleCountLocationStatus.Counting => "Continuar conteo",
                CycleCountLocationStatus.UnderReview => "Revisar diferencia",
                _ => "Contar ubicación"
            },
            item.Status == CycleCountLocationStatus.UnderReview
                ? $"/Operations/CycleCounts/Review?id={item.CampaignId}&locationId={item.Id}"
                : $"/Operations/CycleCounts/Count?id={item.CampaignId}&locationId={item.Id}"))
            .ToArray();

        return new(items, total);
    }

    private async Task<WorkQueueSectionDto<ExceptionWorkItemDto>> GetExceptionsAsync(
        string? search,
        int previewSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.OperationalExceptionCases.AsNoTracking()
            .Where(item => item.Status == OperationalExceptionStatus.New ||
                           item.Status == OperationalExceptionStatus.InProgress ||
                           item.Status == OperationalExceptionStatus.Waiting);

        if (search is not null)
        {
            var term = search.ToUpperInvariant();
            query = query.Where(item =>
                item.PrimaryText.ToUpper().Contains(term) ||
                item.SecondaryText.ToUpper().Contains(term) ||
                item.ReasonText.ToUpper().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(item => item.Severity == OperationalExceptionSeverity.Critical ? 0 :
                item.Severity == OperationalExceptionSeverity.Warning ? 1 : 2)
            .ThenBy(item => item.Status == OperationalExceptionStatus.New ? 0 :
                item.Status == OperationalExceptionStatus.InProgress ? 1 : 2)
            .ThenBy(item => item.FirstDetectedAt)
            .ThenBy(item => item.Id)
            .Take(previewSize)
            .Select(item => new
            {
                item.Id,
                item.Category,
                item.Severity,
                item.Status,
                item.PrimaryText,
                item.SecondaryText,
                item.ValueText,
                AssignedUserName = item.AssignedUser == null ? null : item.AssignedUser.FullName
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(item => new ExceptionWorkItemDto(
            item.Id,
            item.Category,
            item.Severity,
            item.Status,
            item.PrimaryText,
            item.SecondaryText,
            item.ValueText,
            item.AssignedUserName,
            "Atender caso",
            $"/Admin/Inventory/Alerts/Details?id={item.Id}"))
            .ToArray();

        return new(items, total);
    }
}
