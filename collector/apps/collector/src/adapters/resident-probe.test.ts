import { readFile } from "node:fs/promises";
import { chromium, type Browser, type BrowserContext, type Page } from "playwright";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { fixtureTask } from "../test-fixtures.ts";
import { DoubaoAdapter, type DoubaoPageContract } from "./doubao.ts";

// Synthetic DOM only: every request is fulfilled locally. Covers the resident collector's probe
// (design §12.3): an already-loaded entry page is checked in place instead of reloaded.
const contract: DoubaoPageContract = {
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

describe("resident probe without navigation (synthetic page)", () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let html: string;
  let navigations: string[];
  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    html = (
      await readFile(new URL("./fixtures/doubao-synthetic.html", import.meta.url), "utf8")
    ).replace("</body>", '<div id="challenge" hidden>Synthetic verification panel</div></body>');
  });
  beforeEach(async () => {
    context = await browser.newContext({ serviceWorkers: "block" });
    page = await context.newPage();
    navigations = [];
    page.on("request", (request) => {
      if (request.isNavigationRequest()) navigations.push(new URL(request.url()).origin);
    });
    await page.route("**/*", (route) =>
      route.fulfill({ contentType: "text/html; charset=utf-8", body: html }),
    );
  });
  afterEach(async () => {
    await context.close();
  });
  afterAll(async () => {
    await browser.close();
  });
  const adapter = () =>
    new DoubaoAdapter(page, contract, { pollMs: 10, stableMs: 30, timeoutMs: 500 });
  const probe = (doubao: DoubaoAdapter) =>
    doubao.probe(new AbortController().signal, { inPlace: true });

  it("navigates from a fresh page once, then re-checks the loaded page in place", async () => {
    const doubao = adapter();
    expect(page.url()).toBe("about:blank");
    expect(await probe(doubao)).toEqual({ status: "ready" });
    expect(navigations).toEqual(["https://www.doubao.com"]);
    expect(await probe(doubao)).toEqual({ status: "ready" });
    // After a completed collection the page stays on the entry origin: still no reload.
    await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(await probe(doubao)).toEqual({ status: "ready" });
    expect(navigations).toEqual(["https://www.doubao.com"]);
  });

  it("reports a visible challenge in place without reloading under the operator", async () => {
    const doubao = adapter();
    expect(await probe(doubao)).toEqual({ status: "ready" });
    await page.locator("#challenge").evaluate((element) => {
      (element as unknown as { hidden: boolean }).hidden = false;
    });
    expect(await probe(doubao)).toEqual({ status: "challenge_required" });
    expect(navigations).toHaveLength(1);
  });

  it("navigates again when a reviewed marker is missing in place", async () => {
    const doubao = adapter();
    expect(await probe(doubao)).toEqual({ status: "ready" });
    await page.locator("#composer").evaluate((element) => {
      (element as unknown as { remove(): void }).remove();
    });
    // The reload restores the synthetic page, so the full probe is ready again.
    expect(await probe(doubao)).toEqual({ status: "ready" });
    expect(navigations).toEqual(["https://www.doubao.com", "https://www.doubao.com"]);
  });

  it("always navigates without the resident opt-in (single-account flow unchanged)", async () => {
    const doubao = adapter();
    const legacyProbe = () => doubao.probe(new AbortController().signal);
    expect(await legacyProbe()).toEqual({ status: "ready" });
    expect(await legacyProbe()).toEqual({ status: "ready" });
    await doubao.collect(fixtureTask(), new AbortController().signal);
    expect(await legacyProbe()).toEqual({ status: "ready" });
    expect(navigations).toEqual([
      "https://www.doubao.com",
      "https://www.doubao.com",
      "https://www.doubao.com",
    ]);
  });

  it("navigates when the page is on another origin", async () => {
    const doubao = adapter();
    await page.goto("https://example.com/");
    expect(await probe(doubao)).toEqual({ status: "ready" });
    expect(navigations).toEqual(["https://example.com", "https://www.doubao.com"]);
  });
});
