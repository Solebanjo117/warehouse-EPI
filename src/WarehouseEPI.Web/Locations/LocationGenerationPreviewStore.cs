using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace WarehouseEPI.Web.Locations;

public sealed class LocationGenerationPreviewStore(IMemoryCache cache, TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private const string KeyPrefix = "location-generation:";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);

    public LocationGenerationPreview Save(Guid ownerUserId, string manifest,
        IReadOnlyList<LocationGenerationRow> rows, IReadOnlyList<string> errors)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = timeProvider.GetUtcNow();
        var preview = new LocationGenerationPreview(token, ownerUserId, now, now.Add(Lifetime), manifest, rows, errors);
        cache.Set(Key(token), preview, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime });
        return preview;
    }

    public bool TryGet(string token, Guid ownerUserId, out LocationGenerationPreview? preview)
    {
        preview = null;
        if (string.IsNullOrWhiteSpace(token) || !cache.TryGetValue(Key(token), out LocationGenerationPreview? stored) || stored is null)
            return false;
        if (stored.ExpiresAt <= timeProvider.GetUtcNow()) { Remove(token); return false; }
        if (stored.OwnerUserId != ownerUserId) return false;
        preview = stored;
        return true;
    }

    public void Remove(string token) => cache.Remove(Key(token));

    public async ValueTask<IAsyncDisposable> LockAsync(string token, CancellationToken cancellationToken)
    {
        var semaphore = locks.GetOrAdd(token, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new LockHandle(semaphore);
    }

    private static string Key(string token) => KeyPrefix + token;
    private sealed class LockHandle(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { semaphore.Release(); return ValueTask.CompletedTask; }
    }
}
