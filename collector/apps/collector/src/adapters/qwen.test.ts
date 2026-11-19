import { readFile } from "node:fs/promises";
import { chromium, type Browser, type BrowserContext, type Page } from "playwright";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { loadSessionConfig } from "@aivc/config";
import { fixtureTask } from "../test-fixtures.ts";
import { buildWebQuestion } from "../web-question.ts";
import { QwenAdapter, qwenPageContractSchema, type QwenPageContract } from "./qwen.ts";
import { createRealAdapter, realPlatformUrl } from "./real-platform.ts";

const sourceToggleMarkup = /<div\s+class="reference-wrap-iEjeb3"[\s\S]*?<\/div>/;
const sourceCardMarkup = /<div\s+class="source-item-mqZd08"[\s\S]*?<\/div>/g;
const firstSourceData = '{"url":"https://example.com/source","title":" First source title "}';

const contract: QwenPageContract = {
  version: 2,
  origin: "https://www.qianwen.com",
  samplingMode: "default_conversation",
  reviewedAt: new Date().toISOString(),
  verifiedOnAuthorizedPage: true,
  selectors: {
    authenticated: "#authenticated",
    newConversation: "#new",
    composer: "#composer",
    conversationActive: "#conversation-active",
    submit: "#submit",
    assistantMessage: '[class*="message-select-wrapper-answer"]',
    answerBody: ".qk-markdown",
    generating: "#generating",
    completed: ".qk-markdown-complete",
    citations: ".citation",
    sourcesToggle: ".reference-wrap-iEjeb3",
    sourceCards: ".source-item-mqZd08",
    loginRequired: "#login",
    regionRestricted: "#region",
    challengeRequired: "#challenge",
    rateLimited: "#rate-limit",
  },
};

describe("Qwen synthetic consumer-Web contract", () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let html: string;
  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    html = await readFile(new URL("./fixtures/qwen-synthetic.html", import.meta.url), "utf8");
  });
  beforeEach(async () => {
    context = await browser.newContext({ serviceWorkers: "block" });
    page = await context.newPage();
  });
  afterEach(async () => await context.close());
  afterAll(async () => await browser.close());

  async function readyAdapter(body = html) {
    await page.route("**/*", (route) =>
      route.fulfill({ contentType: "text/html; charset=utf-8", body }),
    );
    const adapter = new QwenAdapter(page, contract, { pollMs: 10, stableMs: 30, timeoutMs: 1_000 });
    expect(await adapter.probe(new AbortController().signal)).toEqual({ status: "ready" });
    return adapter;
  }

  const task = () => ({
    ...fixtureTask(),
    platformCode: "qwen" as const,
    questionText: "中文  原文\nsecond line",
  });

  it("routes Qwen without falling back and validates its reviewed origin", async () => {
    expect(qwenPageContractSchema.parse(contract).origin).toBe("https://www.qianwen.com");
    const config = loadSessionConfig({
      COLLECTOR_PROFILE_DIRECTORY: "/aivc/qwen",
      COLLECTOR_SUPPORTED_PLATFORMS: "qwen",
    });
    expect(realPlatformUrl(config)).toBe("https://www.qianwen.com/");
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const adapter = await createRealAdapter(page, config);
    expect(adapter.platformCode).toBe("qwen");
    expect(await adapter.probe(new AbortController().signal)).toEqual({
      status: "page_contract_unverified",
    });
  });

  it("sends the original question and aligns source titles after canonical URL deduplication", async () => {
    const adapter = await readyAdapter();
    const result = await adapter.collect(task(), new AbortController().signal);
    expect(await page.locator("#sent-prompt").textContent()).toBe(
      buildWebQuestion(task().questionText),
    );
    expect(result.answerText).toBe("Synthetic Qwen answer for fixture testing only.");
    expect(result.citations).toEqual([
      "https://example.com/inline-only",
      "https://example.com/source",
      "https://second.example.com/source",
    ]);
    expect(result.citationTitles).toEqual([null, "First source title", "Second source title"]);
  });

  it.each(["\uFEFF向千问提问", "\uFEFF\n\n向千问提问"])(
    "accepts the reviewed empty composer placeholder with whitespace: %j",
    async (placeholder) => {
      const adapter = await readyAdapter();
      await page.locator("#new").evaluate((element, value) => {
        element.setAttribute(
          "onclick",
          `${element.getAttribute("onclick")}; document.querySelector('#composer').value = ${JSON.stringify(value)}`,
        );
      }, placeholder);
      const result = await adapter.collect(task(), new AbortController().signal);
      expect(await page.locator("#sent-prompt").textContent()).toBe(
        buildWebQuestion(task().questionText),
      );
      expect(result.answerText).toBe("Synthetic Qwen answer for fixture testing only.");
    },
  );

  it("rejects other non-empty composer content before sending", async () => {
    const adapter = await readyAdapter();
    await page.locator("#new").evaluate((element) => {
      element.setAttribute(
        "onclick",
        `${element.getAttribute("onclick")}; document.querySelector('#composer').value = 'Existing unsent draft'`,
      );
    });
    await expect(adapter.collect(task(), new AbortController().signal)).rejects.toMatchObject({
      code: "page_contract_changed",
    });
    expect(await page.locator("#sent-prompt").textContent()).toBe("");
  });

  it.each([false, true])(
    "preserves inline-only results when the source toggle is absent (empty=%s)",
    async (empty) => {
      let body = html.replace(sourceToggleMarkup, "");
      if (empty) body = body.replace(/<a class="citation"[\s\S]*?<\/a>/g, "");
      const adapter = await readyAdapter(body);
      const result = await adapter.collect(task(), new AbortController().signal);
      expect(result.citations).toEqual(empty ? [] : ["https://example.com/inline-only"]);
      expect(result.citationTitles).toBeUndefined();
    },
  );

  it("keeps valid URLs when titles are malformed or oversized and uses the first readable duplicate title", async () => {
    const adapter = await readyAdapter(
      html
        .replace(firstSourceData, JSON.stringify({ url: "https://example.com/source", title: {} }))
        .replace('"title":"Second source title"', `"title":"${"长".repeat(513)}"`),
    );
    const result = await adapter.collect(task(), new AbortController().signal);
    expect(result.citations).toHaveLength(3);
    expect(result.citationTitles).toEqual([null, "Later duplicate title", null]);
  });

  it.each(["0篇来源", "101篇来源", "来源加载中"])(
    "rejects an invalid declared count: %s",
    async (caption) => {
      const adapter = await readyAdapter(html.replace("3篇来源", caption));
      await expect(adapter.collect(task(), new AbortController().signal)).rejects.toMatchObject({
        code: "page_contract_changed",
      });
    },
  );

  it.each(["duplicate", "hidden"])("rejects a %s source toggle", async (state) => {
    const body = html.replace(sourceToggleMarkup, (markup) =>
      state === "duplicate" ? markup + markup : markup.replace("<div", "<div hidden"),
    );
    const adapter = await readyAdapter(body);
    await expect(adapter.collect(task(), new AbortController().signal)).rejects.toMatchObject({
      code: "page_contract_changed",
    });
  });

  it("rejects pre-existing visible source cards instead of reading an older panel", async () => {
    const card = html.match(sourceCardMarkup)![0]!;
    const adapter = await readyAdapter(
      html.replace('<div id="sources"></div>', `<div id="sources">${card}</div>`),
    );
    await expect(adapter.collect(task(), new AbortController().signal)).rejects.toMatchObject({
      code: "page_contract_changed",
    });
  });

  it.each([
    ["malformed JSON", "{"],
    ["missing URL", '{"title":"Missing URL"}'],
    ["non-string URL", '{"url":17,"title":"Invalid URL"}'],
    ["non-public URL", '{"url":"https://www.qianwen.com/internal","title":"Internal URL"}'],
    ["invalid URL", '{"url":"not-a-url","title":"Invalid URL"}'],
  ])(
    "rejects a card with %s instead of silently dropping a declared source",
    async (_name, raw) => {
      const adapter = await readyAdapter(html.replace(firstSourceData, raw));
      await expect(adapter.collect(task(), new AbortController().signal)).rejects.toMatchObject({
        code: "page_contract_changed",
      });
    },
  );

  it.each(["incomplete", "excess"])("rejects an %s source panel", async (state) => {
    const cards = html.match(sourceCardMarkup)!;
    const body =
      state === "incomplete"
        ? html.replace(cards[2]!, "")
        : html.replace(cards[0]!, cards[0]! + cards[0]!);
    const adapter = await readyAdapter(body);
    await expect(adapter.collect(task(), new AbortController().signal)).rejects.toMatchObject({
      code: "page_contract_changed",
    });
  });

  it("rejects a prompt echo before reading any sources", async () => {
    const adapter = await readyAdapter(
      html.replace("Synthetic Qwen answer for fixture testing only.", task().questionText),
    );
    await expect(adapter.collect(task(), new AbortController().signal)).rejects.toMatchObject({
      code: "page_contract_changed",
    });
    expect(await page.locator("#sources .source-item-mqZd08").count()).toBe(0);
  });
});
