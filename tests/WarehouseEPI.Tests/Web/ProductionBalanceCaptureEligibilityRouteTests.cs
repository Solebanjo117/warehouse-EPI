using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using static WarehouseEPI.Tests.Web.ProductionCaptureRecoveryTests;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionBalanceCaptureEligibilityRouteTests
{
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(18, false, false)]
    [InlineData(1, true, false)]
    [InlineData(18, true, false)]
    [InlineData(1, false, true)]
    [InlineData(18, false, true)]
    public async Task Opening_only_fields_and_draft_recovery_share_server_eligibility(short unitId, bool cuttingOnly, bool closed)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId);
        week.ExplicitCarryover = true;
        week.Status = closed ? ProductionScheduleWeekStatus.Closed : ProductionScheduleWeekStatus.Open;
        var product = new Product { Sku = "ELIGIBILITY-ONLY", BaseUnitId = unitId };
        db.Add(product);
        var stageIds = cuttingOnly ? new[] { config.CuttingStageId!.Value }
            : new[] { config.CuttingStageId!.Value, config.SewingStageId!.Value, config.ReadyToPackStageId!.Value };
        db.Add(new ProductionRoute
        {
            ProductId = product.Id,
            Name = "Eligibility route",
            Stages = stageIds.Select((id, i) => new ProductionRouteStage { StageId = id, Sequence = i + 1 }).ToArray()
        });
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
            db.Add(new ProductionInitialBalance
            {
                WeekId = week.Id,
                ProductId = product.Id,
                Area = area,
                Quantity = area == ProductionDailyArea.Cutting ? 700 : 0
            });
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var page = await client.GetStringAsync($"/Operations/Production?WeekId={week.Id}&Through={seed.Date:yyyy-MM-dd}&Sku={product.Sku}");
        using var rows = JsonDocument.Parse(await client.GetStringAsync($"/Operations/Production?handler=BalanceRows&weekId={week.Id}&date={seed.Date:yyyy-MM-dd}&productId={product.Id}"));
        foreach (var html in new[] { page, rows.RootElement.GetProperty("html").GetString()! })
            foreach (var area in Enum.GetValues<ProductionDailyArea>())
                foreach (var shift in new[] { 1, 2 })
                {
                    var expected = !closed;
                    var inputId = $"id=\"balance-produced-{product.Id}-{(int)area}-{shift}\"";
                    Assert.Equal(expected, html.Contains(inputId, StringComparison.Ordinal));
                }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Operations/Production?handler=BalanceEditRestore");
        request.Headers.Add("RequestVerificationToken", Input(page, "__RequestVerificationToken"));
        request.Content = JsonContent.Create(new
        {
            products = new[] { product.Id },
            edit = new
            {
                operationId = Guid.NewGuid(),
                weekId = week.Id,
                date = seed.Date,
                cells = new[] { new { productId = product.Id, area = 1, shift = 1, observed = "0", requested = "100" } }
            }
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var restored = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(closed, restored.RootElement.GetProperty("cells")[0].GetProperty("blocked").GetBoolean());
        Assert.Equal("0", restored.RootElement.GetProperty("cells")[0].GetProperty("current").GetString());
        Assert.False(await db.ProductionDailyCaptures.AnyAsync());
        Assert.False(await db.ProductionScheduleLines.AnyAsync(x => x.ProductId == product.Id));
    }
}
