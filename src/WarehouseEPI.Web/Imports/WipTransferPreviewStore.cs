using Microsoft.Extensions.Caching.Memory;
using WarehouseEPI.Infrastructure.Imports;

namespace WarehouseEPI.Web.Imports;

public sealed record WipTransferDraft(Guid Token, Guid OwnerId, DateTimeOffset ExpiresAt,
    WipTransferFile File, IReadOnlyDictionary<int, WipTransferResolution> Resolutions, int Revision = 1);

// Immutable snapshots and a revision guard prevent another tab from confirming old corrections.
public sealed class WipTransferPreviewStore(IMemoryCache cache, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public WipTransferDraft Create(Guid ownerId, WipTransferFile file, IReadOnlyDictionary<int, WipTransferResolution> resolutions)
    {
        var draft = new WipTransferDraft(Guid.NewGuid(), ownerId, time.GetUtcNow().AddHours(1), file,
            resolutions);
        Save(draft);
        return draft;
    }

    public WipTransferDraft? Get(Guid token, Guid ownerId) =>
        cache.TryGetValue(Key(token), out WipTransferDraft? draft) && draft?.OwnerId == ownerId && draft.ExpiresAt > time.GetUtcNow()
            ? draft : null;

    public void Save(WipTransferDraft draft) => cache.Set(Key(draft.Token), draft, draft.ExpiresAt);
    public void Remove(Guid token) => cache.Remove(Key(token));
    public async ValueTask<IAsyncDisposable> LockAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        return new Handle(gate);
    }
    public void Dispose() => gate.Dispose();
    private static string Key(Guid token) => "wip-transfer:" + token;
    private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }
}
