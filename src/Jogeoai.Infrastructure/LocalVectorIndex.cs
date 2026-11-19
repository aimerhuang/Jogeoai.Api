using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Configuration;

namespace Jogeoai.Infrastructure;

/// <summary>供本机 API 和 Worker 跨进程共享的持久化向量索引。</summary>
public sealed class LocalVectorIndex : IVectorIndex
{
    private readonly string root;

    /// <summary>使用配置的本地目录和 Embedding 档案。</summary>
    public LocalVectorIndex(IConfiguration configuration, IEmbeddingProvider embeddingProvider)
    {
        var configured = configuration["Knowledge:VectorRoot"];
        var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? ".data/knowledge-vectors" : configured);
        var profileHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(embeddingProvider.Profile))).ToLowerInvariant();
        root = Path.Combine(directory, profileHash);
        Directory.CreateDirectory(root);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(Guid tenantId, Guid versionId, IReadOnlyList<KnowledgeChunk> chunks,
        IReadOnlyList<float[]> vectors, CancellationToken cancellationToken = default)
    {
        if (chunks.Count != vectors.Count || vectors.Any(vector => vector.Length == 0 || vector.Any(value => !float.IsFinite(value))))
            throw new ArgumentException("向量数量或数值无效。", nameof(vectors));
        var path = PathFor(tenantId, versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream,
                    chunks.Zip(vectors, (chunk, vector) => new VectorEntry(chunk.ChunkId, vector)).ToArray(),
                    cancellationToken: cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(Guid tenantId,
        IReadOnlySet<Guid> versionIds, float[] query, int topK, CancellationToken cancellationToken = default)
    {
        var scored = new List<(Guid ChunkId, double Score)>();
        foreach (var versionId in versionIds)
        {
            var path = PathFor(tenantId, versionId);
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous);
                var entries = await JsonSerializer.DeserializeAsync<VectorEntry[]>(stream, cancellationToken: cancellationToken) ?? [];
                scored.AddRange(entries.Select(entry => (entry.ChunkId, Cosine(query, entry.Vector))));
            }
            catch (FileNotFoundException) { /* Version has no local vectors yet or was removed. */ }
            catch (DirectoryNotFoundException) { /* Tenant has no local vectors. */ }
        }
        return scored.OrderByDescending(item => item.Score).ThenBy(item => item.ChunkId)
            .Take(Math.Max(0, topK)).ToArray();
    }

    /// <inheritdoc />
    public Task DeleteVersionAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(tenantId, versionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(Guid tenantId, Guid versionId)
        => Path.Combine(root, tenantId.ToString("N"), versionId.ToString("N") + ".json");

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length) return 0;
        var dot = 0d; var leftLength = 0d; var rightLength = 0d;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftLength += left[i] * left[i];
            rightLength += right[i] * right[i];
        }
        return leftLength == 0 || rightLength == 0 ? 0 : dot / Math.Sqrt(leftLength * rightLength);
    }

    private sealed record VectorEntry(Guid ChunkId, float[] Vector);
}
