using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WarehouseEPI.Infrastructure.Security;

public sealed class PinProtector
{
    public const int MinimumLength = 4;
    public const int MaximumLength = 8;

    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string HashVersion = "v1";

    private readonly byte[] lookupKey;

    public PinProtector(string base64LookupKey)
    {
        try
        {
            lookupKey = Convert.FromBase64String(base64LookupKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Security:PinLookupKey debe ser una clave Base64 válida.",
                exception);
        }

        if (lookupKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Security:PinLookupKey debe contener al menos 32 bytes.");
        }
    }

    public string Normalize(string pin)
    {
        var normalized = pin.Trim();

        if (normalized.Length is < MinimumLength or > MaximumLength ||
            normalized.Any(character => character is < '0' or > '9'))
        {
            throw new PinFormatException();
        }

        return normalized;
    }

    public string CreateLookup(string pin)
    {
        var normalized = Normalize(pin);
        using var hmac = new HMACSHA256(lookupKey);
        var lookup = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));

        return Convert.ToHexStringLower(lookup);
    }

    public string Hash(string pin)
    {
        var normalized = Normalize(pin);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            normalized,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return string.Join(
            '$',
            HashVersion,
            Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool Verify(string pin, string encodedHash)
    {
        string normalized;

        try
        {
            normalized = Normalize(pin);
        }
        catch (PinFormatException)
        {
            return false;
        }

        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != HashVersion ||
            !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var iterations) ||
            iterations < 100_000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expectedHash = Convert.FromBase64String(parts[3]);
            var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                normalized,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
