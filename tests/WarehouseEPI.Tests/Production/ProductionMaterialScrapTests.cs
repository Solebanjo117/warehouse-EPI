using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed partial class ProductionMaterialServiceTests
{
    [Fact]
    public async Task P5_material_scrap_uses_real_lots_reduces_reservation_and_reverses_once()
    {
        await using var f = await Fixture.CreateAsync();
        await f.IssueAsync(6);
        var issue = Assert.Single(await f.Materials.GetIssuesAsync(f.Order.Id));
        var command = new ProductionMaterialCommand(Guid.NewGuid(), f.Order.Id, f.OrderStage.Id, f.Order.Version,
            ProductionMaterialOperationType.Scrap, [new(issue.IssueLinkId, 2)], f.OperatorPin, Notes: "Daño durante corte");
        var first = await f.Materials.ApplyAsync(command);
        Assert.Equal(ProductionMaterialStatus.Success, first.Status);
        Assert.Equal(ProductionMaterialStatus.Success, (await f.Materials.ApplyAsync(command)).Status);
        var row = Assert.Single(await f.Materials.GetIssuesAsync(f.Order.Id));
        Assert.Equal(4, row.Pending); Assert.Equal(2, row.Scrapped); Assert.Equal(0, row.Consumed);
        Assert.Equal(4, await f.Db.InventoryBalances.Where(x => x.LocationId == f.Wip.Id).SumAsync(x => x.Quantity));
        var reverse = await f.Materials.ReverseAsync(Guid.NewGuid(), first.OperationId!.Value, f.Order.Version, f.AdminPin, "Captura equivocada");
        Assert.Equal(ProductionMaterialStatus.Success, reverse.Status);
        Assert.Equal(6, Assert.Single(await f.Materials.GetIssuesAsync(f.Order.Id)).Pending);
        Assert.Equal(6, await f.Db.InventoryBalances.Where(x => x.LocationId == f.Wip.Id).SumAsync(x => x.Quantity));
    }
}
