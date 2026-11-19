using System.Text;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>图片文件与无文本层 PDF 的解析：返回 image 类型与页图，不再被拒绝。</summary>
public sealed class BasicDocumentTextExtractorImageTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF, 0xE0];

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData("PNG")]
    public void ImageExtensionsAreAccepted(string extension)
        => Assert.True(new BasicDocumentTextExtractor().CanExtract(extension, null));

    [Fact]
    public async Task PngReturnsImageKindWithZeroSegmentsAndOneImage()
    {
        var bytes = PngMagic.Concat(new byte[] { 1, 2, 3, 4, 5 }).ToArray();
        await using var stream = new MemoryStream(bytes);

        var result = await new BasicDocumentTextExtractor().ExtractAsync(stream, ".png", "image/png");

        Assert.Equal("image", result.ContentKind);
        Assert.Empty(result.Segments);
        Assert.Equal(0, result.CharacterCount);
        var image = Assert.Single(result.Images!);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(1, image.Ordinal);
        Assert.Equal(bytes, image.Bytes);
        Assert.False(result.Truncated);
        Assert.Empty(new KnowledgeChunker().Chunk(Guid.NewGuid(), result));
    }

    [Fact]
    public async Task JpegReturnsImageKindAndInvalidMagicIsRejected()
    {
        await using var jpeg = new MemoryStream(JpegMagic.Concat(new byte[] { 9, 9 }).ToArray());
        var result = await new BasicDocumentTextExtractor().ExtractAsync(jpeg, ".jpg", null);
        Assert.Equal("image", result.ContentKind);
        Assert.Equal("image/jpeg", Assert.Single(result.Images!).MediaType);

        await using var fake = new MemoryStream(Encoding.ASCII.GetBytes("not an image"));
        var exception = await Assert.ThrowsAsync<KnowledgeExtractionException>(() =>
            new BasicDocumentTextExtractor().ExtractAsync(fake, ".png", null));
        Assert.Equal("KNOWLEDGE_PARSE_FAILED", exception.Code);
    }

    [Fact]
    public async Task OversizedImageIsDroppedAndMarkedTruncated()
    {
        var bytes = new byte[BasicDocumentTextExtractor.MaxImageBytes + 1];
        PngMagic.CopyTo(bytes, 0);
        await using var stream = new MemoryStream(bytes);

        var result = await new BasicDocumentTextExtractor().ExtractAsync(stream, ".png", null);

        Assert.Equal("image", result.ContentKind);
        Assert.Empty(result.Images!);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task PdfWithoutTextLayerReturnsEmbeddedPageImages()
    {
        await using var stream = new MemoryStream(BuildImageOnlyPdf());

        var result = await new BasicDocumentTextExtractor().ExtractAsync(stream, ".pdf", "application/pdf");

        Assert.Equal("image", result.ContentKind);
        Assert.Equal(1, result.PageCount);
        Assert.True(result.CharacterCount < BasicDocumentTextExtractor.MinimumPdfTextCharacters);
        var image = Assert.Single(result.Images!);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("page:1", image.Location);
        Assert.Equal(0x89, image.Bytes[0]);
        Assert.Equal((byte)'P', image.Bytes[1]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task TextDocumentsKeepTextKindWithoutImages()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# 产品\n" + new string('参', 300)));
        var result = await new BasicDocumentTextExtractor().ExtractAsync(stream, ".md", "text/markdown");
        Assert.Equal("text", result.ContentKind);
        Assert.Null(result.Images);
        Assert.True(result.CharacterCount >= 300);
    }

    /// <summary>手工构造只含一张 2x2 DeviceGray 图片、没有任何文字的单页 PDF。</summary>
    internal static byte[] BuildImageOnlyPdf()
    {
        static byte[] A(string value) => Encoding.ASCII.GetBytes(value);
        static byte[] Concat(params byte[][] parts) { var m = new MemoryStream(); foreach (var p in parts) m.Write(p); return m.ToArray(); }
        var content = A("q 200 0 0 200 0 0 cm /Im1 Do Q");
        var pixels = new byte[] { 0x00, 0x40, 0x80, 0xFF };
        var objects = new List<byte[]>
        {
            A("<< /Type /Catalog /Pages 2 0 R >>"),
            A("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            A("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Concat(A($"<< /Length {content.Length} >>\nstream\n"), content, A("\nendstream")),
            Concat(A($"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length {pixels.Length} >>\nstream\n"), pixels, A("\nendstream")),
        };
        var output = new MemoryStream();
        void Write(byte[] value) => output.Write(value, 0, value.Length);
        Write(A("%PDF-1.4\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write(A($"{i + 1} 0 obj\n")); Write(objects[i]); Write(A("\nendobj\n"));
        }
        var xref = output.Position;
        Write(A($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets) Write(A($"{offset:D10} 00000 n \n"));
        Write(A($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return output.ToArray();
    }
}
