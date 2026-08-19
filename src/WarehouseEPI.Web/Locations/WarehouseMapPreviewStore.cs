using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace WarehouseEPI.Web.Locations;

public sealed record WarehouseMapPreview(string Token, Guid OwnerUserId, DateTimeOffset ExpiresAt);

public sealed class WarehouseMapPreviewStore(IMemoryCache cache, TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private const string Prefix = "warehouse-map-preview:";
    public WarehouseMapPreview Save(Guid owner)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var preview = new WarehouseMapPreview(token, owner, timeProvider.GetUtcNow().Add(Lifetime));
        cache.Set(Prefix + token, preview, Lifetime); cache.Set(Prefix + "owner:" + owner, token, Lifetime); return preview;
    }
    public bool Consume(string? token, Guid owner)
    {
        if (string.IsNullOrWhiteSpace(token)) cache.TryGetValue(Prefix + "owner:" + owner, out token);
        if (string.IsNullOrWhiteSpace(token) || !cache.TryGetValue(Prefix + token, out WarehouseMapPreview? preview) || preview is null || preview.OwnerUserId != owner || preview.ExpiresAt <= timeProvider.GetUtcNow()) return false;
        cache.Remove(Prefix + token); return true;
    }
}
