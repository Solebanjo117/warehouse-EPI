using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductionDailyRouteTests
{
    [Theory]
    [InlineData("en", "Weekly schedule", "The week must start on Monday.")]
    [InlineData("es", "Programa semanal", "La semana debe iniciar en lunes.")]
    public async Task Admin_setup_and_week_creation_are_explicit_localized_and_preserve_failed_dates(
        string language, string title, string dateError)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new SundayClock());
                // Login deliberately uses wall-clock expiry; only the production calendar is frozen.
                services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
                    Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
                    options => options.TimeProvider = TimeProvider.System);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var role = await db.Roles.SingleAsync(x => x.Code == "ADMIN");
            var user = new User { FullName = "Daily test admin", RoleId = role.Id, PinLookup = "", PinHash = "" };
            await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
            db.AddRange(user, new ProductionStage { Code = "CUT", Name = "Cutting" }, new ProductionStage { Code = "SEW", Name = "Sewing" },
                new ProductionStage { Code = "RTP", Name = "Ready to Pack" }, new ProductionShift { Code = "T1", Name = "Z shift" }, new ProductionShift { Code = "T2", Name = "A shift" });
            await db.SaveChangesAsync();
        }
        var login = await client.GetStringAsync("/Admin/Login");
        var signedIn = await Post(client, "/Admin/Login", login, new() { ["Input.Pin"] = "0123" });
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Production/Schedule"));
        Assert.Contains(title, html, StringComparison.Ordinal);
        Assert.Contains("data-week-picker", html, StringComparison.Ordinal);
        Assert.Contains(language == "en" ? "Review and open week" : "Revisar y abrir semana", html, StringComparison.Ordinal);
        Assert.Matches("<fieldset[^>]*data-workspace-controls[^>]*disabled", html);
        Assert.Equal("2026-09-21", Input(html, "NewWeek.WeekStart")); // UTC Monday is still Sunday at the warehouse.
        Assert.DoesNotContain("data-schedule-week-select", html);
        Assert.True(Regex.IsMatch(Select(html, "Configuration.Shift1Id"), "<option(?=[^>]*selected=\"selected\")[^>]*>Z shift</option>"), Select(html, "Configuration.Shift1Id"));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            Assert.Empty(await db.ProductionScheduleWeeks.ToListAsync());
            Assert.Null((await db.ProductionDailyConfigurations.SingleAsync()).Shift1Id);
        }
        var invalid = await Post(client, "/Admin/Production/Schedule?handler=CreateWeek", html,
            new() { ["NewWeek.OperationId"] = Guid.NewGuid().ToString(), ["NewWeek.WeekStart"] = "2026-09-22", ["NewWeek.Pin"] = "0123" });
        var failedHtml = WebUtility.HtmlDecode(await invalid.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains(dateError, failedHtml, StringComparison.Ordinal);
        Assert.Empty(Input(failedHtml, "NewWeek.WeekStart"));
        Assert.DoesNotContain("value=\"2026-09-22\"", failedHtml);
        Assert.DoesNotContain("0001-01-01", failedHtml, StringComparison.Ordinal);
        var blocked = await Post(client, "/Admin/Production/Schedule?handler=CreateWeek", failedHtml,
            new() { ["NewWeek.OperationId"] = Guid.NewGuid().ToString(), ["NewWeek.WeekStart"] = "2026-09-21", ["NewWeek.Pin"] = "0123" });
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        using (var scope = factory.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionScheduleWeeks.ToListAsync());
        var blockedHtml = WebUtility.HtmlDecode(await blocked.Content.ReadAsStringAsync());
        var configuration = new Dictionary<string, string>
        {
            ["Configuration.OperationId"] = Input(blockedHtml, "Configuration.OperationId"),
            ["Configuration.ExpectedVersion"] = Input(blockedHtml, "Configuration.ExpectedVersion")
        };
        foreach (var field in new[] { "CuttingStageId", "SewingStageId", "ReadyToPackStageId", "Shift1Id", "Shift2Id" })
            configuration["Configuration." + field] = Regex.Match(Select(blockedHtml, "Configuration." + field), "<option(?=[^>]*selected=\"selected\")[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var configured = await Post(client, "/Admin/Production/Schedule?handler=Configure", blockedHtml, configuration);
        Assert.Equal(HttpStatusCode.Redirect, configured.StatusCode);
        var readyHtml = WebUtility.HtmlDecode(await client.GetStringAsync(configured.Headers.Location));
        Assert.DoesNotMatch("<button[^>]*disabled[^>]*>" + (language == "en" ? "Review and open week" : "Revisar y abrir semana"), readyHtml);
        var rejectedPin = await Post(client, "/Admin/Production/Schedule?handler=CreateWeek", readyHtml,
            new() { ["NewWeek.OperationId"] = Guid.NewGuid().ToString(), ["NewWeek.WeekStart"] = "2026-09-21", ["NewWeek.Pin"] = "0000" });
        Assert.Equal(HttpStatusCode.OK, rejectedPin.StatusCode);
        using (var scope = factory.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().ProductionScheduleWeeks.ToListAsync());
        var rejectedHtml = WebUtility.HtmlDecode(await rejectedPin.Content.ReadAsStringAsync());
        Assert.Equal(string.Empty, Input(rejectedHtml, "NewWeek.Pin"));
        var created = await Post(client, "/Admin/Production/Schedule?handler=CreateWeek", rejectedHtml,
            new() { ["NewWeek.OperationId"] = Guid.NewGuid().ToString(), ["NewWeek.WeekStart"] = "2026-09-21", ["NewWeek.Pin"] = "0123" });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Contains("WeekId=", created.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var selected = WebUtility.HtmlDecode(await client.GetStringAsync(created.Headers.Location));
        Assert.Matches("<option(?=[^>]*selected=\"selected\")[^>]*>21/09", Select(selected, "WeekId"));
        Assert.Contains("data-week-status=\"Open\"", selected);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var week = await db.ProductionScheduleWeeks.SingleAsync();
            Assert.Equal(ProductionScheduleWeekStatus.Open, week.Status);
            Assert.NotNull(week.PublishedAt);
            Assert.Single(await db.ProductionScheduleRevisions.Where(x => x.WeekId == week.Id && x.Action == "published").ToListAsync());
        }
        var import = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Production/ScheduleImport"));
        Assert.Contains(language == "en" ? "Import Excel" : "Importar Excel", import, StringComparison.Ordinal);
        using var upload = new MultipartFormDataContent
        {
            { new StringContent(Input(import, "__RequestVerificationToken")), "__RequestVerificationToken" },
            { new ByteArrayContent([1, 2, 3]), "Upload", "plan.xlsx" }
        };
        var invalidImport = await client.PostAsync("/Admin/Production/ScheduleImport?handler=Preview", upload);
        Assert.Equal(HttpStatusCode.OK, invalidImport.StatusCode);
        var invalidImportHtml = WebUtility.HtmlDecode(await invalidImport.Content.ReadAsStringAsync());
        Assert.Contains(language == "en" ? "The file is not a valid XLSX file or is damaged." : "El archivo no es un XLSX válido o está dañado.", invalidImportHtml, StringComparison.Ordinal);
        var menu = WebUtility.HtmlDecode(await client.GetStringAsync("/Modules/production"));
        Assert.DoesNotContain("/Operations/Production/Advanced", menu, StringComparison.Ordinal);
        var capture = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/Production?Tab=capture"));
        Assert.Contains("data-context-state=\"Open\"", capture);
        Assert.Contains("data-group-preview", capture);
        var shiftSelect = Select(capture, "ShiftId");
        Assert.True(shiftSelect.IndexOf("Z shift", StringComparison.Ordinal) < shiftSelect.IndexOf("A shift", StringComparison.Ordinal));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            (await db.ProductionShifts.SingleAsync(x => x.Code == "T2")).IsActive = false;
            await db.SaveChangesAsync();
        }
        var inactive = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/Production?Tab=capture"));
        Assert.Contains("data-context-state=\"ConfigurationIncomplete\"", inactive);
        Assert.DoesNotContain("data-group-preview", inactive);
        Assert.Contains("#daily-configuration", inactive, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "Contact the administrator to enable entry.", "The selected week no longer exists. Select another week.")]
    [InlineData("es", "Contacta al administrador para habilitar la captura.", "La semana seleccionada ya no existe. Selecciona otra semana.")]
    public async Task Missing_configuration_blocks_capture_and_unknown_week_is_explained(string language, string warning, string missing)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?Tab=capture&WeekId={Guid.NewGuid()}"));
        Assert.Contains(warning, html, StringComparison.Ordinal);
        Assert.Contains(missing, html, StringComparison.Ordinal);
        Assert.Contains("data-context-state=\"ConfigurationIncomplete\"", html);
        Assert.DoesNotContain("data-group-preview", html);
        var result = await Post(client, "/Operations/Production?handler=Preview", html, new());
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains(warning, WebUtility.HtmlDecode(await result.Content.ReadAsStringAsync()), StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string url, string html, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = Input(html, "__RequestVerificationToken");
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }
    private static string Input(string html, string name)
    {
        var checkedAttribute = name == "NewWeek.WeekStart" ? "(?=[^>]*checked)" : "";
        return WebUtility.HtmlDecode(Regex.Match(html, "<input" + checkedAttribute + "[^>]*name=\"" + Regex.Escape(name) + "\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    }
    private static string Select(string html, string name) => Regex.Match(html, "<select[^>]*name=\"" + Regex.Escape(name) + "\"[^>]*>[\\s\\S]*?</select>").Value;
    private sealed class SundayClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 28, 2, 0, 0, TimeSpan.Zero);
    }
}
