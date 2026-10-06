using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using static WarehouseEPI.Tests.Web.ProductionCaptureRecoveryTests;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionBalanceEntryTests
{
    [Fact]
    public async Task Balance_entry_reviews_and_records_only_the_difference_in_all_three_areas()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var page = await client.GetStringAsync($"/Operations/Production?Day={seed.Date:yyyy-MM-dd}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();

        foreach (var (observed, requested) in new[] { ("0", "20"), ("20", "25") })
        {
            var operationId = Guid.NewGuid();
            async Task<JsonDocument> Send(string handler, string fingerprint, string pin)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/Operations/Production?handler={handler}");
                request.Headers.Add("RequestVerificationToken", Input(page, "__RequestVerificationToken"));
                request.Content = JsonContent.Create(new
                {
                    operationId,
                    weekId = seed.WeekId,
                    date = seed.Date,
                    fingerprint,
                    pin,
                    cells = Enumerable.Range(0, 3).Select(area => new { productId = seed.Products[0], area, shift = 1, observed, requested })
                });
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            }

            var count = await db.ProductionDailyCaptures.CountAsync();
            using var preview = await Send("BalanceEditPreview", "", "");
            Assert.True(preview.RootElement.GetProperty("canConfirm").GetBoolean(), preview.RootElement.ToString());
            Assert.Equal(count, await db.ProductionDailyCaptures.CountAsync());
            var fingerprint = preview.RootElement.GetProperty("fingerprint").GetString()!;
            using var invalid = await Send("BalanceEditConfirm", fingerprint, "9876");
            Assert.False(invalid.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(count, await db.ProductionDailyCaptures.CountAsync());
            using var saved = await Send("BalanceEditConfirm", fingerprint, "0123");
            Assert.True(saved.RootElement.GetProperty("success").GetBoolean(), saved.RootElement.ToString());
            using var repeated = await Send("BalanceEditConfirm", fingerprint, "0123");
            Assert.True(repeated.RootElement.GetProperty("success").GetBoolean());
        }

        var totals = await db.ProductionDailyCaptures.Where(x => x.Status == ProductionDailyCaptureStatus.Active)
            .GroupBy(x => x.Area).Select(x => x.Sum(c => c.Quantity)).ToArrayAsync();
        Assert.Equal(3, totals.Length);
        Assert.All(totals, total => Assert.Equal(25m, total));
    }

    [Theory]
    [InlineData("")]
    [InlineData("&Tab=unknown")]
    public async Task Entry_opens_editable_balance_and_preserves_requested_day_without_capture_navigation(string query)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(
            $"/Operations/Production?Day={seed.Date:yyyy-MM-dd}{query}"));

        Assert.Contains("id=\"production-balance-table\"", html);
        Assert.Contains("id=\"balance-editor\"", html);
        Assert.Contains($"data-date=\"{seed.Date:yyyy-MM-dd}\"", html);
        Assert.Contains("data-edit-shift=\"1\"", html);
        Assert.Contains("data-edit-shift=\"2\"", html);
        Assert.DoesNotContain("id=\"capture-group\"", html);
        Assert.DoesNotContain("Tab=capture", html);
        Assert.DoesNotContain("Group.Pin", html);

        var menu = WebUtility.HtmlDecode(await client.GetStringAsync("/Modules/production"));
        Assert.DoesNotContain("view=capture", menu);
        Assert.DoesNotContain("Tab=capture", menu);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionDailyCaptures.ToListAsync());
    }
}
