using System.Net;
using System.Net.Http.Json;
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

public sealed class ProductionScheduleCarryoverRouteTests
{
    [Fact]
    public async Task Open_copy_reviews_and_saves_carryover_in_the_workspace_and_renders_one_controller()
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Guid sourceId, targetId, productId, actorId;
        var monday = new DateOnly(2026, 9, 21);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
            var actor = new User { FullName = "Copy route admin", RoleId = 1, PinLookup = "", PinHash = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(actor, "0123");
            db.Users.Add(actor); await db.SaveChangesAsync(); actorId = actor.Id;
            productId = (await db.Products.SingleAsync(x => x.Sku == "FG-100")).Id;
            var schedule = scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
            sourceId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), monday.AddDays(-7), actor.Id))).Id!.Value;
            var source = (await schedule.GetWeekAsync(sourceId))!;
            Assert.True((await schedule.SaveLineAsync(new(Guid.NewGuid(), sourceId, null, source.Version, null,
                source.WeekStart, productId, 100, null, null, null, null, actor.Id))).Success);
            source = (await schedule.GetWeekAsync(sourceId))!;
            Assert.True((await schedule.PublishAsync(new(Guid.NewGuid(), sourceId, source.Version, "0123", actor.Id))).Success);
            var config = await db.ProductionDailyConfigurations.SingleAsync();
            db.ProductionDailyCaptures.Add(new()
            {
                WeekId = sourceId,
                ProductId = productId,
                Quantity = 100,
                Area = ProductionDailyArea.Cutting,
                StageId = config.CuttingStageId!.Value,
                ShiftId = config.Shift1Id!.Value,
                EffectiveDate = source.WeekStart,
                ResponsibleUserId = actor.Id,
                RequestFingerprint = "copy-route",
                OperationId = Guid.NewGuid(),
                IsFlexible = true
            });
            await db.SaveChangesAsync();
            targetId = (await schedule.CreateWeekAsync(new(Guid.NewGuid(), monday, actor.Id))).Id!.Value;
            var target = (await schedule.GetWeekAsync(targetId))!;
            Assert.True((await schedule.PublishAsync(new(Guid.NewGuid(), targetId, target.Version, "0123", actor.Id))).Success);
        }
        var login = await client.GetStringAsync("/Admin/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = Token(login) }))).StatusCode);
        var route = $"/Admin/Production/Schedule?WeekId={targetId}";
        var html = await client.GetStringAsync(route);
        Assert.True(Regex.Count(html, "data-opening-editor") == 1);
        Assert.Contains("data-integrated=\"true\"", html);
        Assert.Contains("data-workspace-review-url", html);
        Assert.Contains("data-workspace-clear-all hidden>Limpiar todo", html);
        Assert.DoesNotContain("data-workspace-discard-preparation", html);
        Assert.Contains("data-program-url=", html);
        Assert.DoesNotContain("data-opening-review>", html);
        Assert.DoesNotContain("data-opening-rows", html);
        Assert.DoesNotContain("data-workspace-paste-preview", html);
        Assert.DoesNotContain("Detalle diario y acciones por renglón", html);
        Assert.Equal(1, Regex.Count(html, "<table\\b"));
        Assert.Contains("colspan=\"3\" scope=\"colgroup\">Arrastre inicial", html);
        var workspaceStart = html.IndexOf("data-week-workspace", StringComparison.Ordinal);
        Assert.True(html.IndexOf("data-opening-editor", StringComparison.Ordinal) > workspaceStart);
        var options = (await client.GetFromJsonAsync<ProductionOpeningOption[]>($"/Admin/Production/Schedule?handler=WorkspaceOpenings&weekId={targetId}"))!;
        Assert.Equal(2, options.Length);
        if (Environment.GetEnvironmentVariable("WAREHOUSE_COPY_CARRYOVER_FIXTURES") is string fixtureDirectory)
        {
            Directory.CreateDirectory(fixtureDirectory);
            await File.WriteAllTextAsync(Path.Combine(fixtureDirectory, "before.html"),
                Regex.Replace(html, "(name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]*", "$1"));
            await File.WriteAllTextAsync(Path.Combine(fixtureDirectory, "openings.json"), JsonSerializer.Serialize(options, JsonSerializerOptions.Web));
            await File.WriteAllTextAsync(Path.Combine(fixtureDirectory, "source.json"), await client.GetStringAsync(
                $"/Admin/Production/Schedule?handler=WorkspaceCopy&weekId={targetId}&sourceWeekId={sourceId}"));
            client.DefaultRequestHeaders.Add("Cookie", "WarehouseEPI.Language=en");
            var english = await client.GetStringAsync(route);
            Assert.Contains("Adjust the orders: total", english);
            Assert.Contains("data-workspace-clear-all hidden>Clear all", english);
            Assert.Contains("&quot;Rengl", english);
            await File.WriteAllTextAsync(Path.Combine(fixtureDirectory, "before-en.html"),
                Regex.Replace(english, "(name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]*", "$1"));
            client.DefaultRequestHeaders.Remove("Cookie");
        }
        var input = new ScheduleModelInput(Guid.NewGuid(), targetId, 1,
            [new("add", null, null, new(monday, productId, 50, null, null, null, null))],
            options.Select(x => new ProductionOpeningChange(x.SourceWeekId, x.SourceLineId, x.Area, x.Available, x.Fingerprint)).ToArray(),
            "Pendientes de la semana elegida", "");
        using (var scope = factory.Services.CreateScope())
            input = input with { ExpectedWeekVersion = (await scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>().GetWeekAsync(targetId))!.Version };
        var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/Production/Schedule?handler=WorkspaceReview") { Content = JsonContent.Create(input) };
        request.Headers.Add("RequestVerificationToken", Token(html));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var review = (await response.Content.ReadFromJsonAsync<ProductionScheduleWorkspaceReview>())!;
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        input = input with { ReviewedFingerprint = review.Fingerprint };
        async Task<HttpResponseMessage> Save(string pin) => await client.PostAsync("/Admin/Production/Schedule?handler=WorkspaceSave",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["payload"] = JsonSerializer.Serialize(input, JsonSerializerOptions.Web),
                ["adminPin"] = pin,
                ["__RequestVerificationToken"] = Token(html)
            }));
        Assert.Equal(HttpStatusCode.BadRequest, (await Save("")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Save("0123")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Save("")).StatusCode);
        html = await client.GetStringAsync(route);
        Assert.Contains("&quot;openings&quot;:[{&quot;area&quot;:1,&quot;quantity&quot;:100},{&quot;area&quot;:2,&quot;quantity&quot;:100}]", html);
        if (Environment.GetEnvironmentVariable("WAREHOUSE_COPY_CARRYOVER_FIXTURES") is string directory)
        {
            Directory.CreateDirectory(directory);
            html = Regex.Replace(html, "(name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]*", "$1");
            await File.WriteAllTextAsync(Path.Combine(directory, "saved.html"), html);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<ProductionDailyScheduleService>();
            var week = (await service.GetWeekAsync(targetId))!;
            Assert.Single(week.Lines); Assert.Equal(2, await db.ProductionWeekOpenings.CountAsync());
            foreach (var action in new[] { "EditLineId", "DeleteLineId" })
            {
                var actionHtml = await client.GetStringAsync($"{route}&SelectedDay={monday:yyyy-MM-dd}&{action}={week.Lines[0].Id}");
                Assert.Contains($"data-focus-line=\"{week.Lines[0].Id}\"", actionHtml);
                Assert.Equal(7, Regex.Count(actionHtml, "data-workspace-day-head="));
                Assert.Contains($"data-request-delete=\"{(action == "DeleteLineId" ? "true" : "false")}\"", actionHtml);
                Assert.Equal(1, Regex.Count(actionHtml, "<table\\b"));
                Assert.DoesNotContain("name=\"Input.LineId\"", actionHtml);
            }
            Assert.Contains("data-request-add=\"true\"", await client.GetStringAsync($"{route}&AddLine=true"));
            Assert.True((await service.CloseAsync(new(Guid.NewGuid(), targetId, week.Version, actorId))).Success);
        }
        input = input with { OperationId = Guid.NewGuid() };
        Assert.Equal(HttpStatusCode.BadRequest, (await Save("0123")).StatusCode);
        var closed = await client.GetStringAsync(route);
        Assert.Contains("data-readonly=\"true\"", closed);
        Assert.DoesNotContain("data-workspace-clear-all", closed);
        Assert.Equal(1, Regex.Count(closed, "data-opening-editor"));
        Assert.Equal(1, Regex.Count(closed, "<table\\b"));
        if (Environment.GetEnvironmentVariable("WAREHOUSE_COPY_CARRYOVER_FIXTURES") is string closedDirectory)
            await File.WriteAllTextAsync(Path.Combine(closedDirectory, "closed.html"),
                Regex.Replace(closed, "(name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]*", "$1"));
        Guid historicalId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var historical = new ProductionScheduleWeek
            {
                WeekStart = new(2026, 8, 31),
                WeekEnd = new(2026, 9, 6),
                Status = ProductionScheduleWeekStatus.Closed,
                ExplicitCarryover = false,
                CreatedByUserId = actorId,
                OperationId = Guid.NewGuid(),
                RequestFingerprint = "historical-alignment-route"
            };
            historical.Lines.Add(new()
            {
                ProductId = productId,
                PlannedDate = new(2026, 9, 2),
                Quantity = 100,
                Sequence = 1,
                OrderReference1 = "HISTORICAL-ORDER"
            });
            db.Add(historical); await db.SaveChangesAsync(); historicalId = historical.Id;
        }
        var historicalHtml = await client.GetStringAsync($"/Admin/Production/Schedule?WeekId={historicalId}");
        Assert.Contains("data-readonly=\"true\"", historicalHtml);
        Assert.Equal(1, Regex.Count(historicalHtml, "data-opening-editor"));
        Assert.Contains("data-totals=\"true\"", historicalHtml);
        Assert.Equal(1, Regex.Count(historicalHtml, "<table\\b"));
        if (Environment.GetEnvironmentVariable("WAREHOUSE_COPY_CARRYOVER_FIXTURES") is string historicalDirectory)
            await File.WriteAllTextAsync(Path.Combine(historicalDirectory, "historical-closed.html"),
                Regex.Replace(historicalHtml, "(name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]*", "$1"));
    }

    private static string Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    private sealed record ScheduleModelInput(Guid OperationId, Guid WeekId, uint ExpectedWeekVersion,
        ProductionScheduleDraftChange[] Changes, ProductionOpeningChange[] Openings, string Reason, string ReviewedFingerprint);
}
