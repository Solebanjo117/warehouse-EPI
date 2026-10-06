using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using static WarehouseEPI.Tests.Web.ProductionCaptureRecoveryTests;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionBalanceWorkspaceTests
{
    [Fact]
    public async Task Planned_quantity_is_read_only_including_admin_sessions_and_future_days()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var date = new DateOnly(2099, 9, 21);
        date = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId);
        week.WeekStart = date; week.WeekEnd = date.AddDays(6);
        foreach (var line in await db.ProductionScheduleLines.Where(x => x.WeekId == week.Id).ToListAsync()) line.PlannedDate = date;
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var url = $"/Operations/Production?WeekId={seed.WeekId}&Through={date:yyyy-MM-dd}";
        var anonymous = await client.GetStringAsync(url);
        Assert.DoesNotContain("data-plan-line=", anonymous);
        Assert.DoesNotContain("data-new-plan=", anonymous);
        var login = await client.GetStringAsync("/Admin/Login");
        using var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Input(login, "__RequestVerificationToken"),
            ["Input.Pin"] = "0123",
            ["ReturnUrl"] = url
        }));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var admin = WebUtility.HtmlDecode(await client.GetStringAsync(url));
        Assert.Contains("data-balance-plan>30</span>", admin);
        Assert.DoesNotContain("data-plan-line=", admin);
        Assert.DoesNotContain("data-new-plan=", admin);
        Assert.Contains("data-edit-shift=\"1\"", admin);
        Assert.Contains("data-edit-shift=\"2\"", admin);
        var unplanned = await client.GetStringAsync($"/Operations/Production?WeekId={seed.WeekId}&Through={date.AddDays(1):yyyy-MM-dd}");
        Assert.Contains("data-balance-plan>0</span>", unplanned);
        Assert.DoesNotContain("data-new-plan=", unplanned);
        Assert.DoesNotContain("Edición administrativa", admin);
        Assert.False(await db.ProductionDailyCaptures.AnyAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Balance_rejects_planning_even_for_admin_and_mixed_production_requests(bool newPlan, bool withProduction)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var login = await client.GetStringAsync("/Admin/Login");
        using var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Input(login, "__RequestVerificationToken"),
            ["Input.Pin"] = "0123"
        }));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var page = await client.GetStringAsync($"/Operations/Production?Day={seed.Date:yyyy-MM-dd}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var line = await db.ProductionScheduleLines.AsNoTracking().FirstAsync(x => x.WeekId == seed.WeekId);
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleAsync(x => x.Id == seed.WeekId);
        var edit = new
        {
            operationId = Guid.NewGuid(),
            weekId = seed.WeekId,
            date = seed.Date,
            pin = "0123",
            cells = withProduction ? new object[] { new { productId = seed.Products[0], area = 0, shift = 1, observed = "0", requested = "5" } } : [],
            planChanges = newPlan ? Array.Empty<object>() : [new { lineId = line.Id, observed = "30", requested = "40", expectedLineVersion = line.Version, expectedWeekVersion = week.Version }],
            newPlans = newPlan ? new object[] { new { operationId = Guid.NewGuid(), productId = seed.Products[0], requested = "10", expectedWeekVersion = week.Version } } : []
        };
        async Task<HttpResponseMessage> Send(string handler, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/Operations/Production?handler={handler}");
            request.Headers.Add("RequestVerificationToken", Input(page, "__RequestVerificationToken"));
            request.Content = JsonContent.Create(body);
            return await client.SendAsync(request);
        }
        using var preview = await Send("BalanceEditPreview", edit);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var review = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.False(review.RootElement.GetProperty("canConfirm").GetBoolean());
        Assert.Contains("Programa semanal", review.RootElement.GetProperty("errors")[0].GetString());
        using var confirmation = await Send("BalanceEditConfirm", edit);
        Assert.Equal(HttpStatusCode.BadRequest, confirmation.StatusCode);
        using var restore = await Send("BalanceEditRestore", new { edit, products = seed.Products });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        using var recovered = JsonDocument.Parse(await restore.Content.ReadAsStringAsync());
        Assert.NotEqual(edit.operationId, recovered.RootElement.GetProperty("operationId").GetGuid());
        Assert.Equal(withProduction ? 1 : 0, recovered.RootElement.GetProperty("cells").GetArrayLength());
        Assert.False(await db.ProductionDailyCaptures.AnyAsync());
        Assert.False(await db.Set<ProductionBalanceEdit>().AnyAsync());
        Assert.Equal(3, await db.ProductionScheduleLines.CountAsync(x => x.WeekId == seed.WeekId));
        Assert.All(await db.ProductionScheduleLines.Where(x => x.WeekId == seed.WeekId).ToArrayAsync(), x => Assert.Equal(30m, x.Quantity));
    }

    [Fact]
    public async Task Recovery_preserves_the_fingerprint_of_preexisting_balance_receipts()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var user = await db.Users.SingleAsync(x => x.FullName == "Capture recovery operator");
        var command = new ProductionBalanceEditCommand(Guid.NewGuid(), seed.WeekId, seed.Date,
            [new(seed.Products[0], ProductionDailyArea.Cutting, 1, 0, 5)]);
        // This is the serialized shape used before extracting the recovery helper.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            command.WeekId,
            command.Date,
            Cells = command.Cells.OrderBy(x => x.Shift).ThenBy(x => x.Area).ThenBy(x => x.ProductId),
            Reason = command.Reason?.Trim(),
            user.Id
        }))));
        db.Set<ProductionBalanceEdit>().Add(new()
        {
            OperationId = command.OperationId,
            WeekId = seed.WeekId,
            EffectiveDate = seed.Date,
            RequestFingerprint = fingerprint,
            ResponsibleUserId = user.Id
        });
        await db.SaveChangesAsync();
        var result = await scope.ServiceProvider.GetRequiredService<ProductionDailyCaptureService>().FindBalanceEditAsync(command);
        Assert.True(result?.Success);
        Assert.False(await db.ProductionDailyCaptures.AnyAsync());
    }

    [Fact]
    public async Task Recovery_blocks_closed_weeks_and_inactive_products_and_keeps_legacy_planning_read_only()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var page = await client.GetStringAsync($"/Operations/Production?Day={seed.Date:yyyy-MM-dd}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        (await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == seed.WeekId)).Status = ProductionScheduleWeekStatus.Closed;
        (await db.Products.SingleAsync(x => x.Id == seed.Products[0])).IsActive = false;
        await db.SaveChangesAsync();
        async Task<HttpResponseMessage> Send(object edit, Guid[]? products = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/Operations/Production?handler=BalanceEditRestore");
            request.Headers.Add("RequestVerificationToken", Input(page, "__RequestVerificationToken"));
            request.Content = JsonContent.Create(new { edit, products = products ?? seed.Products });
            return await client.SendAsync(request);
        }
        var edit = new
        {
            operationId = Guid.NewGuid(),
            weekId = seed.WeekId,
            date = seed.Date,
            cells = new[] { new { productId = seed.Products[0], area = 0, shift = 1, observed = "0", requested = "bad" } }
        };
        using var response = await Send(edit);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("blocked").GetBoolean());
        Assert.True(result.RootElement.GetProperty("cells")[0].GetProperty("blocked").GetBoolean());
        Assert.Equal("bad", result.RootElement.GetProperty("cells")[0].GetProperty("cell").GetProperty("requested").GetString());
        using var duplicate = await Send(edit, [seed.Products[0], seed.Products[0]]);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var planning = await Send(new
        {
            operationId = Guid.NewGuid(),
            weekId = seed.WeekId,
            date = seed.Date,
            planChanges = new[] { new { lineId = Guid.NewGuid(), observed = "0", requested = "1", expectedLineVersion = 0, expectedWeekVersion = 0 } }
        });
        Assert.Equal(HttpStatusCode.OK, planning.StatusCode);
        using var planningResult = JsonDocument.Parse(await planning.Content.ReadAsStringAsync());
        Assert.DoesNotContain("data-plan-line=", planningResult.RootElement.GetProperty("html").GetString());
        Assert.DoesNotContain("data-new-plan=", planningResult.RootElement.GetProperty("html").GetString());
        Assert.False(await db.ProductionDailyCaptures.AnyAsync());
    }

    [Fact]
    public async Task Catalogue_rows_review_confirm_and_recover_lost_response_without_creating_a_plan()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var product = new Product { Sku = "UNPLANNED-TEST", Description = "Catalogue recovery test", BaseUnitId = 1 };
        db.Products.Add(product); await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var page = await client.GetStringAsync($"/Operations/Production?WeekId={seed.WeekId}&Through={seed.Date:yyyy-MM-dd}&Sku=absent");
        Assert.DoesNotContain("id=\"balance-product-search\"", page);
        Assert.Contains("id=\"balance-editor\"", page);
        var search = await client.GetStringAsync($"/Operations/Production?handler=BalanceProducts&date={seed.Date:yyyy-MM-dd}&text=Catalogue");
        Assert.Contains(product.Sku, search);
        using var rows = JsonDocument.Parse(await client.GetStringAsync($"/Operations/Production?handler=BalanceRows&weekId={seed.WeekId}&date={seed.Date:yyyy-MM-dd}&productId={product.Id}"));
        Assert.True(rows.RootElement.GetProperty("editable").GetBoolean());
        var html = rows.RootElement.GetProperty("html").GetString()!;
        if (Environment.GetEnvironmentVariable("WAREHOUSE_BALANCE_FIXTURES") is string directory)
        {
            Directory.CreateDirectory(directory);
            var balancePage = await client.GetStringAsync($"/Operations/Production?WeekId={seed.WeekId}&Through={seed.Date:yyyy-MM-dd}");
            Assert.DoesNotContain("data-balance-metric=", balancePage);
            await File.WriteAllTextAsync(Path.Combine(directory, "balance.html"), balancePage);
            await File.WriteAllTextAsync(Path.Combine(directory, "row.html"), html);
        }
        Assert.Contains("data-edit-area=\"2\"", html);
        Assert.Contains("data-edit-shift=\"2\"", html);
        Assert.False(await db.ProductionDailyCaptures.AnyAsync());
        Assert.False(await db.ProductionScheduleLines.AnyAsync(x => x.ProductId == product.Id));
        var operationId = Guid.NewGuid();
        object Edit(string fingerprint = "", string pin = "", string requested = "5") => new
        {
            operationId,
            weekId = seed.WeekId,
            date = seed.Date,
            fingerprint,
            pin,
            cells = Enumerable.Range(0, 3).Select(area => new { productId = product.Id, area, shift = 1, observed = "0", requested })
        };
        async Task<JsonDocument> Send(string handler, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/Operations/Production?handler={handler}");
            request.Headers.Add("RequestVerificationToken", Input(page, "__RequestVerificationToken"));
            request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        using var preview = await Send("BalanceEditPreview", Edit());
        Assert.True(preview.RootElement.GetProperty("canConfirm").GetBoolean(), preview.RootElement.ToString());
        var previewCutting = preview.RootElement.GetProperty("balance").GetProperty("products")[0].GetProperty("cutting");
        Assert.True(previewCutting.TryGetProperty("dailyCoverage", out _));
        Assert.True(previewCutting.TryGetProperty("accumulatedCoverage", out _));
        using var saved = await Send("BalanceEditConfirm", Edit(preview.RootElement.GetProperty("fingerprint").GetString()!, "0123"));
        Assert.True(saved.RootElement.GetProperty("success").GetBoolean(), saved.RootElement.ToString());
        Assert.Equal(operationId, saved.RootElement.GetProperty("operationId").GetGuid());
        var count = await db.ProductionDailyCaptures.CountAsync();
        using var recovered = await Send("BalanceEditRestore", new { edit = Edit(), products = new[] { product.Id } });
        Assert.True(recovered.RootElement.GetProperty("registered").GetBoolean());
        using var conflict = await Send("BalanceEditRestore", new { edit = Edit(requested: "6"), products = new[] { product.Id } });
        Assert.True(conflict.RootElement.GetProperty("conflict").GetBoolean());
        Assert.Equal(count, await db.ProductionDailyCaptures.CountAsync());
        Assert.False(await db.ProductionScheduleLines.AnyAsync(x => x.ProductId == product.Id && !x.IsExtra));
    }

    [Fact]
    public async Task Restore_keeps_invalid_text_detects_conflicts_and_does_not_write()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = Configure(original);
        var seed = await SeedAsync(factory.Services);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var page = await client.GetStringAsync($"/Operations/Production?Day={seed.Date:yyyy-MM-dd}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Operations/Production?handler=BalanceEditRestore");
        request.Headers.Add("RequestVerificationToken", Input(page, "__RequestVerificationToken"));
        var body = new
        {
            products = seed.Products,
            edit = new
            {
                operationId = Guid.NewGuid(),
                weekId = seed.WeekId,
                date = seed.Date,
                cells = new[] { new { productId = seed.Products[0], area = 0, shift = 1, observed = "7", requested = "1,2" } }
            }
        };
        request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cell = result.RootElement.GetProperty("cells")[0];
        Assert.True(cell.GetProperty("conflict").GetBoolean());
        Assert.Equal("0", cell.GetProperty("current").GetString());
        Assert.Equal("1,2", cell.GetProperty("cell").GetProperty("requested").GetString());
        using var denied = await client.PostAsJsonAsync("/Operations/Production?handler=BalanceEditRestore", body);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionDailyCaptures.AnyAsync());
    }
}
