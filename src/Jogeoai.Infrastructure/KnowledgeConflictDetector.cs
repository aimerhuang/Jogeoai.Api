using System.Runtime.CompilerServices;
using System.Text;
using Jogeoai.Domain;

[assembly: InternalsVisibleTo("Jogeoai.Tests")]

namespace Jogeoai.Infrastructure;

internal static class KnowledgeConflictDetector
{
    internal static IReadOnlyList<KnowledgeConflict> FindCandidates(IReadOnlyList<KnowledgeEvidenceView> evidence)
        => evidence.Where(item => !string.IsNullOrWhiteSpace(item.ProductKey))
            .GroupBy(item => item.ProductKey!.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant(), StringComparer.Ordinal)
            .Where(group => group.Select(item => (item.DocumentId, item.VersionId)).Distinct().Count() > 1)
            .Select(group => new KnowledgeConflict(group.Key, group.Select(item => item.EvidenceId).Distinct(StringComparer.Ordinal).ToArray(),
                "同一产品有多个资料来源，可能存在冲突；请人工核对具体事实和版本。"))
            .ToArray();
}
