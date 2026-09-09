using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;

namespace WarehouseEPI.Tests.Reporting;

public sealed class MovementTraceabilityServiceTests
{
    [Fact]
    public async Task Standard_movement_is_the_only_event_when_no_causal_context_exists()
    {
        await using var db = CreateDbContext();
        var seed = Seed("TRACE-ONLY", "TRACE-01");
        var movement = Movement(seed.User, seed.Product, seed.Location, InventoryMovementType.Entry, InventoryMovementPurpose.Standard, At(2));
        db.AddRange(seed.User, seed.Product, seed.Location, movement);
        await db.SaveChangesAsync();

        var result = await new MovementTraceabilityService(db).GetAsync(movement.Id);

        Assert.NotNull(result);
        Assert.Equal("Vigente", result.CurrentMovementStatus);
        Assert.False(result.HasAdditionalContext);
        Assert.Empty(result.Resources);
        var traceEvent = Assert.Single(result.Events);
        Assert.True(traceEvent.IsCurrentMovement);
        Assert.True(traceEvent.ChangesInventory);
    }

    [Fact]
    public async Task Correction_chain_and_receiving_context_are_identical_from_original_reversal_or_replacement()
    {
        await using var db = CreateDbContext();
        var seed = Seed("TRACE-RECEIPT", "TRACE-02");
        var original = Movement(seed.User, seed.Product, seed.Location, InventoryMovementType.Entry, InventoryMovementPurpose.DocumentReceipt, At(2));
        var reversal = Movement(seed.User, seed.Product, seed.Location, InventoryMovementType.Exit, InventoryMovementPurpose.DocumentReceipt, At(4));
        var replacement = Movement(seed.User, seed.Product, seed.Location, InventoryMovementType.Entry, InventoryMovementPurpose.DocumentReceipt, At(5));
        var correction = new InventoryMovementCorrection
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "trace-correction",
            Type = InventoryMovementCorrectionType.Replacement,
            OriginalMovement = original,
            ReversalMovement = reversal,
            ReplacementMovement = replacement,
            Reason = "Cantidad documentada incorrecta",
            RequestedByUser = seed.User,
            AuthorizedByUser = seed.User,
            RecordedAt = At(3)
        };
        var document = new ReceivingDocument
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "trace-document",
            Type = ReceivingDocumentType.PurchaseOrder,
            Number = "PO-778",
            NormalizedNumber = "PO-778",
            Origin = "Proveedor Norte",
            NormalizedOrigin = "PROVEEDOR NORTE",
            Status = ReceivingDocumentStatus.PartiallyReceived,
            OpenedByUser = seed.User,
            OpenedAt = At(1)
        };
        var confirmation = new ReceivingConfirmation
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "trace-confirmation",
            ReceivingDocument = document,
            InventoryMovement = original,
            ResponsibleUser = seed.User,
            DifferenceAcknowledged = true,
            DifferenceNotes = "Faltaron dos piezas",
            OccurredAt = At(2),
            RecordedAt = At(2)
        };
        confirmation.Lines.Add(new ReceivingConfirmationLine
        {
            InventoryMovementLine = original.Lines.Single(),
            ExternalLotReference = "ROLL-EXT-91"
        });
        var directEvent = new ReceivingDocumentEvent
        {
            ReceivingDocument = document,
            OperationId = confirmation.OperationId,
            RequestFingerprint = "trace-confirmation",
            Type = ReceivingDocumentEventType.ReceiptConfirmed,
            Notes = "Confirmación automática de prueba",
            RecordedAt = At(2)
        };
        db.AddRange(seed.User, seed.Product, seed.Location, original, reversal, replacement, correction, document, confirmation, directEvent);
        await db.SaveChangesAsync();

        var service = new MovementTraceabilityService(db);
        var fromOriginal = (await service.GetAsync(original.Id))!;
        var fromReversal = (await service.GetAsync(reversal.Id))!;
        var fromReplacement = (await service.GetAsync(replacement.Id))!;

        Assert.Equal("Original corregido", fromOriginal.CurrentMovementStatus);
        Assert.Equal("Reverso", fromReversal.CurrentMovementStatus);
        Assert.Equal("Reemplazo", fromReplacement.CurrentMovementStatus);
        Assert.Equal(fromOriginal.Events.Select(item => item.Id), fromReversal.Events.Select(item => item.Id));
        Assert.Equal(fromOriginal.Events.Select(item => item.Id), fromReplacement.Events.Select(item => item.Id));
        Assert.Equal(fromOriginal.Events.OrderBy(item => item.OccurredAt).ThenBy(item => item.Category).ThenBy(item => item.Id).Select(item => item.Id), fromOriginal.Events.Select(item => item.Id));
        Assert.Equal(fromOriginal.Events.Count, fromOriginal.Events.Select(item => item.Id).Distinct().Count());
        Assert.Contains(fromReplacement.Resources, item => item.Label.Contains("PO-778") && item.Summary.Contains("ROLL-EXT-91"));
        Assert.Contains(fromReplacement.Events, item => item.Category == "Recepción" && item.Responsible == "Sistema");
        Assert.Single(fromReplacement.Events, item => item.IsCurrentMovement);
    }

    [Fact]
    public async Task Reversal_without_replacement_is_labeled_without_inventing_a_current_replacement()
    {
        await using var db = CreateDbContext();
        var seed = Seed("TRACE-REVERSE", "TRACE-03");
        var original = Movement(seed.User, seed.Product, seed.Location, InventoryMovementType.Entry, InventoryMovementPurpose.Standard, At(1));
        var reversal = Movement(seed.User, seed.Product, seed.Location, InventoryMovementType.Exit, InventoryMovementPurpose.Standard, At(3));
        db.AddRange(seed.User, seed.Product, seed.Location, original, reversal, new InventoryMovementCorrection
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "trace-reversal",
            Type = InventoryMovementCorrectionType.Reversal,
            OriginalMovement = original,
            ReversalMovement = reversal,
            Reason = "Operación anulada",
            RequestedByUser = seed.User,
            AuthorizedByUser = seed.User,
            RecordedAt = At(2)
        });
        await db.SaveChangesAsync();

        var result = (await new MovementTraceabilityService(db).GetAsync(reversal.Id))!;

        Assert.Equal("Reverso", result.CurrentMovementStatus);
        Assert.DoesNotContain(result.Events, item => item.Status == "Reemplazo");
        Assert.Contains(result.Events, item => item.Category == "Corrección" && item.Status == "Reverso sin reemplazo");
    }

    [Fact]
    public async Task Wip_context_includes_dispositions_for_the_source_line_and_excludes_other_issues()
    {
        await using var db = CreateDbContext();
        var seed = Seed("TRACE-WIP", "WIP-2");
        var otherSeed = Seed("TRACE-WIP-OTHER", "WIP-3", "Otra persona");
        var issue = Movement(seed.User, seed.Product, seed.Location, InventoryMovementType.Transfer, InventoryMovementPurpose.ProductionIssue, At(1));
        issue.OperationalArea = seed.Location;
        var otherIssue = Movement(otherSeed.User, otherSeed.Product, otherSeed.Location, InventoryMovementType.Transfer, InventoryMovementPurpose.ProductionIssue, At(1));
        otherIssue.OperationalArea = otherSeed.Location;
        var disposition = new WipDisposition
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "trace-wip",
            OriginalMovementLine = issue.Lines.Single(),
            Type = WipDispositionType.SupplierReturn,
            Quantity = 2,
            ResponsibleUser = seed.User,
            Reference = "RMA-88",
            OccurredAt = At(2),
            RecordedAt = At(2)
        };
        var unrelated = new WipDisposition
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "trace-wip-other",
            OriginalMovementLine = otherIssue.Lines.Single(),
            Type = WipDispositionType.SupplierReturn,
            Quantity = 1,
            ResponsibleUser = otherSeed.User,
            Reference = "RMA-OTHER",
            OccurredAt = At(2),
            RecordedAt = At(2)
        };
        db.AddRange(seed.User, seed.Product, seed.Location, otherSeed.User, otherSeed.Product, otherSeed.Location, issue, otherIssue, disposition, unrelated);
        await db.SaveChangesAsync();

        var result = (await new MovementTraceabilityService(db).GetAsync(issue.Id))!;

        Assert.Contains(result.Resources, item => item.Kind == "WIP" && item.Label.Contains(seed.Product.Sku));
        Assert.Contains(result.Events, item => item.Category == "WIP" && item.Summary.Contains("RMA-88"));
        Assert.DoesNotContain(result.Events, item => item.Summary.Contains("RMA-OTHER"));
    }

    [Fact]
    public async Task Cycle_count_context_includes_only_actions_for_the_location_that_created_the_adjustment()
    {
        await using var db = CreateDbContext();
        var seed = Seed("TRACE-COUNT", "COUNT-01");
        var otherLocation = new Location { Code = "COUNT-02", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Storage };
        var movement = Movement(seed.User, seed.Product, seed.Location, InventoryMovementType.Adjustment, InventoryMovementPurpose.CycleCountAdjustment, At(3));
        var campaign = new CycleCountCampaign
        {
            OperationId = Guid.NewGuid(),
            Number = 417,
            Title = "Conteo mensual",
            Status = CycleCountCampaignStatus.Completed,
            CreatedByUser = seed.User,
            CreatedAt = At(1)
        };
        var counted = new CycleCountLocation
        {
            Campaign = campaign,
            Location = seed.Location,
            SortOrder = 1,
            Status = CycleCountLocationStatus.Completed,
            AdjustmentMovement = movement,
            CompletedAt = At(4),
            LastActionByUserId = seed.User.Id
        };
        var unrelated = new CycleCountLocation
        {
            Campaign = campaign,
            Location = otherLocation,
            SortOrder = 2,
            Status = CycleCountLocationStatus.Completed,
            CompletedAt = At(4),
            LastActionByUserId = seed.User.Id
        };
        var relatedAction = new CycleCountAction
        {
            Campaign = campaign,
            CycleCountLocation = counted,
            Type = CycleCountActionType.AdjustmentApproved,
            ResponsibleUser = seed.User,
            Notes = "Ajuste COUNT-01",
            RecordedAt = At(2)
        };
        var unrelatedAction = new CycleCountAction
        {
            Campaign = campaign,
            CycleCountLocation = unrelated,
            Type = CycleCountActionType.LocationCompleted,
            ResponsibleUser = seed.User,
            Notes = "Evento COUNT-02",
            RecordedAt = At(2)
        };
        db.AddRange(seed.User, seed.Product, seed.Location, otherLocation, movement, campaign, counted, unrelated, relatedAction, unrelatedAction);
        await db.SaveChangesAsync();

        var result = (await new MovementTraceabilityService(db).GetAsync(movement.Id))!;

        Assert.Contains(result.Resources, item => item.Kind == "Conteo" && item.Label.Contains("417") && item.Summary.Contains("COUNT-01"));
        Assert.Contains(result.Events, item => item.Category == "Conteo" && item.Summary == "Ajuste COUNT-01");
        Assert.DoesNotContain(result.Events, item => item.Summary == "Evento COUNT-02");
    }

    private static TraceSeed Seed(string sku, string locationCode, string userName = "Auditora") => new(
        new User { FullName = userName, PinLookup = Guid.NewGuid().ToString("N"), PinHash = "hash", RoleId = 1 },
        new Product { Sku = sku, BaseUnitId = 1 },
        new Location { Code = locationCode, Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Storage });

    private static InventoryMovement Movement(
        User user,
        Product product,
        Location location,
        InventoryMovementType type,
        InventoryMovementPurpose purpose,
        DateTimeOffset occurredAt)
    {
        var movement = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString("N"),
            Type = type,
            Purpose = purpose,
            ResponsibleUser = user,
            OccurredAt = occurredAt,
            RecordedAt = occurredAt
        };
        movement.Lines.Add(new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            SourceLocation = type is InventoryMovementType.Exit or InventoryMovementType.Transfer ? location : null,
            DestinationLocation = type is InventoryMovementType.Entry or InventoryMovementType.Transfer ? location : null,
            Quantity = 5,
            LineNumber = 1
        });
        return movement;
    }

    private static DateTimeOffset At(int hour) => new(2026, 9, 2, hour, 0, 0, TimeSpan.Zero);

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"MovementTraceability-{Guid.NewGuid():N}").Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed record TraceSeed(User User, Product Product, Location Location);
}
