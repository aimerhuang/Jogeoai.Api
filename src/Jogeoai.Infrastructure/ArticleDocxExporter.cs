using System.Globalization;
using System.Net;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Jogeoai.Application;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Dml = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 把文章 Markdown 转成 Word 文档（.docx）：标题层级（Title / Heading1–6 样式）、段落、粗体 / 斜体 / 删除线、行内代码、
/// 有序 / 无序列表（含嵌套）、引用、代码块、超链接、表格；平台图片（/api/v1/content/images/{id}）从已加载的字节嵌入，
/// 外链图片不下载，降级为「图片：说明」链接文字。原始 HTML 只作为纯文本输出。
/// </summary>
public static class ArticleDocxExporter
{
    /// <summary>docx 的 MIME 类型。</summary>
    public const string ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>生成 docx 字节。</summary>
    /// <param name="title">文章标题（作为 Title 样式首段）。</param>
    /// <param name="markdown">Markdown 正文。</param>
    /// <param name="images">可嵌入的平台图片，按图片标识索引；缺失的平台图片输出占位文字。</param>
    public static byte[] Export(string title, string markdown, IReadOnlyDictionary<Guid, ContentImageBinary> images)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body());
            main.AddNewPart<StyleDefinitionsPart>().Styles = BuildStyles();
            var writer = new Writer(main, images);
            var body = main.Document.Body!;
            body.Append(Writer.Paragraph("Title", null, [Writer.TextRun(title, default)]));
            if (ArticleMarkdown.TryParse(markdown) is { } parsed)
                writer.WriteBlocks(body, parsed, default);
            else
                // Beyond Markdig's nesting / table limits: keep the content readable as plain paragraphs.
                foreach (var line in (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                    body.Append(Writer.Paragraph(null, null, [Writer.TextRun(line, default)]));
            if (writer.HasNumbering)
                main.AddNewPart<NumberingDefinitionsPart>().Numbering = writer.BuildNumbering();
            body.Append(new SectionProperties(
                new PageSize { Width = 11906U, Height = 16838U },
                new PageMargin { Top = 1440, Right = 1440U, Bottom = 1440, Left = 1440U, Header = 720U, Footer = 720U, Gutter = 0U }));
        }
        return stream.ToArray();
    }

    private readonly record struct InlineStyle(bool Bold, bool Italic, bool Strike, bool Code, bool Link);

    private readonly record struct BlockContext(int ListLevel, bool Quote, bool HeaderCell, int Indent);

    private sealed class Writer(MainDocumentPart main, IReadOnlyDictionary<Guid, ContentImageBinary> images)
    {
        private const long MaxImageWidthEmu = 5_486_400; // 6 inches
        private const long EmuPerPixel = 9_525;
        private const int IndentStep = 720;
        // Emphasis can nest without limit in Markdig (e.g. 50000 "*"), and the inline writer is recursive: past this depth
        // the remaining text is flattened into one run so a hostile body cannot overflow the stack.
        private const int MaxInlineDepth = 32;
        private readonly List<(int Id, bool Ordered, int Start)> lists = [];
        private uint drawingId = 1;

        internal bool HasNumbering => lists.Count > 0;

        internal void WriteBlocks(OpenXmlElement container, ContainerBlock blocks, BlockContext context)
        {
            foreach (var block in blocks) WriteBlock(container, block, context);
        }

        private void WriteBlock(OpenXmlElement container, Block block, BlockContext context)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    container.Append(Paragraph($"Heading{Math.Clamp(heading.Level, 1, 6)}", null, Inlines(heading.Inline, default)));
                    break;
                case ParagraphBlock paragraph:
                    container.Append(Paragraph(context.Quote ? "Quote" : null, Indentation(context.Indent),
                        Inlines(paragraph.Inline, new InlineStyle(context.HeaderCell, false, false, false, false))));
                    break;
                case ListBlock list:
                    WriteList(container, list, context);
                    break;
                case QuoteBlock quote:
                    WriteBlocks(container, quote, context with { Quote = true });
                    break;
                case CodeBlock code:
                    foreach (var line in Lines(code))
                        container.Append(Paragraph("Code", Indentation(context.Indent), [TextRun(line, new InlineStyle(false, false, false, true, false))]));
                    break;
                case ThematicBreakBlock:
                    container.Append(new Paragraph(new ParagraphProperties(new ParagraphBorders(
                        new BottomBorder { Val = BorderValues.Single, Size = 6U, Space = 1U, Color = "999999" }))));
                    break;
                case MdTable table:
                    container.Append(Table(table, context));
                    container.Append(new Paragraph());
                    break;
                case HtmlBlock html:
                    foreach (var line in Lines(html))
                        container.Append(Paragraph(null, Indentation(context.Indent), [TextRun(line, default)]));
                    break;
                case LinkReferenceDefinitionGroup:
                case LinkReferenceDefinition:
                    break;
                case ContainerBlock nested:
                    WriteBlocks(container, nested, context);
                    break;
                case LeafBlock leaf:
                    foreach (var line in Lines(leaf))
                        container.Append(Paragraph(null, Indentation(context.Indent), [TextRun(line, default)]));
                    break;
            }
        }

        private void WriteList(OpenXmlElement container, ListBlock list, BlockContext context)
        {
            var start = list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Max(parsed, 0) : 1;
            var numId = lists.Count + 1;
            lists.Add((numId, list.IsOrdered, start));
            var level = Math.Clamp(context.ListLevel, 0, 8);
            var itemIndent = IndentStep * (level + 1);
            foreach (var item in list.OfType<ListItemBlock>())
            {
                var numbered = false;
                foreach (var child in item)
                {
                    if (!numbered && child is ParagraphBlock paragraph)
                    {
                        numbered = true;
                        var properties = new ParagraphProperties();
                        if (context.Quote) properties.Append(new ParagraphStyleId { Val = "Quote" });
                        properties.Append(new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = numId }));
                        var element = new Paragraph(properties);
                        element.Append(Inlines(paragraph.Inline, default));
                        container.Append(element);
                    }
                    else if (child is ListBlock nested)
                        WriteList(container, nested, context with { ListLevel = level + 1 });
                    else
                        WriteBlock(container, child, context with { Indent = itemIndent });
                }
            }
        }

        private Table Table(MdTable source, BlockContext context)
        {
            var rows = source.OfType<MdTableRow>().ToArray();
            var columns = Math.Max(1, rows.Length == 0 ? 1 : rows.Max(row => row.Count));
            var width = 9000 / columns;
            var table = new Table(new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                    new LeftBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                    new BottomBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                    new RightBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" })));
            table.Append(new TableGrid(Enumerable.Range(0, columns).Select(_ => new GridColumn { Width = width.ToString(CultureInfo.InvariantCulture) })));
            foreach (var row in rows)
            {
                var tableRow = new TableRow();
                if (row.IsHeader) tableRow.Append(new TableRowProperties(new TableHeader()));
                var cells = row.OfType<MdTableCell>().ToArray();
                for (var index = 0; index < columns; index++)
                {
                    var cell = new TableCell(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = width.ToString(CultureInfo.InvariantCulture) }));
                    if (index < cells.Length)
                        WriteBlocks(cell, cells[index], new BlockContext(0, false, row.IsHeader, 0));
                    // Every cell needs at least one paragraph to be a valid WordprocessingML table cell.
                    if (!cell.Elements<Paragraph>().Any()) cell.Append(new Paragraph());
                    tableRow.Append(cell);
                }
                table.Append(tableRow);
            }
            return table;
        }

        private IEnumerable<OpenXmlElement> Inlines(ContainerInline? container, InlineStyle style, int depth = 0)
        {
            if (container is null) yield break;
            if (depth > MaxInlineDepth)
            {
                yield return TextRun(string.Concat(container.Descendants<LiteralInline>().Select(literal => literal.Content.ToString())), style);
                yield break;
            }
            foreach (var inline in container)
                foreach (var element in Inline(inline, style, depth + 1))
                    yield return element;
        }

        private IEnumerable<OpenXmlElement> Inline(Markdig.Syntax.Inlines.Inline inline, InlineStyle style, int depth)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    yield return TextRun(literal.Content.ToString(), style);
                    break;
                case CodeInline code:
                    yield return TextRun(code.Content, style with { Code = true });
                    break;
                case HtmlEntityInline entity:
                    yield return TextRun(entity.Transcoded.ToString(), style);
                    break;
                case LineBreakInline lineBreak:
                    yield return lineBreak.IsHard ? new Run(new Break()) : TextRun(" ", style);
                    break;
                case HtmlInline html:
                    if (html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase)) yield return new Run(new Break());
                    break;
                case AutolinkInline autolink:
                    yield return Hyperlink(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url, [TextRun(autolink.Url, style with { Link = true })]);
                    break;
                case LinkInline { IsImage: true } image:
                    yield return Image(image, style);
                    break;
                case LinkInline link:
                    var children = Inlines(link, style with { Link = true }, depth).ToList();
                    if (children.Count == 0) children.Add(TextRun(link.Url ?? string.Empty, style with { Link = true }));
                    yield return Hyperlink(link.Url, children);
                    break;
                case EmphasisInline emphasis:
                    var emphasized = emphasis.DelimiterChar == '~' ? style with { Strike = true }
                        : emphasis.DelimiterCount >= 2 ? style with { Bold = true } : style with { Italic = true };
                    foreach (var element in Inlines(emphasis, emphasized, depth)) yield return element;
                    break;
                case ContainerInline nested:
                    foreach (var element in Inlines(nested, style, depth)) yield return element;
                    break;
            }
        }

        private OpenXmlElement Image(LinkInline image, InlineStyle style)
        {
            var url = (image.Url ?? string.Empty).Trim();
            var alt = string.Concat(image.Descendants<LiteralInline>().Select(literal => literal.Content.ToString())).Trim();
            if (ArticleMarkdown.PlatformImageId(url) is { } imageId)
            {
                if (images.TryGetValue(imageId, out var binary)) return Drawing(binary, alt);
                return TextRun($"[图片不可用{(alt.Length > 0 ? "：" + alt : string.Empty)}]", style);
            }
            // External images are never downloaded; keep a readable link to the original.
            var label = $"图片：{(alt.Length > 0 ? alt : url)}";
            return Hyperlink(url, [TextRun(label, style with { Link = true })]);
        }

        private Run Drawing(ContentImageBinary binary, string alt)
        {
            var part = main.AddImagePart(binary.ContentType);
            using (var data = new MemoryStream(binary.Data, writable: false)) part.FeedData(data);
            var relationshipId = main.GetIdOfPart(part);
            long width = (binary.Width ?? 400) * EmuPerPixel, height = (binary.Height ?? 300) * EmuPerPixel;
            if (width > MaxImageWidthEmu)
            {
                height = height * MaxImageWidthEmu / width;
                width = MaxImageWidthEmu;
            }
            var id = drawingId++;
            var inline = new DW.Inline(
                new DW.Extent { Cx = width, Cy = height },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = id, Name = $"Picture {id}", Description = XmlSafe(alt) },
                new DW.NonVisualGraphicFrameDrawingProperties(new Dml.GraphicFrameLocks { NoChangeAspect = true }),
                new Dml.Graphic(new Dml.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"image{id}" },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(new Dml.Blip { Embed = relationshipId }, new Dml.Stretch(new Dml.FillRectangle())),
                        new PIC.ShapeProperties(
                            new Dml.Transform2D(new Dml.Offset { X = 0L, Y = 0L }, new Dml.Extents { Cx = width, Cy = height }),
                            new Dml.PresetGeometry(new Dml.AdjustValueList()) { Preset = Dml.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U };
            return new Run(new Drawing(inline));
        }

        private OpenXmlElement Hyperlink(string? url, IReadOnlyList<OpenXmlElement> runs)
        {
            var target = WebUtility.HtmlDecode(url ?? string.Empty).Trim();
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            {
                var relationship = main.AddHyperlinkRelationship(uri, true);
                var hyperlink = new Hyperlink { Id = relationship.Id, History = true };
                hyperlink.Append(runs.Select(run => run.CloneNode(true)));
                return hyperlink;
            }
            // Relative or unsupported targets stay readable as text.
            var text = string.Concat(runs.Select(run => run.InnerText));
            return TextRun(target.Length > 0 && !string.Equals(text, target, StringComparison.Ordinal) ? $"{text} ({target})" : text, default);
        }

        internal Numbering BuildNumbering()
        {
            var numbering = new Numbering();
            foreach (var (id, ordered, _) in lists)
                numbering.Append(AbstractNum(id, ordered));
            foreach (var (id, ordered, start) in lists)
            {
                var instance = new NumberingInstance(new AbstractNumId { Val = id }) { NumberID = id };
                if (ordered)
                    instance.Append(new LevelOverride(new StartOverrideNumberingValue { Val = start }) { LevelIndex = 0 });
                numbering.Append(instance);
            }
            return numbering;
        }

        private static AbstractNum AbstractNum(int id, bool ordered)
        {
            var abstractNum = new AbstractNum(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }) { AbstractNumberId = id };
            string[] bullets = ["•", "◦", "▪"];
            for (var level = 0; level < 9; level++)
            {
                abstractNum.Append(new Level(
                    new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = ordered ? NumberFormatValues.Decimal : NumberFormatValues.Bullet },
                    new LevelText { Val = ordered ? $"%{level + 1}." : bullets[level % bullets.Length] },
                    new LevelJustification { Val = LevelJustificationValues.Left },
                    new PreviousParagraphProperties(new Indentation
                    {
                        Left = (IndentStep * (level + 1)).ToString(CultureInfo.InvariantCulture),
                        Hanging = "360"
                    }))
                { LevelIndex = level });
            }
            return abstractNum;
        }

        internal static Paragraph Paragraph(string? style, Indentation? indentation, IEnumerable<OpenXmlElement> content)
        {
            var paragraph = new Paragraph();
            if (style is not null || indentation is not null)
            {
                var properties = new ParagraphProperties();
                if (style is not null) properties.Append(new ParagraphStyleId { Val = style });
                if (indentation is not null) properties.Append(indentation);
                paragraph.Append(properties);
            }
            paragraph.Append(content);
            return paragraph;
        }

        private static Indentation? Indentation(int left)
            => left <= 0 ? null : new Indentation { Left = left.ToString(CultureInfo.InvariantCulture) };

        internal static Run TextRun(string text, InlineStyle style)
        {
            var run = new Run();
            if (style != default)
            {
                var properties = new RunProperties();
                if (style.Link) properties.Append(new RunStyle { Val = "Hyperlink" });
                if (style.Code) properties.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
                if (style.Bold) properties.Append(new Bold());
                if (style.Italic) properties.Append(new Italic());
                if (style.Strike) properties.Append(new Strike());
                run.Append(properties);
            }
            run.Append(new Text(XmlSafe(text)) { Space = SpaceProcessingModeValues.Preserve });
            return run;
        }

        /// <summary>去掉 XML 1.0 不允许的字符（控制字符、孤立代理项），否则保存 docx 时 XmlWriter 抛出异常。</summary>
        internal static string XmlSafe(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (!text.Any(character => char.IsControl(character) || char.IsSurrogate(character) || character is '\uFFFE' or '\uFFFF')) return text;
            var builder = new System.Text.StringBuilder(text.Length);
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    builder.Append(character).Append(text[++index]);
                    continue;
                }
                if (char.IsSurrogate(character) || character is '\uFFFE' or '\uFFFF') continue;
                if (char.IsControl(character) && character is not ('\t' or '\n' or '\r')) continue;
                builder.Append(character);
            }
            return builder.ToString();
        }

        private static IEnumerable<string> Lines(LeafBlock block)
            => block.Lines.Lines.Take(block.Lines.Count).Select(line => line.Slice.ToString());
    }

    private static Styles BuildStyles()
    {
        var styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft YaHei", ComplexScript = "Calibri" },
                new FontSize { Val = "22" }, new FontSizeComplexScript { Val = "22" },
                new Languages { Val = "en-US", EastAsia = "zh-CN" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                new SpacingBetweenLines { After = "120", Line = "300", LineRule = LineSpacingRuleValues.Auto }))));
        styles.Append(new Style(new StyleName { Val = "Normal" }, new PrimaryStyle())
        { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
        styles.Append(ParagraphStyle("Title", "Title", 0, 44, outline: null));
        int[] sizes = [36, 32, 28, 26, 24, 22];
        for (var level = 1; level <= 6; level++)
            styles.Append(ParagraphStyle($"Heading{level}", $"heading {level}", 9 - level, sizes[level - 1], outline: level - 1));
        styles.Append(new Style(
            new StyleName { Val = "Quote" }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" }, new PrimaryStyle(),
            new StyleParagraphProperties(
                new ParagraphBorders(new LeftBorder { Val = BorderValues.Single, Size = 18U, Space = 8U, Color = "BBBBBB" }),
                new Indentation { Left = "567" }),
            new StyleRunProperties(new Italic(), new Color { Val = "555555" }))
        { Type = StyleValues.Paragraph, StyleId = "Quote" });
        styles.Append(new Style(
            new StyleName { Val = "Code" }, new BasedOn { Val = "Normal" }, new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { After = "0" }),
            new StyleRunProperties(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }, new FontSize { Val = "20" }))
        { Type = StyleValues.Paragraph, StyleId = "Code" });
        styles.Append(new Style(
            new StyleName { Val = "Hyperlink" }, new UIPriority { Val = 99 }, new UnhideWhenUsed(),
            new StyleRunProperties(new Color { Val = "0563C1" }, new Underline { Val = UnderlineValues.Single }))
        { Type = StyleValues.Character, StyleId = "Hyperlink" });
        return styles;
    }

    private static Style ParagraphStyle(string id, string name, int priority, int halfPoints, int? outline)
    {
        var paragraph = new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "240", After = "120" });
        if (outline is not null) paragraph.Append(new OutlineLevel { Val = outline.Value });
        return new Style(
            new StyleName { Val = name }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" },
            new UIPriority { Val = priority }, new PrimaryStyle(), paragraph,
            new StyleRunProperties(new Bold(), new FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) }))
        { Type = StyleValues.Paragraph, StyleId = id };
    }
}
