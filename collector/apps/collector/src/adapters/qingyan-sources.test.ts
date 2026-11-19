import { readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { chromium, type Browser, type BrowserContext, type Page } from "playwright";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import {
  QingyanAdapter,
  qingyanPageContractSchema,
  readQingyanSources,
  type QingyanPageContract,
} from "./qingyan.ts";
import { classifyLandingPage } from "./consumer-web.ts";
import { fixtureTask } from "../test-fixtures.ts";

const contract: QingyanPageContract = {
  version: 2,
  origin: "https://chatglm.cn",
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
    citations: ".inline-citation",
    loginRequired: "#login",
    regionRestricted: "#region",
    challengeRequired: "#challenge",
    rateLimited: "#rate-limit",
    sourcesToggle: ".sources-toggle",
    sourcePanel: ".source-panel",
    sourceCards: ".source-card",
    sourceEvidence: ".source-marker",
  },
};
describe("Qingyan synthetic popup sources, no live website requests", () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let base: string;
  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    base = await readFile(new URL("./fixtures/doubao-synthetic.html", import.meta.url), "utf8");
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
  function fixture(count = 2, cards = ["https://example.com/one", "https://example.com/two"]) {
    return base
      .replace(
        "</article>",
        '<button class="sources-toggle" onclick="document.querySelector(\'.source-panel\').hidden=false; this.dataset.clicks = Number(this.dataset.clicks || 0) + 1">' +
          count +
          "个来源</button></article>",
      )
      .replace(
        "</body>",
        '<div class="source-panel" hidden>' +
          cards
            .map(
              (url) =>
                '<button class="source-card" onclick="this.dataset.clicks = Number(this.dataset.clicks || 0) + 1; window.open(\'' +
                url +
                "', '_blank')\">Synthetic source</button>",
            )
            .join("") +
          "</div></body>",
      );
  }
  async function prepare(html = fixture()) {
    await context.route("**/*", (route) =>
      route.fulfill({
        contentType: "text/html; charset=utf-8",
        body:
          new URL(route.request().url()).origin === contract.origin
            ? html
            : "<html><body>Source fixture</body></html>",
      }),
    );
    const adapter = new QingyanAdapter(page, contract, {
      pollMs: 10,
      stableMs: 30,
      timeoutMs: 2_000,
    });
    await adapter.probe(new AbortController().signal);
    return adapter;
  }
  async function historical(html = fixture()) {
    await prepare(html);
    await page.locator("#submit").click();
  }
  function read(signal = new AbortController().signal, timeout = 1_000) {
    return readQingyanSources(
      page,
      page.locator(".assistant"),
      contract,
      signal,
      Date.now() + timeout,
      [],
    );
  }
  it("opens each counted card exactly once and closes all source tabs", async () => {
    const adapter = await prepare();
    const result = await adapter.collect(fixtureTask(), new AbortController().signal);
    expect(result.citations).toEqual(["https://example.com/one", "https://example.com/two"]);
    expect(await page.locator(".sources-toggle").getAttribute("data-clicks")).toBe("1");
    expect(
      await page
        .locator(".source-card")
        .evaluateAll((cards) => cards.map((card) => card.getAttribute("data-clicks"))),
    ).toEqual(["1", "1"]);
    expect(context.pages()).toEqual([page]);
  });
  it("deduplicates actual popup URLs after verifying the card count", async () => {
    await historical(fixture(2, ["https://example.com/one", "https://example.com/one"]));
    expect(await read()).toEqual(["https://example.com/one"]);
  });
  it("preserves the initial browser target when its load fails", async () => {
    await historical(fixture(1, ["https://example.com/unreachable"]));
    await context.route("https://example.com/unreachable", (route) => route.abort("failed"));
    expect(await read()).toEqual(["https://example.com/unreachable"]);
    expect(context.pages()).toEqual([page]);
  });
  it("fails closed when a delayed response prevents public frame/opener correlation", async () => {
    await historical(fixture(1, ["https://example.com/slow"]));
    let popupEvents = 0;
    let frameUnavailable = false;
    page.on("popup", () => popupEvents++);
    context.on("request", (request) => {
      if (request.url() !== "https://example.com/slow") return;
      try {
        request.frame();
      } catch {
        frameUnavailable = true;
      }
    });
    let releaseResponse: ReturnType<typeof setTimeout> | undefined;
    await context.route("https://example.com/slow", (route) => {
      releaseResponse = setTimeout(() => {
        void route
          .fulfill({ contentType: "text/html", body: "Slow fixture" })
          .catch(() => undefined);
      }, 6_000);
    });
    try {
      await expect(read(undefined, 1_000)).rejects.toMatchObject({ code: "page_contract_changed" });
      expect(frameUnavailable).toBe(true);
      expect(popupEvents).toBe(0);
      expect(context.pages()).toEqual([page]);
    } finally {
      if (releaseResponse) clearTimeout(releaseResponse);
    }
  });
  it("preserves the initial target rather than its redirect destination", async () => {
    await historical(fixture(1, ["https://example.com/original"]));
    let redirectedRequests = 0;
    const server = createServer((_request, response) => {
      redirectedRequests++;
      response.writeHead(200, { "content-type": "text/html" });
      response.end("Local source fixture");
    });
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address();
    if (!address || typeof address === "string") throw new Error("test_server");
    try {
      // Playwright routes only the first request in an HTTP redirect chain.
      // Serve its destination locally so this test never falls through to the Web.
      await context.route("https://example.com/original", (route) =>
        route.fulfill({
          status: 302,
          headers: { location: `http://127.0.0.1:${address.port}/redirected` },
          body: "",
        }),
      );
      expect(await read()).toEqual(["https://example.com/original"]);
      expect(redirectedRequests).toBe(1);
      expect(context.pages()).toEqual([page]);
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });
  it("captures a source delayed six seconds within the bounded source budget", async () => {
    await historical(fixture(1, ["https://example.com/slow-success"]));
    let releaseResponse: ReturnType<typeof setTimeout> | undefined;
    await context.route("https://example.com/slow-success", (route) => {
      releaseResponse = setTimeout(() => {
        void route
          .fulfill({ contentType: "text/html", body: "Slow fixture" })
          .catch(() => undefined);
      }, 6_000);
    });
    try {
      expect(await read(undefined, 10_000)).toEqual(["https://example.com/slow-success"]);
      expect(context.pages()).toEqual([page]);
    } finally {
      if (releaseResponse) clearTimeout(releaseResponse);
    }
  }, 12_000);
  it("ignores unrelated existing-page navigation and subresource requests", async () => {
    await historical(fixture(1, ["https://example.com/actual"]));
    const unrelated = await context.newPage();
    await unrelated.goto("https://example.com/unrelated");
    const work = read();
    await unrelated.goto("https://example.com/another");
    expect(await work).toEqual(["https://example.com/actual"]);
    expect(context.pages()).toEqual([page, unrelated]);
  });
  it.each([0, 1, 2, 3, 4])("rejects ambiguous multiple popups from one card, run %s", async () => {
    await historical(
      fixture(1, ["https://example.com/one"]).replace(
        "window.open",
        "window.open('https://example.com/extra', '_blank'); window.open",
      ),
    );
    await expect(read()).rejects.toMatchObject({ code: "page_contract_changed" });
    expect(context.pages()).toEqual([page]);
  });
  it("rejects a second popup delivered shortly after the first event", async () => {
    await historical(
      fixture(1, ["https://example.com/one"]).replace(
        "window.open",
        "setTimeout(() => window.open('https://example.com/extra', '_blank'), 60); window.open",
      ),
    );
    await expect(read()).rejects.toMatchObject({ code: "page_contract_changed" });
    expect(context.pages()).toEqual([page]);
  });
  it.each([1, 3])("rejects source card count mismatch %s", async (count) => {
    await historical(fixture(count));
    await expect(read()).rejects.toMatchObject({ code: "page_contract_changed" });
    expect(context.pages()).toEqual([page]);
  });
  it("rejects missing popups without repeating clicks", async () => {
    await historical(fixture(1, ["https://example.com/one"]).replace("window.open", "void"));
    await expect(read(undefined, 150)).rejects.toMatchObject({ code: "page_contract_changed" });
    expect(await page.locator(".source-card").getAttribute("data-clicks")).toBe("1");
  });
  it.each(["https://chatglm.cn/internal", "javascript:void(0)"])(
    "rejects unusable source target %s",
    async (url) => {
      await historical(fixture(1, [url]));
      await expect(read(undefined, 200)).rejects.toThrow();
      expect(context.pages()).toEqual([page]);
    },
  );
  it("allows truly absent sources and an explicit zero-source footer", async () => {
    await historical(base);
    expect(await read()).toEqual([]);
    await page.reload();
    await page.locator("#submit").click();
    await page.locator(".assistant").evaluate((message) => {
      const footer = message.ownerDocument.createElement("button");
      footer.className = "sources-toggle";
      footer.textContent = "0个来源";
      message.append(footer);
    });
    expect(await read()).toEqual([]);
  });
  it("rejects orphan source markers when the footer is absent", async () => {
    await historical(base.replace("</article>", '<span class="source-marker">1</span></article>'));
    await expect(read()).rejects.toMatchObject({ code: "page_contract_changed" });
  });
  it("rejects zero source count contradicted by visible citation markers", async () => {
    await historical(
      fixture(0, []).replace("</article>", '<span class="source-marker">1</span></article>'),
    );
    await expect(read()).rejects.toMatchObject({ code: "page_contract_changed" });
  });
  it.each(["来源", "101个来源", "2个来源 3个来源"])(
    "rejects an unverified source count caption %s",
    async (caption) => {
      await historical(fixture().replace("2个来源", caption));
      await expect(read()).rejects.toMatchObject({ code: "page_contract_changed" });
    },
  );
  it("rejects multiple panels opened by a toggle", async () => {
    await historical(
      fixture().replace(
        "this.dataset.clicks = Number(this.dataset.clicks || 0) + 1",
        "document.querySelector('.source-panel').after(document.querySelector('.source-panel').cloneNode(true)); this.dataset.clicks = Number(this.dataset.clicks || 0) + 1",
      ),
    );
    await expect(read()).rejects.toMatchObject({ code: "page_contract_changed" });
  });
  it("rejects a pre-existing or ambiguous visible source panel", async () => {
    await historical(fixture().replace('class="source-panel" hidden', 'class="source-panel"'));
    await expect(read()).rejects.toMatchObject({ code: "page_contract_changed" });
  });
  it("aborts popup waiting and leaves no source tabs", async () => {
    await historical(fixture(1, ["about:blank"]));
    const controller = new AbortController();
    const work = read(controller.signal);
    await new Promise<void>((resolve) =>
      page.once("popup", () => {
        controller.abort();
        resolve();
      }),
    );
    await expect(work).rejects.toThrow();
    expect(context.pages()).toEqual([page]);
  });
  it("refuses submission if the answer changes during source reading", async () => {
    const adapter = await prepare(
      fixture(1, ["https://example.com/one"]).replace(
        "window.open",
        "document.querySelector('.answer').textContent='Changed body'; window.open",
      ),
    );
    await expect(
      adapter.collect(fixtureTask(), new AbortController().signal),
    ).rejects.toMatchObject({ code: "page_contract_changed" });
  });
  it("rejects source count changes during the final card popup", async () => {
    await historical(
      fixture(1, ["https://example.com/one"]).replace(
        "window.open",
        "document.querySelector('.sources-toggle').textContent='2个来源'; document.querySelector('.source-panel').append(document.querySelector('.source-card').cloneNode(true)); window.open",
      ),
    );
    await expect(read()).rejects.toMatchObject({ code: "page_contract_changed" });
    expect(context.pages()).toEqual([page]);
  });
  it("rejects card content changes during source capture", async () => {
    await historical(
      fixture().replace(
        "window.open",
        "document.querySelector('.source-card').textContent='Changed card'; window.open",
      ),
    );
    await expect(read()).rejects.toMatchObject({ code: "page_contract_changed" });
  });
  it("does not open sources until completion is positive", async () => {
    const adapter = await prepare(fixture().replace('class="complete"', 'class="pending"'));
    await expect(
      adapter.collect(fixtureTask(), new AbortController().signal),
    ).rejects.toMatchObject({ code: "collection_timeout" });
    expect(await page.locator(".sources-toggle").getAttribute("data-clicks")).toBeNull();
  });
  it("requires paired source selectors with their toggle", () => {
    expect(
      qingyanPageContractSchema.safeParse({
        ...contract,
        selectors: { ...contract.selectors, sourceCards: undefined },
      }).success,
    ).toBe(false);
  });
  it.each(["访问验证", "滑动验证", "验证失败"])("classifies visible challenge %s", (text) => {
    expect(classifyLandingPage(text, "https://chatglm.cn/")).toBe("challenge_required");
  });
});
