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
    [InlineData("en", "Weekly schedule", "No weeks have been scheduled yet", "The week must start on Monday.")]
    [InlineData("es", "Programa semanal", "Todavía no hay semanas programadas", "La semana debe iniciar en lunes.")]
    public async Task Admin_setup_and_week_creation_are_explicit_localized_and_preserve_failed_dates(
        string language, string title, string empty, string dateError)
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
        Assert.Contains(empty, html, StringComparison.Ordinal);
        Assert.Equal("2026-09-21", Input(html, "NewWeek.WeekStart")); // UTC Monday is still Sunday at the warehouse.
        Assert.Contains("disabled", Select(html, "WeekId"), StringComparison.Ordinal);
        Assert.True(Regex.IsMatch(Select(html, "Configuration.Shift1Id"), "<option(?=[^>]*selected=\"selected\")[^>]*>Z shift</option>"), Select(html, "Configuration.Shift1Id"));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            Assert.Empty(await db.ProductionScheduleWeeks.ToListAsync());
            Assert.Null((await db.ProductionDailyConfigurations.SingleAsync()).Shift1Id);
        }
        var invalid = await Post(client, "/Admin/Production/Schedule?handler=CreateWeek", html,
            new() { ["NewWeek.OperationId"] = Guid.NewGuid().ToString(), ["NewWeek.WeekStart"] = "2026-09-22" });
        var failedHtml = WebUtility.HtmlDecode(await invalid.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains(dateError, failedHtml, StringComparison.Ordinal);
        Assert.Equal("2026-09-22", Input(failedHtml, "NewWeek.WeekStart"));
        Assert.DoesNotContain("0001-01-01", failedHtml, StringComparison.Ordinal);
        var created = await Post(client, "/Admin/Production/Schedule?handler=CreateWeek", failedHtml,
            new() { ["NewWeek.OperationId"] = Guid.NewGuid().ToString(), ["NewWeek.WeekStart"] = "2026-09-21" });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Contains("WeekId=", created.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var selected = WebUtility.HtmlDecode(await client.GetStringAsync(created.Headers.Location));
        Assert.Matches("<option(?=[^>]*selected=\"selected\")[^>]*>21/09", Select(selected, "WeekId"));
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
        Assert.Contains(language == "en" ? "Daily configuration" : "Configuración diaria", invalidImportHtml, StringComparison.Ordinal);
        var menu = WebUtility.HtmlDecode(await client.GetStringAsync("/Modules/production"));
        Assert.Contains(language == "en" ? "Advanced production" : "Producción avanzada", menu, StringComparison.Ordinal);
        var configuration = new Dictionary<string, string>
        {
            ["Configuration.OperationId"] = Input(html, "Configuration.OperationId"),
            ["Configuration.ExpectedVersion"] = Input(html, "Configuration.ExpectedVersion")
        };
        foreach (var field in new[] { "CuttingStageId", "SewingStageId", "ReadyToPackStageId", "Shift1Id", "Shift2Id" })
            configuration["Configuration." + field] = Regex.Match(Select(html, "Configuration." + field), "<option(?=[^>]*selected=\"selected\")[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var configured = await Post(client, "/Admin/Production/Schedule?handler=Configure", selected, configuration);
        Assert.Equal(HttpStatusCode.Redirect, configured.StatusCode);
        var capture = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/Production"));
        // Configuration is complete, but the week is still a draft: capture remains disabled.
        Assert.Matches("<fieldset[^>]*disabled", capture);
        var shiftSelect = Select(capture, "ShiftId");
        Assert.True(shiftSelect.IndexOf("Z shift", StringComparison.Ordinal) < shiftSelect.IndexOf("A shift", StringComparison.Ordinal));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            (await db.ProductionShifts.SingleAsync(x => x.Code == "T2")).IsActive = false;
            await db.SaveChangesAsync();
        }
        var inactive = WebUtility.HtmlDecode(await client.GetStringAsync("/Operations/Production"));
        Assert.Matches("<fieldset[^>]*disabled", inactive);
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
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/Production?WeekId={Guid.NewGuid()}"));
        Assert.Contains(warning, html, StringComparison.Ordinal);
        Assert.Contains(missing, html, StringComparison.Ordinal);
        Assert.Matches("<fieldset[^>]*disabled", html);
        var result = await Post(client, "/Operations/Production?handler=Preview", html, new());
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains(warning, WebUtility.HtmlDecode(await result.Content.ReadAsStringAsync()), StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string url, string html, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = Input(html, "__RequestVerificationToken");
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }
    private static string Input(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, "<input[^>]*name=\"" + Regex.Escape(name) + "\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private static string Select(string html, string name) => Regex.Match(html, "<select[^>]*name=\"" + Regex.Escape(name) + "\"[^>]*>[\\s\\S]*?</select>").Value;
    private sealed class SundayClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 28, 2, 0, 0, TimeSpan.Zero);
    }
}
