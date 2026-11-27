import { cardNodes, cardTexts, fitCard, wholeCards } from "./cards.ts";
import { randomUUID } from "node:crypto";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { ResolvedBinding } from "./bindings.ts";
import { checkBindings, type CheckReason, type LoginProbe } from "./check.ts";
import {
  isQuestionBubble,
  sourcePaths,
  type AppContract,
  type ContractSource,
  type ContractStep,
  type SourceLinks,
  type SourcePath,
} from "./contract.ts";
import type { DeviceActual, DeviceInspector } from "./devices.ts";
import {
  DriverError,
  type DeviceDriver,
  type DriverProvider,
  type Selector,
  type UiElement,
} from "./driver.ts";
import {
  fileEvidence,
  overallCitations,
  type AppRecord,
  type CitationLink,
  type CitationList,
  type EvidenceIssue,
  type EvidenceStore,
  type FileEvidence,
  type LedgerEntry,
  type LinkIssue,
} from "./evidence.ts";
import { AppChannelError, AppSetupError, type AppFailureCode } from "./failure.ts";
import {
  descendants,
  findNodes,
  isInside,
  isReadable,
  matchesSelector,
  mergeOverlap,
  parseHierarchy,
  type Bounds,
  type UiNode,
  layoutSignature,
  mergeWindows,
} from "./hierarchy.ts";
import { restoreLink } from "./links.ts";
import type { RateGate } from "./rate-gate.ts";
import type { ActiveRecording, RecordingOutcome, ScreenRecorder } from "./recorder.ts";
import { realSleep, timestampPair, type Sleep } from "./time.ts";

// One collection attempt on one binding, driven by the operator-reviewed contract (design §6):
//
//   pre-check -> answer ID -> recording -> App -> popups -> blockers -> new conversation
//   -> fill and read back -> ledger -> send -> wait for completion -> read answer and sources
//   -> screenshot and hierarchy -> stop recording -> record.json -> home screen
//
// Nothing is guessed: a required view that is missing is a contract change. Login and
// verification screens are only ever looked at. Every wait is a fixed poll interval; there is no
// input timing of any kind.
//
// The text sent is the approved question as it is (design §16.1); the Web channel's prefix is
// not used here. The answer is what makes a sample: its sources are read as far as the App lets
// them be read, and a source read that fails is recorded as such without failing the attempt
// (design §16.2). The recording step does nothing when recording is switched off (design §16.3).

type Log = (line: Record<string, unknown>) => void;

export interface AttemptRequest {
  batchId: string;
  binding: ResolvedBinding;
  questionText: string;
  // Aborts when the run has been asked to stop and the grace period is over: the attempt then
  // ends as a failure with its record.
  signal: AbortSignal;
  // Aborts as soon as the run has been asked to stop, while the grace period still runs. Work
  // that may be cut short without losing the sample (reading the links of its sources) ends
  // then, so that a sent question is not lost to the end of the grace period.
  stopping?: AbortSignal;
  // Called once the question is counted as sent (ledger line written), right before the tap.
  onSent: () => void;
  // Control-plane mode (`serve`): the answer ID chosen before the attempt, so a restart can find
  // the attempt in the ledger, and the leased task the record belongs to.
  answerId?: string;
  task?: { taskId: string; leaseVersion: number };
  onSendIntent?: () => Promise<void>;
  onConfirmedSend?: (sentAt: string) => Promise<void>;
}
export interface AttemptOutcome {
  // null: the pre-check failed, so no attempt was made and no record exists.
  answerId: string | null;
  status: AppRecord["status"];
  failureCode: AppFailureCode | null;
  phase: AppRecord["phase"];
  evidenceIssue?: EvidenceIssue;
}
export type AttemptRunner = (request: AttemptRequest) => Promise<AttemptOutcome>;

export interface AdapterTimings {
  // Cold start: how long to wait for a readable hierarchy, and how often to look.
  readyTimeoutMs: number;
  readyPollMs: number;
  // Default wait of a contract step and of every required view.
  stepTimeoutMs: number;
  lookupPollMs: number;
  // Upper bound from the send tap to the completion marker.
  completionTimeoutMs: number;
  completionPollMs: number;
}
export const DEFAULT_TIMINGS: AdapterTimings = {
  readyTimeoutMs: 90_000,
  readyPollMs: 3_000,
  stepTimeoutMs: 10_000,
  lookupPollMs: 500,
  completionTimeoutMs: 300_000,
  completionPollMs: 3_000,
};

export interface AppAdapterOptions {
  store: EvidenceStore;
  gate?: RateGate;
  recorder: Pick<ScreenRecorder, "start">;
  inspector: Pick<DeviceInspector, "inspectAll" | "installedAppVersion">;
  contracts: Pick<ContractSource, "verified">;
  drivers: Pick<DriverProvider, "open">;
  scriptRevision: string;
  timings?: Partial<AdapterTimings>;
  now?: () => Date;
  sleep?: Sleep;
  newAnswerId?: () => string;
  log?: Log;
}

// Checked in this order; the first visible one names the failure.
const BLOCKER_ORDER = [
  "emulator_rejected",
  "challenge_required",
  "login_required",
  "rate_limited",
] as const;
// Android's own "isn't responding" / "has stopped" dialog.
const SYSTEM_DIALOG_CLOSE: Selector = { id: "android:id/aerr_close" };
const SYSTEM_DIALOG_WAIT: Selector = { id: "android:id/aerr_wait" };
// While an App starts, the system dialog's "wait" is chosen at most this often before the App
// counts as crashed: on a loaded host a cold start regularly outlasts the system's patience and
// recovers on its own (stage-0 and stage-6 observations).
const STARTUP_WAIT_TAPS = 5;
const SNAPSHOT_ATTEMPTS = 3;
const REVEAL_ROUNDS = 10;
const CLIPBOARD_READS = 5;
// Swipes in a row that may leave the answer's hierarchy unchanged while scrolling back to the
// question before the list counts as stuck.
const ANSWER_UNMOVED_SWIPES = 8;
// Swipes in a row that must leave a source list unchanged before it counts as read to its end.
const SOURCE_LIST_END_SWIPES = 2;
// Closing a source panel with the back key: how often it is pressed at most, and how long each
// press is given to take effect before the screen is looked at again.
const PANEL_BACK_PRESSES = 3;
const PANEL_BACK_WAIT_MS = 2_000;
// Rows of a screen between source entry and source list that are opened when the contract does
// not say how many.
const VIA_ITEMS = 5;
// How long the views under Android's clipboard preview may take to come back after a copy.
const COPY_SETTLE_MS = 10_000;
// Reading the link of every source (contract `links`). The time allowed for the links of one
// answer when the contract does not say; how long a tapped title is given to open its page
// (real device, Doubao 15.7.0: 0.55 to 0.65 s); how often the clipboard is read after a copy
// before it counts as unchanged.
const LINK_BUDGET_SECONDS = 180;
const PAGE_OPEN_MS = 5_000;
const LINK_CLIPBOARD_READS = 3;
// Back presses that may bring the App to the front again after a source or its share control
// took the screen to another package (design of task 11-24, §11.1).
const FOREIGN_BACK_PRESSES = 2;
const isWebLink = (text: string) => /^https?:\/\/\S+$/u.test(text);

// `cause` names the check that failed: a fixed word for the logs, never text from a screen.
const changed = (cause: string) => new AppChannelError("app_contract_changed", undefined, cause);
// Log fields for a failed driver command: its name and the protocol error code, nothing else.
const driverDetail = (error: unknown) =>
  error instanceof DriverError
    ? { command: error.command ?? null, errorName: error.errorName ?? null }
    : error instanceof AppChannelError && error.numbers
      ? { numbers: error.numbers }
      : {};
const squash = (text: string) => text.replace(/\s+/gu, "");
// What a view says: its text, or its content description where it has no text (an App that
// draws its own screen describes its views instead of giving them text).
const shown = (view: { text: string; desc: string }) => view.text || view.desc;
const isHierarchyProblem = (error: unknown) =>
  error instanceof DriverError || (error instanceof Error && error.message === "hierarchy_invalid");

type Citation = AppRecord["citations"][number];
const sameCitation = (a: Citation, b: Citation) =>
  a.title === b.title && a.site === b.site && a.url === b.url;
// Why the links of an answer's sources were not all read: the time was up, a stop was
// requested, a page or the panel behind it did not come back, or the list stopped moving before
// every source had come on screen.
// `driver_error`: a driver command failed during the read; what was read by then is kept.
type LinkStop =
  | "time_budget"
  | "stop_requested"
  | "page_not_closed"
  | "list_not_moving"
  | "driver_error";
interface LinkRead {
  elapsedMs: number;
  stoppedBy?: LinkStop;
  // With `driver_error`: the failed command and its protocol error code, nothing else.
  driver?: ReturnType<typeof driverDetail>;
  // The read did not end on the source panel (a page that stayed, or a panel that did not come
  // back): what is on screen is left to the caller's clean-up, and nothing is pressed for it.
  leftOpen: boolean;
  // Diagnostics (design of task 11-24, §12.1): one entry per screen of the list, the last
  // `MAX_LINK_SCREENS` of them, and how many earlier ones were dropped.
  screens: LinkScreen[];
  screensDropped: number;
  // With `list_not_moving`: which of its three conditions held (diagnostics, §12.1).
  listStop?: ListStop;
}
type ListStop = "no_titles" | "unmoved" | "screen_limit";
// What one screen of the list gave while its links were read: counts and positions only, never a
// title, a link or other text. `all`: title views seen; `whole`: those the list does not cut;
// `at`: the position in the list (from 1) of the first whole one, null where the screen does not
// fit the list read before; `next`: the first source not yet handled when the screen was taken
// (from 1); `opened`: sources opened from it; `waited`: the wait for the clipboard preview was
// made on it.
interface LinkScreen {
  all: number;
  whole: number;
  at: number | null;
  next: number;
  opened: number;
  waited: boolean;
}
// Screens kept for the log line; the stop is at the end, so the earliest ones are dropped.
const MAX_LINK_SCREENS = 30;
// What the read of one source path gave. `issue` is the fixed cause word of a read that failed.
// `links` only where the path reads the link of every source.
interface Sources {
  citations: Citation[];
  searchQueries: string[];
  status: CitationList["status"];
  issue?: string;
  links?: LinkRead;
}

// The answer text views of one hierarchy export.
function answerWindow(
  root: UiNode,
  answer: Extract<AppContract["answer"], { method: "hierarchy" }>,
  sentText: string,
) {
  const containers = findNodes(root, answer.container);
  if (containers.length === 0) return undefined;
  if (containers.length > 1 && (answer.container.pick ?? "only") === "only")
    throw changed("answer_container_ambiguous");
  const container = answer.container.pick === "last" ? containers.at(-1)! : containers[0]!;
  const excluded = answer.exclude.flatMap((selector) => findNodes(container, selector));
  const question = squash(sentText);
  const texts: string[] = [];
  let sawQuestion = false;
  for (const node of descendants(container)) {
    // Everything from the end marker on belongs to what follows the answer.
    if (answer.end && matchesSelector(node, answer.end)) break;
    const text = node.text.trim();
    if (text === "") continue;
    // The bubble showing what was sent. Apps re-space mixed CJK/Latin text, so whitespace is
    // not compared. The sent text is the bare question and an answer may repeat it as a line of
    // its own, so a view is the bubble only if it also meets the contract's bubble conditions,
    // and only the first such view of an export is.
    if (!sawQuestion && squash(text) === question && isQuestionBubble(node, answer.question)) {
      sawQuestion = true;
      // Whatever the list shows above the sent question is not its answer.
      texts.length = 0;
      continue;
    }
    if (excluded.some((skip) => skip === node || isInside(node, skip))) continue;
    texts.push(text);
  }
  // Where the views sit is part of the signature: an answer held in one long text view keeps
  // the same views and texts while it scrolls, and would otherwise look as if it had not moved
  // (real device, Doubao 15.7.0).
  return { texts, sawQuestion, bounds: container.bounds, signature: layoutSignature(container) };
}

// The card views of one export of the open panel. In a list of equally tall cards, a card that
// is clearly less tall than the usual one is cut by an edge of the list and is left out.
function sourceCards(root: UiNode, contract: SourcePath): UiNode[] {
  const cards = cardNodes(root, contract.cards?.from);
  return contract.cards?.sameHeight ? wholeCards(cards) : cards;
}
// Sources of one export of the open panel. A site or URL belongs to a title only when both sit
// in the same item: the highest ancestor of the title that holds no other title.
function citationItems(root: UiNode, contract: SourcePath): Citation[] {
  if (contract.cards) {
    const cards = contract.cards;
    return sourceCards(root, contract).flatMap((card) => {
      const fields = fitCard(cardTexts(card, cards.from), cards);
      if (!fields) return [];
      const url = fields.url ?? null;
      return [
        {
          title: fields.title!,
          site: fields.site ?? null,
          url: url !== null && /^https?:\/\/\S+$/u.test(url) ? url : null,
        },
      ];
    });
  }
  return titleItems(root, contract).map(({ item }) => item);
}
// The title views of one export of the open panel with the source each of them shows, in screen
// order (a source path with `itemTitle`).
function titleItems(root: UiNode, contract: SourcePath): { node: UiNode; item: Citation }[] {
  const titles = findNodes(root, contract.itemTitle!).filter((node) => node.text.trim() !== "");
  const inItem = (title: UiNode, selector: Selector | undefined): string | null => {
    if (selector === undefined) return null;
    let scope = title.parent;
    while (
      titles.length > 1 &&
      scope?.parent &&
      titles.every((other) => other === title || !isInside(other, scope!.parent!))
    )
      scope = scope.parent;
    if (!scope || titles.some((other) => other !== title && isInside(other, scope))) return null;
    return findNodes(scope, selector)[0]?.text.trim() || null;
  };
  return titles.map((title) => {
    const url = inItem(title, contract.itemUrl);
    return {
      node: title,
      item: {
        title: title.text.trim(),
        site: inItem(title, contract.itemSite),
        // Only what is shown as a link; nothing is completed.
        url: url !== null && /^https?:\/\/\S+$/u.test(url) ? url : null,
      },
    };
  });
}
// Of the title views of one export, those that the list does not cut. A view that an edge of the
// list cuts is reported with the part that shows, so it is shorter than the others where it
// touches that edge; a tap on it can land on the edge (real device, Doubao 15.7.0: a tap on a
// row at the bottom of the half-open panel opened nothing). Its source is opened from a later
// screen. The edge is that of the nearest view holding every title; without one, nothing is
// left out.
function uncutTitles<T extends { node: UiNode }>(titles: readonly T[]): T[] {
  const nodes = titles.map((title) => title.node);
  let list: UiNode | null = nodes[0]?.parent ?? null;
  while (list && !nodes.every((node) => isInside(node, list!))) list = list.parent;
  const edge = list?.bounds;
  const height = (node: UiNode) => (node.bounds ? node.bounds.bottom - node.bounds.top : 0);
  if (!edge || titles.length < 2) return [...titles];
  const tallest = Math.max(...nodes.map(height));
  return titles.filter(
    ({ node }) =>
      !node.bounds ||
      height(node) >= tallest ||
      (node.bounds.top > edge.top && node.bounds.bottom < edge.bottom),
  );
}
// Where the sources of one screen sit in the list read before: the position of the first, at
// or after `from` where the screen is found there, else the first position it is found at.
function alignWindow(
  items: readonly Citation[],
  window: readonly Citation[],
  from: number,
): number | undefined {
  if (window.length === 0) return undefined;
  const fits = (offset: number) =>
    window.every((item, index) => sameCitation(item, items[offset + index]!));
  for (const start of from > 0 ? [from, 0] : [0])
    for (let offset = start; offset + window.length <= items.length; offset += 1)
      if (fits(offset)) return offset;
  return undefined;
}
function searchQueries(root: UiNode, contract: SourcePath): string[] {
  const rule = contract.searchQueries;
  if (!rule) return [];
  let node: UiNode | undefined;
  if (rule.selector) node = findNodes(root, rule.selector)[0];
  else {
    const all = [...descendants(root)];
    const anchor = all.findIndex((item) => matchesSelector(item, rule.after!));
    if (anchor >= 0) node = all.slice(anchor + 1).find((item) => item.text.trim() !== "");
    if (
      node &&
      ((contract.itemTitle !== undefined && matchesSelector(node, contract.itemTitle)) ||
        rule.exclude?.some((selector) => matchesSelector(node!, selector)))
    )
      node = undefined;
  }
  const line = node?.text.trim();
  if (!line) return [];
  return (rule.separator ? line.split(rule.separator) : [line])
    .map((part) => part.trim())
    .filter(Boolean);
}
function unionBounds(nodes: readonly UiNode[]): Bounds | undefined {
  const boxes = nodes.flatMap((node) => (node.bounds ? [node.bounds] : []));
  if (boxes.length === 0) return undefined;
  return {
    left: Math.min(...boxes.map((box) => box.left)),
    top: Math.min(...boxes.map((box) => box.top)),
    right: Math.max(...boxes.map((box) => box.right)),
    bottom: Math.max(...boxes.map((box) => box.bottom)),
  };
}

// The contract applied to one driver session.
class Screen {
  // The screen between source entry and source list showed a row that leads to sources.
  private sourceRowSeen = false;
  // The screen between source entry and source list was opened. The way back from it and from
  // its lists is the back key, pressed once per screen when all goes well; a press too many
  // lands on the conversation and leaves it, with nothing left on screen that a source read
  // could still see. The caller therefore asks for the end of the answer afterwards.
  sourceStepsOpened = false;
  // Reading links ended away from a source panel (a source's page that stayed, or a panel that
  // did not come back) and was left by the clean-up: as after a screen in between, the caller
  // asks for the end of the answer afterwards.
  linkPageLeftOpen = false;
  // What the clipboard held after the last link was copied, where links were read: a copy of the
  // answer that gives this text did nothing.
  lastLink: string | undefined;
  // The attempt's record directory, once it exists: where the screen at a stop of the link read
  // is kept (design of task 11-24, §12.2). Set by the attempt, not by the contract.
  recordDirectory: string | undefined;
  // That screen and hierarchy, taken at the first such stop of the attempt.
  linkStop: AppRecord["linkStop"];

  constructor(
    readonly driver: DeviceDriver,
    readonly contract: AppContract,
    private readonly timings: AdapterTimings,
    private readonly now: () => Date,
    private readonly sleep: Sleep,
    private readonly signal?: AbortSignal,
    // A stop was requested; the grace period still runs (`AttemptRequest.stopping`).
    private readonly stopping?: AbortSignal,
  ) {}

  // A wait between two looks at the screen. An aborted run ends the attempt as a failure.
  async pause(ms: number): Promise<void> {
    if (this.signal?.aborted) throw new AppChannelError("collection_timeout");
    await this.sleep(ms, this.signal);
    if (this.signal?.aborted) throw new AppChannelError("collection_timeout");
  }
  async visible(selector: Selector): Promise<boolean> {
    return (await this.driver.find(selector)).length > 0;
  }
  // Which of several visible matches a step acts on. Without an explicit `pick` more than one
  // match is a contract change.
  private pick(elements: UiElement[], selector: Selector): UiElement {
    if (selector.pick === "first") return elements[0]!;
    if (selector.pick === "last") return elements.at(-1)!;
    if (elements.length > 1) throw changed("selector_ambiguous");
    return elements[0]!;
  }
  // More matches than the one view a selector names get the same time as a view that is not
  // there yet: a screen that is still changing shows what goes and what comes side by side for
  // a moment (real device, design §16.8: the hint above a composer was still there when the
  // send icon had come). Only matches that stay are a contract change. Nothing is picked from
  // them meanwhile.
  async waitFor(selector: Selector, timeoutMs: number): Promise<UiElement | undefined> {
    const deadline = this.now().getTime() + timeoutMs;
    for (;;) {
      const elements = await this.driver.find(selector);
      const expired = this.now().getTime() >= deadline;
      const several = elements.length > 1 && (selector.pick ?? "only") === "only";
      if (elements.length > 0 && (!several || expired)) return this.pick(elements, selector);
      if (expired) return undefined;
      await this.pause(this.timings.lookupPollMs);
    }
  }
  async waitGone(selector: Selector, timeoutMs: number): Promise<boolean> {
    const deadline = this.now().getTime() + timeoutMs;
    for (;;) {
      if (!(await this.visible(selector))) return true;
      if (this.now().getTime() >= deadline) return false;
      await this.pause(this.timings.lookupPollMs);
    }
  }
  // A view the contract says must be there. Before calling its absence a contract change, the
  // screen is checked for a state with its own class (crash, blocker) and for a declared popup.
  async require(selector: Selector, timeoutMs = this.timings.stepTimeoutMs): Promise<UiElement> {
    let element = await this.waitFor(selector, timeoutMs);
    if (!element) {
      await this.assertHealthy();
      if (await this.dismissPopups()) element = await this.waitFor(selector, timeoutMs);
    }
    if (!element) throw changed("required_view_missing");
    return element;
  }

  async runSteps(steps: readonly ContractStep[]): Promise<void> {
    for (const step of steps) {
      if ("tap" in step) {
        if (step.unless && (await this.visible(step.unless))) continue;
        if (step.optional) {
          const element = await this.waitFor(step.tap, step.timeoutMs ?? 0);
          if (element) await this.driver.tap(element);
        } else await this.driver.tap(await this.require(step.tap, step.timeoutMs));
      } else if ("waitFor" in step) await this.require(step.waitFor, step.timeoutMs);
      else if ("swipe" in step) await this.driver.swipe(step.swipe, step.count);
      else await this.driver.back();
    }
  }

  // One parsed hierarchy export. An empty or failing export is retried a bounded number of times.
  async snapshot(): Promise<UiNode> {
    let failure: unknown;
    for (let attempt = 1; attempt <= SNAPSHOT_ATTEMPTS; attempt += 1) {
      try {
        const root = parseHierarchy(await this.driver.hierarchy());
        if (isReadable(root)) return root;
        failure = undefined;
      } catch (error) {
        if (!isHierarchyProblem(error)) throw error;
        failure = error;
      }
      if (attempt < SNAPSHOT_ATTEMPTS) await this.pause(this.timings.lookupPollMs);
    }
    throw failure instanceof DriverError ? failure : changed("hierarchy_unreadable");
  }
  // After (re)starting an App its hierarchy can stay empty or time out for a while.
  async waitReadable(): Promise<void> {
    const deadline = this.now().getTime() + this.timings.readyTimeoutMs;
    for (;;) {
      let failure: unknown;
      try {
        if (isReadable(parseHierarchy(await this.driver.hierarchy()))) return;
      } catch (error) {
        if (!isHierarchyProblem(error)) throw error;
        failure = error;
      }
      if (this.now().getTime() >= deadline) {
        await this.assertHealthy();
        throw failure instanceof DriverError ? failure : changed("hierarchy_unreadable");
      }
      await this.pause(this.timings.readyPollMs);
    }
  }

  // The system "isn't responding" / "has stopped" dialog; `close` is its close button if shown.
  async systemDialog(): Promise<{ close?: UiElement; wait?: UiElement } | undefined> {
    const close = (await this.driver.find(SYSTEM_DIALOG_CLOSE))[0];
    const wait = (await this.driver.find(SYSTEM_DIALOG_WAIT))[0];
    return close || wait ? { close, wait } : undefined;
  }
  // Throws the class of whatever stops the attempt: a crashed or unresponsive App, another
  // package in front, or a declared blocker. Visibility only.
  async assertHealthy(): Promise<void> {
    if (await this.systemDialog())
      throw new AppChannelError("app_crashed", undefined, "system_dialog");
    const foreground = await this.driver.foregroundPackage();
    if (foreground !== null && foreground !== this.contract.package)
      throw new AppChannelError("app_crashed", undefined, "foreground_other");
    for (const code of BLOCKER_ORDER)
      for (const marker of this.contract.blockers[code])
        if (await this.visible(marker))
          throw new AppChannelError(code, undefined, "blocker_marker");
  }
  // Closes the popups the contract declares. Returns whether one was closed.
  async dismissPopups(): Promise<boolean> {
    let dismissed = false;
    for (const popup of this.contract.popups) {
      if (!(await this.visible(popup.marker))) continue;
      const dismiss = await this.waitFor(popup.dismiss, 0);
      if (!dismiss) throw changed("popup_dismiss_missing");
      await this.driver.tap(dismiss);
      if (!(await this.waitGone(popup.marker, this.timings.stepTimeoutMs)))
        throw changed("popup_not_dismissed");
      dismissed = true;
    }
    return dismissed;
  }

  // Until the App is in front, the launcher's hierarchy would pass as readable and the health
  // check would call a slow cold start a crash. A "not responding" dialog during the start is
  // answered with "wait" a bounded number of times; a dialog without that choice, or one still
  // showing when the time is up, is a crash and is left on screen for the failure evidence.
  private async waitForeground(): Promise<void> {
    const deadline = this.now().getTime() + this.timings.readyTimeoutMs;
    let waits = 0;
    for (;;) {
      const dialog = await this.systemDialog();
      const expired = this.now().getTime() >= deadline;
      if (dialog) {
        if (!dialog.wait || waits >= STARTUP_WAIT_TAPS || expired)
          throw new AppChannelError("app_crashed", undefined, "system_dialog");
        waits += 1;
        await this.driver.tap(dialog.wait);
      } else if ((await this.driver.foregroundPackage()) === this.contract.package) return;
      else if (expired) throw new AppChannelError("app_crashed", undefined, "startup_timeout");
      await this.pause(this.timings.readyPollMs);
    }
  }

  // Brings the App up and waits until it can be looked at, before anything is recorded. A cold
  // start while the guest's software video encoder is already running saturates the emulator,
  // and Android then reports the App as not responding (real devices: Kimi, Yuanbao and Doubao
  // right after a device start). The recording begins once the App is up; it still covers
  // everything from before the question is typed.
  async warmUp(): Promise<void> {
    await this.driver.activateApp(this.contract.package);
    await this.waitForeground();
    await this.waitReadable();
  }

  async open(): Promise<void> {
    await this.driver.activateApp(this.contract.package);
    await this.waitForeground();
    await this.waitReadable();
    await this.dismissPopups();
    await this.assertHealthy();
  }

  // Polls with targeted lookups until the completion marker shows. From then on every poll also
  // exports the hierarchy once, to see whether the screen still changes. For most Apps that is
  // two exports at the very end; an App that shows its marker mid-answer is exported once per
  // poll interval for the rest of the answer.
  async waitForCompletion(): Promise<void> {
    const { reveal, marker, generating, notices = [] } = this.contract.completion;
    const deadline = this.now().getTime() + this.timings.completionTimeoutMs;
    // The marker alone is not proof: some Apps show their answer controls while the answer is
    // still being written (real device, Yuanbao 2.92.0: the copy control was there mid-answer).
    // The screen must also be the same on two looks one poll apart.
    let settled: string | undefined;
    for (;;) {
      await this.assertHealthy();
      await this.runSteps(reveal);
      // A notice in place of the answer ends the wait at once instead of at the deadline. Only
      // here, after the question was sent: the same words in an older conversation say nothing
      // about this one. A declined question is an ordinary timeout for the control plane, so
      // another device may ask it; the cause tells it apart in the log.
      for (const notice of notices)
        if (await this.visible(notice.marker))
          throw notice.as === "rate_limited"
            ? new AppChannelError("rate_limited", undefined, "notice_rate_limited")
            : new AppChannelError("collection_timeout", undefined, "platform_declined");
      const stillGenerating = generating !== undefined && (await this.visible(generating));
      if (!stillGenerating && (await this.visible(marker))) {
        const layout = layoutSignature(await this.snapshot());
        if (layout === settled) return;
        settled = layout;
      } else settled = undefined;
      await this.dismissPopups();
      if (this.now().getTime() >= deadline) throw new AppChannelError("collection_timeout");
      await this.pause(this.timings.completionPollMs);
    }
  }
  // Brings the end of the answer back on screen after reading scrolled away from it.
  async revealEnd(): Promise<boolean> {
    const { reveal, marker } = this.contract.completion;
    for (let round = 0; round < REVEAL_ROUNDS; round += 1) {
      if (await this.visible(marker)) return true;
      if (reveal.length === 0) return false;
      await this.runSteps(reveal);
    }
    return this.visible(marker);
  }

  // After the App's copy control is used, Android shows its clipboard preview over the lower
  // part of the screen for a few seconds and the automation framework no longer sees the views
  // under it (real device, DeepSeek: the source entry was "not there" right after a copy). Wait
  // until the control is visible again; if it stays hidden the caller's own checks decide.
  async settleAfterCopy(): Promise<void> {
    const answer = this.contract.answer;
    if (answer.method === "copy") await this.waitFor(answer.copy, COPY_SETTLE_MS);
  }

  async readAnswer(sentText: string): Promise<{ text: string; snapshots: UiNode[] }> {
    const answer = this.contract.answer;
    if (answer.method === "copy") {
      await this.driver.tap(await this.require(answer.copy));
      for (let read = 1; ; read += 1) {
        const text = (await this.driver.readClipboard()).trim();
        if (text !== "" || read >= CLIPBOARD_READS) return { text, snapshots: [] };
        await this.pause(this.timings.lookupPollMs);
      }
    }
    const first = await this.snapshot();
    const snapshots = [first];
    let window = answerWindow(first, answer, sentText);
    if (!window) throw changed("answer_container_missing");
    let texts = window.texts;
    if (answer.stitch && !window.sawQuestion) {
      // Scroll back until the sent question shows: only then is the start of the answer known.
      // A list that stops moving before that, or needs more swipes than allowed, is never read
      // as a shortened answer.
      let reachedStart = false;
      let unmoved = 0;
      for (let swipe = 0; swipe < (answer.maxSwipes ?? 20) && !reachedStart; swipe += 1) {
        await this.driver.swipe("down", 1, window.bounds ?? undefined);
        const root = await this.snapshot();
        snapshots.push(root);
        const earlier = answerWindow(root, answer, sentText);
        if (!earlier) throw changed("answer_container_missing");
        // An unchanged hierarchy is not proof that the list did not move: an answer held in one
        // text view taller than the screen reports the same clipped bounds and the same text
        // wherever it is scrolled (real device, Doubao 15.7.0). Only several unchanged swipes in
        // a row count as a list that no longer moves.
        if (!earlier.sawQuestion && earlier.signature === window.signature) {
          unmoved += 1;
          if (unmoved >= ANSWER_UNMOVED_SWIPES) throw changed("answer_list_not_moving");
        } else unmoved = 0;
        texts = mergeOverlap(earlier.texts, texts);
        window = earlier;
        reachedStart = earlier.sawQuestion;
      }
      if (!reachedStart) throw changed("answer_start_not_reached");
    }
    return { text: texts.join("\n").trim(), snapshots };
  }
  // What can be read of an answer that did not complete. Never throws.
  async partialAnswer(sentText: string): Promise<string | null> {
    const answer = this.contract.answer;
    if (answer.method !== "hierarchy") return null;
    try {
      const window = answerWindow(await this.snapshot(), answer, sentText);
      return window?.texts.join("\n").trim() || null;
    } catch {
      return null;
    }
  }

  // Reads one source path of the contract, from looking for its entry to leaving its screens.
  // Throws when there is a source entry but its list cannot be read whole; the caller decides
  // what that means for the attempt.
  async readCitations(contract: SourcePath): Promise<Sources> {
    const none = { citations: [], searchQueries: [] };
    let entry = await this.waitFor(contract.entry, 0);
    if (!entry && (await this.revealEnd())) entry = await this.waitFor(contract.entry, 0);
    const { entrySwipe } = contract;
    // With `stopWhenUnmoved` the hierarchy is exported around every swipe; without it the
    // screen is only looked at for the entry.
    let layout: string | undefined;
    let unmoved = 0;
    for (let swipe = 0; !entry && swipe < (entrySwipe?.max ?? 0); swipe += 1) {
      if (entrySwipe!.stopWhenUnmoved !== undefined)
        layout ??= layoutSignature(await this.snapshot());
      await this.driver.swipe(entrySwipe!.swipe, 1);
      entry = await this.waitFor(contract.entry, 0);
      if (entry || entrySwipe!.stopWhenUnmoved === undefined) continue;
      const later = layoutSignature(await this.snapshot());
      unmoved = later === layout ? unmoved + 1 : 0;
      layout = later;
      // The list no longer moves: it is at its end and the entry is not on it.
      if (unmoved >= entrySwipe!.stopWhenUnmoved) break;
    }
    // No source entry: this answer shows no sources.
    if (!entry) return { ...none, status: "absent" };
    const entryText = shown(entry);
    await this.driver.tap(entry);
    if (contract.via) return this.readThrough(contract, contract.via);
    if (!(await this.waitFor(contract.panel, this.timings.stepTimeoutMs)))
      throw changed("source_panel_not_opened");
    const list = await this.readOpenList(contract, entryText);
    // Only a list that was read whole, and checked against the number the App states, is worth
    // the minutes its links take; a list that is not saved has no use for them.
    const linked =
      contract.links && list.items.length > 0
        ? await this.readLinks(contract, contract.links, list.items)
        : undefined;
    const citations = linked?.items ?? list.items;
    // An empty list got this far only because the App itself states that there are no sources.
    const read: Sources = {
      citations,
      searchQueries: list.queries,
      status: citations.length > 0 ? "read" : "absent",
      ...(linked ? { links: linked.read } : {}),
    };
    // Not on the panel: the back key is not pressed for it here, where nothing says what it
    // would land on. The caller's clean-up looks first.
    if (linked?.read.leftOpen) return read;
    // Pages were opened and left for minutes since the panel was last looked at for a crash or
    // blocker (a rate-limit notice can show over the panel): the panel is not closed under one,
    // and the back key is not pressed on one. Without links the panel was just read; as before.
    if (linked) await this.assertHealthy();
    if ("back" in contract.close) await this.backOutOf(contract.panel, linked !== undefined);
    else await this.driver.tap(await this.require(contract.close));
    if (!(await this.waitGone(contract.panel, this.timings.stepTimeoutMs)))
      throw changed("source_panel_not_closed");
    return read;
  }
  // The link of every source of an open list that was read whole (contract `links`; design of
  // task 11-24, §3). Each title is tapped, the page it opens is shared with "copy link", the
  // clipboard is read through the driver and the page is left for the panel again (real device,
  // Doubao 15.7.0). The clipboard is never read by bringing another App to the front: leaving
  // the App closed its page and the panel behind it.
  //
  // A source whose link is not got keeps its title and the URL the panel showed, with the cause
  // in `link`; nothing here fails the list. Only a crash or a blocker throws (its own class),
  // and a failed driver command, which the caller treats as a failed source read. Every tap is
  // preceded by a look for a crash or blocker, and every back press on a page by another
  // (`backOutOf` with `lookFirst`): the key is never pressed on a login, verification,
  // rate-limit or emulator notice.
  private async readLinks(
    contract: SourcePath,
    links: SourceLinks,
    items: readonly Citation[],
  ): Promise<{ items: Citation[]; read: LinkRead }> {
    const started = this.now().getTime();
    const deadline = started + (links.budgetSeconds ?? LINK_BUDGET_SECONDS) * 1_000;
    const result = items.map((item) => ({ ...item }));
    const outcome: (CitationLink | undefined)[] = items.map(() => undefined);
    // Every source from `from` on that has no outcome yet gets `issue`.
    const rest = (from: number, issue: LinkIssue) => {
      for (let index = from; index < items.length; index += 1) outcome[index] ??= { issue };
    };
    let stoppedBy: LinkStop | undefined;
    let driver: ReturnType<typeof driverDetail> | undefined;
    let leftOpen = false;
    // The first source not yet handled.
    let next = 0;
    // Diagnostics only: nothing here decides what the read does.
    const screens: LinkScreen[] = [];
    let screensDropped = 0;
    let listStop: ListStop | undefined;
    const noteScreen = (screen: LinkScreen) => {
      screens.push(screen);
      if (screens.length > MAX_LINK_SCREENS) {
        screens.shift();
        screensDropped += 1;
      }
    };
    const finish = async () => {
      // Whatever ended the read, it leaves the panel on screen; where it does not, the caller's
      // clean-up decides what to press.
      if (!leftOpen && !(await this.waitFor(contract.panel, this.timings.stepTimeoutMs)))
        leftOpen = true;
      outcome.forEach((link, index) => {
        if (link === undefined) return;
        if ("copied" in link) {
          const restored = restoreLink(link.copied, links);
          result[index] = {
            ...result[index]!,
            url: restored.url,
            link: { copied: link.copied, restored: restored.restored },
          };
        } else result[index] = { ...result[index]!, link };
      });
      return {
        items: result,
        read: {
          elapsedMs: this.now().getTime() - started,
          ...(stoppedBy ? { stoppedBy } : {}),
          ...(driver ? { driver } : {}),
          leftOpen,
          screens,
          screensDropped,
          ...(listStop ? { listStop } : {}),
        },
      };
    };
    // Checked before every source is opened.
    const halt = (): LinkStop | undefined =>
      this.stopping?.aborted
        ? "stop_requested"
        : this.now().getTime() >= deadline
          ? "time_budget"
          : undefined;
    const stop = (from: number, reason: LinkStop) => {
      stoppedBy = reason;
      rest(from, reason === "time_budget" ? "time_budget" : "not_attempted");
    };
    if (halt()) {
      stop(0, halt()!);
      return finish();
    }
    // A failed driver command ends the read of the links, not that of the list (PRD R3): the
    // titles and the links read by then are kept, the source at hand and those after it are
    // not attempted. Where the screen was left is unknown, so nothing more is pressed here: the
    // caller's clean-up looks for a crash or blocker first, and fails the attempt where a page or
    // the panel stays. A crash, a blocker and an aborted run keep their own class.
    // `finish` is awaited inside: a failed driver command while it looks for the panel ends the
    // read the same way.
    try {
      // What the clipboard holds before the first copy: a copy that leaves it so did nothing.
      let previous = (await this.driver.readClipboard()).trim();
      this.lastLink = previous;

      // Back to the top of the list, which was read to its end. On a list that is already at its
      // top, a swipe towards the start drags the panel itself, and a bottom sheet can close under
      // it: the swipes end as soon as the first source is the first title on screen.
      let view = await this.linkTitles(contract, false);
      for (
        let swipe = 0, unmoved = 0;
        swipe < (contract.maxSwipes ?? 10) + SOURCE_LIST_END_SWIPES &&
        unmoved < SOURCE_LIST_END_SWIPES &&
        view.all.length > 0 &&
        alignWindow(items, view.window, 0) !== 0;
        swipe += 1
      ) {
        await this.driver.swipe("down", 1, view.bounds);
        const later = await this.linkTitles(contract, false);
        unmoved = later.signature === view.signature ? unmoved + 1 : 0;
        view = later;
      }

      // Screen by screen, from the first source not yet handled.
      let offset = 0;
      let unmoved = 0;
      let signature: string | undefined;
      // The source first not handled when the last wait for the clipboard preview was made.
      let waitedAt: number | undefined;
      for (let screen = 0; next < items.length; screen += 1) {
        view = await this.linkTitles(contract, true);
        unmoved = view.signature === signature ? unmoved + 1 : 0;
        signature = view.signature;
        const at = alignWindow(items, view.window, offset);
        const seen: LinkScreen = {
          all: view.all.length,
          whole: view.whole.length,
          at: at === undefined ? null : at + 1,
          next: next + 1,
          opened: 0,
          waited: false,
        };
        noteScreen(seen);
        // A list that looks unmoved with sources left is not yet at its end: for a few seconds
        // after a copy, Android's clipboard preview swallows a swipe, and can hide the last rows
        // (real device, Doubao 15.7.0: right after "copy link" a swipe did nothing, the same
        // swipe some 12 s later scrolled the list; design §13). So the preview is given its time
        // once, until a source still to be handled shows or the time is up, and the list is
        // swiped once more. The next look decides: a list that moved, or now shows other rows,
        // goes on as usual; one that is the same counts as at its end. Once per stop: the wait
        // and the swipe are made again only after a source was handled since. The time and a
        // stop request are looked at before both.
        if (unmoved >= SOURCE_LIST_END_SWIPES && view.all.length > 0 && waitedAt !== next) {
          waitedAt = next;
          const before = halt();
          if (before) {
            stop(next, before);
            return await finish();
          }
          seen.waited = true;
          await this.waitForTitle(contract, new Set(items.slice(next).map(({ title }) => title)));
          const after = halt();
          if (after) {
            stop(next, after);
            return await finish();
          }
          await this.driver.swipe("up", 1, view.bounds);
          continue;
        }
        // The list no longer moves, or will not show the rest: those sources are not reached.
        if (
          view.all.length === 0 ||
          unmoved >= SOURCE_LIST_END_SWIPES ||
          screen > items.length + SOURCE_LIST_END_SWIPES
        ) {
          // The screen as it is now, before anything else is done on it, for finding out why
          // the rest was not reached (design §12.2). Once per attempt.
          if (this.recordDirectory !== undefined && this.linkStop === undefined)
            this.linkStop = await this.captureLinkStop(this.recordDirectory);
          listStop =
            view.all.length === 0
              ? "no_titles"
              : unmoved >= SOURCE_LIST_END_SWIPES
                ? "unmoved"
                : "screen_limit";
          stoppedBy = "list_not_moving";
          rest(next, "not_reached");
          return await finish();
        }
        for (let position = 0; at !== undefined && position < view.window.length; position += 1) {
          const index = at + position;
          if (index < next) continue;
          // Sources the list went past without showing them whole.
          for (let skipped = next; skipped < index; skipped += 1)
            outcome[skipped] ??= { issue: "not_reached" };
          const reason = halt();
          if (reason) {
            stop(index, reason);
            return await finish();
          }
          // Where the same source is listed twice in a row, the same link is what a working copy
          // gives the second time.
          const same =
            index > 0 &&
            items[index - 1]!.title === items[index]!.title &&
            items[index - 1]!.site === items[index]!.site;
          const repeated = same ? outcome[index - 1] : undefined;
          seen.opened += 1;
          const got = await this.readLink(
            contract,
            links,
            view.whole[position]!.node,
            view.all.map(({ node }) => node),
            previous,
            repeated !== undefined && "copied" in repeated && repeated.copied === previous,
          );
          previous = got.clipboard;
          this.lastLink = previous;
          next = index + 1;
          if (!got.closed) {
            // Nothing more is opened over a page that stayed or without the panel it leads back to.
            outcome[index] = { issue: "page_not_closed" };
            stoppedBy = "page_not_closed";
            rest(next, "not_attempted");
            leftOpen = true;
            return await finish();
          }
          outcome[index] =
            got.issue !== undefined
              ? { issue: got.issue }
              : { copied: got.clipboard, restored: false };
        }
        if (at !== undefined) offset = at;
        if (next >= items.length) break;
        // Also before every swipe: a list whose screens give no source to open is not swiped
        // through to its end once the time is up or a stop was requested.
        const halted = halt();
        if (halted) {
          stop(next, halted);
          return await finish();
        }
        await this.driver.swipe("up", 1, view.bounds);
      }
    } catch (error) {
      if (!(error instanceof DriverError)) throw error;
      stoppedBy = "driver_error";
      driver = driverDetail(error);
      rest(next, "not_attempted");
      leftOpen = true;
    }
    return finish();
  }
  // The title views of the open panel: all of them, those the list does not cut with the source
  // each shows, the place of these on screen, and their bounds. `atRest`: taken once the screen
  // has stopped changing. Android's clipboard preview hides the views under it for a few seconds
  // after a copy (real device: it covers the lower part of the screen), so a screen without a
  // title is given that long to show one.
  private async linkTitles(contract: SourcePath, atRest: boolean) {
    const look = () => (atRest ? this.snapshotAtRest() : this.snapshot());
    let all = titleItems(await look(), contract);
    if (
      all.length === 0 &&
      (await this.waitFor({ ...contract.itemTitle!, pick: "first" }, COPY_SETTLE_MS))
    )
      all = titleItems(await look(), contract);
    const whole = uncutTitles(all);
    return {
      all,
      whole,
      window: whole.map(({ item }) => item),
      signature: JSON.stringify(whole.map(({ node }) => [node.text, node.bounds])),
      bounds: unionBounds(all.map(({ node }) => node)),
    };
  }
  // The link of one source, from the panel back to the panel. `closed`: the panel is on screen
  // again and nothing of the page is. `clipboard`: what the clipboard holds afterwards.
  private async readLink(
    contract: SourcePath,
    links: SourceLinks,
    title: UiNode,
    titles: readonly UiNode[],
    previous: string,
    repeated: boolean,
  ): Promise<{ issue?: LinkIssue; clipboard: string; closed: boolean }> {
    const leave = async (
      issue: LinkIssue | undefined,
      clipboard = previous,
    ): Promise<{ issue?: LinkIssue; clipboard: string; closed: boolean }> => {
      const closed = await this.leaveLinkPage(contract, links);
      return { ...(issue ? { issue } : {}), clipboard, closed };
    };
    // Another App in front after a source was tapped: the back key until this App is in front
    // again, then on to the page or the panel as below. A blocker or a system dialog keeps its
    // class and nothing is pressed on it; an App that does not give way is a crash, as before.
    const leaveForeign = async (
      issue: LinkIssue | undefined,
      clipboard = previous,
    ): Promise<{ issue?: LinkIssue; clipboard: string; closed: boolean }> => {
      await this.backToApp();
      if (await this.visible(links.page)) return leave(issue, clipboard);
      const panel = await this.waitFor(contract.panel, this.timings.stepTimeoutMs);
      return { ...(issue ? { issue } : {}), clipboard, closed: panel !== undefined };
    };
    // A row tapped right after a page was left, or under the preview of a copy, can miss (real
    // device, Doubao 15.7.0). Whether the page opened is looked at; a page that did not is given
    // one more tap once the panel is at rest.
    let page: UiElement | undefined;
    for (let tap = 1; tap <= 2 && !page; tap += 1) {
      if (tap > 1) {
        // The first tap may have opened another App instead of a page (design §11.1).
        if (!(await this.linkHealth())) return leaveForeign("left_app");
        await this.snapshotAtRest();
        if (!(await this.linkHealth())) return leaveForeign("left_app");
      } else await this.assertHealthy();
      // The page may have come after all.
      if (tap > 1 && (await this.visible(links.page))) break;
      const row = await this.linkRow(contract, title, titles);
      if (!row) {
        if (tap > 1) break;
        return { issue: "row_not_found", clipboard: previous, closed: true };
      }
      await this.driver.tap(row);
      page = await this.waitFor(links.page, PAGE_OPEN_MS);
    }
    if (!page && !(await this.visible(links.page))) {
      if (!(await this.linkHealth())) return leaveForeign("left_app");
      // Nothing opened: the panel must still be there for the next source.
      const panel = await this.waitFor(contract.panel, this.timings.stepTimeoutMs);
      return { issue: "page_not_opened", clipboard: previous, closed: panel !== undefined };
    }
    if (!(await this.linkHealth())) return leaveForeign("left_app");
    const share = await this.waitFor(links.share, this.timings.stepTimeoutMs);
    if (!share) {
      if (!(await this.linkHealth())) return leaveForeign("left_app");
      return leave("share_missing");
    }
    await this.driver.tap(share);
    // The share control can open the App's own sheet or Android's (real device, Doubao 15.7.0 on
    // Android 15: once in 12 sources, `com.android.intentresolver`). Whichever copy control
    // shows first is used; the package of Android's sheet is tolerated in front only from here
    // until the clipboard is read.
    const system = links.systemShare;
    const sheet = await this.shareSheet(links);
    if (sheet === "foreign") return leaveForeign("share_external");
    if (sheet === "none") return leave("copy_missing");
    if (sheet === "system") {
      // As the App's sheet: tapped once at rest, and the clipboard must have changed.
      await this.snapshotAtRest();
      const copy = await this.waitFor(system!.copy, this.timings.stepTimeoutMs);
      if (!copy || !(await this.linkHealth(system!.package))) return leaveForeign("share_external");
      await this.driver.tap(copy);
      let text = await this.clipboardAfterCopy(previous);
      if (text === previous) {
        const again = await this.waitFor(system!.copy, 0);
        if (again) {
          await this.driver.tap(again);
          text = await this.clipboardAfterCopy(previous);
        }
      }
      if (text === previous && !repeated) return leaveForeign("share_external");
      // The sheet usually closes itself after the copy; one that stays takes one back press,
      // after a look for a crash or blocker.
      if (
        (await this.driver.foregroundPackage()) === system!.package &&
        (await this.linkHealth(system!.package))
      ) {
        await this.driver.back();
        await this.pause(PANEL_BACK_WAIT_MS);
      }
      return leaveForeign(isWebLink(text) ? undefined : "not_a_link", text);
    }
    // The App's own sheet also offers "more", which opens Android's sheet; the smoke of
    // 2026-11-25 could not tell whether that is how Android's sheet came. Another package in
    // front after the App's sheet showed is left with the back key as above, not taken for a
    // crash (PRD R9); a link already copied is kept. `leaveForeign` looks for a crash or blocker
    // before every press.
    const leaveSheet = async (issue: LinkIssue | undefined, clipboard = previous) => {
      const foreground = await this.driver.foregroundPackage();
      if (foreground === null || foreground === this.contract.package)
        return leave(issue, clipboard);
      return leaveForeign(
        issue === "copy_missing" || issue === "clipboard_unchanged" ? "share_external" : issue,
        clipboard,
      );
    };
    // A tap while the share sheet still slides in lands nowhere (real device, Doubao 15.7.0):
    // "copy link" is tapped once the sheet is at rest.
    await this.snapshotAtRest();
    const copy = await this.waitFor(links.copy, this.timings.stepTimeoutMs);
    if (!copy) return leaveSheet("copy_missing");
    await this.driver.tap(copy);
    let text = await this.clipboardAfterCopy(previous);
    if (text === previous) {
      // The sheet still showing means the tap missed it.
      const again = await this.waitFor(links.copy, 0);
      if (again) {
        await this.driver.tap(again);
        text = await this.clipboardAfterCopy(previous);
      }
    }
    if (text === previous && !repeated) return leaveSheet("clipboard_unchanged");
    return leaveSheet(isWebLink(text) ? undefined : "not_a_link", text);
  }
  // After a tap on a page's share control: which sheet shows its copy control, the App's own or
  // Android's (where the contract declares it). "foreign": another package is in front instead,
  // or Android's sheet is but its copy control did not show; "none": this App is in front and
  // neither shows.
  private async shareSheet(links: SourceLinks): Promise<"app" | "system" | "foreign" | "none"> {
    const system = links.systemShare;
    const deadline = this.now().getTime() + this.timings.stepTimeoutMs;
    for (;;) {
      if (await this.visible(links.copy)) return "app";
      if (system && (await this.visible(system.copy))) return "system";
      if (!(await this.linkHealth(system?.package))) return "foreign";
      if (this.now().getTime() >= deadline) return (await this.linkHealth()) ? "none" : "foreign";
      await this.pause(this.timings.lookupPollMs);
    }
  }
  // The health check of the link read (design of task 11-24, §11.1). A system dialog and a
  // blocker throw their class first, as in `assertHealthy`; another package in front is not a
  // crash here but returns false, for the caller to leave with the back key. `allow`: a package
  // that may be in front as well (Android's share sheet, while its copy is used).
  private async linkHealth(allow?: string): Promise<boolean> {
    if (await this.systemDialog())
      throw new AppChannelError("app_crashed", undefined, "system_dialog");
    for (const code of BLOCKER_ORDER)
      for (const marker of this.contract.blockers[code])
        if (await this.visible(marker))
          throw new AppChannelError(code, undefined, "blocker_marker");
    const foreground = await this.driver.foregroundPackage();
    return foreground === null || foreground === this.contract.package || foreground === allow;
  }
  // Presses back, a bounded number of times, until this App is in front again. Every press is
  // preceded by `linkHealth`: nothing is pressed on a blocker or a system dialog. An App that
  // stays in front is a crash, as it is outside the link read.
  private async backToApp(): Promise<void> {
    for (let press = 1; press <= FOREIGN_BACK_PRESSES; press += 1) {
      if (await this.linkHealth()) return;
      await this.driver.back();
      await this.pause(PANEL_BACK_WAIT_MS);
    }
    if (!(await this.linkHealth()))
      throw new AppChannelError("app_crashed", undefined, "foreground_other");
  }
  // Up to the time Android's clipboard preview stays (`COPY_SETTLE_MS`), whether a title of
  // `titles` shows on the panel.
  private async waitForTitle(contract: SourcePath, titles: ReadonlySet<string>): Promise<void> {
    const deadline = this.now().getTime() + COPY_SETTLE_MS;
    for (;;) {
      const found = await this.driver.find(contract.itemTitle!);
      if (found.some((element) => titles.has(element.text.trim()))) return;
      if (this.now().getTime() >= deadline) return;
      await this.pause(this.timings.lookupPollMs);
    }
  }
  // The row of `title` on the panel now. A title is known by its place where the screen still
  // has a view there with its text, else as the n-th view with its text, n counted in the export
  // it was found in. Up to the time Android's clipboard preview may hide it.
  private async linkRow(
    contract: SourcePath,
    title: UiNode,
    titles: readonly UiNode[],
  ): Promise<UiElement | undefined> {
    const text = title.text.trim();
    const nth = titles.filter((node) => node.text.trim() === text).indexOf(title);
    const deadline = this.now().getTime() + COPY_SETTLE_MS;
    for (;;) {
      const found = (await this.driver.find(contract.itemTitle!)).filter(
        (element) => element.text.trim() === text,
      );
      const row =
        found.find((element) => JSON.stringify(element.bounds) === JSON.stringify(title.bounds)) ??
        found[nth];
      if (row || this.now().getTime() >= deadline) return row;
      await this.pause(this.timings.lookupPollMs);
    }
  }
  // The clipboard after a copy, read again a few times while it still holds what it held before.
  private async clipboardAfterCopy(previous: string): Promise<string> {
    for (let read = 1; ; read += 1) {
      const text = (await this.driver.readClipboard()).trim();
      if (text !== previous || read >= LINK_CLIPBOARD_READS) return text;
      await this.pause(this.timings.lookupPollMs);
    }
  }
  // Leaves a source's page for the panel. The back key from the page leads straight back to the
  // panel as it was left (real device, Doubao 15.7.0); a share sheet still open takes a press of
  // its own. Returns whether the page is gone and the panel back.
  private async leaveLinkPage(contract: SourcePath, links: SourceLinks): Promise<boolean> {
    let gone = false;
    if (!("back" in links.close)) {
      const close = await this.waitFor(links.close, 0);
      if (close) {
        // As every press of the back key below: nothing is tapped on a crash or blocker.
        await this.assertHealthy();
        await this.driver.tap(close);
        gone = await this.waitGone(links.page, this.timings.stepTimeoutMs);
      }
    }
    if (!gone) await this.backOutOf(links.page, true);
    if (await this.visible(links.page)) return false;
    return (await this.waitFor(contract.panel, this.timings.stepTimeoutMs)) !== undefined;
  }
  // The open source list, read to its end, and the keyword line of its panel.
  private async readOpenList(
    contract: SourcePath,
    entryText: string,
  ): Promise<{ items: Citation[]; queries: string[] }> {
    let root = await this.snapshot();
    let items = citationItems(root, contract);
    // A panel can show its title before its list. An entry that opened a panel leads to sources:
    // a list that stays unreadable is not saved as "this answer shows no sources". Only an App
    // that states the number itself can say there are none (the count check below).
    const listDeadline = this.now().getTime() + this.timings.stepTimeoutMs;
    while (items.length === 0 && this.now().getTime() < listDeadline) {
      await this.pause(this.timings.lookupPollMs);
      root = await this.snapshot();
      items = citationItems(root, contract);
    }
    if (items.length === 0 && !contract.count) throw changed("source_list_empty");
    const queries = searchQueries(root, contract);
    // The stated number is read while the panel is still at its top: where the App shows it in
    // a row of the list itself, that row has scrolled away by the time the list is read to its
    // end (real device, Doubao 15.7.0).
    const statedIn = (tree: UiNode, selector: Selector) => {
      const node = findNodes(tree, selector)[0];
      return node ? shown(node) : "";
    };
    const statedAtTop =
      contract.count?.from === "panel" ? statedIn(root, contract.count.selector) : "";
    // Scroll the list until swipes no longer move it. One swipe without effect is not the end
    // of the list (a drag can land while a panel is still settling), two in a row are.
    let complete = items.length === 0;
    let unmoved = 0;
    for (let swipe = 0; swipe < (contract.maxSwipes ?? 10) && !complete; swipe += 1) {
      await this.driver.swipe(
        "up",
        1,
        unionBounds(
          contract.cards
            ? cardNodes(root, contract.cards.from)
            : findNodes(root, contract.itemTitle!),
        ),
      );
      const later = await this.snapshot();
      if (layoutSignature(later) === layoutSignature(root)) {
        unmoved += 1;
        complete = unmoved >= SOURCE_LIST_END_SWIPES;
      } else {
        unmoved = 0;
        items = mergeWindows(items, citationItems(later, contract), sameCitation);
      }
      root = later;
    }
    // A list that is still growing after the allowed swipes is not saved as if it were whole.
    if (!complete) throw changed("source_list_still_moving");
    // Nor is a list whose length differs from the number the App itself states.
    if (contract.count) {
      const stated =
        contract.count.from === "entry"
          ? entryText
          : statedAtTop || statedIn(root, contract.count.selector);
      const number = new RegExp(contract.count.pattern, "u").exec(stated)?.[1];
      if (number === undefined || Number(number) !== items.length)
        throw new AppChannelError("app_contract_changed", undefined, "citation_count", {
          stated: number === undefined ? -1 : Number(number),
          read: items.length,
        });
    }
    return { items, queries };
  }
  // The source entry opened a screen in between (the tool steps of the answer) whose rows lead
  // to one source list each: every such row is opened, its list read to its end and left again,
  // and the lists are joined. Every back press on the way is preceded by a look for a crash or
  // blocker.
  private async readThrough(
    contract: SourcePath,
    via: NonNullable<SourcePath["via"]>,
  ): Promise<Sources> {
    const close = async (
      screen: { panel: Selector; close: typeof via.close },
      // Leaving a list must bring the screen in between back.
      back?: Selector,
    ) => {
      if ("back" in screen.close) await this.backOutOf(screen.panel, true);
      else await this.driver.tap(await this.require(screen.close));
      if (
        !(await this.waitGone(screen.panel, this.timings.stepTimeoutMs)) ||
        (back !== undefined && !(await this.waitFor(back, this.timings.stepTimeoutMs)))
      )
        throw changed("source_panel_not_closed");
    };
    if (!(await this.waitFor(via.panel, this.timings.stepTimeoutMs)))
      throw changed("source_panel_not_opened");
    // From here on a back key or a close control is used on the way back to the conversation.
    this.sourceStepsOpened = true;
    // The rows as the screen shows them once it has opened, each text once. The screen is not
    // scrolled: a row that is further down is not opened. A screen can show its title before
    // its rows, as a source panel can, so the rows are taken from an export of the screen at
    // rest: read from the first look, rows that were not there yet would be left out, and with
    // none there the answer would be recorded as showing no sources.
    const rows = [
      ...new Set(
        findNodes(await this.snapshotAtRest(), via.item)
          .map((node) => node.text)
          .filter((text) => text.trim() !== ""),
      ),
    ].slice(0, via.maxItems ?? VIA_ITEMS);
    // Steps, but none that leads to sources: this answer shows none.
    if (rows.length === 0) {
      await close(via);
      return { citations: [], searchQueries: [], status: "absent" };
    }
    // A row that leads to sources is there: the answer was written with a web search, whatever
    // becomes of reading the lists.
    this.sourceRowSeen = true;
    const citations: Citation[] = [];
    const queries: string[] = [];
    const keep = (query: string) => {
      if (query !== "" && !queries.includes(query)) queries.push(query);
    };
    for (const row of rows) {
      if (via.queryPrefix !== undefined && row.trim().startsWith(via.queryPrefix))
        keep(row.trim().slice(via.queryPrefix.length).trim());
      // Lists were opened and left since the screen was last looked at for a crash or blocker
      // (a notice can answer just that): a row is not tapped on or under one.
      await this.assertHealthy();
      await this.driver.tap(await this.sourceRow(via.item, row));
      if (!(await this.waitFor(contract.panel, this.timings.stepTimeoutMs)))
        throw changed("source_panel_not_opened");
      const list = await this.readOpenList(contract, "");
      list.queries.forEach(keep);
      // A source that several lists show is kept once.
      for (const item of list.items)
        if (!citations.some((kept) => sameCitation(kept, item))) citations.push(item);
      await close(contract, via.panel);
    }
    await close(via);
    // Every opened list held at least one source, or its read would have thrown.
    return { citations, searchQueries: queries, status: "read" };
  }
  // One export of a screen that has just opened, taken once it has stopped changing: two
  // exports one lookup interval apart that show the same views at the same places. When the
  // step time is up the last export is taken as it is.
  private async snapshotAtRest(): Promise<UiNode> {
    const deadline = this.now().getTime() + this.timings.stepTimeoutMs;
    let root = await this.snapshot();
    for (;;) {
      await this.pause(this.timings.lookupPollMs);
      const later = await this.snapshot();
      if (layoutSignature(later) === layoutSignature(root) || this.now().getTime() >= deadline)
        return later;
      root = later;
    }
  }
  // The row of the screen between entry and source list that shows `text`: the first view the
  // contract's `item` selector finds whose text is exactly that (rows with the same text were
  // taken once). The row's text comes from the screen and is compared here; it is never made
  // part of a lookup expression, where a backslash or a quote in a search keyword would have to
  // survive the quoting of the device's selector syntax.
  private async sourceRow(item: Selector, text: string): Promise<UiElement> {
    const look = async (): Promise<UiElement | undefined> => {
      const deadline = this.now().getTime() + this.timings.stepTimeoutMs;
      for (;;) {
        const row = (await this.driver.find(item)).find((element) => element.text === text);
        if (row || this.now().getTime() >= deadline) return row;
        await this.pause(this.timings.lookupPollMs);
      }
    };
    // As with any required view (`require`): a crash or blocker has its own class, a declared
    // popup is closed, and only then is the missing row a contract change.
    let row = await look();
    if (!row) {
      await this.assertHealthy();
      if (await this.dismissPopups()) row = await look();
    }
    if (!row) throw changed("required_view_missing");
    return row;
  }
  // Presses back until `panel` is gone, a bounded number of times. A panel that was dragged open
  // to full height takes one back press to return to its first height and another to close (real
  // device, Yuanbao 2.92.0). The screen is looked at only after the press had its time: a look
  // that began while the panel was still closing would call for another press, and that one
  // would land on the conversation and leave it. With `lookFirst` every press is preceded by a
  // look for a crash or blocker, which throws its class instead of pressing: the key is never
  // pressed on a login, verification, rate-limit or emulator notice.
  private async backOutOf(panel: Selector, lookFirst = false): Promise<void> {
    for (let press = 1; press <= PANEL_BACK_PRESSES; press += 1) {
      if (lookFirst) await this.assertHealthy();
      await this.driver.back();
      await this.pause(PANEL_BACK_WAIT_MS);
      if (!(await this.visible(panel))) return;
    }
  }
  // After the read of a source path that failed, or a read of its links that did not end on its
  // panel: leaves what the read left open, by the contract's own way of closing it and then by
  // the back key: a source's page first where the path reads links, then the path's source
  // panel, then the screen between entry and panel where it declares one. The screens of the
  // contract's other paths are not this read's to leave. Nothing is pressed while neither is on
  // screen, and the back key is not pressed on a crash or blocker screen (the caller has looked
  // for one right before this; every press looks again, since seconds pass between them). Best
  // effort, never throws: whether both are gone and the conversation back is decided by the
  // caller. Returns whether one of them was on screen, that is whether something was done and
  // time has passed.
  async closeSourcePanel(contract: SourcePath): Promise<boolean> {
    let found = false;
    try {
      // A source's page, where links are read, is over the panel and is left first.
      for (const screen of [
        ...(contract.links ? [{ panel: contract.links.page, close: contract.links.close }] : []),
        contract,
        ...(contract.via ? [contract.via] : []),
      ]) {
        // The screen in between needs a moment to show again after the panel over it was left.
        const shown = found
          ? await this.waitFor({ ...screen.panel, pick: "first" }, this.timings.stepTimeoutMs)
          : await this.visible(screen.panel);
        if (!shown) continue;
        found = true;
        if (!("back" in screen.close)) {
          const close = await this.waitFor(screen.close, 0);
          if (close) {
            await this.driver.tap(close);
            if (await this.waitGone(screen.panel, this.timings.stepTimeoutMs)) continue;
          }
        }
        await this.backOutOf(screen.panel, true);
        // A source panel that stays is on top of whatever else is open: nothing more is pressed.
        if (await this.visible(screen.panel)) break;
      }
    } catch {
      // The caller's own checks decide.
      return true;
    }
    return found;
  }
  // Whether a source panel, or a screen between entry and panel, of any of the contract's
  // source paths is on screen. The views of the conversation under an open panel can still be
  // in the hierarchy, so "the end of the answer is visible" does not answer this.
  async sourcePanelOpen(): Promise<boolean> {
    for (const path of sourcePaths(this.contract))
      if (
        (await this.visible(path.panel)) ||
        (path.via !== undefined && (await this.visible(path.via.panel))) ||
        (path.links !== undefined && (await this.visible(path.links.page)))
      )
        return true;
    return false;
  }

  searchState(roots: readonly UiNode[], queries: string[]): NonNullable<AppRecord["searchState"]> {
    const markers = this.contract.searchState;
    const shown = (selector: Selector | undefined) =>
      selector !== undefined && roots.some((root) => findNodes(root, selector).length > 0);
    const state = (on: Selector | undefined, off: Selector | undefined) =>
      shown(on) ? "on" : shown(off) ? "off" : "unknown";
    return {
      webSearch: this.sourceRowSeen ? "on" : state(markers?.webSearchOn, markers?.webSearchOff),
      deepThinking: state(markers?.deepThinkingOn, markers?.deepThinkingOff),
      searchQueries: queries,
    };
  }

  // A driver command failed: is the device gone, the App gone, or did the screen change?
  async classifyDriverFailure(): Promise<AppFailureCode> {
    try {
      const foreground = await this.driver.foregroundPackage();
      return foreground !== null && foreground !== this.contract.package
        ? "app_crashed"
        : "app_contract_changed";
    } catch {
      return "device_offline";
    }
  }
  // The crash or blocker the screen shows right now, if any. Asked before a failure is recorded
  // as a contract change: a view goes missing just the same when a rate-limit notice, a
  // verification screen or the launcher has taken the place of the conversation. A screen that
  // cannot be looked at keeps the class it had. Visibility only.
  async blockingState(): Promise<AppChannelError | undefined> {
    try {
      await this.assertHealthy();
      return undefined;
    } catch (error) {
      return error instanceof AppChannelError ? error : undefined;
    }
  }
  // After `app_crashed`: close the App through the system dialog when it shows one, otherwise
  // stop it, so the next attempt starts it fresh.
  async closeCrashedApp(): Promise<void> {
    try {
      const dialog = await this.systemDialog();
      if (dialog?.close) await this.driver.tap(dialog.close);
      else await this.driver.terminateApp(this.contract.package);
    } catch {
      // Best effort: the next attempt's own checks decide.
    }
  }
  // Screenshot and hierarchy of the final or failing screen. Best effort: a part that cannot be
  // taken is null.
  async capture(directory: string, screenshotName: "final.png" | "failure.png") {
    let screenshot: FileEvidence | null = null;
    let uiDump: FileEvidence | null = null;
    let root: UiNode | undefined;
    try {
      const path = join(directory, screenshotName);
      await this.driver.screenshot(path);
      screenshot = await fileEvidence(path);
    } catch {
      screenshot = null;
    }
    try {
      const xml = await this.driver.hierarchy();
      const path = join(directory, "ui.xml");
      await writeFile(path, xml, { mode: 0o600, flag: "wx" });
      uiDump = await fileEvidence(path);
      root = parseHierarchy(xml);
    } catch {
      root = undefined;
    }
    return { screenshot, uiDump, root };
  }
  // The screen and hierarchy where the link read stopped because the list did not move, as
  // `links-stop.png` and `links-stop.xml` in the record directory: private, created once, never
  // replaced (as `capture`). Best effort: a part that cannot be taken is null, and nothing here
  // fails the read or the sample.
  async captureLinkStop(directory: string): Promise<NonNullable<AppRecord["linkStop"]>> {
    let screenshot: FileEvidence | null = null;
    let hierarchy: FileEvidence | null = null;
    try {
      const path = join(directory, "links-stop.png");
      await this.driver.screenshot(path);
      screenshot = await fileEvidence(path);
    } catch {
      screenshot = null;
    }
    try {
      const path = join(directory, "links-stop.xml");
      await writeFile(path, await this.driver.hierarchy(), { mode: 0o600, flag: "wx" });
      hierarchy = await fileEvidence(path);
    } catch {
      hierarchy = null;
    }
    return { screenshot, hierarchy };
  }
  async leave(): Promise<void> {
    await this.driver.home().catch(() => undefined);
    await this.driver.close().catch(() => undefined);
  }
}

interface Prepared {
  serial: string;
  actual: DeviceActual;
  appVersion: string;
  contract: AppContract;
  driver: DeviceDriver;
}
interface Progress {
  attemptedTap: boolean;
  sentAt: Date | null;
  completedAt: Date | null;
  answerText: string | null;
  citations: Citation[];
  searchQueries: string[];
  // How the source read ended; null until it has ended. `lists` only where the contract
  // declares several source paths: how each of them ended, in the order they were read.
  // `failed`: the read of a path, or of one of several, did not get through.
  sources: {
    status: NonNullable<AppRecord["citationsStatus"]>;
    issue?: string;
    lists?: CitationList[];
    failed: boolean;
  } | null;
  snapshots: UiNode[];
}

// Self-check reasons that are not failure classes map to the class that stops the binding.
function checkFailure(reason: CheckReason | undefined): AppFailureCode {
  return reason === undefined || reason === "app_not_installed" || reason === "login_probe_failed"
    ? "app_contract_unverified"
    : reason;
}

export class AppAdapter {
  private readonly timings: AdapterTimings;
  private readonly now: () => Date;
  private readonly sleep: Sleep;
  private readonly newAnswerId: () => string;
  private readonly log: Log;
  // Device -> the text this run last read from its clipboard. The clipboard is one per device
  // and outlives a conversation and an App, so a copy control that did nothing leaves the text
  // of an earlier answer there.
  private readonly lastCopied = new Map<string, string>();

  constructor(private readonly options: AppAdapterOptions) {
    this.timings = { ...DEFAULT_TIMINGS, ...options.timings };
    this.now = options.now ?? (() => new Date());
    this.sleep = options.sleep ?? realSleep;
    this.newAnswerId = options.newAnswerId ?? randomUUID;
    this.log = options.log ?? (() => undefined);
  }

  // Device online and as declared, installed version reviewed, contract usable, session open.
  // A failure here means no attempt: nothing is recorded and nothing is sent.
  private async prepare(binding: ResolvedBinding, leased: boolean): Promise<Prepared> {
    const { inspector } = this.options;
    let serial: string;
    let installedAppVersion: string;
    let actual: DeviceActual = binding.device;
    if (leased) {
      // Already leased. Only inspect the target and use its real facts; a trial-specific
      // difference in timezone, ABI etc. is not a reason to reject a task.
      const device = (await inspector.inspectAll(binding.device.id)).get(binding.device.id);
      if (!device?.serial || !device.actual)
        throw new AppChannelError("device_offline", "pre_send");
      actual = device.actual;
      serial = device.serial;
      const installed = await inspector.installedAppVersion(
        binding.device.id,
        binding.package,
        serial,
      );
      if (!installed || !binding.enabled)
        throw new AppChannelError("app_contract_unverified", "pre_send");
      installedAppVersion = installed;
    } else {
      const [result] = await checkBindings({ bindings: [binding], inspector });
      if (!result || result.status !== "pass" || !result.adbSerial || !result.installedAppVersion)
        throw new AppChannelError(checkFailure(result?.reason), "pre_send");
      serial = result.adbSerial;
      installedAppVersion = result.installedAppVersion;
    }
    const contract = await this.options.contracts.verified(binding, {
      installedAppVersion,
      deviceLocale: actual.locale,
      now: this.now(),
    });
    try {
      const driver = await this.options.drivers.open({
        deviceId: binding.device.id,
        serial,
      });
      return {
        serial,
        actual,
        appVersion: installedAppVersion,
        contract,
        driver,
      };
    } catch (error) {
      if (!(error instanceof DriverError)) throw error;
      // The device answers adb but no automation session could be opened on it.
      this.log({
        event: "app_driver_session_failed",
        device: binding.device.id,
        platform: binding.platformCode,
      });
      throw new AppChannelError("device_offline", "pre_send");
    }
  }

  readonly collect: AttemptRunner = async (request) => {
    const { binding, batchId, questionText } = request;
    const { store } = this.options;
    const identity = { batchId, device: binding.device.id, platform: binding.platformCode };

    let prepared: Prepared;
    try {
      prepared = await this.prepare(binding, request.task !== undefined);
    } catch (error) {
      if (request.task && error instanceof AppSetupError && error.code === "adb_unavailable") {
        this.log({ event: "app_attempt_skipped", ...identity, failureCode: "device_offline" });
        return {
          answerId: null,
          status: "failed",
          failureCode: "device_offline",
          phase: "pre_send",
        };
      }
      if (!(error instanceof AppChannelError)) throw error;
      this.log({ event: "app_attempt_skipped", ...identity, failureCode: error.code });
      return { answerId: null, status: "failed", failureCode: error.code, phase: "pre_send" };
    }
    const { serial, actual, appVersion, contract, driver } = prepared;
    const screen = new Screen(
      driver,
      contract,
      this.timings,
      this.now,
      this.sleep,
      request.signal,
      request.stopping,
    );

    // An App that does not come up is like a failed pre-check: nothing was recorded or sent, so
    // there is no record and no answer ID.
    try {
      await screen.warmUp();
    } catch (error) {
      const failureCode =
        error instanceof AppChannelError
          ? error.code
          : error instanceof DriverError
            ? await screen.classifyDriverFailure()
            : undefined;
      if (failureCode === "app_crashed") await screen.closeCrashedApp();
      await screen.leave();
      if (failureCode === undefined) throw error;
      this.log({
        event: "app_attempt_skipped",
        ...identity,
        failureCode,
        cause:
          error instanceof AppChannelError
            ? (error.cause ?? null)
            : `driver_${(error as DriverError).kind}`,
      });
      return { answerId: null, status: "failed", failureCode, phase: "pre_send" };
    }

    const answerId = request.answerId ?? this.newAnswerId();
    const startedAt = this.now();
    // The approved question as it is: nothing is put before or after it.
    const sentText = questionText;
    const progress: Progress = {
      attemptedTap: false,
      sentAt: null,
      completedAt: null,
      answerText: null,
      citations: [],
      searchQueries: [],
      sources: null,
      snapshots: [],
    };
    const base = {
      answerId,
      batchId,
      ...(request.task
        ? { sampleClass: "control_plane" as const, task: request.task }
        : { sampleClass: "local_trial" as const }),
      channel: "consumer_app",
      scriptRevision: this.options.scriptRevision,
      questionText,
      sentText,
      platformCode: binding.platformCode,
      package: binding.package,
      appVersion,
      deviceId: binding.device.id,
      adbSerial: serial,
      androidVersion: actual.androidVersion,
      abi: actual.abi,
      deviceLocale: actual.locale,
      deviceTimezone: actual.timezone,
      accountLabel: binding.accountLabel,
      startedAt: timestampPair(startedAt),
    } satisfies Partial<AppRecord>;
    const finish = async (record: AppRecord): Promise<AttemptOutcome> => {
      await store.writeRecord(record);
      this.log({
        event: "app_attempt_finished",
        ...identity,
        answerId,
        at: this.now().toISOString(),
        status: record.status,
        failureCode: record.failureCode,
        phase: record.phase,
        ...(record.evidenceIssue ? { evidenceIssue: record.evidenceIssue } : {}),
        answerChars: record.answerText?.length ?? 0,
        citations: record.citations.length,
        ...(record.citationsStatus ? { citationsStatus: record.citationsStatus } : {}),
      });
      return {
        answerId,
        status: record.status,
        failureCode: record.failureCode,
        phase: record.phase,
        ...(record.evidenceIssue ? { evidenceIssue: record.evidenceIssue } : {}),
      };
    };

    this.log({
      event: "app_attempt_started",
      ...identity,
      answerId,
      at: startedAt.toISOString(),
    });
    let directory: string;
    let recording: ActiveRecording;
    try {
      directory = await store.recordDirectory(batchId, answerId);
      screen.recordDirectory = directory;
      recording = await this.options.recorder.start({ serial, answerId, directory });
    } catch (error) {
      await driver.close().catch(() => undefined);
      if (!(error instanceof AppChannelError)) throw error;
      // No confirmed recording: nothing is sent and no ledger line is written.
      return finish({
        ...base,
        sentAt: null,
        completedAt: null,
        status: "failed",
        failureCode: error.code,
        phase: "pre_send",
        answerText: null,
        citations: [],
        searchState: null,
        video: null,
        screenshot: null,
        uiDump: null,
      });
    }

    // Whatever happens from here on, the session is closed and the App left on the home screen.
    try {
      let failure: AppFailureCode | null = null;
      let fatal: { error: unknown } | undefined;
      try {
        await this.ask(screen, request, { answerId, sentText }, progress, recording);
      } catch (error) {
        if (error instanceof AppChannelError) failure = error.code;
        else if (error instanceof DriverError) failure = await screen.classifyDriverFailure();
        else fatal = { error };
        // A contract change is what is left when the screen shows no state with its own class.
        const seen = failure === "app_contract_changed" ? await screen.blockingState() : undefined;
        if (seen) failure = seen.code;
        // Which check gave the class; fixed words only, never text read from the screen.
        if (failure !== null)
          this.log({
            event: "app_attempt_failure_cause",
            ...identity,
            answerId,
            failureCode: failure,
            cause: seen
              ? (seen.cause ?? null)
              : error instanceof AppChannelError
                ? (error.cause ?? null)
                : `driver_${(error as DriverError).kind}`,
            ...driverDetail(error),
          });
      }
      if (failure === "collection_timeout" && progress.sentAt && progress.answerText === null)
        progress.answerText = await screen.partialAnswer(sentText);
      const captured = await screen.capture(
        directory,
        failure === null && !fatal ? "final.png" : "failure.png",
      );
      if (failure === "app_crashed") await screen.closeCrashedApp();

      // The recording is stopped in every case, also when the run is about to end on an error.
      let outcome: RecordingOutcome;
      try {
        outcome = await recording.stop({
          sentAt: progress.sentAt?.toISOString() ?? null,
          completedAt: progress.completedAt?.toISOString() ?? null,
        });
      } catch (error) {
        throw fatal?.error ?? error;
      }
      // Not a classified failure (for example the evidence directory became unwritable): the
      // run ends and nothing is recorded as a sample.
      if (fatal) throw fatal.error;

      const evidenceIssue: EvidenceIssue | undefined =
        outcome.issue ??
        (failure === null && captured.screenshot === null ? "screenshot_failed" : undefined);
      const status: AppRecord["status"] =
        failure !== null ? "failed" : evidenceIssue !== undefined ? "evidence_incomplete" : "valid";
      const completed = status === "failed" ? null : progress.completedAt;
      const result = await finish({
        ...base,
        sentAt: progress.sentAt ? timestampPair(progress.sentAt) : null,
        completedAt: completed ? timestampPair(completed) : null,
        status,
        failureCode: failure,
        phase: status === "valid" ? null : progress.attemptedTap ? "post_send" : "pre_send",
        answerText: progress.answerText,
        citations: progress.citations,
        searchState:
          status === "failed"
            ? null
            : screen.searchState(
                captured.root ? [...progress.snapshots, captured.root] : progress.snapshots,
                progress.searchQueries,
              ),
        // How the sources were read belongs to a collected answer; a failure does not carry it.
        ...(status !== "failed" && progress.sources
          ? {
              citationsStatus: progress.sources.status,
              ...(progress.sources.issue !== undefined
                ? { citationsIssue: progress.sources.issue }
                : {}),
              ...(progress.sources.lists ? { citationLists: progress.sources.lists } : {}),
            }
          : {}),
        video: outcome.video,
        screenshot: captured.screenshot,
        uiDump: captured.uiDump,
        ...(screen.linkStop ? { linkStop: screen.linkStop } : {}),
        ...(evidenceIssue !== undefined ? { evidenceIssue } : {}),
      });

      const at = this.now();
      if (!request.task && failure === "rate_limited") {
        // The binding stops for the rest of the day, across restarts.
        await store.markBindingRateLimited(binding.key, at, answerId);
        this.options.gate?.noteRateLimited(binding.key, at);
      }
      if (!request.task && failure === "emulator_rejected")
        await store.markPlatformUnavailable(binding.platformCode, {
          failureCode: failure,
          at,
          batchId,
          answerId,
        });
      return result;
    } finally {
      await screen.leave();
    }
  };

  // From opening the App to the read answer. Throws the failure class; `progress` keeps what
  // was reached.
  private async ask(
    screen: Screen,
    request: AttemptRequest,
    attempt: { answerId: string; sentText: string },
    progress: Progress,
    recording: ActiveRecording,
  ): Promise<void> {
    const { contract, driver } = screen;
    const { binding, batchId, questionText } = request;
    await screen.open();

    // A new conversation without history.
    await screen.runSteps(contract.newConversation.steps);
    const { emptyMarker, emptyWhenAbsent } = contract.newConversation;
    if (emptyMarker) await screen.require(emptyMarker);
    // A view every conversation with a message shows is still there: this is not a new one.
    for (const selector of emptyWhenAbsent ?? [])
      if (!(await screen.waitGone(selector, this.timings.stepTimeoutMs)))
        throw changed("conversation_not_empty");

    // Fill the composer with the question as it is and read it back.
    await screen.runSteps(contract.composer.prepare);
    await driver.setText(await screen.require(contract.composer.input), attempt.sentText);
    const filled = await driver.readText(await screen.require(contract.composer.input));
    if (filled !== attempt.sentText) throw changed("composer_readback_differs");
    const send = await screen.require(contract.composer.send);

    // A question is never sent without a running recording (with recording switched off there
    // is nothing that could have stopped), and never after a stop request.
    if (!recording.isRecording()) throw new AppChannelError("recording_failed");
    if (request.signal.aborted) throw new AppChannelError("collection_timeout");
    if (request.task) {
      if (!request.onSendIntent || !request.onConfirmedSend) throw new Error("send_hooks_required");
      const onSendIntent = request.onSendIntent;
      // Appium can take time to revalidate the target. Check the lease after that
      // lookup, at the last boundary before issuing the irreversible click.
      await driver.tap(send, async () => {
        await onSendIntent();
        if (request.signal.aborted) throw new AppChannelError("collection_timeout");
        if (!recording.isRecording()) throw new AppChannelError("recording_failed");
        progress.attemptedTap = true;
      });
    }
    const sentAt = this.now();
    const entry: LedgerEntry = {
      answerId: attempt.answerId,
      batchId,
      deviceId: binding.device.id,
      platformCode: binding.platformCode,
      sentAt: sentAt.toISOString(),
    };
    if (request.task) {
      progress.sentAt = sentAt;
      await request.onConfirmedSend!(entry.sentAt);
      await this.options.store.appendLedger(entry);
    } else {
      // The attended local trial conservatively counts the send before tapping.
      if (!this.options.gate) throw new Error("trial_rate_gate_required");
      await this.options.store.appendLedger(entry);
      this.options.gate.noteSent(entry);
      progress.sentAt = sentAt;
      progress.attemptedTap = true;
      request.onSent();
    }
    this.log({
      event: "app_attempt_sent",
      batchId,
      device: binding.device.id,
      platform: binding.platformCode,
      answerId: attempt.answerId,
      at: entry.sentAt,
    });
    if (!request.task) await driver.tap(send);
    await screen.waitForCompletion();
    progress.completedAt = this.now();

    // Sources are best effort: an entry whose list cannot be read whole (panel not opened, empty
    // or still moving list, a count other than the stated one, panel not closed, a failed driver
    // command) leaves the sample without the sources of that path instead of failing it. Nothing
    // partial is kept.
    const readPath = async (path: SourcePath): Promise<Sources> => {
      let sources: Sources;
      try {
        sources = await screen.readCitations(path);
      } catch (error) {
        const issue =
          error instanceof DriverError
            ? `driver_${error.kind}`
            : error instanceof AppChannelError && error.code === "app_contract_changed"
              ? (error.cause ?? "contract_changed")
              : undefined;
        if (issue === undefined) throw error;
        // A crash or a blocker (login, verification, rate limit, emulator refused) is not a
        // source problem: it keeps its own class and fails the attempt.
        const seen = await screen.blockingState();
        if (seen) throw seen;
        this.log({
          event: "app_citations_unreadable",
          batchId,
          device: binding.device.id,
          platform: binding.platformCode,
          answerId: attempt.answerId,
          // Which of several source lists; a contract with one path names none.
          ...(path.label !== undefined ? { list: path.label } : {}),
          cause: issue,
          ...driverDetail(error),
        });
        if (await screen.closeSourcePanel(path)) {
          // Leaving the panel took seconds. What follows swipes and taps on this screen, so it
          // is looked at once more: a crash or blocker that showed meanwhile is left alone.
          const now = await screen.blockingState();
          if (now) throw now;
        }
        // A panel that would not close covers the conversation even where the views under it
        // are still in the hierarchy: nothing is tapped or copied through it, no further path
        // is started under it, and no sample is saved with it on screen.
        if (await screen.sourcePanelOpen()) throw changed("answer_end_not_visible");
        return { citations: [], searchQueries: [], status: "unreadable", issue };
      }
      if (sources.links) {
        logLinks(path, sources.citations, sources.links);
        // The read of the links ended on a page that stayed, without the panel, or on a failed
        // driver command, after which nothing says what is on screen. The list was
        // read whole and its sources keep what was read of them; what is open is left as after
        // a failed read, and with a page or panel still on screen nothing is saved.
        if (sources.links.leftOpen) {
          screen.linkPageLeftOpen = true;
          // A panel that did not come back can be one a blocker took the place of (a
          // verification after a source's page): as after a failed read, it keeps its own class,
          // before anything is pressed, swiped or revealed on it.
          const seen = await screen.blockingState();
          if (seen) throw seen;
          if (await screen.closeSourcePanel(path)) {
            const now = await screen.blockingState();
            if (now) throw now;
          }
          if (await screen.sourcePanelOpen()) throw changed("answer_end_not_visible");
        }
      }
      return sources;
    };
    // One line per source whose link was not got and one per list: positions (from 1, in the
    // order of the list), cause words and counts, never a title or a link.
    const logLinks = (path: SourcePath, citations: readonly Citation[], read: LinkRead) => {
      const list = path.label !== undefined ? { list: path.label } : {};
      citations.forEach((source, index) => {
        if (source.link && "issue" in source.link)
          this.log({
            event: "app_citation_link_missing",
            answerId: attempt.answerId,
            ...list,
            index: index + 1,
            issue: source.link.issue,
          });
      });
      const copied = citations.filter((source) => source.link && "copied" in source.link);
      this.log({
        event: "app_citation_links",
        answerId: attempt.answerId,
        ...list,
        total: citations.length,
        withUrl: copied.length,
        restored: copied.filter(
          (source) => source.link && "copied" in source.link && source.link.restored,
        ).length,
        elapsedMs: read.elapsedMs,
        ...(read.stoppedBy ? { stoppedBy: read.stoppedBy } : {}),
        // The failed command's name and protocol error code, as in other failure lines.
        ...(read.driver ? { driver: read.driver } : {}),
        // How each screen of the list went, where the read ended early or left a source not
        // reached (design §12.1); a read that got through has the line it always had.
        ...(read.stoppedBy !== undefined ||
        citations.some(
          (source) => source.link && "issue" in source.link && source.link.issue === "not_reached",
        )
          ? {
              screens: read.screens,
              ...(read.screensDropped > 0 ? { screensDropped: read.screensDropped } : {}),
              ...(read.listStop ? { listStop: read.listStop } : {}),
            }
          : {}),
      });
    };
    const endVisible = async () =>
      (await screen.waitFor(contract.completion.marker, this.timings.stepTimeoutMs)) !== undefined;
    // Reads the contract's source paths one after the other, in the order written: a path whose
    // read failed has left its screens by then and does not keep the next one from being read.
    // Returns whether the end of the answer is back on screen afterwards.
    const readSources = async () => {
      const paths = sourcePaths(contract);
      const lists: Sources[] = [];
      for (const path of paths) {
        if (lists.length > 0) {
          // The path before this one opened and left screens since the last look for a crash or
          // blocker. Finding this path's entry swipes and taps on what is on screen now, so it
          // is looked at first: nothing is done on a crash or blocker.
          const seen = await screen.blockingState();
          if (seen) throw seen;
          // Nor is an entry looked for and tapped on a screen that is not the conversation. A
          // read that failed was left by its clean-up, and a screen between entry and list by
          // the back key: a press too many leaves the conversation as well, for a screen of the
          // same App on which neither a source screen nor a crash is to be seen. After either,
          // the end of the answer must be back before the next path is begun, as it must be
          // before the sample is saved.
          if (
            (lists.at(-1)!.status === "unreadable" ||
              screen.sourceStepsOpened ||
              screen.linkPageLeftOpen) &&
            !(await screen.revealEnd()) &&
            !(await endVisible())
          )
            throw changed("answer_end_not_visible");
        }
        lists.push(await readPath(path));
      }
      // Several paths: every source names its list, and a source that two lists show is kept
      // once for each. One path: the record is what that path gave, as it always was.
      const several = Array.isArray(contract.citations);
      progress.citations = lists.flatMap((list, index) =>
        several
          ? list.citations.map((source) => ({ ...source, list: paths[index]!.label! }))
          : list.citations,
      );
      // Several paths: each keyword once, in the order the paths gave them. One path: its
      // keywords as it gave them; a panel that names a keyword twice is recorded as it is.
      const queries = lists.flatMap((list) => list.searchQueries);
      progress.searchQueries = several ? [...new Set(queries)] : queries;
      progress.sources = {
        ...(paths.length === 0 ? { status: "not_declared" as const } : overallCitations(lists)),
        ...(several
          ? {
              lists: lists.map((list, index) => ({
                label: paths[index]!.label!,
                status: list.status,
                ...(list.issue !== undefined ? { issue: list.issue } : {}),
                count: list.citations.length,
              })),
            }
          : {}),
        failed: lists.some((list) => list.status === "unreadable"),
      };
      // The copy control and the final screenshot need the end of the answer on screen.
      return screen.revealEnd();
    };
    // A copy hides the screen behind the system's clipboard preview for a while, so with that
    // method the sources are read first and the copy comes last.
    const copies = contract.answer.method === "copy";
    // The copy control is chosen among the visible matches of its selector. Without the end of
    // the answer on screen that could be the control of something else (a code block, another
    // message), so nothing is copied then.
    if (copies && !(await readSources()) && !(await endVisible()))
      throw changed("answer_end_not_visible");

    const answer = await screen.readAnswer(attempt.sentText);
    progress.snapshots = answer.snapshots;
    // An empty read or the question coming back is not an answer. Apps re-space the text they
    // show (see `answerWindow`), so whitespace is not compared here either.
    if (answer.text === "" || squash(answer.text) === squash(questionText))
      throw changed("answer_empty_or_echo");
    if (copies) {
      // The very text the last copy on this device gave: the copy control did nothing and the
      // clipboard still holds an earlier answer. Only copies of this run are known; what the
      // clipboard held before the run started cannot be told apart from an answer.
      const device = binding.device.id;
      if (this.lastCopied.get(device) === answer.text) throw changed("clipboard_unchanged");
      // Nor is the link the last source's page put on the clipboard.
      if (screen.lastLink !== undefined && screen.lastLink === answer.text)
        throw changed("clipboard_unchanged");
      this.lastCopied.set(device, answer.text);
    }
    progress.answerText = answer.text;

    if (copies) await screen.settleAfterCopy();
    // After a source read that failed (of the one path, or of any of several), the conversation
    // must be back: a sample whose final screen is a source panel that would not close is not
    // saved. The same holds, however the read ended, once the screen between entry and source
    // list of a path was opened: every screen on the way back is left by the back key, and a
    // press too many leaves the conversation as well (the answer text is read by then; the
    // sample would be saved over whatever screen the press led to).
    else if (
      !(await readSources()) &&
      (progress.sources?.failed || screen.sourceStepsOpened || screen.linkPageLeftOpen) &&
      !(await endVisible())
    )
      throw changed("answer_end_not_visible");
    // A panel that did not show in time when its entry was tapped may show later. The last look
    // before the final screenshot: a failed source read never leaves its panel on a sample, nor
    // does a read of links that had to be cleaned up its page.
    if ((progress.sources?.failed || screen.linkPageLeftOpen) && (await screen.sourcePanelOpen()))
      throw changed("answer_end_not_visible");
  }
}

export interface LoginProbeOptions {
  contracts: Pick<ContractSource, "verified">;
  drivers: Pick<DriverProvider, "open">;
  timings?: Partial<AdapterTimings>;
  now?: () => Date;
  sleep?: Sleep;
  // Aborted: remaining bindings are not probed.
  signal?: AbortSignal;
  log?: Log;
}
// Self-check plug-in (check.ts): is the binding's account logged in? Opens the App and looks at
// the contract's logged-in marker; it never types, sends, records or touches a login or
// verification screen.
export function createLoginProbe(options: LoginProbeOptions): LoginProbe {
  const timings = { ...DEFAULT_TIMINGS, ...options.timings };
  const now = options.now ?? (() => new Date());
  return async (binding, device) => {
    if (device.serial === null || device.actual === null) return "device_offline";
    if (options.signal?.aborted) throw new Error("stopped");
    let contract: AppContract;
    try {
      contract = await options.contracts.verified(binding, {
        // check.ts has already compared the installed version with the reviewed one.
        installedAppVersion: binding.reviewedAppVersion,
        deviceLocale: device.actual.locale,
        now: now(),
      });
    } catch (error) {
      if (error instanceof AppChannelError) return error.code;
      throw error;
    }
    let driver: DeviceDriver;
    try {
      driver = await options.drivers.open({ deviceId: binding.device.id, serial: device.serial });
    } catch (error) {
      if (error instanceof DriverError) return "device_offline";
      throw error;
    }
    const screen = new Screen(
      driver,
      contract,
      timings,
      now,
      options.sleep ?? realSleep,
      options.signal,
    );
    try {
      await screen.open();
      await screen.runSteps(contract.loggedIn.open);
      if (!(await screen.waitFor(contract.loggedIn.marker, timings.stepTimeoutMs))) {
        // A blocker or crash has its own class; otherwise the account is not logged in.
        await screen.assertHealthy();
        return "login_required";
      }
      await screen.runSteps(contract.loggedIn.close);
      return "logged_in";
    } catch (error) {
      const failureCode =
        error instanceof AppChannelError
          ? error.code
          : error instanceof DriverError
            ? await screen.classifyDriverFailure()
            : undefined;
      if (failureCode === undefined) throw error;
      options.log?.({
        event: "app_login_probe_failed",
        device: binding.device.id,
        platform: binding.platformCode,
        failureCode,
        cause:
          error instanceof AppChannelError
            ? (error.cause ?? null)
            : `driver_${(error as DriverError).kind}`,
        ...driverDetail(error),
      });
      return failureCode;
    } finally {
      await screen.leave();
    }
  };
}
