import { setTimeout as delay } from "node:timers/promises";
import type { Locator, Page } from "playwright";
import { z } from "zod";
import {
  type CompletedSources,
  ConsumerWebAdapter,
  pageContractSchema as sharedSchema,
  loadPageContract as loadContract,
  publicCitations,
} from "./consumer-web.ts";
import { CollectorError } from "../platform-adapter.ts";
import { readAnswerMarkdown } from "./answer-markdown.ts";
export { classifyLandingPage, publicCitations } from "./consumer-web.ts";
export { answerToMarkdown, readAnswerMarkdown, readAnswerPlainText } from "./answer-markdown.ts";

export const DOUBAO_URL = "https://www.doubao.com/chat/";
export const pageContractSchema = sharedSchema.extend({
  origin: z.literal("https://www.doubao.com"),
  samplingMode: z.literal("default_conversation"),
});
export type DoubaoPageContract = z.infer<typeof pageContractSchema>;
export async function loadPageContract(path: string | undefined) {
  const contract = await loadContract(path, pageContractSchema);
  return contract ? pageContractSchema.parse(contract) : undefined;
}
export class DoubaoAdapter extends ConsumerWebAdapter {
  constructor(
    page: Page,
    contract?: DoubaoPageContract,
    timing?: { pollMs: number; stableMs: number; timeoutMs: number },
  ) {
    // 2026-10-21 现场复审：豆包回答先渲染 receive_message 与 message_loading / 搜索块，正文
    // message_text_content 稍后才出现。按多正文块模式等待正文，而不是把短暂缺失判为合同变化。
    super(
      page,
      contract,
      "doubao",
      DOUBAO_URL,
      ["www.doubao.com", "doubao.com"],
      timing,
      undefined,
      true,
      5_000,
      contract
        ? (message, signal, deadline, inlineUrls) =>
            readDoubaoSources(message, contract, signal, deadline, inlineUrls)
        : undefined,
      undefined,
      false,
      true,
      // R8：回答正文按页面结构转为 Markdown（剔除 answerIgnore 命中的媒体与来源角标）；
      // 转换为空或失败时沿用原 innerText 纯文本，完成/稳定判定仍只看纯文本。
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

const DOUBAO_HOSTS = ["www.doubao.com", "doubao.com"];
// 标题与关键词只是附加信息：留出余量，绝不因读取它们而让整题超时或失败。
const METADATA_MARGIN_MS = 50;
const METADATA_READ_TIMEOUT_MS = 1_000;

// 2026-10-21 现场截图：展开"参考 N 篇资料"后每条来源链接文本即来源标题，前面可能带"1."编号。
// 折叠空白、去掉锚点内的前导编号（不误删"3.5 寸"这类小数）、截断到 512 个字符，空串记为 null。
export function normalizeCitationTitle(text: string): string | null {
  let title = text
    .replace(/\s+/gu, " ")
    .trim()
    .replace(/^\d{1,3}\.(?!\d)\s*/u, "");
  if (title.length > 512) {
    title = title.slice(0, 512);
    const last = title.charCodeAt(title.length - 1);
    // 不留下半个代理对。
    if (last >= 0xd800 && last <= 0xdbff) title = title.slice(0, -1);
    title = title.trimEnd();
  }
  return title || null;
}

// 关键词行形如 “…”、“…”；没有中文引号时接受英文直引号。只有数量与标题声明的 N 一致才返回。
export function parseSearchQueries(text: string, claimed: number): string[] | undefined {
  let segments = [...text.matchAll(/“([^”]+)”/gu)].map((match) => match[1]!.trim());
  if (!segments.length)
    segments = [...text.matchAll(/"([^"]+)"/gu)].map((match) => match[1]!.trim());
  if (
    !Number.isSafeInteger(claimed) ||
    claimed < 1 ||
    claimed > 32 ||
    segments.length !== claimed ||
    segments.some((segment) => !segment || segment.length > 500)
  )
    return undefined;
  return segments;
}

function claimedSearchQueries(caption: string): number | undefined {
  const claims = [...caption.matchAll(/搜索\s*(\d+)\s*个关键词/gu)];
  const claimed = Number(claims[0]?.[1]);
  return claims.length === 1 && Number.isSafeInteger(claimed) ? claimed : undefined;
}

// 按 URL 取第一条可读标题，再按最终 URL 顺序对齐；读取失败只让对应标题为 null。
async function readCitationTitles(
  message: Locator,
  contract: DoubaoPageContract,
  urls: string[],
  until: number,
): Promise<(string | null)[]> {
  const titles = new Map<string, string>();
  try {
    for (const link of await visibleItems(message.locator(contract.selectors.citations))) {
      const timeout = () => Math.min(METADATA_READ_TIMEOUT_MS, until - Date.now());
      if (timeout() <= 0) break;
      const href = await link.getAttribute("href", { timeout: timeout() });
      const url = href ? publicCitations([href], DOUBAO_HOSTS)[0] : undefined;
      if (!url || titles.has(url) || timeout() <= 0) continue;
      const title = normalizeCitationTitle(await link.innerText({ timeout: timeout() }));
      if (title) titles.set(url, title);
    }
  } catch {
    /* 标题不可读：保留已读到的标题，其余为 null。 */
  }
  return urls.map((url) => titles.get(url) ?? null);
}

async function readSearchQueries(
  message: Locator,
  contract: DoubaoPageContract,
  caption: string,
  until: number,
): Promise<string[] | undefined> {
  const selector = contract.selectors.searchQueries;
  const claimed = claimedSearchQueries(caption);
  if (!selector || claimed === undefined) return undefined;
  try {
    const [line] = await visibleItems(message.locator(selector));
    const timeout = Math.min(METADATA_READ_TIMEOUT_MS, until - Date.now());
    if (!line || timeout <= 0) return undefined;
    return parseSearchQueries(await line.innerText({ timeout }), claimed);
  } catch {
    return undefined;
  }
}

// 2026-10-21 现场复审：豆包把"搜索 N 个关键词，参考 M 篇资料"展开后最多内联渲染 12 条来源链接，
// 剩余条目不在 DOM 中，也没有"展开更多"控件。因此允许"少于标题声明数"的部分来源，
// 仍拒绝多于声明数、标题不可解析、展开后毫无链接等合同异常；切换控件只点击一次。
// URL 规则保持严格；来源标题（与 urls 按位对齐）与搜索关键词只做尽力读取，失败时省略。
export async function readDoubaoSources(
  message: Locator,
  contract: DoubaoPageContract,
  signal: AbortSignal,
  deadline: number,
  inlineUrls: string[],
): Promise<CompletedSources> {
  const selectors = contract.selectors;
  const until = deadline - METADATA_MARGIN_MS;
  const inlineOnly = async (): Promise<CompletedSources> => ({
    urls: inlineUrls,
    titles: await readCitationTitles(message, contract, inlineUrls, until),
  });
  if (!selectors.sourcesToggle) return inlineOnly();
  const toggles = await visibleItems(message.locator(selectors.sourcesToggle));
  if (!toggles.length) return inlineOnly();
  if (toggles.length !== 1) throw new CollectorError("page_contract_changed");
  const toggle = toggles[0]!;
  const caption = await toggle.innerText();
  const counts = [...caption.matchAll(/参考\s*(\d+)\s*篇资料/gu)];
  const claimed = Number(counts[0]?.[1]);
  if (counts.length !== 1 || !Number.isSafeInteger(claimed) || claimed < 1 || claimed > 100)
    throw new CollectorError("page_contract_changed");
  const remaining = () => {
    signal.throwIfAborted();
    const ms = deadline - Date.now();
    if (ms <= 0) throw new CollectorError("collection_timeout");
    return ms;
  };
  const finish = async (urls: string[]): Promise<CompletedSources> => {
    const titles = await readCitationTitles(message, contract, urls, until);
    const searchQueries = await readSearchQueries(message, contract, caption, until);
    return { urls, titles, ...(searchQueries ? { searchQueries } : {}) };
  };
  await toggle.click({ timeout: Math.min(5_000, remaining()) });
  // 在采集截止前留出余量；稳定判定窗口按剩余时间缩放，正式运行为 1.5 秒。
  const window = Math.min(deadline - 100, Date.now() + 5_000);
  const settleMs = Math.min(1_500, Math.max(50, Math.floor((window - Date.now()) / 3)));
  let previous = -1;
  let stableSince = Date.now();
  while (Date.now() < window) {
    remaining();
    const hrefs: string[] = [];
    for (const link of await visibleItems(message.locator(selectors.citations))) {
      const href = await link.getAttribute("href");
      if (href) hrefs.push(href);
    }
    const urls = publicCitations(hrefs, DOUBAO_HOSTS);
    if (urls.length > claimed) throw new CollectorError("page_contract_changed");
    if (urls.length === claimed) return finish(urls);
    if (urls.length !== previous) {
      previous = urls.length;
      stableSince = Date.now();
    } else if (urls.length >= 1 && Date.now() - stableSince >= settleMs) {
      return finish(urls);
    }
    await delay(100, undefined, { signal });
  }
  if (previous >= 1) {
    const hrefs: string[] = [];
    for (const link of await visibleItems(message.locator(selectors.citations))) {
      const href = await link.getAttribute("href");
      if (href) hrefs.push(href);
    }
    return finish(publicCitations(hrefs, DOUBAO_HOSTS));
  }
  throw new CollectorError("page_contract_changed");
}
