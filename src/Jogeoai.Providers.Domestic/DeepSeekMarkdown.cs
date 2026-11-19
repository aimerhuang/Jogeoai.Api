using System.Text;

namespace Jogeoai.Providers.Domestic;

/// <summary>
/// DeepSeek 回答的 Markdown 规范化：只统一空白，不改写任何 Markdown 语法（标题、列表、加粗、表格、链接保持原样）。
/// </summary>
public static class DeepSeekMarkdown
{
    /// <summary>
    /// 依次执行：1. <c>\r\n</c> 与 <c>\r</c> 统一为 <c>\n</c>；2. 去掉每行行尾空白（``` 或 ~~~ 围栏代码块内部保持原样）；
    /// 3. 围栏外连续两行及以上的空行（即三个及以上连续换行）合并为一个空行；4. 去掉首尾空白。
    /// </summary>
    /// <param name="markdown">模型输出的 Markdown 正文；为空时返回空字符串。</param>
    public static string Normalize(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var output = new StringBuilder(markdown.Length);
        string? fence = null;
        var blankRun = 0;
        foreach (var line in lines)
        {
            var marker = FenceMarker(line);
            if (fence is not null)
            {
                // Inside a fenced code block every character is content, including trailing spaces and blank lines.
                Append(output, line);
                if (marker is not null && marker[0] == fence[0] && marker.Length >= fence.Length
                    && line.TrimStart().TrimStart(fence[0]).Trim().Length == 0)
                    fence = null;
                continue;
            }
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                // One blank line separates blocks; further consecutive blank lines carry no Markdown meaning.
                if (++blankRun > 1) continue;
                Append(output, "");
                continue;
            }
            blankRun = 0;
            Append(output, trimmed);
            if (marker is not null) fence = marker;
        }
        return output.ToString().Trim();
    }

    private static void Append(StringBuilder output, string line) => output.Append(line).Append('\n');

    // A fence opens with at least three backticks or tildes, indented by at most three spaces.
    private static string? FenceMarker(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent > 3 || indent >= line.Length || line[indent] is not ('`' or '~')) return null;
        var c = line[indent];
        var length = 0;
        while (indent + length < line.Length && line[indent + length] == c) length++;
        return length >= 3 ? new string(c, length) : null;
    }
}
