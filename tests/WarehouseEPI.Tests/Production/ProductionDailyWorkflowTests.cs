using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionDailyWorkflowTests
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Workflow_preserves_carryover_and_confirms_group_idempotently()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await VerifyWorkflowAsync(db);
    }

    [Fact]
    public async Task PostgreSql_migration_workflow_and_late_failure_are_atomic()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? configuration.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var database = "warehouse_epi_workflow_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_workflow_test_[a-f0-9]{32}$", database);
        var adminString = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false }.ConnectionString;
        var testString = new NpgsqlConnectionStringBuilder(source) { Database = database, Pooling = false }.ConnectionString;
        await using var admin = new NpgsqlConnection(adminString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await using (var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testString).Options))
            {
                await db.Database.MigrateAsync();
                await VerifyWorkflowAsync(db);
                await ProductionDailyFlexibleTests.VerifyAsync(db);
                var actor = await db.Users.SingleAsync(x => x.FullName == "Workflow admin");
                foreach (var sku in new[] { "WF-PG-A", "WF-PG-B" })
                {
                    var product = new Product { Sku = sku, BaseUnitId = 1 };
                    db.Products.Add(product); await db.SaveChangesAsync();
                    var week = await db.ProductionScheduleWeeks.OrderByDescending(x => x.WeekStart).FirstAsync();
                    Assert.True((await Schedule(db).SaveLineAsync(new(Guid.NewGuid(), week.Id, null, week.Version,
                        null, week.WeekStart, product.Id, 10, null, null, null, null, actor.Id, "4826"))).Success);
                }
            }
            var failure = new FailSecondCapture();
            await using (var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testString).AddInterceptors(failure).Options))
            {
                var week = await db.ProductionScheduleWeeks.OrderByDescending(x => x.WeekStart).FirstAsync();
                var products = await db.Products.Where(x => x.Sku.StartsWith("WF-PG-")).OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
                var shift = (await db.ProductionDailyConfigurations.SingleAsync()).Shift1Id!.Value;
                var capture = Capture(db);
                var command = new ProductionCaptureGroupCommand(Guid.NewGuid(), week.WeekStart, ProductionDailyArea.Cutting,
                    shift, products.Select(x => new ProductionCaptureRow(x, 15, null)).ToArray(), Pin: "4826");
                var preview = await capture.PreviewGroupAsync(command);
                Assert.True(preview.CanConfirm, string.Join(" | ", preview.Errors));
                var before = await db.ProductionDailyCaptures.CountAsync();
                var eventsBefore = await db.ProductionEvents.CountAsync();
                var resultsBefore = await db.ProductionBatchResults.CountAsync();
                var ordersBefore = await db.ProductionWorkOrders.CountAsync();
                var batchesBefore = await db.ProductionBatches.CountAsync();
                failure.Enabled = true;
                var result = await capture.ConfirmGroupAsync(command with { ReviewedFingerprint = preview.Fingerprint });
                Assert.False(result.Success);
                Assert.True(failure.Injected);
                db.ChangeTracker.Clear();
                Assert.Equal(before, await db.ProductionDailyCaptures.CountAsync());
                Assert.Equal(eventsBefore, await db.ProductionEvents.CountAsync());
                Assert.Equal(resultsBefore, await db.ProductionBatchResults.CountAsync());
                Assert.Equal(ordersBefore, await db.ProductionWorkOrders.CountAsync());
                Assert.Equal(batchesBefore, await db.ProductionBatches.CountAsync());
                Assert.False(await db.ProductionCaptureSubmissions.AnyAsync(x => x.OperationId == command.OperationId));
            }
            var editFailure = new FailSecondCapture();
            await using (var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testString).AddInterceptors(editFailure).Options))
            {
                var week = await db.ProductionScheduleWeeks.OrderByDescending(x => x.WeekStart).FirstAsync();
                var products = await db.Products.Where(x => x.Sku.StartsWith("WF-PG-")).OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
                var command = new ProductionBalanceEditCommand(Guid.NewGuid(), week.Id, week.WeekStart,
                    products.Select(x => new ProductionBalanceCell(x, ProductionDailyArea.Cutting, 1, 0, 15)).ToArray(), Pin: "4826");
                var preview = await Capture(db).PreviewBalanceEditAsync(command);
                Assert.True(preview.CanConfirm, string.Join(" | ", preview.Errors));
                var before = await db.ProductionDailyCaptures.CountAsync();
                var eventsBefore = await db.ProductionEvents.CountAsync();
                var ordersBefore = await db.ProductionWorkOrders.CountAsync();
                editFailure.Enabled = true;
                var result = await Capture(db).ConfirmBalanceEditAsync(command with { ReviewedFingerprint = preview.Fingerprint });
                Assert.False(result.Success);
                Assert.True(editFailure.Injected);
                db.ChangeTracker.Clear();
                Assert.Equal(before, await db.ProductionDailyCaptures.CountAsync());
                Assert.Equal(eventsBefore, await db.ProductionEvents.CountAsync());
                Assert.Equal(ordersBefore, await db.ProductionWorkOrders.CountAsync());
                Assert.False(await db.Set<ProductionBalanceEdit>().AnyAsync(x => x.OperationId == command.OperationId));
            }
            await using (var first = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testString).Options))
            await using (var second = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testString).Options))
            {
                var week = await first.ProductionScheduleWeeks.OrderByDescending(x => x.WeekStart).FirstAsync();
                var product = await first.Products.SingleAsync(x => x.Sku == "WF-PG-A");
                var shift = (await first.ProductionDailyConfigurations.SingleAsync()).Shift1Id!.Value;
                var command = new ProductionCaptureGroupCommand(Guid.NewGuid(), week.WeekStart, ProductionDailyArea.Cutting,
                    shift, [new(product.Id, 1, null)], Pin: "4826");
                var preview = await Capture(first).PreviewGroupAsync(command);
                Assert.True(preview.CanConfirm, string.Join(" | ", preview.Errors));
                command = command with { ReviewedFingerprint = preview.Fingerprint };
                var before = await first.ProductionDailyCaptures.CountAsync();
                var results = await Task.WhenAll(Capture(first).ConfirmGroupAsync(command),
                    Capture(second).ConfirmGroupAsync(command with { OperationId = Guid.NewGuid() }));
                Assert.True(results.Count(x => x.Success) == 1,
                    string.Join(" | ", results.Select(x => x.Status + ": " + string.Join(", ", x.Errors ?? []))));
                first.ChangeTracker.Clear(); second.ChangeTracker.Clear();
                Assert.Equal(before + 1, await first.ProductionDailyCaptures.CountAsync());
                var repeated = command with { OperationId = Guid.NewGuid() };
                repeated = repeated with { ReviewedFingerprint = (await Capture(first).PreviewGroupAsync(repeated)).Fingerprint };
                var repeats = await Task.WhenAll(Capture(first).ConfirmGroupAsync(repeated), Capture(second).ConfirmGroupAsync(repeated));
                Assert.Contains(repeats, x => x.Success);
                first.ChangeTracker.Clear(); second.ChangeTracker.Clear();
                Assert.True((await Capture(second).ConfirmGroupAsync(repeated)).Success);
                Assert.Equal(before + 2, await first.ProductionDailyCaptures.CountAsync());
                Assert.Single(await first.ProductionCaptureSubmissions.Where(x => x.OperationId == repeated.OperationId).ToListAsync());
                var current = await first.ProductionDailyCaptures.Where(x => x.ProductId == product.Id && x.EffectiveDate == week.WeekStart &&
                    x.Area == ProductionDailyArea.Cutting && x.ShiftId == shift && x.Status == ProductionDailyCaptureStatus.Active).SumAsync(x => x.Quantity);
                var edit = new ProductionBalanceEditCommand(Guid.NewGuid(), week.Id, week.WeekStart,
                    [new(product.Id, ProductionDailyArea.Cutting, 1, current, current + 1)], Pin: "4826");
                edit = edit with { ReviewedFingerprint = (await Capture(first).PreviewBalanceEditAsync(edit)).Fingerprint };
                var competing = edit with { OperationId = Guid.NewGuid() };
                competing = competing with { ReviewedFingerprint = (await Capture(second).PreviewBalanceEditAsync(competing)).Fingerprint };
                var editResults = await Task.WhenAll(Capture(first).ConfirmBalanceEditAsync(edit), Capture(second).ConfirmBalanceEditAsync(competing));
                Assert.Single(editResults, x => x.Success);
                first.ChangeTracker.Clear(); second.ChangeTracker.Clear();
                var winner = editResults[0].Success ? edit : competing;
                Assert.True((await Capture(first).ConfirmBalanceEditAsync(winner)).Success);
                Assert.Single(await first.Set<ProductionBalanceEdit>().Where(x => x.OperationId == winner.OperationId).ToListAsync());
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyWorkflowAsync(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        var pins = new UserPinService(db, new PinProtector(Key));
        var actor = new User { FullName = "Workflow admin", RoleId = 1, PinHash = "", PinLookup = "" };
        await pins.AssignAsync(actor, "4826"); db.Add(actor);
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var a = new Product { Sku = "WF-A", BaseUnitId = 1 };
        var b = new Product { Sku = "WF-B", BaseUnitId = 1 };
        db.AddRange(a, b); await db.SaveChangesAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monday = today.AddDays(-21 - ((int)today.DayOfWeek + 6) % 7);
        var schedule = Schedule(db);
        var created = await schedule.CreateWeekAsync(new(Guid.NewGuid(), monday, actor.Id));
        var sourceId = created.Id!.Value;
        (await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == sourceId)).ExplicitCarryover = false;
        await db.SaveChangesAsync(); // Regression of the legacy intention-based workflow.
        foreach (var product in new[] { a, a, b })
        {
            var current = (await schedule.GetWeekAsync(sourceId))!;
            var saved = await schedule.SaveLineAsync(new(Guid.NewGuid(), sourceId, null, current.Version, null,
                monday, product.Id, product.Id == a.Id ? 10 : 20, "OLD-REF", null, null, "old-note", actor.Id));
            Assert.True(saved.Success, string.Join(" | ", saved.Errors ?? []));
        }
        var source = (await schedule.GetWeekAsync(sourceId))!;
        Assert.True((await schedule.PublishAsync(new(Guid.NewGuid(), sourceId, source.Version, "4826", actor.Id))).Success);
        var nextId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), monday.AddDays(7), actor.Id))).Id!.Value;
        (await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == nextId)).ExplicitCarryover = false;
        await db.SaveChangesAsync();
        var next = (await schedule.GetWeekAsync(nextId))!;
        var suggestions = await schedule.GetCarryoverSuggestionsAsync(nextId);
        Assert.Equal(20, suggestions.Single(x => x.ProductId == a.Id && x.Area == ProductionDailyArea.Cutting).Available);
        var orderCount = await db.ProductionWorkOrders.CountAsync();
        var planCommand = new SaveProductionCarryoverPlanCommand(Guid.NewGuid(), nextId, next.Version, null, 0,
            next.WeekStart, a.Id, ProductionDailyArea.Cutting, 15, 20, actor.Id);
        var planned = await schedule.SaveCarryoverPlanAsync(planCommand);
        Assert.True(planned.Success, string.Join(" | ", planned.Errors ?? []));
        Assert.True((await schedule.SaveCarryoverPlanAsync(planCommand)).Success);
        Assert.Equal(15, (await db.ProductionCarryoverPlans.SingleAsync()).Quantity);
        Assert.Equal(orderCount, await db.ProductionWorkOrders.CountAsync());
        Assert.Equal(20, (await schedule.GetCarryoverSuggestionsAsync(nextId)).Single(x => x.ProductId == a.Id && x.Area == ProductionDailyArea.Cutting).Available);
        next = (await schedule.GetWeekAsync(nextId))!;
        Assert.True((await schedule.PublishAsync(new(Guid.NewGuid(), nextId, next.Version, "4826", actor.Id))).Success);
        next = (await schedule.GetWeekAsync(nextId))!;
        var add = new SaveProductionScheduleLineCommand(Guid.NewGuid(), nextId, null, next.Version, null,
            next.WeekStart.AddDays(1), b.Id, 5, null, null, null, null, actor.Id);
        Assert.Equal(ProductionDailyCommandStatus.InvalidPin, (await schedule.SaveLineAsync(add)).Status);
        var added = await schedule.SaveLineAsync(add with { AdminPin = "4826" });
        Assert.True(added.Success, string.Join(" | ", added.Errors ?? []));
        var newLine = await db.ProductionScheduleLines.AsNoTracking().SingleAsync(x => x.Id == added.Id);
        Assert.NotNull(newLine.WorkOrderId);
        Assert.Single(await db.ProductionBatches.Where(x => x.WorkOrderId == newLine.WorkOrderId).ToListAsync());
        // Copy two separate lines for the same product; neither references nor notes are copied.
        source = (await schedule.GetWeekAsync(sourceId))!; next = (await schedule.GetWeekAsync(nextId))!;
        var copy = new CopyProductionWeekCommand(Guid.NewGuid(), nextId, next.Version, source.Id, source.Version,
            source.Lines.Where(x => x.ProductId == a.Id).Select(x => new ProductionCopyRow(x.Id, next.WeekStart, 2)).ToArray(), actor.Id, "4826");
        var copied = await schedule.CopyWeekAsync(copy);
        Assert.True(copied.Success, string.Join(" | ", copied.Errors ?? []));
        Assert.True((await schedule.CopyWeekAsync(copy)).Success);
        Assert.Equal(ProductionDailyCommandStatus.IdempotencyConflict,
            (await schedule.CopyWeekAsync(copy with { Rows = [copy.Rows[0] with { Quantity = 3 }] })).Status);
        var inactive = new Product { Sku = "WF-INACTIVE", BaseUnitId = 1, IsActive = false };
        db.Products.Add(inactive);
        await db.SaveChangesAsync();
        var sourceLine = await db.ProductionScheduleLines.FirstAsync(x => x.WeekId == sourceId && x.ProductId == b.Id);
        var originalProduct = sourceLine.ProductId;
        sourceLine.ProductId = inactive.Id;
        await db.SaveChangesAsync();
        var invalidCopy = copy with
        {
            OperationId = Guid.NewGuid(),
            ExpectedVersion = (await schedule.GetWeekAsync(nextId))!.Version,
            Rows = [new(sourceLine.Id, next.WeekStart, 1)]
        };
        Assert.False((await schedule.CopyWeekAsync(invalidCopy)).Success);
        sourceLine.ProductId = originalProduct;
        await db.SaveChangesAsync();
        var copiedLines = (await schedule.GetWeekAsync(nextId))!.Lines.Where(x => x.ProductId == a.Id).ToArray();
        Assert.Equal(2, copiedLines.Length);
        Assert.All(copiedLines, x => { Assert.Equal(2, x.Quantity); Assert.Null(x.OrderReference1); Assert.Null(x.Notes); Assert.NotNull(x.WorkOrderId); });
        var capture = Capture(db);
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var command = new ProductionCaptureGroupCommand(Guid.NewGuid(), next.WeekStart, ProductionDailyArea.Cutting, config.Shift1Id!.Value,
            [new(a.Id, 3, null), new(b.Id, 4, "ok")], Pin: "4826");
        var bad = command with { Rows = [new(a.Id, 3, null), new(b.Id, -1, null)] };
        Assert.False((await capture.PreviewGroupAsync(bad)).CanConfirm);
        Assert.False((await capture.ConfirmGroupAsync(bad)).Success);
        Assert.Empty(await db.ProductionDailyCaptures.ToListAsync());
        var review = await capture.PreviewGroupAsync(command);
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        Assert.True((await capture.PreviewGroupAsync(command with { Rows = [.. command.Rows, new(Guid.NewGuid(), 0, null)] })).CanConfirm);
        Assert.False((await capture.PreviewGroupAsync(command with { Area = (ProductionDailyArea)999 })).CanConfirm);
        Assert.Equal(ProductionDailyCommandStatus.InvalidPin, (await capture.ConfirmGroupAsync(command with { Pin = "9999", ReviewedFingerprint = review.Fingerprint })).Status);
        var stale = await capture.ConfirmGroupAsync(command with { ReviewedFingerprint = "old" });
        Assert.Equal(ProductionDailyCommandStatus.ConcurrencyConflict, stale.Status);
        command = command with { ReviewedFingerprint = review.Fingerprint };
        var confirmed = await capture.ConfirmGroupAsync(command);
        Assert.True(confirmed.Success, string.Join(" | ", confirmed.Errors ?? []));
        Assert.True((await capture.ConfirmGroupAsync(command)).Success);
        Assert.Equal(ProductionDailyCommandStatus.IdempotencyConflict, (await capture.ConfirmGroupAsync(command with { Rows = [new(a.Id, 1, null)] })).Status);
        Assert.Equal(2, await db.ProductionDailyCaptures.CountAsync());
        Assert.Equal(2, await db.Set<ProductionCaptureSubmissionItem>().CountAsync());
        var first = await db.ProductionDailyCaptures.SingleAsync(x => x.ProductId == a.Id);
        Assert.True((await capture.ReverseAsync(new(Guid.NewGuid(), first.Id, "Correction", "4826"))).Success);
        Assert.Equal(1, await db.ProductionDailyCaptures.CountAsync(x => x.Status == ProductionDailyCaptureStatus.Active));
        Assert.Equal(15, (await db.ProductionCarryoverPlans.SingleAsync()).Quantity);
        var savedPlan = await db.ProductionCarryoverPlans.SingleAsync();
        var changedAvailability = await schedule.SaveCarryoverPlanAsync(planCommand with
        {
            OperationId = Guid.NewGuid(),
            Id = savedPlan.Id,
            Version = savedPlan.Version,
            WeekVersion = (await schedule.GetWeekAsync(nextId))!.Version,
            ExpectedAvailable = 999,
            Quantity = 14
        });
        Assert.False(changedAvailability.Success);
        Assert.Equal(15, (await db.ProductionCarryoverPlans.SingleAsync()).Quantity);
    }

    private static ProductionDailyScheduleService Schedule(WarehouseDbContext db)
    {
        var pins = new UserPinService(db, new PinProtector(Key));
        return new(db, pins, new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
    }
    private static ProductionDailyCaptureService Capture(WarehouseDbContext db)
    {
        var pins = new UserPinService(db, new PinProtector(Key));
        return new(db, pins, new InventoryMovementService(db, pins, TimeProvider.System), new WarehouseClock(new WarehouseSettingsService(db)), TimeProvider.System);
    }
    private sealed class FailSecondCapture : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Injected { get; private set; }
        private int count;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<ProductionDailyCapture>().Any(x => x.State == EntityState.Added) && ++count == 2)
            {
                Injected = true;
                throw new DbUpdateException("Injected failure after the first capture.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
