import { readFile } from "node:fs/promises";
import { chromium, type Browser, type BrowserContext, type Page } from "playwright";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { loadSessionConfig } from "@aivc/config";
import { fixtureTask } from "../test-fixtures.ts";
import { buildWebQuestion } from "../web-question.ts";
import {
  GeminiAdapter,
  geminiPageContractSchema,
  loadGeminiPageContract,
  type GeminiPageContract,
} from "./gemini.ts";
import { createRealAdapter, realPlatformUrl } from "./real-platform.ts";

const contract: GeminiPageContract = {
  version: 2,
  origin: "https://gemini.google.com",
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

describe("Gemini synthetic consumer-Web contract", () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let html: string;

  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    html = await readFile(new URL("./fixtures/gemini-synthetic.html", import.meta.url), "utf8");
  });
  beforeEach(async () => {
    context = await browser.newContext({ serviceWorkers: "block" });
    page = await context.newPage();
  });
  afterEach(async () => await context.close());
  afterAll(async () => await browser.close());

  it("routes Gemini to its overseas web origin without fallback", async () => {
    const config = loadSessionConfig({
      COLLECTOR_PROFILE_DIRECTORY: "/aivc/gemini",
      COLLECTOR_SUPPORTED_PLATFORMS: "gemini",
    });
    expect(realPlatformUrl(config)).toBe("https://gemini.google.com/app");
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const adapter = await createRealAdapter(page, config);
    expect(adapter.platformCode).toBe("gemini");
    expect(await adapter.probe(new AbortController().signal)).toEqual({
      status: "page_contract_unverified",
    });
  });

  it("extracts the reviewed assistant body and public citations", async () => {
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const adapter = new GeminiAdapter(page, contract, {
      pollMs: 10,
      stableMs: 30,
      timeoutMs: 500,
    });
    expect(await adapter.probe(new AbortController().signal)).toEqual({ status: "ready" });
    const result = await adapter.collect(
      { ...fixtureTask(), platformCode: "gemini", questionText: "中文  原文\nsecond line" },
      new AbortController().signal,
    );
    expect(await page.locator("#composer").inputValue()).toBe(
      buildWebQuestion("中文  原文\nsecond line"),
    );
    expect(result.answerText).toBe("Synthetic Gemini answer for fixture testing only.");
    expect(result.citations).toEqual(["https://example.com/gemini-source"]);
  });

  it("rejects unreviewed origins and sampling modes", async () => {
    await expect(loadGeminiPageContract(undefined)).resolves.toBeUndefined();
    expect(() =>
      geminiPageContractSchema.parse({ ...contract, origin: "https://gemini.google.com.br" }),
    ).toThrow();
    expect(() =>
      geminiPageContractSchema.parse({ ...contract, samplingMode: "quick_conversation" }),
    ).toThrow();
  });
});
