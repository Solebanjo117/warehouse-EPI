using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Inventory;

public sealed class PalletTrackingTests
{
    [Fact]
    public async Task Identification_creates_partial_plate_without_changing_inventory_and_retry_is_idempotent()
    {
        await using var f = await Fixture.Create();
        var entry = f.Command(InventoryMovementType.Entry, 100);
        entry = entry with { Lines = [entry.Lines[0] with { PalletQuantities = null }] };
        Assert.Equal(InventoryMovementStatus.Success, (await f.Service.ConfirmAsync(entry)).Status);
        var summary = Assert.Single(await f.Tracking.IdentificationProductsAsync(f.Source.Id));
        Assert.Equal((100m, 0m, 100m, 100m), (summary.Total, summary.Plated, summary.Unplated, summary.Identifiable));
        var command = new PalletIdentificationCommand(Guid.NewGuid(), f.Source.Id, f.Product.Id, 40,
            summary.BalanceVersion, summary.Identifiable);
        var first = await f.Tracking.IdentifyAsync(command);
        var retry = await f.Tracking.IdentifyAsync(command);
        Assert.Equal(InventoryMovementStatus.Success, first.Status);
        Assert.Equal(first.Plates!.Single().PlateId, retry.Plates!.Single().PlateId);
        Assert.Equal(100, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
        Assert.Equal(40, await f.Db.PalletPlates.SumAsync(x => x.Quantity));
        var printable = await new PalletLicensePlateService(f.Db).LoadAsync(first.Plates!.Single().PlateId);
        Assert.Equal(PalletLicensePlateStatus.Success, printable.Status);
        Assert.True(printable.Entry!.IsTracked); Assert.Null(printable.Entry.OriginMovementId); Assert.Equal("Sin identificación de operador", printable.Entry.Responsible);
        Assert.Null((await f.Db.PalletPlateEvents.SingleAsync(x => x.Kind == "Identification")).ResponsibleUserId);
        var after = Assert.Single(await f.Tracking.IdentificationProductsAsync(f.Source.Id));
        Assert.Equal((40m, 60m, 60m), (after.Plated, after.Unplated, after.Identifiable));
        Assert.Equal(InventoryMovementStatus.IdempotencyConflict,
            (await f.Tracking.IdentifyAsync(command with { Quantity = 30 })).Status);
    }

    [Fact]
    public async Task Admin_can_void_pristine_identification_but_not_a_moved_plate()
    {
        await using var f = await Fixture.Create();
        var entry = f.Command(InventoryMovementType.Entry, 50);
        await f.Service.ConfirmAsync(entry with { Lines = [entry.Lines[0] with { PalletQuantities = null }] });
        var summary = Assert.Single(await f.Tracking.IdentificationProductsAsync(f.Source.Id));
        var identified = await f.Tracking.IdentifyAsync(new(Guid.NewGuid(), f.Source.Id, f.Product.Id, 20,
            summary.BalanceVersion, summary.Identifiable));
        var plate = Assert.Single(identified.Plates!);
        var voided = await f.Tracking.VoidIdentificationAsync(new(Guid.NewGuid(), plate.PlateId, plate.Version, "Etiqueta equivocada", "2468"));
        Assert.Equal(InventoryMovementStatus.Success, voided.Status);
        Assert.True((await f.Db.PalletPlates.SingleAsync()).IsVoided);
        Assert.Contains("Etiqueta equivocada", (await f.Db.PalletPlateEvents.SingleAsync(x => x.Kind == "IdentificationVoided")).After);
        Assert.Equal(50, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
        Assert.Equal(50, (await f.Tracking.IdentificationProductsAsync(f.Source.Id)).Single().Identifiable);

        summary = Assert.Single(await f.Tracking.IdentificationProductsAsync(f.Source.Id));
        identified = await f.Tracking.IdentifyAsync(new(Guid.NewGuid(), f.Source.Id, f.Product.Id, 20,
            summary.BalanceVersion, summary.Identifiable));
        plate = Assert.Single(identified.Plates!);
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Exit, 1, plates: [new(plate.PlateId, 1, plate.Version)]));
        Assert.Equal(InventoryMovementStatus.ValidationFailed,
            (await f.Tracking.VoidIdentificationAsync(new(Guid.NewGuid(), plate.PlateId, plate.Version + 1, "Ya se movió", "2468"))).Status);
    }

    [Fact]
    public async Task Automatic_transfer_keeps_unplated_part_unplated_at_destination()
    {
        await using var f = await Fixture.Create();
        var entry = f.Command(InventoryMovementType.Entry, 100);
        await f.Service.ConfirmAsync(entry with { Lines = [entry.Lines[0] with { PalletQuantities = null }] });
        var summary = Assert.Single(await f.Tracking.IdentificationProductsAsync(f.Source.Id));
        var identified = await f.Tracking.IdentifyAsync(new(Guid.NewGuid(), f.Source.Id, f.Product.Id, 30,
            summary.BalanceVersion, summary.Identifiable));
        var transfer = f.Command(InventoryMovementType.Transfer, 50);
        transfer = transfer with { Lines = [transfer.Lines[0] with { Plates = null, AutomaticPalletHandling = true }] };
        Assert.Equal(InventoryMovementStatus.Success, (await f.Service.ConfirmAsync(transfer)).Status);
        Assert.Equal(30, await f.Db.PalletPlates.Where(x => x.LocationId == f.Destination.Id).SumAsync(x => x.Quantity));
        Assert.Equal(50, await f.Db.InventoryBalances.Where(x => x.LocationId == f.Destination.Id).SumAsync(x => x.Quantity));
        Assert.Equal(20, (await f.Tracking.AvailableAsync(f.Product.Id, f.Destination.Id)).Unplated);
        Assert.Single(await f.Db.PalletPlateEvents.Where(x => x.Kind == "Transfer").ToListAsync());
    }

    [Fact]
    public async Task Identification_excludes_active_warehouse_reservations_and_rejects_stale_state()
    {
        await using var f = await Fixture.Create();
        var entry = f.Command(InventoryMovementType.Entry, 100);
        await f.Service.ConfirmAsync(entry with { Lines = [entry.Lines[0] with { PalletQuantities = null }] });
        var lot = await f.Db.ProductLots.SingleAsync();
        var line = new ProductionSupplyRequestLine { ProductId = f.Product.Id, UnitId = 1, SupplyRequestId = Guid.NewGuid(), MaterialPlanId = Guid.NewGuid(), RequiredQuantity = 30 };
        f.Db.ProductionWarehouseReservations.Add(new() { SupplyRequestLine = line, LocationId = f.Source.Id, LotId = lot.Id, Quantity = 30 });
        await f.Db.SaveChangesAsync();
        var summary = Assert.Single(await f.Tracking.IdentificationProductsAsync(f.Source.Id));
        Assert.Equal(30, summary.Protected); Assert.Equal(70, summary.Identifiable);
        var first = await f.Tracking.IdentifyAsync(new(Guid.NewGuid(), f.Source.Id, f.Product.Id, 10,
            summary.BalanceVersion, summary.Identifiable));
        Assert.Equal(InventoryMovementStatus.Success, first.Status);
        Assert.Equal(InventoryMovementStatus.BalanceChanged,
            (await f.Tracking.IdentifyAsync(new(Guid.NewGuid(), f.Source.Id, f.Product.Id, 10,
                summary.BalanceVersion, summary.Identifiable))).Status);
    }

    [Fact]
    public async Task Identification_excludes_open_preparations_and_order_owned_unplated_wip()
    {
        await using var f = await Fixture.Create();
        var entry = f.Command(InventoryMovementType.Entry, 100);
        await f.Service.ConfirmAsync(entry with { Lines = [entry.Lines[0] with { PalletQuantities = null }] });
        var lot = await f.Db.ProductLots.SingleAsync();
        var requestLine = new ProductionSupplyRequestLine { ProductId = f.Product.Id, UnitId = 1, SupplyRequestId = Guid.NewGuid(), MaterialPlanId = Guid.NewGuid(), RequiredQuantity = 25 };
        var preparation = new ProductionSupplyPreparation { OperationId = Guid.NewGuid(), RequestFingerprint = "prep", SupplyRequestLine = requestLine,
            DestinationLocationId = f.Destination.Id, ResponsibleUserId = f.User.Id, Status = ProductionSupplyPreparationStatus.Open };
        preparation.Sources.Add(new ProductionSupplyPreparationSource { Kind = ProductionSupplySourceKind.ExistingWip,
            LocationId = f.Source.Id, Quantity = 25, PlatesJson = "[]" });
        f.Db.ProductionSupplyPreparations.Add(preparation);
        f.Db.ProductionMaterialIssueLinks.Add(new ProductionMaterialIssueLink { ProductId = f.Product.Id, WipLocationId = f.Source.Id,
            WorkOrderId = Guid.NewGuid(), WorkOrderStageId = Guid.NewGuid(), Quantity = 10, PlateAllocationsJson = "[]",
            Lots = [new ProductionMaterialIssueLot { LotId = lot.Id, Quantity = 10 }] });
        await f.Db.SaveChangesAsync();
        var summary = Assert.Single(await f.Tracking.IdentificationProductsAsync(f.Source.Id));
        Assert.Equal(35, summary.Protected); Assert.Equal(65, summary.Identifiable);
    }

    [Fact]
    public async Task Legacy_activation_uses_verified_balance_without_creating_inventory()
    {
        await using var f = await Fixture.Create();
        var legacy = f.Command(InventoryMovementType.Entry, 100);
        legacy = legacy with { Lines = [legacy.Lines[0] with { PalletQuantities = null }] };
        var entry = await f.Service.ConfirmAsync(legacy);
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Exit, 30));
        var activation = new PalletActivationCommand(Guid.NewGuid(), entry.MovementId!.Value, f.Source.Id, 70, "2468");
        Assert.Equal(InventoryMovementStatus.Success, (await f.Tracking.ActivateAsync(activation)).Status);
        Assert.Equal(InventoryMovementStatus.Success, (await f.Tracking.ActivateAsync(activation)).Status);
        Assert.Equal(entry.MovementId, (await f.Db.PalletPlates.SingleAsync()).Id);
        Assert.Equal(70, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
        Assert.Equal(2, await f.Db.InventoryMovements.CountAsync());
        Assert.Equal(InventoryMovementStatus.ValidationFailed, (await f.Tracking.ActivateAsync(activation with { OperationId = Guid.NewGuid() })).Status);
    }

    [Fact]
    public async Task Activation_rejects_quantity_not_backed_by_unplated_inventory()
    {
        await using var f = await Fixture.Create();
        var legacy = f.Command(InventoryMovementType.Entry, 50);
        var entry = await f.Service.ConfirmAsync(legacy with { Lines = [legacy.Lines[0] with { PalletQuantities = null }] });
        var result = await f.Tracking.ActivateAsync(new(Guid.NewGuid(), entry.MovementId!.Value, f.Source.Id, 60, "2468"));
        Assert.Equal(InventoryMovementStatus.ValidationFailed, result.Status); Assert.Empty(await f.Db.PalletPlates.ToListAsync());
        Assert.Equal(50, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Wip_assignment_consumption_and_return_use_reserved_plates()
    {
        await using var f = await Fixture.Create();
        f.Source.OperationalRole = LocationOperationalRole.Wip; await f.Db.SaveChangesAsync();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100)); var p = Assert.Single(entry.Plates!);
        var assigned = await ProductionPlateAllocation.AssignAsync(f.Db, f.Product.Id, f.Source.Id, 60, [new(p.PlateId, 60, p.Version)], default);
        var issue = new ProductionMaterialIssueLink { ProductId = f.Product.Id, WipLocationId = f.Source.Id, WorkOrderId = Guid.NewGuid(), WorkOrderStageId = Guid.NewGuid(),
            Quantity = 60, Source = ProductionMaterialSupplySource.WipAssignment, PlateAllocationsJson = assigned.Plates,
            Lots = assigned.Lots.Select(x => new ProductionMaterialIssueLot { LotId = x.LotId, Quantity = x.Quantity }).ToList() };
        f.Db.ProductionMaterialIssueLinks.Add(issue); await f.Db.SaveChangesAsync();
        var forbidden = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Exit, 50, plates: [new(p.PlateId, 50, p.Version)]));
        Assert.Equal(InventoryMovementStatus.ValidationFailed, forbidden.Status);
        var balanceBeforeCount = await new InventoryQueryService(f.Db).GetBalanceAsync(f.Product.Id, f.Source.Id);
        var protectedCount = await f.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Adjustment, "2468",
            [new(f.Product.Id, 100, LocationId: f.Source.Id, ExpectedBalanceVersion: balanceBeforeCount.Version,
                PlateCounts: [new(p.PlateId, 50, p.Version)])], Notes: "Conteo con reserva"));
        Assert.Equal(InventoryMovementStatus.ValidationFailed, protectedCount.Status);
        Assert.Equal(100, (await f.Db.PalletPlates.SingleAsync(x => x.Id == p.PlateId)).Quantity);
        var selections = await ProductionPlateAllocation.SelectAsync(f.Db, issue, 25, default);
        var consumed = await f.Service.ConfirmAuthorizedAsync(new(Guid.NewGuid(), InventoryMovementType.Exit, "2468",
            [new(f.Product.Id, 25, SourceLocationId: f.Source.Id, Plates: selections, MaterialIssueLinkId: issue.Id)], Purpose: InventoryMovementPurpose.WipConsumption, OperationalAreaId: f.Source.Id),
            (await f.Pins.AuthenticateAsync("2468"))!, allowReservedWip: true);
        Assert.Equal(InventoryMovementStatus.Success, consumed.Status);
        var op = new ProductionMaterialOperation { OperationId = Guid.NewGuid(), RequestFingerprint = "test", WorkOrderId = issue.WorkOrderId, WorkOrderStageId = issue.WorkOrderStageId,
            ResponsibleUserId = f.User.Id, Type = ProductionMaterialOperationType.Consumption, Lines = [new() { IssueLinkId = issue.Id,
                InventoryMovementLineId = (await f.Db.InventoryMovementLines.SingleAsync(x => x.MovementId == consumed.MovementId)).Id, Quantity = 25 }] };
        f.Db.ProductionMaterialOperations.Add(op); await f.Db.SaveChangesAsync();
        Assert.Equal(35, (await ProductionPlateAllocation.RemainingAsync(f.Db, issue, default)).Sum(x => x.Quantity));
        var remaining = await ProductionPlateAllocation.SelectAsync(f.Db, issue, 35, default);
        var returned = await f.Service.ConfirmAuthorizedAsync(new(Guid.NewGuid(), InventoryMovementType.Transfer, "2468",
            [new(f.Product.Id, 35, SourceLocationId: f.Source.Id, DestinationLocationId: f.Destination.Id, Plates: remaining, MaterialIssueLinkId: issue.Id)],
            Purpose: InventoryMovementPurpose.WipWarehouseReturn, OperationalAreaId: f.Source.Id), (await f.Pins.AuthenticateAsync("2468"))!, allowReservedWip: true);
        Assert.Equal(InventoryMovementStatus.Success, returned.Status);
        Assert.Equal(40, (await f.Db.PalletPlates.SingleAsync(x => x.Id == p.PlateId)).Quantity);
        Assert.Equal(35, (await f.Db.PalletPlates.SingleAsync(x => x.LocationId == f.Destination.Id)).Quantity);
        Assert.Equal(75, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
    }

    [Fact]
    public void Operational_label_contains_product_and_plate_barcodes()
    {
        var preset = PalletLicensePlatePresetCatalog.Operational;
        var validation = LabelDesignSerializer.Validate(preset.Design, preset.Size, LabelTemplateKind.PalletLicensePlate);
        Assert.Empty(validation.Errors);
        Assert.Contains(preset.Design.Elements, x => x.Type == LabelElementType.Code128 && x.Binding == "plate.identifier");
        Assert.Contains(preset.Design.Elements, x => x.Type == LabelElementType.Code128 && x.Binding == "product.sku");
    }
    [Fact]
    public async Task Entry_distribution_and_retry_create_exactly_two_plates()
    {
        await using var f = await Fixture.Create();
        var command = f.Command(InventoryMovementType.Entry, 100, quantities: [40, 60]);
        var result = await f.Service.ConfirmAsync(command);
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.Equal(new[] { 40m, 60m }, result.Plates!.Select(x => x.Quantity).Order().ToArray());
        Assert.Equal(InventoryMovementStatus.Success, (await f.Service.ConfirmAsync(command)).Status);
        Assert.Equal(2, await f.Db.PalletPlates.CountAsync());
        Assert.Equal(100, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
        Assert.Equal(InventoryMovementStatus.IdempotencyConflict, (await f.Service.ConfirmAsync(command with { Lines = [command.Lines[0] with { PalletQuantities = [50, 50] }] })).Status);
    }

    [Fact]
    public async Task Automatic_adjustment_updates_the_existing_plate_to_the_full_physical_count()
    {
        await using var f = await Fixture.Create();
        var entry = f.Command(InventoryMovementType.Entry, 100) with
        {
            Lines = [f.Command(InventoryMovementType.Entry, 100).Lines[0] with { PalletQuantities = null, AutomaticPalletHandling = true }]
        };
        var entryResult = await f.Service.ConfirmAsync(entry);
        var plate = Assert.Single(entryResult.Plates!);
        var balance = await new InventoryQueryService(f.Db).GetBalanceAsync(f.Product.Id, f.Source.Id);
        var adjustment = await f.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Adjustment, "2468",
            [new(f.Product.Id, 70, LocationId: f.Source.Id, ExpectedBalanceVersion: balance.Version, AutomaticPalletHandling: true)], Notes: "Conteo total"));
        Assert.Equal(InventoryMovementStatus.Success, adjustment.Status);
        Assert.Equal(70, (await f.Db.PalletPlates.SingleAsync(x => x.Id == plate.PlateId)).Quantity);
        Assert.Equal(70, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
        Assert.Equal(0, (await f.Tracking.AvailableAsync(f.Product.Id, f.Source.Id)).Unplated);
        Assert.Contains(await f.Db.PalletPlateEvents.Where(x => x.MovementId == adjustment.MovementId).ToListAsync(),
            x => x.PlateId == plate.PlateId && x.Kind == InventoryMovementType.Adjustment.ToString());
        var corrections = new InventoryCorrectionService(f.Db, f.Pins, f.Service, TimeProvider.System);
        var reversed = await corrections.ConfirmAsync(new(Guid.NewGuid(), adjustment.MovementId!.Value, f.User.Id, "2468", "Revertir conteo"));
        Assert.Equal(InventoryCorrectionStatus.Success, reversed.Status);
        Assert.Equal(100, (await f.Db.PalletPlates.SingleAsync(x => x.Id == plate.PlateId)).Quantity);
        Assert.Equal(100, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Adjustment_from_46_to_50_reuses_the_plate_instead_of_identifying_only_the_difference()
    {
        await using var f = await Fixture.Create();
        var platedEntry = f.Command(InventoryMovementType.Entry, 46) with
        {
            Lines = [f.Command(InventoryMovementType.Entry, 46).Lines[0] with { AutomaticPalletHandling = true }]
        };
        var plate = Assert.Single((await f.Service.ConfirmAsync(platedEntry)).Plates!);
        var unplatedEntry = f.Command(InventoryMovementType.Entry, 4) with
        {
            Lines = [f.Command(InventoryMovementType.Entry, 4).Lines[0] with { PalletQuantities = null, AutomaticPalletHandling = false }]
        };
        await f.Service.ConfirmAsync(unplatedEntry);
        var balance = await new InventoryQueryService(f.Db).GetBalanceAsync(f.Product.Id, f.Source.Id);

        var adjustment = await f.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Adjustment, "2468",
            [new(f.Product.Id, 50, LocationId: f.Source.Id, ExpectedBalanceVersion: balance.Version, AutomaticPalletHandling: true)],
            Notes: "Conteo total"));

        Assert.Equal(InventoryMovementStatus.Success, adjustment.Status);
        Assert.Single(await f.Db.PalletPlates.Where(x => x.Quantity > 0).ToListAsync());
        Assert.Equal(50, (await f.Db.PalletPlates.SingleAsync(x => x.Id == plate.PlateId)).Quantity);
        Assert.Equal(0, (await f.Tracking.AvailableAsync(f.Product.Id, f.Source.Id)).Unplated);
        var printable = await new PalletLicensePlateService(f.Db).LoadAsync(plate.PlateId);
        Assert.Equal(50, printable.Entry!.Quantity);
        var suggestion = await f.Tracking.SuggestionAsync(adjustment.MovementId!.Value);
        Assert.Equal((f.Source.Id, f.Product.Id), (suggestion!.LocationId, suggestion.ProductId));
    }

    [Fact]
    public async Task Identification_reuses_the_positive_plate_and_consolidation_repairs_historical_duplicates()
    {
        await using var f = await Fixture.Create();
        var entry = f.Command(InventoryMovementType.Entry, 100) with
        {
            Lines = [f.Command(InventoryMovementType.Entry, 100).Lines[0] with { PalletQuantities = null }]
        };
        await f.Service.ConfirmAsync(entry);
        var summary = Assert.Single(await f.Tracking.IdentificationProductsAsync(f.Source.Id));
        var first = Assert.Single((await f.Tracking.IdentifyAsync(new(Guid.NewGuid(), f.Source.Id, f.Product.Id, 46,
            summary.BalanceVersion, summary.Identifiable))).Plates!);
        summary = Assert.Single(await f.Tracking.IdentificationProductsAsync(f.Source.Id));
        var reused = Assert.Single((await f.Tracking.IdentifyAsync(new(Guid.NewGuid(), f.Source.Id, f.Product.Id, 4,
            summary.BalanceVersion, summary.Identifiable))).Plates!);
        Assert.Equal(first.PlateId, reused.PlateId);
        Assert.Single(await f.Db.PalletPlates.Where(x => x.Quantity > 0).ToListAsync());
        Assert.Equal(50, reused.Quantity);

        var lot = await f.Db.ProductLots.SingleAsync();
        var duplicate = new PalletPlate { ProductId = f.Product.Id, LocationId = f.Source.Id, Quantity = 5,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1), Lots = [new() { LotId = lot.Id, Quantity = 5 }] };
        f.Db.PalletPlates.Add(duplicate);
        await f.Db.SaveChangesAsync();
        var consolidationId = Guid.NewGuid();
        var consolidated = await f.Tracking.ConsolidateAsync(new(consolidationId, f.Product.Id, f.Source.Id));

        Assert.Equal(InventoryMovementStatus.Success, consolidated.Status);
        var canonical = Assert.Single(consolidated.Plates!);
        Assert.Equal(first.PlateId, canonical.PlateId);
        Assert.Equal(55, canonical.Quantity);
        Assert.Equal(0, (await f.Db.PalletPlates.SingleAsync(x => x.Id == duplicate.Id)).Quantity);
        var events = await f.Db.PalletPlateEvents.Where(x => x.OperationId == consolidationId).ToListAsync();
        Assert.Contains(events, x => x.PlateId == first.PlateId && x.Kind == "ConsolidationPrimary");
        Assert.Contains(events, x => x.PlateId == duplicate.Id && x.Kind == "Consolidation");
        var retry = await f.Tracking.ConsolidateAsync(new(consolidationId, f.Product.Id, f.Source.Id));
        Assert.Equal(55, Assert.Single(retry.Plates!).Quantity);
    }

    [Fact]
    public async Task Automatic_mode_is_idempotent_and_reversal_restores_the_selected_plate()
    {
        await using var f = await Fixture.Create();
        var entry = f.Command(InventoryMovementType.Entry, 100);
        entry = entry with { Lines = [entry.Lines[0] with { PalletQuantities = null, AutomaticPalletHandling = true }] };
        var created = await f.Service.ConfirmAsync(entry);
        var plate = Assert.Single(created.Plates!);
        Assert.Equal(InventoryMovementStatus.Success, (await f.Service.ConfirmAsync(entry)).Status);
        Assert.Equal(InventoryMovementStatus.IdempotencyConflict,
            (await f.Service.ConfirmAsync(entry with { Lines = [entry.Lines[0] with { AutomaticPalletHandling = false }] })).Status);

        var exit = f.Command(InventoryMovementType.Exit, 30);
        exit = exit with { Lines = [exit.Lines[0] with { AutomaticPalletHandling = true }] };
        var moved = await f.Service.ConfirmAsync(exit);
        Assert.Equal(70, (await f.Db.PalletPlates.SingleAsync(x => x.Id == plate.PlateId)).Quantity);
        Assert.Contains(await f.Db.PalletPlateEvents.Where(x => x.MovementId == moved.MovementId).ToListAsync(),
            x => x.PlateId == plate.PlateId && x.Kind == InventoryMovementType.Exit.ToString());

        var corrections = new InventoryCorrectionService(f.Db, f.Pins, f.Service, TimeProvider.System);
        var reversed = await corrections.ConfirmAsync(new(Guid.NewGuid(), moved.MovementId!.Value, f.User.Id, "2468", "Prueba automática"));
        Assert.Equal(InventoryCorrectionStatus.Success, reversed.Status);
        Assert.Equal(100, (await f.Db.PalletPlates.SingleAsync(x => x.Id == plate.PlateId)).Quantity);
        Assert.Contains(await f.Db.PalletPlateEvents.Where(x => x.ReversesEventId != null).ToListAsync(), x => x.PlateId == plate.PlateId);
    }

    [Fact]
    public async Task Automatic_transfer_uses_oldest_source_plates_and_merges_oldest_destination_plate()
    {
        await using var f = await Fixture.Create();
        var firstEntry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        var first = Assert.Single(firstEntry.Plates!);
        var seededDestination = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Transfer, 20,
            plates: [new(first.PlateId, 20, first.Version)]));
        var target = seededDestination.Plates!.Single(x => x.LocationId == f.Destination.Id);
        var firstRemaining = seededDestination.Plates!.Single(x => x.PlateId == first.PlateId);
        var secondEntry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 50));
        var second = secondEntry.Plates!.Single(x => x.PlateId != first.PlateId);
        var firstEntity = await f.Db.PalletPlates.SingleAsync(x => x.Id == first.PlateId);
        var secondEntity = await f.Db.PalletPlates.SingleAsync(x => x.Id == second.PlateId);
        firstEntity.CreatedAt = firstEntity.CreatedAt.AddMinutes(-1);
        secondEntity.CreatedAt = firstEntity.CreatedAt.AddMinutes(1);
        await f.Db.SaveChangesAsync();

        var command = f.Command(InventoryMovementType.Transfer, 90) with
        {
            Lines = [f.Command(InventoryMovementType.Transfer, 90).Lines[0] with { AutomaticPalletHandling = true }]
        };
        var result = await f.Service.ConfirmAsync(command);
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.Equal(110, (await f.Db.PalletPlates.SingleAsync(x => x.Id == target.PlateId)).Quantity);
        Assert.Equal(0, (await f.Db.PalletPlates.SingleAsync(x => x.Id == firstRemaining.PlateId)).Quantity);
        Assert.Equal(40, (await f.Db.PalletPlates.SingleAsync(x => x.Id == second.PlateId)).Quantity);
        Assert.Single(await f.Db.PalletPlates.Where(x => x.LocationId == f.Destination.Id).ToListAsync());
    }

    [Theory]
    [InlineData(30, 70, 30, 2)]
    [InlineData(100, 0, 100, 1)]
    [InlineData(120, -20, 120, 2)]
    public async Task Transfers_split_move_or_record_negative_difference(decimal transfer, decimal source, decimal destination, int count)
    {
        await using var f = await Fixture.Create();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        var plate = Assert.Single(entry.Plates!);
        var result = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Transfer, transfer, plates: [new(plate.PlateId, transfer, plate.Version)]));
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.Equal(count, await f.Db.PalletPlates.CountAsync());
        Assert.Equal(source, await f.Db.PalletPlates.Where(x => x.LocationId == f.Source.Id).SumAsync(x => x.Quantity));
        Assert.Equal(destination, await f.Db.PalletPlates.Where(x => x.LocationId == f.Destination.Id).SumAsync(x => x.Quantity));
        if (count == 1) Assert.Equal(f.Destination.Id, (await f.Db.PalletPlates.SingleAsync()).LocationId);
        if (source < 0) { Assert.True(result.HasNegativeBalance); Assert.Equal("Con diferencia", (await f.Db.PalletPlates.SingleAsync(x => x.Id == plate.PlateId)).Status); }
        Assert.Equal(100, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
        Assert.Equal(100, await f.Db.PalletPlateLots.SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Merge_preserves_target_and_exhausts_origin()
    {
        await using var f = await Fixture.Create();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100)); var origin = Assert.Single(entry.Plates!);
        var split = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Transfer, 30, plates: [new(origin.PlateId, 30, origin.Version)]));
        var target = split.Plates!.Single(x => x.LocationId == f.Destination.Id); origin = split.Plates!.Single(x => x.LocationId == f.Source.Id);
        var command = f.Command(InventoryMovementType.Transfer, 70, plates: [new(origin.PlateId, 70, origin.Version)]);
        command = command with { Lines = [command.Lines[0] with { DestinationPlateId = target.PlateId, ExpectedDestinationPlateVersion = target.Version }] };
        var result = await f.Service.ConfirmAsync(command);
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.Equal(2, await f.Db.PalletPlates.CountAsync());
        Assert.Equal(100, (await f.Db.PalletPlates.SingleAsync(x => x.Id == target.PlateId)).Quantity);
        Assert.Equal("Agotada", (await f.Db.PalletPlates.SingleAsync(x => x.Id == origin.PlateId)).Status);
    }

    [Fact]
    public async Task Exit_and_unplated_stock_never_silently_change_other_plates()
    {
        await using var f = await Fixture.Create();
        var result = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100)); var p = Assert.Single(result.Plates!);
        var exit = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Exit, 40, plates: [new(p.PlateId, 40, p.Version)]));
        Assert.Equal(InventoryMovementStatus.Success, exit.Status);
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Exit, 5));
        Assert.Equal(60, (await f.Db.PalletPlates.SingleAsync()).Quantity);
        Assert.Equal(55, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
        Assert.Equal(-5, (await f.Tracking.AvailableAsync(f.Product.Id, f.Source.Id)).Unplated);
    }

    [Fact]
    public async Task Stale_plate_version_rejects_without_inventory_mutation()
    {
        await using var f = await Fixture.Create();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100)); var p = Assert.Single(entry.Plates!);
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Exit, 10, plates: [new(p.PlateId, 10, p.Version)]));
        var result = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Exit, 10, plates: [new(p.PlateId, 10, p.Version)]));
        Assert.Equal(InventoryMovementStatus.ValidationFailed, result.Status);
        Assert.Equal(90, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Reversal_requires_latest_dependencies_then_restores_split()
    {
        await using var f = await Fixture.Create();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100)); var p = Assert.Single(entry.Plates!);
        var split = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Transfer, 40, plates: [new(p.PlateId, 40, p.Version)]));
        var corrections = new InventoryCorrectionService(f.Db, f.Pins, f.Service, TimeProvider.System);
        var blocked = await corrections.ConfirmAsync(new(Guid.NewGuid(), entry.MovementId!.Value, f.User.Id, "2468", "Prueba"));
        Assert.Equal(InventoryCorrectionStatus.ValidationFailed, blocked.Status);
        var reversed = await corrections.ConfirmAsync(new(Guid.NewGuid(), split.MovementId!.Value, f.User.Id, "2468", "Prueba"));
        Assert.Equal(InventoryCorrectionStatus.Success, reversed.Status);
        Assert.Equal(100, (await f.Db.PalletPlates.SingleAsync(x => x.Id == p.PlateId)).Quantity);
        Assert.Single(await f.Db.PalletPlates.Where(x => x.IsVoided).ToListAsync());
        Assert.Equal(InventoryCorrectionStatus.Success, (await corrections.ConfirmAsync(new(Guid.NewGuid(), entry.MovementId.Value, f.User.Id, "2468", "Prueba"))).Status);
    }

    [Fact]
    public async Task Plate_adjustment_reconciles_composition_even_if_total_is_unchanged()
    {
        await using var f = await Fixture.Create();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100, quantities: [40, 60]));
        var plates = entry.Plates!.OrderBy(x => x.Quantity).ToArray();
        var balance = await new InventoryQueryService(f.Db).GetBalanceAsync(f.Product.Id, f.Source.Id);
        var result = await f.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Adjustment, "2468",
            [new(f.Product.Id, 100, LocationId: f.Source.Id, ExpectedBalanceVersion: balance.Version,
                PlateCounts: [new(plates[0].PlateId, 30, plates[0].Version), new(plates[1].PlateId, 70, plates[1].Version)])], Notes: "Conteo"));
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.Equal(new[] {30m,70m}, (await f.Db.PalletPlates.ToListAsync()).Select(x => x.Quantity).Order());
        Assert.Equal(100, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Printed_plate_load_uses_current_quantity_and_location()
    {
        await using var f = await Fixture.Create();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100)); var p = Assert.Single(entry.Plates!);
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Transfer, 100, plates: [new(p.PlateId,100,p.Version)]));
        var loaded = await new PalletLicensePlateService(f.Db).LoadAsync(p.PlateId);
        Assert.True(loaded.Entry!.IsTracked); Assert.Equal(f.Destination.Code, loaded.Entry.Destination);
        Assert.Equal(p.Identifier, loaded.Entry.Identifier);
    }

    [Fact]
    public async Task Complete_transfer_preserves_signed_lot_composition()
    {
        await using var f = await Fixture.Create();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        var plate = await f.Db.PalletPlates.Include(x => x.Lots).SingleAsync();
        var first = Assert.Single(plate.Lots); first.Quantity = -20;
        (await f.Db.InventoryBalances.SingleAsync()).Quantity = -20;
        var extra = new ProductLot { ProductId = f.Product.Id, Number = "OTHER", NormalizedNumber = "OTHER" };
        f.Db.ProductLots.Add(extra);
        plate.Lots.Add(new() { PlateId = plate.Id, LotId = extra.Id, Quantity = 120 });
        f.Db.InventoryBalances.Add(new() { ProductId = f.Product.Id, LocationId = f.Source.Id, LotId = extra.Id, Quantity = 120 });
        await f.Db.SaveChangesAsync();
        var result = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Transfer, 100, plates: [new(plate.Id, 100, plate.Version)]));
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.Single(await f.Db.PalletPlates.ToListAsync());
        Assert.Equal(f.Destination.Id, plate.LocationId);
        foreach (var lot in plate.Lots)
        {
            Assert.Equal(lot.Quantity, (await f.Db.InventoryBalances.SingleAsync(x => x.LocationId == f.Destination.Id && x.LotId == lot.LotId)).Quantity);
            Assert.Equal(0, (await f.Db.InventoryBalances.SingleAsync(x => x.LocationId == f.Source.Id && x.LotId == lot.LotId)).Quantity);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public WarehouseDbContext Db { get; } = new(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public InventoryMovementService Service { get; private set; } = null!;
        public PalletTrackingService Tracking { get; private set; } = null!;
        public UserPinService Pins { get; private set; } = null!;
        public Product Product { get; } = new() { Sku = "PLATE-TEST", BaseUnitId = 1 };
        public Location Source { get; } = new() { Code = "A-1-1", Kind = LocationKind.Area };
        public Location Destination { get; } = new() { Code = "B-1-1", Kind = LocationKind.Area };
        public User User { get; } = new() { FullName = "Plates test", RoleId = 1, PinLookup = "", PinHash = "" };
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); await f.Db.Database.EnsureCreatedAsync();
            f.Pins = new(f.Db, new PinProtector(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
            await f.Pins.AssignAsync(f.User, "2468"); f.Db.AddRange(f.User, f.Product, f.Source, f.Destination); await f.Db.SaveChangesAsync();
            f.Service = new(f.Db, f.Pins, TimeProvider.System); f.Tracking = new(f.Db, f.Pins, TimeProvider.System); return f;
        }
        public InventoryMovementCommand Command(InventoryMovementType type, decimal quantity, IReadOnlyList<decimal>? quantities = null, IReadOnlyList<PalletSelection>? plates = null) =>
            new(Guid.NewGuid(), type, "2468", [new(Product.Id, quantity, SourceLocationId: type == InventoryMovementType.Entry ? null : Source.Id,
                DestinationLocationId: type == InventoryMovementType.Exit ? null : type == InventoryMovementType.Entry ? Source.Id : Destination.Id,
                PalletQuantities: type == InventoryMovementType.Entry ? quantities ?? [quantity] : null, Plates: plates)]);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
