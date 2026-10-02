using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using WarehouseEPI.Web.Backups;

namespace WarehouseEPI.Tests.Web;

public sealed class BackupRestoreTests
{
    [Fact]
    public async Task Upload_validates_the_whole_package_and_confirmation_preserves_the_paired_key_without_saving_the_pin()
    {
        using var fixture = new ManualBackupTests.BackupFixture();
        var store = new RestoreJobStore(fixture.Settings);
        var coordinator = new BackupOperationCoordinator();
        using var worker = new RestoreUploadService(fixture.Settings, store, new(), coordinator, TimeProvider.System);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var actor = Guid.NewGuid();
            var bytes = await PackageAsync(fixture.Directory);
            using var stream = new MemoryStream(bytes);
            var result = await worker.UploadAsync(actor, new FormFile(stream, 0, bytes.Length, "backup", "fixture.webackup"), ManualBackupTests.Password, CancellationToken.None);
            Assert.Null(result.Error);
            var job = await WaitAsync(store, result.Id!.Value);
            Assert.Equal(RestoreJobState.Ready, job.State);
            Assert.Equal(1, job.Summary!.Logos); Assert.Equal(1, job.Summary.References);
            Assert.False(File.Exists(Path.Combine(store.DirectoryPath(job.Id), ".decrypted.zip")));
            Assert.False(File.Exists(Path.Combine(store.DirectoryPath(job.Id), ".references.zip")));
            Assert.NotNull(worker.Confirm(job.Id, actor, "1470", true, "IMPORTAR")); // agent is mandatory
            Heartbeat(store);
            Assert.NotNull(worker.Confirm(job.Id, Guid.NewGuid(), "1470", true, "IMPORTAR"));
            Assert.NotNull(worker.Confirm(job.Id, actor, "1470", false, "IMPORTAR"));
            Assert.NotNull(worker.Confirm(job.Id, actor, "1470", true, "DELETE"));
            using (coordinator.TryBegin()) Assert.NotNull(worker.Confirm(job.Id, actor, "1470", true, "IMPORTAR"));
            Assert.False(File.Exists(store.PendingPath));
            Assert.Null(worker.Confirm(job.Id, actor, "1470", true, "IMPORTAR"));
            Assert.Equal(RestoreJobState.Requested, store.Get(job.Id)!.State);
            Assert.True(store.IsMaintenance);
            var pending = await File.ReadAllTextAsync(store.PendingPath);
            Assert.DoesNotContain(ManualBackupTests.Password, pending);
            Assert.DoesNotContain(ManualBackupTests.PinKey, pending);
            Assert.DoesNotContain("\"1470\"", pending);
            var request = JsonSerializer.Deserialize<RestoreRequest>(pending)!;
            Assert.Equal(job.Summary.PackageSha256, request.PackageSha256);
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(ManualBackupTests.PinKey), "1470"u8.ToArray())).ToLowerInvariant(), request.AdminPinLookup);
            Assert.NotNull(worker.Confirm(job.Id, actor, "1470", true, "IMPORTAR"));
            using var backups = new ManualBackupService(fixture.Settings, new ManualBackupTests.FixtureBuilder(), TimeProvider.System, coordinator, store);
            Assert.Null(backups.Start(ManualBackupTests.Password).Id);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authentication_failure_never_produces_plaintext_staging(bool tamper)
    {
        using var fixture = new ManualBackupTests.BackupFixture();
        var bytes = await PackageAsync(fixture.Directory);
        if (tamper) bytes[45] ^= 1;
        var stage = Path.Combine(fixture.Directory, "stage"); Directory.CreateDirectory(stage);
        var source = Path.Combine(stage, "upload.webackup"); await File.WriteAllBytesAsync(source, bytes);
        var key = ManualBackupEncryption.DeriveKey(tamper ? ManualBackupTests.Password : "incorrect fixture password", RestorePackageInspector.ReadSalt(source));
        try { await Assert.ThrowsAsync<RestorePackageException>(() => new RestorePackageInspector().InspectAsync(stage, key, CancellationToken.None)); }
        finally { CryptographicOperations.ZeroMemory(key); }
        Assert.Single(Directory.EnumerateFiles(stage));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("reference-hash")]
    [InlineData("reference-pair")]
    [InlineData("paired-key")]
    [InlineData("undeclared")]
    [InlineData("secret-declaration")]
    public async Task Invalid_packages_are_rejected_and_the_worker_cleans_decrypted_secrets(string defect)
    {
        using var fixture = new ManualBackupTests.BackupFixture();
        var bytes = await PackageAsync(fixture.Directory, defect);
        var store = new RestoreJobStore(fixture.Settings);
        using var worker = new RestoreUploadService(fixture.Settings, store, new(), new(), TimeProvider.System);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var stream = new MemoryStream(bytes);
            var result = await worker.UploadAsync(Guid.NewGuid(), new FormFile(stream, 0, bytes.Length, "backup", "fixture.webackup"), ManualBackupTests.Password, CancellationToken.None);
            var job = await WaitAsync(store, result.Id!.Value);
            Assert.Equal(RestoreJobState.Failed, job.State);
            Assert.Equal("status.json", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(store.DirectoryPath(job.Id)))));
            Assert.DoesNotContain(ManualBackupTests.PinKey, await File.ReadAllTextAsync(Path.Combine(store.DirectoryPath(job.Id), "status.json")));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public void Expired_previews_and_corrupt_maintenance_requests_cannot_trigger_a_restore()
    {
        using var fixture = new ManualBackupTests.BackupFixture();
        var store = new RestoreJobStore(fixture.Settings); Heartbeat(store);
        var job = new RestoreJob(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-31), RestoreJobState.Ready,
            new(DateTimeOffset.UtcNow, "fixture", 100, 0, 0, new string('a', 64)));
        store.Save(job);
        using var worker = new RestoreUploadService(fixture.Settings, store, new(), new(), TimeProvider.System);
        Assert.NotNull(worker.Confirm(job.Id, job.ActorId, "1470", true, "IMPORTAR"));
        Assert.False(File.Exists(store.PendingPath));
        File.WriteAllText(store.MaintenancePath, "interrupted JSON");
        Assert.True(store.IsMaintenance); Assert.Null(store.ActiveId);
        RestoreJobStore.AtomicWrite(Path.Combine(store.Root, "agent.json"), new { SchemaVersion = 1, CheckedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4) });
        Assert.NotNull(store.AgentUnavailableReason(TimeProvider.System));
    }

    internal static void Heartbeat(RestoreJobStore store)
    {
        Directory.CreateDirectory(store.Root);
        RestoreJobStore.AtomicWrite(Path.Combine(store.Root, "agent.json"), new { SchemaVersion = 1, CheckedAtUtc = DateTimeOffset.UtcNow });
    }
    internal static async Task<RestoreJob> WaitAsync(RestoreJobStore store, Guid id)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = store.Get(id);
            if (job is not null && job.State != RestoreJobState.Validating) return job;
            await Task.Delay(25);
        }
        throw new TimeoutException();
    }

    internal static async Task<byte[]> PackageAsync(string directory, string? defect = null)
    {
        const string dump = "warehouseEPI-20261002-120000.dump";
        var image = "fixture image"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        var referenceName = hash + ".png";
        var references = Zip(new Dictionary<string, byte[]>
        {
            [referenceName] = image,
            ["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 1,
                DatabaseBackup = defect == "reference-pair" ? "other.dump" : dump,
                Files = new[] { new { Name = referenceName, Length = image.Length, Sha256 = defect == "reference-hash" ? new string('0', 64) : hash } }
            })
        });
        var files = new Dictionary<string, (string Kind, byte[] Data)>
        {
            ["database/" + dump] = ("database", "fixture dump"u8.ToArray()),
            ["references/warehouseEPI-20261002-120000-references.zip"] = ("references", references),
            ["branding/" + new string('a', 32) + ".png"] = ("branding", image),
            ["RESTORE.txt"] = ("instructions", "fixture"u8.ToArray())
        };
        if (defect == "path") files["../outside.txt"] = ("branding", image);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            PackageType = "WarehouseEPI-MigrationBackup",
            ContainsSecrets = false,
            RequiredExternalSecrets = defect == "secret-declaration" ? Array.Empty<string>() : new[] { "Security:PinLookupKey", "PostgreSQL credentials", "LAN CA PFX" },
            DatabaseName = "warehouseEPI",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ApplicationVersion = "10.0.0-fixture",
            Files = files.Select(item => new
            {
                Path = item.Key,
                item.Value.Kind,
                Length = item.Value.Data.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(item.Value.Data)).ToLowerInvariant()
            })
        });
        var content = files.ToDictionary(item => item.Key, item => item.Value.Data); content["manifest.json"] = manifest;
        if (defect == "undeclared") content["undeclared.txt"] = image;
        var migration = Zip(content);
        var secrets = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            PinLookupKey = ManualBackupTests.PinKey,
            MigrationPackageSha256 = defect == "paired-key" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(migration)).ToLowerInvariant()
        });
        var outer = Zip(new Dictionary<string, byte[]> { ["migration.zip"] = migration, ["secrets.json"] = secrets });
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".webackup");
        var salt = RandomNumberGenerator.GetBytes(16); var key = ManualBackupEncryption.DeriveKey(ManualBackupTests.Password, salt);
        try { await ManualBackupEncryption.WriteAsync(path, key, salt, (stream, token) => stream.WriteAsync(outer, token).AsTask(), CancellationToken.None); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return await File.ReadAllBytesAsync(path);
    }
    private static byte[] Zip(Dictionary<string, byte[]> entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var item in entries) { using var stream = zip.CreateEntry(item.Key).Open(); stream.Write(item.Value); }
        return memory.ToArray();
    }
}
