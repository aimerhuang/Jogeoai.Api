import { readFile } from "node:fs/promises";
import { chromium, type Browser, type BrowserContext, type Page } from "playwright";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { loadSessionConfig } from "@aivc/config";
import { fixtureTask } from "../test-fixtures.ts";
import { buildWebQuestion } from "../web-question.ts";
import { ErnieAdapter, erniePageContractSchema, type ErniePageContract } from "./ernie.ts";
import { createRealAdapter, realPlatformUrl } from "./real-platform.ts";

const contract: ErniePageContract = {
  version: 2,
  origin: "https://wenxin.baidu.com",
  samplingMode: "quick_conversation",
  reviewedAt: new Date().toISOString(),
  verifiedOnAuthorizedPage: true,
  selectors: {
    authenticated: "#authenticated",
    newConversation: "#new",
    composer: "#composer",
    conversationActive: '#quick:text-is("快速")',
    submit: "#submit",
    assistantMessage: ".assistant",
    answerBody: ".answer .markdown",
    generating: "#generating",
    completed: ".answer.finished",
    citations: ".reference",
    loginRequired: "#login",
    regionRestricted: "#region",
    challengeRequired: "#challenge",
    rateLimited: "#rate-limit",
  },
};
describe("Ernie synthetic page contract, not live verification", () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let html: string;
  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    html = await readFile(new URL("./fixtures/ernie-synthetic.html", import.meta.url), "utf8");
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
  async function adapter(content = html, reviewed: ErniePageContract | undefined = contract) {
    await page.route("**/*", (route) =>
      route.fulfill({ contentType: "text/html; charset=utf-8", body: content }),
    );
    return new ErnieAdapter(page, reviewed, { pollMs: 10, stableMs: 30, timeoutMs: 500 });
  }
  it("routes login and missing-contract probe to Ernie without falling back to Doubao", async () => {
    const config = loadSessionConfig({
      COLLECTOR_PROFILE_DIRECTORY: "/aivc/ernie",
      COLLECTOR_SUPPORTED_PLATFORMS: "ernie",
    });
    expect(realPlatformUrl(config)).toBe("https://wenxin.baidu.com/");
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const ernie = await createRealAdapter(page, config);
    expect(ernie.platformCode).toBe("ernie");
    expect(await ernie.probe(new AbortController().signal)).toEqual({
      status: "page_contract_unverified",
    });
  });
  it("submits the original question and extracts visible assistant JSON references, excluding snippets", async () => {
    const ernie = await adapter();
    expect(await ernie.probe(new AbortController().signal)).toEqual({ status: "ready" });
    const task = {
      ...fixtureTask(),
      platformCode: "ernie" as const,
      questionText: "中文  原文\nsecond line",
    };
    const result = await ernie.collect(task, new AbortController().signal);
    expect(await page.locator("#composer").inputValue()).toBe(buildWebQuestion(task.questionText));
    expect(result.answerText).toBe("Synthetic Ernie answer only.");
    expect(result.citations).toEqual(["https://example.com/source"]);
  });
  it("accepts genuinely absent references", async () => {
    const ernie = await adapter(html.replaceAll('class="reference"', 'class="unrelated"'));
    await ernie.probe(new AbortController().signal);
    expect((await ernie.collect(fixtureTask(), new AbortController().signal)).citations).toEqual(
      [],
    );
  });
  it("returns the answer as Markdown while completion still uses the plain text", async () => {
    const ernie = await adapter(
      html.replace(
        '<div class="markdown">Synthetic Ernie answer only.</div>',
        '<div class="markdown"><div class="marklang"><p>导语：</p><h3>优联科技（深圳）</h3>' +
          "<ul><li>\u200c<strong>4188KH</strong>\u200c：23mm 摄像头<br>续行</li><li>型号 *二*</li></ul><hr></div></div>",
      ),
    );
    await ernie.probe(new AbortController().signal);
    const result = await ernie.collect(fixtureTask(), new AbortController().signal);
    expect(result.answerText).toBe(
      "导语：\n\n### 优联科技（深圳）\n\n- \u200c**4188KH**\u200c：23mm 摄像头  \n  续行\n- 型号 \\*二\\*\n\n---",
    );
    expect(result.citations).toEqual(["https://example.com/source"]);
  });
  it("joins visible body chunks in DOM order without source snippets", async () => {
    const ernie = await adapter(
      html.replace(
        "Synthetic Ernie answer only.</div>",
        'First chunk.</div><div class="markdown" hidden>Hidden chunk</div><div class="markdown">Second chunk.</div>',
      ),
    );
    await ernie.probe(new AbortController().signal);
    const result = await ernie.collect(fixtureTask(), new AbortController().signal);
    // Markdown joins visible chunks as separate paragraphs; the plain stability text uses "\n".
    expect(result.answerText).toBe("First chunk.\n\nSecond chunk.");
    expect(result.citations).toEqual(["https://example.com/source"]);
  });
  it.each(["not-json", '{"link":42}', '{"link":"not-a-url"}'])(
    "rejects malformed citation attributes: %s",
    async (value) => {
      const ernie = await adapter(
        html.replace('{"link":"https://example.com/source","linkTitle":"Synthetic source"}', value),
      );
      await ernie.probe(new AbortController().signal);
      await expect(
        ernie.collect(fixtureTask(), new AbortController().signal),
      ).rejects.toMatchObject({ code: "page_contract_changed" });
    },
  );
  it("rejects streaming output without a positive completion marker", async () => {
    const ernie = await adapter(html.replace('class="answer finished"', 'class="answer"'));
    await ernie.probe(new AbortController().signal);
    await expect(ernie.collect(fixtureTask(), new AbortController().signal)).rejects.toMatchObject({
      code: "collection_timeout",
    });
  });
  it("waits for delayed first Markdown chunks after the assistant wrapper appears", async () => {
    const ernie = await adapter(
      html
        .replace('class="answer finished"', 'class="answer"')
        .replace(
          'class="markdown">Synthetic Ernie answer only.',
          'class="pending-body">Synthetic Ernie answer only.',
        ),
    );
    await ernie.probe(new AbortController().signal);
    await page.locator("#submit").evaluate((button) => {
      button.addEventListener("click", () => {
        setTimeout(() => {
          button.ownerDocument
            .querySelector(".pending-body")
            ?.classList.replace("pending-body", "markdown");
          button.ownerDocument.querySelector(".answer")?.classList.add("finished");
        }, 80);
      });
    });
    expect((await ernie.collect(fixtureTask(), new AbortController().signal)).answerText).toBe(
      "Synthetic Ernie answer only.",
    );
  });
  it("rejects completed wrappers with no answer body", async () => {
    const ernie = await adapter(
      html.replace(
        'class="markdown">Synthetic Ernie answer only.',
        'class="missing-body">Synthetic Ernie answer only.',
      ),
    );
    await ernie.probe(new AbortController().signal);
    await expect(ernie.collect(fixtureTask(), new AbortController().signal)).rejects.toMatchObject({
      code: "page_contract_changed",
    });
  });
  it("fails closed when the observed quick mode changes", async () => {
    const ernie = await adapter(html.replace("快速", "深度思考"));
    expect(await ernie.probe(new AbortController().signal)).toEqual({
      status: "page_contract_changed",
    });
    expect(await page.locator("#composer").inputValue()).toBe("");
  });
  it("rejects a contract for another platform or sampling mode", () => {
    for (const patch of [
      { origin: "https://www.doubao.com" },
      { samplingMode: "default_conversation" },
      { verifiedOnAuthorizedPage: false },
    ]) {
      expect(erniePageContractSchema.safeParse({ ...contract, ...patch }).success).toBe(false);
    }
  });
});
