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
    public async Task PdfImageOverDeclaredPixelLimitIsSkippedBeforeDecodingAndMarkedTruncated()
    {
        // 第 1 页：一张声明 10000x10000（1 亿像素，实际只有 4 字节数据）的图片和一张正常的 2x2 图片；第 2 页只有超限图片。
        var declaredHuge = new PdfImageSpec(10_000, 10_000, [0x00, 0x40, 0x80, 0xFF]);
        var small = new PdfImageSpec(2, 2, [0x00, 0x40, 0x80, 0xFF]);
        var pdf = BuildPdf([new PdfPageSpec(Images: [declaredHuge, small]), new PdfPageSpec(Images: [declaredHuge])]);
        Assert.True((long)declaredHuge.Width * declaredHuge.Height > new KnowledgeExtractionLimits().MaxImagePixels);
        await using var stream = new MemoryStream(pdf);

        var result = await new BasicDocumentTextExtractor().ExtractAsync(stream, ".pdf", "application/pdf");

        // 这些图片是未压缩的原始采样，取页图时需要解码成 PNG：超限的不解码、不参与「每页最大图」的选择。
        // 第 1 页退回到 2x2 图片，第 2 页没有页图，整体标记截断。
        Assert.Equal("image", result.ContentKind);
        Assert.Equal(2, result.PageCount);
        var image = Assert.Single(result.Images!);
        Assert.Equal("page:1", image.Location);
        Assert.Equal("image/png", image.MediaType);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task PdfImagePixelLimitIsConfigurableAndComparesDeclaredWidthTimesHeight()
    {
        // 上限 3 像素：1x1 保留，2x2（4 像素）跳过。同一张 2x2 图片在默认上限下被接受（见上面的页图用例）。
        var pdf = BuildPdf([
            new PdfPageSpec(Images: [new PdfImageSpec(1, 1, [0x80])]),
            new PdfPageSpec(Images: [new PdfImageSpec(2, 2, [0x00, 0x40, 0x80, 0xFF])])]);
        var limited = new BasicDocumentTextExtractor(new KnowledgeExtractionLimits { MaxImagePixels = 3 });
        await using var limitedStream = new MemoryStream(pdf);

        var result = await limited.ExtractAsync(limitedStream, ".pdf", null);

        Assert.Equal("page:1", Assert.Single(result.Images!).Location);
        Assert.True(result.Truncated);

        // 上限恰好等于声明像素时不算超限。
        var exact = new BasicDocumentTextExtractor(new KnowledgeExtractionLimits { MaxImagePixels = 4 });
        await using var exactStream = new MemoryStream(pdf);
        var accepted = await exact.ExtractAsync(exactStream, ".pdf", null);
        Assert.Equal(["page:1", "page:2"], accepted.Images!.Select(item => item.Location));
        Assert.False(accepted.Truncated);
    }

    [Fact]
    public async Task PdfJpegPassThroughImageOverDeclaredPixelLimitIsStillReturnedBecauseItIsNotDecoded()
    {
        // 600 dpi 的 A3 扫描页约 7000 万像素。DCT 编码的页图不解码、原样送出，像素上限不适用，只受 10 MB 字节上限约束。
        var scan = new PdfImageSpec(9_922, 7_016, RawJpeg(64), Filter: "DCTDecode");
        Assert.True((long)scan.Width * scan.Height > new KnowledgeExtractionLimits().MaxImagePixels);
        await using var stream = new MemoryStream(BuildPdf([new PdfPageSpec(Images: [scan])]));

        var result = await new BasicDocumentTextExtractor().ExtractAsync(stream, ".pdf", "application/pdf");

        Assert.Equal("image", result.ContentKind);
        var image = Assert.Single(result.Images!);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal("page:1", image.Location);
        Assert.Equal(scan.Data, image.Bytes);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task PdfPixelLimitSkipsOnlyTheOverLimitImageThatWouldNeedDecoding()
    {
        // 同一页上：一张声明 4 亿像素、需要解码的图片（声明像素最大）和一张声明 7000 万像素的原始 JPEG。
        // 前者在解码前跳过并标记截断；后者不解码，照常作为这一页的页图返回。
        var needsDecoding = new PdfImageSpec(20_000, 20_000, [0x00, 0x40, 0x80, 0xFF]);
        var scan = new PdfImageSpec(9_922, 7_016, RawJpeg(64), Filter: "DCTDecode");
        await using var stream = new MemoryStream(BuildPdf([new PdfPageSpec(Images: [needsDecoding, scan])]));

        var result = await new BasicDocumentTextExtractor().ExtractAsync(stream, ".pdf", "application/pdf");

        var image = Assert.Single(result.Images!);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(scan.Data, image.Bytes);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task PdfOverLimitImageIsNeverDecodedEvenWhenDecodingWouldSucceed()
    {
        // 2x2 未压缩灰度图，采样数据恰好以 JPEG 文件头开始：既能解码成 PNG，也满足「原始字节是 JPEG」。
        // 用它区分两条路径：未超限时解码成 PNG；超限时不调用解码，原始字节原样送出。
        var ambiguous = new PdfImageSpec(2, 2, [0xFF, 0xD8, 0xFF, 0x00]);
        var pdf = BuildPdf([new PdfPageSpec(Images: [ambiguous])]);

        await using var withinStream = new MemoryStream(pdf);
        var within = await new BasicDocumentTextExtractor(new KnowledgeExtractionLimits { MaxImagePixels = 4 })
            .ExtractAsync(withinStream, ".pdf", null);
        Assert.Equal("image/png", Assert.Single(within.Images!).MediaType);

        await using var overStream = new MemoryStream(pdf);
        var over = await new BasicDocumentTextExtractor(new KnowledgeExtractionLimits { MaxImagePixels = 3 })
            .ExtractAsync(overStream, ".pdf", null);
        var image = Assert.Single(over.Images!);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(ambiguous.Data, image.Bytes);
        Assert.False(over.Truncated);
    }

    [Fact]
    public async Task PdfJpegPassThroughImageIsStillBoundedByTheByteCap()
    {
        // 第 1 页：声明像素超限但字节很小的原始 JPEG，照常返回。第 2 页：超过 10 MB 的原始 JPEG，丢弃并标记截断。
        var scan = new PdfImageSpec(9_922, 7_016, RawJpeg(64), Filter: "DCTDecode");
        var oversized = new PdfImageSpec(9_922, 7_016, RawJpeg(BasicDocumentTextExtractor.MaxImageBytes + 1), Filter: "DCTDecode");
        await using var stream = new MemoryStream(BuildPdf([new PdfPageSpec(Images: [scan]), new PdfPageSpec(Images: [oversized])]));

        var result = await new BasicDocumentTextExtractor().ExtractAsync(stream, ".pdf", "application/pdf");

        var image = Assert.Single(result.Images!);
        Assert.Equal("page:1", image.Location);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.True(result.Truncated);
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

    /// <summary>
    /// 测试用 PDF 的一张内嵌 DeviceGray 图片：声明宽高可以与实际数据长度不一致，用来构造「声明像素很大」的样本。
    /// Filter 为空表示未压缩的原始采样（取页图时需要解码成 PNG）；"DCTDecode" 表示数据本身是 JPEG（不解码、原样送出）。
    /// </summary>
    internal sealed record PdfImageSpec(int Width, int Height, byte[] Data, string? Filter = null);

    /// <summary>以 JPEG 文件头开始、其余为填充的指定长度字节。解析器只看文件头、不解码，所以不需要是完整的 JPEG。</summary>
    private static byte[] RawJpeg(int length)
    {
        var bytes = new byte[length];
        JpegMagic.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>测试用 PDF 的一页：若干行 ASCII 文字（Helvetica）与若干张内嵌图片。</summary>
    internal sealed record PdfPageSpec(IReadOnlyList<string>? Lines = null, IReadOnlyList<PdfImageSpec>? Images = null);

    /// <summary>生成每页 3 行、每行 100 个字母的文字页，足够超过「文本不足改用页图」的 200 字门槛。</summary>
    internal static PdfPageSpec TextPage(int pageIndex)
    {
        var letter = (char)('a' + pageIndex % 26);
        return new PdfPageSpec([new string(letter, 100), new string(letter, 100), new string(letter, 100)]);
    }

    /// <summary>在测试代码里手工拼出多页 PDF（不依赖外部样本文件）。文字只允许字母、数字和空格。</summary>
    internal static byte[] BuildPdf(IReadOnlyList<PdfPageSpec> pages)
    {
        static byte[] A(string value) => Encoding.ASCII.GetBytes(value);
        static byte[] Concat(params byte[][] parts) { var m = new MemoryStream(); foreach (var p in parts) m.Write(p); return m.ToArray(); }
        // 对象 1 目录、2 页树、3 字体，其后每页依次是页对象、内容流和该页的图片。
        var bodies = new List<byte[]> { A("<< /Type /Catalog /Pages 2 0 R >>"), Array.Empty<byte>(), A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>") };
        var kids = new List<int>();
        foreach (var page in pages)
        {
            var pageNumber = bodies.Count + 1;
            var contentNumber = pageNumber + 1;
            var images = page.Images ?? [];
            var content = new StringBuilder();
            if (page.Lines is { Count: > 0 } lines)
            {
                content.Append("BT /F1 10 Tf 12 TL 10 180 Td");
                foreach (var line in lines)
                {
                    Assert.True(line.All(character => char.IsAsciiLetterOrDigit(character) || character == ' '), "测试 PDF 的文字只允许字母、数字和空格。");
                    content.Append(" (").Append(line).Append(") Tj T*");
                }
                content.Append(" ET");
            }
            var xobjects = new StringBuilder();
            for (var i = 0; i < images.Count; i++)
            {
                content.Append($" q 100 0 0 100 0 0 cm /Im{i + 1} Do Q");
                xobjects.Append($"/Im{i + 1} {contentNumber + 1 + i} 0 R ");
            }
            var contentBytes = A(content.ToString().Trim());
            kids.Add(pageNumber);
            bodies.Add(A($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 3 0 R >> /XObject << {xobjects}>> >> /Contents {contentNumber} 0 R >>"));
            bodies.Add(Concat(A($"<< /Length {contentBytes.Length} >>\nstream\n"), contentBytes, A("\nendstream")));
            foreach (var image in images)
                bodies.Add(Concat(A($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8{(image.Filter is null ? string.Empty : $" /Filter /{image.Filter}")} /Length {image.Data.Length} >>\nstream\n"), image.Data, A("\nendstream")));
        }
        bodies[1] = A($"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(kid => $"{kid} 0 R"))}] /Count {kids.Count} >>");

        var output = new MemoryStream();
        void Write(byte[] value) => output.Write(value, 0, value.Length);
        Write(A("%PDF-1.4\n"));
        var offsets = new List<long>();
        for (var i = 0; i < bodies.Count; i++)
        {
            offsets.Add(output.Position);
            Write(A($"{i + 1} 0 obj\n")); Write(bodies[i]); Write(A("\nendobj\n"));
        }
        var xref = output.Position;
        Write(A($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets) Write(A($"{offset:D10} 00000 n \n"));
        Write(A($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return output.ToArray();
    }
}
