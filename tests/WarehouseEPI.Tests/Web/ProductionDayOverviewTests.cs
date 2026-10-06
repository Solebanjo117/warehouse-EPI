using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Production;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionDayOverviewTests
{
    [Theory]
    [InlineData(0, 12)]
    [InlineData(1, 5)]
    [InlineData(2, 7)]
    public void Units_shifts_missing_targets_and_pending_do_not_cancel_between_products(int shift, decimal expected)
    {
        var metadata = new Dictionary<Guid, ProductionOverviewProduct>();
        var products = new List<ProductionDailyProductSummary>();
        foreach (var (unit, code, pending) in new[] { ((short)1, "PZA", 20m), ((short)1, "PZA", -10m), ((short)2, "KG", 4m), ((short)18, "UNASSIGNED", 5m), ((short)18, "UNASSIGNED", 6m) })
        {
            var id = Guid.NewGuid();
            metadata[id] = new(id, "Description", unit, code);
            products.Add(new(id, id.ToString(), 0,
                new(ProductionDailyArea.Cutting, true, 12, pending, 0, Opening: 20, CompletedShift1: 5, CompletedShift2: 7),
                new(ProductionDailyArea.Sewing, true, 2, -2, 0, ToReconcile: 2),
                new(ProductionDailyArea.ReadyToPack, false, 0, 0, 0), []));
        }
        var overview = ProductionDayOverview.Create(new(Guid.NewGuid(), new(2026,9,21), new(2026,9,27), new(2026,9,21), ProductionScheduleWeekStatus.Open, products), metadata, shift);
        var cutting = overview.Areas[0];
        Assert.Equal(4, cutting.Quantities.Count);
        Assert.Equal(5, overview.AttentionProductCount);
        Assert.Equal(expected * 2, cutting.Quantities.Single(x => x.Unit == "PZA").Produced);
        Assert.Equal(20, cutting.Quantities.Single(x => x.Unit == "PZA").Pending);
        Assert.Equal(40, cutting.Quantities.Single(x => x.Unit == "PZA").Target);
        Assert.Equal(2, cutting.Quantities.Count(x => x.Unit == "UNASSIGNED" && x.Sku != null));
        Assert.All(overview.Areas[1].Quantities, row => Assert.Null(row.Target));
        Assert.Contains(overview.Attention, row => row.Reason == "Producción por conciliar");
        Assert.Empty(overview.Areas[2].Quantities);
    }

    [Fact]
    public async Task Routes_filters_default_clock_and_safe_rendered_fixtures()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = ProductionCaptureRecoveryTests.Configure(original);
        var seed = await ProductionCaptureRecoveryTests.SeedAsync(factory.Services);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var products = await db.Products.Where(x => seed.Products.Contains(x.Id)).OrderBy(x => x.Sku).ToArrayAsync();
        products[0].Description = "Arnés industrial de seguridad con protección reforzada y ajuste multiposición";
        products[1].BaseUnitId = 18;
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId);
        foreach (var (shiftId, quantity, state) in new[] {
            (config.Shift1Id!.Value, 12m, ProductionDailyCaptureStatus.Active),
            (config.Shift2Id!.Value, 6m, ProductionDailyCaptureStatus.Active),
            (config.Shift1Id.Value, 900m, ProductionDailyCaptureStatus.Reversed) })
            db.Add(new ProductionDailyCapture { WeekId = seed.WeekId, EffectiveDate = seed.Date,
                ProductId = products[0].Id, Area = ProductionDailyArea.Cutting, StageId = config.CuttingStageId!.Value,
                ShiftId = shiftId, Quantity = quantity, Status = state, IsFlexible = true,
                OperationId = Guid.NewGuid(), RequestFingerprint = "overview-fixture", ResponsibleUserId = week.CreatedByUserId });
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var today = await scope.ServiceProvider.GetRequiredService<WarehouseClock>().GetDateAsync(
            scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow());
        var home = await client.GetStringAsync("/Operations/Production");
        Assert.Contains("day-overview", home);
        Assert.Contains($"value=\"{today:yyyy-MM-dd}\"", home);
        foreach (var language in new[] { "es", "en" })
        foreach (var shift in new[] { 0, 1, 2 })
        {
            var url = $"/Operations/Production?Tab=summary&Day={seed.Date:yyyy-MM-dd}&OverviewShift={shift}&culture={language}&ui-culture={language}";
            var html = await client.GetStringAsync(url);
            Assert.Equal(3, Regex.Count(html, "data-overview-area="));
            var expected = shift == 0 ? 18 : shift == 1 ? 12 : 6;
            Assert.Contains($"<strong>{expected}</strong>", html);
            Assert.DoesNotContain("<strong>900</strong>", html);
            Assert.Contains("RECOVER-001", html);
            Assert.Contains("Tab=balance", html);
            Assert.Contains("Through=2026-09-21", html);
            Assert.Contains("Sku=RECOVER-001", html);
            Assert.Contains("Tab=capture", html);
            await Fixture($"summary-{language}-{shift}", html);
        }
        var empty = await client.GetStringAsync("/Operations/Production?Tab=summary&Day=2035-01-01");
        Assert.DoesNotContain("data-overview-area=", empty);
        await Fixture("summary-empty", empty);
        var invalid = await client.GetStringAsync("/Operations/Production?Tab=summary&Day=bad&OverviewShift=9");
        Assert.Contains("validation-summary-errors", invalid);
        Assert.DoesNotContain("data-overview-area=", invalid);
        await Fixture("summary-error", invalid);
        var balance = await client.GetStringAsync($"/Operations/Production?WeekId={seed.WeekId}&Through={seed.Date:yyyy-MM-dd}&Sku=RECOVER-001");
        Assert.Contains("balance-editor", balance);
        await Fixture("balance", balance);
        var capture = await client.GetStringAsync($"/Operations/Production?Tab=capture&Day={seed.Date:yyyy-MM-dd}");
        await Fixture("capture", capture);
        Assert.Equal(3, await db.ProductionDailyCaptures.CountAsync());
    }

    private static async Task Fixture(string name, string html)
    {
        var output = Environment.GetEnvironmentVariable("WAREHOUSE_OVERVIEW_FIXTURES");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        html = Regex.Replace(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", "");
        await File.WriteAllTextAsync(Path.Combine(output, name + ".html"), html);
    }
}
