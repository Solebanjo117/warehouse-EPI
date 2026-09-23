using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionImportDraftView(Guid Id, string FileName, ProductionImportDraftStatus Status,
    int Version, ProductionScheduleImportResolutions Resolutions, ProductionScheduleImportPreview Preview,
    string ReviewedFingerprint, bool IsCurrent, Guid? BatchId);
public sealed record ProductionImportDraftSummary(Guid Id, string FileName, ProductionImportDraftStatus Status, DateTimeOffset UpdatedAt);
public sealed record ProductionImportConfirmCommand(Guid DraftId, int ExpectedVersion, string Fingerprint, Guid OperationId, Guid ActorId);

public sealed class ProductionImportDraftService(WarehouseDbContext db, ProductionScheduleImportService importer, TimeProvider clock)
{
    public const int MaxBytes = 15 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new();

    public async Task<IReadOnlyList<ProductionImportDraftSummary>> ListAsync(Guid actor, CancellationToken token = default)
    {
        if (!await IsAdmin(actor, token)) return [];
        return await db.ProductionImportDrafts.AsNoTracking().Where(x => x.OwnerId == actor)
            .OrderByDescending(x => x.UpdatedAt).Select(x => new ProductionImportDraftSummary(x.Id, x.FileName, x.Status, x.UpdatedAt))
            .ToListAsync(token);
    }

    public async Task<Guid> CreateAsync(string fileName, byte[] bytes, Guid actor, CancellationToken token = default)
    {
        if (!await IsAdmin(actor, token)) throw new UnauthorizedAccessException();
        if (bytes.Length is 0 or > MaxBytes) throw new ArgumentException("Selecciona un archivo XLSX de hasta 15 MB.", nameof(bytes));
        var draft = new ProductionImportDraft { OwnerId = actor, FileName = Path.GetFileName(fileName),
            FileHash = Convert.ToHexString(SHA256.HashData(bytes)), FileBytes = bytes,
            CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        var preview = await ReadAsync(draft, ProductionScheduleImportResolutions.None, token);
        AddRevision(draft, ProductionScheduleImportResolutions.None, preview, actor, "Uploaded");
        db.ProductionImportDrafts.Add(draft);
        await db.SaveChangesAsync(token);
        return draft.Id;
    }

    public async Task<ProductionImportDraftView?> GetAsync(Guid id, Guid actor, CancellationToken token = default)
    {
        var draft = await Owned(id, actor, false, token);
        if (draft is null) return null;
        var revision = draft.Revisions.MaxBy(x => x.Number)!;
        var resolutions = JsonSerializer.Deserialize<ProductionScheduleImportResolutions>(revision.ResolutionsJson, Json)!;
        var stored = JsonSerializer.Deserialize<ProductionScheduleImportPreview>(revision.PreviewJson, Json)!;
        var editable = draft.Status is ProductionImportDraftStatus.Reviewing or ProductionImportDraftStatus.Ready;
        var fresh = editable ? await ReadAsync(draft, resolutions, token) : stored;
        return new(draft.Id, draft.FileName, draft.Status, draft.Version, resolutions, fresh,
            revision.Fingerprint, fresh.Fingerprint == revision.Fingerprint, draft.BatchId);
    }

    public async Task<ProductionDailyCommandResult> ReviseAsync(Guid id, int expected, Guid actor,
        ProductionScheduleImportResolutions resolutions, bool discard = false, CancellationToken token = default)
    {
        var draft = await Owned(id, actor, true, token);
        if (!Editable(draft, expected)) return Conflict();
        var preview = await ReadAsync(draft!, resolutions, token);
        AddRevision(draft!, resolutions, preview, actor, discard ? "Discarded" : "Reviewed");
        if (discard) draft!.Status = ProductionImportDraftStatus.Discarded;
        try { await db.SaveChangesAsync(token); return new(ProductionDailyCommandStatus.Success, id); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return Conflict(); }
    }

    public async Task<ProductionDailyCommandResult> DeleteAsync(Guid id, Guid actor, CancellationToken token = default)
    {
        if (!await IsAdmin(actor, token)) return Conflict();
        var draft = await db.ProductionImportDrafts.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == actor, token);
        if (draft is null) return new(ProductionDailyCommandStatus.NotFound, Errors: ["El borrador ya no existe."]);
        if (draft.Status == ProductionImportDraftStatus.Confirmed)
            return new(ProductionDailyCommandStatus.ValidationFailed, Errors: ["Las importaciones confirmadas se conservan como registro y no se pueden eliminar."]);
        // Revisions are deleted by key so their stored previews are not loaded only to be discarded.
        var revisions = await db.ProductionImportRevisions.Where(x => x.DraftId == id).Select(x => x.Id).ToListAsync(token);
        db.ProductionImportRevisions.RemoveRange(revisions.Select(revision => db.ProductionImportRevisions.Local.FirstOrDefault(x => x.Id == revision) ??
            new ProductionImportRevision { Id = revision, DraftId = id, Action = "", ResolutionsJson = "", PreviewJson = "", Fingerprint = "" }));
        db.ProductionImportDrafts.Remove(draft);
        try { await db.SaveChangesAsync(token); return new(ProductionDailyCommandStatus.Success, id); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return Conflict(); }
    }

    public async Task<ProductionDailyCommandResult> ConfirmAsync(ProductionImportConfirmCommand command, CancellationToken token = default)
    {
        if (!await IsAdmin(command.ActorId, token)) return Conflict();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        try
        {
            var draft = await Owned(command.DraftId, command.ActorId, true, token);
            if (draft is null) return Conflict();
            if (draft.Status == ProductionImportDraftStatus.Confirmed)
            {
                var final = draft.Revisions.MaxBy(x => x.Number)!;
                return final.OperationId == command.OperationId && final.Fingerprint == command.Fingerprint
                    ? new(ProductionDailyCommandStatus.Success, draft.BatchId) : new(ProductionDailyCommandStatus.IdempotencyConflict);
            }
            if (!Editable(draft, command.ExpectedVersion)) return Conflict();
            var reviewed = draft.Revisions.MaxBy(x => x.Number)!;
            if (reviewed.Fingerprint != command.Fingerprint) return Conflict();
            var resolutions = JsonSerializer.Deserialize<ProductionScheduleImportResolutions>(reviewed.ResolutionsJson, Json)!;
            var fresh = await ReadAsync(draft, resolutions, token);
            if (fresh.Fingerprint != reviewed.Fingerprint)
            {
                AddRevision(draft, resolutions, fresh, command.ActorId, "DependenciesChanged");
                await db.SaveChangesAsync(token);
                if (transaction is not null) await transaction.CommitAsync(token);
                return Conflict();
            }
            if (!fresh.CanConfirm) return new(ProductionDailyCommandStatus.ValidationFailed, Errors: ["Resuelve las discrepancias antes de confirmar."]);
            if (await db.ProductionImportRevisions.AnyAsync(x => x.OperationId == command.OperationId, token))
                return new(ProductionDailyCommandStatus.IdempotencyConflict);
            var result = await importer.ConfirmAsync(fresh, command.OperationId, command.ActorId, token);
            if (!result.Success) return result;
            var batch = await db.ProductionScheduleImportBatches.SingleAsync(x => x.OperationId == command.OperationId, token);
            AddRevision(draft, resolutions, fresh, command.ActorId, "Confirmed", command.OperationId);
            draft.Status = ProductionImportDraftStatus.Confirmed;
            draft.BatchId = batch.Id;
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, batch.Id);
        }
        catch (Exception ex) when (IsConflict(ex))
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return Conflict();
        }
    }

    private async Task<ProductionScheduleImportPreview> ReadAsync(ProductionImportDraft draft, ProductionScheduleImportResolutions resolutions, CancellationToken token)
    {
        using var stream = new MemoryStream(draft.FileBytes, writable: false);
        var preview = await importer.PreviewAsync(stream, draft.FileName, resolutions, token);
        if (await db.ProductionScheduleImportBatches.AnyAsync(x => x.FileHash == draft.FileHash, token))
            preview = preview with { Issues = preview.Issues.Append(new("Archivo", null, "Este archivo ya fue importado; no se reemplazarán las semanas existentes.")).ToArray() };
        else if (await db.ProductionScheduleWeeks.AnyAsync(x => preview.Weeks.Select(w => w.WeekStart).Contains(x.WeekStart), token))
            preview = preview with { Issues = preview.Issues.Append(new("Archivo", null, "Ya existe una de las semanas del archivo. La carga inicial no reemplaza datos existentes.")).ToArray() };
        return preview with { Fingerprint = ProductionScheduleImportService.CanonicalHash(new { preview.Fingerprint, preview.Issues }) };
    }

    private async Task<ProductionImportDraft?> Owned(Guid id, Guid actor, bool tracking, CancellationToken token)
    {
        if (!await IsAdmin(actor, token)) return null;
        var query = db.ProductionImportDrafts.Include(x => x.Revisions.OrderByDescending(r => r.Number).Take(1))
            .Where(x => x.Id == id && x.OwnerId == actor);
        return await (tracking ? query : query.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    private Task<bool> IsAdmin(Guid actor, CancellationToken token) => db.Users.AnyAsync(x => x.Id == actor && x.IsActive && x.Role.Code == "ADMIN", token);
    private static bool Editable(ProductionImportDraft? draft, int version) => draft is not null && draft.Version == version &&
        draft.Status is ProductionImportDraftStatus.Reviewing or ProductionImportDraftStatus.Ready;
    private static ProductionDailyCommandResult Conflict() => new(ProductionDailyCommandStatus.ConcurrencyConflict,
        Errors: ["La revisión cambió o no está disponible. Recarga y vuelve a revisar antes de continuar."]);
    private static bool IsConflict(Exception exception) => exception is DbUpdateException ||
        exception is PostgresException { SqlState: "40001" or "40P01" } || exception.InnerException is not null && IsConflict(exception.InnerException);
    private void AddRevision(ProductionImportDraft draft, ProductionScheduleImportResolutions resolutions,
        ProductionScheduleImportPreview preview, Guid actor, string action, Guid? operation = null)
    {
        draft.Version++;
        draft.UpdatedAt = clock.GetUtcNow();
        draft.Status = preview.CanConfirm ? ProductionImportDraftStatus.Ready : ProductionImportDraftStatus.Reviewing;
        var revision = new ProductionImportRevision { DraftId = draft.Id, Number = draft.Version, ActorId = actor,
            CreatedAt = draft.UpdatedAt, Action = action, ResolutionsJson = JsonSerializer.Serialize(resolutions, Json),
            PreviewJson = JsonSerializer.Serialize(preview, Json), Fingerprint = preview.Fingerprint, OperationId = operation };
        draft.Revisions.Add(revision);
        db.Entry(revision).State = EntityState.Added;
    }
}
