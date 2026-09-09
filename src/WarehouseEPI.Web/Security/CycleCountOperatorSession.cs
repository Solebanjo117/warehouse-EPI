using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Web.Security;

public sealed class CycleCountOperatorSession(
    IDataProtectionProvider dataProtectionProvider,
    IMemoryCache cache,
    UserPinService pinService,
    WarehouseDbContext dbContext,
    IWebHostEnvironment environment,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(8);
    public const string DevelopmentCookieName = "WarehouseEPI.CycleCountOperator";
    public const string ProductionCookieName = "__Host-WarehouseEPI.CycleCountOperator";

    private const string CachePrefix = "cycle-count-operator-session:";
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector("WarehouseEPI.CycleCounts.OperatorSession.v1");
    private string CookieName => environment.IsProduction() ? ProductionCookieName : DevelopmentCookieName;

    public async Task<CycleCountOperatorSessionView?> StartAsync(
        HttpContext context,
        Guid campaignId,
        string pin,
        CancellationToken cancellationToken = default)
    {
        var user = await pinService.AuthenticateAsync(pin, cancellationToken);
        if (user is null || user.Role.Code is not ("ADMIN" or "OPERATOR")) return null;

        Clear(context);
        var now = timeProvider.GetUtcNow();
        var ticket = new Ticket(Guid.NewGuid(), user.Id, user.FullName, campaignId, now, now, now.Add(AbsoluteLifetime));
        // MemoryCache evalúa las fechas absolutas con su propio reloj. La entrada usa una
        // duración relativa y el ticket, basado en el reloj inyectado, conserva los límites
        // exactos de inactividad y duración absoluta en GetAsync.
        cache.Set(CacheKey(ticket.SessionId), true, AbsoluteLifetime);
        WriteCookie(context, ticket);
        return View(ticket, now);
    }

    public async Task<CycleCountOperatorSessionView?> GetAsync(
        HttpContext context,
        Guid campaignId,
        bool renew,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadTicket(context, out var ticket) || ticket is null) return null;
        var now = timeProvider.GetUtcNow();
        if (ticket.CampaignId != campaignId || ticket.IssuedAt > now || ticket.LastActivityAt > now ||
            now - ticket.LastActivityAt >= IdleLifetime || now >= ticket.AbsoluteExpiresAt ||
            !cache.TryGetValue(CacheKey(ticket.SessionId), out _))
        {
            Clear(context);
            return null;
        }

        var user = await dbContext.Users.AsNoTracking().Include(item => item.Role)
            .SingleOrDefaultAsync(item => item.Id == ticket.UserId, cancellationToken);
        if (user is null || !user.IsActive || user.Role.Code is not ("ADMIN" or "OPERATOR"))
        {
            Clear(context);
            return null;
        }

        ticket = ticket with { UserName = user.FullName };
        if (renew)
        {
            ticket = ticket with { LastActivityAt = now };
            WriteCookie(context, ticket);
        }
        return View(ticket, now);
    }

    public void Clear(HttpContext context)
    {
        if (TryReadTicket(context, out var ticket) && ticket is not null)
            cache.Remove(CacheKey(ticket.SessionId));
        context.Response.Cookies.Delete(CookieName, CookieOptions(timeProvider.GetUtcNow()));
    }

    private void WriteCookie(HttpContext context, Ticket ticket)
    {
        var value = protector.Protect(JsonSerializer.Serialize(ticket));
        context.Response.Cookies.Append(CookieName, value, CookieOptions(ticket.AbsoluteExpiresAt));
    }

    private bool TryReadTicket(HttpContext context, out Ticket? ticket)
    {
        ticket = null;
        if (!context.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            ticket = JsonSerializer.Deserialize<Ticket>(protector.Unprotect(value));
            return ticket is not null;
        }
        catch
        {
            return false;
        }
    }

    private CookieOptions CookieOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = environment.IsProduction(),
        IsEssential = true,
        Path = "/",
        Expires = expiresAt
    };

    private static string CacheKey(Guid sessionId) => CachePrefix + sessionId.ToString("N");

    private static CycleCountOperatorSessionView View(Ticket ticket, DateTimeOffset now)
    {
        var idleExpiresAt = now.Add(IdleLifetime);
        if (idleExpiresAt > ticket.AbsoluteExpiresAt) idleExpiresAt = ticket.AbsoluteExpiresAt;
        return new(ticket.UserId, ticket.UserName, ticket.CampaignId, ticket.IssuedAt, idleExpiresAt, ticket.AbsoluteExpiresAt);
    }

    private sealed record Ticket(Guid SessionId, Guid UserId, string UserName, Guid CampaignId,
        DateTimeOffset IssuedAt, DateTimeOffset LastActivityAt, DateTimeOffset AbsoluteExpiresAt);
}

public sealed record CycleCountOperatorSessionView(Guid UserId, string UserName, Guid CampaignId,
    DateTimeOffset IssuedAt, DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt);
