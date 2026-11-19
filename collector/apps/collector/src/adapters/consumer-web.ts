import { readFile } from "node:fs/promises";
import { setTimeout as delay } from "node:timers/promises";
import type { LeasedTask } from "@aivc/contracts";
import type { Locator, Page } from "playwright";
import { z } from "zod";
import { buildWebQuestion, isWebQuestionEcho } from "../web-question.ts";
import {
  CollectorError,
  type CollectContext,
  type CollectionPhase,
  type CollectionResult,
  type FailureCode,
  type PlatformAdapter,
  type PlatformProbeResult,
  type ProbeOptions,
} from "../platform-adapter.ts";

const selector = z.string().min(1).max(500);
export const pageContractSchema = z
  .object({
    version: z.literal(2),
    origin: z.enum([
      "https://www.doubao.com",
      "https://wenxin.baidu.com",
      "https://chatglm.cn",
      "https://www.qianwen.com",
      "https://www.kimi.com",
      "https://chat.deepseek.com",
      "https://yuanbao.tencent.com",
      "https://chatgpt.com",
      "https://gemini.google.com",
    ]),
    // Approved sampling mode, not evidence that a response used network search.
    samplingMode: z.enum(["default_conversation", "quick_conversation", "web_search_conversation"]),
    reviewedAt: z.iso.datetime(),
    // Explicit operational assertion, NOT proof supplied by our fixture tests.
    verifiedOnAuthorizedPage: z.literal(true),
    selectors: z
      .object({
        authenticated: selector,
        newConversation: selector,
        composer: selector,
        conversationActive: selector,
        submit: selector,
        assistantMessage: selector,
        answerBody: selector,
        generating: selector,
        completed: selector,
        citations: selector,
        sourcesToggle: selector.optional(),
        // Optional search-keyword line inside the expanded sources block (Doubao).
        searchQueries: selector.optional(),
        // Optional elements inside the answer body left out of the Markdown answer (media
        // carousels, inline source chips). Only used by adapters with an answer formatter.
        answerIgnore: selector.optional(),
        loginRequired: selector,
        regionRestricted: selector,
        challengeRequired: selector,
        rateLimited: selector,
      })
      .strict(),
  })
  .strict();
export type ConsumerWebPageContract = z.infer<typeof pageContractSchema>;
// Result of an adapter-specific completed-sources reader. `titles` is position-aligned with
// `urls`; titles and searchQueries are best-effort and omitted when not reliably readable.
export interface CompletedSources {
  urls: string[];
  titles?: (string | null)[];
  searchQueries?: string[];
}

export async function loadPageContract(
  path: string | undefined,
  schema: z.ZodType<ConsumerWebPageContract> = pageContractSchema,
): Promise<ConsumerWebPageContract | undefined> {
  if (!path) return undefined;
  try {
    const contract = schema.parse(JSON.parse(await readFile(path, "utf8")));
    const age = Date.now() - Date.parse(contract.reviewedAt);
    if (age < -60_000 || age > 7 * 86_400_000) throw new Error();
    return contract;
  } catch {
    throw new CollectorError("page_contract_unverified");
  }
}
export function classifyLandingPage(text: string, url: string): FailureCode | undefined {
  if (
    /region.?ban|unsupported.?region|restricted.?region/i.test(url) ||
    /所在地区|所在区域|暂不支持.*地区|not available in your (country|region)|region.*not supported/i.test(
      text,
    )
  )
    return "region_restricted";
  if (
    /验证码|安全验证|完成验证|访问验证|滑动验证|验证失败|captcha|verify you are human/i.test(text)
  )
    return "challenge_required";
  if (/请求过于频繁|稍后再试|操作频繁|too many requests/i.test(text)) return "rate_limited";
  if (/登录后使用|请先登录|登录豆包|log in to continue|sign in to continue/i.test(text))
    return "login_required";
  return undefined;
}
export function publicCitations(
  urls: string[],
  internalHosts = ["www.doubao.com", "doubao.com"],
): string[] {
  const citations: string[] = [];
  for (const value of urls) {
    try {
      const url = new URL(value);
      const host = url.hostname.toLowerCase();
      if (
        !["http:", "https:"].includes(url.protocol) ||
        url.username ||
        url.password ||
        !host.includes(".") ||
        host.endsWith(".invalid") ||
        host.endsWith(".local") ||
        host.endsWith(".localhost") ||
        host === "localhost" ||
        internalHosts.includes(host) ||
        /^[\d.]+$/.test(host) ||
        host.includes(":") ||
        host.startsWith("[")
      )
        continue;
      if (!citations.includes(url.href)) citations.push(url.href);
    } catch {
      /* Not a public URL. */
    }
  }
  if (citations.length > 100) throw new CollectorError("page_contract_changed");
  return citations;
}
async function visible(locator: Locator): Promise<boolean> {
  return (await locator.count()) === 1 && locator.isVisible();
}
async function readFilledComposer(composer: Locator): Promise<string> {
  return composer.inputValue().catch(() =>
    composer.evaluate((element) => {
      // Paragraph editors (live Doubao) keep one <p> per line, an empty <p> for a blank
      // line. Selected text separates paragraphs by a blank line, so it cannot tell
      // [a, "", b] from [a, b]; read the lines themselves, minus zero-width placeholders.
      type ComposerNode = { nodeType: number; tagName?: string; textContent: string | null };
      const children = Array.from(
        (element as unknown as { childNodes: ArrayLike<ComposerNode> }).childNodes,
      );
      const paragraphs = children.filter((node) => node.nodeType === 1);
      if (
        paragraphs.length > 0 &&
        children.every(
          (node) =>
            (node.nodeType === 1 && node.tagName === "P") ||
            (node.nodeType === 3 && !node.textContent?.trim()),
        )
      )
        return paragraphs
          .map((node) => (node.textContent ?? "").replace(/\uFEFF/gu, ""))
          .join("\n");
      // Chromium fill() inserts block elements for contenteditable newlines. innerText
      // adds an extra newline for an empty block; native selected text preserves it.
      const document = element.ownerDocument;
      const selection = document.getSelection();
      if (!selection) throw new Error("composer_selection_unavailable");
      const saved = Array.from({ length: selection.rangeCount }, (_, index) =>
        selection.getRangeAt(index).cloneRange(),
      );
      const range = document.createRange();
      range.selectNodeContents(element);
      try {
        selection.removeAllRanges();
        selection.addRange(range);
        return selection.toString();
      } finally {
        selection.removeAllRanges();
        for (const previous of saved) selection.addRange(previous);
      }
    }),
  );
}

export class ConsumerWebAdapter implements PlatformAdapter {
  constructor(
    private readonly page: Page,
    private readonly contract: ConsumerWebPageContract | undefined,
    readonly platformCode:
      | "doubao"
      | "ernie"
      | "zhipu_qingyan"
      | "qwen"
      | "kimi"
      | "deepseek"
      | "tencent_yuanbao"
      | "chatgpt"
      | "gemini",
    private readonly entryUrl: string,
    private readonly internalHosts: string[],
    private readonly timing = { pollMs: 500, stableMs: 2_000, timeoutMs: 180_000 },
    private readonly citationAttribute?: string,
    private readonly multipleAnswerBodies = false,
    private readonly probeTimeoutMs = 5_000,
    private readonly completedCitations?: (
      message: Locator,
      signal: AbortSignal,
      deadline: number,
      inlineUrls: string[],
    ) => Promise<string[] | CompletedSources>,
    private readonly emptyComposerPattern?: RegExp,
    private readonly newConversationNoWait = false,
    // Some approved pages (currently ChatGPT) have no durable, answer-local
    // completion control. For those, absence of the generating control plus
    // a stable assistant body is the reviewed completion contract.
    private readonly requiresCompletionMarker = true,
    // Optional formatted answer (Doubao: Markdown) read once the plain answer is complete and
    // stable. Best effort: undefined, empty or a throw keeps the plain innerText answer.
    private readonly answerFormatter?: (
      message: Locator,
      timeoutMs: number,
    ) => Promise<string | undefined>,
  ) {
    page.setDefaultTimeout(5_000);
    page.setDefaultNavigationTimeout(30_000);
  }
  // Completion, stability and source checks always use the plain innerText answer; only the
  // returned text is formatted. The prompt-echo guard applies to whichever text is returned.
  private async finalAnswerText(
    message: Locator,
    plain: string,
    questionText: string,
    signal: AbortSignal,
    deadline: number,
  ): Promise<string> {
    let formatted: string | undefined;
    if (this.answerFormatter) {
      try {
        const timeoutMs = Math.max(1, Math.min(5_000, deadline - Date.now()));
        formatted = (await this.answerFormatter(message, timeoutMs))?.trim();
      } catch {
        formatted = undefined;
      }
      signal.throwIfAborted();
    }
    const answerText = formatted || plain;
    if (isWebQuestionEcho(answerText, questionText))
      throw new CollectorError("page_contract_changed");
    return answerText;
  }
  private async runAbortable<T>(signal: AbortSignal, operation: () => Promise<T>): Promise<T> {
    signal.throwIfAborted();
    const stop = () => {
      void this.page.close().catch(() => undefined);
    };
    signal.addEventListener("abort", stop, { once: true });
    try {
      return await operation();
    } catch (error) {
      if (signal.aborted)
        throw signal.reason instanceof CollectorError
          ? signal.reason
          : new CollectorError("stopped");
      if (error instanceof CollectorError) throw error;
      throw new CollectorError("page_contract_changed");
    } finally {
      signal.removeEventListener("abort", stop);
    }
  }
  async probe(signal: AbortSignal, options: ProbeOptions = {}): Promise<PlatformProbeResult> {
    return this.runAbortable(signal, async () => {
      // A resident collector probes before every lease (design §12.3). When the page is already
      // on the entry origin it is checked in place: reviewed markers all visible -> ready, and a
      // visible challenge is reported without reloading the page under the operator. Any other
      // state (or another origin, or a fresh about:blank page) takes the full navigation probe.
      // Page text is not classified here: a finished answer may legitimately mention captchas.
      // Opt-in only: without a session profile every probe still navigates (unchanged flow).
      if (options.inPlace && this.contract && this.onEntryOrigin()) {
        const status = await this.readiness().catch(() => undefined);
        if (status === "ready" || status === "challenge_required") return { status };
        signal.throwIfAborted();
      }
      await this.page.goto(this.entryUrl, { waitUntil: "domcontentloaded" });
      if (await this.challengeVisible()) return { status: "challenge_required" };
      const classified = classifyLandingPage(
        await this.page.locator("body").innerText(),
        this.page.url(),
      );
      if (classified) return { status: classified };
      if (new URL(this.page.url()).origin !== new URL(this.entryUrl).origin)
        return { status: "page_contract_changed" };
      if (!this.contract) return { status: "page_contract_unverified" };
      // The authenticated UI can hydrate after DOMContentLoaded. Never lease
      // until all reviewed markers render; blockers still stop this wait.
      const deadline = Date.now() + Math.min(this.probeTimeoutMs, this.timing.timeoutMs);
      while (Date.now() < deadline) {
        signal.throwIfAborted();
        const status = await this.readiness();
        if (status !== "page_contract_changed") return { status };
        await delay(this.timing.pollMs, undefined, { signal });
      }
      return { status: "page_contract_changed" };
    });
  }
  private onEntryOrigin(): boolean {
    try {
      return new URL(this.page.url()).origin === new URL(this.entryUrl).origin;
    } catch {
      return false;
    }
  }
  private async challengeVisible(): Promise<boolean> {
    // Only inspect iframe visibility, never its document, challenge or secrets.
    for (const frame of await this.page.locator("#captcha_container iframe").all()) {
      if (await frame.isVisible()) return true;
    }
    return false;
  }
  private async blockers(): Promise<FailureCode | undefined> {
    if (await this.challengeVisible()) return "challenge_required";
    if (!this.contract) return "page_contract_unverified";
    for (const [key, code] of [
      ["regionRestricted", "region_restricted"],
      ["challengeRequired", "challenge_required"],
      ["rateLimited", "rate_limited"],
      ["loginRequired", "login_required"],
    ] as const) {
      if (await this.page.locator(this.contract.selectors[key]).isVisible()) return code;
    }
    return undefined;
  }
  private async readiness(): Promise<"ready" | FailureCode> {
    const blocker = await this.blockers();
    if (blocker) return blocker;
    if (!this.contract) return "page_contract_unverified";
    const selectors = this.contract.selectors;
    for (const key of [
      "authenticated",
      "newConversation",
      "composer",
      "conversationActive",
    ] as const) {
      if (!(await visible(this.page.locator(selectors[key])))) return "page_contract_changed";
    }
    return "ready";
  }
  // A visible challenge either fails immediately (no waiter) or waits for the operator. Only
  // visibility is polled; the challenge itself is never touched. Returns the time spent waiting.
  private async handleBlocker(
    blocker: FailureCode,
    phase: CollectionPhase,
    context: CollectContext | undefined,
    signal: AbortSignal,
  ): Promise<number> {
    const waiter = context?.challengeWaiter;
    if (blocker !== "challenge_required" || !waiter) throw new CollectorError(blocker);
    const started = Date.now();
    const after = await waiter.wait({
      phase,
      pollMs: this.timing.pollMs,
      signal,
      check: () => this.blockers(),
    });
    signal.throwIfAborted();
    if (after) throw new CollectorError(after);
    return Date.now() - started;
  }
  // Re-run the pre-send conditions after a cleared challenge: no assistant answer, a visible
  // editable composer in the selected conversation mode, holding either nothing (fill again)
  // or exactly the outgoing question (submit). Anything else is an unknown page state.
  private async recheckPreSend(outgoingQuestion: string): Promise<"empty" | "filled"> {
    const selectors = this.contract!.selectors;
    await this.page.locator(selectors.assistantMessage).waitFor({ state: "hidden" });
    const composer = this.page.locator(selectors.composer);
    if (
      !(await visible(composer)) ||
      !(await composer.isEditable()) ||
      !(await visible(this.page.locator(selectors.conversationActive)))
    )
      throw new CollectorError("page_contract_changed");
    const value = await composer.inputValue().catch(() => composer.innerText());
    if (!value.trim() || this.emptyComposerPattern?.test(value)) return "empty";
    if ((await readFilledComposer(composer)) === outgoingQuestion) return "filled";
    throw new CollectorError("page_contract_changed");
  }
  async collect(
    task: LeasedTask,
    signal: AbortSignal,
    context?: CollectContext,
  ): Promise<CollectionResult> {
    return this.runAbortable(signal, async () => {
      if (!this.contract) throw new CollectorError("page_contract_unverified");
      let state = await this.readiness();
      if (state === "challenge_required") {
        await this.handleBlocker(state, "pre_send", context, signal);
        state = await this.readiness();
      }
      if (state !== "ready") throw new CollectorError(state);
      const selectors = this.contract.selectors;
      const newConversation = this.page.locator(selectors.newConversation);
      if (this.newConversationNoWait) {
        await newConversation.evaluate((element) =>
          (element as unknown as { click(): void }).click(),
        );
        await delay(500, undefined, { signal });
      } else {
        await newConversation.click();
      }
      // No old answer and an empty composer are required before any external prompt.
      await this.page.locator(selectors.assistantMessage).waitFor({ state: "hidden" });
      const composer = this.page.locator(selectors.composer);
      if (
        !(await visible(composer)) ||
        !(await composer.isEditable()) ||
        !(await visible(this.page.locator(selectors.conversationActive)))
      )
        throw new CollectorError("page_contract_changed");
      const value = await composer.inputValue().catch(() => composer.innerText());
      if (value.trim() && !this.emptyComposerPattern?.test(value))
        throw new CollectorError("page_contract_changed");
      const outgoingQuestion = buildWebQuestion(task.questionText);
      let filled = false;
      while (true) {
        if (!filled) {
          const beforeFill = await this.blockers();
          if (beforeFill) {
            await this.handleBlocker(beforeFill, "pre_send", context, signal);
            filled = (await this.recheckPreSend(outgoingQuestion)) === "filled";
            continue;
          }
          await composer.fill(outgoingQuestion);
          const filledText = await readFilledComposer(composer);
          if (filledText !== outgoingQuestion) throw new CollectorError("page_contract_changed");
          filled = true;
        }
        signal.throwIfAborted();
        const beforeSubmit = await this.blockers();
        if (!beforeSubmit) break;
        await this.handleBlocker(beforeSubmit, "pre_send", context, signal);
        filled = (await this.recheckPreSend(outgoingQuestion)) === "filled";
      }
      if (!(await visible(this.page.locator(selectors.submit))))
        throw new CollectorError("page_contract_changed");
      context?.onSubmitted?.();
      await this.page.locator(selectors.submit).click();
      let previous = "";
      let stableSince = Date.now();
      let deadline = Date.now() + this.timing.timeoutMs;
      let expectedSources: number | undefined;
      let sourcesDeadline = deadline;
      while (Date.now() < deadline) {
        signal.throwIfAborted();
        const blocked = await this.blockers();
        if (blocked) {
          // The question is already sent: wait, then keep polling the same answer. Time spent
          // waiting does not count against the answer deadline.
          const waited = await this.handleBlocker(blocked, "post_send", context, signal);
          deadline += waited;
          if (expectedSources !== undefined) sourcesDeadline += waited;
          previous = "";
          stableSince = Date.now();
          continue;
        }
        if (expectedSources !== undefined && Date.now() >= sourcesDeadline)
          throw new CollectorError("page_contract_changed");
        const message = this.page.locator(selectors.assistantMessage);
        if (await visible(message)) {
          const body = message.locator(selectors.answerBody);
          const chunks: string[] = [];
          if (this.multipleAnswerBodies) {
            for (const chunk of await body.all()) {
              if (await chunk.isVisible()) chunks.push(await chunk.innerText());
            }
            if (!chunks.length) {
              if (await visible(message.locator(selectors.completed))) {
                // Completion may render after the earlier body query. Recheck
                // before classifying this non-atomic DOM snapshot as missing body.
                let bodyNowVisible = false;
                for (const chunk of await body.all()) {
                  if (await chunk.isVisible()) {
                    bodyNowVisible = true;
                    break;
                  }
                }
                if (!bodyNowVisible) throw new CollectorError("page_contract_changed");
              }
              await delay(this.timing.pollMs, undefined, { signal });
              continue;
            }
          } else {
            if (!(await visible(body))) throw new CollectorError("page_contract_changed");
            chunks.push(await body.innerText());
          }
          const answerText = chunks.join("\n").replace(/\r\n/g, "\n").trim();
          const citations: string[] = [];
          for (const link of await message.locator(selectors.citations).all()) {
            if (await link.isVisible()) {
              if (this.citationAttribute) {
                const data = await link.getAttribute(this.citationAttribute);
                try {
                  const citation = z.object({ link: z.url() }).parse(JSON.parse(data ?? ""));
                  citations.push(citation.link);
                } catch {
                  throw new CollectorError("page_contract_changed");
                }
              } else {
                const href = await link.getAttribute("href");
                if (href) citations.push(href);
              }
            }
          }
          const urls = publicCitations(citations, this.internalHosts);
          const snapshot = JSON.stringify([answerText, urls]);
          if (snapshot !== previous) {
            previous = snapshot;
            stableSince = Date.now();
          }
          const complete =
            !this.requiresCompletionMarker || (await visible(message.locator(selectors.completed)));
          const generating = await this.page.locator(selectors.generating).isVisible();
          if (!complete || generating) stableSince = Date.now();
          if (
            answerText &&
            complete &&
            !generating &&
            Date.now() - stableSince >= this.timing.stableMs
          ) {
            // User messages can use the same Markdown renderer as assistant
            // messages. A broad manifest must never publish the prompt itself.
            if (isWebQuestionEcho(answerText, task.questionText))
              throw new CollectorError("page_contract_changed");
            if (this.completedCitations) {
              const read = await this.completedCitations(message, signal, deadline, urls);
              const sources: CompletedSources = Array.isArray(read) ? { urls: read } : read;
              signal.throwIfAborted();
              const blocked = await this.blockers();
              if (blocked) throw new CollectorError(blocked);
              if (Date.now() >= deadline) throw new CollectorError("collection_timeout");
              // Format before the final recheck so it also proves the body did not change.
              const finalText = await this.finalAnswerText(
                message,
                answerText,
                task.questionText,
                signal,
                deadline,
              );
              const finalChunks: string[] = [];
              for (const chunk of await message.locator(selectors.answerBody).all())
                if (await chunk.isVisible()) finalChunks.push(await chunk.innerText());
              if (
                new URL(this.page.url()).origin !== this.contract.origin ||
                !(await visible(message)) ||
                (this.requiresCompletionMarker &&
                  !(await visible(message.locator(selectors.completed)))) ||
                (await this.page.locator(selectors.generating).isVisible()) ||
                finalChunks.join("\n").replace(/\r\n/g, "\n").trim() !== answerText
              )
                throw new CollectorError("page_contract_changed");
              return {
                answerText: finalText,
                citations: sources.urls,
                ...(sources.titles ? { citationTitles: sources.titles } : {}),
                ...(sources.searchQueries ? { searchQueries: sources.searchQueries } : {}),
                completedAt: new Date().toISOString(),
              };
            }
            if (expectedSources === undefined && selectors.sourcesToggle) {
              const toggle = message.locator(selectors.sourcesToggle);
              if ((await toggle.count()) > 1) throw new CollectorError("page_contract_changed");
              if (await visible(toggle)) {
                const caption = await toggle.innerText();
                const counts = [...caption.matchAll(/参考\s*(\d+)\s*篇资料/gu)];
                const count = Number(counts[0]?.[1]);
                // This observed panel has a positive source count. Without it,
                // a collapsed/loading panel cannot safely mean zero citations.
                if (counts.length !== 1 || !Number.isSafeInteger(count) || count < 1 || count > 100)
                  throw new CollectorError("page_contract_changed");
                expectedSources = count;
                sourcesDeadline = Math.min(deadline, Date.now() + 5_000);
                await toggle.click({ timeout: Math.max(1, sourcesDeadline - Date.now()) });
                previous = "";
                stableSince = Date.now();
                continue;
              }
            }
            if (expectedSources === undefined || urls.length === expectedSources) {
              return {
                answerText: await this.finalAnswerText(
                  message,
                  answerText,
                  task.questionText,
                  signal,
                  deadline,
                ),
                citations: urls,
                completedAt: new Date().toISOString(),
              };
            }
          }
        }
        await delay(this.timing.pollMs, undefined, { signal });
      }
      throw new CollectorError(
        expectedSources === undefined ? "collection_timeout" : "page_contract_changed",
      );
    });
  }
}
