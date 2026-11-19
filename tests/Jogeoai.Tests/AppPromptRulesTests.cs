using System.Globalization;
using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>提问进入 App 渠道的自动检查（候选 v1，DC-01 未确认）：每条规则的边界与原因码。</summary>
public sealed class AppPromptRulesTests
{
    private static string? Reason(string? content) => AppPromptRules.Evaluate(content).ReasonCode;

    private static void Accepted(string? content)
    {
        var result = AppPromptRules.Evaluate(content);
        Assert.True(result.Accepted, $"{result.ReasonCode}: {content}");
        Assert.Null(result.ReasonCode);
    }

    private static void Rejected(string expected, string? content)
    {
        var result = AppPromptRules.Evaluate(content);
        Assert.False(result.Accepted);
        Assert.Equal(expected, result.ReasonCode);
    }

    [Fact]
    public void CandidateConstantsArePinned()
    {
        Assert.Equal(1, AppPromptRules.RuleVersion);
        Assert.Equal(1, AppPromptRules.MinLength);
        Assert.Equal(200, AppPromptRules.MaxLength);
        Assert.Equal(["EMPTY", "TOO_LONG", "MULTILINE", "CONTROL_CHARACTER", "CONTAINS_URL"], AppPromptRules.ReasonCodes);
        Assert.Equal(("EMPTY", "TOO_LONG", "MULTILINE", "CONTROL_CHARACTER", "CONTAINS_URL"),
            (AppPromptRules.ReasonEmpty, AppPromptRules.ReasonTooLong, AppPromptRules.ReasonMultiline,
                AppPromptRules.ReasonControlCharacter, AppPromptRules.ReasonContainsUrl));
        Assert.Same(AppPromptEvaluation.Pass, AppPromptRules.Evaluate("豆包怎么样"));
        Assert.Equal(new AppPromptEvaluation(false, "EMPTY"), AppPromptEvaluation.Reject("EMPTY"));
    }

    [Theory]
    [InlineData("豆包和元宝哪个更适合写周报？")]
    [InlineData("a")]
    [InlineData("问")]
    [InlineData("2026 年值得买的 5 款扫地机器人，预算 3000 元以内")]
    [InlineData("What is the best CRM for a 10-person team?")]
    [InlineData("C++ 和 Rust 哪个好？（附理由）#编程 @新手 100% “引号”…")]
    public void OrdinaryQuestionsAreAccepted(string content) => Accepted(content);

    [Fact]
    public void NullEmptyAndWhitespaceOnlyAreEmpty()
    {
        // Tab, CR and LF are trimmed as leading / trailing white space, so a whitespace-only prompt is EMPTY, not MULTILINE.
        foreach (var content in new[] { null, "", " ", "   ", "\t", "\n", "\r\n", " \t\r\n ", "\u3000\u3000", "\u00A0", "\u2028", "\u0085" })
            Rejected("EMPTY", content);
    }

    [Fact]
    public void LengthBoundaryIsTwoHundredCharactersAfterTrimming()
    {
        Accepted(new string('a', 200));
        Rejected("TOO_LONG", new string('a', 201));
        // CJK characters count one each.
        Accepted(new string('问', 200));
        Rejected("TOO_LONG", new string('问', 201));
        Accepted(new string('问', 199) + "？");
        // Leading / trailing white space is not counted (prompts are stored trimmed) ...
        Accepted("  \t" + new string('问', 200) + " \r\n");
        // ... but inner white space is.
        Rejected("TOO_LONG", new string('问', 100) + " " + new string('问', 100));
        Rejected("TOO_LONG", new string('a', 10000));
    }

    [Fact]
    public void LengthCountsUtf16CodeUnitsLikeTheExistingPromptValidation()
    {
        const string emoji = "\U0001F600"; // one code point, two UTF-16 code units
        Assert.Equal(2, emoji.Length);
        Accepted(emoji);
        Accepted(string.Concat(Enumerable.Repeat(emoji, 100)));           // 200 code units
        Rejected("TOO_LONG", string.Concat(Enumerable.Repeat(emoji, 100)) + "a"); // 201 code units, 101 code points
        Rejected("TOO_LONG", string.Concat(Enumerable.Repeat(emoji, 101)));
        Accepted(new string('问', 198) + emoji);
        Rejected("TOO_LONG", new string('问', 199) + emoji);
        // A supplementary-plane CJK ideograph counts two as well.
        Accepted(string.Concat(Enumerable.Repeat("\U00020000", 100)));
        Rejected("TOO_LONG", string.Concat(Enumerable.Repeat("\U00020000", 100)) + "问");
        // Zero-width joiner sequences and variation selectors are format characters, not control characters.
        Accepted("这个表情 \U0001F468\u200D\U0001F469\u200D\U0001F467 和 ❤\uFE0F 是什么意思");
    }

    [Fact]
    public void LineBreaksInsideThePromptAreMultiline()
    {
        foreach (var separator in new[] { "\n", "\r\n", "\r", "\n\n", "\u0085", "\u2028", "\u2029" })
            Rejected("MULTILINE", $"第一行{separator}第二行");
        // A trailing line break is trimmed away, like when the prompt is saved.
        Accepted("只有一行\r\n");
        Accepted("\n只有一行");
    }

    [Fact]
    public void OtherControlCharactersAreRejected()
    {
        foreach (var control in new[] { '\t', '\0', '\u0001', '\u0008', '\u000B', '\u000C', '\u001B', '\u001F', '\u007F', '\u0080', '\u009F' })
            Rejected("CONTROL_CHARACTER", $"前半{control}后半");
        // The neighbours of the control ranges are ordinary characters.
        foreach (var ordinary in new[] { ' ', '~', '\u00A0', '¡', '\u3000' })
            Accepted($"前半{ordinary}后半");
    }

    [Theory]
    [InlineData("看看 http://example.com 这个网站")]
    [InlineData("https://example.com/path?q=1")]
    [InlineData("HTTPS://Example.COM")]
    [InlineData("Http://例子.中国")]
    [InlineData("这个链接https://t.cn/abc安全吗")]
    [InlineData("http://")]
    [InlineData("www.example.com 靠谱吗")]
    [InlineData("访问www.example.com了解")]
    [InlineData("WWW.Example.cn")]
    [InlineData("官网（www.jd.com）怎么样")]
    [InlineData("m.www.example.com")]
    [InlineData("www.1688.com")]
    public void UrlsAreRejected(string content) => Rejected("CONTAINS_URL", content);

    [Theory]
    [InlineData("http 和 https 有什么区别")]
    [InlineData("http:/example 是合法地址吗")]
    [InlineData("https:example")]
    [InlineData("www 是什么的缩写")]
    [InlineData("www. 开头的网址安全吗")]
    [InlineData("www.开头的网址安全吗")] // only an ASCII letter or digit right after "www." makes it a host
    [InlineData("以 www. 结尾")]
    [InlineData("www.。")]
    [InlineData("awww.cute 是什么梗")]
    [InlineData("jd.com 和 tmall.com 哪个便宜")] // bare domains without a scheme or www. are outside candidate v1
    [InlineData("user@example.com 这种邮箱格式对吗")]
    public void TextThatOnlyLooksLikeAUrlIsAccepted(string content) => Accepted(content);

    [Fact]
    public void TheFirstViolatedRuleInTheDocumentedOrderWins()
    {
        Rejected("TOO_LONG", new string('问', 150) + "\n" + new string('问', 100));
        Rejected("TOO_LONG", "http://example.com/" + new string('a', 200));
        Rejected("MULTILINE", "第一行\n\thttp://example.com");
        Rejected("MULTILINE", "a\tb\nc");
        Rejected("CONTROL_CHARACTER", "http://example.com\t好用吗");
    }

    [Fact]
    public void EvaluationIsDeterministicAndCultureInvariant()
    {
        string[] samples =
        [
            "豆包怎么样", "", new string('问', 201), "a\nb", "a\tb", "HTTP://EXAMPLE.COM", "WWW.EXAMPLE.COM", "www.iı.com", "İstanbul 好玩吗",
        ];
        var expected = samples.Select(Reason).ToArray();
        Assert.Equal(new string?[] { null, "EMPTY", "TOO_LONG", "MULTILINE", "CONTROL_CHARACTER", "CONTAINS_URL", "CONTAINS_URL", "CONTAINS_URL", null }, expected);
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "tr-TR", "zh-CN", "ar-SA", "" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal(expected, samples.Select(Reason).ToArray());
                Assert.Equal(expected, samples.Select(Reason).ToArray());
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void LongestExistingPromptLengthPasses()
    {
        // T01 (2026-11-16): the longest enabled prompt in the local database has 49 characters.
        Accepted(new string('问', 49));
    }
}
