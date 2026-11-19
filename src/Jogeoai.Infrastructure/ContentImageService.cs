using System.Buffers.Binary;
using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>编辑器图片对象键：content-images/{tenantId:N}/{imageId:N}，与知识库 {tenantId:N}/{versionId:N}/source 不重叠。</summary>
public static class ContentImageKeys
{
    /// <summary>对象键前缀。</summary>
    public const string Prefix = "content-images/";

    /// <summary>生成对象键。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="imageId">图片标识。</param>
    public static string For(Guid tenantId, Guid imageId) => $"{Prefix}{tenantId:N}/{imageId:N}";

    /// <summary>是否为服务端生成的图片对象键。</summary>
    /// <param name="key">对象键。</param>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = key[Prefix.Length..].Split('/');
        return parts.Length == 2 && parts.All(part => part.Length == 32 && Guid.TryParseExact(part, "N", out _));
    }
}

/// <summary>按文件头魔数识别 PNG / JPEG / WebP / GIF 并读取像素尺寸；不信任文件扩展名或客户端 Content-Type。</summary>
public static class ContentImageSniffer
{
    /// <summary>识别结果：类型与可选尺寸；不受支持时返回空。</summary>
    /// <param name="data">文件内容。</param>
    public static (string ContentType, int? Width, int? Height)? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return ("image/png", Positive(BinaryPrimitives.ReadInt32BigEndian(data[16..20])), Positive(BinaryPrimitives.ReadInt32BigEndian(data[20..24])));
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            var (width, height) = JpegSize(data);
            return ("image/jpeg", width, height);
        }
        if (data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
            return ("image/gif", Positive(BinaryPrimitives.ReadUInt16LittleEndian(data[6..8])), Positive(BinaryPrimitives.ReadUInt16LittleEndian(data[8..10])));
        if (data.Length >= 16 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            var (width, height) = WebpSize(data);
            return ("image/webp", width, height);
        }
        return null;
    }

    private static int? Positive(long value) => value is > 0 and <= 100_000 ? (int)value : null;

    private static (int?, int?) JpegSize(ReadOnlySpan<byte> data)
    {
        var index = 2;
        while (index + 9 < data.Length)
        {
            if (data[index] != 0xFF) { index++; continue; }
            var marker = data[index + 1];
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { index += 2; continue; }
            if (marker == 0xFF) { index++; continue; }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(index + 2)..(index + 4)]);
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                return (Positive(BinaryPrimitives.ReadUInt16BigEndian(data[(index + 7)..(index + 9)])),
                    Positive(BinaryPrimitives.ReadUInt16BigEndian(data[(index + 5)..(index + 7)])));
            if (length < 2) break;
            index += 2 + length;
        }
        return (null, null);
    }

    private static (int?, int?) WebpSize(ReadOnlySpan<byte> data)
    {
        var chunk = data[12..16];
        if (chunk.SequenceEqual("VP8 "u8) && data.Length >= 30)
            return (Positive(BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]) & 0x3FFF),
                Positive(BinaryPrimitives.ReadUInt16LittleEndian(data[28..30]) & 0x3FFF));
        if (chunk.SequenceEqual("VP8L"u8) && data.Length >= 25)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..25]);
            return (Positive((bits & 0x3FFF) + 1), Positive(((bits >> 14) & 0x3FFF) + 1));
        }
        if (chunk.SequenceEqual("VP8X"u8) && data.Length >= 30)
            return (Positive((data[24] | data[25] << 8 | data[26] << 16) + 1), Positive((data[27] | data[28] << 8 | data[29] << 16) + 1));
        return (null, null);
    }
}

/// <summary>编辑器图片：元数据在 content_images（迁移 041），对象本体复用知识库对象存储（本地或 S3）。</summary>
public sealed class ContentImageService(MySqlDatabase database, IObjectStorage storage) : IContentImageService
{
    private const int MaxExportImages = 50;
    // Export keeps every embedded image in memory (and again inside the docx package); beyond this total the remaining
    // platform images are left out and rendered as「图片不可用」placeholders.
    private const long MaxExportBytes = 30L * 1024 * 1024;

    /// <inheritdoc />
    public async Task<ContentImageUploadResult> UploadAsync(Guid tenantId, Guid siteId, Guid userId, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        if (content.Length > ArticleEditorLimits.ImageMaxBytes)
            throw new CollectorStoreException(ArticleEditorErrorCodes.ImageTooLarge, "图片不能超过 5 MB。", 413);
        var detected = content.IsEmpty ? null : ContentImageSniffer.Detect(content.Span);
        if (detected is not { } image)
            throw new CollectorStoreException(ArticleEditorErrorCodes.ImageTypeUnsupported, "只支持 PNG、JPEG、WebP 或 GIF 图片。", 415);

        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ArticleEditorSchema.RequireAsync(connection, cancellationToken);
        await using (var site = new MySqlCommand("SELECT 1 FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site) AND status='active';", connection))
        {
            site.Parameters.AddWithValue("@tenant", tenantId.ToString());
            site.Parameters.AddWithValue("@site", siteId.ToString());
            if (await site.ExecuteScalarAsync(cancellationToken) is null)
                throw new CollectorStoreException("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
        }

        var imageId = Guid.NewGuid();
        var key = ContentImageKeys.For(tenantId, imageId);
        using (var stream = new MemoryStream(content.ToArray(), writable: false))
            await storage.PutAtAsync(key, stream, cancellationToken);
        try
        {
            await using var insert = new MySqlCommand("""
                INSERT INTO content_images (id,tenant_id,site_id,storage_key,content_type,byte_size,width,height,created_by,created_at)
                VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),@key,@type,@size,@width,@height,UUID_TO_BIN(@user),UTC_TIMESTAMP(6));
                """, connection);
            insert.Parameters.AddWithValue("@id", imageId.ToString());
            insert.Parameters.AddWithValue("@tenant", tenantId.ToString());
            insert.Parameters.AddWithValue("@site", siteId.ToString());
            insert.Parameters.AddWithValue("@key", key);
            insert.Parameters.AddWithValue("@type", image.ContentType);
            insert.Parameters.AddWithValue("@size", content.Length);
            insert.Parameters.AddWithValue("@width", (object?)image.Width ?? DBNull.Value);
            insert.Parameters.AddWithValue("@height", (object?)image.Height ?? DBNull.Value);
            insert.Parameters.AddWithValue("@user", userId.ToString());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            // The object is unreachable without its row; remove it so it is not orphaned.
            try { await storage.DeleteAsync(key, CancellationToken.None); } catch { /* compensation only */ }
            throw;
        }
        return new ContentImageUploadResult(imageId, ArticleEditorLimits.ImageUrlPrefix + imageId.ToString("D"), image.ContentType,
            content.Length, image.Width, image.Height);
    }

    /// <inheritdoc />
    public async Task<ContentImageRecord?> FindAsync(Guid imageId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await ArticleEditorSchema.ExistsAsync(connection, cancellationToken)) return null;
        var rows = await ReadAsync(connection, "WHERE id=UUID_TO_BIN(@id)", [("@id", imageId.ToString())], cancellationToken);
        return rows.FirstOrDefault();
    }

    /// <inheritdoc />
    public Task<Stream?> OpenAsync(ContentImageRecord image, CancellationToken cancellationToken = default)
        => ContentImageKeys.IsValid(image.StorageKey) ? storage.OpenReadAsync(image.StorageKey, cancellationToken) : Task.FromResult<Stream?>(null);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, ContentImageBinary>> LoadAsync(Guid tenantId, IReadOnlyCollection<Guid> imageIds,
        CancellationToken cancellationToken = default)
    {
        var ids = imageIds.Where(id => id != Guid.Empty).Distinct().Take(MaxExportImages).ToArray();
        var result = new Dictionary<Guid, ContentImageBinary>();
        if (ids.Length == 0) return result;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await ArticleEditorSchema.ExistsAsync(connection, cancellationToken)) return result;
        var parameters = ids.Select((id, index) => ($"@id{index}", (object?)id.ToString())).Append(("@tenant", tenantId.ToString())).ToArray();
        var rows = await ReadAsync(connection,
            $"WHERE tenant_id=UUID_TO_BIN(@tenant) AND id IN ({string.Join(',', ids.Select((_, index) => $"UUID_TO_BIN(@id{index})"))})",
            parameters, cancellationToken);
        long total = 0;
        // Keep the article's order (ids arrive in first-appearance order) so the budget favours the first images.
        var order = ids.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index);
        foreach (var row in rows.OrderBy(row => order[row.ImageId]))
        {
            if (total + row.ByteSize > MaxExportBytes) continue;
            await using var stream = await OpenAsync(row, cancellationToken);
            if (stream is null) continue;
            await using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length == 0 || buffer.Length > ArticleEditorLimits.ImageMaxBytes) continue;
            total += buffer.Length;
            result[row.ImageId] = new ContentImageBinary(row.ContentType, buffer.ToArray(), row.Width, row.Height);
        }
        return result;
    }

    private static async Task<List<ContentImageRecord>> ReadAsync(MySqlConnection connection, string where,
        (string Name, object? Value)[] parameters, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand($"""
            SELECT BIN_TO_UUID(id),BIN_TO_UUID(tenant_id),BIN_TO_UUID(site_id),storage_key,content_type,byte_size,width,height
            FROM content_images {where};
            """, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ContentImageRecord>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new ContentImageRecord(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4), Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                reader.IsDBNull(7) ? null : Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture)));
        return rows;
    }
}
