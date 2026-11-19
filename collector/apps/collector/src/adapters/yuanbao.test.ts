import { readFile } from "node:fs/promises";
import { chromium, type Browser, type BrowserContext, type Page } from "playwright";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { loadSessionConfig } from "@aivc/config";
import { fixtureTask } from "../test-fixtures.ts";
import { buildWebQuestion } from "../web-question.ts";
import {
  YuanbaoAdapter,
  YUANBAO_RESPONSE_ROOT,
  YUANBAO_SOURCE_TOGGLE,
  loadYuanbaoPageContract,
  yuanbaoPageContractSchema,
  type YuanbaoPageContract,
} from "./yuanbao.ts";
import { createRealAdapter, realPlatformUrl } from "./real-platform.ts";

const contract: YuanbaoPageContract = {
  version: 2,
  origin: "https://yuanbao.tencent.com",
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

describe("Tencent Yuanbao synthetic consumer-Web contract", () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let html: string;

  beforeAll(async () => {
    browser = await chromium.launch({ headless: true });
    html = await readFile(new URL("./fixtures/yuanbao-synthetic.html", import.meta.url), "utf8");
  });
  beforeEach(async () => {
    context = await browser.newContext({ serviceWorkers: "block" });
    page = await context.newPage();
  });
  afterEach(async () => await context.close());
  afterAll(async () => await browser.close());

  it("routes Yuanbao login/probe to the web origin without fallback", async () => {
    const config = loadSessionConfig({
      COLLECTOR_PROFILE_DIRECTORY: "/aivc/yuanbao",
      COLLECTOR_SUPPORTED_PLATFORMS: "tencent_yuanbao",
    });
    expect(realPlatformUrl(config)).toBe("https://yuanbao.tencent.com/");
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const adapter = await createRealAdapter(page, config);
    expect(adapter.platformCode).toBe("tencent_yuanbao");
    expect(await adapter.probe(new AbortController().signal)).toEqual({
      status: "page_contract_unverified",
    });
  });

  it("uses the shared browser flow for the original question", async () => {
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const adapter = new YuanbaoAdapter(page, contract, {
      pollMs: 10,
      stableMs: 30,
      timeoutMs: 2_000,
    });
    expect(yuanbaoPageContractSchema.parse(contract).origin).toBe("https://yuanbao.tencent.com");
    expect(await adapter.probe(new AbortController().signal)).toEqual({
      status: "ready",
    });
    const result = await adapter.collect(
      {
        ...fixtureTask(),
        platformCode: "tencent_yuanbao",
        platformEntryCode: "tencent_yuanbao.consumer_web",
        questionText: "中文原文",
      },
      new AbortController().signal,
    );
    expect(await page.locator("#composer").inputValue()).toBe(buildWebQuestion("中文原文"));
    expect(result.answerText).toBe("Synthetic Tencent Yuanbao answer for fixture testing only.");
    expect(result.citations).toEqual(["https://example.com/yuanbao-source"]);
  });

  // Card markup mirrors the 2026-10-22 live DOM (class names and nesting); the content is synthetic.
  const goodsCard = (title: string) =>
    '<div class="hyc-common-markdown__goods-card-v2"><div class="hyc-common-markdown__goods-card-v2__row" role="button">' +
    '<div class="hyc-common-markdown__goods-card-v2__image"><img src="https://img.example.com/p(1).png" alt="x"></div>' +
    '<div class="hyc-common-markdown__goods-card-v2__info"><div class="hyc-common-markdown__goods-card-v2__title-block">' +
    `<div class="hyc-common-markdown__goods-card-v2__title">${title}</div>` +
    '<div class="hyc-common-markdown__goods-card-v2__tags"><span class="hyc-common-markdown__goods-card-v2__tag">好评率100%</span>' +
    '<span class="hyc-common-markdown__goods-card-v2__tag">质保*三年*</span></div></div>' +
    '<div class="hyc-common-markdown__goods-card-v2__price-block"><span class="hyc-common-markdown__goods-card-v2__price">' +
    '<span>¥</span><span>50</span></span><div class="hyc-common-markdown__goods-card-v2__shop">京东 · 示例店</div></div></div></div>' +
    '<div class="hyc-common-markdown__goods-card-v2__more" role="button"><span>查看更多商品</span></div></div>';
  const videoCard =
    '<div class="ybc-chat-videoBoxV2-bigCard-wrapper"><div class="ybc-chat-videoBoxV2-bigCard__title">相关视频</div>' +
    '<div class="ybc-chat-videoBoxV2-bigCard"><div class="ybc-chat-videoBoxV2-v3Card__item">' +
    '<div class="ybc-chat-videoBoxV2-v3Card__item__cover video-box-v2_ybc-chat-videoBoxV2-v3Card__item__cover__sNskx"><img src="http://insecure.example.com/c.png">' +
    '<span class="video-box-v2_ybc-chat-videoBoxV2-v3Card__item__duration__Cbbyj">01:27</span></div><div class="ybc-chat-videoBoxV2-v3Card__item__footer">' +
    '<div class="ybc-chat-videoBoxV2-v3Card__item__title">#市政 管道\n内窥镜</div>' +
    '<span class="ybc-chat-videoBoxV2-v3Card__item__source__name">示例账号</span>' +
    '<span class="video-box-v2_ybc-chat-videoBoxV2-v3Card__item__publishTime__ul6yR">3个月前</span></div></div></div></div>';
  const richAnswer = (cards: string) =>
    '<div class="answer"><div class="ybc-p">导语<strong>加粗</strong>。</div><h2>一、厂家</h2>' +
    '<ul class="ybc-ul-component"><li class="ybc-li-component"><span class="ybc-li-component__dot-wp">' +
    '<span class="ybc-li-component_dot">∙</span></span><span class="ybc-li-component_content">' +
    '<div class="ybc-p"><strong>RIDGID</strong>：手机看画面。<div class="hyc-common-markdown__ref-list">' +
    '<div class="hyc-common-markdown__ref-list__trigger">3</div></div></div></span></li></ul>' +
    cards +
    '<blockquote><div class="ybc-p">建议。</div></blockquote></div>';
  async function richAdapter(answer: string) {
    await page.route("**/*", (route) =>
      route.fulfill({
        contentType: "text/html; charset=utf-8",
        body: html.replace(
          '<div class="answer">Synthetic Tencent Yuanbao answer for fixture testing only.</div>',
          answer,
        ),
      }),
    );
    const adapter = new YuanbaoAdapter(
      page,
      {
        ...contract,
        selectors: {
          ...contract.selectors,
          // Same value as the reviewed 2026-10-22 live contract; the DOM is synthetic.
          answerIgnore:
            '.ybc-li-component__dot-wp, .hyc-common-markdown__ref-list, .hyc-common-markdown__goods-card-v2, [class*="ybc-chat-videoBoxV2-bigCard"]',
        },
      },
      { pollMs: 10, stableMs: 30, timeoutMs: 2_000 },
    );
    await adapter.probe(new AbortController().signal);
    return adapter.collect(
      { ...fixtureTask(), platformCode: "tencent_yuanbao" },
      new AbortController().signal,
    );
  }

  it("returns Markdown with goods and video cards appended below the answer", async () => {
    const result = await richAdapter(richAnswer(goodsCard("REEDEA 管道[内窥镜]") + videoCard));
    expect(result.answerText).toBe(
      "导语**加粗**。\n\n## 一、厂家\n\n- **RIDGID**：手机看画面。\n\n> 建议。\n\n---\n\n" +
        "**相关商品**\n\n" +
        "- ![REEDEA 管道\\[内窥镜\\]](https://img.example.com/p%281%29.png)  \n" +
        "  **REEDEA 管道\\[内窥镜\\]**  \n  好评率100% · 质保\\*三年\\*  \n  ¥50 · 京东 · 示例店\n\n" +
        "**相关视频**\n\n" +
        "- **\\#市政 管道 内窥镜**  \n  01:27 · 示例账号 · 3个月前",
    );
    expect(result.citations).toEqual(["https://example.com/yuanbao-source"]);
  });

  it("keeps the plain answer when a card cannot be read", async () => {
    const result = await richAdapter(richAnswer(goodsCard("")));
    expect(result.answerText).toContain("查看更多商品");
    expect(result.answerText).not.toContain("**");
  });

  it("locates a completed Yuanbao response from its source affordance", async () => {
    await page.setContent(html);
    await page.locator("#submit").click();
    const message = page.locator(YUANBAO_RESPONSE_ROOT);
    expect(await message.count()).toBe(1);
    expect(await message.locator(".answer").innerText()).toBe(
      "Synthetic Tencent Yuanbao answer for fixture testing only.",
    );
    expect(await message.locator(YUANBAO_SOURCE_TOGGLE).isVisible()).toBe(true);
  });

  it("waits for the answer body after the assistant placeholder renders", async () => {
    const delayed = html.replace(
      "document.querySelector('#messages').innerHTML = document.querySelector('#answer-template').innerHTML",
      "document.querySelector('#messages').innerHTML = '<article class=&quot;assistant&quot;></article>'; setTimeout(() => {document.querySelector('#messages').innerHTML = document.querySelector('#answer-template').innerHTML}, 100)",
    );
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: delayed }));
    const adapter = new YuanbaoAdapter(page, contract, {
      pollMs: 10,
      stableMs: 30,
      timeoutMs: 2_000,
    });
    await adapter.probe(new AbortController().signal);
    const result = await adapter.collect(
      { ...fixtureTask(), platformCode: "tencent_yuanbao" },
      new AbortController().signal,
    );
    expect(result.answerText).toBe("Synthetic Tencent Yuanbao answer for fixture testing only.");
  });

  it("reads source URLs and aligned titles from the completed answer's portal drawer", async () => {
    await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body: html }));
    const adapter = new YuanbaoAdapter(
      page,
      {
        ...contract,
        selectors: { ...contract.selectors, sourcesToggle: YUANBAO_SOURCE_TOGGLE },
      },
      { pollMs: 10, stableMs: 30, timeoutMs: 2_000 },
    );
    await adapter.probe(new AbortController().signal);
    const result = await adapter.collect(
      { ...fixtureTask(), platformCode: "tencent_yuanbao" },
      new AbortController().signal,
    );
    expect(result.citations).toEqual([
      "https://example.com/yuanbao-source",
      "https://second.example.com/source",
    ]);
    expect(result.citationTitles).toEqual(["First source title", "Second source title"]);
    expect(await page.locator(".agent-dialogue-references").count()).toBe(0);
  });

  // Shape observed 2026-10-27 on a live local-venue answer; the content here is synthetic.
  const linklessCard = (classes: string, tag: string) =>
    `<li class="agent-dialogue-references__item"><div class="${classes}" data-url="">` +
    `<span class="${tag}">腾讯地图</span>` +
    '<h4 class="hyc-common-markdown__ref_card-title">来自腾讯地图的参考资料</h4></div></li></ul>';
  const pluginCard = linklessCard(
    "hyc-common-markdown__ref_card hyc-common-markdown__ref_card--disable",
    "hyc-common-markdown__ref_card-tags hyc-common-markdown__ref_card-tags-plugin",
  );
  const withPluginSource = (value: string, card = pluginCard) =>
    value.replace("引用2篇", "引用3篇").replace("</ul>", card);

  it("counts Tencent Map plugin sources and keeps the map widget out of answer and citations", async () => {
    const mapWidget =
      '<div class="hyc-replace-location-mapGL"><div class="logo-text">©2026 Tencent - GS(2026)1190号</div>' +
      '<a href="http://map.qq.com?ref=jsapi_v3">腾讯地图</a></div>';
    const body = withPluginSource(html).replace(
      "Synthetic Tencent Yuanbao answer for fixture testing only.</div>",
      `Synthetic Tencent Yuanbao answer for fixture testing only.${mapWidget}</div>`,
    );
    await page.route("**/*", (route) =>
      route.fulfill({ contentType: "text/html; charset=utf-8", body }),
    );
    const adapter = new YuanbaoAdapter(
      page,
      {
        ...contract,
        selectors: {
          ...contract.selectors,
          sourcesToggle: YUANBAO_SOURCE_TOGGLE,
          // Same exclusions as the reviewed 2026-10-27 live contract.
          citations: ".citation, .answer a[href]:not(.hyc-replace-location-mapGL a)",
          answerIgnore: ".hyc-replace-location-mapGL",
        },
      },
      { pollMs: 10, stableMs: 30, timeoutMs: 2_000 },
    );
    await adapter.probe(new AbortController().signal);
    const result = await adapter.collect(
      { ...fixtureTask(), platformCode: "tencent_yuanbao" },
      new AbortController().signal,
    );
    expect(result.answerText).toBe("Synthetic Tencent Yuanbao answer for fixture testing only.");
    expect(result.citations).toEqual([
      "https://example.com/yuanbao-source",
      "https://second.example.com/source",
    ]);
    expect(result.citationTitles).toEqual(["First source title", "Second source title"]);
  });

  it.each([
    ["incomplete drawer", (value: string) => value.replace("引用2篇", "引用3篇")],
    [
      "linkless card without the plugin marker",
      (value: string) =>
        withPluginSource(
          value,
          linklessCard("hyc-common-markdown__ref_card", "hyc-common-markdown__ref_card-tags"),
        ),
    ],
    [
      "plugin-marked card that is not disabled",
      (value: string) =>
        withPluginSource(
          value,
          linklessCard(
            "hyc-common-markdown__ref_card",
            "hyc-common-markdown__ref_card-tags-plugin",
          ),
        ),
    ],
    [
      "non-public source",
      (value: string) =>
        value.replace(
          'data-url="https://second.example.com/source"',
          'data-url="https://yuanbao.tencent.com/internal"',
        ),
    ],
    [
      "stale drawer",
      (value: string) =>
        value.replace(
          '<div id="drawers"></div>',
          '<div id="drawers"><div class="t-drawer--open"><section class="agent-dialogue-references">Old sources</section></div></div>',
        ),
    ],
  ])("rejects %s instead of publishing empty or unrelated citations", async (_name, change) => {
    await page.route("**/*", (route) =>
      route.fulfill({ contentType: "text/html", body: change(html) }),
    );
    const adapter = new YuanbaoAdapter(
      page,
      {
        ...contract,
        selectors: { ...contract.selectors, sourcesToggle: YUANBAO_SOURCE_TOGGLE },
      },
      { pollMs: 10, stableMs: 30, timeoutMs: 700 },
    );
    await adapter.probe(new AbortController().signal);
    await expect(
      adapter.collect(
        { ...fixtureTask(), platformCode: "tencent_yuanbao" },
        new AbortController().signal,
      ),
    ).rejects.toMatchObject({ code: "page_contract_changed" });
  });

  it("rejects a contract that changes the web origin or sampling semantics", async () => {
    await expect(loadYuanbaoPageContract(undefined)).resolves.toBeUndefined();
    expect(() =>
      yuanbaoPageContractSchema.parse({
        ...contract,
        origin: "https://www.doubao.com",
      }),
    ).toThrow();
    expect(() =>
      yuanbaoPageContractSchema.parse({
        ...contract,
        samplingMode: "quick_conversation",
      }),
    ).toThrow();
  });
});
