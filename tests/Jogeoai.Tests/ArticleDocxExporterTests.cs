using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Dml = DocumentFormat.OpenXml.Drawing;

namespace Jogeoai.Tests;

/// <summary>Markdown → docx 导出（B-R6 / AC-B5）：用 OpenXml 打开生成结果并断言结构；不连接数据库或对象存储。</summary>
public sealed class ArticleDocxExporterTests
{
    // 2 × 1 transparent PNG.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAAC0lEQVR42mNgAAIAAAUAAen63NgAAAAASUVORK5CYII=");

    [Fact]
    public void ExportKeepsHeadingsListsQuoteTableLinksAndEmbedsPlatformImages()
    {
        var platform = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var markdown = $"""
            ## 选型要点

            正文有 **加粗**、*斜体* 与 [官网链接](https://brand.example/about)。

            ### 细分维度

            - 无序一
              - 嵌套无序
            - 无序二

            3. 有序三
            4. 有序四

            > 引用的说明文字

            | 维度 | 说明 |
            | --- | --- |
            | 价格 | 待补充 |
            | 售后 | 以官网为准 |

            ![平台图]({ArticleEditorLimits.ImageUrlPrefix}{platform})

            ![外链图](https://cdn.example/x.png)

            ![已删除]({ArticleEditorLimits.ImageUrlPrefix}{missing})
            """;
        var bytes = ArticleDocxExporter.Export("文章标题", markdown,
            new Dictionary<Guid, ContentImageBinary> { [platform] = new("image/png", Png, 2, 1) });

        using var stream = new MemoryStream(bytes);
        using var document = WordprocessingDocument.Open(stream, false);
        var main = document.MainDocumentPart!;
        var body = main.Document!.Body!;
        var paragraphs = body.Elements<Paragraph>().ToArray();
        string? Style(Paragraph paragraph) => paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;

        Assert.Equal("Title", Style(paragraphs[0]));
        Assert.Equal("文章标题", paragraphs[0].InnerText);
        Assert.Equal("选型要点", Assert.Single(paragraphs, p => Style(p) == "Heading2").InnerText);
        Assert.Equal("细分维度", Assert.Single(paragraphs, p => Style(p) == "Heading3").InnerText);
        Assert.Contains(main.StyleDefinitionsPart!.Styles!.Elements<Style>(), style => style.StyleId == "Heading2"
            && style.StyleParagraphProperties?.OutlineLevel?.Val?.Value == 1);

        var emphasis = paragraphs.Single(p => p.InnerText.StartsWith("正文有", StringComparison.Ordinal));
        Assert.Contains(emphasis.Descendants<Run>(), run => run.RunProperties?.Bold is not null && run.InnerText == "加粗");
        Assert.Contains(emphasis.Descendants<Run>(), run => run.RunProperties?.Italic is not null && run.InnerText == "斜体");
        var link = Assert.Single(emphasis.Elements<Hyperlink>());
        Assert.Equal("官网链接", link.InnerText);
        Assert.Equal(new Uri("https://brand.example/about"), main.HyperlinkRelationships.Single(rel => rel.Id == link.Id).Uri);

        var numbered = paragraphs.Where(p => p.ParagraphProperties?.NumberingProperties is not null).ToArray();
        Assert.Equal(["无序一", "嵌套无序", "无序二", "有序三", "有序四"], numbered.Select(p => p.InnerText));
        Assert.Equal([0, 1, 0, 0, 0], numbered.Select(p => p.ParagraphProperties!.NumberingProperties!.NumberingLevelReference!.Val!.Value));
        var numbering = main.NumberingDefinitionsPart!.Numbering!;
        var orderedNum = numbered[3].ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value;
        var orderedInstance = numbering.Elements<NumberingInstance>().Single(num => num.NumberID!.Value == orderedNum);
        Assert.Equal(3, orderedInstance.Elements<LevelOverride>().Single().StartOverrideNumberingValue!.Val!.Value);
        var orderedAbstract = numbering.Elements<AbstractNum>().Single(item => item.AbstractNumberId!.Value == orderedInstance.AbstractNumId!.Val!.Value);
        Assert.Equal(NumberFormatValues.Decimal, orderedAbstract.Elements<Level>().First().NumberingFormat!.Val!.Value);
        var bulletNum = numbered[0].ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value;
        var bulletAbstract = numbering.Elements<AbstractNum>().Single(item => item.AbstractNumberId!.Value == bulletNum);
        Assert.Equal(NumberFormatValues.Bullet, bulletAbstract.Elements<Level>().First().NumberingFormat!.Val!.Value);

        Assert.Equal("引用的说明文字", Assert.Single(paragraphs, p => Style(p) == "Quote").InnerText);

        var table = Assert.Single(body.Elements<Table>());
        var rows = table.Elements<TableRow>().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.NotNull(rows[0].TableRowProperties?.GetFirstChild<TableHeader>());
        Assert.Equal(["维度", "说明"], rows[0].Elements<TableCell>().Select(cell => cell.InnerText));
        Assert.Equal(["售后", "以官网为准"], rows[2].Elements<TableCell>().Select(cell => cell.InnerText));
        Assert.All(rows[0].Descendants<Run>(), run => Assert.NotNull(run.RunProperties?.Bold));

        var blip = Assert.Single(body.Descendants<Dml.Blip>());
        var image = Assert.IsAssignableFrom<ImagePart>(main.GetPartById(blip.Embed!.Value!));
        Assert.Equal("image/png", image.ContentType);
        using (var data = image.GetStream()) Assert.Equal(Png.Length, data.Length);

        var external = body.Descendants<Hyperlink>().Single(h => h.InnerText.StartsWith("图片：", StringComparison.Ordinal));
        Assert.Equal("图片：外链图", external.InnerText);
        Assert.Equal(new Uri("https://cdn.example/x.png"), main.HyperlinkRelationships.Single(rel => rel.Id == external.Id).Uri);
        Assert.Contains(paragraphs, p => p.InnerText == "[图片不可用：已删除]");

        var errors = new OpenXmlValidator().Validate(document).Select(error => $"{error.Path?.XPath}: {error.Description}").ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors));
    }

    [Fact]
    public void PlainMarkdownWithCodeBlockAndRelativeLinkProducesValidDocumentWithoutNumbering()
    {
        var bytes = ArticleDocxExporter.Export("标题", "第一段\n第二行\n\n```\ncode line\n```\n\n[相对链接](/docs/a)\n\n---\n\n<b>原始 HTML</b>", new Dictionary<Guid, ContentImageBinary>());
        using var stream = new MemoryStream(bytes);
        using var document = WordprocessingDocument.Open(stream, false);
        var main = document.MainDocumentPart!;
        Assert.Null(main.NumberingDefinitionsPart);
        var texts = main.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToArray();
        Assert.Contains("第一段 第二行", texts);
        Assert.Contains("code line", texts);
        Assert.Contains("相对链接 (/docs/a)", texts);
        Assert.Empty(main.HyperlinkRelationships);
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    [Fact]
    public void HostileNestingTablesAndControlCharactersStillProduceAValidDocument()
    {
        var images = new Dictionary<Guid, ContentImageBinary>();
        // Emphasis nests without a Markdig limit; the writer must flatten it instead of recursing 25000 levels deep.
        var deep = new string('*', 25_000) + "深层文字" + new string('*', 25_000);
        // Lists beyond Markdig's nesting limit fail to parse; the export falls back to plain paragraphs.
        var tooDeep = string.Concat(Enumerable.Repeat("- ", 30_000)) + "x";
        foreach (var markdown in new[] { deep, tooDeep, "控制\u0001字符\u000B与孤立代理\uD800结束 ![替代\u0002](https://cdn.example/a.png)" })
        {
            var bytes = ArticleDocxExporter.Export("标题\u0007", markdown, images);
            using var stream = new MemoryStream(bytes);
            using var document = WordprocessingDocument.Open(stream, false);
            Assert.NotNull(document.MainDocumentPart!.Document!.Body);
        }
        using var plain = new MemoryStream(ArticleDocxExporter.Export("标题", deep, images));
        using var opened = WordprocessingDocument.Open(plain, false);
        Assert.Contains("深层文字", opened.MainDocumentPart!.Document!.Body!.InnerText, StringComparison.Ordinal);
    }
}
