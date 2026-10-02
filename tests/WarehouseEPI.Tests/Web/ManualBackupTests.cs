using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using WarehouseEPI.Web.Backups;

namespace WarehouseEPI.Tests.Web;

public sealed class ManualBackupTests
{
    internal const string Password = "Backup fixture phrase 2026";
    internal const string PinKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Portable_encryption_hides_the_pin_key_and_authenticates_header_and_ciphertext()
    {
        using var fixture = new BackupFixture();
        var path = Path.Combine(fixture.Directory, "encrypted.webackup");
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = ManualBackupEncryption.DeriveKey(Password, salt);
        try
        {
            await ManualBackupEncryption.WriteAsync(path, key, salt,
                (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes(PinKey), token).AsTask(), CancellationToken.None);
            var encrypted = await File.ReadAllBytesAsync(path);
            Assert.Equal("WEPBK001", Encoding.ASCII.GetString(encrypted[..8]));
            Assert.Equal(ManualBackupEncryption.Iterations, BinaryPrimitives.ReadInt32LittleEndian(encrypted.AsSpan(40, 4)));
            Assert.DoesNotContain(PinKey, Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);
            Assert.Equal(PinKey, Encoding.UTF8.GetString(Decrypt(encrypted, Password)));
            Assert.Throws<CryptographicException>(() => Decrypt(encrypted, "incorrect backup password"));
            encrypted[45] ^= 1;
            Assert.Throws<CryptographicException>(() => Decrypt(encrypted, Password));
            encrypted[45] ^= 1;
            encrypted[24] ^= 1;
            Assert.Throws<CryptographicException>(() => Decrypt(encrypted, Password));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task Worker_serializes_requests_persists_downloads_and_clears_password_material()
    {
        using var fixture = new BackupFixture();
        var builder = new FixtureBuilder { Hold = true };
        using var worker = new ManualBackupService(fixture.Settings, builder, TimeProvider.System);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var first = worker.Start(Password);
            Assert.NotNull(first.Id);
            await builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(worker.Start(Password).Id);
            Assert.Null(worker.OpenDownload(first.Id.Value));
            builder.Release.TrySetResult();
            await WaitForResultAsync(worker);
            Assert.Equal(ManualBackupState.Ready, worker.Current!.State);
            Assert.Equal(1, builder.Calls);
            Assert.All(builder.LastKey!, value => Assert.Equal(0, value));
            var record = Assert.Single(worker.History());
            using var download = worker.OpenDownload(record.Id);
            Assert.NotNull(download);
            Assert.True(download.Length > 0);
            using var restarted = new ManualBackupService(fixture.Settings, builder, TimeProvider.System);
            Assert.Equal(record, Assert.Single(restarted.History()));
            Assert.Null(restarted.OpenDownload(Guid.NewGuid()));
            File.Delete(fixture.Settings.PgDumpPath);
            Assert.NotNull(restarted.UnavailableReason);
            using var recoveryDownload = restarted.OpenDownload(record.Id);
            Assert.NotNull(recoveryDownload);
        }
        finally { builder.Release.TrySetResult(); await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Failed_jobs_are_not_downloadable_and_remove_plaintext_staging()
    {
        using var fixture = new BackupFixture();
        var builder = new FixtureBuilder { Fail = true };
        using var worker = new ManualBackupService(fixture.Settings, builder, TimeProvider.System);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.NotNull(worker.Start(Password).Id);
            await WaitForResultAsync(worker);
            Assert.Equal(ManualBackupState.Failed, worker.Current!.State);
            Assert.Empty(worker.History());
            Assert.All(builder.LastKey!, value => Assert.Equal(0, value));
            Assert.Empty(System.IO.Directory.EnumerateDirectories(fixture.Directory, ".work-*"));
            Assert.Empty(System.IO.Directory.EnumerateFiles(fixture.Directory, "*.webackup*"));
            Assert.DoesNotContain(PinKey, JsonSerializer.Serialize(worker.Current), StringComparison.Ordinal);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Retention_preserves_five_successful_backups_and_unrelated_files()
    {
        using var fixture = new BackupFixture();
        var unrelated = Path.Combine(fixture.Directory, "IT-notes.txt");
        await File.WriteAllTextAsync(unrelated, "retain");
        using var worker = new ManualBackupService(fixture.Settings, new FixtureBuilder(), TimeProvider.System);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 6; i++)
            {
                Assert.NotNull(worker.Start(Password).Id);
                await WaitForResultAsync(worker);
                Assert.Equal(ManualBackupState.Ready, worker.Current!.State);
            }
            Assert.Equal(5, worker.History().Count);
            Assert.True(File.Exists(unrelated));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    internal static async Task WaitForResultAsync(ManualBackupService worker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (worker.Current?.State == ManualBackupState.Running) await Task.Delay(20, timeout.Token);
    }

    internal static byte[] Decrypt(byte[] data, string password)
    {
        var key = ManualBackupEncryption.DeriveKey(password, data[8..24]);
        try
        {
            var actual = HMACSHA256.HashData(key.AsSpan(32), data.AsSpan(0, data.Length - 32));
            if (!CryptographicOperations.FixedTimeEquals(actual, data.AsSpan(data.Length - 32)))
                throw new CryptographicException();
            using var aes = Aes.Create();
            aes.Key = key[..32]; aes.IV = data[24..40];
            return aes.DecryptCbc(data.AsSpan(44, data.Length - 76), aes.IV);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal sealed class FixtureBuilder : IManualBackupBuilder
    {
        public bool Hold { get; init; }
        public bool Fail { get; init; }
        public int Calls { get; private set; }
        public byte[]? LastKey { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task BuildAsync(string workingDirectory, string encryptedPath, DateTimeOffset startedAt,
            byte[] key, byte[] salt, CancellationToken cancellationToken)
        {
            Calls++; LastKey = key; Started.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "database.dump"), "private fixture", cancellationToken);
            if (Fail) throw new IOException("Sensitive database detail " + PinKey);
            await ManualBackupEncryption.WriteAsync(encryptedPath, key, salt,
                (stream, token) => stream.WriteAsync("fixture"u8.ToArray(), token).AsTask(), cancellationToken);
        }
    }

    internal sealed class BackupFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "warehouse-epi-backup-test-" + Guid.NewGuid().ToString("N"));
        public ManualBackupSettings Settings { get; }
        public Dictionary<string, string?> Configuration { get; }
        public BackupFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tool = Path.Combine(Directory, "fixture-tool");
            File.WriteAllText(tool, "fixture");
            Configuration = new()
            {
                ["Backups:ManualDirectory"] = Directory,
                ["Backups:PgDumpPath"] = tool,
                ["Backups:PgRestorePath"] = tool,
                ["Security:PinLookupKey"] = PinKey
            };
            Settings = new(new TestEnvironment(), new ConfigurationBuilder().AddInMemoryCollection(Configuration).Build());
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    internal sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "WarehouseEPI.Web";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
