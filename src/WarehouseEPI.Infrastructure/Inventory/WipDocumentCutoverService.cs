using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record WipCutoverRow(Guid LocationId, string Location, Guid ProductId, string Product,
    Guid? LotId, decimal Quantity, decimal PlateQuantity, string Unit);
public sealed record WipCutoverPreview(string Revision, IReadOnlyList<WipCutoverRow> Rows,
    IReadOnlyList<string> Errors, bool Completed);
public sealed record WipCutoverCommand(Guid OperationId, string Revision, string Pin, string Reason);

public sealed class WipDocumentCutoverService(WarehouseDbContext db, UserPinService pins, TimeProvider timeProvider)
{
    public async Task<WipCutoverPreview> PreviewAsync(CancellationToken token = default)
    {
        var balances = await db.InventoryBalances.AsNoTracking().Include(x => x.Location).Include(x => x.Product).ThenInclude(x => x.BaseUnit)
            .Where(x => x.Location.OperationalRole == LocationOperationalRole.Wip).OrderBy(x => x.LocationId).ThenBy(x => x.ProductId).ThenBy(x => x.LotId).ToListAsync(token);
        var plates = await db.PalletPlates.AsNoTracking().Include(x => x.Lots)
            .Where(x => x.Location.OperationalRole == LocationOperationalRole.Wip).OrderBy(x => x.Id).ToListAsync(token);
        var assignments = await db.ProductLocationAssignments.AsNoTracking().Where(x => x.Location.OperationalRole == LocationOperationalRole.Wip)
            .OrderBy(x => x.LocationId).ThenBy(x => x.ProductId).Select(x => new { x.LocationId, x.ProductId, x.IsActive, x.UpdatedAt }).ToListAsync(token);
        var links = await db.ProductionMaterialIssueLinks.AsNoTracking().Include(x => x.OperationLines).ThenInclude(x => x.Operation)
            .Include(x => x.Lots).ThenInclude(x => x.Lot)
            .Include(x => x.OperationLines).ThenInclude(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
            .OrderBy(x => x.Id).ToListAsync(token);
        var reversed = await db.ProductionMaterialOperations.Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        var documentedLinks = await db.WipDocumentAssignments.Select(x => x.IssueLinkId).Distinct().ToListAsync(token);
        var rows = balances.Select(x => new WipCutoverRow(x.LocationId, x.Location.Code, x.ProductId, x.Product.Sku, x.LotId, x.Quantity,
            plates.Where(p => p.LocationId == x.LocationId && p.ProductId == x.ProductId && !p.IsVoided).SelectMany(p => p.Lots).Where(l => l.LotId == x.LotId).Sum(l => l.Quantity), x.Product.BaseUnit.Code)).ToArray();
        var errors = new List<string>();
        if (links.Any(x => Pending(x, reversed) < 0)) errors.Add("Una orden tiene compromisos documentales negativos. Concilia antes del corte.");
        foreach (var row in rows)
        {
            if (row.Quantity < 0) errors.Add($"{row.Location} · {row.Product}: saldo negativo; concilia antes del corte.");
            if (plates.Any(p => p.LocationId == row.LocationId && p.ProductId == row.ProductId && !p.IsVoided) && row.Quantity != row.PlateQuantity)
                errors.Add($"{row.Location} · {row.Product}: las placas no coinciden con el saldo por lote.");
        }
        foreach (var plate in plates.Where(x => !x.IsVoided))
            if (plate.Quantity != plate.Lots.Sum(x => x.Quantity) || plate.Quantity < 0 || plate.Lots.Any(x => x.Quantity < 0 ||
                (x.Quantity != 0 && !rows.Any(r => r.LocationId == plate.LocationId && r.ProductId == plate.ProductId && r.LotId == x.LotId))))
                errors.Add($"{plate.Identifier}: composición de lotes inconsistente.");
        foreach (var group in links.Where(x => !documentedLinks.Contains(x.Id)).GroupBy(x => new { x.WipLocationId, x.ProductId }))
        {
            var pending = group.Sum(x => Pending(x, reversed));
            if (pending > rows.Where(x => x.LocationId == group.Key.WipLocationId && x.ProductId == group.Key.ProductId).Sum(x => x.Quantity))
                errors.Add("Los compromisos documentales de una orden superan las existencias a convertir.");
        }
        var remainingByLot = rows.ToDictionary(x => (x.LocationId, x.ProductId, x.LotId), x => x.Quantity);
        foreach (var issue in links.Where(x => !documentedLinks.Contains(x.Id) && Pending(x, reversed) > 0))
        {
            var lots = InventoryMovementService.RemainingLots(issue, reversed).ToArray();
            if (lots.Sum(x => x.Quantity) != Pending(issue, reversed))
                errors.Add("Los lotes pendientes de una orden no coinciden con su cantidad documental.");
            foreach (var lot in lots)
            {
                var key = (issue.WipLocationId, issue.ProductId, (Guid?)lot.LotId);
                if (remainingByLot.GetValueOrDefault(key) < lot.Quantity)
                    errors.Add("Los compromisos por lote de una orden superan las existencias a convertir.");
                remainingByLot[key] = remainingByLot.GetValueOrDefault(key) - lot.Quantity;
            }
        }
        if (await db.ProductionWarehouseReservations.AnyAsync(x => x.Location.OperationalRole == LocationOperationalRole.Wip && x.Quantity > x.ReleasedQuantity, token))
            errors.Add("Hay reservas de almacén en WIP. Resuélvelas antes del corte.");
        var snapshot = JsonSerializer.Serialize(new
        {
            Balances = balances.Select(x => new { x.ProductId, x.LocationId, x.LotId, x.Quantity, x.Version }),
            Plates = plates.Select(x => new { x.Id, x.Version, State = PalletPlateEngine.State(x) }),
            Assignments = assignments,
            Links = links.Select(x => new { x.Id, x.Quantity, x.CancelledQuantity, Pending = Pending(x, reversed) }),
            Dispositions = await db.WipDispositions.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.OperationId, x.Quantity, x.ReversesDispositionId }).ToListAsync(token),
            Applications = await db.WipDocumentApplications.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.OperationId, x.DocumentId, x.Quantity, x.ReversesApplicationId }).ToListAsync(token)
        });
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))), rows, errors.Distinct().ToArray(),
            await db.WipDocumentCutovers.AnyAsync(token));
    }

    public async Task<InventoryMovementResult> ConfirmAsync(WipCutoverCommand command, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code != "ADMIN") return new(InventoryMovementStatus.InvalidPin);
        if (command.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 500)
            return new(InventoryMovementStatus.ValidationFailed, Errors: ["Indica la operación y un motivo de hasta 500 caracteres."]);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            // One database-wide maintenance transaction: no capture can interleave with the snapshot and compensation.
            if (tx is not null)
                await db.Database.ExecuteSqlRawAsync("LOCK TABLE locations, inventory_balances, pallet_plates, pallet_plate_lots, product_location_assignments, inventory_movements, production_material_issue_links, production_material_operations, production_warehouse_reservations, wip_documents, wip_document_applications, wip_document_assignments, wip_document_lots, wip_dispositions, inventory_movement_lines, inventory_balance_changes, production_material_operation_lines, production_material_issue_lots, pallet_plate_events IN EXCLUSIVE MODE", token);
            var prior = await db.WipDocumentCutovers.SingleOrDefaultAsync(token);
            if (prior is not null)
                return prior.OperationId == command.OperationId && prior.Reason == command.Reason.Trim() && prior.ResponsibleUserId == user.Id && prior.Revision == command.Revision
                    ? new(InventoryMovementStatus.Success) : new(InventoryMovementStatus.IdempotencyConflict, Errors: ["El corte documental ya fue aplicado."]);
            var preview = await PreviewAsync(token);
            if (preview.Revision != command.Revision) return new(InventoryMovementStatus.BalanceChanged, Errors: ["Los datos cambiaron. Revisa nuevamente la vista previa."]);
            if (preview.Errors.Count > 0) return new(InventoryMovementStatus.ValidationFailed, Errors: preview.Errors);
            var now = timeProvider.GetUtcNow();
            var cutover = new WipDocumentCutover
            {
                OperationId = command.OperationId,
                ResponsibleUserId = user.Id,
                RecordedAt = now,
                Revision = preview.Revision,
                Reason = command.Reason.Trim()
            };
            var balances = await db.InventoryBalances.Include(x => x.Lot).Include(x => x.Product)
                .Where(x => x.Location.OperationalRole == LocationOperationalRole.Wip).ToListAsync(token);
            var plates = await db.PalletPlates.Include(x => x.Lots).Where(x => x.Location.OperationalRole == LocationOperationalRole.Wip).ToListAsync(token);
            var links = await db.ProductionMaterialIssueLinks.Include(x => x.OperationLines).ThenInclude(x => x.Operation)
                .Include(x => x.Lots).ThenInclude(x => x.Lot)
                .Include(x => x.OperationLines).ThenInclude(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
                .Include(x => x.InventoryMovementLine).ThenInclude(x => x!.Movement).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token);
            var reversed = await db.ProductionMaterialOperations.Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
            cutover.SnapshotJson = JsonSerializer.Serialize(new
            {
                Rows = preview.Rows,
                Assignments = await db.ProductLocationAssignments.AsNoTracking().Where(x => x.Location.OperationalRole == LocationOperationalRole.Wip).Select(x => new { x.ProductId, x.LocationId, x.IsActive, x.UpdatedAt }).ToListAsync(token),
                Plates = plates.Select(x => new { x.Id, x.Version, State = PalletPlateEngine.State(x) }),
                Links = links.Select(x => new { x.Id, x.InventoryMovementLineId, x.WorkOrderId, x.WorkOrderStageId, x.Quantity, x.CancelledQuantity, Pending = Pending(x, reversed) })
            });
            db.WipDocumentCutovers.Add(cutover);
            var available = balances.ToDictionary(x => (x.LocationId, x.ProductId, x.LotId), x => x.Quantity);
            foreach (var issue in links)
            {
                var pending = Pending(issue, reversed);
                if (pending <= 0) continue;
                if (await db.WipDocumentAssignments.AnyAsync(x => x.IssueLinkId == issue.Id, token)) continue;
                var document = new WipDocument
                {
                    CutoverId = cutover.Id,
                    MovementLineId = issue.InventoryMovementLineId,
                    ProductId = issue.ProductId,
                    WipLocationId = issue.WipLocationId,
                    Quantity = pending,
                    IsOpening = issue.InventoryMovementLineId is null,
                    ResponsibleUserId = user.Id,
                    OccurredAt = issue.InventoryMovementLine?.Movement.OccurredAt ?? now
                };
                var lots = InventoryMovementService.RemainingLots(issue, reversed).ToArray();
                if (lots.Sum(x => x.Quantity) != pending) throw new PalletPlateException("Los lotes pendientes de una orden no concuerdan con su cantidad documental.");
                foreach (var lot in lots)
                {
                    var key = (issue.WipLocationId, issue.ProductId, (Guid?)lot.LotId);
                    if (available.GetValueOrDefault(key) < lot.Quantity) throw new PalletPlateException("Los compromisos de lote de una orden superan las existencias a convertir.");
                    document.Lots.Add(new WipDocumentLot { LotId = lot.LotId, Quantity = lot.Quantity });
                    available[key] -= lot.Quantity;
                }
                document.Assignments.Add(new WipDocumentAssignment { IssueLinkId = issue.Id, Quantity = pending });
                db.WipDocuments.Add(document);
            }
            var knownLines = await db.InventoryMovementLines.Include(x => x.Movement).Include(x => x.BalanceChanges)
                .Where(x => x.Movement.Purpose == InventoryMovementPurpose.ProductionIssue && x.Movement.Type == InventoryMovementType.Transfer &&
                    !db.InventoryMovementCorrections.Any(c => c.OriginalMovementId == x.MovementId || c.ReversalMovementId == x.MovementId))
                .OrderBy(x => x.Id).ToListAsync(token);
            var documentedLines = db.ChangeTracker.Entries<WipDocument>().Select(x => x.Entity.MovementLineId).ToHashSet();
            var knownDocuments = new Dictionary<Guid, WipDocument>();
            foreach (var key in available.Keys.Where(x => available[x] > 0).ToArray())
            {
                var candidates = knownLines.Where(x => !documentedLines.Contains(x.Id) && x.ProductId == key.ProductId &&
                    x.Movement.OperationalAreaId == key.LocationId && x.BalanceChanges.Any(c => c.LocationId == key.LocationId && c.LotId == key.LotId && c.DeltaQuantity > 0)).ToArray();
                if (candidates.Length != 1) continue;
                var line = candidates[0];
                var take = Math.Min(available[key], line.BalanceChanges.Where(x => x.LocationId == key.LocationId && x.LotId == key.LotId && x.DeltaQuantity > 0).Sum(x => x.DeltaQuantity));
                if (!knownDocuments.TryGetValue(line.Id, out var document))
                {
                    document = new WipDocument
                    {
                        CutoverId = cutover.Id,
                        MovementLineId = line.Id,
                        ProductId = line.ProductId,
                        WipLocationId = key.LocationId,
                        ResponsibleUserId = line.Movement.ResponsibleUserId,
                        OccurredAt = line.Movement.OccurredAt
                    };
                    knownDocuments.Add(line.Id, document); db.WipDocuments.Add(document);
                }
                document.Quantity += take;
                document.Lots.Add(new WipDocumentLot { LotId = key.LotId, Quantity = take });
                available[key] -= take;
            }
            var legacyIssues = await db.InventoryMovementLines.Include(x => x.Movement).Include(x => x.BalanceChanges)
                .Where(x => x.Movement.Purpose == InventoryMovementPurpose.ProductionIssue && x.Movement.Type == InventoryMovementType.Exit &&
                    !db.WipDocuments.Any(d => d.MovementLineId == x.Id) &&
                    !db.InventoryMovementCorrections.Any(c => c.OriginalMovementId == x.MovementId || c.ReversalMovementId == x.MovementId))
                .ToListAsync(token);
            foreach (var line in legacyIssues.Where(x => !documentedLines.Contains(x.Id)))
            {
                var dispositions = await db.WipDispositions.Include(x => x.InventoryMovement).ThenInclude(x => x!.Lines).ThenInclude(x => x.BalanceChanges).Where(x => x.OriginalMovementLineId == line.Id && x.ReversesDispositionId == null &&
                    !db.WipDispositions.Any(r => r.ReversesDispositionId == x.Id)).OrderBy(x => x.RecordedAt).ThenBy(x => x.Id).ToListAsync(token);
                var document = new WipDocument
                {
                    CutoverId = cutover.Id,
                    MovementLineId = line.Id,
                    ProductId = line.ProductId,
                    WipLocationId = line.Movement.OperationalAreaId!.Value,
                    ResponsibleUserId = line.Movement.ResponsibleUserId,
                    OccurredAt = line.Movement.OccurredAt,
                    Quantity = line.Quantity,
                    Lots = line.BalanceChanges.Where(x => x.DeltaQuantity < 0).GroupBy(x => x.LotId)
                        .Select(x => new WipDocumentLot { LotId = x.Key, Quantity = -x.Sum(y => y.DeltaQuantity) }).ToList()
                };
                if (document.Lots.Sum(x => x.Quantity) != document.Quantity || dispositions.Sum(x => x.Quantity) > document.Quantity)
                    throw new PalletPlateException("Una entrega histórica sin saldo tiene lotes o devoluciones inconsistentes.");
                var remainingLots = document.Lots.ToDictionary(x => x.Id, x => x.Quantity);
                foreach (var disposition in dispositions)
                {
                    var left = disposition.Quantity;
                    foreach (var lot in document.Lots)
                    {
                        var take = Math.Min(left, remainingLots[lot.Id]); if (take <= 0) continue;
                        document.Applications.Add(new WipDocumentApplication
                        {
                            OperationId = disposition.OperationId,
                            Fingerprint = "legacy:" + disposition.Id,
                            LotId = lot.LotId,
                            Quantity = take,
                            InventoryMovementLineId = disposition.InventoryMovement?.Lines.FirstOrDefault(x => x.ProductId == line.ProductId && x.BalanceChanges.Any(c => c.LotId == lot.LotId))?.Id,
                            Kind = disposition.Type == WipDispositionType.WarehouseReturn ? WipDocumentApplicationKind.WarehouseReturn : WipDocumentApplicationKind.SupplierReturn,
                            ResponsibleUserId = disposition.ResponsibleUserId,
                            RecordedAt = disposition.RecordedAt,
                            Reference = disposition.Reference,
                            Notes = disposition.Notes
                        });
                        remainingLots[lot.Id] -= take; left -= take; if (left == 0) break;
                    }
                }
                db.WipDocuments.Add(document);
            }
            // Remaining quantities have no unambiguous order allocation. Preserve them as explicit openings,
            // retaining the cutover snapshot instead of inventing warehouse origins or consumption history.
            foreach (var group in available.Where(x => x.Value > 0).GroupBy(x => new { x.Key.LocationId, x.Key.ProductId }))
                db.WipDocuments.Add(new WipDocument
                {
                    CutoverId = cutover.Id,
                    ProductId = group.Key.ProductId,
                    WipLocationId = group.Key.LocationId,
                    Quantity = group.Sum(x => x.Value),
                    IsOpening = true,
                    ResponsibleUserId = user.Id,
                    OccurredAt = now,
                    Lots = group.Select(x => new WipDocumentLot { LotId = x.Key.LotId, Quantity = x.Value }).ToList()
                });
            var compensation = new InventoryMovement
            {
                OperationId = command.OperationId,
                RequestFingerprint = preview.Revision,
                Type = InventoryMovementType.Adjustment,
                Purpose = InventoryMovementPurpose.WipDocumentCutover,
                ResponsibleUserId = user.Id,
                Notes = command.Reason.Trim(),
                OccurredAt = now,
                RecordedAt = now
            };
            foreach (var balance in balances.Where(x => x.Quantity != 0))
            {
                var line = new InventoryMovementLine
                {
                    LineNumber = compensation.Lines.Count + 1,
                    ProductId = balance.ProductId,
                    UnitId = balance.Product.BaseUnitId,
                    LotId = balance.LotId,
                    Quantity = 0,
                    PreviousQuantity = balance.Quantity,
                    AdjustmentDelta = -balance.Quantity
                };
                line.BalanceChanges.Add(new InventoryBalanceChange
                {
                    LocationId = balance.LocationId,
                    LotId = balance.LotId,
                    LotNumberSnapshot = balance.Lot?.Number,
                    LotDateSnapshot = balance.Lot?.LotDate,
                    PreviousQuantity = balance.Quantity,
                    DeltaQuantity = -balance.Quantity,
                    ResultingQuantity = 0
                });
                compensation.Lines.Add(line); balance.Quantity = 0; balance.UpdatedAt = now;
            }
            if (compensation.Lines.Count > 0) db.InventoryMovements.Add(compensation);
            foreach (var plate in plates.Where(x => !x.IsVoided))
            {
                var before = PalletPlateEngine.State(plate);
                foreach (var lot in plate.Lots) lot.Quantity = 0;
                plate.Quantity = 0; plate.IsVoided = true;
                new PalletPlateEngine(db).Record(plate, before, command.OperationId, user.Id, now, "WIP_DOCUMENT_CUTOVER", fingerprint: preview.Revision);
            }
            foreach (var assignment in await db.ProductLocationAssignments.Where(x => x.Location.OperationalRole == LocationOperationalRole.Wip && x.IsActive).ToListAsync(token))
            { assignment.IsActive = false; assignment.UpdatedAt = now; }
            foreach (var product in await db.Products.Where(x => x.DefaultEntryLocation != null && x.DefaultEntryLocation.OperationalRole == LocationOperationalRole.Wip).ToListAsync(token))
                product.DefaultEntryLocationId = null;
            await db.SaveChangesAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
            return new(InventoryMovementStatus.Success);
        }
        catch (PalletPlateException e)
        {
            if (tx is not null) await tx.RollbackAsync(token); db.ChangeTracker.Clear();
            return new(InventoryMovementStatus.ValidationFailed, Errors: [e.Message]);
        }
    }

    private static decimal Pending(ProductionMaterialIssueLink link, IReadOnlyCollection<Guid> reversed) => link.Quantity - link.CancelledQuantity -
        link.OperationLines.Where(x => x.Operation.Type != ProductionMaterialOperationType.Reversal && !reversed.Contains(x.Operation.Id)).Sum(x => x.Quantity);

}
