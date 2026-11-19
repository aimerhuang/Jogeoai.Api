import { setTimeout as delay } from "node:timers/promises";
import type { Locator, Page } from "playwright";
import { z } from "zod";
import {
  ConsumerWebAdapter,
  pageContractSchema as sharedSchema,
  loadPageContract as loadContract,
  publicCitations,
  type CompletedSources,
} from "./consumer-web.ts";
import { CollectorError } from "../platform-adapter.ts";
import { readAnswerMarkdown } from "./answer-markdown.ts";

export const YUANBAO_URL = "https://yuanbao.tencent.com/";

// Reviewed 2026-10-22: the source drawer is a portal outside the assistant message.
export const YUANBAO_RESPONSE_ROOT = ".agent-chat__list__item--ai";
export const YUANBAO_SOURCE_TOGGLE = '[aria-label^="引用"][aria-label$="篇资料作为参考"]';
// Reviewed 2026-10-27: plugin sources such as「来自腾讯地图的参考资料」are disabled cards with a
// plugin tag and an empty data-url. They count toward「引用N篇」but carry no public URL.
export const YUANBAO_PLUGIN_SOURCE_CARD = ".hyc-common-markdown__ref_card--disable";
export const YUANBAO_PLUGIN_SOURCE_TAG = ".hyc-common-markdown__ref_card-tags-plugin";

async function readYuanbaoSources(
  page: Page,
  message: Locator,
  toggleSelector: string,
  signal: AbortSignal,
  deadline: number,
  inlineUrls: string[],
): Promise<CompletedSources> {
  const toggle = message.locator(toggleSelector);
  if ((await toggle.count()) === 0) {
    if (await message.locator(".hyc-common-markdown__ref-list").count())
      throw new CollectorError("page_contract_changed");
    return { urls: inlineUrls };
  }
  if ((await toggle.count()) !== 1 || !(await toggle.isVisible()))
    throw new CollectorError("page_contract_changed");
  const claim = (await toggle.getAttribute("aria-label"))?.match(/^引用(\d+)篇资料作为参考$/u);
  const expected = Number(claim?.[1]);
  if (!Number.isSafeInteger(expected) || expected < 1 || expected > 100)
    throw new CollectorError("page_contract_changed");
  const panel = page.locator(".t-drawer--open .agent-dialogue-references");
  // An already open drawer could belong to an earlier response.
  if (await panel.count()) throw new CollectorError("page_contract_changed");
  const until = Math.min(deadline, Date.now() + 5_000);
  await toggle.click({ timeout: Math.max(1, until - Date.now()) });
  try {
    while (Date.now() < until) {
      signal.throwIfAborted();
      if ((await panel.count()) === 1 && (await panel.isVisible())) {
        const cards = await panel
          .locator(".agent-dialogue-references__item .hyc-common-markdown__ref_card[data-url]")
          .evaluateAll(
            (elements, plugin) =>
              elements.map((element) => ({
                url: element.getAttribute("data-url") ?? "",
                title:
                  element
                    .querySelector(".hyc-common-markdown__ref_card-title")
                    ?.textContent?.trim() || null,
                plugin:
                  element.matches(plugin.card) &&
                  element.querySelector(plugin.tag) !== null &&
                  element.getAttribute("data-url") === "",
              })),
            { card: YUANBAO_PLUGIN_SOURCE_CARD, tag: YUANBAO_PLUGIN_SOURCE_TAG },
          );
        if (cards.length > expected) throw new CollectorError("page_contract_changed");
        if (cards.length === expected) {
          // Only marked plugin cards may lack a URL; any other card must be a public link.
          const linked = cards.filter((card) => !card.plugin);
          if (
            linked.some((card) => publicCitations([card.url], ["yuanbao.tencent.com"]).length !== 1)
          )
            throw new CollectorError("page_contract_changed");
          const urls = publicCitations(
            [...inlineUrls, ...linked.map((card) => card.url)],
            ["yuanbao.tencent.com"],
          );
          return {
            urls,
            titles: urls.map(
              (url) => linked.find((card) => new URL(card.url).href === url)?.title ?? null,
            ),
          };
        }
      }
      await delay(100, undefined, { signal });
    }
    throw new CollectorError("page_contract_changed");
  } finally {
    if (!signal.aborted && (await panel.count()) === 1) {
      await panel.locator(".agent-dialogue-references__close button").click({ timeout: 1_000 });
      await panel.waitFor({ state: "hidden", timeout: 1_000 });
    }
  }
}

// Reviewed 2026-10-22: JD goods cards and the "相关视频" card render inside the answer body. The
// Markdown body leaves them out (answerIgnore) and appends them below the answer instead.
export const YUANBAO_GOODS_CARD = ".hyc-common-markdown__goods-card-v2";
export const YUANBAO_VIDEO_CARD = ".ybc-chat-videoBoxV2-bigCard-wrapper";

interface YuanbaoCard {
  kind: "goods" | "video";
  image: string;
  title: string;
  lines: string[];
}

function escapeCardText(text: string): string {
  return text
    .replace(/\s+/gu, " ")
    .trim()
    .replace(/[\\`*_~[\]|<>]/gu, "\\$&")
    .replace(/&(?=#?\w+;)/gu, "\\&")
    .replace(/^([#>+=-])/u, "\\$1")
    .replace(/^(\d+)([.)])/u, "$1\\$2");
}

function cardImageUrl(src: string): string | undefined {
  try {
    const url = new URL(src);
    return url.protocol === "https:"
      ? url.href.replaceAll("(", "%28").replaceAll(")", "%29")
      : undefined;
  } catch {
    return undefined;
  }
}

/**
 * Goods and video cards inside the answer body, in DOM order, as a Markdown section appended
 * below the answer. Returns "" when there are none; throws when a card lacks its title so the
 * caller keeps the plain answer (which still contains the card text) instead of losing it.
 */
export async function readYuanbaoCardsMarkdown(
  message: Locator,
  answerBody: string,
): Promise<string> {
  const cards: YuanbaoCard[] = await message
    .locator(answerBody)
    .locator(`${YUANBAO_GOODS_CARD}, ${YUANBAO_VIDEO_CARD}`)
    .evaluateAll(
      (elements, selectors) =>
        elements.flatMap((element): YuanbaoCard[] => {
          // Object-literal method on purpose: tsx wraps named nested functions in a module-scope
          // __name() call that does not exist in the page (see answer-markdown.ts).
          const h = {
            text(node: { textContent: string | null } | null): string {
              return node?.textContent?.trim() ?? "";
            },
          };
          if (element.matches(selectors.goods)) {
            const prefix = ".hyc-common-markdown__goods-card-v2__";
            const price = h.text(element.querySelector(`${prefix}price`));
            const sales = h.text(element.querySelector(`${prefix}sales`));
            return [
              {
                kind: "goods",
                image: element.querySelector(`${prefix}image img`)?.getAttribute("src") ?? "",
                title: h.text(element.querySelector(`${prefix}title`)),
                lines: [
                  [...element.querySelectorAll(`${prefix}tag`)].map((tag) => h.text(tag)),
                  [price, sales, h.text(element.querySelector(`${prefix}shop`))],
                ].map((parts) => parts.filter(Boolean).join(" · ")),
              },
            ];
          }
          return [...element.querySelectorAll(".ybc-chat-videoBoxV2-v3Card__item")].map((item) => ({
            kind: "video",
            image: item.querySelector("[class*='__item__cover'] img")?.getAttribute("src") ?? "",
            title: h.text(item.querySelector(".ybc-chat-videoBoxV2-v3Card__item__title")),
            lines: [
              [
                h.text(item.querySelector("[class*='__item__duration']")),
                h.text(item.querySelector(".ybc-chat-videoBoxV2-v3Card__item__source__name")),
                h.text(item.querySelector("[class*='__item__publishTime']")),
              ]
                .filter(Boolean)
                .join(" · "),
            ],
          }));
        }),
      { goods: YUANBAO_GOODS_CARD },
    );
  if (cards.some((card) => !card.title)) throw new CollectorError("page_contract_changed");
  const sections: string[] = [];
  for (const [kind, heading] of [
    ["goods", "相关商品"],
    ["video", "相关视频"],
  ] as const) {
    const items = cards
      .filter((card) => card.kind === kind)
      .map((card) => {
        const title = escapeCardText(card.title);
        const image = cardImageUrl(card.image);
        const lines = [
          ...(image ? [`![${title}](${image})`] : []),
          `**${title}**`,
          ...card.lines.map(escapeCardText).filter(Boolean),
        ];
        return `- ${lines.join("  \n  ")}`;
      });
    if (items.length) sections.push(`**${heading}**\n\n${items.join("\n")}`);
  }
  return sections.join("\n\n");
}

/**
 * Yuanbao answer Markdown: the body (answerIgnore drops dots, source chips and cards) followed by
 * the goods/video cards. Undefined when either part cannot be read, so the plain answer is kept.
 */
export async function readYuanbaoAnswerMarkdown(
  message: Locator,
  contract: { selectors: { answerBody: string; answerIgnore?: string } },
  timeoutMs?: number,
): Promise<string | undefined> {
  const body = await readAnswerMarkdown(message, contract, timeoutMs);
  if (!body) return undefined;
  try {
    const cards = await readYuanbaoCardsMarkdown(message, contract.selectors.answerBody);
    return cards ? `${body}\n\n---\n\n${cards}` : body;
  } catch {
    return undefined;
  }
}

// Selectors are reviewed against an authorized persistent Profile before live scheduling.
export const yuanbaoPageContractSchema = sharedSchema.extend({
  origin: z.literal("https://yuanbao.tencent.com"),
  samplingMode: z.literal("default_conversation"),
});
export type YuanbaoPageContract = z.infer<typeof yuanbaoPageContractSchema>;

export async function loadYuanbaoPageContract(path: string | undefined) {
  const contract = await loadContract(path, yuanbaoPageContractSchema);
  return contract ? yuanbaoPageContractSchema.parse(contract) : undefined;
}

export class YuanbaoAdapter extends ConsumerWebAdapter {
  constructor(
    page: Page,
    contract?: YuanbaoPageContract,
    timing?: { pollMs: number; stableMs: number; timeoutMs: number },
  ) {
    const sourceToggle = contract?.selectors.sourcesToggle;
    super(
      page,
      contract,
      "tencent_yuanbao",
      YUANBAO_URL,
      ["yuanbao.tencent.com"],
      timing,
      undefined,
      true,
      5_000,
      sourceToggle
        ? (message, signal, deadline, inlineUrls) =>
            readYuanbaoSources(page, message, sourceToggle, signal, deadline, inlineUrls)
        : undefined,
      undefined,
      false,
      true,
      // 回答正文转为 Markdown（列表圆点、来源角标由 answerIgnore 剔除），商品与视频卡片附在正文下方。
      contract
        ? (message, timeoutMs) => readYuanbaoAnswerMarkdown(message, contract, timeoutMs)
        : undefined,
    );
  }
}
