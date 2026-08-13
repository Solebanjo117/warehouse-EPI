using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Security;

public sealed class PinProtectorTests
{
    private const string LookupKey =
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Theory]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("12A4")]
    [InlineData("１２３４")]
    public void Normalize_rejects_invalid_pin(string pin)
    {
        var protector = new PinProtector(LookupKey);

        Assert.Throws<PinFormatException>(() => protector.Normalize(pin));
    }

    [Theory]
    [InlineData("0000")]
    [InlineData("12345678")]
    [InlineData(" 0123 ")]
    public void Normalize_accepts_four_to_eight_ascii_digits(string pin)
    {
        var protector = new PinProtector(LookupKey);

        var normalized = protector.Normalize(pin);

        Assert.Equal(pin.Trim(), normalized);
    }

    [Fact]
    public void Lookup_is_deterministic_and_has_64_hex_characters()
    {
        var protector = new PinProtector(LookupKey);

        var first = protector.CreateLookup("0123");
        var second = protector.CreateLookup("0123");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Hash_is_salted_and_verifies_only_the_correct_pin()
    {
        var protector = new PinProtector(LookupKey);

        var first = protector.Hash("0123");
        var second = protector.Hash("0123");

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("0123", first, StringComparison.Ordinal);
        Assert.True(protector.Verify("0123", first));
        Assert.False(protector.Verify("0124", first));
        Assert.False(protector.Verify("invalid", first));
        Assert.False(protector.Verify("0123", "not-a-valid-hash"));
    }

    [Fact]
    public void Constructor_rejects_short_lookup_key()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);

        Assert.Throws<InvalidOperationException>(() => new PinProtector(shortKey));
    }
}
