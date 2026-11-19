import type { Locator, Page, Request } from "playwright";
import { setTimeout as delay } from "node:timers/promises";
import { z } from "zod";
import {
  ConsumerWebAdapter,
  pageContractSchema as sharedSchema,
  loadPageContract as loadContract,
  publicCitations,
} from "./consumer-web.ts";
import { CollectorError } from "../platform-adapter.ts";
import { readAnswerMarkdown } from "./answer-markdown.ts";

export const QINGYAN_URL = "https://chatglm.cn/";
// An external reviewed manifest is required; fixture selectors are not live proof.
export const qingyanPageContractSchema = sharedSchema.extend({
  origin: z.literal("https://chatglm.cn"),
  samplingMode: z.literal("default_conversation"),
  selectors: sharedSchema.shape.selectors
    .extend({
      sourcePanel: z.string().min(1).max(500).optional(),
      sourceCards: z.string().min(1).max(500).optional(),
      sourceEvidence: z.string().min(1).max(500).optional(),
    })
    .refine(
      (selectors) =>
        (selectors.sourcePanel === undefined && selectors.sourceCards === undefined) ||
        (!!selectors.sourcePanel && !!selectors.sourceCards && !!selectors.sourcesToggle),
    ),
});
export type QingyanPageContract = z.infer<typeof qingyanPageContractSchema>;
export async function loadQingyanPageContract(path: string | undefined) {
  const contract = await loadContract(path, qingyanPageContractSchema);
  return contract ? qingyanPageContractSchema.parse(contract) : undefined;
}
export class QingyanAdapter extends ConsumerWebAdapter {
  constructor(
    page: Page,
    contract?: QingyanPageContract,
    timing?: { pollMs: number; stableMs: number; timeoutMs: number },
  ) {
    super(
      page,
      contract,
      "zhipu_qingyan",
      QINGYAN_URL,
      ["chatglm.cn", "www.chatglm.cn"],
      timing,
      undefined,
      true,
      15_000,
      contract
        ? (message, signal, deadline, inlineUrls) =>
            readQingyanSources(page, message, contract, signal, deadline, inlineUrls)
        : undefined,
      undefined,
      false,
      true,
      contract
        ? (message, timeoutMs) => readAnswerMarkdown(message, contract, timeoutMs)
        : undefined,
    );
  }
}

async function visibleItems(locator: Locator): Promise<Locator[]> {
  const items: Locator[] = [];
  for (const item of await locator.all()) if (await item.isVisible()) items.push(item);
  return items;
}

export async function readQingyanSources(
  page: Page,
  message: Locator,
  contract: QingyanPageContract,
  signal: AbortSignal,
  deadline: number,
  inlineUrls: string[],
): Promise<string[]> {
  const selectors = contract.selectors;
  const assertNoUnresolvedSources = async () => {
    if (
      (selectors.sourceEvidence &&
        (await visibleItems(message.locator(selectors.sourceEvidence))).length) ||
      (selectors.sourcePanel && (await visibleItems(page.locator(selectors.sourcePanel))).length)
    )
      throw new CollectorError("page_contract_changed");
  };
  if (!selectors.sourcesToggle) {
    await assertNoUnresolvedSources();
    return inlineUrls;
  }
  const toggles = await visibleItems(message.locator(selectors.sourcesToggle));
  if (!toggles.length) {
    await assertNoUnresolvedSources();
    return inlineUrls;
  }
  if (toggles.length !== 1 || !selectors.sourcePanel || !selectors.sourceCards)
    throw new CollectorError("page_contract_changed");
  const toggle = toggles[0]!;
  const match = /^\s*(\d+)\s*个来源\s*$/.exec(await toggle.innerText());
  const count = match ? Number(match[1]) : NaN;
  if (!Number.isSafeInteger(count) || count < 0 || count > 100)
    throw new CollectorError("page_contract_changed");
  if (!count) {
    await assertNoUnresolvedSources();
    return inlineUrls;
  }
  const remaining = () => {
    signal.throwIfAborted();
    const ms = deadline - Date.now();
    if (ms <= 0) throw new CollectorError("collection_timeout");
    return ms;
  };
  // A pre-existing panel cannot establish association with this answer's toggle.
  if ((await visibleItems(page.locator(selectors.sourcePanel))).length)
    throw new CollectorError("page_contract_changed");
  await toggle.click({ timeout: Math.min(5_000, remaining()) });
  const panelDeadline = Math.min(deadline, Date.now() + 5_000);
  let panel: Locator | undefined;
  while (Date.now() < panelDeadline) {
    remaining();
    const panels = await visibleItems(page.locator(selectors.sourcePanel));
    if (panels.length > 1) throw new CollectorError("page_contract_changed");
    if (panels.length === 1) {
      const cards = await visibleItems(panels[0]!.locator(selectors.sourceCards));
      if (cards.length > count) throw new CollectorError("page_contract_changed");
      if (cards.length === count) {
        panel = panels[0];
        break;
      }
    }
    await delay(50, undefined, { signal });
  }
  if (!panel) throw new CollectorError("page_contract_changed");
  const initialCards = await visibleItems(panel.locator(selectors.sourceCards));
  const cardTexts = await Promise.all(initialCards.map((card) => card.innerText()));
  const verifyPanel = async () => {
    remaining();
    const currentToggles = await visibleItems(message.locator(selectors.sourcesToggle!));
    if (
      currentToggles.length !== 1 ||
      Number(/^\s*(\d+)\s*个来源\s*$/.exec(await currentToggles[0]!.innerText())?.[1]) !== count
    )
      throw new CollectorError("page_contract_changed");
    if ((await visibleItems(page.locator(selectors.sourcePanel!))).length !== 1)
      throw new CollectorError("page_contract_changed");
    const cards = await visibleItems(panel!.locator(selectors.sourceCards!));
    if (
      cards.length !== count ||
      JSON.stringify(await Promise.all(cards.map((card) => card.innerText()))) !==
        JSON.stringify(cardTexts)
    )
      throw new CollectorError("page_contract_changed");
    return cards;
  };
  const urls: string[] = [];
  for (let index = 0; index < count; index++) {
    const cards = await verifyPanel();
    const url = await sourcePopupUrl(page, cards[index]!, signal, Math.min(30_000, remaining()));
    const publicUrls = publicCitations([url], ["chatglm.cn", "www.chatglm.cn"]);
    if (publicUrls.length !== 1) throw new CollectorError("page_contract_changed");
    urls.push(publicUrls[0]!);
  }
  await verifyPanel();
  return publicCitations([...inlineUrls, ...urls], ["chatglm.cn", "www.chatglm.cn"]);
}

async function sourcePopupUrl(
  page: Page,
  card: Locator,
  signal: AbortSignal,
  timeoutMs: number,
): Promise<string> {
  const context = page.context();
  const deadline = Date.now() + timeoutMs;
  const existingPages = new Set(context.pages());
  const popups: Page[] = [];
  const initialUrls = new Map<Page, string>();
  const earlyRequests = new Set<Request>();
  const ownedPages = async () => {
    const owned = new Set(popups);
    for (const candidate of new Set([...initialUrls.keys(), ...context.pages()])) {
      if (!existingPages.has(candidate) && (await candidate.opener().catch(() => null)) === page)
        owned.add(candidate);
    }
    return [...owned];
  };
  let resolvePopup: ((popup: Page) => void) | undefined;
  const maybeResolve = () => {
    if (popups[0] && initialUrls.has(popups[0])) resolvePopup?.(popups[0]);
  };
  const recordRequest = (request: Request) => {
    if (!request.isNavigationRequest() || request.serviceWorker() || request.redirectedFrom())
      return;
    try {
      const frame = request.frame();
      const target = frame.page();
      if (existingPages.has(target) || frame !== target.mainFrame()) return;
      // Only browser navigation URL metadata; no request bodies, headers or responses.
      if (!initialUrls.has(target)) initialUrls.set(target, request.url());
      earlyRequests.delete(request);
      maybeResolve();
    } catch {
      // Navigation can precede frame creation. Retry correlation when popup appears,
      // and fail closed on timeout if Playwright never supplies a frame identity.
      earlyRequests.add(request);
    }
  };
  const onPopup = (popup: Page) => {
    popups.push(popup);
    for (const request of earlyRequests) recordRequest(request);
    maybeResolve();
  };
  context.on("request", recordRequest);
  page.on("popup", onPopup);
  let timer: ReturnType<typeof setTimeout> | undefined;
  let rejectPopup: ((error: Error) => void) | undefined;
  const abort = () => {
    rejectPopup?.(new CollectorError("stopped"));
    for (const popup of popups) void popup.close().catch(() => undefined);
  };
  signal.addEventListener("abort", abort, { once: true });
  try {
    signal.throwIfAborted();
    const pending = new Promise<Page>((resolve, reject) => {
      resolvePopup = resolve;
      rejectPopup = reject;
      timer = setTimeout(() => reject(new CollectorError("page_contract_changed")), timeoutMs);
    });
    const [popup] = await Promise.all([pending, card.click({ timeout: timeoutMs })]);
    signal.throwIfAborted();
    // Popup events may lag their navigation requests. Confirm one originating
    // page across both surfaces after the click, with a bounded delivery window.
    // Never re-click, and never accept just the first event of a multi-popup click.
    for (let checkpoint = 0; checkpoint < 3; checkpoint++) {
      if ((await ownedPages()).length !== 1) throw new CollectorError("page_contract_changed");
      if (Date.now() + 50 >= deadline) throw new CollectorError("page_contract_changed");
      await delay(50, undefined, { signal });
    }
    if ((await ownedPages()).length !== 1 || popups.length !== 1 || (await popup.opener()) !== page)
      throw new CollectorError("page_contract_changed");
    const url = initialUrls.get(popup);
    if (!url) throw new CollectorError("page_contract_changed");
    return url;
  } finally {
    if (timer) clearTimeout(timer);
    signal.removeEventListener("abort", abort);
    page.removeListener("popup", onPopup);
    context.removeListener("request", recordRequest);
    await Promise.all((await ownedPages()).map((popup) => popup.close().catch(() => undefined)));
  }
}
