import { readFile } from "node:fs/promises";
import { chromium, type Browser, type BrowserContext, type Page } from "playwright";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { loadSessionConfig } from "@aivc/config";
import { fixtureTask } from "../test-fixtures.ts";
import { buildWebQuestion } from "../web-question.ts";
import { QingyanAdapter, qingyanPageContractSchema, type QingyanPageContract } from "./qingyan.ts";
import { createRealAdapter, realPlatformUrl } from "./real-platform.ts";

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
    citations: ".citation",
    loginRequired: "#login",
    regionRestricted: "#region",
    challengeRequired: "#challenge",
    rateLimited: "#rate-limit",
  },
};
describe("Qingyan synthetic shared contract, not observed live selectors", () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let html: string;
  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    // Reuse the deliberately synthetic shared consumer-Web fixture.
    html = await readFile(new URL("./fixtures/doubao-synthetic.html", import.meta.url), "utf8");
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
  async function adapter(content = html, reviewedContract = contract) {
    await page.route("**/*", (route) =>
      route.fulfill({ contentType: "text/html; charset=utf-8", body: content }),
    );
    return new QingyanAdapter(page, reviewedContract, { pollMs: 10, stableMs: 30, timeoutMs: 500 });
  }
  it("routes official login and missing-contract probe without platform fallback", async () => {
    const config = loadSessionConfig({
      COLLECTOR_PROFILE_DIRECTORY: "/aivc/qingyan",
      COLLECTOR_SUPPORTED_PLATFORMS: "zhipu_qingyan",
    });
    expect(realPlatformUrl(config)).toBe("https://chatglm.cn/");
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const qingyan = await createRealAdapter(page, config);
    expect(qingyan.platformCode).toBe("zhipu_qingyan");
    expect(await qingyan.probe(new AbortController().signal)).toEqual({
      status: "page_contract_unverified",
    });
  });
  it("submits the original question, reads only assistant body and filters internal or hidden references", async () => {
    const qingyan = await adapter(
      html.replace(
        "</article>",
        '<a class="citation" href="https://chatglm.cn/internal">Internal</a></article>',
      ),
    );
    expect(await qingyan.probe(new AbortController().signal)).toEqual({ status: "ready" });
    const task = {
      ...fixtureTask(),
      platformCode: "zhipu_qingyan" as const,
      questionText: "中文  原文\nsecond line",
    };
    const result = await qingyan.collect(task, new AbortController().signal);
    expect(await page.locator("#composer").inputValue()).toBe(buildWebQuestion(task.questionText));
    expect(result.answerText).toBe("Synthetic answer for fixture testing only.");
    expect(result.citations).toEqual(["https://example.com/source"]);
  });
  it("requires positive completion", async () => {
    const qingyan = await adapter(html.replace('class="complete"', 'class="pending"'));
    await qingyan.probe(new AbortController().signal);
    await expect(
      qingyan.collect(fixtureTask(), new AbortController().signal),
    ).rejects.toMatchObject({ code: "collection_timeout" });
  });
  it("joins visible body chunks in order, excluding diagrams and reasoning", async () => {
    const qingyan = await adapter(
      html.replace(
        '<div class="answer">Synthetic answer for fixture testing only.</div>',
        '<div class="reasoning">Private reasoning</div><div class="answer">First chunk.</div><div class="diagram">Diagram controls</div><div class="answer" hidden>Hidden duplicate</div><div class="answer">Second chunk.</div>',
      ),
    );
    await qingyan.probe(new AbortController().signal);
    expect((await qingyan.collect(fixtureTask(), new AbortController().signal)).answerText).toBe(
      "First chunk.\n\nSecond chunk.",
    );
  });
  it("preserves Markdown structure while excluding reasoning, diagrams, hidden copies and ignored source chips", async () => {
    const qingyan = await adapter(
      html.replace(
        '<div class="answer">Synthetic answer for fixture testing only.</div>',
        '<aside class="reasoning">Private reasoning</aside>' +
          '<div class="answer"><h2>选型建议</h2><p>第一段<span class="source-chip">来源摘要</span>。</p>' +
          '<p>第二段，参考<a class="citation" href="https://example.com/details">说明文档</a>。</p>' +
          "<ul><li>便携机型</li><li>工业机型</li></ul>" +
          '<ol start="3"><li>核对口径</li><li>确认长度</li></ol>' +
          "<table><thead><tr><th>机型</th><th>用途</th></tr></thead>" +
          "<tbody><tr><td>A</td><td>巡检</td></tr></tbody></table>" +
          '<p hidden>Hidden paragraph</p><span style="display:none">Hidden inline</span></div>' +
          '<div class="diagram">Diagram controls</div><aside class="source-summary">Source snippet</aside>' +
          '<div class="answer" hidden><h2>Hidden duplicate</h2></div>' +
          '<div class="answer"><h3>结论</h3><p>按实际工况选择。</p></div>',
      ),
      { ...contract, selectors: { ...contract.selectors, answerIgnore: ".source-chip" } },
    );
    await qingyan.probe(new AbortController().signal);
    const result = await qingyan.collect(fixtureTask(), new AbortController().signal);
    expect(result.answerText).toBe(
      [
        "## 选型建议",
        "第一段。",
        "第二段，参考[说明文档](https://example.com/details)。",
        "- 便携机型\n- 工业机型",
        "3. 核对口径\n4. 确认长度",
        "| 机型 | 用途 |\n| --- | --- |\n| A | 巡检 |",
        "### 结论",
        "按实际工况选择。",
      ].join("\n\n"),
    );
    expect(result.citations).toEqual(["https://example.com/details", "https://example.com/source"]);
  });
  it("keeps the plain answer when the reviewed ignore selector leaves no Markdown", async () => {
    const qingyan = await adapter(
      html.replace(
        '<div class="answer">Synthetic answer for fixture testing only.</div>',
        '<div class="answer"><span>Plain ~ *fallback*</span></div>',
      ),
      { ...contract, selectors: { ...contract.selectors, answerIgnore: ".answer *" } },
    );
    await qingyan.probe(new AbortController().signal);
    const result = await qingyan.collect(fixtureTask(), new AbortController().signal);
    expect(result.answerText).toBe("Plain ~ *fallback*");
    expect(result.citations).toEqual(["https://example.com/source"]);
  });
  it.each(["original question", "full outgoing message"])(
    "rejects the %s exposed by Markdown source-chip removal",
    async (echo) => {
      const task = fixtureTask();
      const text =
        echo === "original question" ? task.questionText : buildWebQuestion(task.questionText);
      const qingyan = await adapter(
        html.replace(
          '<div class="answer">Synthetic answer for fixture testing only.</div>',
          '<div class="answer">' +
            text
              .split("\n\n")
              .map((paragraph) => `<p>${paragraph}</p>`)
              .join("") +
            '<span class="source-chip">Synthetic source chip</span></div>',
        ),
        { ...contract, selectors: { ...contract.selectors, answerIgnore: ".source-chip" } },
      );
      await qingyan.probe(new AbortController().signal);
      await expect(qingyan.collect(task, new AbortController().signal)).rejects.toMatchObject({
        code: "page_contract_changed",
      });
      // The raw answer is not a prompt echo; the guard must also run after formatting.
      expect(await page.locator(".answer").innerText()).toContain("Synthetic source chip");
    },
  );
  it("waits for the first body while its assistant wrapper is still generating", async () => {
    const qingyan = await adapter(
      html
        .replace('class="answer"', 'class="pending-body"')
        .replace('class="complete"', 'class="pending-completion"'),
    );
    await qingyan.probe(new AbortController().signal);
    await page.locator("#submit").evaluate((button) => {
      button.addEventListener("click", () =>
        setTimeout(() => {
          button.ownerDocument
            .querySelector(".pending-body")
            ?.classList.replace("pending-body", "answer");
          button.ownerDocument
            .querySelector(".pending-completion")
            ?.classList.replace("pending-completion", "complete");
        }, 80),
      );
    });
    expect((await qingyan.collect(fixtureTask(), new AbortController().signal)).answerText).toBe(
      "Synthetic answer for fixture testing only.",
    );
  });
  it("rejects completed assistant wrappers without body content", async () => {
    const qingyan = await adapter(html.replace('class="answer"', 'class="missing-body"'));
    await qingyan.probe(new AbortController().signal);
    await expect(
      qingyan.collect(fixtureTask(), new AbortController().signal),
    ).rejects.toMatchObject({ code: "page_contract_changed" });
  });
  it("allows Qingyan hydration longer than the legacy five-second probe window", async () => {
    const content =
      html.replace('id="authenticated"', 'id="hydrating"') +
      '<script>setTimeout(() => document.querySelector("#hydrating").id = "authenticated", 5200)</script>';
    await page.route("**/*", (route) =>
      route.fulfill({ contentType: "text/html; charset=utf-8", body: content }),
    );
    const qingyan = new QingyanAdapter(page, contract, {
      pollMs: 100,
      stableMs: 30,
      timeoutMs: 10_000,
    });
    expect(await qingyan.probe(new AbortController().signal)).toEqual({ status: "ready" });
  }, 12_000);
  it("rejects a prompt echo in an incorrectly reviewed assistant selector", async () => {
    const qingyan = await adapter(
      html.replace("Synthetic answer for fixture testing only.", fixtureTask().questionText),
    );
    await qingyan.probe(new AbortController().signal);
    await expect(
      qingyan.collect(fixtureTask(), new AbortController().signal),
    ).rejects.toMatchObject({ code: "page_contract_changed" });
  });
  it("requires strict Qingyan origin, mode and reviewed manifest", () => {
    for (const patch of [
      { origin: "https://wenxin.baidu.com" },
      { samplingMode: "quick_conversation" },
      { version: 1 },
      { verifiedOnAuthorizedPage: false },
      { extra: true },
    ])
      expect(qingyanPageContractSchema.safeParse({ ...contract, ...patch }).success).toBe(false);
  });
});
