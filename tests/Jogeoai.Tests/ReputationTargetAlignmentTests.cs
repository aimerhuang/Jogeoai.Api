using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class ReputationTargetAlignmentTests
{
    [Theory]
    [InlineData("consumer_web", "scraped")]
    [InlineData("scraped", "scraped")]
    [InlineData("api", "api")]
    public void DomesticTargetsAlignWithTheCaptureModeTheirAnswersAreStoredUnder(string target, string expected)
        => Assert.Equal(expected, AnalyticsQueryStore.AnswerCaptureMode(target));
}
