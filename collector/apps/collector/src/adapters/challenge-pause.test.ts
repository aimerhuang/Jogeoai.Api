import { readFile } from "node:fs/promises";
import { chromium, type Browser, type BrowserContext, type Page } from "playwright";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { createChallengeWaiter } from "../challenge-wait.ts";
import { withHeartbeat } from "../heartbeat.ts";
import type { CollectContext } from "../platform-adapter.ts";
import { fixtureConfig, fixtureTask } from "../test-fixtures.ts";
import { DoubaoAdapter, type DoubaoPageContract } from "./doubao.ts";

// Synthetic DOM only: no platform request, account or real challenge is involved. The adapter
// only observes visibility; the test page hides the challenge on its own timer, standing in for
// the operator solving it in the profile window.
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
const challengeMarkup =
  '<div id="challenge" hidden>Synthetic verification panel</div>' +
  '<div id="captcha_container" hidden><iframe srcdoc="synthetic iframe only"></iframe></div>';

describe("operator challenge pause on a synthetic page (no live website requests)", () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let html: string;
  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    html = (
      await readFile(new URL("./fixtures/doubao-synthetic.html", import.meta.url), "utf8")
    ).replace("</body>", `${challengeMarkup}</body>`);
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
  async function adapter(timeoutMs = 500) {
    await page.route("**/*", (route) =>
      route.fulfill({ contentType: "text/html; charset=utf-8", body: html }),
    );
    const doubao = new DoubaoAdapter(page, contract, { pollMs: 10, stableMs: 30, timeoutMs });
    expect(await doubao.probe(new AbortController().signal)).toEqual({ status: "ready" });
    // Count submit clicks on the page itself.
    await page
      .locator("#submit")
      .evaluate((button) =>
        button.addEventListener("click", () =>
          button.setAttribute("data-clicks", String(Number(button.dataset.clicks ?? 0) + 1)),
        ),
      );
    return doubao;
  }
  // Shows the challenge `selector` when `trigger` fires, hides it after `visibleMs` (-1: never).
  async function challengeOn(
    trigger: "new" | "fill" | "submit",
    selector: string,
    visibleMs: number,
  ) {
    await page.evaluate(
      ([event, target, ms]) => {
        type Element = {
          hidden: boolean;
          addEventListener(type: string, listener: () => void): void;
        };
        const document = (
          globalThis as unknown as { document: { querySelector(selector: string): Element } }
        ).document;
        const source =
          event === "fill"
            ? document.querySelector("#composer")
            : document.querySelector(`#${event}`);
        let shown = false;
        source.addEventListener(event === "fill" ? "input" : "click", () => {
          if (shown) return;
          shown = true;
          const challenge = document.querySelector(target);
          challenge.hidden = false;
          if (Number(ms) >= 0) setTimeout(() => (challenge.hidden = true), Number(ms));
        });
      },
      [trigger, selector, String(visibleMs)] as const,
    );
  }
  const submitClicks = async () =>
    Number((await page.locator("#submit").getAttribute("data-clicks")) ?? 0);
  function waiterContext(budgetMs: number, extendDeadline = vi.fn()) {
    const events: string[] = [];
    let submitted = false;
    const collectContext: CollectContext = {
      challengeWaiter: createChallengeWaiter({
        budgetMs,
        extendDeadline,
        onEvent: (event, phase) => {
          events.push(`${event}:${phase}`);
        },
      }),
      onSubmitted: () => {
        submitted = true;
      },
    };
    return { collectContext, events, extendDeadline, submitted: () => submitted };
  }

  it("AC-8: a pre-send challenge that clears in time resumes, submits once and succeeds without a collection timeout", async () => {
    const doubao = await adapter();
    await challengeOn("new", "#challenge", 2_500);
    const heartbeat = vi.fn(async () => ({
      leaseExpiresAt: new Date(Date.now() + 60_000).toISOString(),
    }));
    const client = {
      lease: vi.fn(),
      submit: vi.fn(),
      reportFailure: vi.fn(),
      sessionEvent: vi.fn(),
      eligibility: vi.fn().mockResolvedValue({ eligible: true, retryAfterSeconds: 30 }),
      heartbeat,
    };
    const config = {
      ...fixtureConfig(),
      // Shorter than the challenge: only the deadline extension keeps the work alive.
      COLLECTOR_COLLECTION_TIMEOUT_MS: 1_500,
      COLLECTOR_HEARTBEAT_INTERVAL_MS: 200,
    };
    const events: string[] = [];
    const started = Date.now();
    const result = await withHeartbeat(
      fixtureTask(),
      config,
      client,
      new AbortController().signal,
      (signal, lease) =>
        doubao.collect(fixtureTask(), signal, {
          challengeWaiter: createChallengeWaiter({
            budgetMs: 10_000,
            extendDeadline: lease.extendDeadline,
            onEvent: (event, phase) => {
              events.push(`${event}:${phase}`);
            },
          }),
        }),
    );
    expect(Date.now() - started).toBeGreaterThan(config.COLLECTOR_COLLECTION_TIMEOUT_MS);
    expect(result.answerText).toBe("Synthetic answer for fixture testing only.");
    expect(await submitClicks()).toBe(1);
    expect(events).toEqual(["challenge_wait_started:pre_send", "challenge_cleared:pre_send"]);
    // The lease kept renewing throughout the wait.
    expect(heartbeat.mock.calls.length).toBeGreaterThan(5);
  }, 20_000);

  it("rechecks the filled composer after a challenge between fill and submit, then submits", async () => {
    const doubao = await adapter();
    await challengeOn("fill", "#captcha_container", 150);
    const { collectContext, events, submitted } = waiterContext(5_000);
    const result = await doubao.collect(
      fixtureTask(),
      new AbortController().signal,
      collectContext,
    );
    expect(result.answerText).toBe("Synthetic answer for fixture testing only.");
    expect(await submitClicks()).toBe(1);
    expect(submitted()).toBe(true);
    expect(events).toEqual(["challenge_wait_started:pre_send", "challenge_cleared:pre_send"]);
  });

  it("AC-9: a challenge that outlasts the wait never clicks submit and fails with challenge_required", async () => {
    const doubao = await adapter();
    await challengeOn("new", "#challenge", -1);
    const { collectContext, events, extendDeadline, submitted } = waiterContext(200);
    await expect(
      doubao.collect(fixtureTask(), new AbortController().signal, collectContext),
    ).rejects.toMatchObject({ code: "challenge_required" });
    expect(await submitClicks()).toBe(0);
    expect(submitted()).toBe(false);
    expect(await page.locator(".assistant").count()).toBe(0);
    expect(events).toEqual(["challenge_wait_started:pre_send"]);
    // Extended by the full budget, nothing returned: the whole budget was used.
    expect(extendDeadline.mock.calls).toEqual([[200], [0]]);
  });

  it("AC-10: a post-send challenge that clears in time still collects the complete answer", async () => {
    // Answer deadline (300ms) shorter than the challenge: waiting must not count against it.
    const doubao = await adapter(300);
    await challengeOn("submit", "#captcha_container", 600);
    const { collectContext, events, submitted } = waiterContext(5_000);
    const result = await doubao.collect(
      fixtureTask(),
      new AbortController().signal,
      collectContext,
    );
    expect(result.answerText).toBe("Synthetic answer for fixture testing only.");
    expect(result.citations).toEqual(["https://example.com/source"]);
    expect(await submitClicks()).toBe(1);
    expect(submitted()).toBe(true);
    expect(events).toEqual(["challenge_wait_started:post_send", "challenge_cleared:post_send"]);
  });

  it("keeps failing immediately without a waiter (existing behaviour)", async () => {
    const doubao = await adapter();
    await challengeOn("new", "#challenge", 100);
    await expect(doubao.collect(fixtureTask(), new AbortController().signal)).rejects.toMatchObject(
      { code: "challenge_required" },
    );
    expect(await submitClicks()).toBe(0);
  });

  it("stops on another blocker that replaces the challenge", async () => {
    const doubao = await adapter();
    await challengeOn("new", "#challenge", -1);
    await page.locator("#new").evaluate((button) =>
      button.addEventListener("click", () =>
        setTimeout(() => {
          const document = button.ownerDocument;
          (document.querySelector("#challenge") as unknown as { hidden: boolean }).hidden = true;
          const login = document.createElement("div");
          login.id = "login-required";
          login.textContent = "Synthetic login panel";
          document.body.append(login);
        }, 100),
      ),
    );
    const { collectContext, events } = waiterContext(5_000);
    await expect(
      doubao.collect(fixtureTask(), new AbortController().signal, collectContext),
    ).rejects.toMatchObject({ code: "login_required" });
    expect(await submitClicks()).toBe(0);
    expect(events).toEqual(["challenge_wait_started:pre_send"]);
  });
});
