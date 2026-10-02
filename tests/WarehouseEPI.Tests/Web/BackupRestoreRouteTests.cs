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
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Tests.Web;

public sealed class BackupRestoreRouteTests
{
    [Fact]
    public async Task Upload_and_confirmation_require_admin_ownership_and_csrf_and_progress_works_without_database_access()
    {
        using var fixture = new ManualBackupTests.BackupFixture();
        var blockDatabase = false;
        await using var original = new AdminRouteTests.WarehouseApplicationFactory();
        await using var factory = original.WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>(fixture.Configuration) { ["AllowedHosts"] = "localhost" }));
            host.ConfigureServices(services =>
            {
                services.RemoveAll<ManualBackupSettings>(); services.AddSingleton(fixture.Settings);
                services.RemoveAll<WarehouseDbContext>();
                services.AddScoped(provider => blockDatabase ? throw new InvalidOperationException("The restore progress must not access PostgreSQL.") :
                    new WarehouseDbContext(provider.GetRequiredService<DbContextOptions<WarehouseDbContext>>()));
            });
        });
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        foreach (var path in new[] { "/Admin/System/Restore", "/Admin/System/Restore?handler=Status&id=" + Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(path)).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var user = new User { FullName = "Restore fixture administrator", RoleId = (await db.Roles.SingleAsync(role => role.Code == "ADMIN")).Id, PinLookup = "", PinHash = "" };
            Assert.Equal(PinAssignmentResult.Success, await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "1470"));
            db.Users.Add(user); await db.SaveChangesAsync();
        }
        var login = await client.GetStringAsync("/Admin/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Admin/Login", Form(Token(login), ("Input.Pin", "1470")))).StatusCode);
        var store = factory.Services.GetRequiredService<RestoreJobStore>(); BackupRestoreTests.Heartbeat(store);
        var html = await client.GetStringAsync("/Admin/System/Restore");
        Assert.Contains("multipart/form-data", html);
        await SaveAsync("restore-upload", html);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Admin/System/Restore?handler=Confirm", Form("invalid", ("confirmation", "IMPORTAR")))).StatusCode);
        var bytes = await BackupRestoreTests.PackageAsync(fixture.Directory);
        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent(Token(html)), "__RequestVerificationToken");
        upload.Add(new StringContent(ManualBackupTests.Password), "password");
        upload.Add(new ByteArrayContent(bytes), "backup", "fixture.webackup");
        var accepted = await client.PostAsync("/Admin/System/Restore?handler=Upload", upload);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var location = accepted.Headers.Location!.ToString();
        var id = Guid.Parse(Regex.Match(location, "id=([a-f0-9-]+)").Groups[1].Value);
        var job = await BackupRestoreTests.WaitAsync(store, id); Assert.Equal(RestoreJobState.Ready, job.State);
        var review = await client.GetStringAsync(location);
        Assert.Contains("Revisar y confirmar", WebUtility.HtmlDecode(review));
        Assert.DoesNotContain(ManualBackupTests.PinKey, review); Assert.DoesNotContain(ManualBackupTests.Password, review);
        await SaveAsync("restore-review", review);
        using (var englishRequest = new HttpRequestMessage(HttpMethod.Get, location))
        {
            englishRequest.Headers.Add("Cookie", UiLanguage.CookieName + "=en");
            var english = await (await client.SendAsync(englishRequest)).Content.ReadAsStringAsync();
            Assert.Contains("Review and confirm import", english); await SaveAsync("restore-review-en", english);
        }
        var other = job with { Id = Guid.NewGuid(), ActorId = Guid.NewGuid() }; store.Save(other);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/Admin/System/Restore?handler=Confirm", Form(Token(review),
            ("id", other.Id.ToString()), ("adminPin", "1470"), ("replaceAccepted", "true"), ("confirmation", "IMPORTAR")))).StatusCode);
        var rejected = await client.PostAsync("/Admin/System/Restore?handler=Confirm", Form(Token(review),
            ("id", id.ToString()), ("adminPin", "1470"), ("replaceAccepted", "true"), ("confirmation", "incorrect")));
        var error = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("IMPORTAR", error); Assert.DoesNotContain("value=\"1470\"", error);
        Assert.False(store.IsMaintenance);
        var confirmed = await client.PostAsync("/Admin/System/Restore?handler=Confirm", Form(Token(error),
            ("id", id.ToString()), ("adminPin", "1470"), ("replaceAccepted", "true"), ("confirmation", "IMPORTAR")));
        Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        Assert.StartsWith("/BackupRestoreProgress", confirmed.Headers.Location!.ToString());
        blockDatabase = true;
        var progressResponse = await client.GetAsync(confirmed.Headers.Location);
        var progress = await progressResponse.Content.ReadAsStringAsync();
        Assert.True(progressResponse.IsSuccessStatusCode, progress);
        Assert.Contains("Restauración en curso", WebUtility.HtmlDecode(progress));
        Assert.DoesNotContain("Restore fixture administrator", progress);
        await SaveAsync("restore-progress", progress);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Admin/System")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/Operations/Production", new StringContent("fixture"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/BackupRestoreProgress?handler=Status&id=" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/BackupRestoreProgress?id=" + Guid.NewGuid())).StatusCode);
        using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/BackupRestoreProgress?id=" + id)).StatusCode);
        File.Delete(store.PendingPath); store.Save(job with { State = RestoreJobState.Succeeded });
        var completed = await client.GetStringAsync("/BackupRestoreProgress?id=" + id);
        Assert.Contains("Importación completada", WebUtility.HtmlDecode(completed));
        Assert.Contains("/Admin/Login", completed); await SaveAsync("restore-completed", completed);
        blockDatabase = false;
    }

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)).Append(new("__RequestVerificationToken", token)));
    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private static async Task SaveAsync(string name, string html)
    {
        var root = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_REPOSITORY_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var directory = Path.Combine(root, "artifacts", "validation", "manual-backups", "fixtures"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".html"), html);
    }
}
