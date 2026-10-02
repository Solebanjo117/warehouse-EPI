using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WarehouseEPI.Web.Backups;

// Portable format: magic (8), salt (16), IV (16), iterations LE (4), CBC ciphertext, HMAC (32).
// The offline recovery tool authenticates the whole header and ciphertext BEFORE decrypting.
public static class ManualBackupEncryption
{
    public const int Iterations = 600_000;
    public static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 64);

    public static async Task WriteAsync(string path, byte[] key, byte[] salt,
        Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken)
    {
        var header = new byte[44];
        "WEPBK001"u8.CopyTo(header);
        salt.CopyTo(header, 8);
        RandomNumberGenerator.Fill(header.AsSpan(24, 16));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), Iterations);
        using var aes = Aes.Create();
        aes.Key = key[..32];
        aes.IV = header[24..40];
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
        await file.WriteAsync(header, cancellationToken);
        await using (var encrypted = new CryptoStream(file, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
        {
            await writeContent(encrypted, cancellationToken);
            await encrypted.FlushFinalBlockAsync(cancellationToken);
        }
        await file.FlushAsync(cancellationToken);
        file.Position = 0;
        using var mac = new HMACSHA256(key[32..]);
        var tag = await mac.ComputeHashAsync(file, cancellationToken);
        await file.WriteAsync(tag, cancellationToken);
        await file.FlushAsync(cancellationToken);
    }
}
