using Jogeoai.Providers.Domestic;

namespace Jogeoai.Tests;

public sealed class DeepSeekMarkdownTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  \r\n \n\t", "")]
    [InlineData("单行", "单行")]
    public void EmptyAndTrivialInputs(string? input, string expected) => Assert.Equal(expected, DeepSeekMarkdown.Normalize(input));

    [Fact]
    public void LineEndingsAreUnifiedAndTrailingWhitespaceIsRemoved()
    {
        Assert.Equal("第一行\n第二行\n第三行", DeepSeekMarkdown.Normalize("第一行  \r\n第二行\t\r第三行 　"));
    }

    [Fact]
    public void RunsOfBlankLinesCollapseToOneOutsideFences()
    {
        Assert.Equal("段落一\n\n段落二\n\n段落三", DeepSeekMarkdown.Normalize("段落一\n\n\n\n段落二\n  \n \t\n段落三"));
        // A single blank line between blocks is kept as is.
        Assert.Equal("a\n\nb", DeepSeekMarkdown.Normalize("a\n\nb"));
    }

    [Fact]
    public void LeadingAndTrailingWhitespaceIsTrimmed()
    {
        Assert.Equal("## 标题\n\n正文", DeepSeekMarkdown.Normalize("\n\n  \n## 标题\n\n正文\n\n\n   "));
    }

    [Fact]
    public void FencedCodeBlocksAreLeftUntouched()
    {
        const string input = "前文  \n```python\nx = 1  \n\n\n\ny = 2\t\n```\n\n\n\n~~~\n  缩进   \n\n\n~~~\n后文";
        Assert.Equal("前文\n```python\nx = 1  \n\n\n\ny = 2\t\n```\n\n~~~\n  缩进   \n\n\n~~~\n后文", DeepSeekMarkdown.Normalize(input));
    }

    [Fact]
    public void ClosingFenceMustMatchTheOpeningMarker()
    {
        // A ~~~ line inside a ``` block is content, so the blank lines after it stay inside the block.
        const string input = "```\n~~~\n\n\n\n```\n\n\n\n尾";
        Assert.Equal("```\n~~~\n\n\n\n```\n\n尾", DeepSeekMarkdown.Normalize(input));
    }

    [Fact]
    public void UnclosedFenceKeepsTheRestVerbatimExceptTheFinalTrim()
    {
        Assert.Equal("```\ncode  \n\n\n\nmore", DeepSeekMarkdown.Normalize("```\ncode  \n\n\n\nmore  \n"));
    }

    [Fact]
    public void MarkdownSyntaxIsNeverRewritten()
    {
        const string markdown = """
            ## 一、管道检测设备

            | 设备类型 | 用途 |
            |---|---|
            | **CCTV 机器人** | 管道内部录像 |

            ---

            1. **武汉中仪物联** — 国内管道检测龙头
               - 子项 *斜体* `code`
            > 引用 [链接](https://example.com)
            """;
        Assert.Equal(markdown, DeepSeekMarkdown.Normalize(markdown));
    }
}
