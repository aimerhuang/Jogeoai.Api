using System.Globalization;
using System.Text;

namespace Jogeoai.Infrastructure;

/// <summary>
/// Literal-match tokenizer used when no semantic embedding is configured.
/// CJK runs become adjacent bigrams (Chinese has no spaces, so a whole sentence
/// would otherwise be one unmatchable term); other letters/digits become
/// lowercase words split on whitespace and punctuation.
/// </summary>
internal static class KnowledgeLexicalTerms
{
    // Enough for a full question; also bounds the LOCATE(...) OR prefilter.
    internal const int MaxTerms = 64;

    internal static IReadOnlyList<string> Extract(string? query)
    {
        var segments = new List<(bool Cjk, List<string> Runes)>();
        List<string>? current = null; var currentCjk = false;
        foreach (var rune in (query ?? string.Empty).EnumerateRunes())
        {
            var kind = Classify(rune);
            if (kind == RuneKind.Separator) { current = null; continue; }
            var cjk = kind == RuneKind.Cjk;
            if (current is null || currentCjk != cjk)
            {
                current = []; currentCjk = cjk;
                segments.Add((cjk, current));
            }
            current.Add(rune.ToString());
        }

        var terms = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string term) { if (terms.Count < MaxTerms && seen.Add(term)) terms.Add(term); }
        foreach (var (cjk, runes) in segments)
        {
            if (!cjk) { Add(string.Concat(runes).ToLowerInvariant()); continue; }
            // A lone CJK character matches far too much; keep it only when it is the whole query.
            if (runes.Count == 1) { if (segments.Count == 1) Add(runes[0]); continue; }
            for (var i = 0; i + 1 < runes.Count; i++) Add(runes[i] + runes[i + 1]);
        }
        return terms;
    }

    /// <summary>Same score as before (1 + occurrences × 0.1 per matched term) plus the distinct matched-term count.</summary>
    internal static (double Score, int Matches) Score(string text, IReadOnlyList<string> terms)
    {
        var lower = text.ToLowerInvariant(); var score = 0d; var matches = 0;
        foreach (var term in terms)
        {
            if (!lower.Contains(term, StringComparison.Ordinal)) continue;
            score += 1d + (lower.Split(term).Length - 1) * 0.1; matches++;
        }
        return (score, matches);
    }

    /// <summary>One shared word pair (or one word) is too weak a signal once a query has several terms.</summary>
    internal static int MinMatches(IReadOnlyList<string> terms) => terms.Count >= 2 ? 2 : terms.Count;

    private enum RuneKind { Separator, Cjk, Word }

    private static RuneKind Classify(Rune rune)
    {
        var value = rune.Value;
        if (value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2FA1F // Han
            or >= 0x3040 and <= 0x309F // Hiragana
            or >= 0x30A0 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F // Katakana
            or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xAC00 and <= 0xD7AF) // Hangul
            return RuneKind.Cjk;
        if (Rune.IsLetterOrDigit(rune)) return RuneKind.Word;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            ? RuneKind.Word : RuneKind.Separator;
    }
}
