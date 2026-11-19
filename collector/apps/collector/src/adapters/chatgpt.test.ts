import { readFile } from "node:fs/promises";
import { chromium, type Browser, type BrowserContext, type Page } from "playwright";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { loadSessionConfig } from "@aivc/config";
import { fixtureTask } from "../test-fixtures.ts";
import { buildWebQuestion } from "../web-question.ts";
import {
  ChatGptAdapter,
  chatgptPageContractSchema,
  loadChatGptPageContract,
  type ChatGptPageContract,
} from "./chatgpt.ts";
import { createRealAdapter, realPlatformUrl } from "./real-platform.ts";

const contract: ChatGptPageContract = {
  version: 2,
  origin: "https://chatgpt.com",
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

describe("ChatGPT synthetic consumer-Web contract", () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let html: string;

  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    html = await readFile(new URL("./fixtures/chatgpt-synthetic.html", import.meta.url), "utf8");
  });
  beforeEach(async () => {
    context = await browser.newContext({ serviceWorkers: "block" });
    page = await context.newPage();
  });
  afterEach(async () => await context.close());
  afterAll(async () => await browser.close());

  it("routes ChatGPT to the overseas web origin without fallback", async () => {
    const config = loadSessionConfig({
      COLLECTOR_PROFILE_DIRECTORY: "/aivc/chatgpt",
      COLLECTOR_SUPPORTED_PLATFORMS: "chatgpt",
    });
    expect(realPlatformUrl(config)).toBe("https://chatgpt.com/");
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const adapter = await createRealAdapter(page, config);
    expect(adapter.platformCode).toBe("chatgpt");
    expect(await adapter.probe(new AbortController().signal)).toEqual({
      status: "page_contract_unverified",
    });
  });

  it("uses the shared assistant extraction with reviewed selectors", async () => {
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const adapter = new ChatGptAdapter(page, contract, {
      pollMs: 10,
      stableMs: 30,
      timeoutMs: 500,
    });
    expect(chatgptPageContractSchema.parse(contract).origin).toBe("https://chatgpt.com");
    expect(await adapter.probe(new AbortController().signal)).toEqual({ status: "ready" });
    const result = await adapter.collect(
      { ...fixtureTask(), platformCode: "chatgpt", questionText: "中文  原文\nsecond line" },
      new AbortController().signal,
    );
    expect(await page.locator("#composer").inputValue()).toBe(
      buildWebQuestion("中文  原文\nsecond line"),
    );
    expect(result.answerText).toBe("Synthetic ChatGPT answer for fixture testing only.");
    expect(result.citations).toEqual(["https://example.com/chatgpt-source"]);
  });

  it("waits for the generating control to disappear instead of treating the body as complete", async () => {
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const adapter = new ChatGptAdapter(
      page,
      {
        ...contract,
        selectors: { ...contract.selectors, generating: "#generating" },
      },
      {
        pollMs: 10,
        stableMs: 30,
        timeoutMs: 500,
      },
    );
    await page.goto("https://chatgpt.com/");
    await page.locator("body").evaluate((body) => {
      const button = body.ownerDocument?.createElement("button");
      if (!button) throw new Error("missing fixture document");
      button.id = "generating";
      button.textContent = "Stop generating";
      body.append(button);
      setTimeout(() => button.remove(), 80);
    });
    const started = Date.now();
    const result = await adapter.collect(
      { ...fixtureTask(), platformCode: "chatgpt" },
      new AbortController().signal,
    );
    expect(result.answerText).toBe("Synthetic ChatGPT answer for fixture testing only.");
    expect(Date.now() - started).toBeGreaterThanOrEqual(80);
  });

  it("rejects unreviewed origins and sampling modes", async () => {
    await expect(loadChatGptPageContract(undefined)).resolves.toBeUndefined();
    expect(() =>
      chatgptPageContractSchema.parse({ ...contract, origin: "https://chat.openai.com" }),
    ).toThrow();
    expect(() =>
      chatgptPageContractSchema.parse({ ...contract, samplingMode: "quick_conversation" }),
    ).toThrow();
  });
});
