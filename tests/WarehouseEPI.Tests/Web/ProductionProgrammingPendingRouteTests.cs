using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Tests.Production;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionProgrammingPendingRouteTests
{
    [Fact]
    public async Task Closed_week_renders_two_pending_and_signed_shift_balance_without_writes()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var actor = new User { FullName = "Test", RoleId = 1, PinLookup = "pending-route", PinHash = "test" };
        var date = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek { CreatedByUser = actor, WeekStart = date, WeekEnd = date.AddDays(6),
            RequestFingerprint = "pending-route", Status = ProductionScheduleWeekStatus.Closed };
        week.Lines.Add(new() { Product = product, PlannedDate = date, Quantity = 300, Sequence = 1 });
        week.Captures.Add(new() { Product = product, EffectiveDate = date, Area = ProductionDailyArea.Sewing,
            StageId = config.SewingStageId!.Value, ShiftId = config.Shift1Id!.Value, Quantity = 298,
            ResponsibleUser = actor, OperationId = Guid.NewGuid(), RequestFingerprint = "route-capture" });
        db.Add(week); await db.SaveChangesAsync();
        var rawHtml = await client.GetStringAsync($"/Operations/Production?Tab=balance&WeekId={week.Id}&Through={date:yyyy-MM-dd}");
        var html = WebUtility.HtmlDecode(rawHtml);
        Assert.Contains("data-balance-field=\"pendingAfterShift1\" data-area=\"1\">2</span>", html);
        Assert.Contains("data-balance-field=\"netPending\" data-area=\"1\">2</span>", html);
        Assert.Contains("Un valor negativo indica adelanto", html);
        Assert.Single(await db.ProductionDailyCaptures.ToListAsync());
        Assert.Equal(ProductionScheduleWeekStatus.Closed, (await db.ProductionScheduleWeeks.AsNoTracking().SingleAsync()).Status);
        if (Environment.GetEnvironmentVariable("WAREHOUSE_BALANCE_FIXTURES") is string directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "pending.html"), rawHtml);
        }
    }
}
