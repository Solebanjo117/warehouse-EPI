using ClosedXML.Excel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionInitialBalanceTests
{
    [Theory]
    [InlineData(ProductionScheduleWeekStatus.Draft)]
    [InlineData(ProductionScheduleWeekStatus.Open)]
    public async Task More_than_100_initial_totals_review_and_save_together(ProductionScheduleWeekStatus status)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, productId) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var template = await db.Products.SingleAsync(x => x.Id == productId);
        var products = Enumerable.Range(1, 36).Select(i => new Product { Sku = $"LARGE-{i}", BaseUnitId = template.BaseUnitId }).ToArray();
        db.AddRange(products);
        var week = new ProductionScheduleWeek { CreatedByUserId = actor, RequestFingerprint = "large", OperationId = Guid.NewGuid(),
            WeekStart = new(2026, 10, 5), WeekEnd = new(2026, 10, 11), ExplicitCarryover = true, Status = status };
        db.Add(week); await db.SaveChangesAsync();
        var changes = products.SelectMany(product => Enum.GetValues<ProductionDailyArea>().Select(area =>
            new ProductionInitialBalanceChange(product.Id, area, area == ProductionDailyArea.Sewing ? 0 : 12, 0))).ToArray();
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), week.Id, week.Version, [], actor,
            Reason: "Arrastre inicial completo", InitialBalances: changes);
        var review = await service.PreviewWorkspaceChangesAsync(command);
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        command = command with { ReviewedFingerprint = review.Fingerprint };
        Assert.True((await service.SaveWorkspaceChangesAsync(command, "4826")).Success);
        Assert.True((await service.SaveWorkspaceChangesAsync(command, "4826")).Success);
        Assert.Equal(108, await db.ProductionInitialBalances.CountAsync());
        Assert.Equal(36, await db.ProductionInitialBalances.CountAsync(x => x.Quantity == 0));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Theory]
    [InlineData(true, ProductionScheduleWeekStatus.Draft)]
    [InlineData(true, ProductionScheduleWeekStatus.Open)]
    [InlineData(false, ProductionScheduleWeekStatus.Draft)]
    [InlineData(false, ProductionScheduleWeekStatus.Open)]
    public async Task Opening_only_saves_one_total_per_area_and_retries_without_inventory_changes(bool explicitWeek, ProductionScheduleWeekStatus status)
    {
        await using var db = ProductionOpeningImportTests.Context();
        await VerifySaveAsync(db, explicitWeek, status);
    }

    internal static async Task VerifySaveAsync(WarehouseDbContext db, bool explicitWeek = false,
        ProductionScheduleWeekStatus status = ProductionScheduleWeekStatus.Open)
    {
        var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var week = new ProductionScheduleWeek { CreatedByUserId = actor, RequestFingerprint = "initial",
            OperationId = Guid.NewGuid(), WeekStart = new(2026, 10, 5), WeekEnd = new(2026, 10, 11),
            ExplicitCarryover = explicitWeek, Origin = explicitWeek ? ProductionScheduleOrigin.Manual : ProductionScheduleOrigin.ExcelImport,
            Status = status };
        db.Add(week); await db.SaveChangesAsync();
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), week.Id, week.Version, [], actor,
            Reason: "Corregir arrastre", InitialBalances: [new(product, ProductionDailyArea.Cutting, 250, 0),
                new(product, ProductionDailyArea.Sewing, 0, 0), new(product, ProductionDailyArea.ReadyToPack, 125, 0)]);
        var review = await service.PreviewWorkspaceChangesAsync(command);
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        Assert.Empty(await db.ProductionInitialBalances.ToListAsync());
        command = command with { ReviewedFingerprint = review.Fingerprint };
        Assert.True((await service.SaveWorkspaceChangesAsync(command, "4826")).Success);
        Assert.True((await service.SaveWorkspaceChangesAsync(command, "4826")).Success);
        Assert.Equal(3, await db.ProductionInitialBalances.CountAsync());
        Assert.Empty(await db.ProductionScheduleLines.ToListAsync());
        Assert.Empty(await db.ProductionWorkOrders.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        var initial = await new ProductionInitialBalanceService(db).GetAsync(week.Id);
        Assert.Equal(250, Assert.Single(initial, x => x.Area == ProductionDailyArea.Cutting).Quantity);
        Assert.Equal(0, Assert.Single(initial, x => x.Area == ProductionDailyArea.Sewing).Quantity);
        var daily = await new ProductionDailyBalanceService(db).GetDailySummaryAsync(week.Id, new(week.WeekStart));
        Assert.Equal(125, Assert.Single(daily!.Products).ReadyToPack.SignedPending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_zero_overrides_inherited_opening_and_lower_total_preserves_captures(bool explicitWeek)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var week = new ProductionScheduleWeek { CreatedByUserId = actor, RequestFingerprint = "initial", OperationId = Guid.NewGuid(),
            WeekStart = new(2026, 10, 5), WeekEnd = new(2026, 10, 11), ExplicitCarryover = explicitWeek, Status = ProductionScheduleWeekStatus.Open };
        week.Lines.Add(new() { ProductId = product, Quantity = 100, PlannedDate = week.WeekStart, IsCarryover = !explicitWeek,
            StartArea = ProductionDailyArea.Cutting });
        if (explicitWeek)
        {
            var source = new ProductionScheduleWeek { CreatedByUserId = actor, RequestFingerprint = "source", OperationId = Guid.NewGuid(),
                WeekStart = week.WeekStart.AddDays(-7), WeekEnd = week.WeekStart.AddDays(-1), Status = ProductionScheduleWeekStatus.Open, ExplicitCarryover = true };
            var root = new ProductionScheduleLine { Week = source, ProductId = product, Quantity = 200, PlannedDate = source.WeekStart };
            db.AddRange(source, root);
            db.ProductionWeekOpenings.Add(new() { WeekId = week.Id, ProductId = product, SourceWeekId = source.Id,
                SourceLineId = root.Id, Area = ProductionDailyArea.Cutting, Quantity = 200, SourceFingerprint = "outdated" });
        }
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        week.Captures.Add(new() { ProductId = product, ResponsibleUserId = actor, OperationId = Guid.NewGuid(), RequestFingerprint = "capture",
            Quantity = 150, EffectiveDate = week.WeekStart, Area = ProductionDailyArea.Cutting,
            StageId = config.CuttingStageId!.Value, ShiftId = config.Shift1Id!.Value });
        db.Add(week); await db.SaveChangesAsync();
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), week.Id, week.Version, [], actor,
            Reason: "Corregir cantidad calculada", InitialBalances: [new(product, ProductionDailyArea.Cutting, 0, 0)]);
        var review = await service.PreviewWorkspaceChangesAsync(command);
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        Assert.True((await service.SaveWorkspaceChangesAsync(command with { ReviewedFingerprint = review.Fingerprint }, "4826")).Success);
        Assert.Equal(150, (await db.ProductionDailyCaptures.SingleAsync()).Quantity);
        var balances = new ProductionDailyBalanceService(db);
        var raw = (await balances.GetAsync(week.Id))!.Rows.Single(x => x.Date == week.WeekStart);
        Assert.Equal(explicitWeek ? -50 : -150, raw.Cutting.SignedPending);
        Assert.Equal(0, Assert.Single(await new ProductionInitialBalanceService(db).GetAsync(week.Id), x => x.Area == ProductionDailyArea.Cutting).Quantity);
        Assert.Empty(await new ProductionWeekOpeningService(db).RevalidateAsync(week.Id));
        var close = (await balances.GetWeekCloseAsync(week.Id, new(week.WeekEnd)))!;
        Assert.Equal(raw.Cutting.SignedPending, Assert.Single(close.Products).Cutting.Pending);
        var progress = Assert.Single(await balances.GetScheduleProgressAsync(week.Id));
        Assert.DoesNotContain(progress.Openings, x => x.Area == ProductionDailyArea.Cutting);
        var bytes = await new ProductionDailyExportService(db, balances).ExportAsync(week.Id);
        using var workbook = new XLWorkbook(new MemoryStream(bytes!));
        Assert.Equal(0, workbook.Worksheet("Arrastre inicial").Cell(4, 4).GetValue<decimal>());
    }

    [Fact]
    public async Task Rejects_duplicate_areas_decimals_in_piece_units_closed_weeks_and_obsolete_versions()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var weekId = (await service.CreateWeekAsync(new(Guid.NewGuid(), new(2026, 10, 5), actor))).Id!.Value;
        (await db.Products.Include(x => x.BaseUnit).SingleAsync(x => x.Id == product)).BaseUnit.AllowsDecimals = false;
        await db.SaveChangesAsync();
        var totals = new ProductionInitialBalanceService(db);
        Assert.NotEmpty(await totals.ValidateAsync(weekId, [new(product, ProductionDailyArea.Cutting, 1.5m, 0)]));
        Assert.NotEmpty(await totals.ValidateAsync(weekId, [new(product, ProductionDailyArea.Cutting, 1, 0), new(product, ProductionDailyArea.Cutting, 2, 0)]));
        Assert.NotEmpty(await totals.ValidateAsync(weekId, [new(product, ProductionDailyArea.Cutting, -1, 0)]));
        await totals.ApplyAsync(weekId, [new(product, ProductionDailyArea.Cutting, 5, 0)], default); await db.SaveChangesAsync();
        Assert.NotEmpty(await totals.ValidateAsync(weekId, [new(product, ProductionDailyArea.Cutting, 10, 0)]));
        (await db.ProductionScheduleWeeks.SingleAsync()).Status = ProductionScheduleWeekStatus.Closed;
        await db.SaveChangesAsync();
        Assert.NotEmpty(await totals.ValidateAsync(weekId, [new(product, ProductionDailyArea.Cutting, 10, 1)]));
        Assert.Equal(5, (await db.ProductionInitialBalances.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task A_prepared_new_week_can_open_with_only_initial_balances()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var command = new CreatePreparedProductionScheduleWeekCommand(Guid.NewGuid(), new(2026, 10, 5), [], actor,
            InitialBalances: [new(product, ProductionDailyArea.Sewing, 87, 0)]);
        var review = await service.PreviewNewWeekAsync(command);
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        var result = await service.CreatePreparedWeekAsync(command with { ReviewedFingerprint = review.Fingerprint }, "4826");
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        Assert.Equal(ProductionScheduleWeekStatus.Open, (await service.GetWeekAsync(result.Id!.Value))!.Status);
        Assert.Equal(87, Assert.Single(await new ProductionInitialBalanceService(db).GetAsync(result.Id.Value), x => x.Area == ProductionDailyArea.Sewing).Quantity);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Definitive_total_replaces_dated_historical_openings_in_every_daily_balance(bool explicitWeek)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var week = new ProductionScheduleWeek { CreatedByUserId = actor, OperationId = Guid.NewGuid(), RequestFingerprint = "dated",
            WeekStart = new(2026, 10, 5), WeekEnd = new(2026, 10, 11), ExplicitCarryover = explicitWeek, Status = ProductionScheduleWeekStatus.Open };
        week.Lines.Add(new() { ProductId = product, Quantity = 70, IsCarryover = true, StartArea = ProductionDailyArea.Cutting, PlannedDate = week.WeekStart });
        week.Lines.Add(new() { ProductId = product, Quantity = 90, IsCarryover = true, StartArea = ProductionDailyArea.Cutting, PlannedDate = week.WeekStart.AddDays(2) });
        var configuration = await db.ProductionDailyConfigurations.SingleAsync();
        week.Captures.Add(new() { ProductId = product, Quantity = 30, Area = ProductionDailyArea.Cutting,
            EffectiveDate = week.WeekStart.AddDays(1), StageId = configuration.CuttingStageId!.Value, ShiftId = configuration.Shift1Id!.Value,
            ResponsibleUserId = actor, OperationId = Guid.NewGuid(), RequestFingerprint = "capture" });
        db.Add(week); await db.SaveChangesAsync();
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), week.Id, week.Version, [], actor,
            Reason: "Consolidar cantidad inicial", InitialBalances: [new(product, ProductionDailyArea.Cutting, 40.125m, 0)]);
        var review = await service.PreviewWorkspaceChangesAsync(command);
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        Assert.True((await service.SaveWorkspaceChangesAsync(command with { ReviewedFingerprint = review.Fingerprint }, "4826")).Success);
        var balances = new ProductionDailyBalanceService(db);
        foreach (var day in ProductionWeekCalendar.Days(week.WeekStart))
        {
            var daily = (await balances.GetDailySummaryAsync(week.Id, new(day)))!;
            var area = Assert.Single(daily.Products).Cutting;
            Assert.Equal(day == week.WeekStart ? 40.125m : 10.125m, area.SignedPending);
            Assert.Equal(day <= week.WeekStart.AddDays(1) ? 40.125m : 10.125m, area.Opening);
            Assert.Equal(40.125m, area.CumulativeRequirement);
        }
        Assert.Equal(10.125m, Assert.Single((await balances.GetWeekCloseAsync(week.Id, new(week.WeekEnd)))!.Products).Cutting.Pending);
        var audit = await db.ProductionScheduleRevisions.SingleAsync(x => x.OperationId == command.OperationId);
        using var before = JsonDocument.Parse(audit.BeforeJson);
        Assert.Equal(70, before.RootElement.GetProperty("InitialBalances")[0].GetProperty("Quantity").GetDecimal());
        Assert.Equal(2, await db.ProductionScheduleLines.CountAsync());
        Assert.Equal(30, (await db.ProductionDailyCaptures.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task Zero_only_persists_without_history_and_duplicate_totals_remain_invalid()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var id = (await service.CreateWeekAsync(new(Guid.NewGuid(), new(2026, 10, 5), actor))).Id!.Value;
        var week = (await service.GetWeekAsync(id))!;
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), id, week.Version, [], actor,
            InitialBalances: [new(product, ProductionDailyArea.Cutting, 0, 0)]);
        Assert.True((await service.SaveWorkspaceChangesAsync(command)).Success);
        Assert.Equal(0, Assert.Single(await new ProductionInitialBalanceService(db).GetAsync(id), x => x.Area == ProductionDailyArea.Cutting).Quantity);
        Assert.True((await db.ProductionInitialBalances.SingleAsync()).Version > 0);
        var excessive = command with { OperationId = Guid.NewGuid(), ExpectedWeekVersion = (await service.GetWeekAsync(id))!.Version,
            InitialBalances = Enumerable.Repeat(new ProductionInitialBalanceChange(product, ProductionDailyArea.Cutting, 0, 1), 101).ToArray() };
        Assert.False((await service.PreviewWorkspaceChangesAsync(excessive)).CanConfirm);
        Assert.False((await service.SaveWorkspaceChangesAsync(excessive)).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retries_of_old_batch_contracts_keep_the_original_idempotency_fingerprint(bool reviewed)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var id = (await service.CreateWeekAsync(new(Guid.NewGuid(), new(2026, 10, 5), actor))).Id!.Value;
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), id, 1,
            [new("add", null, null, new(new(2026, 10, 5), product, 10, null, null, null, null))], actor,
            Reason: reviewed ? "Motivo anterior" : "", ReviewedFingerprint: reviewed ? "review-before-migration" : "");
        object oldRequest = reviewed ? new { command.OperationId, command.WeekId, command.ExpectedWeekVersion, command.Changes,
            command.ActorUserId, command.Openings, command.Reason, command.ReviewedFingerprint } : new { command.OperationId,
            command.WeekId, command.ExpectedWeekVersion, command.Changes, command.ActorUserId, command.Openings };
        db.ProductionScheduleRevisions.Add(new() { WeekId = id, OperationId = command.OperationId, RequestFingerprint = Hash(oldRequest),
            Action = "saved", ResponsibleUserId = actor, BeforeJson = "{}", AfterJson = "{}" });
        await db.SaveChangesAsync();
        Assert.True((await service.SaveWorkspaceChangesAsync(command)).Success);
        Assert.Empty(await db.ProductionScheduleLines.ToListAsync());
        var prepared = new CreatePreparedProductionScheduleWeekCommand(Guid.NewGuid(), new(2026, 10, 5), [], actor, ReviewedFingerprint: "old-review");
        var week = await db.ProductionScheduleWeeks.SingleAsync(); week.OperationId = prepared.OperationId;
        week.RequestFingerprint = Hash(new { prepared.OperationId, prepared.WeekStart, prepared.Lines, prepared.ActorUserId,
            prepared.Openings, prepared.ReviewedFingerprint });
        await db.SaveChangesAsync();
        Assert.True((await service.CreatePreparedWeekAsync(prepared, "4826")).Success);
    }

    private static string Hash(object request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
