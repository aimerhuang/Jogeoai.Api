using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>A visible active chunk of a pinned (explicitly selected) document, with its position in the version.</summary>
internal sealed record KnowledgePinnedChunk(KnowledgeEvidenceView Evidence, long Ordinal);

/// <summary>
/// Builds a writing context from per-query relevant hits and, when documents or a
/// product were pinned, their visible chunks. Without pinned chunks the result is
/// identical to the original per-query loop.
/// </summary>
internal static class KnowledgeContextAssembler
{
    internal static IReadOnlyList<KnowledgeEvidenceView> Assemble(
        IReadOnlyList<IReadOnlyList<KnowledgeEvidenceView>> relevantPerQuery,
        IReadOnlyList<KnowledgePinnedChunk> pinned, int maxChunks, int maxCharacters)
    {
        var all = new List<KnowledgeEvidenceView>();
        var included = new HashSet<string>(StringComparer.Ordinal);
        var characters = 0;
        bool Fits(KnowledgeEvidenceView item) => characters + item.Text.Length <= maxCharacters;
        void Add(KnowledgeEvidenceView item) { all.Add(item); included.Add(item.EvidenceId); characters += item.Text.Length; }

        // Pinned chunks grouped per document in load order (document id, then ordinal).
        var pinnedDocuments = pinned.GroupBy(chunk => chunk.Evidence.DocumentId)
            .Select(group => group.OrderBy(chunk => chunk.Ordinal).Select(chunk => chunk.Evidence).ToList())
            .ToList();

        // 1. Coverage: every pinned document first contributes its best chunk — the
        //    highest-scoring relevant hit if any, otherwise its first chunk. When that
        //    chunk exceeds the remaining character budget, the next fitting chunk (by
        //    ordinal) still covers the document.
        if (pinnedDocuments.Count > 0)
        {
            var bestRelevant = new Dictionary<Guid, KnowledgeEvidenceView>();
            var relevantById = new Dictionary<string, KnowledgeEvidenceView>(StringComparer.Ordinal);
            foreach (var item in relevantPerQuery.SelectMany(found => found))
            {
                if (!bestRelevant.TryGetValue(item.DocumentId, out var best) || item.Score > best.Score)
                    bestRelevant[item.DocumentId] = item;
                relevantById.TryAdd(item.EvidenceId, item);
            }
            var coverage = pinnedDocuments
                .Select((chunks, index) => (Index: index, Relevant: bestRelevant.GetValueOrDefault(chunks[0].DocumentId), Chunks: chunks))
                .OrderBy(entry => entry.Relevant is null).ThenByDescending(entry => entry.Relevant?.Score ?? 0d).ThenBy(entry => entry.Index)
                .Select(entry => entry.Chunks.Select(chunk => relevantById.GetValueOrDefault(chunk.EvidenceId) ?? chunk)
                    .Prepend(entry.Relevant).OfType<KnowledgeEvidenceView>());
            foreach (var candidates in coverage)
            {
                if (all.Count >= maxChunks) break;
                var item = candidates.FirstOrDefault(candidate => !included.Contains(candidate.EvidenceId) && Fits(candidate));
                if (item is not null) Add(item);
            }
        }

        // 2. Relevance: queries in order, each already sorted by score. Kept exactly as
        //    before (a budget overflow stops the current query) so non-pinned requests
        //    do not change.
        foreach (var found in relevantPerQuery)
            foreach (var item in found.Where(item => !included.Contains(item.EvidenceId)))
            {
                if (all.Count >= maxChunks) break;
                if (!Fits(item)) break;
                Add(item);
            }

        // 3. Fill: remaining pinned chunks, round-robin across documents by ordinal;
        //    an oversized chunk is skipped so shorter ones can still use the budget.
        var remaining = pinnedDocuments.Select(chunks => chunks.Where(item => !included.Contains(item.EvidenceId)).ToList()).ToList();
        var depth = remaining.Count == 0 ? 0 : remaining.Max(chunks => chunks.Count);
        for (var position = 0; position < depth && all.Count < maxChunks; position++)
            foreach (var chunks in remaining)
            {
                if (all.Count >= maxChunks) break;
                if (position >= chunks.Count) continue;
                var item = chunks[position];
                if (included.Contains(item.EvidenceId) || !Fits(item)) continue;
                Add(item);
            }
        return all;
    }
}
