using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed class CycleCountService(
    WarehouseDbContext dbContext,
    UserPinService userPinService,
    InventoryQueryService inventoryQuery,
    InventoryMovementService movementService,
    TimeProvider timeProvider)
{
    private static readonly CycleCountCampaignStatus[] OpenCampaignStatuses =
    [CycleCountCampaignStatus.Draft, CycleCountCampaignStatus.Released, CycleCountCampaignStatus.InProgress, CycleCountCampaignStatus.UnderReview];

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

        var productIds = await GetKnownProductIdsAsync(location.LocationId, cancellationToken);
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
        if (command.OperationId == Guid.Empty) return new(CycleCountStatus.ValidationFailed, Errors: ["El identificador de operación es obligatorio."]);
        var submitted = await dbContext.CycleCountAttempts.AsNoTracking()
            .Where(item => item.SubmissionOperationId == command.OperationId)
            .Select(item => new { item.Id, item.CycleCountLocationId, item.CycleCountLocation.CampaignId })
            .SingleOrDefaultAsync(cancellationToken);
        if (submitted is not null)
            return submitted.Id == command.AttemptId
                ? new(CycleCountStatus.Success, submitted.CampaignId, submitted.CycleCountLocationId, submitted.Id)
                : new(CycleCountStatus.IdempotencyConflict);
        var attempt = await dbContext.CycleCountAttempts.Include(item => item.Entries).ThenInclude(item => item.Product).ThenInclude(item => item.BaseUnit)
            .Include(item => item.CycleCountLocation).ThenInclude(item => item.Campaign)
            .SingleOrDefaultAsync(item => item.Id == command.AttemptId, cancellationToken);
        if (attempt is null) return new(CycleCountStatus.NotFound);
        if (attempt.Status != CycleCountAttemptStatus.Counting) return new(CycleCountStatus.InvalidState, attempt.CycleCountLocation.CampaignId, attempt.CycleCountLocationId, attempt.Id);

        var errors = ValidateSubmission(command, attempt);
        if (errors.Count != 0) return new(CycleCountStatus.ValidationFailed, attempt.CycleCountLocation.CampaignId, attempt.CycleCountLocationId, attempt.Id, Errors: errors);
        var entriesByProduct = command.Entries.GroupBy(item => item.ProductId).ToDictionary(group => group.Key, group => group.Single().Quantity);
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
            entry.CountedQuantity = command.IsLocationEmpty ? 0m : entriesByProduct[entry.ProductId];
        attempt.Status = CycleCountAttemptStatus.Submitted;
        attempt.SubmissionOperationId = command.OperationId;
        attempt.SubmittedByUserId = user.Id;
        attempt.SubmittedAt = timeProvider.GetUtcNow();
        var hasDifference = attempt.Entries.Any(item => item.CountedQuantity != item.ExpectedQuantity);
        var cycleLocation = attempt.CycleCountLocation;
        cycleLocation.Status = hasDifference ? CycleCountLocationStatus.UnderReview : CycleCountLocationStatus.Completed;
        cycleLocation.LastActionByUserId = user.Id;
        if (!hasDifference) cycleLocation.CompletedAt = attempt.SubmittedAt;
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
        if (location is null || location.Status is not (CycleCountLocationStatus.Pending or CycleCountLocationStatus.RecountRequested or CycleCountLocationStatus.Stale)) return null;
        var productIds = await GetKnownProductIdsAsync(location.LocationId, cancellationToken);
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
        if (command.OperationId == Guid.Empty) return new(CycleCountStatus.ValidationFailed, Errors: ["El identificador de operación es obligatorio."]);
        var existing = await dbContext.CycleCountAttempts.AsNoTracking().Where(item => item.SubmissionOperationId == command.OperationId)
            .Select(item => new { item.Id, item.CycleCountLocationId, item.CycleCountLocation.CampaignId }).SingleOrDefaultAsync(cancellationToken);
        if (existing is not null) return existing.CycleCountLocationId == command.Preparation.CycleCountLocationId
            ? new(CycleCountStatus.Success, existing.CampaignId, existing.CycleCountLocationId, existing.Id) : new(CycleCountStatus.IdempotencyConflict);
        var location = await dbContext.CycleCountLocations.Include(item => item.Campaign).Include(item => item.Attempts)
            .SingleOrDefaultAsync(item => item.Id == command.Preparation.CycleCountLocationId && item.CampaignId == command.Preparation.CampaignId, cancellationToken);
        if (location is null) return new(CycleCountStatus.NotFound);
        if (location.Status is not (CycleCountLocationStatus.Pending or CycleCountLocationStatus.RecountRequested or CycleCountLocationStatus.Stale)) return new(CycleCountStatus.InvalidState, location.CampaignId, location.Id);

        // La captura se valida antes de persistir: un envío inválido no debe crear el intento
        // ni dejar la ubicación en Counting, porque el mismo token preparado se reenvía.
        var errors = ValidateQuantities(
            [.. command.Preparation.Entries.Select(item => new ExpectedCountLine(item.ProductId, item.Sku, item.AllowsDecimals))],
            command.Entries,
            command.IsLocationEmpty);
        if (errors.Count == 0)
            errors.AddRange(await ValidateUnexpectedProductsAsync(command.Entries, command.Preparation.Entries, cancellationToken));
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
            StartedAt = command.Preparation.PreparedAt
        };
        foreach (var entry in command.Preparation.Entries)
            attempt.Entries.Add(new() { ProductId = entry.ProductId, UnitId = await UnitIdAsync(entry.ProductId, cancellationToken), ExpectedQuantity = entry.ExpectedQuantity, ExpectedBalanceVersion = entry.ExpectedBalanceVersion });
        dbContext.CycleCountAttempts.Add(attempt);
        location.Status = CycleCountLocationStatus.Counting;
        location.Campaign.Status = CycleCountCampaignStatus.InProgress;
        AddAction(location.Campaign, location, attempt, CycleCountActionType.AttemptStarted, user.Id, command.Preparation.PreparedAt, "Preparación confirmada al enviar el conteo.");
        await dbContext.SaveChangesAsync(cancellationToken);
        var result = await SubmitAsync(new(attempt.Id, command.OperationId, command.Pin, command.Entries, command.IsLocationEmpty), cancellationToken);
        // Se confirma cualquier estado devuelto, incluido el marcado Stale, que es una
        // transición legítima; sólo una excepción revierte el intento recién creado.
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<CycleCountBatchResult> ReviewBatchAsync(ApproveCycleCountBatchCommand command, CancellationToken cancellationToken = default)
    {
        var user = await AuthenticateAsync(command.Pin, cancellationToken);
        if (user is null) return new(CycleCountStatus.InvalidPin, null, [], ["NIP no válido."]);
        if (command.Decisions.Any(item => item.Decision == CycleCountReviewDecision.Approve && item.Reason is null))
            return new(CycleCountStatus.ValidationFailed, null, [], ["Selecciona una causa para cada ajuste aprobado."]);
        if (command.Decisions.Any(item => item.Decision == CycleCountReviewDecision.Approve && item.Reason == CycleCountAdjustmentReason.Other && string.IsNullOrWhiteSpace(item.Notes)))
            return new(CycleCountStatus.ValidationFailed, null, [], ["Describe la causa cuando eliges Otro."]);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('|', command.Decisions.Select(item => $"{item.LocationId:N}:{item.OperationId:N}:{item.Decision}:{item.Reason}:{item.Notes}")))));
        var batch = await dbContext.CycleCountReviewBatches.SingleOrDefaultAsync(item => item.OperationId == command.OperationId, cancellationToken);
        if (batch is not null && batch.RequestFingerprint != fingerprint) return new(CycleCountStatus.IdempotencyConflict, batch.Id, [], ["El identificador de lote ya pertenece a otra solicitud."]);
        if (batch is null) { batch = new() { OperationId = command.OperationId, CampaignId = command.CampaignId, AuthorizedByUserId = user.Id, AuthorizedAt = timeProvider.GetUtcNow(), RequestFingerprint = fingerprint }; dbContext.CycleCountReviewBatches.Add(batch); await dbContext.SaveChangesAsync(cancellationToken); }
        var results = new List<CycleCountBatchItemResult>();
        foreach (var item in command.Decisions)
        {
            CycleCountResult result;
            if (item.Decision == CycleCountReviewDecision.Recount)
                result = await RequestRecountAsync(new(item.LocationId, item.OperationId, command.Pin, item.Notes), cancellationToken);
            else
                result = await ApproveAsync(new(item.LocationId, item.OperationId, command.Pin, FormatReason(item.Reason, item.Notes), item.ApprovedSharedAssignments), cancellationToken);
            if (item.Decision == CycleCountReviewDecision.Approve && result.Status == CycleCountStatus.Success)
            {
                var location = await dbContext.CycleCountLocations.SingleAsync(value => value.Id == item.LocationId, cancellationToken);
                location.AdjustmentReason = item.Reason;
                location.AdjustmentReasonNotes = Normalize(item.Notes, 500);
                var action = await dbContext.CycleCountActions.Where(value => value.CycleCountLocationId == item.LocationId && value.Type == CycleCountActionType.AdjustmentApproved && value.ReviewBatchId == null)
                    .OrderByDescending(value => value.RecordedAt).FirstOrDefaultAsync(cancellationToken);
                if (action is not null) action.ReviewBatchId = batch.Id;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            results.Add(new(item.LocationId, result.Status, result.MovementId, result.ValidationErrors, result.Conflicts));
        }
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
        AddAction(location.Campaign, location, null, CycleCountActionType.RecountRequested, user.Id, now, Normalize(command.Notes, 500), command.OperationId);
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
        var differences = attempt.Entries.Where(item => item.CountedQuantity != item.ExpectedQuantity).ToArray();
        if (differences.Length == 0) return new(CycleCountStatus.InvalidState, location.CampaignId, location.Id, attempt.Id);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var movement = await movementService.ConfirmAsync(new(
            command.OperationId,
            InventoryMovementType.Adjustment,
            command.Pin,
            differences.Select(item => new InventoryMovementLineCommand(item.ProductId, item.CountedQuantity!.Value, LocationId: location.LocationId, ExpectedBalanceVersion: item.ExpectedBalanceVersion)).ToArray(),
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
        location.LastActionByUserId = user.Id;
        location.Campaign.LastActionByUserId = user.Id;
        AddAction(location.Campaign, location, attempt, CycleCountActionType.AdjustmentApproved, user.Id, now, Normalize(command.Notes, 500));
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
                includeExpected && entry.CountedQuantity is not null ? entry.CountedQuantity - entry.ExpectedQuantity : null, entry.IsUnexpectedProduct)).ToArray());
    }

    public async Task<CycleCountAttemptView?> GetLatestAttemptAsync(Guid locationId, bool includeExpected, CancellationToken cancellationToken = default)
    {
        var attemptId = await dbContext.CycleCountAttempts.AsNoTracking().Where(item => item.CycleCountLocationId == locationId)
            .OrderByDescending(item => item.AttemptNumber).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken);
        return attemptId is Guid id ? await GetAttemptAsync(id, includeExpected, cancellationToken) : null;
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

    private async Task<bool> VersionsMatchAsync(CycleCountAttempt attempt, CancellationToken cancellationToken)
    {
        foreach (var entry in attempt.Entries)
        {
            var current = await inventoryQuery.GetBalanceAsync(entry.ProductId, attempt.CycleCountLocation.LocationId, cancellationToken);
            if (current.Version != entry.ExpectedBalanceVersion) return false;
        }
        return true;
    }

    private static List<string> ValidateSubmission(SubmitCycleCountCommand command, CycleCountAttempt attempt) =>
        ValidateQuantities(
            [.. attempt.Entries.Select(item => new ExpectedCountLine(item.ProductId, item.Product.Sku, item.Product.BaseUnit.AllowsDecimals))],
            command.Entries,
            command.IsLocationEmpty);

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

    private void AddAction(CycleCountCampaign campaign, CycleCountLocation? location, CycleCountAttempt? attempt, CycleCountActionType type, Guid userId, DateTimeOffset now, string? notes, Guid? operationId = null) =>
        dbContext.CycleCountActions.Add(new() { OperationId = operationId, CampaignId = campaign.Id, CycleCountLocationId = location?.Id, CycleCountAttemptId = attempt?.Id, Type = type, ResponsibleUserId = userId, RecordedAt = now, Notes = notes });

    private static string Folio(long number) => $"CC-{number:D6}";
    private static string? Normalize(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
}
