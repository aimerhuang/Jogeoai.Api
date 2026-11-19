using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Configuration;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Jogeoai.Infrastructure;

/// <summary>本地开发用对象存储；生产环境可替换为 S3 兼容实现。</summary>
public sealed class LocalObjectStorage : IObjectStorage
{
    private readonly string root;

    /// <summary>按配置初始化本地对象存储。</summary>
    public LocalObjectStorage(IConfiguration configuration)
    {
        var configured = configuration["Knowledge:StorageRoot"];
        root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? ".data/knowledge" : configured);
        Directory.CreateDirectory(root);
    }

    /// <inheritdoc />
    public async Task<string> PutAsync(Guid tenantId, Guid versionId, Stream content,
        CancellationToken cancellationToken = default)
    {
        var key = $"{tenantId:N}/{versionId:N}/source";
        var path = Path.Combine(root, tenantId.ToString("N"), versionId.ToString("N"), "source");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await content.CopyToAsync(output, cancellationToken);
            return key;
        }
        catch
        {
            // PutAsync is the object-storage transaction boundary.  If the
            // caller never receives a key, it cannot compensate a partial
            // object, so remove it here on cancellation or write failure.
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort cleanup */ }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task PutAtAsync(string objectKey, Stream content, CancellationToken cancellationToken = default)
    {
        if (!ContentImageKeys.IsValid(objectKey)) throw new InvalidOperationException("Invalid object key.");
        var path = Resolve(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await content.CopyToAsync(output, cancellationToken);
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort cleanup */ }
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var path = Resolve(objectKey);
        if (!File.Exists(path)) return Task.FromResult<Stream?>(null);
        Stream result = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<Stream?>(result);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var path = Resolve(objectKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains("..", StringComparison.Ordinal)
            || key.StartsWith('/') || key.Contains('\\'))
            throw new InvalidOperationException("Invalid object key.");
        var path = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid object key.");
        return path;
    }
}

/// <summary>PDF、DOCX、Markdown、TXT 的受限正文解析器；JPG / PNG 与无文本层的 PDF 以图片内容返回供多模态识别。</summary>
public sealed class BasicDocumentTextExtractor : IDocumentTextExtractor
{
    private static readonly HashSet<string> Supported = [".pdf", ".docx", ".md", ".markdown", ".txt", ".jpg", ".jpeg", ".png"];
    private const int MaxCharacters = 10_000_000;
    /// <summary>PDF 文本少于该字数时改用内嵌页图识别。</summary>
    public const int MinimumPdfTextCharacters = 200;
    /// <summary>单次最多提取的页图张数。</summary>
    public const int MaxImages = 20;
    /// <summary>单张图片字节上限。</summary>
    public const int MaxImageBytes = 10 * 1024 * 1024;

    /// <inheritdoc />
    public bool CanExtract(string extension, string? contentType) => Supported.Contains(NormalizeExtension(extension));

    /// <inheritdoc />
    public async Task<ExtractedDocument> ExtractAsync(Stream content, string extension, string? contentType,
        CancellationToken cancellationToken = default)
    {
        extension = NormalizeExtension(extension);
        if (!Supported.Contains(extension)) throw new KnowledgeExtractionException("KNOWLEDGE_UNSUPPORTED_FILE_TYPE", "暂不支持该文件格式。", false);
        await using var buffer = new MemoryStream();
        await CopyWithLimitAsync(content, buffer, 100L * 1024 * 1024, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0) throw new KnowledgeExtractionException("KNOWLEDGE_EMPTY_CONTENT", "文件内容为空。", false);
        ExtractedDocument result = extension switch
        {
            ".txt" or ".md" or ".markdown" => ParsePlainText(bytes),
            ".docx" => ParseDocx(bytes),
            ".pdf" => ParsePdf(bytes),
            ".jpg" or ".jpeg" => ParseImage(bytes, "image/jpeg"),
            ".png" => ParseImage(bytes, "image/png"),
            _ => throw new KnowledgeExtractionException("KNOWLEDGE_UNSUPPORTED_FILE_TYPE", "暂不支持该文件格式。", false)
        };
        if (result.ContentKind != "image" && (result.CharacterCount == 0 || result.Segments.Count == 0))
            throw new KnowledgeExtractionException("KNOWLEDGE_EMPTY_CONTENT", "文件未解析出正文。", false);
        if (result.CharacterCount > MaxCharacters)
            throw new KnowledgeExtractionException("KNOWLEDGE_CONTENT_TOO_LARGE", "文件正文超过安全上限。", false);
        return result;
    }

    private static ExtractedDocument ParseImage(byte[] bytes, string mediaType)
    {
        var isJpeg = mediaType == "image/jpeg";
        var validMagic = isJpeg
            ? bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF
            : bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
        if (!validMagic) throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "图片文件结构无效。", false);
        // 图片没有可检索文本：0 段落、0 字符，由 content_kind=image 允许直接发布。
        if (bytes.Length > MaxImageBytes)
            return new ExtractedDocument([], 0, null, "image", [], true);
        return new ExtractedDocument([], 0, null, "image", [new ExtractedImage(1, mediaType, bytes, "image:1")], false);
    }

    private static ExtractedDocument ParsePlainText(byte[] bytes)
    {
        var text = DecodeText(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = text.Split('\n');
        var segments = new List<ExtractedTextSegment>();
        string? heading = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) heading = line.TrimStart('#').Trim();
            segments.Add(new ExtractedTextSegment(line, heading, $"line:{i + 1}"));
        }
        return new ExtractedDocument(segments, segments.Sum(item => item.Text.Length));
    }

    private static ExtractedDocument ParseDocx(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
            throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "DOCX 文件结构无效。", false);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read, leaveOpen: false);
            var entry = archive.GetEntry("word/document.xml")
                ?? throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "DOCX 缺少正文。", false);
            var uncompressedLength = 0L;
            try { foreach (var archiveEntry in archive.Entries) uncompressedLength = checked(uncompressedLength + archiveEntry.Length); }
            catch (OverflowException) { throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "DOCX 压缩内容超过安全上限。", false); }
            if (archive.Entries.Count > 256 || uncompressedLength > 40_000_000)
                throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "DOCX 压缩内容超过安全上限。", false);
            if (archive.Entries.Any(item => item.FullName.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.FullName, "vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "含宏的 DOCX 暂不支持。", false);
            foreach (var relationship in archive.Entries.Where(item => item.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                if (relationship.Length > 1_000_000)
                    throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "DOCX 关系文件超过安全上限。", false);
                using var relationshipStream = relationship.Open();
                if (ContainsExternalRelationship(relationshipStream))
                    throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "含外部链接的 DOCX 暂不支持。", false);
            }
            using var input = entry.Open();
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 };
            using var reader = XmlReader.Create(input, settings);
            var document = System.Xml.Linq.XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            XNamespace wordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var segmentsBuilder = new List<ExtractedTextSegment>();
            var headingPath = new List<string>();
            var paragraphNumber = 0;
            foreach (var paragraph in document.Descendants(wordNamespace + "p"))
            {
                paragraphNumber++;
                var text = string.Concat(paragraph.Descendants(wordNamespace + "t").Select(item => item.Value)).Trim();
                if (text.Length == 0) continue;
                var style = paragraph.Element(wordNamespace + "pPr")?.Element(wordNamespace + "pStyle")?.Attribute(wordNamespace + "val")?.Value;
                if (style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var level = int.TryParse(style[7..], out var parsedLevel) ? Math.Clamp(parsedLevel, 1, 9) : 1;
                    while (headingPath.Count >= level) headingPath.RemoveAt(headingPath.Count - 1);
                    headingPath.Add(text);
                }
                segmentsBuilder.Add(new ExtractedTextSegment(text, headingPath.Count == 0 ? null : string.Join(" > ", headingPath), $"paragraph:{paragraphNumber}"));
            }
            var segments = segmentsBuilder.ToArray();
            return new ExtractedDocument(segments, segments.Sum(item => item.Text.Length));
        }
        catch (KnowledgeExtractionException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        { throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "DOCX 文件无法安全解析。", false, exception); }
    }

    private static bool ContainsExternalRelationship(Stream relationshipStream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1_000_000,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        using var reader = XmlReader.Create(relationshipStream, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || !string.Equals(reader.LocalName, "Relationship", StringComparison.Ordinal))
                continue;
            var mode = reader.GetAttribute("TargetMode");
            var target = reader.GetAttribute("Target");
            if (string.Equals(mode?.Trim(), "External", StringComparison.OrdinalIgnoreCase)
                || (Uri.TryCreate(target, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https" or "file" or "ftp"))
                return true;
        }
        return false;
    }

    private static ExtractedDocument ParsePdf(byte[] bytes)
    {
        var header = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 8));
        if (!header.StartsWith("%PDF-", StringComparison.Ordinal))
            throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "PDF 文件结构无效。", false);
        if (ContainsAsciiIgnoreCase(bytes, "/Encrypt"u8))
            throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "加密 PDF 暂不支持。", false);
        try
        {
            using var document = PdfDocument.Open(new MemoryStream(bytes, writable: false), new ParsingOptions());
            var segments = new List<ExtractedTextSegment>();
            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                var pageText = ContentOrderTextExtractor.GetText(document.GetPage(pageNumber));
                var lines = pageText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
                for (var line = 0; line < lines.Length; line++)
                {
                    var value = lines[line].Trim();
                    if (value.Length > 0) segments.Add(new ExtractedTextSegment(value, null, $"page:{pageNumber};line:{line + 1}"));
                }
            }
            var characterCount = segments.Sum(item => item.Text.Length);
            if (characterCount >= MinimumPdfTextCharacters)
                return new ExtractedDocument(segments, characterCount, document.NumberOfPages);
            // 无文本层（扫描件）：取前 20 页每页最大的内嵌图作为页图；整页光栅化属后续增强。
            var (images, truncated) = ExtractPageImages(document);
            if (images.Count == 0)
                return new ExtractedDocument(segments, characterCount, document.NumberOfPages);
            return new ExtractedDocument(segments, characterCount, document.NumberOfPages, "image", images,
                truncated || document.NumberOfPages > MaxImages);
        }
        catch (KnowledgeExtractionException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "PDF 文件无法安全解析。", false, exception); }
    }

    private static (IReadOnlyList<ExtractedImage> Images, bool Truncated) ExtractPageImages(PdfDocument document)
    {
        var images = new List<ExtractedImage>();
        var truncated = false;
        var pageLimit = Math.Min(document.NumberOfPages, MaxImages);
        for (var pageNumber = 1; pageNumber <= pageLimit; pageNumber++)
        {
            UglyToad.PdfPig.Content.IPdfImage? largest = null;
            try
            {
                foreach (var image in document.GetPage(pageNumber).GetImages())
                {
                    if (image.WidthInSamples <= 0 || image.HeightInSamples <= 0) continue;
                    if (largest is null || (long)image.WidthInSamples * image.HeightInSamples > (long)largest.WidthInSamples * largest.HeightInSamples)
                        largest = image;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // 单页图片解码失败不影响其余页。
                continue;
            }
            if (largest is null) continue;
            byte[]? encoded = null;
            string? mediaType = null;
            try
            {
                if (largest.TryGetPng(out var png) && png is { Length: > 0 }) { encoded = png; mediaType = "image/png"; }
                else
                {
                    var raw = largest.RawMemory;
                    if (raw.Length >= 3 && raw.Span[0] == 0xFF && raw.Span[1] == 0xD8 && raw.Span[2] == 0xFF)
                    { encoded = raw.ToArray(); mediaType = "image/jpeg"; }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { encoded = null; }
            if (encoded is null || mediaType is null) continue;
            if (encoded.Length > MaxImageBytes) { truncated = true; continue; }
            images.Add(new ExtractedImage(images.Count + 1, mediaType, encoded, $"page:{pageNumber}"));
        }
        return (images, truncated);
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        if (value.Length == 0 || source.Length < value.Length) return false;
        for (var offset = 0; offset <= source.Length - value.Length; offset++)
        {
            var matched = true;
            for (var i = 0; i < value.Length; i++)
            {
                var left = source[offset + i];
                var right = value[i];
                if (left is >= (byte)'A' and <= (byte)'Z') left = (byte)(left + ('a' - 'A'));
                if (right is >= (byte)'A' and <= (byte)'Z') right = (byte)(right + ('a' - 'A'));
                if (left != right) { matched = false; break; }
            }
            if (matched) return true;
        }
        return false;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains('\uFFFD')) throw new KnowledgeExtractionException("KNOWLEDGE_PARSE_FAILED", "文本编码无法识别。", false);
        return text;
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            total += read;
            if (total > limit) throw new KnowledgeExtractionException("KNOWLEDGE_FILE_TOO_LARGE", "文件超过安全大小上限。", false);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static string NormalizeExtension(string extension)
    {
        extension = extension.Trim().ToLowerInvariant();
        return extension.StartsWith('.') ? extension : $".{extension}";
    }
}

/// <summary>安全解析失败异常。</summary>
public sealed class KnowledgeExtractionException(string code, string message, bool retryable, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>稳定错误码。</summary>
    public string Code { get; } = code;
    /// <summary>是否允许重试。</summary>
    public bool Retryable { get; } = retryable;
}

/// <summary>确定性的标题/段落分段器。</summary>
public sealed class KnowledgeChunker
{
    /// <summary>按配置目标长度生成稳定片段。</summary>
    public IReadOnlyList<KnowledgeChunk> Chunk(Guid versionId, ExtractedDocument document,
        int targetCharacters = 1200, int overlapCharacters = 120)
    {
        targetCharacters = Math.Clamp(targetCharacters, 32, 8000);
        overlapCharacters = Math.Clamp(overlapCharacters, 0, Math.Max(0, targetCharacters / 2));
        var pieces = new List<Piece>();
        foreach (var segment in document.Segments)
        {
            var source = segment.Text.Trim();
            if (source.Length == 0) continue;
            for (var start = 0; start < source.Length;)
            {
                var end = Math.Min(source.Length, start + targetCharacters);
                if (end < source.Length)
                {
                    var split = source.LastIndexOfAny([' ', '\n', '\t'], end - 1, end - start);
                    if (split > start + targetCharacters / 2) end = split;
                }
                var location = string.IsNullOrWhiteSpace(segment.Location)
                    ? $"range:{start}-{end}" : $"{segment.Location};range:{start}-{end}";
                pieces.Add(new Piece(source[start..end].Trim(), segment.HeadingPath, location));
                start = end;
                while (start < source.Length && char.IsWhiteSpace(source[start])) start++;
            }
        }
        var baseChunks = new List<Piece>();
        var pending = new StringBuilder();
        var headings = new List<string>();
        var locations = new List<string>();
        void Flush()
        {
            var text = pending.ToString().Trim();
            if (text.Length == 0) return;
            baseChunks.Add(new Piece(text, headings.LastOrDefault(), string.Join('|', locations.Distinct(StringComparer.Ordinal))));
            pending.Clear(); headings.Clear(); locations.Clear();
        }
        foreach (var piece in pieces)
        {
            var addition = piece.Text;
            if (pending.Length > 0 && pending.Length + 1 + addition.Length > targetCharacters) Flush();
            if (pending.Length > 0) pending.Append('\n');
            pending.Append(addition);
            if (!string.IsNullOrWhiteSpace(piece.HeadingPath)) headings.Add(piece.HeadingPath!);
            if (!string.IsNullOrWhiteSpace(piece.Location)) locations.Add(piece.Location!);
        }
        Flush();
        var result = new List<KnowledgeChunk>(baseChunks.Count);
        for (var ordinal = 0; ordinal < baseChunks.Count; ordinal++)
        {
            var piece = baseChunks[ordinal];
            var text = piece.Text;
            if (ordinal > 0 && overlapCharacters > 0)
            {
                var previous = result[ordinal - 1].Text;
                var available = Math.Max(0, targetCharacters - text.Length - 1);
                var count = Math.Min(overlapCharacters, Math.Min(previous.Length, available));
                if (count > 0) text = $"{previous[^count..]}\n{text}";
            }
            // Compute the ID only after overlap and final source ranges are fixed.
            var id = StableGuid($"{versionId:N}:{ordinal}:{piece.Location}:{text}");
            result.Add(new KnowledgeChunk(id, ordinal, text, piece.HeadingPath, piece.Location));
        }
        return result;
    }

    private sealed record Piece(string Text, string? HeadingPath, string? Location);

    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
}

/// <summary>本地可重复 Embedding，便于开发环境在没有外部模型时完成链路。</summary>
public sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
{
    /// <inheritdoc />
    public string Profile => "local-deterministic-v1";
    /// <summary>测试实现的模型标识。</summary>
    public string Model => "local-deterministic";
    /// <summary>测试实现的固定向量维度。</summary>
    public int Dimensions => 32;

    /// <inheritdoc />
    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var result = texts.Select(text =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            var vector = new float[32];
            for (var i = 0; i < vector.Length; i++) vector[i] = (hash[i] - 128) / 128f;
            return vector;
        }).ToArray();
        return Task.FromResult<IReadOnlyList<float[]>>(result);
    }
}

/// <summary>开发环境内存向量索引；MySQL 仍是权限过滤和证据来源。</summary>
public sealed class InMemoryVectorIndex : IVectorIndex
{
    private readonly object gate = new();
    private readonly Dictionary<(Guid TenantId, Guid ChunkId), (Guid VersionId, float[] Vector)> values = [];

    /// <inheritdoc />
    public Task UpsertAsync(Guid tenantId, Guid versionId, IReadOnlyList<KnowledgeChunk> chunks,
        IReadOnlyList<float[]> vectors, CancellationToken cancellationToken = default)
    {
        if (chunks.Count != vectors.Count)
            throw new InvalidOperationException("Embedding 返回数量与片段数量不一致。");
        lock (gate)
            for (var i = 0; i < chunks.Count; i++) values[(tenantId, chunks[i].ChunkId)] = (versionId, vectors[i]);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(Guid tenantId, IReadOnlySet<Guid> versionIds,
        float[] query, int topK, CancellationToken cancellationToken = default)
    {
        List<(Guid ChunkId, double Score)> result;
        lock (gate)
        {
            result = values.Where(item => item.Key.TenantId == tenantId && versionIds.Contains(item.Value.VersionId))
                .Select(item => (item.Key.ChunkId, Score: Cosine(query, item.Value.Vector)))
                .OrderByDescending(item => item.Score).Take(topK).ToList();
        }
        return Task.FromResult<IReadOnlyList<(Guid ChunkId, double Score)>>(result);
    }

    /// <inheritdoc />
    public Task DeleteVersionAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default)
    {
        lock (gate)
            foreach (var key in values.Where(item => item.Key.TenantId == tenantId && item.Value.VersionId == versionId).Select(item => item.Key).ToArray()) values.Remove(key);
        return Task.CompletedTask;
    }

    private static double Cosine(float[] left, float[] right)
    {
        var dot = 0d; var a = 0d; var b = 0d;
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++) { dot += left[i] * right[i]; a += left[i] * left[i]; b += right[i] * right[i]; }
        return a == 0 || b == 0 ? 0 : dot / Math.Sqrt(a * b);
    }
}
