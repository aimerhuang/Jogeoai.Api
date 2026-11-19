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

export const QWEN_URL = "https://www.qianwen.com/";
const QWEN_HOSTS = ["qianwen.com", "www.qianwen.com"];
const sourceCardSchema = z.object({ url: z.string(), title: z.unknown().optional() });
// Selectors are reviewed against the authorized persistent Profile before live scheduling.
export const qwenPageContractSchema = sharedSchema.extend({
  origin: z.literal("https://www.qianwen.com"),
  samplingMode: z.literal("default_conversation"),
  selectors: sharedSchema.shape.selectors.extend({
    sourceCards: z.string().min(1).max(500).optional(),
  }),
});
export type QwenPageContract = z.infer<typeof qwenPageContractSchema>;
export async function loadQwenPageContract(path: string | undefined) {
  const contract = await loadContract(path, qwenPageContractSchema);
  return contract ? qwenPageContractSchema.parse(contract) : undefined;
}
export class QwenAdapter extends ConsumerWebAdapter {
  constructor(
    page: Page,
    contract?: QwenPageContract,
    timing?: { pollMs: number; stableMs: number; timeoutMs: number },
  ) {
    super(
      page,
      contract,
      "qwen",
      QWEN_URL,
      QWEN_HOSTS,
      timing,
      undefined,
      true,
      15_000,
      contract
        ? (message, signal, deadline, inlineUrls) =>
            readQwenSources(page, message, contract, signal, deadline, inlineUrls)
        : undefined,
      /^\s*向千问提问\s*$/u,
    );
  }
}

async function visibleSourceCards(cards: Locator): Promise<Locator[]> {
  const visible: Locator[] = [];
  for (const card of await cards.all()) if (await card.isVisible()) visible.push(card);
  return visible;
}

async function readQwenSources(
  page: Page,
  message: Locator,
  contract: QwenPageContract,
  signal: AbortSignal,
  deadline: number,
  inlineUrls: string[],
): Promise<CompletedSources> {
  const toggle = message.locator(contract.selectors.sourcesToggle ?? ".reference-wrap-iEjeb3");
  if ((await toggle.count()) === 0) return { urls: inlineUrls };
  if ((await toggle.count()) !== 1 || !(await toggle.isVisible()))
    throw new CollectorError("page_contract_changed");
  const count = Number(/^\s*(\d+)\s*篇来源\s*$/u.exec(await toggle.innerText())?.[1]);
  if (!Number.isSafeInteger(count) || count < 1 || count > 100)
    throw new CollectorError("page_contract_changed");
  const cards = page.locator(contract.selectors.sourceCards ?? ".source-item-mqZd08");
  // Visible cards before opening this answer's sources may belong to an older response.
  if ((await visibleSourceCards(cards)).length) throw new CollectorError("page_contract_changed");
  signal.throwIfAborted();
  const until = Math.min(deadline, Date.now() + 5_000);
  if (Date.now() >= until) throw new CollectorError("page_contract_changed");
  await toggle.click({ timeout: Math.max(1, until - Date.now()) });
  while (Date.now() < until) {
    signal.throwIfAborted();
    const visibleCards = await visibleSourceCards(cards);
    if (visibleCards.length > count) throw new CollectorError("page_contract_changed");
    if (visibleCards.length === count) {
      const sourceUrls: string[] = [];
      const titles = new Map<string, string>();
      for (const card of visibleCards) {
        const raw = await card.getAttribute("data-click-extra");
        let data: z.infer<typeof sourceCardSchema>;
        try {
          data = sourceCardSchema.parse(JSON.parse(raw ?? ""));
        } catch {
          throw new CollectorError("page_contract_changed");
        }
        const url = publicCitations([data.url], QWEN_HOSTS)[0];
        if (!url) throw new CollectorError("page_contract_changed");
        sourceUrls.push(url);
        // Titles are optional metadata. Keep the first readable title for each canonical URL.
        const title = typeof data.title === "string" ? data.title.trim() : "";
        if (title && title.length <= 512 && title.isWellFormed() && !titles.has(url))
          titles.set(url, title);
      }
      const urls = publicCitations([...inlineUrls, ...sourceUrls], QWEN_HOSTS);
      return { urls, titles: urls.map((url) => titles.get(url) ?? null) };
    }
    await delay(50, undefined, { signal });
  }
  throw new CollectorError("page_contract_changed");
}
