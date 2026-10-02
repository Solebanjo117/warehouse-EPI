namespace WarehouseEPI.Web.Backups;

public sealed class BackupOperationCoordinator
{
    private readonly object gate = new();
    private bool busy;
    public IDisposable? TryBegin()
    {
        lock (gate)
        {
            if (busy) return null;
            busy = true;
            return new Lease(this);
        }
    }
    private sealed class Lease(BackupOperationCoordinator owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            lock (owner.gate)
            {
                if (disposed) return;
                disposed = true; owner.busy = false;
            }
        }
    }
}
