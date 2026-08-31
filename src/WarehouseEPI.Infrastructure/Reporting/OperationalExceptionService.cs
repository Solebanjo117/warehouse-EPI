using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Reporting;

public sealed record OperationalExceptionFilter(
    OperationalExceptionStatus? Status = null,
    OperationalExceptionCategory? Category = null,
    OperationalExceptionSeverity? Severity = null,
    Guid? AssignedUserId = null,
    string? Search = null,
    int PageNumber = 1,
    int PageSize = 25);

public sealed record OperationalExceptionListItemDto(
    Guid Id, OperationalExceptionCategory Category, OperationalExceptionSeverity Severity,
    OperationalExceptionStatus Status, string PrimaryText, string SecondaryText, string? ValueText,
    string TargetUrl, Guid? AssignedUserId, string? AssignedUserName, DateTimeOffset FirstDetectedAt, DateTimeOffset LastDetectedAt,
    DateTimeOffset? ResolvedAt, uint Version);

public sealed record OperationalExceptionPageDto(
    IReadOnlyList<OperationalExceptionListItemDto> Items, int TotalCount, int PageNumber, int PageSize,
    int NewCount, int InProgressCount, int WaitingCount, int CriticalCount);

public sealed record OperationalExceptionEventDto(
    OperationalExceptionEventType Type, OperationalExceptionStatus? PreviousStatus, OperationalExceptionStatus? CurrentStatus,
    string? PreviousAssignedUserName, string? CurrentAssignedUserName, string? ActorUserName, string? Notes,
    DateTimeOffset RecordedAt);

public sealed record OperationalExceptionDetailDto(
    OperationalExceptionListItemDto Case, Guid? ProductId, Guid? LocationId, Guid? CycleCountLocationId,
    IReadOnlyList<OperationalExceptionEventDto> Events);

public sealed record OperationalExceptionAssigneeDto(Guid Id, string FullName, string RoleCode);

public sealed record OperationalExceptionUpdateCommand(
    Guid CaseId, Guid OperationId, Guid ActorUserId, Guid? AssignedUserId,
    OperationalExceptionStatus Status, string? Notes, uint Version);

public enum OperationalExceptionUpdateStatus { Success, AlreadyApplied, NotFound, InactiveAssignee, Invalid, ConcurrencyConflict }
public sealed record OperationalExceptionUpdateResult(OperationalExceptionUpdateStatus Status, string? Error = null);
public sealed record OperationalExceptionReconciliationResult(int Created, int Updated, int Resolved);

public sealed class OperationalExceptionService(
    WarehouseDbContext dbContext,
    OperationalAlertService alerts,
    TimeProvider timeProvider)
{
    private static readonly SemaphoreSlim ReconciliationGate = new(1, 1);

    public async Task<OperationalExceptionReconciliationResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await ReconciliationGate.WaitAsync(cancellationToken);
        try
        {
            // Materialize every condition before any database mutation. A failed alert query leaves cases untouched.
            var conditions = await alerts.GetActiveConditionsAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            var openCases = await dbContext.OperationalExceptionCases
                .Where(item => item.ResolvedAt == null)
                .ToListAsync(cancellationToken);
            var active = conditions.ToDictionary(item => (item.Category, item.ConditionKey));
            var existing = openCases.ToDictionary(item => (item.Category, item.ConditionKey));
            var created = 0;
            var updated = 0;
            var resolved = 0;

            foreach (var condition in conditions)
            {
                var key = (condition.Category, condition.ConditionKey);
                if (!existing.TryGetValue(key, out var exceptionCase))
                {
                    exceptionCase = new OperationalExceptionCase
                    {
                        Category = condition.Category,
                        Severity = condition.Severity,
                        ConditionKey = Fit(condition.ConditionKey, 220),
                        Status = OperationalExceptionStatus.New,
                        ProductId = condition.ProductId,
                        LocationId = condition.LocationId,
                        CycleCountLocationId = condition.CycleCountLocationId,
                        PrimaryText = Fit(condition.PrimaryText, 160),
                        SecondaryText = Fit(condition.SecondaryText, 200),
                        ValueText = FitOptional(condition.ValueText, 200),
                        TargetUrl = Fit(condition.TargetUrl, 1000),
                        FirstDetectedAt = now,
                        LastDetectedAt = now
                    };
                    dbContext.OperationalExceptionEvents.Add(new OperationalExceptionEvent
                    {
                        OperationalExceptionCaseId = exceptionCase.Id,
                        Type = OperationalExceptionEventType.Detected,
                        CurrentStatus = OperationalExceptionStatus.New,
                        RecordedAt = now
                    });
                    dbContext.OperationalExceptionCases.Add(exceptionCase);
                    created++;
                    continue;
                }

                if (Refresh(exceptionCase, condition, now)) updated++;
            }

            foreach (var exceptionCase in openCases.Where(item => !active.ContainsKey((item.Category, item.ConditionKey))))
            {
                var previousStatus = exceptionCase.Status;
                exceptionCase.Status = OperationalExceptionStatus.Resolved;
                exceptionCase.ResolvedAt = now;
                dbContext.OperationalExceptionEvents.Add(new OperationalExceptionEvent
                {
                    OperationalExceptionCaseId = exceptionCase.Id,
                    Type = OperationalExceptionEventType.AutoResolved,
                    PreviousStatus = previousStatus,
                    CurrentStatus = OperationalExceptionStatus.Resolved,
                    RecordedAt = now
                });
                resolved++;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return new(created, updated, resolved);
        }
        finally
        {
            ReconciliationGate.Release();
        }
    }

    public async Task<OperationalExceptionPageDto> GetPageAsync(OperationalExceptionFilter filter, CancellationToken cancellationToken = default)
    {
        var query = dbContext.OperationalExceptionCases.AsNoTracking().AsQueryable();
        query = filter.Status is null
            ? query.Where(item => item.ResolvedAt == null)
            : query.Where(item => item.Status == filter.Status);
        if (filter.Category is not null) query = query.Where(item => item.Category == filter.Category);
        if (filter.Severity is not null) query = query.Where(item => item.Severity == filter.Severity);
        if (filter.AssignedUserId is not null) query = query.Where(item => item.AssignedUserId == filter.AssignedUserId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            query = query.Where(item => item.PrimaryText.ToUpper().Contains(term) || item.SecondaryText.ToUpper().Contains(term));
        }

        var summary = await dbContext.OperationalExceptionCases.AsNoTracking().Where(item => item.ResolvedAt == null)
            .GroupBy(item => 1).Select(group => new
            {
                New = group.Count(item => item.Status == OperationalExceptionStatus.New),
                InProgress = group.Count(item => item.Status == OperationalExceptionStatus.InProgress),
                Waiting = group.Count(item => item.Status == OperationalExceptionStatus.Waiting),
                Critical = group.Count(item => item.Severity == OperationalExceptionSeverity.Critical)
            }).SingleOrDefaultAsync(cancellationToken);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var total = await query.CountAsync(cancellationToken);
        var page = Math.Clamp(Math.Max(1, filter.PageNumber), 1, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var rows = await query
            .OrderBy(item => item.Severity == OperationalExceptionSeverity.Critical ? 0 : item.Severity == OperationalExceptionSeverity.Warning ? 1 : 2)
            .ThenBy(item => item.Status == OperationalExceptionStatus.New ? 0 : item.Status == OperationalExceptionStatus.InProgress ? 1 : item.Status == OperationalExceptionStatus.Waiting ? 2 : 3)
            .ThenBy(item => item.FirstDetectedAt).ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new OperationalExceptionListItemDto(item.Id, item.Category, item.Severity, item.Status,
                item.PrimaryText, item.SecondaryText, item.ValueText, item.TargetUrl,
                item.AssignedUserId, item.AssignedUser == null ? null : item.AssignedUser.FullName, item.FirstDetectedAt, item.LastDetectedAt,
                item.ResolvedAt, item.Version)).ToListAsync(cancellationToken);
        return new(rows, total, page, pageSize, summary?.New ?? 0, summary?.InProgress ?? 0, summary?.Waiting ?? 0, summary?.Critical ?? 0);
    }

    public async Task<OperationalExceptionDetailDto?> GetDetailAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.OperationalExceptionCases.AsNoTracking().Where(entry => entry.Id == id)
            .Select(entry => new
            {
                Case = new OperationalExceptionListItemDto(entry.Id, entry.Category, entry.Severity, entry.Status,
                    entry.PrimaryText, entry.SecondaryText, entry.ValueText, entry.TargetUrl,
                    entry.AssignedUserId, entry.AssignedUser == null ? null : entry.AssignedUser.FullName, entry.FirstDetectedAt, entry.LastDetectedAt,
                    entry.ResolvedAt, entry.Version),
                entry.ProductId, entry.LocationId, entry.CycleCountLocationId,
                Events = entry.Events.OrderBy(history => history.RecordedAt).ThenBy(history => history.Id).Select(history =>
                    new OperationalExceptionEventDto(history.Type, history.PreviousStatus, history.CurrentStatus,
                        history.PreviousAssignedUser == null ? null : history.PreviousAssignedUser.FullName,
                        history.CurrentAssignedUser == null ? null : history.CurrentAssignedUser.FullName,
                        history.ActorUser == null ? null : history.ActorUser.FullName, history.Notes, history.RecordedAt)).ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        return item is null ? null : new(item.Case, item.ProductId, item.LocationId, item.CycleCountLocationId, item.Events);
    }

    public async Task<IReadOnlyList<OperationalExceptionAssigneeDto>> GetAssignableUsersAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.FullName)
            .Select(item => new OperationalExceptionAssigneeDto(item.Id, item.FullName, item.Role.Code)).ToListAsync(cancellationToken);

    public async Task<OperationalExceptionUpdateResult> UpdateAsync(OperationalExceptionUpdateCommand command, CancellationToken cancellationToken = default)
    {
        if (command.OperationId == Guid.Empty || command.ActorUserId == Guid.Empty)
            return new(OperationalExceptionUpdateStatus.Invalid, "La operación administrativa no es válida.");
        if (command.Status == OperationalExceptionStatus.Resolved)
            return new(OperationalExceptionUpdateStatus.Invalid, "Los casos se resuelven automáticamente cuando desaparece la condición.");
        var notes = string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim();
        if (command.Status == OperationalExceptionStatus.Waiting && notes is null)
            return new(OperationalExceptionUpdateStatus.Invalid, "Explica por qué el caso queda en espera.");
        if (notes?.Length > 500) return new(OperationalExceptionUpdateStatus.Invalid, "La nota no puede exceder 500 caracteres.");
        if (await dbContext.OperationalExceptionEvents.AsNoTracking().AnyAsync(item => item.OperationId == command.OperationId, cancellationToken))
            return new(OperationalExceptionUpdateStatus.AlreadyApplied);
        if (command.AssignedUserId is Guid assigneeId && !await dbContext.Users.AsNoTracking().AnyAsync(item => item.Id == assigneeId && item.IsActive, cancellationToken))
            return new(OperationalExceptionUpdateStatus.InactiveAssignee, "El responsable seleccionado ya no está activo.");

        var exceptionCase = await dbContext.OperationalExceptionCases.SingleOrDefaultAsync(item => item.Id == command.CaseId, cancellationToken);
        if (exceptionCase is null) return new(OperationalExceptionUpdateStatus.NotFound);
        if (exceptionCase.ResolvedAt is not null) return new(OperationalExceptionUpdateStatus.Invalid, "El caso ya fue resuelto automáticamente.");
        var previousStatus = exceptionCase.Status;
        if (command.Version != 0)
            dbContext.Entry(exceptionCase).Property(item => item.Version).OriginalValue = command.Version;
        var previousAssignee = exceptionCase.AssignedUserId;
        exceptionCase.Status = command.Status;
        exceptionCase.AssignedUserId = command.AssignedUserId;
        dbContext.OperationalExceptionEvents.Add(new OperationalExceptionEvent
        {
            OperationalExceptionCaseId = exceptionCase.Id,
            OperationId = command.OperationId,
            Type = OperationalExceptionEventType.TriageUpdated,
            PreviousStatus = previousStatus,
            CurrentStatus = command.Status,
            PreviousAssignedUserId = previousAssignee,
            CurrentAssignedUserId = command.AssignedUserId,
            ActorUserId = command.ActorUserId,
            Notes = notes,
            RecordedAt = timeProvider.GetUtcNow()
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(OperationalExceptionUpdateStatus.Success);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(OperationalExceptionUpdateStatus.ConcurrencyConflict, "El caso cambió; recarga antes de guardar.");
        }
    }

    private static bool Refresh(OperationalExceptionCase exceptionCase, OperationalAlertConditionDto condition, DateTimeOffset now)
    {
        var primaryText = Fit(condition.PrimaryText, 160);
        var secondaryText = Fit(condition.SecondaryText, 200);
        var valueText = FitOptional(condition.ValueText, 200);
        var targetUrl = Fit(condition.TargetUrl, 1000);
        var changed = exceptionCase.Severity != condition.Severity || exceptionCase.PrimaryText != primaryText ||
            exceptionCase.SecondaryText != secondaryText || exceptionCase.ValueText != valueText ||
            exceptionCase.TargetUrl != targetUrl;
        exceptionCase.Severity = condition.Severity;
        exceptionCase.PrimaryText = primaryText;
        exceptionCase.SecondaryText = secondaryText;
        exceptionCase.ValueText = valueText;
        exceptionCase.TargetUrl = targetUrl;
        exceptionCase.LastDetectedAt = now;
        return changed;
    }

    private static string Fit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private static string? FitOptional(string? value, int maximumLength) =>
        value is null ? null : Fit(value, maximumLength);
}
