using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class CycleCountOperatorSessionTests
{
    private const string LookupKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Valid_pin_creates_a_protected_campaign_scoped_cookie()
    {
        await using var fixture = await Fixture.CreateAsync();
        var context = NewContext();

        var session = await fixture.Sessions.StartAsync(context, fixture.CampaignId, fixture.Pin);

        Assert.NotNull(session);
        Assert.Equal(fixture.UserId, session.UserId);
        var header = Assert.Single(context.Response.Headers.SetCookie, value =>
            value!.StartsWith(CycleCountOperatorSession.DevelopmentCookieName + "=", StringComparison.Ordinal) &&
            !value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);

        var next = NewContext(CookiePair(header!));
        Assert.NotNull(await fixture.Sessions.GetAsync(next, fixture.CampaignId, renew: true));
        Assert.Null(await fixture.Sessions.GetAsync(NewContext(CookiePair(header!)), Guid.NewGuid(), renew: false));
    }

    [Fact]
    public async Task Invalid_pin_never_creates_a_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        var context = NewContext();

        Assert.Null(await fixture.Sessions.StartAsync(context, fixture.CampaignId, "9999"));
        Assert.DoesNotContain(context.Response.Headers.SetCookie, value =>
            value!.StartsWith(CycleCountOperatorSession.DevelopmentCookieName + "=", StringComparison.Ordinal) &&
            !value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_expires_after_inactivity_and_rejects_a_deactivated_user()
    {
        await using var fixture = await Fixture.CreateAsync();
        var started = NewContext();
        await fixture.Sessions.StartAsync(started, fixture.CampaignId, fixture.Pin);
        var cookie = CookiePair(started.Response.Headers.SetCookie.Last(value => !value!.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))!);

        fixture.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Null(await fixture.Sessions.GetAsync(NewContext(cookie), fixture.CampaignId, renew: false));

        var restarted = NewContext();
        await fixture.Sessions.StartAsync(restarted, fixture.CampaignId, fixture.Pin);
        cookie = CookiePair(restarted.Response.Headers.SetCookie.Last(value => !value!.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))!);
        var user = await fixture.Db.Users.SingleAsync(item => item.Id == fixture.UserId);
        user.IsActive = false;
        await fixture.Db.SaveChangesAsync();
        Assert.Null(await fixture.Sessions.GetAsync(NewContext(cookie), fixture.CampaignId, renew: false));
    }

    [Fact]
    public async Task Sliding_activity_never_extends_the_eight_hour_absolute_limit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var started = NewContext();
        await fixture.Sessions.StartAsync(started, fixture.CampaignId, fixture.Pin);
        var cookie = CookiePair(started.Response.Headers.SetCookie.Last(value => !value!.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))!);

        for (var index = 0; index < 16; index++)
        {
            fixture.Time.Advance(TimeSpan.FromMinutes(29));
            var active = NewContext(cookie);
            Assert.NotNull(await fixture.Sessions.GetAsync(active, fixture.CampaignId, renew: true));
            cookie = CookiePair(active.Response.Headers.SetCookie.Last()!);
        }

        fixture.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.Null(await fixture.Sessions.GetAsync(NewContext(cookie), fixture.CampaignId, renew: true));
    }

    private static DefaultHttpContext NewContext(string? cookie = null)
    {
        var context = new DefaultHttpContext();
        if (cookie is not null) context.Request.Headers.Cookie = cookie;
        return context;
    }

    private static string CookiePair(string setCookie) => setCookie.Split(';', 2)[0];

    private sealed class Fixture : IAsyncDisposable
    {
        public WarehouseDbContext Db { get; }
        public CycleCountOperatorSession Sessions { get; }
        public MutableTimeProvider Time { get; }
        public Guid CampaignId { get; } = Guid.NewGuid();
        public Guid UserId { get; }
        public string Pin { get; } = "8642";

        private Fixture(WarehouseDbContext db, CycleCountOperatorSession sessions, MutableTimeProvider time, Guid userId)
        { Db = db; Sessions = sessions; Time = time; UserId = userId; }

        public static async Task<Fixture> CreateAsync()
        {
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase($"CycleCountSession-{Guid.NewGuid():N}").Options);
            await db.Database.EnsureCreatedAsync();
            var pins = new UserPinService(db, new PinProtector(LookupKey));
            var user = new User { FullName = "Operador de prueba", RoleId = 2, PinLookup = string.Empty, PinHash = string.Empty };
            Assert.Equal(PinAssignmentResult.Success, await pins.AssignAsync(user, "8642"));
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var time = new MutableTimeProvider(Start);
            var sessions = new CycleCountOperatorSession(new EphemeralDataProtectionProvider(),
                new MemoryCache(new MemoryCacheOptions()), pins, db, new TestEnvironment(), time);
            return new(db, sessions, time, user.Id);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "WarehouseEPI.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
