using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Inventory;

public sealed class CycleCountServiceTests
{
    [Fact]
    public async Task Matching_blind_count_completes_without_adjustment()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("COUNT-MATCH");
        var location = await fixture.AddLocationAsync("A-1-1");
        await fixture.EnterAsync(product.Id, location.Id, 5m);
        var (campaignId, countLocationId, attemptId) = await fixture.CreateReleasedAttemptAsync(location.Id);

        var blind = await fixture.CycleCounts.GetAttemptAsync(attemptId, false);
        Assert.Null(Assert.Single(blind!.Entries).ExpectedQuantity);
        var submitted = await fixture.CycleCounts.SubmitAsync(new(attemptId, Guid.NewGuid(), fixture.Pin, [new(product.Id, 5m)]));

        Assert.Equal(CycleCountStatus.Success, submitted.Status);
        var campaign = await fixture.CycleCounts.GetCampaignAsync(campaignId);
        Assert.Equal(CycleCountCampaignStatus.Completed, campaign!.Status);
        Assert.Equal(CycleCountLocationStatus.Completed, Assert.Single(campaign.Locations).Status);
        Assert.Single(await fixture.Db.InventoryMovements.ToListAsync());
        Assert.Equal(countLocationId, submitted.LocationId);
    }

    [Fact]
    public async Task Difference_requires_review_and_approval_creates_one_cycle_count_adjustment()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("COUNT-DIFF");
        var location = await fixture.AddLocationAsync("A-1-2");
        await fixture.EnterAsync(product.Id, location.Id, 8m);
        var (_, countLocationId, attemptId) = await fixture.CreateReleasedAttemptAsync(location.Id);
        var submitted = await fixture.CycleCounts.SubmitAsync(new(attemptId, Guid.NewGuid(), fixture.Pin, [new(product.Id, 6m)]));
        Assert.Equal(CycleCountStatus.Success, submitted.Status);
        Assert.Equal(CycleCountLocationStatus.UnderReview, (await fixture.CycleCounts.GetCampaignAsync(submitted.CampaignId!.Value))!.Locations.Single().Status);

        var approved = await fixture.CycleCounts.ApproveAsync(new(countLocationId, Guid.NewGuid(), fixture.Pin, "Diferencia física confirmada"));

        Assert.Equal(CycleCountStatus.Success, approved.Status);
        var movement = await fixture.Db.InventoryMovements.SingleAsync(item => item.Id == approved.MovementId);
        Assert.Equal(InventoryMovementType.Adjustment, movement.Type);
        Assert.Equal(InventoryMovementPurpose.CycleCountAdjustment, movement.Purpose);
        Assert.StartsWith("CC-", movement.Reference, StringComparison.Ordinal);
        Assert.Equal(6m, (await new InventoryQueryService(fixture.Db).GetBalanceAsync(product.Id, location.Id)).Quantity);
    }

    [Fact]
    public async Task Inventory_change_after_start_marks_attempt_stale_and_does_not_adjust()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("COUNT-STALE");
        var location = await fixture.AddLocationAsync("A-1-3");
        await fixture.EnterAsync(product.Id, location.Id, 2m);
        var (campaignId, _, attemptId) = await fixture.CreateReleasedAttemptAsync(location.Id);
        await fixture.EnterAsync(product.Id, location.Id, 1m);

        var result = await fixture.CycleCounts.SubmitAsync(new(attemptId, Guid.NewGuid(), fixture.Pin, [new(product.Id, 2m)]));

        Assert.Equal(CycleCountStatus.BalanceChanged, result.Status);
        Assert.Equal(CycleCountLocationStatus.Stale, (await fixture.CycleCounts.GetCampaignAsync(campaignId))!.Locations.Single().Status);
        Assert.Equal(2, await fixture.Db.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task Recount_is_manual_blind_and_preserves_previous_attempt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("COUNT-RECOUNT");
        var location = await fixture.AddLocationAsync("B-1-1");
        await fixture.EnterAsync(product.Id, location.Id, 4m);
        var (_, countLocationId, firstAttemptId) = await fixture.CreateReleasedAttemptAsync(location.Id);
        await fixture.CycleCounts.SubmitAsync(new(firstAttemptId, Guid.NewGuid(), fixture.Pin, [new(product.Id, 3m)]));
        var requested = await fixture.CycleCounts.RequestRecountAsync(new(countLocationId, Guid.NewGuid(), fixture.Pin, "Verificar diferencia"));
        Assert.Equal(CycleCountStatus.Success, requested.Status);

        var second = await fixture.CycleCounts.StartAttemptAsync(countLocationId, Guid.NewGuid(), fixture.Pin);

        Assert.Equal(CycleCountStatus.Success, second.Status);
        Assert.Equal(2, (await fixture.CycleCounts.GetAttemptAsync(second.AttemptId!.Value, false))!.AttemptNumber);
        Assert.Equal(2, await fixture.Db.CycleCountAttempts.CountAsync());
        Assert.Equal(CycleCountAttemptStatus.Superseded, (await fixture.Db.CycleCountAttempts.SingleAsync(item => item.Id == firstAttemptId)).Status);
    }

    [Fact]
    public async Task Overlapping_open_campaign_and_invalid_locations_are_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var location = await fixture.AddLocationAsync("C-1-1");
        var operationId = Guid.NewGuid();
        var first = await fixture.CycleCounts.CreateAsync(new(fixture.Pin, "Primera", null, [location.Id], OperationId: operationId));
        var repeated = await fixture.CycleCounts.CreateAsync(new(fixture.Pin, "Primera", null, [location.Id], OperationId: operationId));
        var overlapping = await fixture.CycleCounts.CreateAsync(new(fixture.Pin, "Segunda", null, [location.Id], OperationId: Guid.NewGuid()));
        var wip = await fixture.AddLocationAsync("WIP-COUNT", LocationOperationalRole.Wip);
        var invalid = await fixture.CycleCounts.CreateAsync(new(fixture.Pin, "WIP", null, [wip.Id], OperationId: Guid.NewGuid()));

        Assert.Equal(CycleCountStatus.Success, first.Status);
        Assert.Equal(first.CampaignId, repeated.CampaignId);
        Assert.Equal(CycleCountStatus.ValidationFailed, overlapping.Status);
        Assert.Equal(CycleCountStatus.ValidationFailed, invalid.Status);
    }

    [Fact]
    public async Task Submit_is_idempotent_and_rejects_negative_physical_quantities()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("COUNT-IDEMPOTENT");
        var location = await fixture.AddLocationAsync("C-1-2");
        var (_, _, attemptId) = await fixture.CreateReleasedAttemptAsync(location.Id);
        var operationId = Guid.NewGuid();

        var invalid = await fixture.CycleCounts.SubmitAsync(new(attemptId, Guid.NewGuid(), fixture.Pin, [new(product.Id, -1m)]));
        var first = await fixture.CycleCounts.SubmitAsync(new(attemptId, operationId, fixture.Pin, [new(product.Id, 0m)]));
        var repeated = await fixture.CycleCounts.SubmitAsync(new(attemptId, operationId, fixture.Pin, [new(product.Id, 0m)]));

        Assert.Equal(CycleCountStatus.ValidationFailed, invalid.Status);
        Assert.Equal(CycleCountStatus.Success, first.Status);
        Assert.Equal(CycleCountStatus.Success, repeated.Status);
        Assert.Equal(operationId, (await fixture.Db.CycleCountAttempts.SingleAsync(item => item.Id == attemptId)).SubmissionOperationId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string LookupKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        public string Pin { get; } = "2468";
        public WarehouseDbContext Db { get; }
        public InventoryMovementService Movements { get; }
        public CycleCountService CycleCounts { get; }

        private Fixture(WarehouseDbContext db, InventoryMovementService movements, CycleCountService cycleCounts) { Db = db; Movements = movements; CycleCounts = cycleCounts; }
        public static async Task<Fixture> CreateAsync()
        {
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase($"CycleCounts-{Guid.NewGuid():N}").Options);
            await db.Database.EnsureCreatedAsync();
            var pinService = new UserPinService(db, new PinProtector(LookupKey));
            var user = new User { FullName = "Contador", RoleId = 2, PinLookup = string.Empty, PinHash = string.Empty };
            Assert.Equal(PinAssignmentResult.Success, await pinService.AssignAsync(user, "2468")); db.Users.Add(user); await db.SaveChangesAsync();
            var movements = new InventoryMovementService(db, pinService, TimeProvider.System);
            return new(db, movements, new CycleCountService(db, pinService, new InventoryQueryService(db), movements, TimeProvider.System));
        }
        public async Task<Product> AddProductAsync(string sku) { var item = new Product { Sku = sku, BaseUnitId = 1 }; Db.Products.Add(item); await Db.SaveChangesAsync(); return item; }
        public async Task<Location> AddLocationAsync(string code, LocationOperationalRole role = LocationOperationalRole.Storage) { var item = new Location { Code = code, Kind = LocationKind.Rack, OperationalRole = role, RowCode = code.Split('-')[0] }; Db.Locations.Add(item); await Db.SaveChangesAsync(); return item; }
        public async Task EnterAsync(Guid productId, Guid locationId, decimal quantity) { var result = await Movements.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, Pin, [new(productId, quantity, DestinationLocationId: locationId)])); Assert.Equal(InventoryMovementStatus.Success, result.Status); }
        public async Task<(Guid CampaignId, Guid LocationId, Guid AttemptId)> CreateReleasedAttemptAsync(Guid locationId)
        { var created = await CycleCounts.CreateAsync(new(Pin, "Campaña", null, [locationId], OperationId: Guid.NewGuid())); Assert.Equal(CycleCountStatus.Success, created.Status); Assert.Equal(CycleCountStatus.Success, (await CycleCounts.ReleaseAsync(created.CampaignId!.Value, Guid.NewGuid(), Pin)).Status); var detail = await CycleCounts.GetCampaignAsync(created.CampaignId.Value); var countLocation = Assert.Single(detail!.Locations); var attempt = await CycleCounts.StartAttemptAsync(countLocation.Id, Guid.NewGuid(), Pin); return (created.CampaignId.Value, countLocation.Id, attempt.AttemptId!.Value); }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
