import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { chromium, type Browser, type Page } from "playwright";
import { afterAll, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { answerToMarkdown, readAnswerMarkdown, readAnswerPlainText } from "./answer-markdown.ts";

// Same selector string as the reviewed live contract value (design.md §6); the DOM is synthetic.
const LIVE_IGNORE =
  '[data-testid="carousel-item"], [data-testid="mdbox_image"], [data-testid="video-box-bg-image"], span[class*="spacing-"] + span';
const CHIP = (text: string) =>
  `<span class="spacing-Bwmglm"></span><span class="container-sWvQla">${text}</span><span class="spacing-Bwmglm !w-2"></span>`;

// Full expected output for fixtures/doubao-answer-synthetic.html with LIVE_IGNORE.
const REALISTIC_MARKDOWN = [
  "下面分**推索式（管道内窥镜**）和**履带式（爬行机器人**）两类，按口径整理：",
  "",
  "### ✅ 推索式（适合 DN30–DN200 管道）",
  "",
  "1. **华普森 Wopson（深圳）**  ",
  "   型号：HD0‑C23H、HD0‑C40H，支持自动计米与故障排查。  ",
  "   优势：DN50\\~DN300 通用，整机 2.5\\~6 万元。",
  "2. **示例品牌 B**  ",
  "   型号：X\\_200\\*Pro  ",
  "   1\\. 不是列表，# 也不是标题",
  "",
  "### ⚠️ 补充：低价机型",
  "",
  "像 Longood 等品牌**没有计米**功能，价格更低。",
  "",
  "- 主要做**小口径** → 优先：**推索式**。",
  "- 主要做**大口径** → 优先：**履带式**。",
  "",
  "> 重要提醒：WiFi 是**主机 / 控制箱发热点**，不是镜头自身联网。",
  "",
  "视频演示：",
  "",
  "如果你需要，我可以按预算再细分。",
].join("\n");

describe("answerToMarkdown (synthetic DOM in headless Chromium, no live page)", () => {
  let browser: Browser;
  let page: Page;
  let realistic: string;
  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    realistic = await readFile(
      new URL("./fixtures/doubao-answer-synthetic.html", import.meta.url),
      "utf8",
    );
  });
  beforeEach(async () => {
    page = await browser.newPage();
  });
  afterAll(async () => {
    await browser.close();
  });
  async function markdown(html: string, ignore?: string): Promise<string> {
    await page.setContent(
      `<!doctype html><html lang="zh-CN"><body><div id="root">${html}</div></body></html>`,
    );
    return page.locator("#root").evaluate(answerToMarkdown, ignore);
  }

  it("converts a realistic Doubao answer body exactly", async () => {
    expect(await markdown(realistic, LIVE_IGNORE)).toBe(REALISTIC_MARKDOWN);
  });
  it("keeps chip text without the ignore selector but still drops media", async () => {
    const result = await markdown(realistic);
    expect(result).toContain("故障排查wopson.com...。");
    expect(result).toContain("价格更低Alibaba。");
    expect(result).not.toMatch(/img|svg|!\[/u);
    expect(result).toContain("视频演示：\n\n如果你需要");
  });
  it("removes an inline source chip without leaving spaces before the punctuation", async () => {
    expect(
      await markdown(
        `<div><div>先做故障排查${CHIP("wopson.com...")}。然后${CHIP("Alibaba")}，再看</div></div>`,
        LIVE_IGNORE,
      ),
    ).toBe("先做故障排查。然后，再看");
  });
  it("renders h1–h6 and drops chips inside headings", async () => {
    expect(
      await markdown(
        `<h1>一级</h1><h2>3、3RDEYE 管道镜${CHIP("3rdeye.com")}</h2><h3>三级</h3><h4>四级</h4><h5>五级</h5><h6>六级</h6><h2>C 语言 #</h2><h3> </h3>`,
        LIVE_IGNORE,
      ),
    ).toBe(
      "# 一级\n\n## 3、3RDEYE 管道镜\n\n### 三级\n\n#### 四级\n\n##### 五级\n\n###### 六级\n\n## C 语言 \\#",
    );
  });
  it("renders nested ordered/unordered lists with start, value and continuation indentation", async () => {
    expect(
      await markdown(
        `<ol start="3"><li>第三<ul><li>子项 A</li><li>子项 B<ol><li>孙项</li></ol></li></ul></li><li value="9">第九</li><li>第十<div></div>续行</li></ol>`,
      ),
    ).toBe(
      [
        "3. 第三",
        "   - 子项 A",
        "   - 子项 B",
        "     1. 孙项",
        "9. 第九",
        "10. 第十  ",
        "    续行",
      ].join("\n"),
    );
  });
  it("keeps multi-paragraph items and non-1 nested ordered lists inside the item", async () => {
    expect(
      await markdown(
        `<ul><li><p>段一</p><p>段二</p></li><li>父项<ol start="2"><li>二</li></ol></li><li> </li></ul>`,
      ),
    ).toBe("- 段一\n\n  段二\n- 父项\n\n  2. 二");
  });
  it("keeps adjacent lists apart by switching the marker", async () => {
    expect(
      await markdown(
        "<ul><li>甲</li></ul><ul><li>乙</li></ul><ol><li>一</li></ol><ol><li>二</li></ol>",
      ),
    ).toBe("- 甲\n\n* 乙\n\n1. 一\n\n1) 二");
  });
  it("turns empty divs and <br> inside inline content into single hard breaks", async () => {
    expect(
      await markdown(
        "<div><div></div>第一行<div></div><div><span></span></div> 第二行<br><br>第三行<div></div></div>",
      ),
    ).toBe("第一行  \n第二行  \n第三行");
  });
  it("prefixes every line of nested blockquotes, including hard breaks", async () => {
    expect(
      await markdown(
        "<blockquote><div><div>外层<div></div>第二行</div></div><blockquote><p>内层</p></blockquote></blockquote>",
      ),
    ).toBe("> 外层  \n> 第二行\n>\n> > 内层");
  });
  it("renders GFM tables with escaped pipes and single-line cells", async () => {
    expect(
      await markdown(
        `<table><caption>对比</caption><thead><tr><th>型号</th><th>口径</th></tr></thead><tbody>
          <tr><td>A|B</td><td>DN50~DN300<br>可选<div>加长</div></td></tr>
          <tr><td><code>x|y</code></td></tr></tbody></table>
         <table><tr><td>首行</td><td><strong>粗</strong></td></tr><tr><td>次行</td><td>*</td></tr></table>`,
      ),
    ).toBe(
      [
        "对比",
        "",
        "| 型号 | 口径 |",
        "| --- | --- |",
        "| A\\|B | DN50\\~DN300 可选 加长 |",
        "| `x\\|y` |  |",
        "",
        "| 首行 | **粗** |",
        "| --- | --- |",
        "| 次行 | \\* |",
      ].join("\n"),
    );
  });
  it("renders pre as fenced code without escaping and inline code as backtick spans", async () => {
    expect(
      await markdown(
        '<pre><code class="language-python">def f(x):\n    return x * 2  # a_b ~c\n</code></pre>' +
          "<pre>has ``` fence</pre><p>用 <code>a*b</code> 或 <code>a`b</code> 或 <code> </code></p>",
      ),
    ).toBe(
      [
        "```python",
        "def f(x):",
        "    return x * 2  # a_b ~c",
        "```",
        "",
        "````",
        "has ``` fence",
        "````",
        "",
        "用 `a*b` 或 ``a`b`` 或",
      ].join("\n"),
    );
  });
  it("keeps only http(s) links and makes their destinations safe", async () => {
    expect(
      await markdown(
        '<p><a href="https://example.com/a_(b) c">示例_链接</a>、<a href="javascript:alert(1)">脚本</a>、' +
          '<a href="/relative">相对</a>、<a href="http://example.org/x"><img src="data:,"></a>、<a>无地址</a></p>',
      ),
    ).toBe("[示例\\_链接](https://example.com/a_%28b%29%20c)、脚本、相对、、无地址");
  });
  it("drops media, controls, scripts, styles and display:none content", async () => {
    expect(
      await markdown(
        `<p>前<img src="data:,"><picture><img src="data:,"></picture><svg><text>矢量</text></svg><video>视频</video><audio>音频</audio><canvas>画布</canvas><button>复制</button><script>bad()</script><style>p{}</style><span style="display:none">隐藏</span><span hidden>也隐藏</span>后</p>`,
      ),
    ).toBe("前后");
  });
  it("escapes Markdown specials in text and block starts but leaves Chinese punctuation alone", async () => {
    expect(
      await markdown(
        "<p>DN50~DN300 a*b c_d e\\f [g] h|i `j` &amp;copy; AT&amp;T，。；（）</p>" +
          "<p># 不是标题</p><p>&gt; 不是引用</p><p>- 不是列表</p><p>+86 电话</p><p>1. 不是有序</p>" +
          "<p>2) 也不是</p><p>1、中文序号</p><p>3.5 寸</p><p>&lt;div&gt; 文本</p><p>===</p>" +
          "<p>首行<br>2026. 年</p>",
      ),
    ).toBe(
      [
        "DN50\\~DN300 a\\*b c\\_d e\\\\f \\[g\\] h\\|i \\`j\\` \\&copy; AT&T，。；（）",
        "\\# 不是标题",
        "\\> 不是引用",
        "\\- 不是列表",
        "\\+86 电话",
        "1\\. 不是有序",
        "2\\) 也不是",
        "1、中文序号",
        "3.5 寸",
        "\\<div> 文本",
        "\\===",
        "首行  \n2026\\. 年",
      ].join("\n\n"),
    );
  });
  it("renders emphasis without empty or doubled delimiters", async () => {
    expect(
      await markdown(
        "<p>x<strong> 粗 </strong>y <em>斜</em> <i>体</i><strong> </strong><b></b>" +
          "<strong>相</strong><strong>邻</strong> <strong>外<b>内</b></strong> <strong><em>都</em></strong></p>",
      ),
    ).toBe("x **粗** y *斜* *体* **相邻** **外内** ***都***");
  });
  it("moves CJK edge punctuation outside delimiters only where CommonMark could not flank", async () => {
    expect(
      await markdown(
        "<p>分<strong>推索式（DN30）</strong>和<strong>“品牌”</strong>是<strong>（注）</strong></p>" +
          "<p><strong>（注）</strong>说明：<strong>履带式</strong>。<strong>（</strong>空</p>",
      ),
    ).toBe("分**推索式（DN30**）和“**品牌**”是（**注）**\n\n**（注**）说明：**履带式**。（空");
  });
  it("collapses whitespace, non-breaking and zero-width characters", async () => {
    expect(await markdown("<p>  多个\n\t空白&nbsp;&nbsp;与\u200B零宽\uFEFF字符  </p>")).toBe(
      "多个 空白 与零宽字符",
    );
  });
  it("tolerates an invalid ignore selector by disabling ignoring", async () => {
    expect(await markdown(`<p>排查${CHIP("chip.example")}。</p>`, "span[")).toBe(
      "排查chip.example。",
    );
  });
  it("returns an empty string for empty or media-only input", async () => {
    expect(await markdown("")).toBe("");
    expect(await markdown("<div> <div></div> </div>")).toBe("");
    expect(
      await markdown(
        '<div data-testid="carousel-item"><img src="data:,"></div><div>视频卡片</div>',
        '[data-testid="carousel-item"], [data-testid="carousel-item"] + div',
      ),
    ).toBe("");
  });
  it("reads Markdown and the legacy plain text from visible answer bodies only", async () => {
    await page.setContent(
      `<article id="message"><div class="answer"><h3>标题</h3><p>正文~</p></div>` +
        `<div class="answer" hidden>隐藏</div><div class="answer"><p>第二段</p></div></article>`,
    );
    const message = page.locator("#message");
    const contract = { selectors: { answerBody: ".answer" } };
    expect(await readAnswerMarkdown(message, contract)).toBe("### 标题\n\n正文\\~\n\n第二段");
    expect(await readAnswerPlainText(message, contract)).toBe("标题\n\n正文~\n第二段");
    expect(
      await readAnswerMarkdown(message, {
        selectors: { answerBody: ".answer", answerIgnore: "*" },
      }),
    ).toBeUndefined();
    expect(
      await readAnswerMarkdown(message, { selectors: { answerBody: ".missing" } }),
    ).toBeUndefined();
  });
  it("survives the tsx (esbuild keepNames) transform used by the live collector", async () => {
    // Playwright serializes the function source. Load the module the way `node --import tsx`
    // does and evaluate that exact source in the page: a nested named helper would reference
    // a module-scope `__name` and throw there.
    const root = fileURLToPath(new URL("../../../../", import.meta.url));
    const module = fileURLToPath(new URL("./answer-markdown.ts", import.meta.url));
    const { stdout } = await promisify(execFile)(
      process.execPath,
      [
        "--import",
        "tsx",
        "--input-type=module",
        "-e",
        `const m = await import(${JSON.stringify(module)}); process.stdout.write(m.answerToMarkdown.toString());`,
      ],
      { cwd: root },
    );
    expect(stdout).toContain("function answerToMarkdown");
    expect(stdout).not.toContain("__name");
    await page.setContent(
      `<!doctype html><html lang="zh-CN"><body><div id="root">${realistic}</div></body></html>`,
    );
    expect(
      await page
        .locator("#root")
        .evaluate(
          (element, [source, ignore]) =>
            new Function("root", "ignore", `return (${source})(root, ignore);`)(element, ignore),
          [stdout, LIVE_IGNORE] as const,
        ),
    ).toBe(REALISTIC_MARKDOWN);
  });
});
