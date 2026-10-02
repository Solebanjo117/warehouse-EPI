using System.Text.Json;

namespace WarehouseEPI.Web.Backups;

public enum RestoreJobState { Validating, Ready, Requested, Running, Succeeded, Failed, RecoveryRequired }
public sealed record RestoreJob(Guid Id, Guid ActorId, DateTimeOffset CreatedAt, RestoreJobState State,
    RestorePackageSummary? Summary = null, string? Error = null);
public sealed record RestoreRequest(Guid Id, Guid ActorId, DateTimeOffset ConfirmedAt, string PackageSha256, string AdminPinLookup);

public sealed class RestoreJobStore(ManualBackupSettings settings)
{
    public string Root => Path.Combine(settings.DirectoryPath, ".restore");
    public string PendingPath => Path.Combine(Root, "pending.json");
    public string MaintenancePath => Path.Combine(Root, "maintenance.json");
    public string DirectoryPath(Guid id) => Path.Combine(Root, id.ToString("N"));

    public Guid? ActiveId
    {
        get
        {
            if (!settings.HasSafeStorageDirectory()) return null;
            foreach (var path in new[] { MaintenancePath, PendingPath })
            {
                var request = Read<RestoreRequest>(path);
                if (request is not null) return request.Id;
            }
            return null;
        }
    }
    // Fail closed even if a request was damaged during a disk/power failure.
    public bool IsMaintenance => File.Exists(MaintenancePath) || File.Exists(PendingPath);

    public string? AgentUnavailableReason(TimeProvider time)
    {
        var heartbeat = Read<AgentHeartbeat>(Path.Combine(Root, "agent.json"));
        return heartbeat is not null && heartbeat.SchemaVersion == 1 &&
            heartbeat.CheckedAtUtc <= time.GetUtcNow().AddMinutes(1) && heartbeat.CheckedAtUtc > time.GetUtcNow().AddMinutes(-3)
            ? null : "IT debe habilitar el servicio local de restauración y comprobar que esté activo.";
    }

    public RestoreJob? Get(Guid id, Guid? actorId = null)
    {
        if (!IsSafePath(DirectoryPath(id))) return null;
        var job = Read<RestoreJob>(Path.Combine(DirectoryPath(id), "status.json"));
        return job?.Id == id && (actorId is null || job.ActorId == actorId) ? job : null;
    }

    public void Save(RestoreJob job)
    {
        if (!IsSafePath(DirectoryPath(job.Id))) throw new IOException("Unsafe restore storage.");
        Directory.CreateDirectory(DirectoryPath(job.Id));
        AtomicWrite(Path.Combine(DirectoryPath(job.Id), "status.json"), job);
    }
    public static void AtomicWrite<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void ClearContent(Guid id)
    {
        var directory = DirectoryPath(id);
        if (!IsSafePath(directory) || !Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory))
            if (!Path.GetFileName(file).Equals("status.json", StringComparison.Ordinal) &&
                !new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint)) File.Delete(file);
    }
    private bool IsSafePath(string path)
    {
        if (!settings.HasSafeStorageDirectory()) return false;
        for (var parent = new DirectoryInfo(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        return true;
    }
    private static T? Read<T>(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1024 * 1024 || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return default;
            // Readers must not prevent the maintenance task from atomically replacing status.
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(input);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
    private sealed record AgentHeartbeat(int SchemaVersion, DateTimeOffset CheckedAtUtc);
}
