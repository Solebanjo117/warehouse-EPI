using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Operations.CycleCounts;

namespace WarehouseEPI.Tests.Inventory;

public sealed class CycleCountPlanSchedulingTests
{
    [Fact]
    public async Task Create_uses_authenticated_admin_identity_and_writes_audit()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.CreatePlanAsync(new(
            fixture.Product.Id, fixture.Location.Id, CycleCountFrequency.Monthly,
            new DateOnly(2026, 1, 31), fixture.Admin.Id));

        Assert.Equal(CycleCountStatus.Success, result.Status);
        Assert.NotNull(result.PlanId);
        Assert.Null(result.MovementId);
        var plan = await fixture.Db.CycleCountPlans.SingleAsync();
        Assert.Equal(fixture.Admin.Id, plan.CreatedByUserId);
        var audit = await fixture.Db.CycleCountPlanEvents.SingleAsync();
        Assert.Equal(CycleCountPlanEventType.Created, audit.Type);
        Assert.Equal(fixture.Admin.Id, audit.ResponsibleUserId);
        Assert.Equal(new DateOnly(2026, 1, 31), audit.NewNextDueDate);
    }

    [Fact]
    public async Task Administrative_commands_reject_non_admin_and_inactive_catalog_selection()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Product.IsActive = false;
        await fixture.Db.SaveChangesAsync();

        var invalidActor = await fixture.Service.CreatePlanAsync(new(
            fixture.Product.Id, fixture.Location.Id, CycleCountFrequency.Weekly,
            new DateOnly(2026, 2, 1), fixture.Operator.Id));
        var inactiveProduct = await fixture.Service.CreatePlanAsync(new(
            fixture.Product.Id, fixture.Location.Id, CycleCountFrequency.Weekly,
            new DateOnly(2026, 2, 1), fixture.Admin.Id));

        Assert.Equal(CycleCountStatus.InvalidState, invalidActor.Status);
        Assert.Equal(CycleCountStatus.ValidationFailed, inactiveProduct.Status);
        Assert.Empty(await fixture.Db.CycleCountPlans.ToListAsync());
        Assert.Empty(await fixture.Db.CycleCountPlanEvents.ToListAsync());
    }

    [Fact]
    public async Task Pause_reactivate_and_identical_update_preserve_due_date_without_duplicate_events()
    {
        await using var fixture = await Fixture.CreateAsync();
        var plan = fixture.AddPlan(CycleCountFrequency.Monthly, new DateOnly(2026, 1, 31), new DateOnly(2026, 3, 31));
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(CycleCountStatus.Success, (await fixture.Service.SetPlanActiveAsync(new(plan.Id, false, fixture.Admin.Id))).Status);
        Assert.Equal(CycleCountStatus.Success, (await fixture.Service.SetPlanActiveAsync(new(plan.Id, true, fixture.Admin.Id))).Status);
        Assert.Equal(CycleCountStatus.Success, (await fixture.Service.UpdatePlanAsync(new(plan.Id, plan.Frequency, plan.AnchorDate, fixture.Admin.Id))).Status);

        await fixture.Db.Entry(plan).ReloadAsync();
        Assert.Equal(new DateOnly(2026, 3, 31), plan.NextDueDate);
        Assert.True(plan.IsActive);
        Assert.Equal(2, await fixture.Db.CycleCountPlanEvents.CountAsync());
    }

    [Fact]
    public async Task Open_campaign_blocks_plan_edit_without_audit_event()
    {
        await using var fixture = await Fixture.CreateAsync();
        var plan = fixture.AddPlan(CycleCountFrequency.Weekly, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 8));
        fixture.AddDispatch(plan, new DateOnly(2026, 1, 8), CycleCountLocationStatus.Pending,
            new DateTimeOffset(2026, 1, 8, 12, 0, 0, TimeSpan.Zero), campaignStatus: CycleCountCampaignStatus.Released);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.UpdatePlanAsync(new(plan.Id, CycleCountFrequency.Monthly,
            new DateOnly(2026, 1, 31), fixture.Admin.Id));

        Assert.Equal(CycleCountStatus.InvalidState, result.Status);
        Assert.Equal(CycleCountFrequency.Weekly, (await fixture.Db.CycleCountPlans.SingleAsync()).Frequency);
        Assert.Empty(await fixture.Db.CycleCountPlanEvents.ToListAsync());
    }

    [Theory]
    [InlineData(CycleCountFrequency.Weekly, "2026-03-07")]
    [InlineData(CycleCountFrequency.Biweekly, "2026-03-14")]
    [InlineData(CycleCountFrequency.Monthly, "2026-03-31")]
    [InlineData(CycleCountFrequency.Quarterly, "2026-04-30")]
    [InlineData(CycleCountFrequency.Semiannual, "2026-07-31")]
    [InlineData(CycleCountFrequency.Annual, "2027-01-31")]
    public async Task Real_edit_recalculates_from_anchor_after_last_completion(CycleCountFrequency frequency, string expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var originalFrequency = frequency == CycleCountFrequency.Weekly ? CycleCountFrequency.Biweekly : CycleCountFrequency.Weekly;
        var plan = fixture.AddPlan(originalFrequency, new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 7));
        fixture.AddDispatch(plan, new DateOnly(2026, 2, 14), CycleCountLocationStatus.Completed,
            new DateTimeOffset(2026, 2, 28, 18, 0, 0, TimeSpan.Zero));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.UpdatePlanAsync(new(plan.Id, frequency, plan.AnchorDate, fixture.Admin.Id));

        Assert.Equal(CycleCountStatus.Success, result.Status);
        Assert.Equal(DateOnly.Parse(expected), (await fixture.Db.CycleCountPlans.SingleAsync()).NextDueDate);
    }

    [Fact]
    public async Task Annual_recurrence_preserves_leap_day_rule_and_is_strictly_after_completion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var plan = fixture.AddPlan(CycleCountFrequency.Weekly, new DateOnly(2024, 2, 29), new DateOnly(2024, 3, 7));
        fixture.AddDispatch(plan, new DateOnly(2025, 2, 28), CycleCountLocationStatus.Completed,
            new DateTimeOffset(2025, 2, 28, 18, 0, 0, TimeSpan.Zero));
        await fixture.Db.SaveChangesAsync();

        await fixture.Service.UpdatePlanAsync(new(plan.Id, CycleCountFrequency.Annual, plan.AnchorDate, fixture.Admin.Id));

        Assert.Equal(new DateOnly(2026, 2, 28), (await fixture.Db.CycleCountPlans.SingleAsync()).NextDueDate);
    }

    [Fact]
    public async Task Calendar_keeps_historical_month_shows_zero_and_separates_overdue_pending()
    {
        await using var fixture = await Fixture.CreateAsync();
        var completed = fixture.AddPlan(CycleCountFrequency.Monthly, new DateOnly(2026, 8, 31), new DateOnly(2026, 10, 31));
        fixture.AddDispatch(completed, new DateOnly(2026, 9, 30), CycleCountLocationStatus.Completed,
            new DateTimeOffset(2026, 10, 2, 3, 0, 0, TimeSpan.Zero), 0m);
        var overdue = fixture.AddPlan(CycleCountFrequency.Weekly, new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 15));
        await fixture.Db.SaveChangesAsync();

        var september = await fixture.Service.GetCalendarAsync(new(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 5)));
        var overdueView = await fixture.Service.GetCalendarAsync(new(
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 5), true));

        var history = Assert.Single(september.Items, item => item.IsHistorical);
        Assert.True(history.IsHistorical);
        Assert.Equal(new DateOnly(2026, 9, 30), history.ScheduledFor);
        Assert.Equal(0m, history.CompletedQuantity);
        Assert.Contains(september.Items, item => item.PlanId == overdue.Id && !item.IsHistorical);
        Assert.Contains(overdueView.Items, item => item.PlanId == overdue.Id && !item.IsHistorical);
        Assert.DoesNotContain(overdueView.Items, item => item.PlanId == completed.Id);
    }

    [Fact]
    public async Task Catalog_search_and_pagination_are_stable()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var index = 0; index < 30; index++)
        {
            var product = new Product { Sku = $"PLAN-{index:D2}", Description = index == 29 ? "Objetivo especial" : null, BaseUnitId = 1 };
            fixture.Db.Products.Add(product);
            fixture.Db.CycleCountPlans.Add(new() { Product = product, LocationId = fixture.Location.Id, Frequency = CycleCountFrequency.Weekly, AnchorDate = new(2026, 1, 1), NextDueDate = new(2026, 1, 1) });
        }
        await fixture.Db.SaveChangesAsync();

        var first = await fixture.Service.GetPlanCatalogAsync(new(null, null, 1, 25));
        var second = await fixture.Service.GetPlanCatalogAsync(new(null, null, 2, 25));
        var searched = await fixture.Service.GetPlanCatalogAsync(new("especial", null, 1, 25));

        Assert.Equal(30, first.Total);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        Assert.Equal("PLAN-29", Assert.Single(searched.Items).Sku);
    }

    [Fact]
    public async Task Calendar_page_rejects_invalid_month_in_spanish_and_uses_controlled_warehouse_date()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = new CalendarModel(fixture.Service, fixture.Clock, fixture.Time);

        await page.OnGetAsync("2026-99", "month");

        Assert.Equal("El mes indicado no es válido. Selecciona un mes con el formato año-mes.", page.Error);
        Assert.Equal(new DateOnly(2026, 10, 1), page.From);
        Assert.Equal("2026-10", page.SelectedMonth);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string LookupKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        public WarehouseDbContext Db { get; }
        public CycleCountService Service { get; }
        public WarehouseClock Clock { get; }
        public TimeProvider Time { get; }
        public User Admin { get; }
        public User Operator { get; }
        public Product Product { get; }
        public Location Location { get; }

        private Fixture(WarehouseDbContext db, CycleCountService service, WarehouseClock clock, TimeProvider time,
            User admin, User @operator, Product product, Location location) =>
            (Db, Service, Clock, Time, Admin, Operator, Product, Location) = (db, service, clock, time, admin, @operator, product, location);

        public static async Task<Fixture> CreateAsync()
        {
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase($"CycleCountPlans-{Guid.NewGuid():N}").Options);
            await db.Database.EnsureCreatedAsync();
            var admin = new User { FullName = "Administradora", RoleId = 1, PinLookup = "admin", PinHash = "hash" };
            var @operator = new User { FullName = "Operador", RoleId = 2, PinLookup = "operator", PinHash = "hash" };
            var product = new Product { Sku = "SKU-PLAN", Description = "Producto planificado", BaseUnitId = 1 };
            var location = new Location { Code = "A-1-1", Kind = LocationKind.Rack, RowCode = "A" };
            db.AddRange(admin, @operator, product, location);
            await db.SaveChangesAsync();
            var time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
            var pins = new UserPinService(db, new PinProtector(LookupKey));
            var movements = new InventoryMovementService(db, pins, time);
            var settings = new WarehouseSettingsService(db);
            var clock = new WarehouseClock(settings);
            return new(db, new CycleCountService(db, pins, new InventoryQueryService(db), movements, time, clock), clock, time, admin, @operator, product, location);
        }

        public CycleCountPlan AddPlan(CycleCountFrequency frequency, DateOnly anchor, DateOnly due)
        {
            var plan = new CycleCountPlan { ProductId = Product.Id, LocationId = Location.Id, Frequency = frequency, AnchorDate = anchor, NextDueDate = due };
            Db.CycleCountPlans.Add(plan);
            return plan;
        }

        public void AddDispatch(CycleCountPlan plan, DateOnly scheduledFor, CycleCountLocationStatus status,
            DateTimeOffset completedAt, decimal? quantity = null,
            CycleCountCampaignStatus campaignStatus = CycleCountCampaignStatus.Completed)
        {
            var campaign = new CycleCountCampaign { OperationId = Guid.NewGuid(), Status = campaignStatus, CreatedByUserId = Admin.Id };
            var location = new CycleCountLocation { Campaign = campaign, LocationId = Location.Id, Status = status, CompletedAt = completedAt, LastActionByUserId = Admin.Id };
            location.PlannedProducts.Add(new() { ProductId = Product.Id, CycleCountPlan = plan, ScheduledFor = scheduledFor });
            if (quantity is not null)
            {
                var attempt = new CycleCountAttempt { OperationId = Guid.NewGuid(), AttemptNumber = 1, Status = CycleCountAttemptStatus.Submitted, StartedByUserId = Admin.Id, SubmittedByUserId = Admin.Id, SubmittedAt = completedAt };
                attempt.Entries.Add(new() { ProductId = Product.Id, UnitId = 1, CountedQuantity = quantity });
                location.Attempts.Add(attempt);
            }
            campaign.Locations.Add(location);
            Db.CycleCountCampaigns.Add(campaign);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
    }
}
