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
import { readAnswerMarkdown } from "./answer-markdown.ts";
import { CollectorError } from "../platform-adapter.ts";

export const DEEPSEEK_URL = "https://chat.deepseek.com/";
const DEEPSEEK_HOSTS = ["chat.deepseek.com", "deepseek.com"];

// Reviewed 2026-10-22 (智能搜索 on): the answer footer reads "N 个网页"; clicking it opens a
// "搜索结果" side panel outside the message with exactly N cards, each an <a href> holding a
// .search-view-card__title. Hashed class names stay in the page contract; these are semantic.
export const DEEPSEEK_SOURCE_PANEL_HEADING = '[role="heading"]:text-is("搜索结果")';
export const DEEPSEEK_SOURCE_CARD = "a[href]:has(.search-view-card__title)";

export async function readDeepSeekSources(
  page: Page,
  message: Locator,
  toggleSelector: string,
  signal: AbortSignal,
  deadline: number,
  inlineUrls: string[],
): Promise<CompletedSources> {
  const toggle = message.locator(toggleSelector);
  // Smart search may answer without searching: no footer count and no inline citations.
  if ((await toggle.count()) === 0) {
    if (inlineUrls.length) throw new CollectorError("page_contract_changed");
    return { urls: [] };
  }
  if ((await toggle.count()) !== 1 || !(await toggle.isVisible()))
    throw new CollectorError("page_contract_changed");
  const claim = (await toggle.innerText()).trim().match(/^(\d+)\s*个网页$/u);
  const expected = Number(claim?.[1]);
  if (!Number.isSafeInteger(expected) || expected < 1 || expected > 100)
    throw new CollectorError("page_contract_changed");
  const heading = page.locator(DEEPSEEK_SOURCE_PANEL_HEADING);
  // An already open panel could belong to an earlier response.
  if (await heading.count()) throw new CollectorError("page_contract_changed");
  const until = Math.min(deadline, Date.now() + 5_000);
  await toggle.click({ timeout: Math.max(1, until - Date.now()) });
  try {
    while (Date.now() < until) {
      signal.throwIfAborted();
      if ((await heading.count()) === 1 && (await heading.isVisible())) {
        const cards = await page.locator(DEEPSEEK_SOURCE_CARD).evaluateAll((elements) =>
          elements.map((element) => ({
            url: element.getAttribute("href") ?? "",
            title: element.querySelector(".search-view-card__title")?.textContent?.trim() || null,
          })),
        );
        if (cards.length > expected) throw new CollectorError("page_contract_changed");
        if (cards.length === expected) {
          if (cards.some((card) => publicCitations([card.url], DEEPSEEK_HOSTS).length !== 1))
            throw new CollectorError("page_contract_changed");
          const urls = publicCitations(
            [...inlineUrls, ...cards.map((card) => card.url)],
            DEEPSEEK_HOSTS,
          );
          return {
            urls,
            titles: urls.map(
              (url) => cards.find((card) => new URL(card.url).href === url)?.title ?? null,
            ),
          };
        }
      }
      await delay(100, undefined, { signal });
    }
    throw new CollectorError("page_contract_changed");
  } finally {
    const close = page.locator(`${DEEPSEEK_SOURCE_PANEL_HEADING} ~ [role="button"]`);
    if (!signal.aborted && (await close.count()) === 1) {
      await close.click({ timeout: 1_000 });
      await heading.waitFor({ state: "hidden", timeout: 1_000 });
    }
  }
}

// Selectors are reviewed against an authorized persistent Profile before live scheduling.
// web_search_conversation: the contract's conversationActive asserts 智能搜索 on and 深度思考 off;
// the adapter never toggles modes itself.
export const deepseekPageContractSchema = sharedSchema.extend({
  origin: z.literal("https://chat.deepseek.com"),
  samplingMode: z.enum(["default_conversation", "web_search_conversation"]),
});
export type DeepSeekPageContract = z.infer<typeof deepseekPageContractSchema>;

export async function loadDeepSeekPageContract(path: string | undefined) {
  const contract = await loadContract(path, deepseekPageContractSchema);
  return contract ? deepseekPageContractSchema.parse(contract) : undefined;
}

export class DeepSeekAdapter extends ConsumerWebAdapter {
  constructor(
    page: Page,
    contract?: DeepSeekPageContract,
    timing?: { pollMs: number; stableMs: number; timeoutMs: number },
  ) {
    const sourceToggle = contract?.selectors.sourcesToggle;
    super(
      page,
      contract,
      "deepseek",
      DEEPSEEK_URL,
      DEEPSEEK_HOSTS,
      timing,
      undefined,
      false,
      5_000,
      sourceToggle
        ? (message, signal, deadline, inlineUrls) =>
            readDeepSeekSources(page, message, sourceToggle, signal, deadline, inlineUrls)
        : undefined,
      undefined,
      false,
      true,
      // 回答正文转为 Markdown（answerIgnore 剔除行内引用角标）；为空或失败时沿用 innerText 纯文本。
      contract
        ? (message, timeoutMs) => readAnswerMarkdown(message, contract, timeoutMs)
        : undefined,
    );
  }
}
