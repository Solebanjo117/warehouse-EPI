using System.Buffers.Binary;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WarehouseEPI.Web.Locations;

namespace WarehouseEPI.Tests.Web;

public sealed class WarehouseMapReferenceStorageTests
{
    [Fact]
    public async Task Storage_stages_signature_checked_image_with_hash_dimensions_and_user_scope()
    {
        var root = Path.Combine(Path.GetTempPath(), $"warehouse-map-reference-{Guid.NewGuid():N}");
        try
        {
            var storage = CreateStorage(root);
            await using var stream = new MemoryStream(Png(640, 480));
            var file = new FormFile(stream, 0, stream.Length, "referenceImage", "plano.png");
            var user = Guid.NewGuid();

            var staged = await storage.StageAsync(file, user, CancellationToken.None);

            Assert.Equal("image/png", staged.ContentType);
            Assert.Equal(640, staged.PixelWidth);
            Assert.Equal(480, staged.PixelHeight);
            Assert.Matches("^[a-f0-9]{64}\\.png$", staged.StoredFileName);
            Assert.NotNull(await storage.GetStageAsync(staged.Token, user, CancellationToken.None));
            Assert.Null(await storage.GetStageAsync(staged.Token, Guid.NewGuid(), CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Storage_rejects_content_that_only_claims_to_be_an_image()
    {
        var root = Path.Combine(Path.GetTempPath(), $"warehouse-map-reference-{Guid.NewGuid():N}");
        try
        {
            var storage = CreateStorage(root);
            await using var stream = new MemoryStream("not-an-image"u8.ToArray());
            var file = new FormFile(stream, 0, stream.Length, "referenceImage", "plano.png")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/png"
            };

            await Assert.ThrowsAsync<WarehouseMapReferenceValidationException>(() =>
                storage.StageAsync(file, Guid.NewGuid(), CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static WarehouseMapReferenceStorage CreateStorage(string root)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WarehouseMap:ReferenceStorageDirectory"] = root
        }).Build();
        return new WarehouseMapReferenceStorage(new TestEnvironment(), configuration, TimeProvider.System);
    }

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[45];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "WarehouseEPI.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
