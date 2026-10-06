using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Localization;
using static WarehouseEPI.Tests.Web.ProductionCaptureRecoveryTests;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionBalanceTabletUiTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Balance_renders_the_existing_model_in_both_languages_and_preserves_permissions(string language)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var schedule = scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
        (await db.Products.SingleAsync(x => x.Id == seed.Products[0])).Sku = "SHORT-SKU";
        (await db.Products.SingleAsync(x => x.Id == seed.Products[1])).Sku = "BALANCE-LONG-SKU-ABCDEFGHIJKLMNOPQRSTUVWXYZ-0123456789-ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        await db.SaveChangesAsync();
        var week = (await schedule.GetWeekAsync(seed.WeekId))!;
        var actor = (await db.Users.SingleAsync(x => x.FullName == "Capture recovery operator")).Id;
        var added = await schedule.SaveLineAsync(new(Guid.NewGuid(), seed.WeekId, null, week.Version, null,
            seed.Date, seed.Products[1], 12.3456m, "ORDER-ONE-LONG-ABCDEFGHIJKLMNOPQRSTUVWXYZ", "ORDER-TWO", "ORDER-THREE", "Independent notes", actor, "0123"));
        Assert.True(added.Success, string.Join("; ", added.Errors ?? []));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        var url = $"/Operations/Production?WeekId={seed.WeekId}&Through={seed.Date:yyyy-MM-dd}";
        var anonymous = await client.GetStringAsync(url);
        Assert.DoesNotContain("data-plan-line=", anonymous);
        Assert.DoesNotContain("data-new-plan=", anonymous);
        await Fixture(language, "open", anonymous);
        var login = await client.GetStringAsync("/Admin/Login");
        using var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Input(login, "__RequestVerificationToken"),
            ["Input.Pin"] = "0123",
            ["ReturnUrl"] = url
        }));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var admin = await client.GetStringAsync(url);
        Assert.DoesNotContain("data-plan-line=", admin);
        Assert.DoesNotContain("data-new-plan=", admin);
        Assert.Contains("data-balance-plan>42.3456</span>", admin);
        Assert.Equal(18, Regex.Count(admin, "data-edit-area=\""));
        Assert.Contains("12.3456", admin);
        Assert.Contains("ORDER-THREE", admin);
        Assert.Contains("type=\"password\"", admin);
        Assert.DoesNotContain("value=\"0123\"", admin);
        await Fixture(language, "admin", admin);
        await Fixture(language, "filtered", await client.GetStringAsync(url + "&Sku=absent&Reference=FILTER-REFERENCE&Area=Sewing"));
        var balances = scope.ServiceProvider.GetRequiredService<ProductionDailyBalanceService>();
        var model = await balances.GetEditableSummaryAsync(seed.WeekId, seed.Date, seed.Products);
        await Fixture(language, "model", JsonSerializer.Serialize(model, JsonSerializerOptions.Web), "json");
        Assert.Equal(1, Regex.Count(admin, "name=\"WeekId\""));
        Assert.Equal(1, Regex.Count(admin, "data-balance-context"));
        Assert.Equal(1, Regex.Count(admin, "data-edit-review-button"));
        Assert.Equal(7, Regex.Count(admin, "data-balance-day=\""));
        Assert.Contains("form=\"balance-filters\"", admin);
        Assert.Equal(3, Regex.Count(admin, "data-balance-disclosure=\"breakdown\""));
        Assert.Equal(1, Regex.Count(admin, "data-balance-disclosure=\"program\""));
        Assert.DoesNotContain("data-balance-metric=", admin);
        Assert.DoesNotContain("id=\"balance-product-search\"", admin);
        Assert.DoesNotContain("id=\"balance-product-results\"", admin);
        Assert.Equal(18, Regex.Count(admin, "data-balance-breakdown=\""));
        var decoded = WebUtility.HtmlDecode(admin);
        Assert.Contains(language == "en" ? "How to interpret the balance" : "Cómo interpretar el balance", decoded);
        Assert.Contains(language == "en" ? "View schedule (2)" : "Ver programación (2)", decoded);
        foreach (var product in model!.Products)
        {
            var row = Regex.Match(admin, $"<tr[^>]*data-balance-product=\"{product.ProductId}\"[^>]*>(.*?)</tr>", RegexOptions.Singleline).Groups[1].Value;
            foreach (var area in new[] { product.Cutting, product.Sewing, product.ReadyToPack })
            {
                foreach (var (field, expected) in new[] { ("opening", area.Opening), ("pendingAfterShift1", area.PendingAfterShift1), ("netPending", area.SignedPending) })
                {
                    var actual = Regex.Match(row, $"<span data-balance-field=\"{field}\" data-area=\"{(int)area.Area}\">([^<]+)</span>").Groups[1].Value;
                    Assert.Equal(expected.ToString("0.####", CultureInfo.InvariantCulture), actual);
                }
            }
        }
        var futureDate = new DateOnly(2099, 9, 21);
        futureDate = futureDate.AddDays(-(((int)futureDate.DayOfWeek + 6) % 7));
        var futureId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), futureDate, actor))).Id!.Value;
        var future = (await schedule.GetWeekAsync(futureId))!;
        Assert.True((await schedule.SaveLineAsync(new(Guid.NewGuid(), futureId, null, future.Version, null,
            futureDate, seed.Products[0], 30, null, null, null, null, actor))).Success);
        future = (await schedule.GetWeekAsync(futureId))!;
        Assert.True((await schedule.PublishAsync(new(Guid.NewGuid(), futureId, future.Version, "0123", actor))).Success);
        var futurePage = await client.GetStringAsync($"/Operations/Production?WeekId={futureId}&Through={futureDate:yyyy-MM-dd}");
        Assert.Contains(language == "en" ? "The future schedule" : "La programación futura", WebUtility.HtmlDecode(futurePage));
        await Fixture(language, "future", futurePage);
        await Fixture(language, "future-model", JsonSerializer.Serialize(await balances.GetDailySummaryAsync(futureId, new(futureDate)), JsonSerializerOptions.Web), "json");
        var currentWeek = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId);
        foreach (var status in new[] { ProductionScheduleWeekStatus.Closed, ProductionScheduleWeekStatus.Draft })
        {
            currentWeek.Status = status;
            await db.SaveChangesAsync();
            var page = await client.GetStringAsync(url);
            Assert.DoesNotContain("data-edit-area=", page);
            Assert.Contains("data-editable=\"false\"", page);
            await Fixture(language, status == ProductionScheduleWeekStatus.Closed ? "closed" : "draft", page);
        }
        currentWeek.Status = ProductionScheduleWeekStatus.Open;
        var configuration = await db.ProductionDailyConfigurations.SingleAsync();
        configuration.Shift2Id = null;
        await db.SaveChangesAsync();
        var missing = await client.GetStringAsync(url);
        Assert.DoesNotContain("data-edit-area=", missing);
        await Fixture(language, "configuration", missing);
        Assert.False(await db.ProductionDailyCaptures.AnyAsync());
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Empty_balance_preserves_the_week_selector_without_an_editor(string language)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        var page = await client.GetStringAsync("/Operations/Production?Tab=balance");
        Assert.Equal(1, Regex.Count(page, "name=\"WeekId\""));
        Assert.DoesNotContain("id=\"balance-editor\"", page);
        await Fixture(language, "empty", page);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Balance_keeps_negative_saldos_non_applicable_areas_and_sub_complete_percentages(string language)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var configuration = await db.ProductionDailyConfigurations.SingleAsync();
        var actor = (await db.Users.SingleAsync(x => x.FullName == "Capture recovery operator")).Id;
        var carry = new Product { Sku = "ONLY-PACK-CARRY", BaseUnitId = 1 };
        db.Products.Add(carry);
        db.ProductionScheduleLines.Add(new()
        {
            WeekId = seed.WeekId,
            ProductId = carry.Id,
            Sequence = 4,
            PlannedDate = seed.Date,
            Quantity = 12.5m,
            IsCarryover = true,
            StartArea = ProductionDailyArea.ReadyToPack
        });
        foreach (var (area, quantity, stage) in new[] { (ProductionDailyArea.Cutting, 40m, configuration.CuttingStageId), (ProductionDailyArea.ReadyToPack, 29.997m, configuration.ReadyToPackStageId) })
        {
            db.ProductionDailyCaptures.Add(new()
            {
                WeekId = seed.WeekId,
                ProductId = seed.Products[0],
                Area = area,
                Quantity = quantity,
                EffectiveDate = seed.Date,
                ShiftId = configuration.Shift1Id!.Value,
                StageId = stage!.Value,
                OperationId = Guid.NewGuid(),
                RequestFingerprint = "F".PadLeft(64, 'F'),
                ResponsibleUserId = actor,
                RecordedAt = DateTimeOffset.UtcNow,
                Status = ProductionDailyCaptureStatus.Active
            });
        }
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        var page = await client.GetStringAsync($"/Operations/Production?WeekId={seed.WeekId}&Through={seed.Date:yyyy-MM-dd}");
        var summary = (await scope.ServiceProvider.GetRequiredService<ProductionDailyBalanceService>()
            .GetDailySummaryAsync(seed.WeekId, new(seed.Date)))!;
        var produced = summary.Products.Single(x => x.ProductId == seed.Products[0]);
        Assert.Equal(-10m, produced.Cutting.SignedPending);
        Assert.Equal(.9999m, produced.StatusRatio);
        var carried = summary.Products.Single(x => x.ProductId == carry.Id);
        Assert.False(carried.Cutting.Applies);
        Assert.False(carried.Sewing.Applies);
        Assert.True(carried.ReadyToPack.Applies);
        Assert.Contains("data-status-band=\"mid\"", page);
        Assert.Contains("99.9", page);
        Assert.Contains(language == "en" ? "Not applicable" : "No aplica", WebUtility.HtmlDecode(page));
        Assert.Contains("data-balance-field=\"netPending\" data-area=\"0\">-10", page);
        await Fixture(language, "edges", page);
        await Fixture(language, "edges-model", JsonSerializer.Serialize(summary, JsonSerializerOptions.Web), "json");
    }

    private static async Task Fixture(string language, string name, string content, string extension = "html")
    {
        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_BALANCE_TABLET_FIXTURES");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{name}-{language}.{extension}"), content);
    }
}
