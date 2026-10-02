using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionInitialBalancePostgreSqlTests
{
    [Fact]
    public Task Opening_only_and_zero_save_on_isolated_postgresql() =>
        ProductionScheduleCarryoverPostgreSqlTests.WithDatabaseAsync(db => ProductionInitialBalanceTests.VerifySaveAsync(db));

    [Fact]
    public Task Migration_consolidates_open_roots_without_changing_closed_history_or_physical_links() =>
        ProductionScheduleCarryoverPostgreSqlTests.WithDatabaseAsync(async db =>
        {
            var migrations = db.Database.GetMigrations().ToArray();
            await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);
            var (_, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
            var source = new ProductionScheduleWeek { CreatedByUserId = actor, OperationId = Guid.NewGuid(), RequestFingerprint = "source",
                WeekStart = new(2026, 9, 28), WeekEnd = new(2026, 10, 4), ExplicitCarryover = true };
            var root = new ProductionScheduleLine { Week = source, ProductId = product, Quantity = 100, PlannedDate = source.WeekStart };
            var secondRoot = new ProductionScheduleLine { Week = source, ProductId = product, Quantity = 100, PlannedDate = source.WeekStart, Sequence = 2 };
            var target = new ProductionScheduleWeek { CreatedByUserId = actor, OperationId = Guid.NewGuid(), RequestFingerprint = "target",
                WeekStart = new(2026, 10, 5), WeekEnd = new(2026, 10, 11), ExplicitCarryover = true, Status = ProductionScheduleWeekStatus.Open };
            var closed = new ProductionScheduleWeek { CreatedByUserId = actor, OperationId = Guid.NewGuid(), RequestFingerprint = "closed",
                WeekStart = new(2026, 10, 12), WeekEnd = new(2026, 10, 18), ExplicitCarryover = true, Status = ProductionScheduleWeekStatus.Closed };
            db.AddRange(source, root, secondRoot, target, closed);
            db.ProductionWeekOpenings.AddRange(new ProductionWeekOpening { WeekId = target.Id, ProductId = product, SourceWeekId = source.Id, SourceLineId = root.Id, Area = ProductionDailyArea.Sewing, Quantity = 10 },
                new() { WeekId = target.Id, ProductId = product, SourceWeekId = source.Id, SourceLineId = secondRoot.Id, Area = ProductionDailyArea.Sewing, Quantity = 20 },
                new() { WeekId = closed.Id, ProductId = product, SourceWeekId = source.Id, SourceLineId = root.Id, Area = ProductionDailyArea.Sewing, Quantity = 40 });
            await db.SaveChangesAsync();
            await db.GetService<IMigrator>().MigrateAsync();
            var total = await db.ProductionInitialBalances.SingleAsync();
            Assert.Equal(target.Id, total.WeekId); Assert.Equal(30, total.Quantity); Assert.Equal(1u, total.Version);
            Assert.Equal(3, await db.ProductionWeekOpenings.CountAsync());
            Assert.Equal(70, await db.ProductionWeekOpenings.SumAsync(x => x.Quantity));
        });

    [Fact]
    public Task Failed_initial_write_rolls_back_new_program_orders_and_audit() =>
        ProductionScheduleCarryoverPostgreSqlTests.WithDatabaseAsync(async db =>
        {
            var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
            var id = (await service.CreateWeekAsync(new(Guid.NewGuid(), new(2026, 10, 5), actor))).Id!.Value;
            var week = (await service.GetWeekAsync(id))!;
            Assert.True((await service.PublishAsync(new(Guid.NewGuid(), id, week.Version, "4826", actor))).Success);
            week = (await service.GetWeekAsync(id))!;
            var revisions = await db.ProductionScheduleRevisions.CountAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE production_initial_balances ADD CONSTRAINT ck_test_write_failure CHECK (quantity <> 123)");
            var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), id, week.Version,
                [new("add", null, null, new(week.WeekStart, product, 50, null, null, null, null))], actor,
                Reason: "Ajustar arrastre", InitialBalances: [new(product, ProductionDailyArea.Cutting, 123, 0)]);
            var review = await service.PreviewWorkspaceChangesAsync(command);
            Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
            Assert.False((await service.SaveWorkspaceChangesAsync(command with { ReviewedFingerprint = review.Fingerprint }, "4826")).Success);
            Assert.Empty(await db.ProductionInitialBalances.ToListAsync());
            Assert.Empty(await db.ProductionScheduleLines.ToListAsync());
            Assert.Empty(await db.ProductionWorkOrders.ToListAsync());
            Assert.Equal(revisions, await db.ProductionScheduleRevisions.CountAsync());
        });

    [Fact]
    public Task Concurrent_totals_preserve_a_single_record_and_report_a_conflict() =>
        ProductionScheduleCarryoverPostgreSqlTests.WithDatabaseAsync(async db =>
        {
            var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
            var id = (await service.CreateWeekAsync(new(Guid.NewGuid(), new(2026, 10, 5), actor))).Id!.Value;
            var week = (await service.GetWeekAsync(id))!;
            var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(db.Database.GetConnectionString()).Options;
            await using var first = new WarehouseDbContext(options); await using var second = new WarehouseDbContext(options);
            SaveProductionScheduleDraftCommand Command(decimal quantity) => new(Guid.NewGuid(), id, week.Version, [], actor,
                InitialBalances: [new(product, ProductionDailyArea.Cutting, quantity, 0)]);
            var results = await Task.WhenAll(ProductionScheduleCarryoverCopyTests.Schedule(first).SaveWorkspaceChangesAsync(Command(20)),
                ProductionScheduleCarryoverCopyTests.Schedule(second).SaveWorkspaceChangesAsync(Command(30)));
            Assert.Single(results, x => x.Success); Assert.Single(results, x => !x.Success);
            Assert.Equal(1, await db.ProductionInitialBalances.CountAsync());
        });
}
