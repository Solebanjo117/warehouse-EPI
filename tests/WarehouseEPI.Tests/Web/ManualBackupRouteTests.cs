using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Backups;

namespace WarehouseEPI.Tests.Web;

public sealed class ManualBackupRouteTests
{
    [Fact]
    public async Task Anonymous_users_cannot_read_start_poll_or_download_a_backup()
    {
        await using var original = new AdminRouteTests.WarehouseApplicationFactory();
        await using var factory = original.WithWebHostBuilder(host => host.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        foreach (var path in new[] { "/Admin/System/Backups", "/Admin/System/Backups?handler=Status",
                     "/Admin/System/Backups?handler=Download&id=" + Guid.NewGuid() })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/Admin/Login", response.Headers.Location?.AbsolutePath);
        }
        var post = await client.PostAsync("/Admin/System/Backups", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
    }

    [Fact]
    public async Task Admin_can_start_and_download_but_csrf_password_errors_and_revoked_access_are_blocked()
    {
        using var fixture = new ManualBackupTests.BackupFixture();
        var builder = new ManualBackupTests.FixtureBuilder { Hold = true };
        await using var original = new AdminRouteTests.WarehouseApplicationFactory();
        await using var factory = original.WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>(fixture.Configuration)
            { ["AllowedHosts"] = "localhost" }));
            host.ConfigureServices(services =>
            {
                services.RemoveAll<ManualBackupSettings>(); services.AddSingleton(fixture.Settings);
                services.RemoveAll<IManualBackupBuilder>(); services.AddSingleton<IManualBackupBuilder>(builder);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var role = await db.Roles.SingleAsync(item => item.Code == "ADMIN");
            var user = new User { FullName = "Backup fixture administrator", RoleId = role.Id, PinLookup = "", PinHash = "" };
            Assert.Equal(PinAssignmentResult.Success, await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "1470"));
            db.Users.Add(user); await db.SaveChangesAsync(); userId = user.Id;
        }
        var login = await client.GetStringAsync("/Admin/Login");
        var signedIn = await client.PostAsync("/Admin/Login", Form("Input.Pin", "1470", Token(login)));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var html = await client.GetStringAsync("/Admin/System/Backups");
        Assert.Contains("Crear respaldo ahora", html);
        var csrf = await client.PostAsync("/Admin/System/Backups", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["password"] = ManualBackupTests.Password, ["confirmation"] = ManualBackupTests.Password }));
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        var mismatch = await client.PostAsync("/Admin/System/Backups", Form("password", ManualBackupTests.Password, Token(html), "incorrect confirmation"));
        var error = await mismatch.Content.ReadAsStringAsync();
        Assert.Contains("Las contraseñas no coinciden.", WebUtility.HtmlDecode(error));
        Assert.DoesNotContain(ManualBackupTests.Password, error, StringComparison.Ordinal);
        Assert.DoesNotContain(ManualBackupTests.PinKey, error, StringComparison.Ordinal);
        var post = await client.PostAsync("/Admin/System/Backups", Form("password", ManualBackupTests.Password, Token(error), ManualBackupTests.Password));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        await builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await client.GetStringAsync("/Admin/System/Backups");
        Assert.Contains("Generando respaldo", WebUtility.HtmlDecode(running));
        await SaveFixtureAsync("running", running);
        builder.Release.TrySetResult();
        var worker = factory.Services.GetRequiredService<ManualBackupService>();
        await ManualBackupTests.WaitForResultAsync(worker);
        var ready = await client.GetStringAsync("/Admin/System/Backups");
        Assert.Contains("Descargar respaldo", ready);
        Assert.Contains("no-store", (await client.GetAsync("/Admin/System/Backups")).Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        await SaveFixtureAsync("ready", ready);
        var record = Assert.Single(worker.History());
        var download = await client.GetAsync("/Admin/System/Backups?handler=Download&id=" + record.Id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("fixture", System.Text.Encoding.UTF8.GetString(ManualBackupTests.Decrypt(await download.Content.ReadAsByteArrayAsync(), ManualBackupTests.Password)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Admin/System/Backups?handler=Download&id=" + Guid.NewGuid())).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            (await db.Users.FindAsync(userId))!.IsActive = false; await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Admin/System/Backups?handler=Download&id=" + record.Id)).StatusCode);
    }

    private static FormUrlEncodedContent Form(string field, string value, string token, string? confirmation = null)
    {
        var fields = new Dictionary<string, string> { [field] = value, ["__RequestVerificationToken"] = token };
        if (confirmation is not null) fields["confirmation"] = confirmation;
        return new(fields);
    }
    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html,
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private static async Task SaveFixtureAsync(string name, string html)
    {
        var root = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_REPOSITORY_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var directory = Path.Combine(root, "artifacts", "validation", "manual-backups", "fixtures");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".html"), html);
    }
}
