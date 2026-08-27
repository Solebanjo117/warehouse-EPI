using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Reports.Notifications;

namespace WarehouseEPI.Tests.Web;

public sealed class OperationalNotificationsRouteTests
{
    [Fact]
    public async Task Snapshot_derives_audience_and_refreshes_only_its_cache_key()
    {
        await using var db = CreateDbContext();
        var product = new Product { Sku = "NOTIFY-ADMIN", BaseUnitId = 1 };
        var blocked = new Location { Code = "NOTIFY-BLOCKED", Kind = LocationKind.Rack, IsBlocked = true };
        db.AddRange(product, blocked, new InventoryBalance { Product = product, Location = blocked, Quantity = 1m });
        await db.SaveChangesAsync();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
        var service = new OperationalAlertService(db, new WarehouseSettingsService(db),
            new WarehouseClock(new WarehouseSettingsService(db)), time);
        using var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        var publicModel = Model(service, cache, isAdmin: false);
        var firstPublic = Snapshot(await publicModel.OnGetSnapshotAsync());
        time.Advance(TimeSpan.FromMinutes(1));
        var adminModel = Model(service, cache, isAdmin: true);
        var firstAdmin = Snapshot(await adminModel.OnGetSnapshotAsync());
        time.Advance(TimeSpan.FromMinutes(1));
        var refreshedPublic = Snapshot(await publicModel.OnGetSnapshotAsync(refresh: true));
        var cachedAdmin = Snapshot(await adminModel.OnGetSnapshotAsync());

        Assert.Equal(OperationalAlertAudience.Public, firstPublic.Audience);
        Assert.Empty(firstPublic.Items);
        Assert.Equal(OperationalAlertAudience.Admin, firstAdmin.Audience);
        Assert.Contains(firstAdmin.Items, item => item.Category == OperationalAlertCategory.RestrictedInventory);
        Assert.True(refreshedPublic.GeneratedAtUtc > firstPublic.GeneratedAtUtc);
        Assert.Equal(firstAdmin.GeneratedAtUtc, cachedAdmin.GeneratedAtUtc);
        Assert.Contains("no-store", publicModel.Response.Headers.CacheControl.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Cookie", publicModel.Response.Headers.Vary.ToString());
    }

    [Fact]
    public void Layout_and_script_preserve_accessibility_polling_and_safe_dom_contracts()
    {
        var layout = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml"));
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "operational-notifications.js"));

        Assert.Equal(2, Count(layout, "data-notification-trigger"));
        Assert.Contains("aria-controls=\"operational-notifications\"", layout, StringComparison.Ordinal);
        Assert.Contains("tabindex=\"-1\" id=\"operational-notifications\"", layout, StringComparison.Ordinal);
        Assert.Contains("intervalMilliseconds = 60000", script, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", script, StringComparison.Ordinal);
        Assert.Contains("requestInProgress", script, StringComparison.Ordinal);
        Assert.Contains("cache: \"no-store\"", script, StringComparison.Ordinal);
        Assert.Contains("Datos desactualizados", script, StringComparison.Ordinal);
        Assert.Contains("if (previousCounts)", script, StringComparison.Ordinal);
        Assert.Contains("99+", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.Ordinal);
    }

    private static IndexModel Model(OperationalAlertService service, IMemoryCache cache, bool isAdmin)
    {
        var identity = new ClaimsIdentity(isAdmin
            ? [new Claim(ClaimTypes.Name, "Admin"), new Claim(ClaimTypes.Role, "ADMIN")]
            : [], "Test");
        return new IndexModel(service, cache)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } }
        };
    }

    private static OperationalAlertSnapshotDto Snapshot(IActionResult result) =>
        Assert.IsType<OperationalAlertSnapshotDto>(Assert.IsType<JsonResult>(result).Value);

    private static int Count(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"OperationalNotificationsRouteTests-{Guid.NewGuid():N}").Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan interval) => current = current.Add(interval);
    }
}
