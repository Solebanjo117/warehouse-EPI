using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Tests.Inventory;

public sealed partial class PalletTrackingTests
{
    private static MaterialIncidentService Incidents(Fixture f) => new(f.Db, f.Pins, TimeProvider.System);
    private static async Task<IncidentReport> IncidentRequest(Fixture f, IncidentContextRequest context, decimal? quantity = null)
    {
        var resolved = await Incidents(f).ContextAsync(context);
        Assert.NotNull(resolved);
        return new(Guid.NewGuid(), context, resolved.Token, MaterialIncidentScope.Undetermined, MaterialIncidentKind.Damage,
            MaterialIncidentDifference.Undetermined, quantity, "Material dañado al inspeccionar", "2468", []);
    }

    [Fact]
    public async Task MaterialIncident_arrival_context_does_not_sum_other_arrivals_and_unit_rules_are_enforced()
    {
        await using var f = await Fixture.Create(); f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        foreach (var q in new[] { 10m, 20m }) await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, q));
        var row = (await new StagingArrivalQuery(f.Db).ListAsync(null)).Items.Single(r => r.Received == 10);
        Assert.Equal(10, (await Incidents(f).ContextAsync(new(ArrivalLineId: row.LineId)))!.ObservedQuantity);
        (await f.Db.Units.SingleAsync(u => u.Id == 1)).AllowsDecimals = false; await f.Db.SaveChangesAsync();
        var request = await IncidentRequest(f, new(ArrivalLineId: row.LineId), 1.5m);
        Assert.NotNull((await Incidents(f).ReportAsync(request)).Error);
        Assert.Null((await Incidents(f).ReportAsync(request with { Quantity = 100 })).Error);
    }

    [Fact]
    public async Task MaterialIncident_correction_links_are_validated_and_historical_context_remains_reportable()
    {
        await using var f = await Fixture.Create(); f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 20));
        var line = await f.Db.InventoryMovementLines.SingleAsync();
        var report = await Incidents(f).ReportAsync(await IncidentRequest(f, new(ArrivalLineId: line.Id)));
        var corrections = new InventoryCorrectionService(f.Db, f.Pins, f.Service, TimeProvider.System);
        Assert.Equal(InventoryCorrectionStatus.Success, (await corrections.ConfirmAsync(new(Guid.NewGuid(), entry.MovementId!.Value, f.User.Id, "2468", "Cantidad incorrecta"))).Status);
        var correction = await f.Db.InventoryMovementCorrections.SingleAsync();
        Assert.Null((await Incidents(f).FollowUpAsync(new(Guid.NewGuid(), report.Id!.Value, 1, "Review", "Revisión de corrección", null, "2468", []))).Error);
        var follow = new IncidentFollowUp(Guid.NewGuid(), report.Id.Value, 2, "Resolve", "Corrección aplicada", correction.Id, "2468", []);
        Assert.NotNull((await Incidents(f).FollowUpAsync(follow with { CorrectionId = Guid.NewGuid() })).Error);
        Assert.Null((await Incidents(f).FollowUpAsync(follow)).Error);
        Assert.Equal(correction.Id, (await f.Db.MaterialIncidentEvents.SingleAsync(e => e.Action == "Resolve")).CorrectionId);
        var context = await Incidents(f).ContextAsync(new(ArrivalLineId: line.Id)); Assert.NotNull(context); Assert.Equal("Movimiento corregido", context.State);
        Assert.Null((await Incidents(f).ReportAsync(await IncidentRequest(f, new(ArrivalLineId: line.Id)))).Error);
    }

    [Fact]
    public async Task MaterialIncident_mixed_plates_do_not_infer_a_receipt_from_origin_movement_or_sku()
    {
        await using var f = await Fixture.Create();
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 10));
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 20));
        Assert.Equal(InventoryMovementStatus.Success, (await f.Tracking.ConsolidateAsync(new(Guid.NewGuid(), f.Product.Id, f.Source.Id))).Status);
        var plate = await f.Db.PalletPlates.SingleAsync(p => p.Quantity == 30);
        Assert.Null((await Incidents(f).ContextAsync(new(PlateId: plate.Id)))!.ArrivalLineId);
        var request = await IncidentRequest(f, new(PlateId: plate.Id));
        Assert.Null((await Incidents(f).ReportAsync(request with { Scope = MaterialIncidentScope.Receiving })).Error);
        Assert.Null((await f.Db.MaterialIncidents.SingleAsync()).ArrivalLineId);
    }

    [Fact]
    public async Task MaterialIncident_report_and_close_are_immutable_idempotent_and_do_not_change_stock()
    {
        await using var f = await Fixture.Create();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        var plate = Assert.Single(entry.Plates!);
        var request = await IncidentRequest(f, new(PlateId: plate.PlateId), 150);
        var snapshot = JsonSerializer.Serialize(await f.Db.InventoryBalances.AsNoTracking().ToArrayAsync());
        var service = Incidents(f);
        var result = await service.ReportAsync(request);
        Assert.Null(result.Error); Assert.NotNull(result.Id);
        Assert.Equal(result, await service.ReportAsync(request));
        Assert.NotNull((await service.ReportAsync(request with { Description = "Otro problema" })).Error);
        var item = await f.Db.MaterialIncidents.SingleAsync();
        Assert.Equal((await f.Db.InventoryMovementLines.SingleAsync()).Id, item.ArrivalLineId);
        Assert.NotNull((await service.FollowUpAsync(new(Guid.NewGuid(), item.Id, 1, "Resolve", "Primero revisar", null, "2468", []))).Error);
        Assert.Null((await service.FollowUpAsync(new(Guid.NewGuid(), item.Id, 1, "Review", "Inspección", null, "2468", []))).Error);
        var close = new IncidentFollowUp(Guid.NewGuid(), item.Id, 2, "Resolve", "Resuelto sin ajuste", null, "2468", []);
        Assert.Null((await service.FollowUpAsync(close)).Error);
        Assert.Null((await service.FollowUpAsync(close)).Error);
        Assert.Equal(3, await f.Db.MaterialIncidentEvents.CountAsync());
        Assert.Equal(snapshot, JsonSerializer.Serialize(await f.Db.InventoryBalances.AsNoTracking().ToArrayAsync()));
        Assert.Single(await f.Db.InventoryMovements.ToListAsync()); Assert.Single(await f.Db.PalletPlateEvents.ToListAsync());
        item.Description = "Reescritura prohibida";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task MaterialIncident_permissions_transitions_and_expected_version_are_checked_on_server()
    {
        await using var f = await Fixture.Create();
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 1));
        var worker = new User { FullName = "Operador", RoleId = 2, PinHash = "", PinLookup = "" };
        await f.Pins.AssignAsync(worker, "3579"); f.Db.Add(worker); await f.Db.SaveChangesAsync();
        var service = Incidents(f); var request = await IncidentRequest(f, new(ProductId: f.Product.Id, LocationId: f.Source.Id));
        Assert.NotNull((await service.ReportAsync(request with { Pin = "0000" })).Error);
        var id = (await service.ReportAsync(request with { Pin = "3579" })).Id!.Value;
        var follow = new IncidentFollowUp(Guid.NewGuid(), id, 1, "Review", "Revisión física", null, "3579", []);
        Assert.Null((await service.FollowUpAsync(follow)).Error);
        Assert.NotNull((await service.FollowUpAsync(follow with { OperationId = Guid.NewGuid() })).Error);
        foreach (var action in new[] { "Resolve", "Void", "Reopen" })
            Assert.NotNull((await service.FollowUpAsync(follow with { OperationId = Guid.NewGuid(), ExpectedVersion = 2, Action = action })).Error);
        Assert.NotNull((await service.FollowUpAsync(follow with { OperationId = Guid.NewGuid(), ExpectedVersion = 2, Action = "Resolve", Pin = "2468", Comment = "" })).Error);
        Assert.Null((await service.FollowUpAsync(follow with { OperationId = Guid.NewGuid(), ExpectedVersion = 2, Action = "Void", Pin = "2468" })).Error);
        Assert.NotNull((await service.FollowUpAsync(follow with { OperationId = Guid.NewGuid(), ExpectedVersion = 3, Action = "Comment" })).Error);
        Assert.Null((await service.FollowUpAsync(follow with { OperationId = Guid.NewGuid(), ExpectedVersion = 3, Action = "Reopen", Pin = "2468" })).Error);
        Assert.Equal(MaterialIncidentStatus.Reviewing, (await f.Db.MaterialIncidents.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("0")][InlineData("-1")][InlineData("0.00001")][InlineData("100000000000000")]
    public async Task MaterialIncident_invalid_quantities_are_rejected(string amount)
    {
        await using var f = await Fixture.Create(); await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 1));
        var request = await IncidentRequest(f, new(ProductId: f.Product.Id, LocationId: f.Source.Id), decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotNull((await Incidents(f).ReportAsync(request)).Error); Assert.Empty(await f.Db.MaterialIncidents.ToListAsync());
    }

    [Fact]
    public async Task MaterialIncident_historical_zero_negative_inactive_contexts_and_changed_plate()
    {
        await using var f = await Fixture.Create(); var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 20));
        var plate = Assert.Single(entry.Plates!); var request = await IncidentRequest(f, new(PlateId: plate.PlateId));
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Exit, 20, plates: [new(plate.PlateId, 20, plate.Version)]));
        Assert.True((await Incidents(f).ReportAsync(request)).ContextChanged);
        f.Source.IsActive = false; f.Source.IsBlocked = true; f.Product.IsActive = false; await f.Db.SaveChangesAsync();
        request = await IncidentRequest(f, new(PlateId: plate.PlateId), 25);
        Assert.Null((await Incidents(f).ReportAsync(request)).Error);
        (await f.Db.InventoryBalances.SingleAsync()).Quantity = -30; await f.Db.SaveChangesAsync();
        Assert.Null((await Incidents(f).ReportAsync(await IncidentRequest(f, new(ProductId: f.Product.Id, LocationId: f.Source.Id), 40))).Error);
        Assert.Null(await Incidents(f).ContextAsync(new(ProductId: f.Product.Id, LocationId: f.Destination.Id)));
        Assert.Null(await Incidents(f).ContextAsync(new(PlateId: plate.PlateId, ProductId: Guid.NewGuid())));
        f.Destination.OperationalRole = LocationOperationalRole.Wip; await f.Db.SaveChangesAsync();
        Assert.Null(await Incidents(f).ContextAsync(new(PlateId: plate.PlateId, LocationId: f.Destination.Id)));
    }

    [Fact]
    public async Task MaterialIncident_exact_arrivals_split_and_transfer_children_keep_related_issues_without_duplicates()
    {
        await using var f = await Fixture.Create(); f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100)); var plate = Assert.Single(entry.Plates!);
        var row = Assert.Single((await new StagingArrivalQuery(f.Db).ListAsync(null)).Items);
        var request = await IncidentRequest(f, new(row.LineId, plate.PlateId));
        var result = await Incidents(f).ReportAsync(request); Assert.Null(result.Error);
        var split = await f.Tracking.SplitStagingPlateAsync(new(Guid.NewGuid(), row.LineId, plate.PlateId, plate.Version, row.Version, [40, 60], "2468"));
        Assert.Equal(InventoryMovementStatus.Success, split.Status);
        var child = split.Plates![0]; Assert.Equal(row.LineId, (await Incidents(f).ContextAsync(new(PlateId: child.PlateId)))!.ArrivalLineId);
        var query = new MaterialIncidentQuery(f.Db, Incidents(f));
        var counts = await query.CountsAsync([new(child.PlateId, f.Product.Id, PlateId: child.PlateId), new(f.Source.Id, f.Product.Id, f.Source.Id)]);
        Assert.Equal(new IncidentCounts(0, 1), counts[child.PlateId]); Assert.Equal(new IncidentCounts(1, 0), counts[f.Source.Id]);
        Assert.Single((await query.ListAsync(new(PlateId: child.PlateId))).Items);
        var transfer = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Transfer, 10, plates: [new(child.PlateId, 10, child.Version)]));
        Assert.Equal(InventoryMovementStatus.Success, transfer.Status);
        var moved = await f.Db.PalletPlates.SingleAsync(p => p.LocationId == f.Destination.Id);
        Assert.Equal(row.LineId, (await Incidents(f).ContextAsync(new(PlateId: moved.Id)))!.ArrivalLineId);
        counts = await query.CountsAsync([new(f.Destination.Id, f.Product.Id, f.Destination.Id)]);
        Assert.Equal(new IncidentCounts(0, 1), counts[f.Destination.Id]);
        Assert.Single((await query.ListAsync(new(LocationId: f.Destination.Id, Relation: "current"))).Items);
        Assert.Equal(InventoryMovementStatus.Success, (await f.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Exit, "2468",
            [new(f.Product.Id, 10, SourceLocationId: f.Destination.Id, Plates: [new(moved.Id, 10, moved.Version)])]))).Status);
        counts = await query.CountsAsync([new(f.Destination.Id, f.Product.Id, f.Destination.Id)]);
        Assert.Equal(new IncidentCounts(0, 0), counts[f.Destination.Id]);
        Assert.Empty((await query.ListAsync(new(LocationId: f.Destination.Id, Relation: "current"))).Items);
        Assert.Single((await query.ListAsync(new(PlateId: moved.Id))).Items);
        var another = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 10));
        var otherLine = await f.Db.InventoryMovementLines.SingleAsync(l => l.MovementId == another.MovementId);
        Assert.Null(await Incidents(f).ContextAsync(new(otherLine.Id, plate.PlateId)));
    }

    [Fact]
    public async Task MaterialIncident_pagination_filters_and_photos_are_independent_of_balances()
    {
        await using var f = await Fixture.Create(); await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 1));
        var service = Incidents(f); var request = await IncidentRequest(f, new(ProductId: f.Product.Id, LocationId: f.Source.Id));
        for (var i = 0; i < 30; i++) Assert.Null((await service.ReportAsync(request with { OperationId = Guid.NewGuid(), Description = $"Problema {i}" })).Error);
        var query = new MaterialIncidentQuery(f.Db, service); var first = await query.ListAsync(new());
        Assert.Equal(25, first.Items.Count); Assert.True(first.HasMore); Assert.Equal(5, (await query.ListAsync(new(Page: 2))).Items.Count);
        Assert.Empty((await query.ListAsync(new(Scope: MaterialIncidentScope.Receiving))).Items);
        Assert.Single((await query.ListAsync(new(Search: first.Items[0].Folio))).Items);
        Assert.Empty((await query.ListAsync(new(ProductCode: "other"))).Items);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aTioAAAAASUVORK5CYII=");
        var photo = new IncidentPhotoInput("proof.png", "image/png", png);
        var withPhoto = request with { OperationId = Guid.NewGuid(), Photos = [photo, photo] };
        var result = await service.ReportAsync(withPhoto); Assert.Null(result.Error);
        Assert.Equal(result, await service.ReportAsync(withPhoto)); Assert.Single(await f.Db.MaterialIncidentPhotos.ToListAsync());
        var invalid = await service.ReportAsync(request with { OperationId = Guid.NewGuid(), Photos = [photo with { ContentType = "image/svg+xml" }] });
        Assert.NotNull(invalid.Error); Assert.Equal(31, await f.Db.MaterialIncidents.CountAsync());
        Assert.NotNull((await service.ReportAsync(request with { OperationId = Guid.NewGuid(), Photos = [photo with { Content = new byte[5 * 1024 * 1024 + 1] }] })).Error);
        var huge = png.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(16, 4), 4097);
        Assert.NotNull((await service.ReportAsync(request with { OperationId = Guid.NewGuid(), Photos = [photo with { Content = huge }] })).Error);
        var excess = Enumerable.Range(0, 6).Select(i => { var bytes = png.ToArray(); bytes[^1] ^= (byte)i; return photo with { Content = bytes }; }).ToArray();
        Assert.Equal("Máximo 5 fotografías por incidencia.", (await service.ReportAsync(request with { OperationId = Guid.NewGuid(), Photos = excess })).Error);
        var follow = new IncidentFollowUp(Guid.NewGuid(), result.Id!.Value, 1, "Comment", "Misma evidencia", null, "2468", [photo]);
        Assert.Null((await service.FollowUpAsync(follow)).Error); Assert.Single(await f.Db.MaterialIncidentPhotos.ToListAsync());
        Assert.Null((await service.FollowUpAsync(new(Guid.NewGuid(), first.Items[0].Id, 1, "Comment", "", null, "2468", [photo]))).Error);
        Assert.Equal(2, await f.Db.MaterialIncidentPhotos.CountAsync());
    }
}
