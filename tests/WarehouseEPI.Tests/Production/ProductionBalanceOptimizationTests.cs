using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionBalanceOptimizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Targeted_reads_match_full_population_with_history_reversals_and_other_shifts(bool explicitWeek)
    {
        await using var db = ProductionOpeningImportTests.Context();
        await VerifyTargetedReadsAsync(db, explicitWeek);
    }

    internal static async Task VerifyTargetedReadsAsync(WarehouseDbContext db, bool explicitWeek)
    {
        await db.Database.EnsureCreatedAsync();
        var seed = await ProductionDailyFlexibleTests.SeedAsync(db);
        seed.Week.ExplicitCarryover = explicitWeek;
        var other = new Product { Sku = "UNRELATED", BaseUnitId = seed.Product.BaseUnitId };
        db.Products.Add(other);
        var historicalShift = new ProductionShift { Code = "OLD", Name = "Historical shift", IsActive = false };
        db.ProductionShifts.Add(historicalShift);
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        for (var index = 0; index < 503; index++)
            db.ProductionDailyCaptures.Add(new() { OperationId = Guid.NewGuid(), RequestFingerprint = "fixture",
                WeekId = seed.Week.Id, EffectiveDate = seed.Date, ProductId = index == 502 ? other.Id : seed.Product.Id,
                Area = ProductionDailyArea.Cutting, StageId = config.CuttingStageId!.Value,
                ShiftId = index == 500 ? historicalShift.Id : seed.Shift, Quantity = 1.0001m,
                Status = index == 501 ? ProductionDailyCaptureStatus.Reversed : ProductionDailyCaptureStatus.Active,
                ResponsibleUserId = seed.User.Id });
        await db.SaveChangesAsync();
        var service = new ProductionDailyBalanceService(db);
        foreach (var physical in new[] { false, true })
        {
            var full = physical ? await service.GetPhysicalAsync(seed.Week.Id, availability: true)
                : await service.GetAsync(seed.Week.Id);
            var scoped = physical ? await service.GetPhysicalAsync(seed.Week.Id, availability: true, selectedProducts: [seed.Product.Id])
                : await service.GetAsync(seed.Week.Id, false, false, default, selectedProducts: [seed.Product.Id]);
            Assert.Equal(JsonSerializer.Serialize(full!.Rows.Where(x => x.ProductId == seed.Product.Id)), JsonSerializer.Serialize(scoped!.Rows));
            Assert.DoesNotContain(scoped.Rows, x => x.ProductId == other.Id);
        }
        var capture = ProductionDailyFlexibleTests.Capture(db);
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
        foreach (var priorOnly in new[] { false, true })
        {
            var full = await capture.GetAvailabilityAsync(seed.Date, area, priorOnly);
            var scoped = await capture.GetAvailabilityAsync(seed.Date, area, priorOnly, [seed.Product.Id], default);
            Assert.Equal(full.Where(x => x.ProductId == seed.Product.Id), scoped);
            Assert.Empty(await capture.GetAvailabilityAsync(seed.Date, area, priorOnly, [], default));
        }
        var day = (await service.GetDailySummaryAsync(seed.Week.Id, new(seed.Date)))!.Products.Single(x => x.ProductId == seed.Product.Id);
        Assert.Equal(501.0501m, day.Cutting.Completed);
        Assert.Equal(-401.0501m, day.Cutting.SignedPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lightweight_review_preserves_fingerprint_and_confirmation_revalidates_after_each_write(bool explicitWeek)
    {
        await using var db = ProductionOpeningImportTests.Context();
        await db.Database.EnsureCreatedAsync();
        var seed = await ProductionDailyFlexibleTests.SeedAsync(db);
        seed.Week.ExplicitCarryover = explicitWeek;
        await db.SaveChangesAsync();
        var capture = ProductionDailyFlexibleTests.Capture(db);
        var command = new ProductionBalanceEditCommand(Guid.NewGuid(), seed.Week.Id, seed.Date,
            [new(seed.Product.Id, ProductionDailyArea.Cutting, 1, 0, 40),
             new(seed.Product.Id, ProductionDailyArea.Cutting, 2, 0, 20)], Pin: "4826");
        var full = await capture.PreviewBalanceEditAsync(command);
        var lean = await capture.PreviewBalanceEditAsync(command, false, default);
        Assert.True(full.CanConfirm, string.Join(" | ", full.Errors));
        Assert.NotNull(full.WeekClose); Assert.Null(lean.WeekClose);
        Assert.Equal(JsonSerializer.Serialize(full with { WeekClose = null }), JsonSerializer.Serialize(lean));
        Assert.Empty(await db.ProductionDailyCaptures.ToArrayAsync());
        var phases = new List<string>();
        var measuring = new AsyncLocal<bool>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "WarehouseEPI.Production.Balance",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { if (measuring.Value) phases.Add(activity.OperationName); }
        };
        ActivitySource.AddActivityListener(listener);
        measuring.Value = true;
        var saved = await capture.ConfirmBalanceEditAsync(command with { ReviewedFingerprint = full.Fingerprint });
        measuring.Value = false;
        Assert.True(saved.Success, string.Join(" | ", saved.Errors ?? []));
        Assert.DoesNotContain("balance.weekly-close", phases);
        Assert.Equal(4, phases.Count(x => x == "capture.validation")); // Two review checks and two fresh write checks.
        Assert.Equal(3, phases.Count(x => x == "capture.metadata")); // One review context, one per mutation.
        var day = Assert.Single((await new ProductionDailyBalanceService(db).GetDailySummaryAsync(seed.Week.Id, new(seed.Date)))!.Products);
        Assert.Equal(60, day.Cutting.Completed); Assert.Equal(60, day.Cutting.PendingAfterShift1); Assert.Equal(40, day.Cutting.SignedPending);
        Assert.Equal(saved.Id, (await capture.ConfirmBalanceEditAsync(command with { ReviewedFingerprint = full.Fingerprint })).Id);
        Assert.Equal(2, await db.ProductionDailyCaptures.CountAsync());
    }
}
