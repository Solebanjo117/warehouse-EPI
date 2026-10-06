using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Production;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionScheduleWorkspaceRouteTests
{
    [Fact]
    public async Task Saved_later_captures_recover_an_earlier_day_in_the_schedule_page()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Guid weekId, productId, actorId, cuttingStageId, shift2Id;
        var monday = new DateOnly(2026, 9, 21);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
            var config = await db.ProductionDailyConfigurations.SingleAsync();
            var actor = new User
            {
                FullName = "Recovery admin",
                RoleId =
                (await db.Roles.SingleAsync(x => x.Code == "ADMIN")).Id,
                PinLookup = "",
                PinHash = ""
            };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(actor, "0123");
            var week = new ProductionScheduleWeek
            {
                WeekStart = monday,
                WeekEnd = monday.AddDays(6),
                Status = ProductionScheduleWeekStatus.Open,
                ExplicitCarryover = true,
                CreatedByUser = actor,
                RequestFingerprint = "recovery-route"
            };
            foreach (var (day, quantity) in new[] { (0, 200m), (1, 400m), (2, 400m) })
                week.Lines.Add(new()
                {
                    Product = product,
                    PlannedDate = monday.AddDays(day),
                    Quantity = quantity,
                    Sequence = day + 1
                });
            foreach (var (day, quantity, shift2) in new[]
                { (0, 400m, false), (1, 200m, true), (3, 55m, false), (4, 40m, true) })
                week.Captures.Add(new()
                {
                    Product = product,
                    ResponsibleUser = actor,
                    EffectiveDate = monday.AddDays(day),
                    Area = ProductionDailyArea.Cutting,
                    StageId = config.CuttingStageId!.Value,
                    ShiftId = (shift2 ? config.Shift2Id : config.Shift1Id)!.Value,
                    Quantity = quantity,
                    OperationId = Guid.NewGuid(),
                    RequestFingerprint = "recovery-route",
                    Status = ProductionDailyCaptureStatus.Active
                });
            db.Add(week); await db.SaveChangesAsync();
            weekId = week.Id; productId = product.Id; actorId = actor.Id;
            cuttingStageId = config.CuttingStageId!.Value; shift2Id = config.Shift2Id!.Value;
        }
        var login = await client.GetStringAsync("/Admin/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Admin/Login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = Token(login) }))).StatusCode);
        var route = $"/Admin/Production/Schedule?WeekId={weekId}&SelectedDay=2026-09-23";
        var before = await client.GetStringAsync(route);
        Assert.Contains("\"required\":400,\"covered\":95", WebUtility.HtmlDecode(before));
        var fixturePath = Environment.GetEnvironmentVariable("WAREHOUSE_SCHEDULE_PROGRESS_FIXTURES");
        if (!string.IsNullOrWhiteSpace(fixturePath))
        {
            Directory.CreateDirectory(fixturePath);
            await File.WriteAllTextAsync(Path.Combine(fixturePath, "recovery-before.html"), before);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            db.ProductionDailyCaptures.Add(new()
            {
                WeekId = weekId,
                ProductId = productId,
                ResponsibleUserId = actorId,
                EffectiveDate = monday.AddDays(4),
                Area = ProductionDailyArea.Cutting,
                StageId = cuttingStageId,
                ShiftId = shift2Id,
                Quantity = 305,
                OperationId = Guid.NewGuid(),
                RequestFingerprint = "recovery-route",
                Status = ProductionDailyCaptureStatus.Active
            });
            await db.SaveChangesAsync();
        }
        var after = await client.GetStringAsync(route);
        Assert.Contains("\"required\":400,\"covered\":400", WebUtility.HtmlDecode(after));
        if (!string.IsNullOrWhiteSpace(fixturePath))
            await File.WriteAllTextAsync(Path.Combine(fixturePath, "recovery-after.html"), after);
    }

    [Fact]
    public async Task Draft_workspace_requires_admin_and_saves_a_reviewed_group_idempotently()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Guid weekId, sourceId, productId;
        var monday = new DateOnly(2026, 9, 21);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var user = new User
            {
                FullName = "Workspace admin",
                RoleId =
                (await db.Roles.SingleAsync(x => x.Code == "ADMIN")).Id,
                PinLookup = "",
                PinHash = ""
            };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.Users.Add(user); await db.SaveChangesAsync();
            productId = (await db.Products.SingleAsync(x => x.Sku == "FG-100")).Id;
            var schedule = scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
            sourceId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), monday.AddDays(-7), user.Id))).Id!.Value;
            var source = (await schedule.GetWeekAsync(sourceId))!;
            Assert.True((await schedule.SaveLineAsync(new(Guid.NewGuid(), sourceId, null, source.Version,
                null, monday.AddDays(-7), productId, 40, "OLD-ORDER", "ORDER-2", "ORDER-3", null, user.Id))).Success);
            source = (await schedule.GetWeekAsync(sourceId))!;
            Assert.True((await schedule.PublishAsync(new(Guid.NewGuid(), sourceId, source.Version, "0123", user.Id))).Success);
            weekId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), monday, user.Id))).Id!.Value;
        }
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/Admin/Production/Schedule?WeekId={weekId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/Admin/Production/Schedule?handler=WorkspaceOpenings&weekId={weekId}")).StatusCode);
        var login = await client.GetStringAsync("/Admin/Login");
        var token = Token(login);
        var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var page = await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={weekId}");
        Assert.Contains("data-week-workspace", page);
        Assert.Contains("data-workspace-scroll-top", page);
        Assert.Contains("data-workspace-scroll-width", page);
        Assert.Contains("data-workspace-scroll>", page);
        Assert.Contains("data-workspace-review", page);
        Assert.Contains("data-workspace-clear-all hidden>Limpiar todo", page);
        Assert.DoesNotContain("data-workspace-discard-preparation", page);
        Assert.Contains("data-opening-editor", page);
        Assert.DoesNotContain("data-opening-panel", page);
        Assert.DoesNotContain("data-opening-rows", page);
        Assert.Contains("data-workspace-copy-products", page);
        Assert.Contains("data-workspace-copy-openings", page);
        Assert.DoesNotContain("data-workspace-opening-preview", page);
        Assert.Contains("/js/production-week-openings.", page);
        var optionsJson = await client.GetStringAsync($"/Admin/Production/Schedule?handler=WorkspaceOpenings&weekId={weekId}");
        var opening = JsonSerializer.Deserialize<ProductionOpeningOption[]>(optionsJson, JsonSerializerOptions.Web)!
            .Single(x => x.Area == ProductionDailyArea.Cutting);
        Assert.Equal(40, opening.Available); Assert.Equal(0, opening.Selected);
        Assert.Contains("data-workspace-copy-toggle aria-expanded=\"false\" aria-controls=\"workspace-copy\"", page);
        Assert.Contains("data-workspace-copy-panel hidden", page);
        Assert.Contains("data-resolve-products-url", page);
        Assert.DoesNotContain("data-workspace-paste", page);
        Assert.DoesNotContain("/js/production-week-paste.", page);
        Assert.DoesNotContain("data-workspace-paste-preview", page);
        Assert.True(Regex.Count(page, "<table\\b") == 1);
        Assert.DoesNotContain("Días de la semana", page);
        Assert.Contains("data-workspace-program-head", page);
        Assert.Equal(1, Regex.Count(page, "id=\"week-workspace-quantity-label\""));
        Assert.Contains("/js/production-week-workspace.", page);
        var copied = await client.GetStringAsync($"/Admin/Production/Schedule?handler=WorkspaceCopy&weekId={weekId}&sourceWeekId={sourceId}");
        Assert.Contains("\"quantity\":40", copied);
        Assert.Contains("OLD-ORDER", copied);
        Assert.Contains("ORDER-2", copied);
        Assert.Contains("ORDER-3", copied);
        var operationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            operationId,
            weekId,
            expectedWeekVersion = 0,
            openings = new[] { new { sourceWeekId = sourceId, sourceLineId = opening.SourceLineId, area = 0, quantity = "10", expectedFingerprint = opening.Fingerprint } },
            changes = new[] { new
            {
                kind = "add", lineId = (Guid?)null, expectedLineVersion = (uint?)null,
                line = new { plannedDate = monday.ToString("yyyy-MM-dd"), productId, quantity = "20",
                    orderReference1 = "ORDER-1", orderReference2 = (string?)null,
                    orderReference3 = (string?)null, notes = (string?)null }
            } }
        });
        async Task<HttpResponseMessage> Save(string body) => await client.PostAsync(
            "/Admin/Production/Schedule?handler=WorkspaceSave",
            new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = Token(page), ["payload"] = body }));
        Assert.Equal(HttpStatusCode.OK, (await Save(payload)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Save(payload)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var week = (await scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>()
                .GetWeekAsync(weekId))!;
            Assert.Single(week.Lines);
            Assert.Equal(20, week.Lines[0].Quantity);
            Assert.Equal("ORDER-1", week.Lines[0].OrderReference1);
            Assert.Null(week.Lines[0].WorkOrderId);
            Assert.Equal(10, (await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionWeekOpenings.SingleAsync()).Quantity);
        }
        var status = await client.GetStringAsync($"/Admin/Production/Schedule?handler=WorkspaceOperation&weekId={weekId}&operationId={operationId}");
        Assert.Contains("\"saved\":true", status);
        var stale = payload.Replace(operationId.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await Save(stale)).StatusCode);
        uint currentVersion;
        using (var scope = factory.Services.CreateScope())
            currentVersion = (await scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>().GetWeekAsync(weekId))!.Version;
        var totalRequest = new
        {
            operationId = Guid.NewGuid(),
            weekId,
            expectedWeekVersion = currentVersion,
            changes = Array.Empty<object>(),
            skuTotals = new[] { new { plannedDate = monday.ToString("yyyy-MM-dd"), productId,
                quantity = "35", orderReference1 = "PACK-1", orderReference2 = "PACK-2", orderReference3 = "PACK-3", notes = "packing" } }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/Production/Schedule?handler=WorkspaceReview")
        { Content = new StringContent(JsonSerializer.Serialize(totalRequest), System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("RequestVerificationToken", Token(page));
        var reviewResponse = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, reviewResponse.StatusCode);
        using var reviewJson = JsonDocument.Parse(await reviewResponse.Content.ReadAsStringAsync());
        Assert.True(reviewJson.RootElement.GetProperty("canConfirm").GetBoolean());
        var totalsPayload = JsonSerializer.Serialize(new
        {
            totalRequest.operationId,
            totalRequest.weekId,
            totalRequest.expectedWeekVersion,
            totalRequest.changes,
            totalRequest.skuTotals,
            reviewedFingerprint = reviewJson.RootElement.GetProperty("fingerprint").GetString()
        });
        Assert.Equal(HttpStatusCode.OK, (await Save(totalsPayload)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Save(totalsPayload)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var line = Assert.Single((await scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>().GetWeekAsync(weekId))!.Lines);
            Assert.Equal(35, line.Quantity); Assert.Equal("PACK-3", line.OrderReference3);
        }
    }

    [Fact]
    public async Task Open_workspace_edits_with_progress_and_closed_workspace_rejects_writes()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Guid weekId, actorId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            weekId = await ProductionScheduleProgressTests.VerifyProjectionAsync(db);
            var user = new User { FullName = "Progress admin", RoleId = 1, PinLookup = "", PinHash = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.Users.Add(user); await db.SaveChangesAsync(); actorId = user.Id;
        }
        var route = $"/Admin/Production/Schedule?WeekId={weekId}&SelectedDay=2026-09-22";
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(route)).StatusCode);
        var login = await client.GetStringAsync("/Admin/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = Token(login) }))).StatusCode);
        var page = await client.GetStringAsync(route);
        Assert.Contains("data-week-status=\"Open\"", page);
        Assert.Contains("data-workspace-scroll-top", page);
        Assert.Contains("data-workspace-form", page); Assert.Equal(7, Regex.Count(page, "data-workspace-day-head="));
        Assert.Contains("production-schedule-sections", page);
        Assert.Contains("Forma de agregar productos", page);
        Assert.Contains("data-workspace-copy-toggle aria-expanded=\"false\"", page);
        Assert.DoesNotContain("data-workspace-mode", page);
        Assert.DoesNotContain("data-workspace-paste", page);
        Assert.Contains("ScheduleImport", page);
        Assert.DoesNotContain("data-workspace-view=", page);
        Assert.Contains("SelectedDay=2026-09-22", WebUtility.HtmlDecode(page));
        Assert.Contains("\"percent\":125", WebUtility.HtmlDecode(page));
        Assert.Contains("\"target\":350", WebUtility.HtmlDecode(page));
        Assert.Contains("\"coverage\":{\"required\":200,\"covered\":200", WebUtility.HtmlDecode(page));
        Assert.Contains("/js/production-schedule-progress.", page);
        var fixturePath = Environment.GetEnvironmentVariable("WAREHOUSE_SCHEDULE_PROGRESS_FIXTURES");
        if (!string.IsNullOrWhiteSpace(fixturePath))
        {
            Directory.CreateDirectory(fixturePath); await File.WriteAllTextAsync(Path.Combine(fixturePath, "open.html"), page);
        }
        ProductionScheduleWeekView week;
        using (var scope = factory.Services.CreateScope())
            week = (await scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>().GetWeekAsync(weekId))!;
        var line = week.Lines.First();
        var operationId = Guid.NewGuid();
        string Payload(Guid operation, uint version) => JsonSerializer.Serialize(new
        {
            operationId = operation,
            weekId,
            expectedWeekVersion = version,
            changes = new[] { new { kind = "add", lineId = (Guid?)null,
                expectedLineVersion = (uint?)null, line = new { plannedDate = "2026-09-23", productId = line.ProductId,
                    quantity = "10", notes = "New daily plan" } } }
        });
        async Task<HttpResponseMessage> Save(string payload, string pin) => await client.PostAsync(
            "/Admin/Production/Schedule?handler=WorkspaceSave", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["payload"] = payload, ["adminPin"] = pin, ["__RequestVerificationToken"] = Token(page) }));
        var payload = Payload(operationId, week.Version);
        var noPin = await Save(payload, ""); Assert.Equal(HttpStatusCode.BadRequest, noPin.StatusCode);
        Assert.Contains("InvalidPin", await noPin.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Save(payload, "0123")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Save(payload, "")).StatusCode);
        Assert.Contains("\"saved\":true", await client.GetStringAsync($"/Admin/Production/Schedule?handler=WorkspaceOperation&weekId={weekId}&operationId={operationId}"));
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
            week = (await service.GetWeekAsync(weekId))!;
            Assert.Equal(6, week.Lines.Count);
            Assert.True((await service.CloseAsync(new(Guid.NewGuid(), weekId, week.Version, actorId))).Success);
            week = (await service.GetWeekAsync(weekId))!;
        }
        page = await client.GetStringAsync(route);
        Assert.Contains("data-week-status=\"Closed\"", page); Assert.Contains("data-readonly=\"true\"", page);
        Assert.Contains("data-workspace-scroll-top", page);
        Assert.DoesNotContain("data-workspace-form", page); Assert.DoesNotContain("data-workspace-search", page);
        Assert.Contains("data-workspace-filter", page);
        Assert.Contains("aria-controls=\"week-workspace-rows\"", page);
        Assert.Contains("Buscar SKU en esta semana", page);
        Assert.DoesNotContain("data-workspace-copy-toggle", page);
        Assert.DoesNotContain("Forma de agregar productos", page);
        Assert.DoesNotContain("data-workspace-view=", page);
        Assert.Equal(HttpStatusCode.BadRequest, (await Save(Payload(Guid.NewGuid(), week.Version), "0123")).StatusCode);
        if (!string.IsNullOrWhiteSpace(fixturePath)) await File.WriteAllTextAsync(Path.Combine(fixturePath, "closed.html"), page);
        client.DefaultRequestHeaders.Add("Cookie", "WarehouseEPI.Language=en");
        var english = await client.GetStringAsync(route);
        Assert.Contains("Search SKU in this week", english);
        Assert.Contains("Showing {0} of {1} SKUs.", english);
        if (!string.IsNullOrWhiteSpace(fixturePath)) await File.WriteAllTextAsync(Path.Combine(fixturePath, "closed-en.html"), english);
    }

    private static string Token(string html) => Regex.Match(html,
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
}
