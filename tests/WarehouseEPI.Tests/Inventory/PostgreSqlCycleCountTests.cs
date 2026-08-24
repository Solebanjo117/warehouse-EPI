using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Pages.Operations.CycleCounts;

namespace WarehouseEPI.Tests.Inventory;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class PostgreSqlCycleCountTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Create_page_groups_physical_locations_and_preserves_selection_after_error()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var rowCode = ((char)('A' + Convert.ToInt32(suffix[..2], 16) % 26)).ToString();
        var rackNumber = (short)(100 + Convert.ToInt32(suffix[..4], 16) % 30000);
        await using var db = fixture.CreateDbContext();
        var top = new Location { Code = $"{rowCode}-{rackNumber}-7", Kind = LocationKind.Rack, RowCode = rowCode, RackNumber = rackNumber, PalletNumber = 7 };
        var bottom = new Location { Code = $"{rowCode}-{rackNumber}-1", Kind = LocationKind.Rack, RowCode = rowCode, RackNumber = rackNumber, PalletNumber = 1 };
        var area = new Location { Code = $"PG-AREA-{suffix}", Kind = LocationKind.Area };
        var wip = new Location { Code = $"PG-WIP-{suffix}", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        db.Locations.AddRange(top, bottom, area, wip);
        await db.SaveChangesAsync();
        var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
        var movements = new InventoryMovementService(db, pins, TimeProvider.System);
        var service = new CycleCountService(db, pins, new InventoryQueryService(db), movements, TimeProvider.System);
        var page = new CreateModel(db, service);

        await page.OnGetAsync(CancellationToken.None);

        var row = Assert.Single(page.RowGroups, item => item.RowCode == rowCode);
        var rack = Assert.Single(row.Racks, item => item.RackNumber == rackNumber);
        Assert.Equal([top.Id, bottom.Id], rack.Locations.Where(item => item.Id == top.Id || item.Id == bottom.Id).Select(item => item.Id));
        Assert.Contains(page.AreaLocations, item => item.Id == area.Id);
        Assert.DoesNotContain(page.Locations, item => item.Id == wip.Id);

        var operationId = Guid.NewGuid();
        page.Input = new()
        {
            OperationId = operationId,
            Title = "Selección conservada",
            Notes = "Dividir entre operadores",
            LocationIds = [top.Id],
            Pin = "00000000"
        };
        var result = await page.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(operationId, page.Input.OperationId);
        Assert.Equal("Selección conservada", page.Input.Title);
        Assert.Equal("Dividir entre operadores", page.Input.Notes);
        Assert.Equal([top.Id], page.Input.LocationIds);
        Assert.Empty(page.Input.Pin);
        Assert.NotNull(page.Error);
    }

    [Fact]
    public async Task Cycle_count_constraints_versions_and_atomic_approval_work_on_postgresql()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"PG-COUNT-{suffix}", $"PGC-{suffix}", "4386");
        var entry = await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 7m, DestinationLocationId: seed.LocationId)]));
        Assert.Equal(InventoryMovementStatus.Success, entry.Status);

        await using var db = fixture.CreateDbContext();
        var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
        var movements = new InventoryMovementService(db, pins, TimeProvider.System);
        var service = new CycleCountService(db, pins, new InventoryQueryService(db), movements, TimeProvider.System);

        var created = await service.CreateAsync(new(seed.Pin, "Conteo PostgreSQL", null, [seed.LocationId], OperationId: Guid.NewGuid()));
        Assert.Equal(CycleCountStatus.Success, created.Status);
        Assert.Equal(CycleCountStatus.Success, (await service.ReleaseAsync(created.CampaignId!.Value, Guid.NewGuid(), seed.Pin)).Status);
        var location = Assert.Single((await service.GetCampaignAsync(created.CampaignId.Value))!.Locations);
        var started = await service.StartAttemptAsync(location.Id, Guid.NewGuid(), seed.Pin);
        var submitted = await service.SubmitAsync(new(started.AttemptId!.Value, Guid.NewGuid(), seed.Pin, [new(seed.ProductId, 5m)]));
        var approved = await service.ApproveAsync(new(location.Id, Guid.NewGuid(), seed.Pin, "Validación PostgreSQL"));

        Assert.Equal(CycleCountStatus.Success, submitted.Status);
        Assert.Equal(CycleCountStatus.Success, approved.Status);
        var movement = await db.InventoryMovements.Include(item => item.Lines)
            .SingleAsync(item => item.Id == approved.MovementId);
        Assert.Equal(InventoryMovementPurpose.CycleCountAdjustment, movement.Purpose);
        Assert.Single(movement.Lines);
        Assert.Equal(5m, (await new InventoryQueryService(db).GetBalanceAsync(seed.ProductId, seed.LocationId)).Quantity);
        Assert.Equal(CycleCountCampaignStatus.Completed, (await service.GetCampaignAsync(created.CampaignId.Value))!.Status);
    }
}
