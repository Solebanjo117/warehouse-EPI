using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace WarehouseEPI.Web.Backups;

public sealed record RestoreUploadResult(Guid? Id, string? Error);

public sealed class RestoreUploadService(ManualBackupSettings settings, RestoreJobStore store,
    RestorePackageInspector inspector, BackupOperationCoordinator coordinator, TimeProvider time) : BackgroundService
{
    private readonly Channel<ValidationRequest> queue = Channel.CreateUnbounded<ValidationRequest>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, byte> active = new();

    public async Task<RestoreUploadResult> UploadAsync(Guid actorId, IFormFile file, string password, CancellationToken token)
    {
        if (password.Length is < 12 or > 128) return new(null, "Usa una contraseña de entre 12 y 128 caracteres.");
        if (!Path.GetExtension(file.FileName).Equals(".webackup", StringComparison.OrdinalIgnoreCase) || file.Length is < 92 or > RestorePackageInspector.MaxUploadBytes)
            return new(null, "Selecciona un respaldo .webackup de hasta 2 GB.");
        var unavailable = settings.GetUnavailableReason();
        if (unavailable is not null) return new(null, unavailable);
        if (store.IsMaintenance) return new(null, "Hay una restauración en curso. Espera a que termine.");
        var lease = coordinator.TryBegin();
        if (lease is null) return new(null, "Hay una tarea de respaldo o restauración en curso. Espera a que termine.");
        var job = new RestoreJob(Guid.NewGuid(), actorId, time.GetUtcNow(), RestoreJobState.Validating);
        active.TryAdd(job.Id, 0);
        byte[]? key = null;
        try
        {
            // Remove abandoned ready previews and their decrypted secrets after 30 minutes.
            if (Directory.Exists(store.Root))
                foreach (var directory in Directory.EnumerateDirectories(store.Root))
                    if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) && store.Get(id) is { State: RestoreJobState.Ready } old &&
                        (old.ActorId == actorId || old.CreatedAt < time.GetUtcNow().AddMinutes(-30)))
                        Fail(old, "La revisión del respaldo venció. Carga y valida el archivo de nuevo.");
            store.Save(job);
            var path = Path.Combine(store.DirectoryPath(job.Id), "upload.webackup");
            await using (var source = file.OpenReadStream())
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    if (destination.Length + count > file.Length) throw new RestorePackageException("El respaldo supera el límite de tamaño.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (destination.Length != file.Length) throw new RestorePackageException("El respaldo está incompleto.");
            }
            key = ManualBackupEncryption.DeriveKey(password, RestorePackageInspector.ReadSalt(path));
            if (!queue.Writer.TryWrite(new(job, key, lease))) throw new IOException();
            return new(job.Id, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            active.TryRemove(job.Id, out _);
            lease.Dispose();
            var error = exception is RestorePackageException known ? known.Message : "No se pudo cargar el respaldo. Vuelve a seleccionarlo.";
            Fail(job, error);
            return new(null, error);
        }
    }

    public string? Confirm(Guid id, Guid actorId, string? adminPin, bool replaceAccepted, string? confirmation)
    {
        if (!replaceAccepted || confirmation != "IMPORTAR") return "Confirma el reemplazo de los datos y escribe IMPORTAR.";
        if (adminPin is null || adminPin.Length is < 4 or > 8 || !adminPin.All(char.IsAsciiDigit)) return "Introduce el NIP de un ADMIN incluido en el respaldo.";
        var unavailable = store.AgentUnavailableReason(time);
        if (unavailable is not null) return unavailable;
        if (store.IsMaintenance) return "Hay una restauración en curso. Espera a que termine.";
        using var lease = coordinator.TryBegin();
        if (lease is null) return "Hay una tarea de respaldo o restauración en curso. Espera a que termine.";
        var job = store.Get(id, actorId);
        if (job is not { State: RestoreJobState.Ready, Summary: not null } || job.CreatedAt < time.GetUtcNow().AddMinutes(-30))
            return "La revisión del respaldo venció. Carga y valida el archivo de nuevo.";
        try
        {
            using var secrets = JsonDocument.Parse(File.ReadAllText(Path.Combine(store.DirectoryPath(id), "pinlookupkey.json")));
            var root = secrets.RootElement;
            if (root.GetProperty("MigrationPackageSha256").GetString() != job.Summary.PackageSha256) return "La clave de NIP no corresponde al respaldo.";
            var key = Convert.FromBase64String(root.GetProperty("PinLookupKey").GetString()!);
            string lookup;
            try { lookup = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(adminPin))).ToLowerInvariant(); }
            finally { CryptographicOperations.ZeroMemory(key); }
            var request = new RestoreRequest(id, actorId, time.GetUtcNow(), job.Summary.PackageSha256, lookup);
            store.Save(job with { State = RestoreJobState.Requested });
            // Publish a complete request atomically; never overwrite another queued restoration.
            var temporary = store.PendingPath + "." + id.ToString("N") + ".partial";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(request));
                File.Move(temporary, store.PendingPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            if (!store.IsMaintenance)
                try { store.Save(job); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return "No se pudo solicitar la importación. Revisa el estado antes de volver a intentarlo.";
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cleanup = SweepAsync(stoppingToken);
        try
        {
            await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    var summary = await inspector.InspectAsync(store.DirectoryPath(request.Job.Id), request.Key, timeout.Token);
                    store.Save(request.Job with { State = RestoreJobState.Ready, Summary = summary });
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Fail(request.Job, exception is RestorePackageException known ? known.Message : "No se pudo validar el respaldo. Revisa el archivo y vuelve a cargarlo.");
                }
                finally { CryptographicOperations.ZeroMemory(request.Key); active.TryRemove(request.Job.Id, out _); request.Lease.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out var pending))
            { CryptographicOperations.ZeroMemory(pending.Key); active.TryRemove(pending.Job.Id, out _); pending.Lease.Dispose(); Fail(pending.Job, "La validación se interrumpió. Carga el respaldo de nuevo."); }
            await cleanup;
        }
    }
    private void Fail(RestoreJob job, string message)
    {
        try { store.ClearContent(job.Id); store.Save(job with { State = RestoreJobState.Failed, Error = message }); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private async Task SweepAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
                try
                {
                    if (!Directory.Exists(store.Root)) continue;
                    foreach (var directory in Directory.EnumerateDirectories(store.Root))
                        if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) && store.Get(id) is { } job)
                        {
                            if (job.State == RestoreJobState.Ready && job.CreatedAt < time.GetUtcNow().AddMinutes(-30))
                                Fail(job, "La revisión del respaldo venció. Carga y valida el archivo de nuevo.");
                            else if (job.State == RestoreJobState.Validating && !active.ContainsKey(id))
                                Fail(job, "La validación se interrumpió. Carga el respaldo de nuevo.");
                        }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private sealed record ValidationRequest(RestoreJob Job, byte[] Key, IDisposable Lease);
}
