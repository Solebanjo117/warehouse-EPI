using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionWeeklySummaryTests
{
    [Fact]
    public async Task Weekly_totals_and_export_include_more_than_500_captures_without_summing_daily_accumulators()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await VerifyAsync(db);
    }

    [Fact]
    public async Task Weekly_summary_and_grouped_lookup_translate_on_isolated_postgresql()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var database = "warehouse_epi_balance_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_balance_test_[a-f0-9]{32}$", database);
        var adminBuilder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        var testBuilder = new NpgsqlConnectionStringBuilder(source) { Database = database, Pooling = false };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseNpgsql(testBuilder.ConnectionString).Options);
            await VerifyAsync(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    internal static async Task VerifyAsync(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var actor = new User { FullName = "Weekly report", RoleId = 1, PinHash = "", PinLookup = Guid.NewGuid().ToString("N") };
        var product = new Product { Sku = "ZZ-WEEK", Description = "Weekly product", BaseUnitId = 1 };
        var week = new ProductionScheduleWeek
        {
            WeekStart = new(2026, 7, 6),
            WeekEnd = new(2026, 7, 11),
            OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('W', 64),
            CreatedByUser = actor
        };
        week.Lines.Add(new() { Product = product, Sequence = 1, PlannedDate = week.WeekStart, Quantity = 550 });
        week.Lines.Add(new() { Product = product, Sequence = 2, PlannedDate = week.WeekEnd, Quantity = 50 });
        week.Lines.Add(new()
        {
            Product = product,
            Sequence = 3,
            PlannedDate = week.WeekStart,
            Quantity = 3,
            IsCarryover = true,
            StartArea = ProductionDailyArea.Sewing
        });
        db.Add(week);
        await db.SaveChangesAsync();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        for (var i = 0; i < 503; i++)
            db.ProductionDailyCaptures.Add(new()
            {
                WeekId = week.Id,
                ProductId = product.Id,
                Area = ProductionDailyArea.Cutting,
                StageId = config.CuttingStageId!.Value,
                ShiftId = i % 2 == 0 ? config.Shift1Id!.Value : config.Shift2Id!.Value,
                Quantity = 1,
                EffectiveDate = i < 501 ? week.WeekStart : week.WeekEnd,
                ResponsibleUserId = actor.Id,
                OperationId = Guid.NewGuid(),
                RequestFingerprint = new string('C', 64),
                Origin = ProductionScheduleOrigin.ExcelImport
            });
        db.ProductionDailyCaptures.Add(new()
        {
            WeekId = week.Id,
            ProductId = product.Id,
            Area = ProductionDailyArea.Cutting,
            StageId = config.CuttingStageId!.Value,
            ShiftId = config.Shift1Id!.Value,
            Quantity = 999,
            Status = ProductionDailyCaptureStatus.Reversed,
            EffectiveDate = week.WeekStart,
            ResponsibleUserId = actor.Id,
            OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('R', 64),
            ReversedByUserId = actor.Id,
            ReversedAt = DateTimeOffset.UtcNow,
            ReverseReason = "Test"
        });
        db.ProductionCarryoverPlans.Add(new()
        {
            WeekId = week.Id,
            ProductId = product.Id,
            PlannedDate = week.WeekStart,
            Area = ProductionDailyArea.Sewing,
            Quantity = 2,
            UpdatedByUserId = actor.Id
        });
        await db.SaveChangesAsync();
        var service = new ProductionDailyBalanceService(db);
        var filter = new ProductionWeeklyFilter(week.WeekStart.AddDays(2), "zz-week");
        var summary = (await service.GetWeeklyAsync(week.Id, filter))!;
        var row = Assert.Single(summary.Products);
        Assert.Equal(600, row.Planned);
        Assert.Equal(501, row.Cutting.Completed);
        Assert.Equal(49, row.Cutting.Pending);
        Assert.Equal(3, row.Sewing.Opening);
        Assert.Equal(504, row.Sewing.Pending);
        Assert.Equal(501, row.Days.Sum(x => x.Shifts.Sum(s => s.Quantity)));
        Assert.Equal(251, row.Days[0].Shifts.Single(x => x.ShiftId == config.Shift1Id).Quantity);
        Assert.Equal(2, Assert.Single(row.Intentions).Quantity);
        Assert.Equal(50, row.Days[^1].Planned);
        Assert.Equal(503, Assert.Single((await service.GetWeeklyAsync(week.Id, new(week.WeekEnd)))!.Products).Cutting.Completed);
        Assert.Empty((await service.GetWeeklyAsync(week.Id, filter with { Sku = "missing" }))!.Products);
        using var workbook = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db, service).ExportAsync(week.Id, filter))!));
        var sheet = workbook.Worksheet("Weekly summary");
        Assert.Equal("ZZ-WEEK", sheet.Cell(5, 1).GetString());
        Assert.Equal(600m, sheet.Cell(5, 3).GetValue<decimal>());
        Assert.Equal(row.Cutting.Completed, sheet.Cell(5, 5).GetValue<decimal>());
        Assert.Equal(row.Cutting.Pending, sheet.Cell(5, 6).GetValue<decimal>());
        Assert.Equal(3m, sheet.Cell(5, 9).GetValue<decimal>());
        Assert.Equal(summary.Through.ToDateTime(TimeOnly.MinValue), sheet.Cell(2, 4).GetDateTime());
        var monday = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekStart)))!.Products);
        Assert.Equal(550, monday.Planned);
        Assert.Equal(501, monday.Cutting.Completed);
        Assert.Equal(251, monday.Cutting.CompletedShift1);
        Assert.Equal(250, monday.Cutting.CompletedShift2);
        Assert.Equal(299, monday.Cutting.PendingAfterShift1);
        Assert.Equal(254, monday.Sewing.PendingAfterShift1);
        Assert.Equal(3, monday.Sewing.Opening);
        Assert.Equal(504, monday.Sewing.Pending);
        var tuesday = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekStart.AddDays(1))))!.Products);
        Assert.Equal(0, tuesday.Planned);
        Assert.Equal(0, tuesday.Cutting.Completed);
        Assert.Equal(0, tuesday.Cutting.CompletedShift1);
        Assert.Equal(0, tuesday.Cutting.CompletedShift2);
        Assert.Equal(49, tuesday.Cutting.Opening);
        Assert.Equal(504, tuesday.Sewing.Opening);
        Assert.Empty(tuesday.Intentions);
        var saturday = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekEnd)))!.Products);
        Assert.Equal(50, saturday.Planned);
        Assert.Equal(2, saturday.Cutting.Completed);
        Assert.Equal(1, saturday.Cutting.CompletedShift1);
        Assert.Equal(1, saturday.Cutting.CompletedShift2);
        Assert.Equal(49, saturday.Cutting.Opening);
        Assert.Equal(97, saturday.Cutting.Pending);
        var dailySheet = workbook.Worksheet("Balance diario");
        using var mondayBook = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db, service).ExportAsync(week.Id, new ProductionWeeklyFilter(week.WeekStart)))!));
        Assert.Equal(251m, mondayBook.Worksheet("Balance diario").Cell(5, 9).GetValue<decimal>());
        Assert.Equal(250m, mondayBook.Worksheet("Balance diario").Cell(5, 10).GetValue<decimal>());
        Assert.Equal(299m, mondayBook.Worksheet("Balance diario").Cell(5, 11).GetValue<decimal>());
        Assert.Equal(0m, dailySheet.Cell(5, 2).GetValue<decimal>());
        Assert.Equal(49m, dailySheet.Cell(5, 3).GetValue<decimal>());
        Assert.Equal(0m, dailySheet.Cell(5, 4).GetValue<decimal>());
        Assert.Equal(49m, dailySheet.Cell(5, 5).GetValue<decimal>());
        Assert.Equal(summary.Through.ToDateTime(TimeOnly.MinValue), dailySheet.Cell(2, 1).GetDateTime());
        db.ProductionScheduleLines.Add(new()
        {
            WeekId = week.Id,
            ProductId = product.Id,
            Sequence = 4,
            PlannedDate = week.WeekStart.AddDays(2),
            Quantity = 7,
            IsCarryover = true,
            StartArea = ProductionDailyArea.Sewing,
            Origin = ProductionScheduleOrigin.ExcelImport
        });
        await db.SaveChangesAsync();
        Assert.Equal(3, Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekStart)))!.Products).Sewing.Opening);
        var wednesday = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekStart.AddDays(2))))!.Products);
        Assert.Equal(511, wednesday.Sewing.Opening);
        Assert.Equal(511, wednesday.Sewing.Pending);
        Assert.Equal(511, Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekStart.AddDays(3))))!.Products).Sewing.Opening);
        await VerifyLookupAsync(db, week, product, actor);
    }

    private static async Task VerifyLookupAsync(WarehouseDbContext db, ProductionScheduleWeek week, Product planned, User actor)
    {
        var products = Enumerable.Range(0, 24).Select(i => new Product { Sku = $"AA-WEEK-{i:00}", BaseUnitId = 1 }).ToArray();
        db.AddRange(products);
        var inactive = new Product { Sku = "AA-WEEK-INACTIVE", BaseUnitId = 1, IsActive = false };
        db.Add(inactive);
        // Duplicate plan lines must not duplicate suggestions, and a planned product sorts before catalog matches.
        foreach (var (product, i) in products.Take(12).Select((p, i) => (p, i)))
            db.ProductionScheduleLines.Add(new()
            {
                WeekId = week.Id,
                ProductId = product.Id,
                Sequence = 10 + i,
                PlannedDate = week.WeekStart,
                Quantity = 1
            });
        db.ProductionScheduleLines.Add(new()
        {
            WeekId = week.Id,
            ProductId = products[14].Id,
            Sequence = 29,
            PlannedDate = week.WeekEnd,
            Quantity = 10
        });
        db.ProductionCarryoverPlans.Add(new()
        {
            WeekId = week.Id,
            ProductId = products[13].Id,
            PlannedDate = week.WeekStart,
            Area = ProductionDailyArea.Cutting,
            Quantity = 2,
            UpdatedByUserId = actor.Id
        });
        await db.SaveChangesAsync();
        var futureProduct = (await new ProductionDailyBalanceService(db).GetDailySummaryAsync(week.Id, new(week.WeekStart)))!
            .Products.Single(x => x.ProductId == products[14].Id);
        Assert.Equal(0, futureProduct.Planned);
        Assert.Equal(0, futureProduct.Cutting.Pending);
        Assert.Equal(10, (await new ProductionDailyBalanceService(db).GetDailySummaryAsync(week.Id, new(week.WeekEnd)))!
            .Products.Single(x => x.ProductId == products[14].Id).Planned);
        var pins = new UserPinService(db, new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="));
        var capture = new ProductionDailyCaptureService(db, pins, new InventoryMovementService(db, pins, TimeProvider.System),
            new WarehouseClock(new WarehouseSettingsService(db)), TimeProvider.System);
        var groups = await capture.SearchDailyProductsAsync(week.WeekStart, ProductionDailyArea.Cutting, "week");
        Assert.True(groups[0].HasMore);
        Assert.Equal(10, groups[0].Items.Count);
        var next = Assert.Single(await capture.SearchDailyProductsAsync(week.WeekStart, ProductionDailyArea.Cutting, "week", 0, 10));
        Assert.Contains(next.Items, x => x.Id == planned.Id);
        Assert.False(next.HasMore);
        Assert.DoesNotContain(groups.SelectMany(x => x.Items), x => x.Id == inactive.Id);
        var initial = await capture.SearchDailyProductsAsync(week.WeekStart, ProductionDailyArea.Cutting, null);
        Assert.DoesNotContain(initial, x => x.Group == 2);
        var exact = await capture.SearchDailyProductsAsync(week.WeekStart, ProductionDailyArea.Cutting, "aa-week-01");
        Assert.Equal(products[1].Id, exact[0].Items[0].Id);
        var all = groups[0].Items.Concat(next.Items).ToArray();
        Assert.Equal(all.Length, all.Select(x => x.Id).Distinct().Count());
        Assert.Contains(all, x => x.Id == products[13].Id);
    }
}
