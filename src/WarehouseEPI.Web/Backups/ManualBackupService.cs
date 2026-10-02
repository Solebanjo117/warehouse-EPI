using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace WarehouseEPI.Web.Backups;

public enum ManualBackupState { Running, Ready, Failed }
public sealed record ManualBackupRecord(Guid Id, DateTimeOffset StartedAt, ManualBackupState State,
    long Bytes = 0, string? Sha256 = null);
public sealed record ManualBackupStart(Guid? Id, string? Error);

public interface IManualBackupBuilder
{
    Task BuildAsync(string workingDirectory, string encryptedPath, DateTimeOffset startedAt,
        byte[] key, byte[] salt, CancellationToken cancellationToken);
}

public sealed class ManualBackupService(ManualBackupSettings settings, IManualBackupBuilder builder,
    TimeProvider clock, BackupOperationCoordinator? coordinator = null, RestoreJobStore? restores = null) : BackgroundService
{
    private readonly BackupOperationCoordinator operations = coordinator ?? new();
    private readonly object gate = new();
    private readonly Channel<BackupRequest> queue = Channel.CreateUnbounded<BackupRequest>(
        new UnboundedChannelOptions { SingleReader = true });
    private ManualBackupRecord? current;
    private bool accepting = true;

    public ManualBackupRecord? Current { get { lock (gate) return current; } }
    public string? UnavailableReason => settings.GetUnavailableReason();

    public ManualBackupStart Start(string password)
    {
        if (password.Length is < 12 or > 128)
            return new(null, "Usa una contraseña de entre 12 y 128 caracteres.");
        lock (gate)
        {
            if (!accepting || current?.State == ManualBackupState.Running)
                return new(null, "Ya hay un respaldo en curso. Espera a que termine.");
            var unavailable = UnavailableReason;
            if (unavailable is not null) return new(null, unavailable);
            if (restores?.IsMaintenance == true) return new(null, "Hay una restauración en curso. Espera a que termine.");
            var lease = operations.TryBegin();
            if (lease is null) return new(null, "Hay una tarea de respaldo o restauración en curso. Espera a que termine.");
            var salt = RandomNumberGenerator.GetBytes(16);
            var key = ManualBackupEncryption.DeriveKey(password, salt);
            current = new(Guid.NewGuid(), clock.GetUtcNow(), ManualBackupState.Running);
            if (!queue.Writer.TryWrite(new(current, key, salt, lease)))
            {
                CryptographicOperations.ZeroMemory(key);
                lease.Dispose();
                current = current with { State = ManualBackupState.Failed };
                return new(null, "No se pudo iniciar el respaldo. Inténtalo de nuevo.");
            }
            return new(current.Id, null);
        }
    }

    public IReadOnlyList<ManualBackupRecord> History() => ReadHistory().Take(10).ToArray();

    private IReadOnlyList<ManualBackupRecord> ReadHistory()
    {
        // Completed backups must remain downloadable if tools are removed or storage becomes read-only/full.
        if (!settings.HasSafeStorageDirectory() || !Directory.Exists(settings.DirectoryPath)) return [];
        var records = new List<ManualBackupRecord>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(settings.DirectoryPath, "*.json"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
                try
                {
                    if (new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    var record = JsonSerializer.Deserialize<ManualBackupRecord>(File.ReadAllText(path));
                    var file = new FileInfo(FilePath(id));
                    if (record is { State: ManualBackupState.Ready } && record.Id == id && file.Exists &&
                        !file.Attributes.HasFlag(FileAttributes.ReparsePoint) && file.Length == record.Bytes)
                        records.Add(record);
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return records.OrderByDescending(record => record.StartedAt).ToArray();
    }

    public Stream? OpenDownload(Guid id)
    {
        if (!History().Any(record => record.Id == id)) return null;
        try { return new FileStream(FilePath(id), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string DownloadName(ManualBackupRecord record) => $"WarehouseEPI-{record.StartedAt:yyyyMMdd-HHmmss}-{record.Id:N}.webackup";
    private string FilePath(Guid id) => Path.Combine(settings.DirectoryPath, $"{id:N}.webackup");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
                await GenerateAsync(request, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (gate) accepting = false;
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out var pending)) { CryptographicOperations.ZeroMemory(pending.Key); pending.Lease.Dispose(); }
        }
    }

    private async Task GenerateAsync(BackupRequest request, CancellationToken stoppingToken)
    {
        var workingDirectory = Path.Combine(settings.DirectoryPath, $".work-{request.Record.Id:N}");
        var partial = FilePath(request.Record.Id) + ".partial";
        var result = request.Record with { State = ManualBackupState.Failed };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            Directory.CreateDirectory(workingDirectory);
            await builder.BuildAsync(workingDirectory, partial, request.Record.StartedAt, request.Key, request.Salt, timeout.Token);
            await using (var file = File.OpenRead(partial))
                result = request.Record with
                {
                    State = ManualBackupState.Ready,
                    Bytes = file.Length,
                    Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(file, timeout.Token)).ToLowerInvariant()
                };
            var metadata = Path.Combine(settings.DirectoryPath, $"{result.Id:N}.json");
            await File.WriteAllTextAsync(metadata + ".partial", JsonSerializer.Serialize(result), timeout.Token);
            File.Move(partial, FilePath(result.Id));
            File.Move(metadata + ".partial", metadata);
            // Only this feature's private files are retained. Downloads in use are skipped.
            foreach (var old in ReadHistory().Skip(5))
            {
                try
                {
                    File.Delete(FilePath(old.Id));
                    File.Delete(Path.Combine(settings.DirectoryPath, $"{old.Id:N}.json"));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = request.Record with { State = ManualBackupState.Failed };
            try { File.Delete(FilePath(result.Id)); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            // Raw process/database exceptions can contain credentials: never expose or log them.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(request.Key);
            try
            {
                if (File.Exists(partial)) File.Delete(partial);
                if (Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            lock (gate) current = result;
            request.Lease.Dispose();
        }
    }

    private sealed record BackupRequest(ManualBackupRecord Record, byte[] Key, byte[] Salt, IDisposable Lease);
}
