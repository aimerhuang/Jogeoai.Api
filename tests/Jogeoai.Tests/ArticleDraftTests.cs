using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>草稿自动保存与 PATCH 共用的编辑侧安全规则（B-R3）；不连接数据库。</summary>
public sealed class ArticleDraftTests
{
    private static readonly Guid Base = Guid.NewGuid();

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("前文 <IFRAME src=\"https://x.example\"></iframe>")]
    [InlineData("<object data=\"x\"></object>")]
    [InlineData("<embed src=\"x\">")]
    [InlineData("<form action=\"https://x.example\"><input></form>")]
    [InlineData("[点我](javascript:alert(1))")]
    [InlineData("[点我](JaVaScRiPt :alert(1))")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">x</a>")]
    [InlineData("[下载](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("<img src=\"https://ok.example/a.png\" onerror=\"alert(1)\">")]
    [InlineData("<div onclick = 'x()'>正文</div>")]
    [InlineData("[脚本](vbscript:msgbox)")]
    public void DangerousMarkupIsRejectedWith400(string body)
    {
        var error = Assert.Throws<CollectorStoreException>(() => ArticleEditSafety.Validate("标题", "正文\n\n" + body));
        Assert.Equal((ArticleEditorErrorCodes.ContentUnsafe, 400), (error.Code, error.StatusCode));
        var draft = Assert.Throws<CollectorStoreException>(() => ContentStore.ValidateDraft(new ArticleDraftRequest("标题", body, Base, 0)));
        Assert.Equal(400, draft.StatusCode);
    }

    [Theory]
    [InlineData("![外链](http://insecure.example/a.png)")]
    [InlineData("![相对](/uploads/a.png)")]
    [InlineData("![伪平台](/api/v1/content/images/not-a-guid)")]
    [InlineData("![引用][pic]\n\n[pic]: ftp://files.example/a.png")]
    [InlineData("<img src=\"http://insecure.example/a.png\">")]
    public void ImagesOutsidePlatformOrHttpsAreRejected(string body)
    {
        var error = Assert.Throws<CollectorStoreException>(() => ArticleEditSafety.Validate("标题", body));
        Assert.Equal((ArticleEditorErrorCodes.ImageSourceInvalid, 400), (error.Code, error.StatusCode));
    }

    [Fact]
    public void EditorMarkdownWithCodeFencesTablesAndAllowedImagesPasses()
    {
        var image = Guid.NewGuid();
        var body = $"""
            ## 选型要点

            1. 第一条 **加粗** 与 *斜体*
            2. 第二条，条件写法 conditional=true 不是事件属性

            > 引用说明

            | 维度 | 说明 |
            | --- | --- |
            | 价格 | 待补充 |

            ![平台图片]({ArticleEditorLimits.ImageUrlPrefix}{image})
            ![外链图片](https://cdn.example/a.webp)
            <img src="https://cdn.example/b.png" alt="b">

            ```html
            <div>代码块允许出现</div>
            ```

            [官网](https://brand.example)
            """;
        ArticleEditSafety.Validate("标题", body);
        ContentStore.ValidateDraft(new ArticleDraftRequest("标题", body, Base, 3));
        Assert.Equal([$"{ArticleEditorLimits.ImageUrlPrefix}{image}", "https://cdn.example/a.webp"], ArticleMarkdown.ImageUrls(body));
        Assert.Equal(image, ArticleMarkdown.PlatformImageId($"{ArticleEditorLimits.ImageUrlPrefix}{image:N}"));
        Assert.Null(ArticleMarkdown.PlatformImageId($"{ArticleEditorLimits.ImageUrlPrefix}{{{image}}}"));
    }

    [Fact]
    public void ProseMentioningSchemesPassesButUrlPositionsAndAutolinksAreRejected()
    {
        ArticleEditSafety.Validate("JavaScript: 入门指南", "推荐阅读 JavaScript: The Good Parts，以及 data: 字段说明。");
        foreach (var body in new[] { "<javascript:alert(1)>", "[x]: javascript:alert(1)\n\n[点我][x]", "<a href='data:text/html,hi'>x</a>" })
            Assert.Equal(ArticleEditorErrorCodes.ContentUnsafe,
                Assert.Throws<CollectorStoreException>(() => ArticleEditSafety.Validate("标题", body)).Code);
    }

    [Fact]
    public void LegacyBodiesStaySaveableAndOnlyNewImagesAreRestricted()
    {
        const string legacy = "导入正文\n\n![旧图](http://legacy.example/a.png)\n\n<iframe src=\"https://v.example\"></iframe>";
        // Unchanged body (e.g. PATCH that only changes tags or the title) is not re-judged.
        ArticleEditSafety.Validate("新标题", legacy, legacy);
        ContentStore.ValidateDraft(new ArticleDraftRequest("新标题", legacy, Base, 0), legacy);

        const string withOldImage = "改过的正文\n\n![旧图](http://legacy.example/a.png)";
        ArticleEditSafety.Validate("标题", withOldImage, legacy);
        Assert.Equal(ArticleEditorErrorCodes.ImageSourceInvalid, Assert.Throws<CollectorStoreException>(
            () => ArticleEditSafety.Validate("标题", withOldImage + "\n\n![新图](http://new.example/b.png)", legacy)).Code);
        // Changed bodies are fully checked again: dangerous markup kept from the legacy body is rejected.
        Assert.Equal(ArticleEditorErrorCodes.ContentUnsafe, Assert.Throws<CollectorStoreException>(
            () => ArticleEditSafety.Validate("标题", legacy + "\n\n补充", legacy)).Code);
        // A dangerous scheme is never grandfathered.
        const string dataImage = "![x](data:image/png;base64,AAAA)";
        Assert.Throws<CollectorStoreException>(() => ArticleEditSafety.Validate("标题", dataImage + " 改", dataImage));
    }

    [Fact]
    public void MarkdownBeyondParserLimitsIsA400NotACrash()
    {
        var error = Assert.Throws<CollectorStoreException>(() => ArticleEditSafety.Validate("标题", string.Concat(Enumerable.Repeat("- ", 30_000)) + "x"));
        Assert.Equal((ArticleEditorErrorCodes.ContentUnsafe, 400), (error.Code, error.StatusCode));
        Assert.Empty(ArticleMarkdown.ImageUrls(string.Concat(Enumerable.Repeat("- ", 30_000)) + "x"));
    }

    [Fact]
    public void DraftRequestBoundsAreChecked()
    {
        Assert.Equal(400, Assert.Throws<CollectorStoreException>(() => ContentStore.ValidateDraft(new ArticleDraftRequest(" ", "正文", Base, 0))).StatusCode);
        Assert.Equal(400, Assert.Throws<CollectorStoreException>(() => ContentStore.ValidateDraft(new ArticleDraftRequest(new string('题', 201), "正文", Base, 0))).StatusCode);
        Assert.Equal(400, Assert.Throws<CollectorStoreException>(() => ContentStore.ValidateDraft(new ArticleDraftRequest("标题", new string('字', 100_001), Base, 0))).StatusCode);
        Assert.Equal(400, Assert.Throws<CollectorStoreException>(() => ContentStore.ValidateDraft(new ArticleDraftRequest("标题", "正文", Base, -1))).StatusCode);
        Assert.Equal(400, Assert.Throws<CollectorStoreException>(() => ContentStore.ValidateDraft(new ArticleDraftRequest("标题", "正文", Guid.Empty, 0))).StatusCode);
        Assert.Equal(400, Assert.Throws<CollectorStoreException>(() => ContentStore.ValidateDraft(new ArticleDraftRequest("<script>", "正文", Base, 0))).StatusCode);
        ContentStore.ValidateDraft(new ArticleDraftRequest("标题", "正文", null, 0));
    }
}
