using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed partial class ProductionSupplyServiceTests
{
    [Fact]
    public async Task Save_proof_survives_later_edits_and_is_scoped_to_line()
    {
        await using var f = await Fixture.CreateAsync(stock: 10);
        await f.CreateAndReleaseAsync(10);
        var line = Assert.Single(await f.Supplies.GetQueueAsync());
        var detail = (await f.Preparations.GetAsync(line.LineId))!;
        var source = detail.Sources.First(x => x.Kind == ProductionSupplySourceKind.Warehouse);
        var command = new ProductionSupplySaveCommand(Guid.NewGuid(), line.LineId, line.RequestVersion,
            detail.DestinationLocationId, null, 0, [new(source.Kind, source.LocationId, 4)], f.OperatorPin);
        Assert.Equal(ProductionSupplyCommandStatus.Success, (await f.Preparations.SaveAsync(command)).Status);
        Assert.Equal(ProductionSupplyCommandStatus.Success, (await f.Preparations.SaveAsync(command)).Status);
        Assert.True((await f.Preparations.GetSaveResultAsync(command.OperationId, line.LineId))!.IsCurrent);
        Assert.Null(await f.Preparations.GetSaveResultAsync(command.OperationId, Guid.NewGuid()));
        detail = (await f.Preparations.GetAsync(line.LineId))!;
        Assert.Equal(ProductionSupplyCommandStatus.Success, (await f.Preparations.SaveAsync(command with {
            OperationId = Guid.NewGuid(), PreparationId = detail.PreparationId,
            ExpectedPreparationVersion = detail.PreparationVersion, ExpectedRequestVersion = detail.Line.RequestVersion,
            Sources = [new(source.Kind, source.LocationId, 3)] })).Status);
        Assert.False((await f.Preparations.GetSaveResultAsync(command.OperationId, line.LineId))!.IsCurrent);
        Assert.Equal(2, await f.Db.ProductionSupplyEvents.CountAsync(x => x.Type == ProductionSupplyEventType.PreparationSaved));
        Assert.Empty(f.Db.InventoryMovements);
    }
}

public sealed partial class ProductionTraceabilityServiceTests
{
    [Fact]
    public async Task Result_rejects_case_in_ordinary_mode_and_rework_without_explicit_case()
    {
        await using var f = await Fixture.CreateAsync();
        var order = await CreateExecutionOrder(f);
        var batch = await f.Trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 10, order.Version, f.AdminPin));
        var command = new RecordBatchResultCommand(Guid.NewGuid(), order.Id, batch.Id!.Value,
            order.Stages.First().Id, f.Shift.Id, false, 1, 1, 0, 0, [], order.Version, null, f.AdminPin, Guid.NewGuid());
        Assert.False((await f.Trace.RecordResultAsync(command)).Success);
        Assert.False((await f.Trace.RecordResultAsync(command with { IsRework = true, ReworkCaseId = null })).Success);
        Assert.Empty(f.Db.ProductionBatchResults);
    }
}
