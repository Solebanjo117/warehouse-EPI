using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionSchedulePreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_is_read_only_and_confirmation_opens_the_complete_week_idempotently(bool empty)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var monday = new DateOnly(2026, 12, 28);
        var command = new CreatePreparedProductionScheduleWeekCommand(Guid.NewGuid(), monday,
            empty ? [] : [new(monday, product, 10, "ORDER", null, null, "Monday"), new(monday.AddDays(6), product, 20, null, null, null, "Sunday")], actor);
        var review = await service.PreviewNewWeekAsync(command);
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        Assert.Empty(await db.ProductionScheduleWeeks.ToListAsync());
        Assert.Empty(await db.ProductionScheduleRevisions.ToListAsync());
        command = command with { ReviewedFingerprint = review.Fingerprint };
        Assert.Equal(ProductionDailyCommandStatus.InvalidPin, (await service.CreatePreparedWeekAsync(command, "0000")).Status);
        Assert.Empty(await db.ProductionScheduleWeeks.ToListAsync());
        var result = await service.CreatePreparedWeekAsync(command, "4826");
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        var week = (await service.GetWeekAsync(result.Id!.Value))!;
        Assert.Equal(ProductionScheduleWeekStatus.Open, week.Status);
        Assert.Equal(command.Lines.Count, week.Lines.Count);
        Assert.Equal(command.Lines.Count, await db.ProductionWorkOrders.CountAsync());
        Assert.Equal(command.Lines.Count, await db.ProductionBatches.CountAsync());
        Assert.Equal(result.Id, (await service.CreatePreparedWeekAsync(command, "")).Id);
        Assert.Equal(ProductionDailyCommandStatus.IdempotencyConflict,
            (await service.CreatePreparedWeekAsync(command with { WeekStart = monday.AddDays(7) }, "4826")).Status);
        Assert.Single(await db.ProductionScheduleWeeks.ToListAsync());
    }

    [Fact]
    public async Task Large_preparation_reviews_and_opens_all_lines_idempotently()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var monday = new DateOnly(2026, 12, 28);
        var lines = Enumerable.Range(1, 150).Select(i => new ProductionScheduleBatchLine(
            monday.AddDays(i % 7), product, i, $"ORDER-{i}", null, null, null)).ToArray();
        var command = new CreatePreparedProductionScheduleWeekCommand(Guid.NewGuid(), monday, lines, actor);
        var review = await service.PreviewNewWeekAsync(command);
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        command = command with { ReviewedFingerprint = review.Fingerprint };
        var result = await service.CreatePreparedWeekAsync(command, "4826");
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        Assert.Equal(150, (await service.GetWeekAsync(result.Id!.Value))!.Lines.Count);
        Assert.Equal(result.Id, (await service.CreatePreparedWeekAsync(command, "")).Id);
        Assert.Equal(150, await db.ProductionScheduleLines.CountAsync());
    }

    [Fact]
    public async Task Moving_preparation_before_its_carryover_source_is_rejected_without_creating_a_week()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var sourceStart = new DateOnly(2026, 9, 28);
        var source = (await service.CreateOpenWeekAsync(new(Guid.NewGuid(), sourceStart, actor), "4826")).Id!.Value;
        var sourceWeek = (await service.GetWeekAsync(source))!;
        Assert.True((await service.SaveLineAsync(new(Guid.NewGuid(), source, null, sourceWeek.Version, null,
            sourceStart, product, 30, null, null, null, null, actor, "4826"))).Success);
        var destination = sourceStart.AddDays(7);
        var option = (await new ProductionWeekOpeningService(db).OptionsAsync(destination)).First();
        var command = new CreatePreparedProductionScheduleWeekCommand(Guid.NewGuid(), destination, [], actor,
            [new(option.SourceWeekId, option.SourceLineId, option.Area, 10, option.Fingerprint)]);
        Assert.True((await service.PreviewNewWeekAsync(command)).CanConfirm);
        Assert.False((await service.PreviewNewWeekAsync(command with { WeekStart = sourceStart.AddDays(-7) })).CanConfirm);
        Assert.Single(await db.ProductionScheduleWeeks.ToListAsync());
        Assert.Empty(await db.ProductionWeekOpenings.ToListAsync());
    }

    [Fact]
    public async Task Invalid_dates_configuration_products_and_stale_review_never_create_a_week()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var monday = new DateOnly(2026, 10, 5);
        var command = new CreatePreparedProductionScheduleWeekCommand(Guid.NewGuid(), monday,
            [new(monday, product, 12, null, null, null, null)], actor);
        Assert.False((await service.PreviewNewWeekAsync(command with { WeekStart = monday.AddDays(1) })).CanConfirm);
        Assert.False((await service.PreviewNewWeekAsync(command with { Lines = [command.Lines[0] with { Quantity = 0 }] })).CanConfirm);
        var review = await service.PreviewNewWeekAsync(command);
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        config.Version++; await db.SaveChangesAsync();
        Assert.Equal(ProductionDailyCommandStatus.ConcurrencyConflict,
            (await service.CreatePreparedWeekAsync(command with { ReviewedFingerprint = review.Fingerprint }, "4826")).Status);
        config = await db.ProductionDailyConfigurations.SingleAsync();
        config.Shift2Id = null; await db.SaveChangesAsync();
        Assert.False((await service.PreviewNewWeekAsync(command)).CanConfirm);
        Assert.Empty(await db.ProductionScheduleWeeks.ToListAsync());
    }
}

public sealed class ProductionSchedulePreparationPostgreSqlTests
{
    [Fact]
    public Task Publication_failure_rolls_back_week_lines_openings_orders_batches_and_revisions() =>
        ProductionScheduleCarryoverPostgreSqlTests.WithDatabaseAsync(async db =>
        {
            var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
            var monday = new DateOnly(2026, 10, 5);
            var source = (await service.CreateOpenWeekAsync(new(Guid.NewGuid(), monday.AddDays(-7), actor), "4826")).Id!.Value;
            var sourceWeek = (await service.GetWeekAsync(source))!;
            Assert.True((await service.SaveLineAsync(new(Guid.NewGuid(), source, null, sourceWeek.Version, null,
                sourceWeek.WeekStart, product, 30, null, null, null, null, actor, "4826"))).Success);
            var option = (await new ProductionWeekOpeningService(db).OptionsAsync(monday)).First();
            var command = new CreatePreparedProductionScheduleWeekCommand(Guid.NewGuid(), monday,
                [new(monday, product, 10, null, null, null, null)], actor,
                [new(option.SourceWeekId, option.SourceLineId, option.Area, 10, option.Fingerprint)]);
            var review = await service.PreviewNewWeekAsync(command);
            Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
            command = command with { ReviewedFingerprint = review.Fingerprint };
            var revisions = await db.ProductionScheduleRevisions.CountAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_prepared_batch() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Injected publication failure' USING ERRCODE = '23514'; END; $$;
                CREATE TRIGGER reject_prepared_batch BEFORE INSERT ON production_batches
                FOR EACH ROW EXECUTE FUNCTION reject_prepared_batch();
                """);
            Assert.False((await service.CreatePreparedWeekAsync(command, "4826")).Success);
            Assert.Single(await db.ProductionScheduleWeeks.ToListAsync());
            Assert.Single(await db.ProductionScheduleLines.ToListAsync());
            Assert.Empty(await db.ProductionWeekOpenings.ToListAsync());
            Assert.Single(await db.ProductionWorkOrders.ToListAsync());
            Assert.Single(await db.ProductionBatches.ToListAsync());
            Assert.Equal(revisions, await db.ProductionScheduleRevisions.CountAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_prepared_batch ON production_batches; DROP FUNCTION reject_prepared_batch();");
            Assert.True((await service.CreatePreparedWeekAsync(command, "4826")).Success);
            Assert.Equal(2, await db.ProductionScheduleWeeks.CountAsync());
            Assert.Single(await db.ProductionWeekOpenings.ToListAsync());
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Concurrent_creations_of_one_monday_commit_only_one_preparation(bool sameOperation) =>
        ProductionScheduleCarryoverPostgreSqlTests.WithDatabaseAsync(async db =>
        {
            var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
            var monday = new DateOnly(2026, 10, 5);
            var first = new CreatePreparedProductionScheduleWeekCommand(Guid.NewGuid(), monday,
                [new(monday, product, 10, null, null, null, null)], actor);
            var second = sameOperation ? first : first with { OperationId = Guid.NewGuid() };
            first = first with { ReviewedFingerprint = (await service.PreviewNewWeekAsync(first)).Fingerprint };
            second = second with { ReviewedFingerprint = (await service.PreviewNewWeekAsync(second)).Fingerprint };
            var options = new DbContextOptionsBuilder<WarehouseEPI.Infrastructure.Persistence.WarehouseDbContext>().UseNpgsql(db.Database.GetConnectionString()).Options;
            await using var one = new WarehouseEPI.Infrastructure.Persistence.WarehouseDbContext(options);
            await using var two = new WarehouseEPI.Infrastructure.Persistence.WarehouseDbContext(options);
            var results = await Task.WhenAll(ProductionScheduleCarryoverCopyTests.Schedule(one).CreatePreparedWeekAsync(first, "4826"),
                ProductionScheduleCarryoverCopyTests.Schedule(two).CreatePreparedWeekAsync(second, "4826"));
            if (sameOperation)
            {
                Assert.All(results, result => Assert.True(result.Success));
                Assert.Equal(results[0].Id, results[1].Id);
            }
            else Assert.Single(results, x => x.Success);
            Assert.Single(await db.ProductionScheduleWeeks.ToListAsync());
            Assert.Single(await db.ProductionScheduleLines.ToListAsync());
            Assert.Single(await db.ProductionWorkOrders.ToListAsync());
            Assert.Single(await db.ProductionBatches.ToListAsync());
        });
}
