using Jogeoai.Domain;
using System.Globalization;

namespace Jogeoai.Tests;

public sealed class AnalyticsCoreTests
{
    private static readonly Guid Brand = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid P1 = Guid.Parse("00000000-0000-0000-0000-000000000011");
    private static readonly Guid P2 = Guid.Parse("00000000-0000-0000-0000-000000000012");
    private static readonly Guid Target = Guid.Parse("00000000-0000-0000-0000-000000000021");

    private static SubjectConfiguration Config => new(Brand, 3,
        [new(Brand, "brand", "A"), new(B, "competitor", "B"), new(C, "competitor", "C")],
        [new(Brand, "brand", "name", "acme"), new(Brand, "brand", "alias", "艾可米"),
         new(Brand, "brand", "primary_domain", "a.test"), new(B, "competitor", "name", "beta")], true);

    [Fact]
    public void FixedFourCheckFixtureUsesCheckPresenceNotWordFrequency()
    {
        var day = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var facts = new[]
        {
            Fact(P1, day, [Brand, B], true),
            Fact(P1, day, [Brand, B], false),
            Fact(P2, day, [Brand, C], true),
            Fact(P2, day, [], false)
        };
        var projection = AnalyticsProjector.Compute(facts, Config);
        Assert.Equal(4, projection.Metrics.EligibleChecks);
        Assert.Equal(3, projection.Metrics.Visibility.Numerator);
        Assert.Equal(4, projection.Metrics.Visibility.Denominator);
        Assert.Equal(0.75, projection.Metrics.Visibility.Value);
        Assert.Equal(0.5, projection.Metrics.ShareOfVoice.Value);
        Assert.Equal(0.5, projection.Metrics.OwnCitationRate.Value);
        Assert.Equal([3L, 2L, 1L], projection.Entries.Select(e => e.Mentions));
        Assert.Equal([2L, 1L, 1L], projection.Entries.Select(e => e.PromptCount));
        Assert.Equal(3, projection.Metrics.AnyCompetitorMentionRate.Numerator);
        var withFailed = facts.Concat([Fact(P2, day, [], false) with { Status = "failed", AnalysisStatus = "pending" },
            Fact(P2, day, [], false) with { AnswerPresent = false, AnalysisStatus = "pending" }]).ToArray();
        var changed = AnalyticsProjector.Compute(withFailed, Config).Metrics;
        Assert.Equal(4, changed.EligibleChecks);
        Assert.Equal(5, changed.SuccessfulChecks);
        Assert.Equal(1, changed.FailedChecks);
        Assert.Equal(1, changed.NoAnswerChecks);
    }

    [Fact]
    public void ZeroDenominatorIsNullButValidZeroMentionIsZero()
    {
        var empty = AnalyticsProjector.Compute([], Config).Metrics;
        Assert.Null(empty.Visibility.Value);
        Assert.Null(empty.ShareOfVoice.Value);
        var valid = AnalyticsProjector.Compute([Fact(P1, DateTimeOffset.UtcNow, [], false)], Config).Metrics;
        Assert.Equal(0, valid.Visibility.Value);
        Assert.Null(valid.ShareOfVoice.Value);
        Assert.Contains(AnalyticsProjector.Compute([Fact(P1, DateTimeOffset.UtcNow, [], false)], Config).Entries,
            entry => entry.SubjectId == C && entry.Mentions == 0);
    }

    [Fact]
    public void LatestObservationReplacesWholeVectorIncludingZeroAndDoesNotPrefill()
    {
        var zone = TimeZoneInfo.Utc;
        var facts = new[]
        {
            Fact(P1, new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero), [Brand, B], false),
            Fact(P1, new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero), [], false)
        };
        var (trend, latest) = AnalyticsProjector.LatestObservation(facts, Config,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 6), zone);
        Assert.Null(trend[0].Visibility.Value);
        Assert.Equal(1, trend[2].Visibility.Value);
        Assert.True(trend[2].IsCarriedForward);
        Assert.Equal(0, trend[3].Visibility.Value);
        Assert.Equal(0, trend[4].Visibility.Value);
        Assert.Equal(0, AnalyticsProjector.Compute(latest, Config).Metrics.Visibility.Value);
    }

    [Fact]
    public void MatcherKeepsOriginalUtf16OffsetsAndRejectsEmbeddedWordsAndDomainSuffixes()
    {
        var answer = "😀 A\u0301cme 艾可米 MyAcme acme2 ACME https://blog.a.test/x a.test.evil";
        var config = Config with { Terms = Config.Terms.Concat([new AnalysisTerm(Brand, "brand", "alias", "ácme")]).ToArray() };
        var matches = MentionMatcher.Match(answer, config);
        Assert.Contains(matches, m => m.MatchedText == "A\u0301cme"
            && answer[m.OffsetStart..m.OffsetEnd] == m.MatchedText && m.OffsetStart == 3);
        Assert.Contains(matches, m => m.MatchedText == "艾可米");
        Assert.Contains(matches, m => m.MatchedText == "ACME");
        Assert.Contains(matches, m => m.MatchedText == "blog.a.test" && m.Basis == "primary_domain");
        Assert.DoesNotContain(matches, m => m.MatchedText.Contains("MyAcme") || m.MatchedText == "acme2"
            || m.MatchedText == "a.test.evil");
        Assert.Single(matches, m => m.MatchedText == "A\u0301cme");
    }

    [Fact]
    public void LegacyRevisionWithoutTermsIsUnknownRatherThanCurrentConfiguration()
    {
        var legacy = SubjectConfiguration.Parse(Brand, 1,
            $$"""{"brandId":"{{Brand}}","name":"A","primaryDomain":"a.test"}""");
        Assert.False(legacy.IsComplete);
        Assert.Empty(legacy.Terms);
        Assert.Empty(MentionMatcher.Match("A a.test", legacy));
    }

    [Fact]
    public void BrandDomainSubdomainSettingControlsAnswerDomainMatches()
    {
        var withoutSubdomains = Config with
        {
            Terms = [new AnalysisTerm(Brand, "brand", "primary_domain", "a.test")],
            IncludeSubdomains = false
        };

        var matches = MentionMatcher.Match("a.test blog.a.test", withoutSubdomains);

        Assert.Single(matches);
        Assert.Equal("a.test", matches[0].MatchedText);
    }

    [Fact]
    public void SnapshotSubdomainSettingIsParsedAndApplied()
    {
        var snapshot = $$"""{"brandId":"{{Brand}}","name":"A","brandStatus":"active","includeSubdomains":false,"terms":[{"subjectId":"{{Brand}}","subjectType":"brand","termType":"primary_domain","key":"a.test"}]}""";

        var configuration = SubjectConfiguration.Parse(Brand, 2, snapshot);

        Assert.False(configuration.IncludeSubdomains);
        Assert.Empty(MentionMatcher.Match("blog.a.test", configuration));
    }

    [Fact]
    public void MissingSubdomainSettingRetainsLegacySnapshotBehavior()
    {
        var legacy = SubjectConfiguration.Parse(Brand, 1,
            $$"""{"brandId":"{{Brand}}","name":"A","brandStatus":"active","terms":[{"subjectId":"{{Brand}}","subjectType":"brand","termType":"primary_domain","key":"a.test"}]}""");

        Assert.True(legacy.IncludeSubdomains);
        Assert.Single(MentionMatcher.Match("blog.a.test", legacy));
    }

    [Fact]
    public void UnicodeDomainMatchesPunycodeConfigurationAtOriginalOffsets()
    {
        var host = "例子.中国";
        var config = Config with { Terms = [new AnalysisTerm(Brand, "brand", "primary_domain",
            new IdnMapping().GetAscii(host).ToLowerInvariant())] };
        var answer = "😀 请看 https://" + host + "/news";
        var match = Assert.Single(MentionMatcher.Match(answer, config));
        Assert.Equal(host, match.MatchedText);
        Assert.Equal(host, answer[match.OffsetStart..match.OffsetEnd]);
    }

    [Fact]
    public void MatcherVersionIsMentionV2()
        => Assert.Equal("mention-v2", MentionMatcher.Version);

    [Fact]
    public void WwwHostCountsAsApexEvenWhenSubdomainsAreDisabled()
    {
        var withoutSubdomains = Config with
        {
            Terms = [new AnalysisTerm(Brand, "brand", "primary_domain", "a.test")],
            IncludeSubdomains = false
        };

        var matches = MentionMatcher.Match("详见 https://WWW.a.test/x 与 blog.a.test、www.blog.a.test", withoutSubdomains);

        var match = Assert.Single(matches);
        Assert.Equal("WWW.a.test", match.MatchedText);
        Assert.Equal("primary_domain", match.Basis);
        // A configured www. domain is compared after the same normalization.
        var configuredWww = withoutSubdomains with { Terms = [new AnalysisTerm(Brand, "brand", "primary_domain", "www.a.test")] };
        Assert.Equal("a.test", Assert.Single(MentionMatcher.Match("see a.test", configuredWww)).MatchedText);
    }

    [Fact]
    public void LatinAliasMatchesNextToCjkButNotInsideLatinWords()
    {
        var config = Config with { Terms = [new AnalysisTerm(Brand, "brand", "alias", "ulinktek")] };
        var answer = "推荐优联Ulinktek的方案";

        var match = Assert.Single(MentionMatcher.Match(answer, config));
        Assert.Equal("Ulinktek", match.MatchedText);
        Assert.Equal(4, match.OffsetStart);
        Assert.Equal(12, match.OffsetEnd);
        Assert.Equal(match.MatchedText, answer[match.OffsetStart..match.OffsetEnd]);
        Assert.Empty(MentionMatcher.Match("xUlinktek Ulinktekx Ulinktek_1 éUlinktek", config));
    }

    [Fact]
    public void DomainGluedToCjkTextMatchesAsciiHostAtOriginalOffsets()
    {
        var config = Config with
        {
            Terms = [new AnalysisTerm(Brand, "brand", "primary_domain", "szulink.com.cn")],
            IncludeSubdomains = false
        };
        var answer = "😀 访问www.szulink.com.cn了解详情，或见官网szulink.com.cn。";

        var matches = MentionMatcher.Match(answer, config);

        Assert.Equal(2, matches.Count);
        Assert.Equal("www.szulink.com.cn", matches[0].MatchedText);
        Assert.Equal(5, matches[0].OffsetStart);
        Assert.Equal(answer.IndexOf("www.", StringComparison.Ordinal), matches[0].OffsetStart);
        Assert.Equal("szulink.com.cn", matches[1].MatchedText);
        Assert.All(matches, m => Assert.Equal(m.MatchedText, answer[m.OffsetStart..m.OffsetEnd]));
        // Latin letters still form a word boundary: "caféa.test" is not a.test.
        var latin = Config with { Terms = [new AnalysisTerm(Brand, "brand", "primary_domain", "a.test")] };
        Assert.Empty(MentionMatcher.Match("caféa.test", latin));
    }

    [Fact]
    public void GluedDomainPrefersAsciiHostOverMixedIdnSpanWhenSubdomainsAreEnabled()
    {
        // With subdomains enabled the mixed IDN host "优联www.b.test" also ends with ".b.test"; the
        // longer span must not swallow the adjacent CJK alias of another subject.
        var config = Config with
        {
            Terms = [new AnalysisTerm(Brand, "brand", "alias", "优联"),
                new AnalysisTerm(B, "competitor", "primary_domain", "b.test")],
            IncludeSubdomains = true
        };
        var answer = "推荐优联www.b.test";

        var matches = MentionMatcher.Match(answer, config);

        Assert.Equal(2, matches.Count);
        Assert.Equal(("优联", Brand), (matches[0].MatchedText, matches[0].SubjectId));
        Assert.Equal(("www.b.test", B), (matches[1].MatchedText, matches[1].SubjectId));
        Assert.All(matches, m => Assert.Equal(m.MatchedText, answer[m.OffsetStart..m.OffsetEnd]));
        // A genuine IDN subdomain keeps its full span and still obeys the subdomain switch.
        var idn = Config with { Terms = [new AnalysisTerm(Brand, "brand", "primary_domain", "a.test")] };
        Assert.Equal("例子.a.test", Assert.Single(MentionMatcher.Match("见 例子.a.test", idn)).MatchedText);
        Assert.Empty(MentionMatcher.Match("见 例子.a.test", idn with { IncludeSubdomains = false }));
    }

    private static AnalyticsCheckFact Fact(Guid prompt, DateTimeOffset captured,
        IReadOnlyList<Guid> mentions, bool ownCitation)
        => new(Guid.NewGuid(), Guid.NewGuid(), prompt, prompt, Target, 1, captured,
            "succeeded", true, "completed", false, false, ownCitation, mentions,
            "cloro", "chatgpt", "US", "en-US", "scraped", "observed_surface", [], "unbranded");
}
