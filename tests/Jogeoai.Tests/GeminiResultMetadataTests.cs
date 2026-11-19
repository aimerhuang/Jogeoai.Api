using System.Text;
using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Providers.Abstractions;

namespace Jogeoai.Tests;

public sealed class GeminiResultMetadataTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1}")]
    public void HistoricMissingCorruptOrUnknownMetadataRemainsUnknown(string? json)
        => Assert.Null(ResultQualityCodec.Decode(json));

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public void Utf8StorageBoundaryDoesNotSplitRunesAndRecordsActualTruncation(string engine)
    {
        var atLimit = new string('a', 1_048_572) + "😀";
        Assert.Equal(atLimit, CloroVisibilityStore.TruncateUtf8(atLimit, 1_048_576));
        var source = atLimit + "中文";
        var stored = CloroVisibilityStore.TruncateUtf8(source, 1_048_576);
        Assert.Equal(atLimit, stored);
        Assert.Equal(1_048_576, Encoding.UTF8.GetByteCount(stored));
        var quality = ResultQualityCodec.Decode(ResultQualityCodec.Encode(Answer(source, engine), stored))!;
        Assert.True(quality.AnswerTruncated);
        Assert.False(quality.AnswerRedacted);
        Assert.False(quality.ModelVersionProvided);
        Assert.False(ResultQualityCodec.Decode(ResultQualityCodec.Encode(Answer(atLimit, engine), atLimit))!.AnswerTruncated);
        Assert.Null(ResultQualityCodec.Encode(Answer(source, engine) with { ResultQuality = null }, stored));
    }

    [Fact]
    public void MetadataRoundTripValidatesRetainedCountAndSafeReasonCodes()
    {
        var answer = Answer("答案");
        var json = ResultQualityCodec.Encode(answer, answer.AnswerText)!;
        Assert.True(Encoding.UTF8.GetByteCount(json) < ResultQualityCodec.MaximumBytes);
        Assert.Equal("empty", ResultQualityCodec.Decode(json)!.Citations.Availability);
        var mismatch = answer with { Citations = [new("https://example.org/", "example.org", null, 1)] };
        Assert.Throws<CollectorStoreException>(() => ResultQualityCodec.Encode(mismatch, mismatch.AnswerText));
        var unsafeQuality = answer with { ResultQuality = new(false, false,
            new("available", "partial", 1, 0, 1, 0, 0, ["https://private.example/?token=private"])) };
        Assert.Throws<CollectorStoreException>(() => ResultQualityCodec.Encode(unsafeQuality, "答案"));
        Assert.Null(ResultQualityCodec.Decode(json.Replace("\"receivedCount\":0", "\"receivedCount\":1")));
        Assert.Null(ResultQualityCodec.Decode(new string(' ', ResultQualityCodec.MaximumBytes + 1)));
    }

    [Theory]
    [InlineData("available", "complete", 0, 0, 0, 0, 0)]
    [InlineData("available", "complete", 2, 1, 1, 0, 0)]
    [InlineData("empty", "complete", 1, 1, 0, 0, 0)]
    [InlineData("available", "partial", 1, 0, 0, 0, 0)]
    [InlineData("not_exposed", "unknown", 0, 0, 0, 0, 0)]
    public void ContradictoryQualityIsNeverProjectedAsComplete(string availability, string completeness,
        int received, int retained, int filtered, int invalid, int truncated)
    {
        var data = new ResultQualityView(1, false, false, false,
            new(availability, completeness, received, retained, filtered, invalid, truncated, []));
        Assert.Null(ResultQualityCodec.Decode(JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    }

    private static CloroAnswerSnapshot Answer(string text, string engine = "gemini") => new("cloro", engine, "scraped", "observed_surface",
        engine, text, text.Length > 0, [], [], JsonSerializer.SerializeToElement(new { text }), DateTimeOffset.UtcNow,
        ResultQuality: new(false, false, new("empty", "complete", 0, 0, 0, 0, 0, [])));
}
