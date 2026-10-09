using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Tests.Inventory;

public sealed partial class PalletTrackingTests
{
    [Theory]
    [InlineData(-1, StagingPriority.Normal)]
    [InlineData(0, StagingPriority.Normal)]
    [InlineData(23.999, StagingPriority.Normal)]
    [InlineData(24, StagingPriority.Soon)]
    [InlineData(47.999, StagingPriority.Soon)]
    [InlineData(48, StagingPriority.Urgent)]
    public void Staging_priority_uses_elapsed_instants(double hours, StagingPriority expected)
    {
        var now = new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);
        var occurred = now.AddHours(-hours).ToOffset(TimeSpan.FromHours(-6));
        Assert.Equal(expected, StagingArrivalAge.Priority(occurred, now));
        Assert.True(StagingArrivalAge.Elapsed(occurred, now) >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Staging_overview_filters_before_paging_and_counts_all_batches_globally()
    {
        await using var f = await Fixture.Create(); f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var query = new StagingArrivalQuery(f.Db);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(0, (await query.OverviewAsync(null, null, 1, now)).Summary.Total);
        for (var i = 0; i < 105; i++)
            f.Db.InventoryMovements.Add(new InventoryMovement { OperationId = Guid.NewGuid(), RequestFingerprint = $"priority-{i}",
                Type = InventoryMovementType.Entry, ResponsibleUserId = f.User.Id, OccurredAt = now.AddHours(-i), Reference = i % 2 == 0 ? "MATCH" : "OTHER",
                Lines = [new InventoryMovementLine { ProductId = f.Product.Id, UnitId = 1, DestinationLocationId = f.Source.Id, Quantity = 1, LineNumber = 1 }] });
        await f.Db.SaveChangesAsync();
        var first = await query.OverviewAsync(null, null, 1, now);
        Assert.Equal(new StagingPendingSummary(24, 24, 57), first.Summary);
        Assert.Equal(25, first.Page.Items.Count); Assert.True(first.Page.HasMore);
        Assert.Equal(now.AddHours(-104), first.Page.Items[0].OccurredAt);
        Assert.All(first.Page.Items, r => Assert.True(r.NeedsIdentification));
        var filtered = await query.OverviewAsync("match", StagingPriority.Urgent, 2, now);
        Assert.Equal(first.Summary, filtered.Summary);
        Assert.Equal(4, filtered.Page.Items.Count); Assert.False(filtered.Page.HasMore);
        Assert.All(filtered.Page.Items, r => { Assert.Equal("MATCH", r.Reference); Assert.True(r.OccurredAt <= now.AddHours(-48)); });
        Assert.Empty((await query.OverviewAsync("absent", null, 1, now)).Page.Items);
        Assert.Equal(105, await query.CountPendingAsync());
        Assert.Null(StagingArrivalAge.Parse("unknown")); Assert.Equal(StagingPriority.Soon, StagingArrivalAge.Parse("SOON"));
    }
}
