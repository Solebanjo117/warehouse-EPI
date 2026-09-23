using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed class CycleCountService(
    WarehouseDbContext dbContext,
    UserPinService userPinService,
    InventoryQueryService inventoryQuery,
    InventoryMovementService movementService,
    TimeProvider timeProvider,
    WarehouseClock warehouseClock)
{
    private static readonly CycleCountCampaignStatus[] OpenCampaignStatuses =
    [CycleCountCampaignStatus.Draft, CycleCountCampaignStatus.Released, CycleCountCampaignStatus.InProgress, CycleCountCampaignStatus.UnderReview];

    public async Task<PagedResult<CycleCountPlanCatalogItem>> GetPlanCatalogAsync(
        CycleCountPlanCatalogFilter filter, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var query = dbContext.CycleCountPlans.AsNoTracking();
        if (filter.IsActive is bool isActive) query = query.Where(item => item.IsActive == isActive);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim().ToUpperInvariant();
            query = query.Where(item => item.Product.Sku.Contains(search) ||
                item.Product.Description != null && item.Product.Description.ToUpper().Contains(search) ||
                item.Location.Code.Contains(search) ||
                item.Location.Description != null && item.Location.Description.ToUpper().Contains(search));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(item => item.Product.Sku).ThenBy(item => item.Location.Code).ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new CycleCountPlanCatalogItem(item.Id, item.ProductId, item.Product.Sku, item.Product.Description,
                item.Product.BaseUnit.Code, item.LocationId, item.Location.Code, item.Frequency, item.AnchorDate,
                item.NextDueDate, item.IsActive, !item.Product.IsActive || !item.Location.IsActive || !item.Location.IsPhysicallyPresent || item.Location.IsBlocked,
                item.Dispatches.Any(dispatch => OpenCampaignStatuses.Contains(dispatch.CycleCountLocation.Campaign.Status))))
            .ToListAsync(cancellationToken);
        return new(items, total, page, pageSize);
    }

    public async Task<CycleCountPlanCatalogItem?> GetPlanAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await dbContext.CycleCountPlans.AsNoTracking().Where(item => item.Id == id)
            .Select(item => new CycleCountPlanCatalogItem(item.Id, item.ProductId, item.Product.Sku, item.Product.Description,
                item.Product.BaseUnit.Code, item.LocationId, item.Location.Code, item.Frequency, item.AnchorDate,
                item.NextDueDate, item.IsActive, !item.Product.IsActive || !item.Location.IsActive || !item.Location.IsPhysicallyPresent || item.Location.IsBlocked,
                item.Dispatches.Any(dispatch => OpenCampaignStatuses.Contains(dispatch.CycleCountLocation.Campaign.Status))))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PagedResult<CycleCountCalendarItem>> GetCalendarAsync(
        CycleCountCalendarFilter filter, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var historicalKeys = dbContext.CycleCountPlannedProducts.AsNoTracking()
            .Where(item => item.CycleCountPlanId != null && item.ScheduledFor != null && !filter.Overdue &&
                item.ScheduledFor >= filter.From && item.ScheduledFor <= filter.To)
            .Select(item => new
            {
                RowId = item.Id,
                IsHistorical = true,
                PlanId = item.CycleCountPlanId,
                CampaignId = (Guid?)item.CycleCountLocation.CampaignId,
                CycleCountLocationId = (Guid?)item.CycleCountLocationId,
                ScheduledFor = item.ScheduledFor,
                item.ProductId,
                LocationId = item.CycleCountLocation.LocationId
            });

        var pendingKeys = dbContext.CycleCountPlans.AsNoTracking()
            .Where(item => item.IsActive &&
                (filter.Overdue ? item.NextDueDate < filter.Today : item.NextDueDate >= filter.From && item.NextDueDate <= filter.To) &&
                !item.Dispatches.Any(dispatch => OpenCampaignStatuses.Contains(dispatch.CycleCountLocation.Campaign.Status)))
            .Select(item => new
            {
                RowId = item.Id,
                IsHistorical = false,
                PlanId = (Guid?)item.Id,
                CampaignId = (Guid?)null,
                CycleCountLocationId = (Guid?)null,
                ScheduledFor = (DateOnly?)item.NextDueDate,
                item.ProductId,
                item.LocationId
            });

        var keys = historicalKeys.Concat(pendingKeys);
        var total = await keys.CountAsync(cancellationToken);
        var rows = await (from key in keys
                          join plan in dbContext.CycleCountPlans.AsNoTracking() on key.PlanId!.Value equals plan.Id
                          join product in dbContext.Products.AsNoTracking() on key.ProductId equals product.Id
                          join unit in dbContext.Units.AsNoTracking() on product.BaseUnitId equals unit.Id
                          join location in dbContext.Locations.AsNoTracking() on key.LocationId equals location.Id
                          orderby key.ScheduledFor, product.Sku, location.Code, key.RowId
                          select new CalendarProjection(key.RowId, key.IsHistorical, key.PlanId.GetValueOrDefault(), key.CampaignId,
                              key.CycleCountLocationId, key.ScheduledFor.GetValueOrDefault(), key.ProductId, product.Sku,
                              product.Description, unit.Code, location.Code, plan.Frequency, plan.IsActive,
                              !product.IsActive || !location.IsActive || !location.IsPhysicallyPresent || location.IsBlocked,
                              plan.Dispatches.Any(dispatch => OpenCampaignStatuses.Contains(dispatch.CycleCountLocation.Campaign.Status))))
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        var cycleLocationIds = rows.Where(item => item.CycleCountLocationId != null)
            .Select(item => item.CycleCountLocationId!.Value).Distinct().ToArray();
        IReadOnlyList<CalendarLocationProjection> historicalStates = cycleLocationIds.Length == 0
            ? []
            : await dbContext.CycleCountLocations.AsNoTracking().Where(item => cycleLocationIds.Contains(item.Id))
                .Select(item => new CalendarLocationProjection(item.Id, item.Status, item.CompletedAt,
                    OpenCampaignStatuses.Contains(item.Campaign.Status))).ToListAsync(cancellationToken);
        var completedLocationIds = historicalStates.Where(item => item.Status == CycleCountLocationStatus.Completed)
            .Select(item => item.Id).ToArray();
        IReadOnlyList<CountedEntryProjection> countedEntries = completedLocationIds.Length == 0
            ? []
            : await dbContext.CycleCountEntries.AsNoTracking()
                .Where(entry => completedLocationIds.Contains(entry.CycleCountAttempt.CycleCountLocationId) &&
                    entry.CycleCountAttempt.Status == CycleCountAttemptStatus.Submitted)
                .Select(entry => new CountedEntryProjection(entry.CycleCountAttempt.CycleCountLocationId,
                    entry.ProductId, entry.CycleCountAttempt.AttemptNumber, entry.CountedQuantity))
                .ToListAsync(cancellationToken);
        return new(rows.Select(item =>
        {
            var historicalState = item.CycleCountLocationId is Guid locationId
                ? historicalStates.Single(state => state.Id == locationId)
                : null;
            return new CycleCountCalendarItem((item.IsHistorical ? "H:" : "P:") + item.RowId, item.PlanId, item.CampaignId,
            item.ScheduledFor, item.Sku, item.Description, item.UnitCode, item.LocationCode, item.Frequency,
            item.IsHistorical, item.IsActive, item.IsBlocked,
            item.IsHistorical ? historicalState!.IsInCampaign : item.PlanHasOpenDispatch,
            historicalState?.Status,
            historicalState?.Status != CycleCountLocationStatus.Completed ? null : countedEntries
                .Where(entry => entry.CycleCountLocationId == historicalState.Id && entry.ProductId == item.ProductId)
                .OrderByDescending(entry => entry.AttemptNumber).Select(entry => entry.CountedQuantity).FirstOrDefault(),
            historicalState?.CompletedAt);
        }).ToArray(), total, page, pageSize);
    }

    public async Task<PagedResult<CycleCountPlanEventItem>> GetPlanEventsAsync(
        Guid planId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = dbContext.CycleCountPlanEvents.AsNoTracking().Where(item => item.CycleCountPlanId == planId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(item => item.RecordedAt).ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new CycleCountPlanEventItem(item.Type,
                item.ResponsibleUser == null ? "No registrado" : item.ResponsibleUser.FullName,
                item.PreviousFrequency, item.NewFrequency, item.PreviousAnchorDate, item.NewAnchorDate,
                item.PreviousNextDueDate, item.NewNextDueDate, item.PreviousIsActive, item.NewIsActive, item.RecordedAt))
            .ToListAsync(cancellationToken);
        return new(items, total, page, pageSize);
    }

    public async Task<CycleCountResult> CreatePlanAsync(CreateCycleCountPlanCommand command, CancellationToken cancellationToken = default)
    {
        if (!await IsActiveAdminAsync(command.ResponsibleUserId, cancellationToken)) return new(CycleCountStatus.InvalidState, Errors: ["La sesión administrativa ya no es válida."]);
        if (command.ProductId == Guid.Empty || command.LocationId == Guid.Empty) return new(CycleCountStatus.ValidationFailed, Errors: ["Selecciona producto y ubicación."]);
        if (!Enum.IsDefined(command.Frequency)) return new(CycleCountStatus.ValidationFailed, Errors: ["Selecciona una frecuencia válida."]);
        var product = await dbContext.Products.Include(item => item.BaseUnit).SingleOrDefaultAsync(item => item.Id == command.ProductId, cancellationToken);
        var location = await dbContext.Locations.SingleOrDefaultAsync(item => item.Id == command.LocationId, cancellationToken);
        if (product is null || location is null) return new(CycleCountStatus.NotFound, Errors: ["El producto o la ubicación ya no existen."]);
        if (!product.IsActive || !product.BaseUnit.IsActive || !location.IsOperational || !location.TracksInventory)
            return new(CycleCountStatus.ValidationFailed, Errors: ["El producto y la ubicación deben estar activos y disponibles para inventario."]);
        if (command.AnchorDate == default) return new(CycleCountStatus.ValidationFailed, Errors: ["La fecha inicial es obligatoria."]);
        if (await dbContext.CycleCountPlans.AnyAsync(item => item.ProductId == command.ProductId && item.LocationId == command.LocationId, cancellationToken))
            return new(CycleCountStatus.ValidationFailed, Errors: ["Ya existe un plan para este SKU y ubicación."]);
        var now = timeProvider.GetUtcNow();
        var plan = new CycleCountPlan { ProductId = command.ProductId, LocationId = command.LocationId, Frequency = command.Frequency, AnchorDate = command.AnchorDate, NextDueDate = command.AnchorDate, CreatedByUserId = command.ResponsibleUserId, UpdatedByUserId = command.ResponsibleUserId, CreatedAt = now, UpdatedAt = now };
        dbContext.CycleCountPlans.Add(plan);
        dbContext.CycleCountPlanEvents.Add(PlanEvent(plan, CycleCountPlanEventType.Created, command.ResponsibleUserId, now,
            null, plan.Frequency, null, plan.AnchorDate, null, plan.NextDueDate, null, plan.IsActive));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(CycleCountStatus.Success, PlanId: plan.Id);
    }

    public async Task<CycleCountResult> UpdatePlanAsync(UpdateCycleCountPlanCommand command, CancellationToken cancellationToken = default)
    {
        if (!await IsActiveAdminAsync(command.ResponsibleUserId, cancellationToken)) return new(CycleCountStatus.InvalidState, Errors: ["La sesión administrativa ya no es válida."]);
        if (!Enum.IsDefined(command.Frequency) || command.AnchorDate == default) return new(CycleCountStatus.ValidationFailed, Errors: ["La frecuencia y la fecha inicial deben ser válidas."]);
        var plan = await dbContext.CycleCountPlans.SingleOrDefaultAsync(item => item.Id == command.Id, cancellationToken);
        if (plan is null) return new(CycleCountStatus.NotFound);
        if (await dbContext.CycleCountPlannedProducts.AnyAsync(item => item.CycleCountPlanId == plan.Id &&
                OpenCampaignStatuses.Contains(item.CycleCountLocation.Campaign.Status), cancellationToken))
            return new(CycleCountStatus.InvalidState, Errors: ["No puedes cambiar un plan mientras su ubicación está en campaña."]);
        if (plan.Frequency == command.Frequency && plan.AnchorDate == command.AnchorDate) return new(CycleCountStatus.Success, PlanId: plan.Id);
        var previousFrequency = plan.Frequency;
        var previousAnchor = plan.AnchorDate;
        var previousDue = plan.NextDueDate;
        var lastCompletedAt = await dbContext.CycleCountPlannedProducts
            .Where(item => item.CycleCountPlanId == plan.Id && item.CycleCountLocation.Status == CycleCountLocationStatus.Completed)
            .Select(item => item.CycleCountLocation.CompletedAt).MaxAsync(cancellationToken);
        plan.Frequency = command.Frequency;
        plan.AnchorDate = command.AnchorDate;
        plan.NextDueDate = lastCompletedAt is DateTimeOffset completedAt
            ? NextDueAfter(command.AnchorDate, command.Frequency, await warehouseClock.GetDateAsync(completedAt, cancellationToken))
            : command.AnchorDate;
        var now = timeProvider.GetUtcNow();
        plan.UpdatedByUserId = command.ResponsibleUserId;
        plan.UpdatedAt = now;
        dbContext.CycleCountPlanEvents.Add(PlanEvent(plan, CycleCountPlanEventType.Updated, command.ResponsibleUserId, now,
            previousFrequency, plan.Frequency, previousAnchor, plan.AnchorDate, previousDue, plan.NextDueDate, plan.IsActive, plan.IsActive));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(CycleCountStatus.Success, PlanId: plan.Id);
    }

    public async Task<CycleCountResult> SetPlanActiveAsync(SetCycleCountPlanActiveCommand command, CancellationToken cancellationToken = default)
    {
        if (!await IsActiveAdminAsync(command.ResponsibleUserId, cancellationToken)) return new(CycleCountStatus.InvalidState, Errors: ["La sesión administrativa ya no es válida."]);
        var plan = await dbContext.CycleCountPlans.SingleOrDefaultAsync(item => item.Id == command.Id, cancellationToken);
        if (plan is null) return new(CycleCountStatus.NotFound);
        if (await dbContext.CycleCountPlannedProducts.AnyAsync(item => item.CycleCountPlanId == plan.Id &&
                OpenCampaignStatuses.Contains(item.CycleCountLocation.Campaign.Status), cancellationToken))
            return new(CycleCountStatus.InvalidState, Errors: ["No puedes cambiar un plan mientras su ubicación está en campaña."]);
        if (plan.IsActive == command.IsActive) return new(CycleCountStatus.Success, PlanId: plan.Id);
        var previous = plan.IsActive;
        var now = timeProvider.GetUtcNow();
        plan.IsActive = command.IsActive;
        plan.UpdatedByUserId = command.ResponsibleUserId;
        plan.UpdatedAt = now;
        dbContext.CycleCountPlanEvents.Add(PlanEvent(plan,
            command.IsActive ? CycleCountPlanEventType.Reactivated : CycleCountPlanEventType.Paused,
            command.ResponsibleUserId, now, plan.Frequency, plan.Frequency, plan.AnchorDate, plan.AnchorDate,
            plan.NextDueDate, plan.NextDueDate, previous, plan.IsActive));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(CycleCountStatus.Success, PlanId: plan.Id);
    }

    public async Task<CycleCountResult> ReleaseScheduledAsync(ReleaseScheduledCycleCountsCommand command, CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(command.Pin, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin);
        if (command.OperationId == Guid.Empty || command.PlanIds.Count == 0) return new(CycleCountStatus.ValidationFailed, Errors: ["Selecciona al menos un conteo programado."]);
        var existing = await dbContext.CycleCountCampaigns.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == command.OperationId, cancellationToken);
        if (existing is not null) return new(CycleCountStatus.Success, CampaignId: existing.Id);
        var today = await warehouseClock.GetDateAsync(timeProvider.GetUtcNow(), cancellationToken);
        var ids = command.PlanIds.Where(item => item != Guid.Empty).Distinct().ToArray();
        var plans = await dbContext.CycleCountPlans.Include(item => item.Product).Include(item => item.Location)
            .Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
        if (plans.Count != ids.Length || plans.Any(item => !item.IsActive || item.NextDueDate > today)) return new(CycleCountStatus.ValidationFailed, Errors: ["Uno de los conteos ya no está disponible para liberar."]);
        if (plans.Any(item => !item.Product.IsActive || !item.Location.IsOperational || !item.Location.TracksInventory)) return new(CycleCountStatus.ValidationFailed, Errors: ["Un producto o ubicación del plan está bloqueado o inactivo."]);
        var locationIds = plans.Select(item => item.LocationId).Distinct().ToArray();
        await using var transaction = dbContext.Database.IsRelational() ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        if (transaction is not null) await InventoryMovementStore.LockLocationsAsync(locationIds, transaction, cancellationToken);
        var overlapping = await dbContext.CycleCountLocations.Where(item => locationIds.Contains(item.LocationId) && OpenCampaignStatuses.Contains(item.Campaign.Status)).Select(item => item.Location.Code).Distinct().ToListAsync(cancellationToken);
        if (overlapping.Count != 0) return new(CycleCountStatus.ValidationFailed, Errors: [$"Estas ubicaciones ya pertenecen a una campaña abierta: {string.Join(", ", overlapping)}."]);
        var now = timeProvider.GetUtcNow();
        var campaign = new CycleCountCampaign { OperationId = command.OperationId, Title = $"Programado · {today:dd/MM/yyyy}", Status = CycleCountCampaignStatus.Released, CreatedByUserId = user.Id, LastActionByUserId = user.Id, CreatedAt = now, ReleasedAt = now };
        dbContext.CycleCountCampaigns.Add(campaign);
        foreach (var group in plans.GroupBy(item => item.Location).OrderBy(item => item.Key.Code))
        {
            var countLocation = new CycleCountLocation { LocationId = group.Key.Id, SortOrder = campaign.Locations.Count + 1, LastActionByUserId = user.Id, CreatedAt = now };
            foreach (var plan in group.OrderBy(item => item.Product.Sku))
                countLocation.PlannedProducts.Add(new() { ProductId = plan.ProductId, CycleCountPlanId = plan.Id, ScheduledFor = plan.NextDueDate });
            campaign.Locations.Add(countLocation);
        }
        AddAction(campaign, null, null, CycleCountActionType.Created, user.Id, now, "Campaña liberada desde el calendario programado.");
        AddAction(campaign, null, null, CycleCountActionType.Released, user.Id, now, null);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(CycleCountStatus.Success, CampaignId: campaign.Id);
    }

    public async Task<CycleCountResult> CreateAsync(CreateCycleCountCommand command, CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(command.Pin, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin);
        if (command.OperationId == Guid.Empty) return new(CycleCountStatus.ValidationFailed, Errors: ["El identificador de operación es obligatorio."]);
        var existingCampaign = await dbContext.CycleCountCampaigns.AsNoTracking()
            .Where(item => item.OperationId == command.OperationId)
            .Select(item => (Guid?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existingCampaign is Guid existingCampaignId) return new(CycleCountStatus.Success, CampaignId: existingCampaignId);
        var title = Normalize(command.Title, 160);
        var notes = Normalize(command.Notes, 500);
        var locations = await ResolveLocationsAsync(command, cancellationToken);
        if (locations.Count == 0) return new(CycleCountStatus.ValidationFailed, Errors: ["Selecciona al menos una ubicación física disponible."]);
        if (locations.Any(item => !item.IsPhysicallyPresent || !item.IsActive || item.IsBlocked || !item.TracksInventory))
            return new(CycleCountStatus.ValidationFailed, Errors: ["Las ubicaciones deben estar activas, no bloqueadas y controlar inventario."]);

        var locationIds = locations.Select(item => item.Id).ToArray();
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (transaction is not null)
            await InventoryMovementStore.LockLocationsAsync(locationIds, transaction, cancellationToken);
        var overlapping = await dbContext.CycleCountLocations
            .Where(item => locationIds.Contains(item.LocationId) && OpenCampaignStatuses.Contains(item.Campaign.Status))
            .Select(item => item.Location.Code)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (overlapping.Count != 0)
            return new(CycleCountStatus.ValidationFailed, Errors: [$"Estas ubicaciones ya pertenecen a una campaña abierta: {string.Join(", ", overlapping)}."]);

        var now = timeProvider.GetUtcNow();
        var campaign = new CycleCountCampaign { OperationId = command.OperationId, Title = title, Notes = notes, CreatedByUserId = user.Id, LastActionByUserId = user.Id, CreatedAt = now };
        dbContext.CycleCountCampaigns.Add(campaign);
        foreach (var (location, index) in locations.OrderBy(item => item.RowCode).ThenBy(item => item.RackNumber).ThenBy(item => item.PalletNumber).ThenBy(item => item.Code).Select((item, index) => (item, index)))
            campaign.Locations.Add(new() { LocationId = location.Id, SortOrder = index + 1, LastActionByUserId = user.Id, CreatedAt = now });
        AddAction(campaign, null, null, CycleCountActionType.Created, user.Id, now, notes);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(CycleCountStatus.Success, CampaignId: campaign.Id);
    }

    public async Task<CycleCountResult> ReleaseAsync(Guid campaignId, Guid operationId, string pin, CancellationToken cancellationToken = default) =>
        await ChangeCampaignStateAsync(campaignId, operationId, pin, CycleCountCampaignStatus.Draft, CycleCountCampaignStatus.Released, CycleCountActionType.Released, cancellationToken);

    public async Task<CycleCountResult> CancelAsync(Guid campaignId, Guid operationId, string pin, string? notes, CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(pin, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin);
        var duplicate = await GetActionResultAsync(operationId, campaignId, cancellationToken);
        if (duplicate is not null) return duplicate;
        var campaign = await dbContext.CycleCountCampaigns.Include(item => item.Locations).SingleOrDefaultAsync(item => item.Id == campaignId, cancellationToken);
        if (campaign is null) return new(CycleCountStatus.NotFound);
        if (campaign.Status is CycleCountCampaignStatus.Completed or CycleCountCampaignStatus.Cancelled) return new(CycleCountStatus.InvalidState, CampaignId: campaignId);
        var now = timeProvider.GetUtcNow();
        campaign.Status = CycleCountCampaignStatus.Cancelled;
        campaign.CancelledAt = now;
        campaign.LastActionByUserId = user.Id;
        foreach (var location in campaign.Locations.Where(item => item.Status != CycleCountLocationStatus.Completed))
        {
            location.Status = CycleCountLocationStatus.Cancelled;
            location.LastActionByUserId = user.Id;
        }
        AddAction(campaign, null, null, CycleCountActionType.Cancelled, user.Id, now, Normalize(notes, 500), operationId);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(CycleCountStatus.Success, CampaignId: campaignId);
    }

    public async Task<CycleCountResult> StartAttemptAsync(Guid locationId, Guid operationId, string pin, CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(pin, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin);
        if (operationId == Guid.Empty) return new(CycleCountStatus.ValidationFailed, Errors: ["El identificador de operación es obligatorio."]);
        var existing = await dbContext.CycleCountAttempts.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (existing is not null) return existing.CycleCountLocationId == locationId ? new(CycleCountStatus.Success, AttemptId: existing.Id, LocationId: locationId) : new(CycleCountStatus.IdempotencyConflict);

        var location = await dbContext.CycleCountLocations.Include(item => item.Campaign).Include(item => item.Attempts)
            .SingleOrDefaultAsync(item => item.Id == locationId, cancellationToken);
        if (location is null) return new(CycleCountStatus.NotFound);
        if (location.Campaign.Status is CycleCountCampaignStatus.Draft or CycleCountCampaignStatus.Completed or CycleCountCampaignStatus.Cancelled ||
            location.Status is CycleCountLocationStatus.Completed or CycleCountLocationStatus.Cancelled or CycleCountLocationStatus.Counting)
            return new(CycleCountStatus.InvalidState, CampaignId: location.CampaignId, LocationId: locationId);

        var productIds = await GetCountProductIdsAsync(location.Id, location.LocationId, cancellationToken);
        var products = await dbContext.Products.AsNoTracking().Include(item => item.BaseUnit)
            .Where(item => productIds.Contains(item.Id)).OrderBy(item => item.Sku).ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var attempt = new CycleCountAttempt
        {
            OperationId = operationId,
            CycleCountLocationId = location.Id,
            AttemptNumber = location.Attempts.Count + 1,
            StartedByUserId = user.Id,
            StartedAt = now
        };
        foreach (var product in products)
        {
            var balance = await inventoryQuery.GetBalanceAsync(product.Id, location.LocationId, cancellationToken);
            attempt.Entries.Add(new()
            {
                ProductId = product.Id,
                UnitId = product.BaseUnitId,
                ExpectedQuantity = balance.Quantity,
                ExpectedBalanceVersion = balance.Version
            });
        }
        dbContext.CycleCountAttempts.Add(attempt);
        location.Status = CycleCountLocationStatus.Counting;
        location.LastActionByUserId = user.Id;
        location.Campaign.Status = CycleCountCampaignStatus.InProgress;
        location.Campaign.LastActionByUserId = user.Id;
        AddAction(location.Campaign, location, attempt, CycleCountActionType.AttemptStarted, user.Id, now, null);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(CycleCountStatus.Success, location.CampaignId, location.Id, attempt.Id);
    }

    public async Task<CycleCountResult> SubmitAsync(SubmitCycleCountCommand command, CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(command.Pin, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin);
        return await SubmitCoreAsync(command.AttemptId, command.OperationId, user, command.Entries, command.IsLocationEmpty, cancellationToken);
    }

    public async Task<CycleCountResult> SubmitForUserAsync(SubmitCycleCountForUserCommand command, CancellationToken cancellationToken = default)
    {
        var user = await FindAuthorizedUserAsync(command.ResponsibleUserId, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin);
        return await SubmitCoreAsync(command.AttemptId, command.OperationId, user, command.Entries, command.IsLocationEmpty, cancellationToken);
    }

    private async Task<CycleCountResult> SubmitCoreAsync(Guid attemptId, Guid operationId, User user,
        IReadOnlyList<CycleCountQuantityCommand> entries, bool isLocationEmpty, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) return new(CycleCountStatus.ValidationFailed, Errors: ["El identificador de operación es obligatorio."]);
        var submitted = await dbContext.CycleCountAttempts.AsNoTracking()
            .Where(item => item.SubmissionOperationId == operationId)
            .Select(item => new { item.Id, item.CycleCountLocationId, item.CycleCountLocation.CampaignId })
            .SingleOrDefaultAsync(cancellationToken);
        if (submitted is not null)
            return submitted.Id == attemptId && await SubmissionMatchesAsync(submitted.Id, entries, isLocationEmpty, cancellationToken)
                ? new(CycleCountStatus.Success, submitted.CampaignId, submitted.CycleCountLocationId, submitted.Id)
                : new(CycleCountStatus.IdempotencyConflict);
        var attempt = await dbContext.CycleCountAttempts.Include(item => item.Entries).ThenInclude(item => item.Product).ThenInclude(item => item.BaseUnit)
            .Include(item => item.CycleCountLocation).ThenInclude(item => item.Campaign)
            .SingleOrDefaultAsync(item => item.Id == attemptId, cancellationToken);
        if (attempt is null) return new(CycleCountStatus.NotFound);
        if (attempt.Status != CycleCountAttemptStatus.Counting) return new(CycleCountStatus.InvalidState, attempt.CycleCountLocation.CampaignId, attempt.CycleCountLocationId, attempt.Id);

        var errors = ValidateSubmission(entries, isLocationEmpty, attempt);
        if (errors.Count != 0) return new(CycleCountStatus.ValidationFailed, attempt.CycleCountLocation.CampaignId, attempt.CycleCountLocationId, attempt.Id, Errors: errors);
        var entriesByProduct = entries.GroupBy(item => item.ProductId).ToDictionary(group => group.Key, group => group.Single().Quantity);
        var unexpectedIds = entriesByProduct.Keys.Except(attempt.Entries.Select(item => item.ProductId)).ToArray();
        if (unexpectedIds.Length != 0)
        {
            var unexpected = await dbContext.Products.Include(item => item.BaseUnit).Where(item => unexpectedIds.Contains(item.Id)).ToListAsync(cancellationToken);
            if (unexpected.Count != unexpectedIds.Length) return new(CycleCountStatus.ValidationFailed, Errors: ["Uno de los productos inesperados no existe."]);
            foreach (var product in unexpected)
            {
                if (decimal.Round(entriesByProduct[product.Id], 4) != entriesByProduct[product.Id] || (!product.BaseUnit.AllowsDecimals && decimal.Truncate(entriesByProduct[product.Id]) != entriesByProduct[product.Id]))
                    return new(CycleCountStatus.ValidationFailed, Errors: [$"La cantidad de {product.Sku} no respeta la unidad base."]);
                var balance = await inventoryQuery.GetBalanceAsync(product.Id, attempt.CycleCountLocation.LocationId, cancellationToken);
                var entry = new CycleCountEntry
                {
                    CycleCountAttemptId = attempt.Id,
                    ProductId = product.Id,
                    UnitId = product.BaseUnitId,
                    ExpectedQuantity = balance.Quantity,
                    ExpectedBalanceVersion = balance.Version,
                    IsUnexpectedProduct = true
                };
                dbContext.CycleCountEntries.Add(entry);
                attempt.Entries.Add(entry);
            }
        }

        if (!await VersionsMatchAsync(attempt, cancellationToken)) return await MarkStaleAsync(attempt, user.Id, cancellationToken);
        foreach (var entry in attempt.Entries)
        {
            entry.PlateCountsJson = "[]";
            entry.HasPlateDifference = false;
            entry.CountedQuantity = isLocationEmpty ? 0m : entriesByProduct[entry.ProductId];
        }
        attempt.Status = CycleCountAttemptStatus.Submitted;
        attempt.SubmissionOperationId = operationId;
        attempt.SubmittedByUserId = user.Id;
        attempt.SubmittedAt = timeProvider.GetUtcNow();
        var hasDifference = attempt.Entries.Any(item => item.CountedQuantity != item.ExpectedQuantity || item.HasPlateDifference);
        var cycleLocation = attempt.CycleCountLocation;
        cycleLocation.Status = hasDifference ? CycleCountLocationStatus.UnderReview : CycleCountLocationStatus.Completed;
        cycleLocation.LastActionByUserId = user.Id;
        if (!hasDifference)
        {
            cycleLocation.CompletedAt = attempt.SubmittedAt;
            await AdvanceScheduledPlansAsync(cycleLocation.Id, attempt.SubmittedAt!.Value, user.Id, cancellationToken);
        }
        cycleLocation.Campaign.LastActionByUserId = user.Id;
        AddAction(cycleLocation.Campaign, cycleLocation, attempt, CycleCountActionType.AttemptSubmitted, user.Id, attempt.SubmittedAt.Value, null);
        if (!hasDifference) AddAction(cycleLocation.Campaign, cycleLocation, attempt, CycleCountActionType.LocationCompleted, user.Id, attempt.SubmittedAt.Value, "Conteo conciliado sin ajuste.");
        await dbContext.SaveChangesAsync(cancellationToken);
        await RefreshCampaignStatusAsync(cycleLocation.Campaign, user.Id, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(CycleCountStatus.Success, cycleLocation.CampaignId, cycleLocation.Id, attempt.Id);
    }

    public async Task<CycleCountPreparation?> PrepareAsync(Guid campaignId, Guid cycleCountLocationId, CancellationToken cancellationToken = default)
    {
        var location = await dbContext.CycleCountLocations.Include(item => item.Location).Include(item => item.Campaign)
            .SingleOrDefaultAsync(item => item.Id == cycleCountLocationId && item.CampaignId == campaignId, cancellationToken);
        if (location is null || location.Campaign.Status is CycleCountCampaignStatus.Draft or CycleCountCampaignStatus.Completed or CycleCountCampaignStatus.Cancelled ||
            location.Status is not (CycleCountLocationStatus.Pending or CycleCountLocationStatus.RecountRequested or CycleCountLocationStatus.Stale)) return null;
        var productIds = await GetCountProductIdsAsync(location.Id, location.LocationId, cancellationToken);
        var products = await dbContext.Products.AsNoTracking().Include(item => item.BaseUnit)
            .Where(item => productIds.Contains(item.Id)).OrderBy(item => item.Sku).ToListAsync(cancellationToken);
        var entries = new List<CycleCountPreparationEntry>();
        foreach (var product in products)
        {
            var balance = await inventoryQuery.GetBalanceAsync(product.Id, location.LocationId, cancellationToken);
            entries.Add(new(product.Id, product.Sku, product.Description, product.BaseUnit.Code, product.BaseUnit.AllowsDecimals, balance.Quantity, balance.Version));
        }
        return new(campaignId, location.Id, location.LocationId, location.Location.Code, timeProvider.GetUtcNow(), entries);
    }

    public async Task<CycleCountResult> SubmitPreparedAsync(SubmitPreparedCycleCountCommand command, CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(command.Pin, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin);
        return await SubmitPreparedCoreAsync(command.Preparation, command.OperationId, user, command.Entries, command.IsLocationEmpty, cancellationToken);
    }

    public async Task<CycleCountResult> SubmitPreparedForUserAsync(SubmitPreparedCycleCountForUserCommand command, CancellationToken cancellationToken = default)
    {
        var user = await FindAuthorizedUserAsync(command.ResponsibleUserId, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin);
        return await SubmitPreparedCoreAsync(command.Preparation, command.OperationId, user, command.Entries, command.IsLocationEmpty, cancellationToken);
    }

    private async Task<CycleCountResult> SubmitPreparedCoreAsync(CycleCountPreparation preparation, Guid operationId, User user,
        IReadOnlyList<CycleCountQuantityCommand> entries, bool isLocationEmpty, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) return new(CycleCountStatus.ValidationFailed, Errors: ["El identificador de operación es obligatorio."]);
        var existing = await dbContext.CycleCountAttempts.AsNoTracking().Where(item => item.SubmissionOperationId == operationId)
            .Select(item => new { item.Id, item.CycleCountLocationId, item.CycleCountLocation.CampaignId }).SingleOrDefaultAsync(cancellationToken);
        if (existing is not null) return existing.CycleCountLocationId == preparation.CycleCountLocationId && await SubmissionMatchesAsync(existing.Id, entries, isLocationEmpty, cancellationToken)
            ? new(CycleCountStatus.Success, existing.CampaignId, existing.CycleCountLocationId, existing.Id) : new(CycleCountStatus.IdempotencyConflict);
        var location = await dbContext.CycleCountLocations.Include(item => item.Campaign).Include(item => item.Attempts)
            .SingleOrDefaultAsync(item => item.Id == preparation.CycleCountLocationId && item.CampaignId == preparation.CampaignId, cancellationToken);
        if (location is null) return new(CycleCountStatus.NotFound);
        if (location.Campaign.Status is CycleCountCampaignStatus.Draft or CycleCountCampaignStatus.Completed or CycleCountCampaignStatus.Cancelled ||
            location.Status is not (CycleCountLocationStatus.Pending or CycleCountLocationStatus.RecountRequested or CycleCountLocationStatus.Stale)) return new(CycleCountStatus.InvalidState, location.CampaignId, location.Id);

        // La captura se valida antes de persistir: un envío inválido no debe crear el intento
        // ni dejar la ubicación en Counting, porque el mismo token preparado se reenvía.
        var errors = ValidateQuantities(
            [.. preparation.Entries.Select(item => new ExpectedCountLine(item.ProductId, item.Sku, item.AllowsDecimals))],
            entries,
            isLocationEmpty);
        if (errors.Count == 0)
            errors.AddRange(await ValidateUnexpectedProductsAsync(entries, preparation.Entries, cancellationToken));
        if (errors.Count != 0) return new(CycleCountStatus.ValidationFailed, location.CampaignId, location.Id, Errors: errors);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var attempt = new CycleCountAttempt
        {
            OperationId = Guid.NewGuid(),
            CycleCountLocationId = location.Id,
            AttemptNumber = location.Attempts.Count + 1,
            StartedByUserId = user.Id,
            StartedAt = preparation.PreparedAt
        };
        foreach (var entry in preparation.Entries)
            attempt.Entries.Add(new() { ProductId = entry.ProductId, UnitId = await UnitIdAsync(entry.ProductId, cancellationToken), ExpectedQuantity = entry.ExpectedQuantity, ExpectedBalanceVersion = entry.ExpectedBalanceVersion });
        dbContext.CycleCountAttempts.Add(attempt);
        location.Status = CycleCountLocationStatus.Counting;
        location.Campaign.Status = CycleCountCampaignStatus.InProgress;
        AddAction(location.Campaign, location, attempt, CycleCountActionType.AttemptStarted, user.Id, preparation.PreparedAt, "Preparación confirmada al enviar el conteo.");
        await dbContext.SaveChangesAsync(cancellationToken);
        var result = await SubmitCoreAsync(attempt.Id, operationId, user, entries, isLocationEmpty, cancellationToken);
        // Se confirma cualquier estado devuelto, incluido el marcado Stale, que es una
        // transición legítima; sólo una excepción revierte el intento recién creado.
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<CycleCountBatchResult> ReviewBatchAsync(ApproveCycleCountBatchCommand command, CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(command.Pin, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin, null, [], ["NIP no válido."]);
        if (command.OperationId == Guid.Empty || command.Decisions.Count == 0 || command.Decisions.Any(item => item.OperationId == Guid.Empty))
            return new(CycleCountStatus.ValidationFailed, null, [], ["La revisión final no contiene identificadores de operación válidos."]);
        if (command.Decisions.Any(item => item.Decision == CycleCountReviewDecision.Approve && item.Reason is null))
            return new(CycleCountStatus.ValidationFailed, null, [], ["Selecciona una causa para cada ajuste aprobado."]);
        if (command.Decisions.Any(item => item.Decision == CycleCountReviewDecision.Approve && item.Reason == CycleCountAdjustmentReason.Other && string.IsNullOrWhiteSpace(item.Notes)))
            return new(CycleCountStatus.ValidationFailed, null, [], ["Describe la causa cuando eliges Otro."]);
        var fingerprintPayload = string.Join('|', command.Decisions.Select(item =>
            $"{item.LocationId:N}:{item.OperationId:N}:{item.Decision}:{item.Reason}:{item.Notes}:{string.Join(',', (item.ApprovedSharedAssignments ?? []).OrderBy(value => value.ProductId).ThenBy(value => value.LocationId).Select(value => $"{value.ProductId:N}-{value.LocationId:N}"))}"));
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprintPayload)));
        var batch = await dbContext.CycleCountReviewBatches.SingleOrDefaultAsync(item => item.OperationId == command.OperationId, cancellationToken);
        if (batch is not null && batch.RequestFingerprint != fingerprint) return new(CycleCountStatus.IdempotencyConflict, batch.Id, [], ["El identificador de lote ya pertenece a otra solicitud."]);
        if (batch is not null) return new(CycleCountStatus.Success, batch.Id, []);
        var decisionIds = command.Decisions.Select(item => item.LocationId).Distinct().ToArray();
        if (decisionIds.Length != command.Decisions.Count)
            return new(CycleCountStatus.ValidationFailed, null, [], ["Cada ubicación debe aparecer una sola vez en la revisión final."]);
        var decisionLocations = await dbContext.CycleCountLocations.AsNoTracking()
            .Where(item => decisionIds.Contains(item.Id))
            .Select(item => new { item.Id, item.CampaignId, item.Status })
            .ToListAsync(cancellationToken);
        if (decisionLocations.Count != decisionIds.Length || decisionLocations.Any(item => item.CampaignId != command.CampaignId))
            return new(CycleCountStatus.ValidationFailed, null, [], ["Una ubicación no pertenece a esta campaña."]);
        if (decisionLocations.Any(item => item.Status is not (CycleCountLocationStatus.UnderReview or CycleCountLocationStatus.Stale)))
            return new(CycleCountStatus.ValidationFailed, null, [], ["Una ubicación ya no está disponible para revisión."]);
        var states = decisionLocations.ToDictionary(item => item.Id, item => item.Status);
        if (command.Decisions.Any(item => item.Decision == CycleCountReviewDecision.Approve && states[item.LocationId] == CycleCountLocationStatus.Stale))
            return new(CycleCountStatus.ValidationFailed, null, [], ["Las ubicaciones cuyo saldo cambió deben enviarse a reconteo."]);
        batch = new() { OperationId = command.OperationId, CampaignId = command.CampaignId, AuthorizedByUserId = user.Id, AuthorizedAt = timeProvider.GetUtcNow(), RequestFingerprint = fingerprint };
        dbContext.CycleCountReviewBatches.Add(batch);
        await dbContext.SaveChangesAsync(cancellationToken);
        var results = new List<CycleCountBatchItemResult>();
        foreach (var item in command.Decisions)
        {
            CycleCountResult result;
            if (item.Decision == CycleCountReviewDecision.Recount)
                result = await RequestRecountAsync(new(item.LocationId, item.OperationId, command.Pin, item.Notes, ReviewBatchId: batch.Id), cancellationToken);
            else
                result = await ApproveAsync(new(item.LocationId, item.OperationId, command.Pin, FormatReason(item.Reason, item.Notes), item.ApprovedSharedAssignments, batch.Id), cancellationToken);
            if (item.Decision == CycleCountReviewDecision.Approve && result.Status == CycleCountStatus.Success)
            {
                var location = await dbContext.CycleCountLocations.SingleAsync(value => value.Id == item.LocationId, cancellationToken);
                location.AdjustmentReason = item.Reason;
                location.AdjustmentReasonNotes = Normalize(item.Notes, 500);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            results.Add(new(item.LocationId, result.Status, result.MovementId, result.ValidationErrors, result.Conflicts));
        }
        if (!await dbContext.CycleCountActions.AnyAsync(item => item.ReviewBatchId == batch.Id && item.Type == CycleCountActionType.BatchReviewed, cancellationToken))
            dbContext.CycleCountActions.Add(new() { OperationId = Guid.NewGuid(), CampaignId = command.CampaignId, ReviewBatchId = batch.Id, Type = CycleCountActionType.BatchReviewed, ResponsibleUserId = user.Id, RecordedAt = timeProvider.GetUtcNow(), Notes = $"Revisión agrupada: {results.Count(item => item.Status == CycleCountStatus.Success)} acción(es)." });
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(CycleCountStatus.Success, batch.Id, results);
    }

    public async Task<CycleCountResult> RequestRecountAsync(CycleCountActionCommand command, CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(command.Pin, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin);
        var location = await dbContext.CycleCountLocations.Include(item => item.Campaign).Include(item => item.Attempts)
            .SingleOrDefaultAsync(item => item.Id == command.LocationId, cancellationToken);
        if (location is null) return new(CycleCountStatus.NotFound);
        var duplicate = await GetActionResultAsync(command.OperationId, location.CampaignId, cancellationToken);
        if (duplicate is not null) return duplicate;
        if (location.Status is not (CycleCountLocationStatus.UnderReview or CycleCountLocationStatus.Stale)) return new(CycleCountStatus.InvalidState, location.CampaignId, location.Id);
        var now = timeProvider.GetUtcNow();
        foreach (var attempt in location.Attempts.Where(item => item.Status == CycleCountAttemptStatus.Submitted)) attempt.Status = CycleCountAttemptStatus.Superseded;
        location.Status = CycleCountLocationStatus.RecountRequested;
        location.LastActionByUserId = user.Id;
        location.Campaign.Status = CycleCountCampaignStatus.InProgress;
        location.Campaign.LastActionByUserId = user.Id;
        AddAction(location.Campaign, location, null, CycleCountActionType.RecountRequested, user.Id, now, Normalize(command.Notes, 500), command.OperationId, command.ReviewBatchId);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(CycleCountStatus.Success, location.CampaignId, location.Id);
    }

    public async Task<CycleCountResult> ApproveAsync(CycleCountActionCommand command, CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(command.Pin, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin);
        var location = await dbContext.CycleCountLocations.Include(item => item.Campaign).Include(item => item.Attempts).ThenInclude(item => item.Entries)
            .SingleOrDefaultAsync(item => item.Id == command.LocationId, cancellationToken);
        if (location is null) return new(CycleCountStatus.NotFound);
        var attempt = location.Attempts.OrderByDescending(item => item.AttemptNumber).FirstOrDefault(item => item.Status == CycleCountAttemptStatus.Submitted);
        if (location.Status != CycleCountLocationStatus.UnderReview || attempt is null) return new(CycleCountStatus.InvalidState, location.CampaignId, location.Id);
        if (!await VersionsMatchAsync(attempt, cancellationToken)) return await MarkStaleAsync(attempt, user.Id, cancellationToken);
        var differences = attempt.Entries.Where(item => item.CountedQuantity != item.ExpectedQuantity || item.HasPlateDifference).ToArray();
        if (differences.Length == 0) return new(CycleCountStatus.InvalidState, location.CampaignId, location.Id, attempt.Id);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var movement = await movementService.ConfirmAsync(new(
            command.OperationId,
            InventoryMovementType.Adjustment,
            command.Pin,
            differences.Select(item => new InventoryMovementLineCommand(item.ProductId, item.CountedQuantity!.Value, LocationId: location.LocationId, ExpectedBalanceVersion: item.ExpectedBalanceVersion, AutomaticPalletHandling: true)).ToArray(),
            $"CC-{location.Campaign.Number:D6}",
            Normalize(command.Notes, 500) ?? $"Ajuste autorizado por conteo cíclico CC-{location.Campaign.Number:D6}.",
            command.ApprovedSharedAssignments,
            InventoryMovementPurpose.CycleCountAdjustment), cancellationToken);
        if (movement.Status == InventoryMovementStatus.BalanceChanged)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            var freshAttempt = await dbContext.CycleCountAttempts.Include(item => item.Entries)
                .Include(item => item.CycleCountLocation).ThenInclude(item => item.Campaign)
                .SingleAsync(item => item.Id == attempt.Id, cancellationToken);
            return await MarkStaleAsync(freshAttempt, user.Id, cancellationToken);
        }
        if (movement.Status == InventoryMovementStatus.RequiresLocationSharingConfirmation)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return new(CycleCountStatus.RequiresLocationSharingConfirmation, location.CampaignId, location.Id, attempt.Id, SharingConflicts: movement.Conflicts);
        }
        if (movement.Status != InventoryMovementStatus.Success || movement.MovementId is not Guid movementId)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return new(CycleCountStatus.ValidationFailed, location.CampaignId, location.Id, attempt.Id, Errors: movement.ValidationErrors);
        }
        var now = timeProvider.GetUtcNow();
        location.AdjustmentMovementId = movementId;
        location.Status = CycleCountLocationStatus.Completed;
        location.CompletedAt = now;
        await AdvanceScheduledPlansAsync(location.Id, now, user.Id, cancellationToken);
        location.LastActionByUserId = user.Id;
        location.Campaign.LastActionByUserId = user.Id;
        AddAction(location.Campaign, location, attempt, CycleCountActionType.AdjustmentApproved, user.Id, now, Normalize(command.Notes, 500), reviewBatchId: command.ReviewBatchId);
        AddAction(location.Campaign, location, attempt, CycleCountActionType.LocationCompleted, user.Id, now, null);
        await dbContext.SaveChangesAsync(cancellationToken);
        await RefreshCampaignStatusAsync(location.Campaign, user.Id, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(CycleCountStatus.Success, location.CampaignId, location.Id, attempt.Id, movementId);
    }

    public async Task<IReadOnlyList<CycleCountCampaignListItem>> GetCampaignsAsync(
        CycleCountCampaignStatus? status,
        string? search,
        int page,
        int pageSize,
        DateTimeOffset? createdFromUtc = null,
        DateTimeOffset? createdToUtc = null,
        IReadOnlyCollection<CycleCountLocationStatus>? attentionStatuses = null,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.CycleCountCampaigns.AsNoTracking().Include(item => item.Locations).AsQueryable();
        if (status is not null) query = query.Where(item => item.Status == status);
        if (createdFromUtc is not null) query = query.Where(item => item.CreatedAt >= createdFromUtc);
        if (createdToUtc is not null) query = query.Where(item => item.CreatedAt < createdToUtc);
        if (attentionStatuses is { Count: > 0 })
            query = query.Where(item => item.Locations.Any(location => attentionStatuses.Contains(location.Status)));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (long.TryParse(term.TrimStart('C', 'c', '-'), out var number))
                query = query.Where(item => item.Number == number || item.Title != null && EF.Functions.ILike(item.Title, $"%{term}%"));
            else query = query.Where(item => item.Title != null && EF.Functions.ILike(item.Title, $"%{term}%"));
        }
        var items = await query.OrderByDescending(item => item.CreatedAt).Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return items.Select(item => new CycleCountCampaignListItem(item.Id, Folio(item.Number), item.Title, item.Status, item.CreatedAt, item.Locations.Count,
            item.Locations.Count(location => location.Status == CycleCountLocationStatus.Completed), item.Locations.Count(location => location.Status == CycleCountLocationStatus.UnderReview))).ToArray();
    }

    public async Task<CycleCountCampaignDetail?> GetCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.CycleCountCampaigns.AsNoTracking().Include(item => item.CreatedByUser).Include(item => item.Locations).ThenInclude(item => item.Location)
            .Include(item => item.Locations).ThenInclude(item => item.Attempts).SingleOrDefaultAsync(item => item.Id == campaignId, cancellationToken);
        return item is null ? null : new(item.Id, Folio(item.Number), item.Title, item.Notes, item.Status, item.CreatedAt, item.CreatedByUser.FullName,
            item.Locations.OrderBy(location => location.SortOrder).Select(location => new CycleCountLocationItem(location.Id, location.LocationId, location.Location.Code, location.Location.Description,
                location.Location.RowCode, location.Location.RackNumber, location.Status, location.Attempts.Count, location.AdjustmentMovementId,
                location.Attempts.Where(attempt => attempt.Status == CycleCountAttemptStatus.Counting).OrderByDescending(attempt => attempt.AttemptNumber)
                    .Select(attempt => (Guid?)attempt.Id).FirstOrDefault())).ToArray());
    }

    public async Task<CycleCountAttemptView?> GetAttemptAsync(Guid attemptId, bool includeExpected, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.CycleCountAttempts.AsNoTracking().Include(item => item.StartedByUser).Include(item => item.SubmittedByUser)
            .Include(item => item.Entries).ThenInclude(item => item.Product).ThenInclude(item => item.BaseUnit).SingleOrDefaultAsync(item => item.Id == attemptId, cancellationToken);
        if (item is null) return null;
        return new(item.Id, item.AttemptNumber, item.Status, item.StartedAt, item.StartedByUser.FullName, item.SubmittedAt, item.SubmittedByUser?.FullName,
            item.Entries.OrderBy(entry => entry.Product.Sku).Select(entry => new CycleCountEntryItem(entry.ProductId, entry.Product.Sku, entry.Product.Description, entry.Product.BaseUnit.Code,
                entry.Product.BaseUnit.AllowsDecimals, entry.CountedQuantity, includeExpected ? entry.ExpectedQuantity : null,
                includeExpected && entry.CountedQuantity is not null ? entry.CountedQuantity - entry.ExpectedQuantity : null, entry.IsUnexpectedProduct, includeExpected ? System.Text.Json.JsonSerializer.Deserialize<List<PalletSelection>>(entry.PlateCountsJson) : null, includeExpected && entry.HasPlateDifference)).ToArray());
    }

    public async Task<CycleCountAttemptView?> GetLatestAttemptAsync(Guid locationId, bool includeExpected, CancellationToken cancellationToken = default)
    {
        var attemptId = await dbContext.CycleCountAttempts.AsNoTracking().Where(item => item.CycleCountLocationId == locationId)
            .OrderByDescending(item => item.AttemptNumber).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken);
        return attemptId is Guid id ? await GetAttemptAsync(id, includeExpected, cancellationToken) : null;
    }

    public async Task<IReadOnlyList<SharedLocationConflict>> GetReviewSharingConflictsAsync(Guid cycleCountLocationId, CancellationToken cancellationToken = default)
    {
        var location = await dbContext.CycleCountLocations.AsNoTracking()
            .Include(item => item.Location)
            .Include(item => item.Attempts).ThenInclude(item => item.Entries).ThenInclude(item => item.Product)
            .SingleOrDefaultAsync(item => item.Id == cycleCountLocationId, cancellationToken);
        var attempt = location?.Attempts.OrderByDescending(item => item.AttemptNumber)
            .FirstOrDefault(item => item.Status == CycleCountAttemptStatus.Submitted);
        if (location is null || attempt is null) return [];
        var differences = attempt.Entries.Where(item => item.CountedQuantity != item.ExpectedQuantity || item.HasPlateDifference).ToArray();
        if (differences.Length == 0) return [];
        var pairs = differences.Select(item => new InventoryAssignmentKey(item.ProductId, location.LocationId)).ToArray();
        var products = differences.Select(item => item.Product).DistinctBy(item => item.Id).ToDictionary(item => item.Id);
        var locations = new Dictionary<Guid, Location> { [location.LocationId] = location.Location };
        return await new InventoryMovementStore(dbContext, timeProvider)
            .FindSharingConflictsAsync(pairs, products, locations, [], cancellationToken);
    }

    public async Task<IReadOnlyList<CycleCountExportRow>> GetExportRowsAsync(Guid campaignId, int maximumRows, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.CycleCountEntries.AsNoTracking()
            .Where(item => item.CycleCountAttempt.CycleCountLocation.CampaignId == campaignId)
            .OrderBy(item => item.CycleCountAttempt.CycleCountLocation.Location.Code).ThenBy(item => item.CycleCountAttempt.AttemptNumber).ThenBy(item => item.Product.Sku)
            .Take(maximumRows + 1)
            .Select(item => new
            {
                item.CycleCountAttempt.CycleCountLocation.Campaign.Number,
                LocationCode = item.CycleCountAttempt.CycleCountLocation.Location.Code,
                item.CycleCountAttempt.AttemptNumber,
                item.Product.Sku,
                item.Product.Description,
                UnitCode = item.Unit.Code,
                item.ExpectedQuantity,
                item.CountedQuantity,
                item.IsUnexpectedProduct,
                LocationStatus = item.CycleCountAttempt.CycleCountLocation.Status,
                item.CycleCountAttempt.StartedAt,
                item.CycleCountAttempt.SubmittedAt
            }).ToListAsync(cancellationToken);
        return rows.Select(item => new CycleCountExportRow(Folio(item.Number), item.LocationCode, item.AttemptNumber, item.Sku, item.Description, item.UnitCode,
            item.ExpectedQuantity, item.CountedQuantity, item.CountedQuantity is null ? null : item.CountedQuantity - item.ExpectedQuantity,
            item.IsUnexpectedProduct, item.LocationStatus, item.StartedAt, item.SubmittedAt)).ToArray();
    }

    private async Task<CycleCountResult> ChangeCampaignStateAsync(Guid campaignId, Guid operationId, string pin, CycleCountCampaignStatus expected, CycleCountCampaignStatus next, CycleCountActionType actionType, CancellationToken cancellationToken)
    {
        var user = await AuthenticateAsync(pin, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin);
        var duplicate = await GetActionResultAsync(operationId, campaignId, cancellationToken);
        if (duplicate is not null) return duplicate;
        var campaign = await dbContext.CycleCountCampaigns.SingleOrDefaultAsync(item => item.Id == campaignId, cancellationToken);
        if (campaign is null) return new(CycleCountStatus.NotFound);
        if (campaign.Status != expected) return new(CycleCountStatus.InvalidState, campaignId);
        var now = timeProvider.GetUtcNow();
        campaign.Status = next;
        campaign.ReleasedAt = now;
        campaign.LastActionByUserId = user.Id;
        AddAction(campaign, null, null, actionType, user.Id, now, null, operationId);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(CycleCountStatus.Success, campaignId);
    }

    private async Task<CycleCountResult> MarkStaleAsync(CycleCountAttempt attempt, Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        attempt.Status = CycleCountAttemptStatus.Superseded;
        var location = attempt.CycleCountLocation;
        location.Status = CycleCountLocationStatus.Stale;
        location.LastActionByUserId = userId;
        location.Campaign.Status = CycleCountCampaignStatus.InProgress;
        location.Campaign.LastActionByUserId = userId;
        AddAction(location.Campaign, location, attempt, CycleCountActionType.StaleDetected, userId, now, "El saldo cambió durante el conteo.");
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(CycleCountStatus.BalanceChanged, location.CampaignId, location.Id, attempt.Id, Errors: ["El saldo cambió. Inicia un reconteo ciego antes de autorizar."]);
    }

    private async Task<bool> SubmissionMatchesAsync(Guid attemptId, IReadOnlyList<CycleCountQuantityCommand> submitted, bool empty, CancellationToken token)
    {
        var saved = await dbContext.CycleCountEntries.AsNoTracking().Where(x => x.CycleCountAttemptId == attemptId).ToListAsync(token);
        if (empty) return saved.All(x => x.CountedQuantity == 0);
        if (saved.Count != submitted.Count || submitted.Select(x => x.ProductId).Distinct().Count() != submitted.Count) return false;
        return saved.All(x => submitted.Any(s => s.ProductId == x.ProductId && s.Quantity == x.CountedQuantity));
    }

    private async Task<bool> VersionsMatchAsync(CycleCountAttempt attempt, CancellationToken cancellationToken)
    {
        foreach (var entry in attempt.Entries)
        {
            var current = await inventoryQuery.GetBalanceAsync(entry.ProductId, attempt.CycleCountLocation.LocationId, cancellationToken);
            if (current.Version != entry.ExpectedBalanceVersion) return false;
        }
        return true;
    }

    private static List<string> ValidateSubmission(IReadOnlyList<CycleCountQuantityCommand> entries, bool isLocationEmpty, CycleCountAttempt attempt) =>
        ValidateQuantities(
            [.. attempt.Entries.Select(item => new ExpectedCountLine(item.ProductId, item.Product.Sku, item.Product.BaseUnit.AllowsDecimals))],
            entries,
            isLocationEmpty);

    private static List<string> ValidateQuantities(IReadOnlyList<ExpectedCountLine> expected, IReadOnlyList<CycleCountQuantityCommand> entries, bool isLocationEmpty)
    {
        var errors = new List<string>();
        if (entries.GroupBy(item => item.ProductId).Any(group => group.Count() != 1)) errors.Add("No repitas un producto en el conteo.");
        if (!isLocationEmpty && expected.Any(item => !entries.Any(input => input.ProductId == item.ProductId))) errors.Add("Captura una cantidad, incluso cero, para todos los productos de la ubicación.");
        var expectedByProduct = expected.ToDictionary(item => item.ProductId);
        foreach (var item in entries)
        {
            if (item.ProductId == Guid.Empty || item.Quantity < 0 || decimal.Round(item.Quantity, 4) != item.Quantity || Math.Abs(item.Quantity) > InventoryMovementRules.MaximumQuantity)
                errors.Add("Una cantidad del conteo no es válida.");
            else if (expectedByProduct.TryGetValue(item.ProductId, out var line) && !line.AllowsDecimals && decimal.Truncate(item.Quantity) != item.Quantity)
                errors.Add($"La cantidad de {line.Sku} no respeta la unidad base.");
        }
        return errors;
    }

    private async Task<List<string>> ValidateUnexpectedProductsAsync(
        IReadOnlyList<CycleCountQuantityCommand> entries, IReadOnlyList<CycleCountPreparationEntry> prepared, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var unexpectedIds = entries.Select(item => item.ProductId).Except(prepared.Select(item => item.ProductId)).ToArray();
        if (unexpectedIds.Length == 0) return errors;
        var products = await dbContext.Products.AsNoTracking()
            .Where(item => unexpectedIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Sku, item.BaseUnit.AllowsDecimals })
            .ToListAsync(cancellationToken);
        if (products.Count != unexpectedIds.Length)
        {
            errors.Add("Uno de los productos inesperados no existe.");
            return errors;
        }
        errors.AddRange(ValidateQuantities(
            [.. products.Select(item => new ExpectedCountLine(item.Id, item.Sku, item.AllowsDecimals))],
            [.. entries.Where(item => unexpectedIds.Contains(item.ProductId))],
            isLocationEmpty: false));
        return errors;
    }

    private readonly record struct ExpectedCountLine(Guid ProductId, string Sku, bool AllowsDecimals);

    private async Task<IReadOnlyList<Guid>> GetKnownProductIdsAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var assigned = dbContext.ProductLocationAssignments.Where(item => item.LocationId == locationId && item.IsActive).Select(item => item.ProductId);
        var withBalance = dbContext.InventoryBalances.Where(item => item.LocationId == locationId && item.Quantity != 0).Select(item => item.ProductId);
        return await assigned.Union(withBalance).Distinct().ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> GetCountProductIdsAsync(Guid cycleCountLocationId, Guid locationId, CancellationToken cancellationToken)
    {
        var planned = await dbContext.CycleCountPlannedProducts.Where(item => item.CycleCountLocationId == cycleCountLocationId)
            .Select(item => item.ProductId).Distinct().ToListAsync(cancellationToken);
        return planned.Count != 0 ? planned : await GetKnownProductIdsAsync(locationId, cancellationToken);
    }

    private async Task AdvanceScheduledPlansAsync(Guid cycleCountLocationId, DateTimeOffset completedAt, Guid responsibleUserId, CancellationToken cancellationToken)
    {
        var dispatches = await dbContext.CycleCountPlannedProducts.Include(item => item.CycleCountPlan)
            .Include(item => item.CycleCountLocation).ThenInclude(item => item.Campaign)
            .Where(item => item.CycleCountLocationId == cycleCountLocationId && item.CycleCountPlanId != null).ToListAsync(cancellationToken);
        if (dispatches.Count == 0) return;
        var completedDate = await warehouseClock.GetDateAsync(completedAt, cancellationToken);
        foreach (var plan in dispatches.Select(item => item.CycleCountPlan!).DistinctBy(item => item.Id))
        {
            var previousDue = plan.NextDueDate;
            var nextDue = NextDueAfter(plan.AnchorDate, plan.Frequency, completedDate);
            if (previousDue == nextDue) continue;
            var now = timeProvider.GetUtcNow();
            plan.NextDueDate = nextDue;
            plan.UpdatedByUserId = responsibleUserId;
            plan.UpdatedAt = now;
            dbContext.CycleCountPlanEvents.Add(PlanEvent(plan, CycleCountPlanEventType.Advanced, responsibleUserId, now,
                plan.Frequency, plan.Frequency, plan.AnchorDate, plan.AnchorDate, previousDue, nextDue,
                plan.IsActive, plan.IsActive, dispatches.First(item => item.CycleCountPlanId == plan.Id).CycleCountLocation.CampaignId,
                cycleCountLocationId));
        }
    }

    private static DateOnly NextDueAfter(DateOnly anchor, CycleCountFrequency frequency, DateOnly completedDate)
    {
        var candidate = anchor;
        var step = 0;
        while (candidate <= completedDate) candidate = AddFrequency(anchor, frequency, ++step);
        return candidate;
    }

    // Se mantiene el cálculo desde el ancla para que 31 de enero vuelva a ser 31 de marzo.
    private static DateOnly AddFrequency(DateOnly anchor, CycleCountFrequency frequency, int step)
    {
        var months = frequency switch { CycleCountFrequency.Monthly => step, CycleCountFrequency.Quarterly => step * 3, CycleCountFrequency.Semiannual => step * 6, CycleCountFrequency.Annual => step * 12, _ => 0 };
        if (months == 0) return anchor.AddDays((frequency == CycleCountFrequency.Biweekly ? 14 : 7) * step);
        var target = anchor.AddMonths(months);
        return target;
    }

    private async Task<bool> IsActiveAdminAsync(Guid userId, CancellationToken cancellationToken)
    {
        return userId != Guid.Empty && await dbContext.Users.AsNoTracking()
            .AnyAsync(item => item.Id == userId && item.IsActive && item.Role.Code == "ADMIN", cancellationToken);
    }

    private static CycleCountPlanEvent PlanEvent(CycleCountPlan plan, CycleCountPlanEventType type,
        Guid? responsibleUserId, DateTimeOffset recordedAt, CycleCountFrequency? previousFrequency,
        CycleCountFrequency? newFrequency, DateOnly? previousAnchorDate, DateOnly? newAnchorDate,
        DateOnly? previousNextDueDate, DateOnly? newNextDueDate, bool? previousIsActive, bool? newIsActive,
        Guid? campaignId = null, Guid? cycleCountLocationId = null) => new()
        {
            CycleCountPlan = plan,
            CycleCountPlanId = plan.Id,
            Type = type,
            ResponsibleUserId = responsibleUserId,
            CampaignId = campaignId,
            CycleCountLocationId = cycleCountLocationId,
            PreviousFrequency = previousFrequency,
            NewFrequency = newFrequency,
            PreviousAnchorDate = previousAnchorDate,
            NewAnchorDate = newAnchorDate,
            PreviousNextDueDate = previousNextDueDate,
            NewNextDueDate = newNextDueDate,
            PreviousIsActive = previousIsActive,
            NewIsActive = newIsActive,
            RecordedAt = recordedAt
        };

    private sealed record CalendarProjection(Guid RowId, bool IsHistorical, Guid PlanId, Guid? CampaignId,
        Guid? CycleCountLocationId, DateOnly ScheduledFor, Guid ProductId, string Sku, string? Description,
        string UnitCode, string LocationCode, CycleCountFrequency Frequency, bool IsActive, bool IsBlocked,
        bool PlanHasOpenDispatch);

    private sealed record CalendarLocationProjection(Guid Id, CycleCountLocationStatus Status,
        DateTimeOffset? CompletedAt, bool IsInCampaign);

    private sealed record CountedEntryProjection(Guid CycleCountLocationId, Guid ProductId, int AttemptNumber, decimal? CountedQuantity);

    private async Task<short> UnitIdAsync(Guid productId, CancellationToken cancellationToken) =>
        await dbContext.Products.Where(item => item.Id == productId).Select(item => item.BaseUnitId).SingleAsync(cancellationToken);

    private static string FormatReason(CycleCountAdjustmentReason? reason, string? notes)
    {
        var label = reason switch
        {
            CycleCountAdjustmentReason.UnrecordedEntry => "Entrada no registrada",
            CycleCountAdjustmentReason.UnrecordedExit => "Salida no registrada",
            CycleCountAdjustmentReason.WrongLocation => "Producto en ubicación incorrecta",
            CycleCountAdjustmentReason.UnrecordedDamageOrScrap => "Daño o merma no registrada",
            CycleCountAdjustmentReason.CaptureOrUnitError => "Error de captura o unidad",
            CycleCountAdjustmentReason.Unknown => "Causa desconocida",
            CycleCountAdjustmentReason.Other => "Otro",
            _ => "Diferencia confirmada"
        };
        return string.IsNullOrWhiteSpace(notes) ? $"Causa: {label}." : $"Causa: {label}. {notes.Trim()}";
    }

    private async Task<IReadOnlyList<Location>> ResolveLocationsAsync(CreateCycleCountCommand command, CancellationToken cancellationToken)
    {
        var ids = command.LocationIds?.Where(item => item != Guid.Empty).Distinct().ToArray() ?? [];
        var rows = command.RowCodes?.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim().ToUpperInvariant()).Distinct().ToArray() ?? [];
        var racks = command.RackNumbers?.Distinct().ToArray() ?? [];
        return await dbContext.Locations.Where(item => ids.Contains(item.Id) || rows.Contains(item.RowCode!) || racks.Contains(item.RackNumber ?? -1)).ToListAsync(cancellationToken);
    }

    private async Task<User?> AuthenticateAsync(string pin, CancellationToken cancellationToken)
    {
        var user = await userPinService.AuthenticateAsync(pin, cancellationToken);
        return user?.Role.Code is "ADMIN" or "OPERATOR" ? user : null;
    }

    private Task<User?> FindAuthorizedUserAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Users.AsNoTracking().Include(item => item.Role)
            .SingleOrDefaultAsync(item => item.Id == userId && item.IsActive &&
                (item.Role.Code == "ADMIN" || item.Role.Code == "OPERATOR"), cancellationToken);

    private async Task RefreshCampaignStatusAsync(CycleCountCampaign campaign, Guid userId, CancellationToken cancellationToken)
    {
        var statuses = await dbContext.CycleCountLocations.AsNoTracking().Where(item => item.CampaignId == campaign.Id).Select(item => item.Status).ToListAsync(cancellationToken);
        var hasOpen = statuses.Any(status => status is not (CycleCountLocationStatus.Completed or CycleCountLocationStatus.Cancelled));
        if (hasOpen)
        {
            campaign.Status = statuses.Any(status => status == CycleCountLocationStatus.UnderReview)
                ? CycleCountCampaignStatus.UnderReview
                : CycleCountCampaignStatus.InProgress;
            return;
        }
        var now = timeProvider.GetUtcNow();
        campaign.Status = CycleCountCampaignStatus.Completed;
        campaign.CompletedAt = now;
        campaign.LastActionByUserId = userId;
        AddAction(campaign, null, null, CycleCountActionType.CampaignCompleted, userId, now, null);
    }

    private async Task<CycleCountResult?> GetActionResultAsync(Guid operationId, Guid? expectedCampaignId, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) return new(CycleCountStatus.ValidationFailed, Errors: ["El identificador de operación es obligatorio."]);
        var existing = await dbContext.CycleCountActions.AsNoTracking()
            .Where(item => item.OperationId == operationId)
            .Select(item => new { item.CampaignId, item.CycleCountLocationId, item.CycleCountAttemptId })
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is null) return null;
        return expectedCampaignId is null || expectedCampaignId == existing.CampaignId
            ? new(CycleCountStatus.Success, existing.CampaignId, existing.CycleCountLocationId, existing.CycleCountAttemptId)
            : new(CycleCountStatus.IdempotencyConflict);
    }

    private void AddAction(CycleCountCampaign campaign, CycleCountLocation? location, CycleCountAttempt? attempt, CycleCountActionType type, Guid userId, DateTimeOffset now, string? notes, Guid? operationId = null, Guid? reviewBatchId = null) =>
        dbContext.CycleCountActions.Add(new() { OperationId = operationId, CampaignId = campaign.Id, CycleCountLocationId = location?.Id, CycleCountAttemptId = attempt?.Id, ReviewBatchId = reviewBatchId, Type = type, ResponsibleUserId = userId, RecordedAt = now, Notes = notes });

    private static string Folio(long number) => $"CC-{number:D6}";
    private static string? Normalize(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
}
