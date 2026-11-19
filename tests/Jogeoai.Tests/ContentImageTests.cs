using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Jogeoai.Tests;

/// <summary>编辑器图片（B-R5 / AC-B4）：魔数识别、大小上限、对象键隔离与跨租户读取判定；不连接数据库。</summary>
public sealed class ContentImageTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAAC0lEQVR42mNgAAIAAAUAAen63NgAAAAASUVORK5CYII=");

    private static (string ContentType, int? Width, int? Height) D(byte[] data) => ContentImageSniffer.Detect(data)!.Value;

    [Fact]
    public void MagicBytesIdentifySupportedFormatsAndDimensions()
    {
        Assert.Equal(("image/png", 2, 1), D(Png));

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x20, 0x00, 0x40, 0x03, 0x01, 0x22, 0x00, 0xFF, 0xD9];
        Assert.Equal(("image/jpeg", 64, 32), D(jpeg));

        byte[] gif = [.. "GIF89a"u8, 0x0A, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00];
        Assert.Equal(("image/gif", 10, 5), D(gif));

        byte[] webp = [.. "RIFF"u8, 0x00, 0x00, 0x00, 0x00, .. "WEBP"u8, .. "VP8X"u8, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x63, 0x00, 0x00, 0x31, 0x00, 0x00];
        Assert.Equal(("image/webp", 100, 50), D(webp));
    }

    [Theory]
    [InlineData("hello, I am a text file renamed to .png")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"></svg>")]
    [InlineData("%PDF-1.7")]
    public async Task ForgedExtensionsAndUnsupportedFormatsAreRejected(string content)
    {
        Assert.Null(ContentImageSniffer.Detect(System.Text.Encoding.UTF8.GetBytes(content)));
        var storage = new RecordingStorage();
        var service = new ContentImageService(null!, storage);
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            service.UploadAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), System.Text.Encoding.UTF8.GetBytes(content)));
        Assert.Equal((ArticleEditorErrorCodes.ImageTypeUnsupported, 415), (error.Code, error.StatusCode));
        Assert.Empty(storage.Keys);
    }

    [Fact]
    public async Task ImagesOverFiveMegabytesAndEmptyFilesAreRejectedBeforeStorage()
    {
        var storage = new RecordingStorage();
        var service = new ContentImageService(null!, storage);
        var big = new byte[ArticleEditorLimits.ImageMaxBytes + 1];
        Png.CopyTo(big, 0);
        var tooLarge = await Assert.ThrowsAsync<CollectorStoreException>(() => service.UploadAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), big));
        Assert.Equal((ArticleEditorErrorCodes.ImageTooLarge, 413), (tooLarge.Code, tooLarge.StatusCode));
        var empty = await Assert.ThrowsAsync<CollectorStoreException>(() => service.UploadAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ReadOnlyMemory<byte>.Empty));
        Assert.Equal(415, empty.StatusCode);
        Assert.Empty(storage.Keys);
    }

    [Fact]
    public void CrossTenantReadsAreRefusedAndHeaderMustMatchImageTenant()
    {
        var own = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.True(ContentImagesController.CanRead([own], null, own));
        Assert.True(ContentImagesController.CanRead([own, other], own.ToString(), own));
        Assert.False(ContentImagesController.CanRead([own], null, other));
        Assert.False(ContentImagesController.CanRead([own, other], other.ToString(), own));
        Assert.False(ContentImagesController.CanRead([own], "not-a-guid", own));
        Assert.False(ContentImagesController.CanRead([], null, own));
    }

    [Fact]
    public async Task ImageKeysAreIsolatedFromKnowledgeObjectsInLocalStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jogeoai-content-image-test-{Guid.NewGuid():N}");
        try
        {
            var storage = new LocalObjectStorage(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Knowledge:StorageRoot"] = root }).Build());
            var tenant = Guid.NewGuid();
            var image = Guid.NewGuid();
            var key = ContentImageKeys.For(tenant, image);
            Assert.Equal($"content-images/{tenant:N}/{image:N}", key);
            Assert.True(ContentImageKeys.IsValid(key));
            await storage.PutAtAsync(key, new MemoryStream(Png));
            await using (var read = await storage.OpenReadAsync(key))
            {
                Assert.NotNull(read);
                using var copy = new MemoryStream();
                await read!.CopyToAsync(copy);
                Assert.Equal(Png, copy.ToArray());
            }

            // Knowledge objects keep their own layout and cannot be written through the image entry point.
            var knowledgeKey = await storage.PutAsync(tenant, Guid.NewGuid(), new MemoryStream([1, 2, 3]));
            Assert.False(ContentImageKeys.IsValid(knowledgeKey));
            Assert.EndsWith("/source", knowledgeKey, StringComparison.Ordinal);
            foreach (var bad in new[] { knowledgeKey, "content-images/../x", $"content-images/{tenant:N}", $"content-images/{tenant:N}/{image:N}/extra", $"content-images/{tenant}/{image}" })
            {
                Assert.False(ContentImageKeys.IsValid(bad));
                await Assert.ThrowsAsync<InvalidOperationException>(() => storage.PutAtAsync(bad, new MemoryStream([1])));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingStorage : IObjectStorage
    {
        public List<string> Keys { get; } = [];
        public Task<string> PutAsync(Guid tenantId, Guid versionId, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PutAtAsync(string objectKey, Stream content, CancellationToken cancellationToken = default) { Keys.Add(objectKey); return Task.CompletedTask; }
        public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
