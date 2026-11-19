using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;

namespace Jogeoai.Infrastructure;

internal static class ResultQualityCodec
{
    internal const int MaximumBytes = 16 * 1024;
    // A null AnswerAbsence is omitted so metadata without a known absence stays byte-identical to the
    // pre-field shape; the API DTO itself is serialized elsewhere and still exposes the property as null.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitNullAnswerAbsence } },
    };
    private static readonly HashSet<string> Reasons = ["INVALID_SOURCE_SHAPE", "UNSAFE_SOURCE", "SOURCE_LIMIT", "SOURCE_REDACTED", "SOURCE_FIELD_LIMIT"];

    internal static string? Encode(CloroAnswerSnapshot answer, string storedText)
    {
        if (answer.ResultQuality is not { } quality) return null;
        var c = quality.Citations;
        var result = new ResultQualityView(1, storedText != answer.AnswerText, quality.AnswerRedacted,
            quality.ModelVersionProvided, new(c.Availability, c.Completeness, c.ReceivedCount,
                c.RetainedCount, c.FilteredCount, c.InvalidCount, c.TruncatedCount, c.Reasons), quality.AnswerAbsence);
        if (c.RetainedCount != answer.Citations.Count || !Valid(result))
            throw new CollectorStoreException("INVALID_RESULT_QUALITY", "采集结果质量与引用不一致。", 500);
        var json = JsonSerializer.Serialize(result, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new CollectorStoreException("INVALID_RESULT_QUALITY", "采集结果质量超出容量。", 500);
        return json;
    }

    internal static ResultQualityView? Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumBytes) return null;
        try
        {
            var result = JsonSerializer.Deserialize<ResultQualityView>(json, Json);
            return result is not null && Valid(result) ? result : null;
        }
        catch (JsonException) { return null; }
    }

    private static void OmitNullAnswerAbsence(JsonTypeInfo info)
    {
        if (info.Type != typeof(ResultQualityView)) return;
        foreach (var property in info.Properties)
            if (string.Equals(property.Name, nameof(ResultQualityView.AnswerAbsence), StringComparison.OrdinalIgnoreCase))
                property.ShouldSerialize = static (_, value) => value is not null;
    }

    private static bool Valid(ResultQualityView result)
    {
        // Known absence reasons: Google showed no AI Overview; an API answer came back empty; the provider's output moderation removed it.
        if (result.SchemaVersion != 1 || result.AnswerAbsence is not (null or "ai_overview_not_shown" or "empty_answer" or "safety_filtered")
            || result.Citations is not { } c
            || c.Availability is not ("available" or "empty" or "not_exposed" or "unknown")
            || c.Completeness is not ("complete" or "partial" or "unknown")
            || c.Reasons is null || c.Reasons.Count > Reasons.Count || c.Reasons.Distinct().Count() != c.Reasons.Count
            || c.Reasons.Any(r => r is null || !Reasons.Contains(r))) return false;
        int?[] counts = [c.ReceivedCount, c.RetainedCount, c.FilteredCount, c.InvalidCount, c.TruncatedCount];
        if (counts.Any(n => n < 0) || c.RetainedCount > 100) return false;
        if (counts.All(n => n.HasValue)
            && c.ReceivedCount != (long)c.RetainedCount!.Value + c.FilteredCount + c.InvalidCount + c.TruncatedCount) return false;
        if (c.Availability == "empty" && (c.ReceivedCount != 0 || c.Completeness != "complete")) return false;
        if (c.Availability == "available" && (c.ReceivedCount is null or 0 || c.InvalidCount != 0)) return false;
        if (c.Availability == "not_exposed" && (c.Completeness != "unknown" || c.ReceivedCount is not null
            || c.RetainedCount > 0 || c.FilteredCount > 0 || c.InvalidCount > 0 || c.TruncatedCount > 0)) return false;
        if (c.Completeness == "partial" && c.Reasons.Count == 0) return false;
        if (c.Completeness == "complete" && (counts.Any(n => !n.HasValue)
            || c.FilteredCount != 0 || c.InvalidCount != 0 || c.TruncatedCount != 0 || c.Reasons.Count != 0)) return false;
        return true;
    }
}
