import { readFile } from "node:fs/promises";
import { chromium, type Browser, type BrowserContext, type Page } from "playwright";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { fixtureTask } from "../test-fixtures.ts";
import { buildWebQuestion } from "../web-question.ts";
import { readAnswerPlainText } from "./answer-markdown.ts";
import { ConsumerWebAdapter } from "./consumer-web.ts";
import {
  classifyLandingPage,
  DOUBAO_URL,
  DoubaoAdapter,
  normalizeCitationTitle,
  pageContractSchema,
  parseSearchQueries,
  publicCitations,
  type DoubaoPageContract,
} from "./doubao.ts";

const syntheticContract: DoubaoPageContract = {
  version: 2,
  origin: "https://www.doubao.com",
  samplingMode: "default_conversation",
  reviewedAt: new Date().toISOString(),
  verifiedOnAuthorizedPage: true,
  selectors: {
    authenticated: "#authenticated",
    newConversation: "#new",
    composer: "#composer",
    conversationActive: "#conversation-active",
    submit: "#submit",
    assistantMessage: ".assistant",
    answerBody: ".answer",
    generating: "#generating",
    completed: ".complete",
    citations: "a.citation",
    loginRequired: "#login-required",
    regionRestricted: "#region-restricted",
    challengeRequired: "#challenge",
    rateLimited: "#rate-limit",
  },
};
describe("Doubao synthetic page contract (not live selector proof)", () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let html: string;
  let sourcesHtml: string;
  const sourcesContract: DoubaoPageContract = {
    ...syntheticContract,
    selectors: { ...syntheticContract.selectors, sourcesToggle: ".sources-toggle" },
  };
  // Synthetic keyword selector defined by this test only; the live selector is reviewed separately.
  const keywordsContract: DoubaoPageContract = {
    ...sourcesContract,
    selectors: { ...sourcesContract.selectors, searchQueries: ".search-queries" },
  };
  const sourcesPanel = /<section class="sources" hidden>[\s\S]*?<\/section>/u;
  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    html = await readFile(new URL("./fixtures/doubao-synthetic.html", import.meta.url), "utf8");
    sourcesHtml = await readFile(
      new URL("./fixtures/doubao-sources-synthetic.html", import.meta.url),
      "utf8",
    );
  });
  beforeEach(async () => {
    context = await browser.newContext({ serviceWorkers: "block" });
    page = await context.newPage();
  });
  afterEach(async () => {
    await context.close();
  });
  afterAll(async () => {
    await browser.close();
  });
  async function adapter(
    content = html,
    contract: DoubaoPageContract | undefined = syntheticContract,
  ) {
    // Every request intercepted locally. No Doubao request or account is used.
    await page.route("**/*", (route) =>
      route.fulfill({ contentType: "text/html; charset=utf-8", body: content }),
    );
    return new DoubaoAdapter(page, contract, { pollMs: 10, stableMs: 30, timeoutMs: 500 });
  }
  it("submits the original question with its whitespace from the same immutable task each time", async () => {
    const doubao = await adapter();
    expect(await doubao.probe(new AbortController().signal)).toEqual({ status: "ready" });
    const task = Object.freeze({
      ...fixtureTask(),
      questionText: "  原文  中文\nwithout rewrite\n ",
    });
    const original = { ...task };
    // Nothing is put before the question and its whitespace is not rewritten.
    const expected = task.questionText;
    // Observe the actual composer at the submit click, before any answer extraction.
    await page.locator("#submit").evaluate((button) => {
      button.addEventListener("click", () => {
        const composer = button.ownerDocument.querySelector("#composer") as unknown as {
          value: string;
        };
        button.setAttribute("data-sent-prompt", composer.value);
      });
    });
    for (let attempt = 0; attempt < 2; attempt += 1) {
      const result = await doubao.collect(task, new AbortController().signal);
      expect(await page.locator("#submit").getAttribute("data-sent-prompt")).toBe(expected);
      expect(task).toEqual(original);
      expect(result.answerText).toBe("Synthetic answer for fixture testing only.");
      expect(result.citations).toEqual(["https://example.com/source"]);
    }
  });
  // Live Doubao re-renders the composer as one <p> per line, an empty <p> for a blank line.
  const paragraphEditor = (dropBlankLines = false) =>
    html
      .replace(
        '<textarea id="composer"></textarea>',
        `<div id="composer" contenteditable="true"></div><script>
          const editor = document.querySelector("#composer");
          // Like editor frameworks, take over the native insertion and keep the model.
          editor.addEventListener("beforeinput", (event) => {
            if (event.inputType !== "insertText" || event.data === null) return;
            event.preventDefault();
            const lines = event.data.split("\\n")
              .filter((line) => ${dropBlankLines ? 'line !== ""' : "true"});
            editor.replaceChildren(...lines.map((line) => {
              const p = document.createElement("p");
              if (line) p.textContent = line; else p.append(document.createElement("br"));
              return p;
            }));
          });
        </script>`,
      )
      .replace(
        "document.querySelector('#composer').value = ''",
        "document.querySelector('#composer').replaceChildren()",
      );
  async function sentParagraphs(doubao: DoubaoAdapter) {
    expect(await doubao.probe(new AbortController().signal)).toEqual({ status: "ready" });
    await page
      .locator("#submit")
      .evaluate((button) =>
        button.addEventListener("click", () =>
          button.setAttribute(
            "data-sent-lines",
            JSON.stringify(
              Array.from(
                button.ownerDocument.querySelectorAll("#composer > p"),
                (p) => (p as { textContent: string | null }).textContent,
              ),
            ),
          ),
        ),
      );
    return () => page.locator("#submit").getAttribute("data-sent-lines");
  }
  // A question that itself holds a blank line: the editor keeps one paragraph per line.
  const twoParagraphs = () =>
    Object.freeze({ ...fixtureTask(), questionText: "合成问题第一段\n\n什么设备适合运维团队？" });
  it("submits a question with a blank line in a paragraph-per-line editor", async () => {
    const doubao = await adapter(paragraphEditor());
    const task = twoParagraphs();
    const sent = await sentParagraphs(doubao);
    const result = await doubao.collect(task, new AbortController().signal);
    expect(JSON.parse((await sent()) ?? "null")).toEqual([
      "合成问题第一段",
      "",
      "什么设备适合运维团队？",
    ]);
    expect(result.answerText).toBe("Synthetic answer for fixture testing only.");
  });
  it("does not submit when a paragraph editor drops the blank line", async () => {
    const doubao = await adapter(paragraphEditor(true));
    const sent = await sentParagraphs(doubao);
    await expect(
      doubao.collect(twoParagraphs(), new AbortController().signal),
    ).rejects.toMatchObject({ code: "page_contract_changed" });
    expect(await sent()).toBeNull();
  });
  it("allows an actual empty citation set", async () => {
    const doubao = await adapter(html.replaceAll('class="citation"', 'class="unrelated"'));
    await doubao.probe(new AbortController().signal);
    expect((await doubao.collect(fixtureTask(), new AbortController().signal)).citations).toEqual(
      [],
    );
  });
  it.each(["original question", "full outgoing message"])(
    "rejects the %s in a completed user-message renderer matched by a broad selector",
    async (echo) => {
      const task = fixtureTask();
      const text =
        echo === "original question" ? task.questionText : buildWebQuestion(task.questionText);
      const doubao = await adapter(
        html
          .replace('class="assistant"', 'class="user"')
          .replace(
            '<div class="answer">Synthetic answer for fixture testing only.</div>',
            `<div class="answer" style="white-space: pre-wrap"> ${text} </div>`,
          ),
        {
          ...syntheticContract,
          selectors: { ...syntheticContract.selectors, assistantMessage: "article:has(.answer)" },
        },
      );
      await doubao.probe(new AbortController().signal);
      await expect(doubao.collect(task, new AbortController().signal)).rejects.toMatchObject({
        code: "page_contract_changed",
      });
    },
  );
  it("keeps role-specific selection when user and assistant share the renderer", async () => {
    const task = fixtureTask();
    const doubao = await adapter(
      html.replace(
        '<template id="answer-template">',
        `<template id="answer-template"><article class="user"><div class="answer">${task.questionText}</div><span class="complete">Complete</span></article>`,
      ),
    );
    await doubao.probe(new AbortController().signal);
    const result = await doubao.collect(task, new AbortController().signal);
    expect(result.answerText).toBe("Synthetic answer for fixture testing only.");
    expect(result.answerText).not.toBe(task.questionText);
  });
  it("expands sources once, waits for all references and excludes source snippets from the body", async () => {
    const doubao = await adapter(sourcesHtml, sourcesContract);
    expect(await doubao.probe(new AbortController().signal)).toEqual({ status: "ready" });
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result.answerText).toBe("Synthetic answer body only.");
    expect(result.citations).toEqual(["https://example.com/first", "https://example.com/second"]);
    expect(await page.locator(".sources-toggle").getAttribute("data-clicks")).toBe("1");
  });
  it("reads aligned source titles without numbering and keywords matching the caption count", async () => {
    const doubao = await adapter(sourcesHtml, keywordsContract);
    expect(await doubao.probe(new AbortController().signal)).toEqual({ status: "ready" });
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result.answerText).toBe("Synthetic answer body only.");
    expect(result.citations).toEqual(["https://example.com/first", "https://example.com/second"]);
    expect(result.citationTitles).toEqual([
      "Synthetic source snippet one",
      "Synthetic source snippet two",
    ]);
    expect(result.searchQueries).toEqual(["合成关键词 一", "合成关键词 二"]);
    expect(await page.locator(".sources-toggle").getAttribute("data-clicks")).toBe("1");
  });
  // Same ignore selector string as the reviewed live contract value; the DOM is synthetic.
  const markdownContract: DoubaoPageContract = {
    ...keywordsContract,
    selectors: {
      ...keywordsContract.selectors,
      answerIgnore:
        '[data-testid="carousel-item"], [data-testid="mdbox_image"], [data-testid="video-box-bg-image"], span[class*="spacing-"] + span',
    },
  };
  const richAnswer =
    '<div class="answer"><div><h3>✅ 合成标题</h3></div>' +
    '<div><div>正文<strong>加粗（注）</strong>和 DN50~DN300<span class="spacing-a"></span><span class="container-b">example.com</span><span class="spacing-a !w-2"></span>。</div></div>' +
    '<div data-testid="carousel-item"><div data-testid="mdbox_image"><img src="data:,"></div></div>' +
    "<div><ul><li>要点 *一*</li></ul></div></div>";
  const plainAnswerBody = '<div class="answer">Synthetic answer body only.</div>';
  it("returns the answer body as Markdown and keeps sources, titles and keywords", async () => {
    const doubao = await adapter(
      sourcesHtml.replace(plainAnswerBody, richAnswer),
      markdownContract,
    );
    expect(await doubao.probe(new AbortController().signal)).toEqual({ status: "ready" });
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result.answerText).toBe(
      "### ✅ 合成标题\n\n正文**加粗（注**）和 DN50\\~DN300。\n\n- 要点 \\*一\\*",
    );
    expect(result.citations).toEqual(["https://example.com/first", "https://example.com/second"]);
    expect(result.citationTitles).toEqual([
      "Synthetic source snippet one",
      "Synthetic source snippet two",
    ]);
    expect(result.searchQueries).toEqual(["合成关键词 一", "合成关键词 二"]);
    expect(await page.locator(".sources-toggle").getAttribute("data-clicks")).toBe("1");
  });
  it("falls back to the legacy plain innerText answer when the Markdown is empty", async () => {
    const contract = pageContractSchema.parse({
      ...markdownContract,
      selectors: { ...markdownContract.selectors, answerIgnore: ".answer *" },
    });
    const doubao = await adapter(
      sourcesHtml.replace(
        plainAnswerBody,
        '<div class="answer"><span>Plain ~ *fallback*</span></div>',
      ),
      contract,
    );
    await doubao.probe(new AbortController().signal);
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result.answerText).toBe("Plain ~ *fallback*");
    expect(await readAnswerPlainText(page.locator(".assistant"), contract)).toBe(result.answerText);
    expect(result.citations).toEqual(["https://example.com/first", "https://example.com/second"]);
  });
  // The shared hook with Doubao's constructor arguments; only the formatter is replaced.
  async function formattedAdapter(formatter: () => Promise<string | undefined>) {
    await page.route("**/*", (route) =>
      route.fulfill({ contentType: "text/html; charset=utf-8", body: html }),
    );
    return new ConsumerWebAdapter(
      page,
      syntheticContract,
      "doubao",
      DOUBAO_URL,
      ["www.doubao.com", "doubao.com"],
      { pollMs: 10, stableMs: 30, timeoutMs: 500 },
      undefined,
      true,
      5_000,
      undefined,
      undefined,
      false,
      true,
      formatter,
    );
  }
  it.each([
    ["throws", () => Promise.reject(new Error("synthetic formatter failure"))],
    ["returns whitespace", () => Promise.resolve("  \n ")],
    ["returns nothing", () => Promise.resolve(undefined)],
  ])("keeps the plain answer when the formatter %s", async (_label, formatter) => {
    const adapter = await formattedAdapter(formatter);
    await adapter.probe(new AbortController().signal);
    const result = await adapter.collect(fixtureTask(), new AbortController().signal);
    expect(result.answerText).toBe("Synthetic answer for fixture testing only.");
    expect(result.citations).toEqual(["https://example.com/source"]);
  });
  it.each(["original question", "full outgoing message"])(
    "rejects the %s returned by the answer formatter",
    async (echo) => {
      const task = fixtureTask();
      const text =
        echo === "original question" ? task.questionText : buildWebQuestion(task.questionText);
      const adapter = await formattedAdapter(() => Promise.resolve(` ${text} `));
      await adapter.probe(new AbortController().signal);
      await expect(adapter.collect(task, new AbortController().signal)).rejects.toMatchObject({
        code: "page_contract_changed",
      });
    },
  );
  it("accepts straight-quoted keywords as a fallback", async () => {
    const doubao = await adapter(
      sourcesHtml.replace("“合成关键词 一”、“合成关键词 二”", '"合成关键词 一", "合成关键词 二"'),
      keywordsContract,
    );
    await doubao.probe(new AbortController().signal);
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result.searchQueries).toEqual(["合成关键词 一", "合成关键词 二"]);
  });
  // Same selector string as the reviewed live contract; the DOM around it is synthetic.
  const liveKeywordSelector =
    '[data-plugin-identifier*="search_query_result_block"] div:text-matches("^“[^”]+”")';
  const livePanel = (keywordLine: string) =>
    sourcesHtml
      .replace(
        '<div id="messages"></div>',
        // Decoy outside the assistant message: must never be read.
        '<div data-plugin-identifier="decoy_search_query_result_block"><div>“外部关键词”</div></div><div id="messages"></div>',
      )
      .replace(
        '<section class="sources" hidden>',
        '<section class="sources" data-plugin-identifier="synthetic_search_query_result_block" hidden>',
      )
      .replace('<div class="search-queries">“合成关键词 一”、“合成关键词 二”</div>', keywordLine);
  const liveContract = () =>
    pageContractSchema.parse({
      ...sourcesContract,
      selectors: { ...sourcesContract.selectors, searchQueries: liveKeywordSelector },
    });
  it("evaluates the live :text-matches keyword selector inside the assistant message", async () => {
    const doubao = await adapter(
      livePanel("<div>“合成关键词 一”、“合成关键词 二”</div>"),
      liveContract(),
    );
    await doubao.probe(new AbortController().signal);
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result.searchQueries).toEqual(["合成关键词 一", "合成关键词 二"]);
    expect(result.citationTitles).toEqual([
      "Synthetic source snippet one",
      "Synthetic source snippet two",
    ]);
  });
  it("omits keywords (keeps the answer) when the live selector finds no own-text line", async () => {
    // :text-matches only matches the smallest element; per-keyword child elements leave no div.
    const doubao = await adapter(
      livePanel("<div><span>“合成关键词 一”</span>、<span>“合成关键词 二”</span></div>"),
      liveContract(),
    );
    await doubao.probe(new AbortController().signal);
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result).not.toHaveProperty("searchQueries");
    expect(result.answerText).toBe("Synthetic answer body only.");
    expect(result.citations).toEqual(["https://example.com/first", "https://example.com/second"]);
  });
  it.each([
    ["keyword count mismatch", "搜索 2 个关键词", "搜索 3 个关键词"],
    ["caption without a keyword count", "搜索 2 个关键词，", ""],
    ["keyword line without quoted segments", "“合成关键词 一”、“合成关键词 二”", "合成关键词"],
    [
      "hidden keyword line",
      'class="search-queries"',
      'class="search-queries" style="display: none"',
    ],
    ["missing keyword line", 'class="search-queries"', 'class="other-line"'],
  ])("omits keywords but keeps the answer, URLs and titles: %s", async (_label, from, to) => {
    const doubao = await adapter(sourcesHtml.replace(from, to), keywordsContract);
    await doubao.probe(new AbortController().signal);
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result).not.toHaveProperty("searchQueries");
    expect(result.citations).toEqual(["https://example.com/first", "https://example.com/second"]);
    expect(result.citationTitles).toEqual([
      "Synthetic source snippet one",
      "Synthetic source snippet two",
    ]);
  });
  it("omits keywords when the contract has no keyword selector", async () => {
    const doubao = await adapter(sourcesHtml, sourcesContract);
    await doubao.probe(new AbortController().signal);
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result).not.toHaveProperty("searchQueries");
    expect(result.citationTitles).toEqual([
      "Synthetic source snippet one",
      "Synthetic source snippet two",
    ]);
  });
  it("aligns titles with filtered, deduplicated URLs and keeps the first readable title", async () => {
    const box = '<span style="display: inline-block; width: 8px; height: 8px"></span>';
    const panel = `<section class="sources" hidden>
      <a class="citation" href="https://www.doubao.com/chat/internal">0. Internal link</a>
      <a class="citation" href="https://example.com/first">${box}</a>
      <a class="citation" href="https://example.com/first">1.   Readable
        first   title</a>
      <a class="citation" href="https://example.com/third">${box}</a>
      <a class="citation" href="https://example.com/second" hidden>2. ${"长".repeat(600)}</a>
    </section>`;
    const doubao = await adapter(
      sourcesHtml.replace(sourcesPanel, panel).replace("参考 2 篇资料", "参考 3 篇资料"),
      keywordsContract,
    );
    await doubao.probe(new AbortController().signal);
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result.citations).toEqual([
      "https://example.com/first",
      "https://example.com/third",
      "https://example.com/second",
    ]);
    expect(result.citationTitles).toEqual(["Readable first title", null, "长".repeat(512)]);
    expect(result).not.toHaveProperty("searchQueries");
  });
  it("returns aligned titles for inline citations when no source toggle is rendered", async () => {
    const doubao = await adapter(html, keywordsContract);
    await doubao.probe(new AbortController().signal);
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result.citations).toEqual(["https://example.com/source"]);
    expect(result.citationTitles).toEqual(["Source"]);
    expect(result).not.toHaveProperty("searchQueries");
  });
  it("preserves empty citations when an optional source toggle is absent", async () => {
    const doubao = await adapter(
      html.replaceAll('class="citation"', 'class="unrelated"'),
      sourcesContract,
    );
    await doubao.probe(new AbortController().signal);
    expect((await doubao.collect(fixtureTask(), new AbortController().signal)).citations).toEqual(
      [],
    );
  });
  it("does not expand sources before positive completion", async () => {
    const doubao = await adapter(
      sourcesHtml.replace('class="complete"', 'class="partial"'),
      sourcesContract,
    );
    await doubao.probe(new AbortController().signal);
    await expect(doubao.collect(fixtureTask(), new AbortController().signal)).rejects.toMatchObject(
      {
        code: "collection_timeout",
      },
    );
    expect(await page.locator(".sources-toggle").getAttribute("data-clicks")).toBeNull();
  });
  it("preserves inline citations when the optional source toggle is hidden", async () => {
    const doubao = await adapter(
      html.replace(
        '<div class="answer">',
        '<button class="sources-toggle" hidden>Unused</button><div class="answer">',
      ),
      sourcesContract,
    );
    await doubao.probe(new AbortController().signal);
    expect((await doubao.collect(fixtureTask(), new AbortController().signal)).citations).toEqual([
      "https://example.com/source",
    ]);
  });
  it.each(["参考若干篇资料", "参考 1 篇资料，参考 2 篇资料", "参考 101 篇资料", "参考 0 篇资料"])(
    "rejects an ambiguous or malformed reference caption: %s",
    async (caption) => {
      const doubao = await adapter(sourcesHtml.replace("参考 2 篇资料", caption), sourcesContract);
      await doubao.probe(new AbortController().signal);
      await expect(
        doubao.collect(fixtureTask(), new AbortController().signal),
      ).rejects.toMatchObject({ code: "page_contract_changed" });
      expect(await page.locator(".sources-toggle").getAttribute("data-clicks")).toBeNull();
    },
  );
  it("accepts fewer rendered references than the caption claims (Doubao caps the inline list)", async () => {
    const doubao = await adapter(
      sourcesHtml.replace('href="https://example.com/second"', 'href="https://example.com/first"'),
      sourcesContract,
    );
    await doubao.probe(new AbortController().signal);
    const result = await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(result.citations).toEqual(["https://example.com/first"]);
    expect(await page.locator(".sources-toggle").getAttribute("data-clicks")).toBe("1");
  });
  it.each([
    ["excess references", "参考 2 篇资料", "参考 1 篇资料"],
    ["never opened panel", "panel.hidden = false", "panel.hidden = true"],
  ])("rejects a partial source panel: %s", async (_label, from, to) => {
    const doubao = await adapter(sourcesHtml.replace(from, to), sourcesContract);
    await doubao.probe(new AbortController().signal);
    await expect(doubao.collect(fixtureTask(), new AbortController().signal)).rejects.toMatchObject(
      { code: "page_contract_changed" },
    );
    expect(await page.locator(".sources-toggle").getAttribute("data-clicks")).toBe("1");
  });
  it("rejects multiple matching source toggles without clicking either", async () => {
    const doubao = await adapter(
      sourcesHtml.replace(
        "</article>",
        '<button class="sources-toggle">参考 2 篇资料</button></article>',
      ),
      sourcesContract,
    );
    await doubao.probe(new AbortController().signal);
    await expect(doubao.collect(fixtureTask(), new AbortController().signal)).rejects.toMatchObject(
      { code: "page_contract_changed" },
    );
    expect(await page.locator(".sources-toggle").first().getAttribute("data-clicks")).toBeNull();
  });
  it("never accepts partial output without a completion indicator", async () => {
    const doubao = await adapter(html.replace('class="complete"', 'class="partial"'));
    await doubao.probe(new AbortController().signal);
    await expect(doubao.collect(fixtureTask(), new AbortController().signal)).rejects.toMatchObject(
      { code: "collection_timeout" },
    );
  });
  it("fails closed when the default conversation marker is missing", async () => {
    expect(
      await (
        await adapter(html.replace('id="conversation-active"', 'id="changed"'))
      ).probe(new AbortController().signal),
    ).toEqual({ status: "page_contract_changed" });
  });
  it("fails closed when the reviewed conversation selector does not match", async () => {
    const doubao = await adapter(html, {
      ...syntheticContract,
      selectors: { ...syntheticContract.selectors, conversationActive: "#different-mode" },
    });
    expect(await doubao.probe(new AbortController().signal)).toEqual({
      status: "page_contract_changed",
    });
  });
  it("rechecks the conversation marker after starting a new chat before submitting", async () => {
    const doubao = await adapter();
    expect(await doubao.probe(new AbortController().signal)).toEqual({ status: "ready" });
    await page.locator("#new").evaluate((button) => {
      button.addEventListener("click", () =>
        button.ownerDocument.querySelector("#conversation-active")?.remove(),
      );
    });
    await expect(doubao.collect(fixtureTask(), new AbortController().signal)).rejects.toMatchObject(
      {
        code: "page_contract_changed",
      },
    );
    expect(await page.locator("#composer").inputValue()).toBe("");
    expect(await page.locator(".assistant").count()).toBe(0);
  });
  it.each([
    ["您所在地区暂不支持", "region_restricted"],
    ["请先登录", "login_required"],
    ["请完成安全验证", "challenge_required"],
    ["请求过于频繁", "rate_limited"],
  ])("classifies a blocked landing page: %s", async (text, status) => {
    expect(
      await (await adapter(`<body>${text}</body>`)).probe(new AbortController().signal),
    ).toEqual({ status });
  });
  it("has no assumed production selectors when a manifest is missing", async () => {
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    expect(await new DoubaoAdapter(page).probe(new AbortController().signal)).toEqual({
      status: "page_contract_unverified",
    });
  });
  it("waits boundedly for known ready markers to hydrate", async () => {
    const doubao = await adapter(
      html
        .replace('id="authenticated"', 'id="authenticated" hidden')
        .replace(
          "</body>",
          '<script>setTimeout(() => document.querySelector("#authenticated").hidden = false, 60)</script></body>',
        ),
    );
    expect(await doubao.probe(new AbortController().signal)).toEqual({ status: "ready" });
    expect(await page.locator("#composer").inputValue()).toBe("");
  });
  it("classifies a visible challenge iframe without reading its content", async () => {
    const doubao = await adapter(
      '<body><div id="captcha_container"><iframe srcdoc="synthetic iframe only"></iframe></div></body>',
    );
    expect(await doubao.probe(new AbortController().signal)).toEqual({
      status: "challenge_required",
    });
  });
  it("stops hydration waiting when a challenge iframe appears", async () => {
    const doubao = await adapter(
      html
        .replace('id="authenticated"', 'id="missing"')
        .replace(
          "</body>",
          '<div id="captcha_container" hidden><iframe srcdoc="synthetic iframe only"></iframe></div><script>setTimeout(() => document.querySelector("#captcha_container").hidden = false, 60)</script></body>',
        ),
    );
    expect(await doubao.probe(new AbortController().signal)).toEqual({
      status: "challenge_required",
    });
  });
  it("does not send a prompt when a challenge appears after starting a new conversation", async () => {
    const doubao = await adapter(
      html.replace(
        "</body>",
        '<div id="captcha_container" hidden><iframe srcdoc="synthetic iframe only"></iframe></div></body>',
      ),
    );
    expect(await doubao.probe(new AbortController().signal)).toEqual({ status: "ready" });
    await page.locator("#new").evaluate((button) => {
      button.addEventListener("click", () => {
        const challenge = button.ownerDocument.getElementById("captcha_container");
        if (challenge) challenge.hidden = false;
      });
    });
    await expect(doubao.collect(fixtureTask(), new AbortController().signal)).rejects.toMatchObject(
      { code: "challenge_required" },
    );
    expect(await page.locator("#composer").inputValue()).toBe("");
    expect(await page.locator(".assistant").count()).toBe(0);
  });
  it("does not block on a hidden challenge iframe", async () => {
    const doubao = await adapter(
      html.replace(
        "</body>",
        '<div id="captcha_container" hidden><iframe srcdoc="synthetic iframe only"></iframe></div></body>',
      ),
    );
    expect(await doubao.probe(new AbortController().signal)).toEqual({ status: "ready" });
  });
  it("stops collection when a challenge iframe appears after submit", async () => {
    const doubao = await adapter(
      html.replace(
        '<article class="assistant">',
        '<div id="captcha_container"><iframe srcdoc="synthetic iframe only"></iframe></div><article class="assistant">',
      ),
    );
    expect(await doubao.probe(new AbortController().signal)).toEqual({ status: "ready" });
    await expect(doubao.collect(fixtureTask(), new AbortController().signal)).rejects.toMatchObject(
      { code: "challenge_required" },
    );
  });
});
describe("explicit default-conversation manifest", () => {
  it("accepts version 2 with the approved sampling mode and conversation marker", () => {
    expect(pageContractSchema.parse(syntheticContract)).toEqual(syntheticContract);
  });
  it("accepts an optional reviewed sources toggle and rejects invalid selectors", () => {
    expect(
      pageContractSchema.safeParse({
        ...syntheticContract,
        selectors: { ...syntheticContract.selectors, sourcesToggle: ".sources-toggle" },
      }).success,
    ).toBe(true);
    expect(
      pageContractSchema.safeParse({
        ...syntheticContract,
        selectors: { ...syntheticContract.selectors, searchQueries: ".search-queries" },
      }).success,
    ).toBe(true);
    expect(
      pageContractSchema.safeParse({
        ...syntheticContract,
        selectors: { ...syntheticContract.selectors, searchQueries: "" },
      }).success,
    ).toBe(false);
    expect(
      pageContractSchema.safeParse({
        ...syntheticContract,
        selectors: {
          ...syntheticContract.selectors,
          answerIgnore: '[data-testid="carousel-item"]',
        },
      }).success,
    ).toBe(true);
    for (const answerIgnore of ["", "x".repeat(501)]) {
      expect(
        pageContractSchema.safeParse({
          ...syntheticContract,
          selectors: { ...syntheticContract.selectors, answerIgnore },
        }).success,
      ).toBe(false);
    }
    expect(
      pageContractSchema.safeParse({
        ...syntheticContract,
        selectors: { ...syntheticContract.selectors, sourcesToggle: "" },
      }).success,
    ).toBe(false);
  });
  it("rejects legacy version 1 and searchActive instead of silently changing semantics", () => {
    const selectors = { ...syntheticContract.selectors };
    Reflect.deleteProperty(selectors, "conversationActive");
    const legacy = {
      ...syntheticContract,
      version: 1,
      selectors: { ...selectors, searchActive: "#search-active" },
    };
    Reflect.deleteProperty(legacy, "samplingMode");
    expect(pageContractSchema.safeParse(legacy).success).toBe(false);
    expect(pageContractSchema.safeParse({ ...syntheticContract, version: 1 }).success).toBe(false);
    expect(
      pageContractSchema.safeParse({
        ...syntheticContract,
        selectors: { ...syntheticContract.selectors, searchActive: "#search-active" },
      }).success,
    ).toBe(false);
  });
  it("rejects missing or unsupported sampling modes and missing conversation selectors", () => {
    for (const samplingMode of [undefined, "search", "deep_research"]) {
      expect(pageContractSchema.safeParse({ ...syntheticContract, samplingMode }).success).toBe(
        false,
      );
    }
    expect(
      pageContractSchema.safeParse({
        ...syntheticContract,
        selectors: { ...syntheticContract.selectors, conversationActive: undefined },
      }).success,
    ).toBe(false);
  });
});
describe("Doubao source title and keyword parsing", () => {
  it.each([
    ["1. 管道内窥镜选购指南", "管道内窥镜选购指南"],
    ["12.管道内窥镜选购指南", "管道内窥镜选购指南"],
    ["  多个   空白\n 换行 ", "多个 空白 换行"],
    ["3.5 寸管道内窥镜评测", "3.5 寸管道内窥镜评测"],
    ["2024 年行业报告", "2024 年行业报告"],
    ["", null],
    ["  1.  ", null],
  ])("normalizes a source title: %j", (text, title) => {
    expect(normalizeCitationTitle(text)).toBe(title);
  });
  it("truncates titles to 512 code units without splitting a surrogate pair", () => {
    expect(normalizeCitationTitle("a".repeat(600))).toBe("a".repeat(512));
    expect(normalizeCitationTitle(`${"a".repeat(511)}😀b`)).toBe("a".repeat(511));
  });
  it("returns keywords only when the parsed count equals the claimed count", () => {
    expect(parseSearchQueries("“管道内窥镜 品牌”、“ 工业内窥镜 推荐 ”", 2)).toEqual([
      "管道内窥镜 品牌",
      "工业内窥镜 推荐",
    ]);
    expect(parseSearchQueries('"a", "b"', 2)).toEqual(["a", "b"]);
    expect(parseSearchQueries("“a”、“b”", 3)).toBeUndefined();
    expect(parseSearchQueries("“a”、“ ”", 2)).toBeUndefined();
    expect(parseSearchQueries("no quotes", 1)).toBeUndefined();
    expect(parseSearchQueries("", 0)).toBeUndefined();
    expect(parseSearchQueries(`“${"词".repeat(501)}”`, 1)).toBeUndefined();
    const many = Array.from({ length: 33 }, (_, i) => `“k${i}”`).join("、");
    expect(parseSearchQueries(many, 33)).toBeUndefined();
  });
});
describe("safe page classification and citations", () => {
  it("recognizes region-ban redirects without exposing their full URLs", () => {
    expect(classifyLandingPage("", "https://www.doubao.com/region-ban?session=secret")).toBe(
      "region_restricted",
    );
  });
  it("rejects credentials, local addresses, internal links and non-web schemes", () => {
    expect(
      publicCitations([
        "javascript:alert(1)",
        "https://u:p@example.com",
        "http://127.0.0.1",
        "http://[::1]",
        "http://server.local",
        "https://www.doubao.com/chat/x",
        "https://example.invalid/",
        "https://example.com/real",
        "https://example.com/real",
      ]),
    ).toEqual(["https://example.com/real"]);
  });
});
