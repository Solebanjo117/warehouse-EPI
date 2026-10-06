using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionBalanceCaptureEligibilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inactive_product_or_stage_remains_blocked(bool inactiveProduct)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (_, actor, productId) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        if (inactiveProduct)
            (await db.Products.SingleAsync(x => x.Id == productId)).IsActive = false;
        else
        {
            var stageId = (await db.ProductionDailyConfigurations.SingleAsync()).SewingStageId;
            (await db.ProductionStages.SingleAsync(x => x.Id == stageId)).IsActive = false;
        }
        var week = new ProductionScheduleWeek
        {
            CreatedByUserId = actor,
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "inactive-eligibility",
            WeekStart = new(2026, 10, 5),
            WeekEnd = new(2026, 10, 11),
            ExplicitCarryover = true,
            Status = ProductionScheduleWeekStatus.Open
        };
        db.Add(week);
        await db.SaveChangesAsync();
        var eligibility = await new ProductionDailyBalanceService(db)
            .GetCaptureEligibilityAsync(week.Id, week.WeekStart, [productId]);
        Assert.DoesNotContain((productId, ProductionDailyArea.Sewing), eligibility);
        var capture = ProductionDailyFlexibleTests.Capture(db);
        var command = new ProductionBalanceEditCommand(Guid.NewGuid(), week.Id, week.WeekStart,
            [new(productId, ProductionDailyArea.Sewing, 1, 0, 100)], Pin: "4826");
        Assert.False((await capture.PreviewBalanceEditAsync(command)).CanConfirm);
        Assert.False((await capture.ConfirmBalanceEditAsync(command)).Success);
        Assert.Empty(await db.ProductionDailyCaptures.ToArrayAsync());
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(18, false, false)]
    [InlineData(1, true, false)]
    [InlineData(18, true, false)]
    [InlineData(1, false, true)]
    [InlineData(18, false, true)]
    [InlineData(1, true, true)]
    [InlineData(18, true, true)]
    public async Task Opening_only_eligibility_ignores_route_and_preserves_week_and_balances(
        short unitId, bool cuttingOnly, bool closed)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (_, actor, productId) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        db.ChangeTracker.Clear();
        var product = await db.Products.SingleAsync(x => x.Id == productId);
        product.BaseUnitId = unitId;
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        if (cuttingOnly)
            db.ProductionRouteStages.RemoveRange(await db.ProductionRouteStages
                .Where(x => x.StageId != config.CuttingStageId).ToArrayAsync());
        var week = new ProductionScheduleWeek
        {
            CreatedByUserId = actor,
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "eligibility",
            WeekStart = new(2026, 10, 5),
            WeekEnd = new(2026, 10, 11),
            ExplicitCarryover = true,
            Status = closed ? ProductionScheduleWeekStatus.Closed : ProductionScheduleWeekStatus.Open
        };
        db.Add(week);
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
            db.Add(new ProductionInitialBalance
            {
                WeekId = week.Id,
                ProductId = productId,
                Area = area,
                Quantity = area == ProductionDailyArea.Cutting ? 700 : 0
            });
        await db.SaveChangesAsync();
        var balances = new ProductionDailyBalanceService(db);
        var before = await balances.GetDailySummaryAsync(week.Id, new(week.WeekStart));
        var eligibility = await balances.GetCaptureEligibilityAsync(week.Id, week.WeekStart, [productId]);
        Assert.Equal(!closed, eligibility.Contains((productId, ProductionDailyArea.Cutting)));
        Assert.Equal(!closed, eligibility.Contains((productId, ProductionDailyArea.Sewing)));
        Assert.Equal(!closed, eligibility.Contains((productId, ProductionDailyArea.ReadyToPack)));
        Assert.Empty(await balances.GetCaptureEligibilityAsync(week.Id, week.WeekEnd.AddDays(1), [productId]));
        var after = await balances.GetDailySummaryAsync(week.Id, new(week.WeekStart));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        var row = Assert.Single(after!.Products);
        Assert.Equal(0, row.Planned);
        Assert.Equal(700, row.Cutting.SignedPending);
        Assert.Equal(0, row.Sewing.SignedPending);
        Assert.Equal(0, row.ReadyToPack.SignedPending);
        Assert.False(row.Sewing.Applies); // Activity reporting remains independent of capture eligibility.

        var capture = ProductionDailyFlexibleTests.Capture(db);
        foreach (var area in new[] { ProductionDailyArea.Sewing, ProductionDailyArea.ReadyToPack })
        {
            var command = new ProductionBalanceEditCommand(Guid.NewGuid(), week.Id, week.WeekStart,
                [new(productId, area, 1, 0, 100)], Pin: "4826");
            var preview = await capture.PreviewBalanceEditAsync(command);
            Assert.Equal(!closed, preview.CanConfirm);
            if (closed)
            {
                Assert.False((await capture.ConfirmBalanceEditAsync(command)).Success);
                Assert.Contains(preview.Errors, error => error.Contains("semana abierta", StringComparison.Ordinal));
            }
            else
            {
                var projected = Assert.Single(preview.Balance!.Products);
                var target = area == ProductionDailyArea.Sewing ? projected.Sewing : projected.ReadyToPack;
                Assert.Equal(-100, target.SignedPending);
                Assert.Equal(700, projected.Cutting.SignedPending);
            }
        }
        Assert.Empty(await db.ProductionDailyCaptures.ToArrayAsync());
        Assert.Empty(await db.InventoryMovements.ToArrayAsync());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(18, false)]
    [InlineData(1, true)]
    [InlineData(18, true)]
    public async Task Eligible_capture_confirms_without_transferring_cutting_opening_to_sewing(short unitId, bool cuttingOnly)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (_, actor, productId) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        db.ChangeTracker.Clear();
        (await db.Products.SingleAsync(x => x.Id == productId)).BaseUnitId = unitId;
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        if (cuttingOnly)
            db.ProductionRouteStages.RemoveRange(await db.ProductionRouteStages
                .Where(x => x.StageId != config.CuttingStageId).ToArrayAsync());
        var week = new ProductionScheduleWeek
        {
            CreatedByUserId = actor,
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "confirm-eligibility",
            WeekStart = new(2026, 10, 5),
            WeekEnd = new(2026, 10, 11),
            ExplicitCarryover = true,
            Status = ProductionScheduleWeekStatus.Open
        };
        db.Add(week);
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
            db.Add(new ProductionInitialBalance
            {
                WeekId = week.Id,
                ProductId = productId,
                Area = area,
                Quantity = area == ProductionDailyArea.Cutting ? 700 : 0
            });
        await db.SaveChangesAsync();
        var capture = ProductionDailyFlexibleTests.Capture(db);
        var available = await capture.GetAvailabilityAsync(week.WeekStart, ProductionDailyArea.Sewing);
        Assert.All(available, item => Assert.Equal(0, item.Available));
        var command = new ProductionBalanceEditCommand(Guid.NewGuid(), week.Id, week.WeekStart,
            [new(productId, ProductionDailyArea.Sewing, 1, 0, 100)], Pin: "4826");
        var preview = await capture.PreviewBalanceEditAsync(command);
        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Errors));
        var result = await capture.ConfirmBalanceEditAsync(command with { ReviewedFingerprint = preview.Fingerprint });
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        var row = Assert.Single((await new ProductionDailyBalanceService(db).GetDailySummaryAsync(week.Id, new(week.WeekStart)))!.Products);
        Assert.Equal(700, row.Cutting.SignedPending);
        Assert.Equal(0, row.Sewing.Opening);
        Assert.Equal(100, row.Sewing.CompletedShift1);
        Assert.Equal(-100, row.Sewing.SignedPending);
        Assert.Equal(0, row.ReadyToPack.SignedPending);
        Assert.Empty(await db.InventoryMovements.ToArrayAsync());
    }
}
