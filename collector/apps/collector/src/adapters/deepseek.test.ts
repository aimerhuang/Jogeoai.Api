import { readFile } from "node:fs/promises";
import { chromium, type Browser, type BrowserContext, type Page } from "playwright";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { loadSessionConfig } from "@aivc/config";
import { fixtureTask } from "../test-fixtures.ts";
import { buildWebQuestion } from "../web-question.ts";
import {
  DeepSeekAdapter,
  deepseekPageContractSchema,
  loadDeepSeekPageContract,
  type DeepSeekPageContract,
} from "./deepseek.ts";
import { createRealAdapter, realPlatformUrl } from "./real-platform.ts";

const contract: DeepSeekPageContract = {
  version: 2,
  origin: "https://chat.deepseek.com",
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
    citations: ".citation",
    loginRequired: "#login",
    regionRestricted: "#region",
    challengeRequired: "#challenge",
    rateLimited: "#rate-limit",
  },
};

describe("DeepSeek synthetic consumer-Web contract", () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let html: string;

  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    html = await readFile(new URL("./fixtures/deepseek-synthetic.html", import.meta.url), "utf8");
  });
  beforeEach(async () => {
    context = await browser.newContext({ serviceWorkers: "block" });
    page = await context.newPage();
  });
  afterEach(async () => await context.close());
  afterAll(async () => await browser.close());

  it("routes DeepSeek login/probe to the web origin without fallback", async () => {
    const config = loadSessionConfig({
      COLLECTOR_PROFILE_DIRECTORY: "/aivc/deepseek",
      COLLECTOR_SUPPORTED_PLATFORMS: "deepseek",
    });
    expect(realPlatformUrl(config)).toBe("https://chat.deepseek.com/");
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const adapter = await createRealAdapter(page, config);
    expect(adapter.platformCode).toBe("deepseek");
    expect(await adapter.probe(new AbortController().signal)).toEqual({
      status: "page_contract_unverified",
    });
  });

  it("uses the shared browser flow for the original question", async () => {
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const adapter = new DeepSeekAdapter(page, contract, {
      pollMs: 10,
      stableMs: 30,
      timeoutMs: 500,
    });
    expect(deepseekPageContractSchema.parse(contract).origin).toBe("https://chat.deepseek.com");
    expect(await adapter.probe(new AbortController().signal)).toEqual({
      status: "ready",
    });
    const result = await adapter.collect(
      { ...fixtureTask(), platformCode: "deepseek", questionText: "中文原文" },
      new AbortController().signal,
    );
    expect(await page.locator("#composer").inputValue()).toBe(buildWebQuestion("中文原文"));
    expect(result.answerText).toBe("Synthetic DeepSeek answer for fixture testing only.");
    expect(result.citations).toEqual(["https://example.com/deepseek-source"]);
  });

  it("returns the answer as Markdown", async () => {
    await page.route("**/*", (route) =>
      route.fulfill({
        contentType: "text/html; charset=utf-8",
        body: html.replace(
          '<div class="answer">Synthetic DeepSeek answer for fixture testing only.</div>',
          '<div class="answer"><h3>推荐</h3><ol><li><strong>优联科技</strong>：推杆式</li><li>其他</li></ol>' +
            "<table><thead><tr><th>品牌</th><th>价格</th></tr></thead><tbody><tr><td>A</td><td>1|2</td></tr></tbody></table></div>",
        ),
      }),
    );
    const adapter = new DeepSeekAdapter(page, contract, {
      pollMs: 10,
      stableMs: 30,
      timeoutMs: 500,
    });
    await adapter.probe(new AbortController().signal);
    const result = await adapter.collect(
      { ...fixtureTask(), platformCode: "deepseek" },
      new AbortController().signal,
    );
    expect(result.answerText).toBe(
      "### 推荐\n\n1. **优联科技**：推杆式\n2. 其他\n\n| 品牌 | 价格 |\n| --- | --- |\n| A | 1\\|2 |",
    );
  });

  describe("smart search (web_search_conversation)", () => {
    let searchHtml: string;
    // Selector shapes follow the reviewed 2026-10-22 live contract; ids/classes are synthetic.
    const searchContract: DeepSeekPageContract = {
      ...contract,
      samplingMode: "web_search_conversation",
      selectors: {
        ...contract.selectors,
        conversationActive:
          '#mode:has(> .ds-toggle-button--selected:has-text("智能搜索")):not(:has(> .ds-toggle-button--selected:has-text("深度思考")))',
        citations: ".answer [data-citation-group] a[href]",
        sourcesToggle: '.complete :text-matches("^\\\\d+ 个网页$")',
        answerIgnore: "[data-citation-group]",
      },
    };
    beforeAll(async () => {
      searchHtml = await readFile(
        new URL("./fixtures/deepseek-search-synthetic.html", import.meta.url),
        "utf8",
      );
    });
    async function collect(body = searchHtml) {
      await page.route("**/*", (route) =>
        route.fulfill({ contentType: "text/html; charset=utf-8", body }),
      );
      const adapter = new DeepSeekAdapter(page, deepseekPageContractSchema.parse(searchContract), {
        pollMs: 10,
        stableMs: 30,
        timeoutMs: 2_000,
      });
      expect(await adapter.probe(new AbortController().signal)).toEqual({ status: "ready" });
      return adapter.collect(
        { ...fixtureTask(), platformCode: "deepseek" },
        new AbortController().signal,
      );
    }

    it("reads every panel source with titles, closes the panel and drops inline cite chips", async () => {
      const result = await collect();
      expect(result.answerText).toBe("### 推荐厂家\n\n**优联科技**：推杆式");
      expect(result.citations).toEqual(["https://example.com/first", "https://example.org/second"]);
      expect(result.citationTitles).toEqual(["First source title", "Second source title"]);
      expect(await page.locator('[role="heading"]').count()).toBe(0);
    });

    it("rejects a panel whose card count differs from the footer claim", async () => {
      await expect(collect(searchHtml.replace(">2 个网页<", ">3 个网页<"))).rejects.toMatchObject({
        code: "page_contract_changed",
      });
    });

    it("rejects inline citations without the footer source count", async () => {
      await expect(collect(searchHtml.replace(">2 个网页<", "><"))).rejects.toMatchObject({
        code: "page_contract_changed",
      });
    });

    it("accepts an answer that did not search", async () => {
      const result = await collect(
        searchHtml
          .replace(">2 个网页<", "><")
          .replace('<span class="ds-markdown-cite">-1</span>', "")
          .replace('href="https://example.com/first"><', "><"),
      );
      expect(result.citations).toEqual([]);
    });

    it("fails closed before asking when deep thinking is on or smart search is off", async () => {
      for (const body of [
        searchHtml.replace(
          'class="ds-toggle-button" role="button">深度思考',
          'class="ds-toggle-button ds-toggle-button--selected" role="button">深度思考',
        ),
        searchHtml.replace(
          'ds-toggle-button ds-toggle-button--selected" role="button">智能搜索',
          'ds-toggle-button" role="button">智能搜索',
        ),
      ]) {
        await page.route("**/*", (route) =>
          route.fulfill({ contentType: "text/html; charset=utf-8", body }),
        );
        const adapter = new DeepSeekAdapter(page, searchContract, {
          pollMs: 10,
          stableMs: 30,
          timeoutMs: 500,
        });
        expect(await adapter.probe(new AbortController().signal)).toEqual({
          status: "page_contract_changed",
        });
        await page.unrouteAll();
      }
    });
  });

  it("rejects a contract that changes the web origin or sampling semantics", async () => {
    await expect(loadDeepSeekPageContract(undefined)).resolves.toBeUndefined();
    expect(() =>
      deepseekPageContractSchema.parse({
        ...contract,
        origin: "https://www.doubao.com",
      }),
    ).toThrow();
    expect(() =>
      deepseekPageContractSchema.parse({
        ...contract,
        samplingMode: "quick_conversation",
      }),
    ).toThrow();
  });
});
