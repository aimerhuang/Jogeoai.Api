import { lstat, readFile, readdir, rm } from "node:fs/promises";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import {
  AppAdapter,
  DEFAULT_TIMINGS,
  createLoginProbe,
  type AdapterTimings,
  type AttemptRequest,
} from "./adapter.ts";
import { parseBindings, resolveBindings } from "./bindings.ts";
import { ContractSource, appContractSchema } from "./contract.ts";
import { DeviceInspector } from "./devices.ts";
import { DriverError, MAX_SWIPES_PER_CALL, type DeviceDriver } from "./driver.ts";
import { EvidenceStore, sha256File } from "./evidence.ts";
import {
  MOCK_PACKAGE,
  MockChatApp,
  MockDrivers,
  MockIconApp,
  type MockIconAppOptions,
  type MockChatOptions,
  type MockDriverOptions,
  type MockModel,
  type MockNode,
  type MockScreen,
} from "./mock-driver.ts";
import { RateGate } from "./rate-gate.ts";
import { ScreenRecorder, disabledScreenRecorder } from "./recorder.ts";
import { pendingCells } from "./scheduler.ts";
import {
  DEVICE_A,
  bindingsFixture,
  emulatorA,
  emulatorB,
  evidenceDirectory,
  fakeAdb,
  fakeRecorderDevice,
  ffprobeAnswer,
  iconAppContract,
  referenceContract,
  steppingClock,
  type FakeEmulator,
  type FakeRecorderOptions,
} from "./test-fixtures.ts";

const QUESTION = "合成问题";
const BATCH = "batch-1";
const ANSWER_IDS = ["aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"];

const created: string[] = [];
const stores: EvidenceStore[] = [];
afterEach(async () => {
  for (const store of stores.splice(0)) await store.close().catch(() => undefined);
  for (const directory of created.splice(0)) await rm(directory, { recursive: true, force: true });
});

interface HarnessOptions {
  app?: Omit<MockChatOptions, "now">;
  // The device shows the App that draws its own screen instead (`MockIconApp`), and the
  // contract is the one written for it.
  icons?: Omit<MockIconAppOptions, "now">;
  iconContract?: (contract: ReturnType<typeof iconAppContract>) => void;
  // The approved question to ask; default QUESTION.
  question?: string;
  // "off": recording switched off, as the script runs by default. The harness records unless
  // told otherwise, so that the recording rules stay covered.
  recording?: "off";
  emulators?: FakeEmulator[];
  recorder?: FakeRecorderOptions;
  contract?: (contract: ReturnType<typeof referenceContract>) => void;
  emptyReads?: number;
  timings?: Partial<AdapterTimings>;
  // Replaces what the device shows; gets the App's own screen and the App.
  screen?: (screen: MockScreen, app: MockChatApp) => MockScreen;
}
async function harness(options: HarnessOptions = {}) {
  const clock = steppingClock();
  const store = await EvidenceStore.open(await evidenceDirectory(created));
  stores.push(store);
  const config = parseBindings(bindingsFixture());
  const binding = resolveBindings(config)[0]!;
  const app = new MockChatApp({ now: clock.now, ...options.app });
  const recorderDevice = fakeRecorderDevice(options.recorder);
  const adb = fakeAdb(options.emulators ?? [emulatorA(), emulatorB()]);
  const gate = new RateGate({
    ledger: [],
    bindingStatus: { version: 1, bindings: {} },
    now: clock.now,
  });
  const icons = new MockIconApp({ now: clock.now, ...options.icons });
  const iconContract = iconAppContract();
  options.iconContract?.(iconContract);
  const contract = referenceContract();
  options.contract?.(contract);
  // Set by a test to make driver commands fail or to act at a given moment.
  const hooks: { intercept?: MockDriverOptions["intercept"] } = {};
  const model: MockModel = options.icons
    ? icons
    : options.screen
      ? {
          screen: () => options.screen!(app.screen(), app),
          activate: (packageName) => app.activate(packageName),
          terminate: (packageName) => app.terminate(packageName),
          back: () => app.back(),
          home: () => app.home(),
          swipe: (direction) => app.swipe(direction),
          clipboard: () => app.clipboard(),
        }
      : app;
  const drivers = new MockDrivers({ [DEVICE_A]: model }, () => ({
    intercept: (method) => hooks.intercept?.(method),
    emptyReads: options.emptyReads,
  }));
  const logs: Record<string, unknown>[] = [];
  let nextId = 0;
  let sentCallbacks = 0;
  const adapter = new AppAdapter({
    store,
    gate,
    recorder:
      options.recording === "off"
        ? disabledScreenRecorder(clock.now)
        : new ScreenRecorder({
            runner: recorderDevice.runner,
            now: clock.now,
            sleep: async () => undefined,
          }),
    inspector: new DeviceInspector({ runner: adb, devices: config.devices }),
    contracts: new ContractSource({
      load: async () => appContractSchema.parse(options.icons ? iconContract : contract),
      log: (line) => logs.push(line),
    }),
    drivers,
    scriptRevision: "test-revision",
    timings: options.timings,
    now: clock.now,
    sleep: clock.sleep,
    newAnswerId: () => ANSWER_IDS[nextId++]!,
    log: (line) => logs.push(line),
  });
  const collect = (
    signal: AbortSignal = new AbortController().signal,
    leased?: Pick<AttemptRequest, "task" | "onSendIntent" | "onConfirmedSend">,
  ) =>
    adapter.collect({
      batchId: BATCH,
      binding,
      questionText: options.question ?? QUESTION,
      signal,
      onSent: () => void (sentCallbacks += 1),
      ...leased,
    });
  const records = () => store.readRecords(BATCH);
  const recordFiles = async (answerId = ANSWER_IDS[0]!) =>
    (await readdir(join(store.root, "batches", BATCH, "records", answerId))).sort();
  const driverCalls = () => drivers.opened.flatMap((driver) => driver.calls);
  return {
    clock,
    store,
    binding,
    app,
    icons,
    recorderDevice,
    gate,
    drivers,
    hooks,
    logs,
    collect,
    records,
    recordFiles,
    driverCalls,
    sentCallbacks: () => sentCallbacks,
  };
}
// Nothing reached the App's composer or send button, and no send was counted.
async function expectNothingSent(h: Awaited<ReturnType<typeof harness>>) {
  expect(h.app.sent).toEqual([]);
  expect(await h.store.readLedger()).toEqual([]);
  expect(h.gate.counts(h.binding)).toEqual({
    bindingLastHour: 0,
    bindingLast24h: 0,
    platformToday: 0,
  });
  expect(h.sentCallbacks()).toBe(0);
}

describe("one collection attempt", () => {
  it("reads an ID-less source list card by card through the slot template", async () => {
    const h = await harness({
      app: {
        sourceCards: true,
        citations: [
          { title: "合成来源一", site: "合成站点一", url: "https://example.invalid/1" },
          { title: "合成来源二", site: "合成站点二", url: "https://example.invalid/2" },
        ],
      },
      contract: (contract) => {
        const citations = contract.citations as Record<string, unknown>;
        delete citations.itemTitle;
        delete citations.itemSite;
        delete citations.itemUrl;
        citations.cards = {
          layout: [
            { name: "index", pattern: "\\d+" },
            { name: "title" },
            { name: "site" },
            { name: "url", pattern: "https?://\\S+" },
          ],
        };
      },
    });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    const [record] = await h.records();
    expect(record!.citations).toEqual([
      { title: "合成来源一", site: "合成站点一", url: "https://example.invalid/1" },
      { title: "合成来源二", site: "合成站点二", url: "https://example.invalid/2" },
    ]);
  });

  it("reads the sources before the copy, whose clipboard preview hides the screen", async () => {
    const h = await harness({
      app: {
        copyOverlayMs: 6_000,
        citations: [{ title: "合成来源一" }, { title: "合成来源二" }],
      },
      contract: (contract) =>
        void ((contract as { answer: unknown }).answer = {
          method: "copy",
          copy: { id: "com.larus.nova:id/msg_action_copy" },
        }),
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record!.answerText).toBe("合成回答第一段\n合成回答第二段");
    // With the copy first, the source entry would be hidden and the answer saved without sources.
    expect(record!.citations).toEqual([
      { title: "合成来源一", site: null, url: null },
      { title: "合成来源二", site: null, url: null },
    ]);
    expect(record!.searchState).toMatchObject({ webSearch: "on" });
  });

  it("saves a source list only when its length is the number the App states", async () => {
    const counted = (stated: number) =>
      harness({
        app: {
          statedSources: stated,
          citations: [{ title: "合成来源一" }, { title: "合成来源二" }],
        },
        contract: (contract) =>
          Object.assign(contract.citations, {
            count: {
              from: "panel",
              selector: { id: "com.larus.nova:id/bottom_sheet_title" },
              pattern: "参考资料\\s*(\\d+)",
            },
            // A panel without a close control is left with the back key.
            close: { back: true },
          }),
      });
    const whole = await counted(2);
    expect(await whole.collect()).toMatchObject({ status: "valid" });
    expect((await whole.records())[0]!.citations).toHaveLength(2);

    // One source is missing from what was read: nothing is saved as if it were the whole list.
    // The answer is still a sample (AC-A16); its record says why it has no sources.
    const short = await counted(3);
    expect(await short.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await short.records())[0]).toMatchObject({
      answerText: "合成回答第一段\n合成回答第二段",
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "citation_count",
    });
    expect(short.logs.find((line) => line.event === "app_citations_unreadable")).toEqual({
      event: "app_citations_unreadable",
      batchId: BATCH,
      device: DEVICE_A,
      platform: "doubao",
      answerId: ANSWER_IDS[0],
      cause: "citation_count",
      numbers: { stated: 3, read: 2 },
    });
    expect(short.logs.map((line) => line.event)).not.toContain("app_attempt_failure_cause");
  });

  it("does not take answer controls that show mid-answer as the end of the answer", async () => {
    const h = await harness({ app: { controlsWhileStreaming: true, generationMs: 20_000 } });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    // The whole answer, not the part that was on screen when the controls first showed.
    expect(record!.answerText).toBe("合成回答第一段\n合成回答第二段");
    const sentAt = Date.parse(record!.sentAt!.utc);
    expect(Date.parse(record!.completedAt!.utc) - sentAt).toBeGreaterThanOrEqual(20_000);
  });

  it("presses back until a source panel without a close control is gone, a bounded number of times", async () => {
    const closing = (backs: number) =>
      harness({
        app: { sourcePanelBacks: backs, citations: [{ title: "合成来源一" }] },
        contract: (contract) => Object.assign(contract.citations, { close: { back: true } }),
      });
    const twice = await closing(2);
    expect(await twice.collect()).toMatchObject({ status: "valid" });
    expect((await twice.records())[0]!.citations).toHaveLength(1);

    // A panel that stays: the sources are given up, and since the conversation does not come
    // back either, the attempt fails. The answer that was read is not saved as a sample.
    const stuck = await closing(9);
    expect(await stuck.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(stuck.logs.find((line) => line.event === "app_citations_unreadable")).toMatchObject({
      cause: "source_panel_not_closed",
    });
    expect(stuck.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      cause: "answer_end_not_visible",
    });
    // Bounded: three presses of the read, three of the clean-up.
    expect(stuck.driverCalls().filter((call) => call === "back")).toHaveLength(6);
    const [record] = await stuck.records();
    expect(record!.citations).toEqual([]);
    expect(record!.citationsStatus).toBeUndefined();
  });

  it("scrolls back through an answer held in one view taller than the screen", async () => {
    // Inside the tall view every swipe leaves the hierarchy as it was; that is not a stuck list.
    const h = await harness({ app: { tallAnswerRows: 12, viewport: 4 } });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await h.records())[0]!.answerText).toBe("合成回答第一段\n合成回答第二段");

    // A view so tall that the question is not reached within the allowed unchanged swipes is not
    // saved as an answer.
    const endless = await harness({ app: { tallAnswerRows: 60, viewport: 4 } });
    expect(await endless.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(endless.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      cause: "answer_list_not_moving",
    });
  });

  it("keeps follow-up suggestions below the answer controls out of the answer", async () => {
    const followUps = ["合成建议追问一", "合成建议追问二"];
    // Without an end marker the suggestions cannot be told from answer text.
    const open = await harness({ app: { followUps } });
    expect(await open.collect()).toMatchObject({ status: "valid" });
    expect((await open.records())[0]!.answerText).toContain("合成建议追问一");

    const bounded = await harness({
      app: { followUps },
      contract: (contract) =>
        Object.assign(contract.answer, { end: { id: "com.larus.nova:id/msg_action_copy" } }),
    });
    expect(await bounded.collect()).toMatchObject({ status: "valid" });
    expect((await bounded.records())[0]!.answerText).toBe("合成回答第一段\n合成回答第二段");
  });

  it("reads the stated number of sources before the row that shows it scrolls away", async () => {
    const citations = Array.from({ length: 7 }, (_, index) => ({ title: `合成来源${index + 1}` }));
    const h = await harness({
      app: { citations, sourcesViewport: 3, statedSourcesInList: true },
      contract: (contract) =>
        Object.assign(contract.citations, {
          count: {
            from: "panel",
            selector: { id: "com.larus.nova:id/mock_reference_count" },
            pattern: "参考\\s*(\\d+)\\s*篇资料",
          },
        }),
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await h.records())[0]!.citations).toHaveLength(7);
  });

  it("recognises an empty conversation by the absence of message controls", async () => {
    const h = await harness({
      contract: (contract) => {
        const conversation = contract.newConversation as Record<string, unknown>;
        delete conversation.emptyMarker;
        conversation.emptyWhenAbsent = [{ id: "com.larus.nova:id/msg_action_regenerate" }];
      },
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect(h.app.sent).toHaveLength(1);
  });

  it("does not ask in a conversation that still shows an earlier message", async () => {
    const h = await harness({
      contract: (contract) =>
        void Object.assign(contract, {
          // Steps that lead back to the conversation that was open.
          newConversation: {
            steps: [{ tap: { id: "com.larus.nova:id/back_icon" } }, { back: true }],
            emptyWhenAbsent: [{ id: "com.larus.nova:id/msg_action_regenerate" }],
          },
        }),
    });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    h.clock.advance(600_000);
    expect(await h.collect()).toMatchObject({
      answerId: ANSWER_IDS[1],
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "pre_send",
    });
    expect(h.app.sent).toHaveLength(1);
    expect(await h.store.readLedger()).toHaveLength(1);
    expect(
      h.logs.filter((line) => line.event === "app_attempt_failure_cause").at(-1),
    ).toMatchObject({ cause: "conversation_not_empty" });
  });

  it("does not take what an earlier copy left in the clipboard for the answer", async () => {
    const h = await harness({
      // From the second conversation on the copy control does nothing.
      app: { answers: [["合成回答甲"], ["合成回答乙"]], copyDeadFrom: 2 },
      contract: (contract) =>
        void (contract.answer = {
          method: "copy",
          copy: { id: "com.larus.nova:id/msg_action_copy" },
        }),
    });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    h.clock.advance(600_000);
    // The clipboard still holds the first answer: it is not saved as the answer to this question.
    expect(await h.collect()).toMatchObject({
      answerId: ANSWER_IDS[1],
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    const records = await h.records();
    expect(records.find((record) => record.answerId === ANSWER_IDS[0])!.answerText).toBe(
      "合成回答甲",
    );
    expect(records.find((record) => record.answerId === ANSWER_IDS[1])!.answerText).toBeNull();
    expect(
      h.logs.filter((line) => line.event === "app_attempt_failure_cause").at(-1),
    ).toMatchObject({ cause: "clipboard_unchanged" });
  });

  it("copies only with the end of the answer on screen, never from another control the selector finds", async () => {
    let sourcesRead = false;
    const h = await harness({
      app: { viewport: 4, inlineCopy: true, citations: [{ title: "合成来源一" }] },
      contract: (contract) => {
        contract.answer = {
          method: "copy",
          copy: { id: "com.larus.nova:id/msg_action_copy", pick: "last" },
        };
        // The source entry sits above the answer.
        Object.assign(contract.citations, { entrySwipe: { swipe: "down", max: 10 } });
      },
      // Once the sources were read, the App no longer offers its jump-to-end control.
      screen: (screen) => {
        const panel = screen.nodes.some(
          (node) => node.id === "com.larus.nova:id/bottom_sheet_title",
        );
        sourcesRead ||= panel;
        return sourcesRead && !panel
          ? {
              ...screen,
              nodes: screen.nodes.filter(
                (node) => node.id !== "com.larus.nova:id/fast_button_icon",
              ),
            }
          : screen;
      },
    });
    // The only copy control on screen is the code block's: its text is not the answer.
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    const [record] = await h.records();
    expect(record!.answerText).toBeNull();
    expect(record!.citations).toEqual([{ title: "合成来源一", site: null, url: null }]);
    expect(h.driverCalls()).not.toContain("readClipboard");
    expect(h.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      cause: "answer_end_not_visible",
    });
  });

  it("waits for a source list that shows after its panel and never records an opened panel as 'no sources'", async () => {
    const listAfter = (looks: number) => {
      let seen = 0;
      return harness({
        app: { citations: [{ title: "合成来源一" }, { title: "合成来源二" }] },
        // For the first looks the open panel shows its title and close control only.
        screen: (screen) => {
          if (!screen.nodes.some((node) => node.id === "com.larus.nova:id/bottom_sheet_title"))
            return screen;
          seen += 1;
          return seen > looks
            ? screen
            : { ...screen, nodes: screen.nodes.filter((node) => node.id !== undefined) };
        },
      });
    };
    const late = await listAfter(4);
    expect(await late.collect()).toMatchObject({ status: "valid" });
    expect((await late.records())[0]!.citations).toHaveLength(2);

    // AC-A16: the sample is valid without sources, and its record says "unreadable", not
    // "absent".
    const never = await listAfter(Number.POSITIVE_INFINITY);
    expect(await never.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await never.records())[0]).toMatchObject({
      answerText: "合成回答第一段\n合成回答第二段",
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "source_list_empty",
    });
    expect(never.logs.find((line) => line.event === "app_citations_unreadable")).toMatchObject({
      cause: "source_list_empty",
    });
  });

  it("checks a source list against the number its entry states", async () => {
    const counted = (dropLast: boolean) =>
      harness({
        app: { citations: [{ title: "合成来源一" }, { title: "合成来源二" }] },
        contract: (contract) =>
          Object.assign(contract.citations, {
            entry: { id: "com.larus.nova:id/tv_reference_title" },
            count: { from: "entry", pattern: "参考\\s*(\\d+)\\s*篇资料" },
          }),
        // The panel shows one source fewer than the entry stated.
        screen: (screen) =>
          dropLast &&
          screen.nodes.some((node) => node.id === "com.larus.nova:id/bottom_sheet_title")
            ? { ...screen, nodes: screen.nodes.slice(0, -1) }
            : screen,
      });
    const whole = await counted(false);
    expect(await whole.collect()).toMatchObject({ status: "valid" });
    expect((await whole.records())[0]!.citations).toHaveLength(2);

    // The one source that was read is not kept as if it were the list.
    const short = await counted(true);
    expect(await short.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await short.records())[0]).toMatchObject({
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "citation_count",
    });
    // The two numbers are logged, nothing else of the screen.
    expect(short.logs.find((line) => line.event === "app_citations_unreadable")).toMatchObject({
      cause: "citation_count",
      numbers: { stated: 2, read: 1 },
    });
    expect(JSON.stringify(short.logs)).not.toContain("篇资料");
  });

  it("joins the windows of a card list across a cut card and equal neighbours without losing a source", async () => {
    const citations = [
      { title: "合成来源一", site: "合成站点一" },
      { title: "合成来源二", site: "合成站点二" },
      { title: "合成来源三", site: "合成站点三" },
      // The same article listed twice.
      { title: "合成来源三", site: "合成站点三" },
      { title: "合成来源五", site: "合成站点五" },
      { title: "合成来源六", site: "合成站点六" },
    ];
    const h = await harness({
      // After every swipe the card above the list's top edge shows without its first line and
      // still fits the template, as another source.
      app: { sourceCards: true, cutTopCard: true, sourcesViewport: 3, citations },
      contract: (contract) => {
        const sources = contract.citations as Record<string, unknown>;
        delete sources.itemTitle;
        sources.cards = {
          layout: [{ name: "rank" }, { name: "title" }, { name: "site", optional: true }],
        };
      },
    });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    expect((await h.records())[0]!.citations).toEqual(
      citations.map((source) => ({ ...source, url: null })),
    );
  });

  it("leaves what the conversation shows above the sent question out of the answer", async () => {
    const h = await harness({
      screen: (screen) => ({
        ...screen,
        nodes: screen.nodes.map((node) =>
          node.id === "com.larus.nova:id/message_list"
            ? {
                ...node,
                children: [
                  { className: "android.widget.TextView", text: "合成会话提示" },
                  ...(node.children ?? []),
                ],
              }
            : node,
        ),
      }),
    });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    expect((await h.records())[0]!.answerText).toBe("合成回答第一段\n合成回答第二段");
  });

  it("gives a back press time to close the source panel before it looks again", async () => {
    let slowLooks = false;
    let advance: (ms: number) => void = () => undefined;
    const h = await harness({
      app: { panelCloseMs: 300, citations: [{ title: "合成来源一" }] },
      contract: (contract) => Object.assign(contract.citations, { close: { back: true } }),
      // A look takes a while and returns the screen as it was when the look began.
      screen: (screen) => {
        if (slowLooks) advance(2_500);
        return screen;
      },
    });
    advance = h.clock.advance;
    h.hooks.intercept = (method) => {
      if (method === "back") slowLooks = true;
    };
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    // A second press would have landed on the conversation and left the App.
    expect(h.driverCalls().filter((call) => call === "back")).toHaveLength(1);
    const directory = join(h.store.root, "batches", BATCH, "records", ANSWER_IDS[0]!);
    expect(await readFile(join(directory, "ui.xml"), "utf8")).toContain(
      "com.larus.nova:id/msg_action_regenerate",
    );
  });

  it("asks in a new conversation and records answer, sources, evidence and metadata", async () => {
    const h = await harness({
      app: {
        citations: [{ title: "合成来源一" }, { title: "合成来源二" }],
        queries: "合成关键词",
      },
    });
    const outcome = await h.collect();
    expect(outcome).toEqual({
      answerId: ANSWER_IDS[0],
      status: "valid",
      failureCode: null,
      phase: null,
    });
    const [record] = await h.records();
    expect(record).toMatchObject({
      answerId: ANSWER_IDS[0],
      batchId: BATCH,
      sampleClass: "local_trial",
      channel: "consumer_app",
      scriptRevision: "test-revision",
      questionText: QUESTION,
      platformCode: "doubao",
      package: "com.larus.nova",
      appVersion: "15.7.0",
      deviceId: DEVICE_A,
      adbSerial: "emulator-5556",
      androidVersion: "15",
      abi: "arm64-v8a",
      deviceLocale: "zh-Hans-CN",
      deviceTimezone: "Asia/Shanghai",
      accountLabel: "doubao-a",
      status: "valid",
      answerText: "合成回答第一段\n合成回答第二段",
      citations: [
        { title: "合成来源一", site: null, url: null },
        { title: "合成来源二", site: null, url: null },
      ],
      searchState: { webSearch: "on", deepThinking: "unknown", searchQueries: ["合成关键词"] },
      screenshot: { file: "final.png" },
      uiDump: { file: "ui.xml" },
    });
    // AC-A9: the question itself was sent, once, with nothing before or after it.
    expect(record!.sentText).toBe(QUESTION);
    expect(h.app.sent).toEqual([QUESTION]);
    expect(record!.citationsStatus).toBe("read");
    expect(record).not.toHaveProperty("citationsIssue");
    // The send is in the ledger and in the limits, with the record's send time.
    expect(await h.store.readLedger()).toEqual([
      {
        answerId: ANSWER_IDS[0],
        batchId: BATCH,
        deviceId: DEVICE_A,
        platformCode: "doubao",
        sentAt: record!.sentAt!.utc,
      },
    ]);
    expect(h.gate.counts(h.binding).bindingLast24h).toBe(1);
    expect(h.sentCallbacks()).toBe(1);
    // AC-A13: the video is named after the answer ID and its hash matches the file.
    expect(await h.recordFiles()).toEqual([
      `${ANSWER_IDS[0]}.mp4`,
      "final.png",
      "record.json",
      "ui.xml",
    ]);
    const directory = join(h.store.root, "batches", BATCH, "records", ANSWER_IDS[0]!);
    expect(record!.video).toMatchObject({ file: `${ANSWER_IDS[0]}.mp4`, durationMs: 95_000 });
    expect(record!.video!.sha256).toBe(await sha256File(join(directory, `${ANSWER_IDS[0]}.mp4`)));
    expect(record!.screenshot!.sha256).toBe(await sha256File(join(directory, "final.png")));
    expect((await lstat(join(directory, "ui.xml"))).mode & 0o777).toBe(0o600);
    // The recording covers the whole attempt: started before the App, stopped after reading.
    expect(Date.parse(record!.video!.startedAt)).toBeLessThanOrEqual(
      Date.parse(record!.sentAt!.utc),
    );
    expect(Date.parse(record!.video!.stoppedAt)).toBeGreaterThanOrEqual(
      Date.parse(record!.completedAt!.utc),
    );
    // The App is left on the home screen and the session is closed.
    expect(h.app.screen().foreground).toBe("com.android.launcher3");
    expect(h.drivers.opened.map((driver) => driver.closed)).toEqual([true]);
  });

  it("writes the ledger line and counts the send before tapping send", async () => {
    const h = await harness();
    const order: string[] = [];
    const append = h.store.appendLedger.bind(h.store);
    h.store.appendLedger = async (entry) => {
      order.push(`ledger:${h.app.sent.length}`);
      await append(entry);
    };
    h.hooks.intercept = (method) => {
      if (method === "tap") order.push(`tap:${h.app.sent.length}:${h.sentCallbacks()}`);
    };
    await h.collect();
    // The ledger line exists while nothing is sent yet; the tap that sends comes after it.
    const ledgerAt = order.indexOf("ledger:0");
    expect(ledgerAt).toBeGreaterThan(-1);
    expect(order[ledgerAt + 1]).toBe("tap:0:1");
    expect(order.slice(0, ledgerAt).every((step) => step === "tap:0:0")).toBe(true);
  });

  it("persists intent before a sole formal tap, then confirms the timestamp and records real device facts", async () => {
    const h = await harness({ emulators: [emulatorA({ timezone: "UTC" }), emulatorB()] });
    const order: string[] = [];
    let stamp: string | undefined;
    let expectedStamp: string | undefined;
    const result = await h.collect(undefined, {
      task: { taskId: ANSWER_IDS[1]!, leaseVersion: 2 },
      onSendIntent: async () => {
        order.push(`intent:${h.app.sent.length}`);
        h.hooks.intercept = (method) => {
          if (method === "tap" && !expectedStamp) {
            h.clock.advance(2_500);
            expectedStamp = h.clock.now().toISOString();
          }
        };
      },
      onConfirmedSend: async (at) => {
        order.push(`confirmed:${h.app.sent.length}`);
        stamp = at;
      },
    });
    expect(result.status).toBe("valid");
    expect(order).toEqual(["intent:0", "confirmed:1"]);
    expect(stamp).toBe(expectedStamp);
    expect(h.sentCallbacks()).toBe(0);
    expect(await h.records()).toMatchObject([
      { sentAt: { utc: stamp }, deviceTimezone: "UTC", sampleClass: "control_plane" },
    ]);
    expect(await h.store.readLedger()).toMatchObject([{ sentAt: stamp }]);
  });

  it("does not issue a formal send when the intent store cannot confirm durability", async () => {
    const h = await harness();
    const outcome = h.collect(undefined, {
      task: { taskId: ANSWER_IDS[1]!, leaseVersion: 1 },
      onSendIntent: async () => {
        throw new Error("directory sync failed");
      },
      onConfirmedSend: async () => {
        throw new Error("unexpected send confirmation");
      },
    });
    await expect(outcome).rejects.toThrow("directory sync failed");
    expect(h.app.sent).toEqual([]);
    expect(await h.store.readLedger()).toHaveLength(0);
  });

  it("does not classify an ambiguous formal tap as not sent or click again", async () => {
    const h = await harness();
    let taps = 0;
    const outcome = await h.collect(undefined, {
      task: { taskId: ANSWER_IDS[1]!, leaseVersion: 1 },
      onSendIntent: async () => {
        h.hooks.intercept = (method) => {
          if (method === "tap") {
            taps++;
            throw new DriverError("unreachable");
          }
        };
      },
      onConfirmedSend: async () => {
        throw new Error("must not confirm a lost tap reply");
      },
    });
    expect(taps).toBe(1);
    expect(outcome).toMatchObject({ status: "failed", phase: "post_send" });
    expect(await h.records()).toMatchObject([{ phase: "post_send", sentAt: null }]);
    expect(await h.store.readLedger()).toHaveLength(0);
  });

  it("stops a formal task at the login screen, without sending or persisting intent", async () => {
    const h = await harness({ app: { blocker: { code: "login_required", when: "launch" } } });
    const unexpected = async () => {
      throw new Error("send boundary should not be reached");
    };
    expect(
      await h.collect(undefined, {
        task: { taskId: ANSWER_IDS[1]!, leaseVersion: 2 },
        onSendIntent: unexpected,
        onConfirmedSend: unexpected,
      }),
    ).toMatchObject({ phase: "pre_send", failureCode: "login_required" });
    expect(await h.store.readLedger()).toHaveLength(0);
    expect(h.app.sent).toEqual([]);
  });

  it("logs identifiers, classes and counts only", async () => {
    const h = await harness({ app: { citations: [{ title: "合成来源一" }] } });
    await h.collect();
    const text = JSON.stringify(h.logs);
    for (const secret of [QUESTION, "合成回答", "合成来源", "合成用户", "新对话"])
      expect(text).not.toContain(secret);
    expect(h.logs.map((line) => line.event)).toEqual([
      "app_attempt_started",
      "app_attempt_sent",
      "app_attempt_finished",
    ]);
    expect(h.logs.at(-1)).toMatchObject({
      status: "valid",
      citations: 1,
      citationsStatus: "read",
      answerChars: 15,
    });
  });

  it("logs the cause of a failure as fixed words, never as text from the screen", async () => {
    const blocked = await harness({
      app: { blocker: { code: "login_required", when: "launch" } },
    });
    await blocked.collect();
    expect(blocked.logs.find((line) => line.event === "app_attempt_failure_cause")).toEqual({
      event: "app_attempt_failure_cause",
      batchId: BATCH,
      device: DEVICE_A,
      platform: "doubao",
      answerId: ANSWER_IDS[0],
      failureCode: "login_required",
      cause: "blocker_marker",
    });
    expect(JSON.stringify(blocked.logs)).not.toContain("合成登录页");

    // A failed driver command is named by its method and protocol error code only.
    const broken = await harness();
    broken.hooks.intercept = (method) => {
      if (method === "setText")
        throw new DriverError("command_failed", "elementSendKeys", "invalid element state");
    };
    expect(await broken.collect()).toMatchObject({
      failureCode: "app_contract_changed",
      phase: "pre_send",
    });
    expect(broken.logs.find((line) => line.event === "app_attempt_failure_cause")).toEqual({
      event: "app_attempt_failure_cause",
      batchId: BATCH,
      device: DEVICE_A,
      platform: "doubao",
      answerId: ANSWER_IDS[0],
      failureCode: "app_contract_changed",
      cause: "driver_command_failed",
      command: "elementSendKeys",
      errorName: "invalid element state",
    });
    const text = JSON.stringify(broken.logs);
    for (const secret of [QUESTION, "合成用户", "新对话", "你好"])
      expect(text).not.toContain(secret);
  });

  it("waits for a hierarchy that is empty right after a cold start", async () => {
    const h = await harness({ emptyReads: 2 });
    expect((await h.collect()).status).toBe("valid");
  });

  it("closes a declared popup and nothing else", async () => {
    const h = await harness({ app: { popup: true } });
    expect((await h.collect()).status).toBe("valid");
    expect(h.app.sent).toEqual([QUESTION]);
  });

  it("stitches an answer longer than one screen without gaps or duplicates", async () => {
    const answer = Array.from({ length: 9 }, (_, index) => `合成段落${index + 1}`);
    const h = await harness({
      app: { answer, viewport: 4, citations: [{ title: "合成来源一" }] },
    });
    expect((await h.collect()).status).toBe("valid");
    const [record] = await h.records();
    expect(record!.answerText).toBe(answer.join("\n"));
    expect(record!.citations).toEqual([{ title: "合成来源一", site: null, url: null }]);
    // Scrolling back is plain bounded swipes inside the message list.
    const swipes = h.drivers.opened[0]!.swipes;
    const back = swipes.filter((swipe) => swipe.direction === "down");
    expect(back.length).toBeGreaterThan(2);
    for (const swipe of swipes) expect(swipe.count).toBeLessThanOrEqual(MAX_SWIPES_PER_CALL);
    for (const swipe of back) expect(swipe.within).toBeDefined();
    // The final screenshot and hierarchy are taken with the end of the answer back on screen.
    const directory = join(h.store.root, "batches", BATCH, "records", ANSWER_IDS[0]!);
    const finalScreen = await readFile(join(directory, "ui.xml"), "utf8");
    expect(finalScreen).toContain("com.larus.nova:id/msg_action_regenerate");
    expect(finalScreen).not.toContain("合成段落1&quot;");
    expect(finalScreen).not.toContain("com.larus.nova:id/bottom_sheet_title");
  });

  it("never saves a shortened answer when the start cannot be reached", async () => {
    const answer = Array.from({ length: 30 }, (_, index) => `合成段落${index + 1}`);
    const h = await harness({
      app: { answer, viewport: 4 },
      contract: (contract) => void (contract.answer.maxSwipes = 3),
    });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect((await h.records())[0]!.answerText).toBeNull();
  });

  it("leaves the bubble with the sent text out even when the App re-spaces it", async () => {
    const question = "合成ulinktek问题，合成补充";
    const h = await harness({ question, app: { respaceBubble: true } });
    await h.collect();
    // What was sent is the question; what the bubble shows differs from it in spaces.
    expect(h.app.sent).toEqual([question]);
    expect((await h.records())[0]!.answerText).toBe("合成回答第一段\n合成回答第二段");
  });

  it("takes only the first view with the sent text for the question bubble", async () => {
    // The answer repeats the question as a line of its own.
    const h = await harness({ app: { answer: ["合成回答第一段", QUESTION, "合成回答第三段"] } });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await h.records())[0]!.answerText).toBe(`合成回答第一段\n${QUESTION}\n合成回答第三段`);
  });

  it("tells the question bubble from an answer line with the same text by the bubble's selector", async () => {
    // A long answer whose third paragraph is the question: while scrolling back, that paragraph
    // is on screen before the bubble is.
    const answer = Array.from({ length: 9 }, (_, index) =>
      index === 2 ? QUESTION : `合成段落${index + 1}`,
    );
    // By text alone the paragraph would pass for the bubble and the answer would start after it.
    const h = await harness({ app: { answer, viewport: 4 } });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    expect((await h.records())[0]!.answerText).toBe(answer.join("\n"));
  });

  it("reads a long source list by scrolling, with site and URL only where the panel shows them", async () => {
    const citations = Array.from({ length: 7 }, (_, index) => ({
      title: `合成来源${index + 1}`,
      ...(index === 0 ? { site: "合成站点", url: "https://example.invalid/a" } : {}),
      ...(index === 1 ? { url: "不是链接" } : {}),
    }));
    const h = await harness({
      app: { citations, sourcesViewport: 3, queries: "合成关键词一、合成关键词二" },
      contract: (contract) => {
        Object.assign(contract.citations, {
          itemSite: { id: "com.larus.nova:id/mock_reference_site" },
          itemUrl: { id: "com.larus.nova:id/mock_reference_url" },
          searchQueries: { after: { id: "com.larus.nova:id/bottom_sheet_title" }, separator: "、" },
        });
      },
    });
    expect((await h.collect()).status).toBe("valid");
    const [record] = await h.records();
    expect(record!.citations).toEqual([
      { title: "合成来源1", site: "合成站点", url: "https://example.invalid/a" },
      // Text that is not a link is not stored as a URL.
      { title: "合成来源2", site: null, url: null },
      ...citations.slice(2).map((source) => ({ title: source.title, site: null, url: null })),
    ]);
    expect(record!.searchState!.searchQueries).toEqual(["合成关键词一", "合成关键词二"]);
  });

  it("records no sources and an unknown search state when the answer shows none", async () => {
    const h = await harness();
    await h.collect();
    const [record] = await h.records();
    expect(record!.citations).toEqual([]);
    // AC-A16: no source entry on screen is "absent", not a failed read.
    expect(record!.citationsStatus).toBe("absent");
    expect(record).not.toHaveProperty("citationsIssue");
    expect(h.logs.map((line) => line.event)).not.toContain("app_citations_unreadable");
    expect(record!.searchState).toEqual({
      webSearch: "unknown",
      deepThinking: "unknown",
      searchQueries: [],
    });
  });

  it("does not take a source number for the keyword line", async () => {
    const h = await harness({ app: { citations: [{ title: "合成来源一" }] } });
    await h.collect();
    expect((await h.records())[0]!.searchState!.searchQueries).toEqual([]);
  });

  it("reads the answer through the App's copy control when the contract says so", async () => {
    const h = await harness({
      contract: (contract) =>
        void (contract.answer = {
          method: "copy",
          copy: { id: "com.larus.nova:id/msg_action_copy" },
        }),
    });
    expect((await h.collect()).status).toBe("valid");
    expect((await h.records())[0]!.answerText).toBe("合成回答第一段\n合成回答第二段");
    expect(h.driverCalls()).toContain("readClipboard");
  });
});

// AC-A16 / design §16.2: the answer makes the sample. A source list that cannot be read whole is
// left out and named in the record; it never fails the attempt. (List empty and a count other
// than the stated one are covered above, next to the reads they guard.)
describe("sources are best effort", () => {
  const ANSWER = "合成回答第一段\n合成回答第二段";
  const copyContract = (contract: ReturnType<typeof referenceContract>) =>
    void (contract.answer = {
      method: "copy",
      copy: { id: "com.larus.nova:id/msg_action_copy" },
    });
  const unreadable = (h: Awaited<ReturnType<typeof harness>>) =>
    h.logs.filter((line) => line.event === "app_citations_unreadable");

  it("keeps the answer as a valid sample when the source panel does not open", async () => {
    const h = await harness({
      app: { sourceEntryDead: true, citations: [{ title: "合成来源一" }], queries: "合成关键词" },
    });
    expect(await h.collect()).toEqual({
      answerId: ANSWER_IDS[0],
      status: "valid",
      failureCode: null,
      phase: null,
    });
    const [record] = await h.records();
    expect(record).toMatchObject({
      status: "valid",
      answerText: ANSWER,
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "source_panel_not_opened",
      screenshot: { file: "final.png" },
    });
    expect(record!.searchState!.searchQueries).toEqual([]);
    expect(unreadable(h)).toEqual([
      {
        event: "app_citations_unreadable",
        batchId: BATCH,
        device: DEVICE_A,
        platform: "doubao",
        answerId: ANSWER_IDS[0],
        cause: "source_panel_not_opened",
      },
    ]);
    // No panel on screen, so nothing was pressed to close one.
    expect(h.driverCalls()).not.toContain("back");
    // The final screen is the conversation with the end of the answer.
    const directory = join(h.store.root, "batches", BATCH, "records", ANSWER_IDS[0]!);
    expect(await readFile(join(directory, "ui.xml"), "utf8")).toContain(
      "com.larus.nova:id/msg_action_regenerate",
    );
  });

  it("reads the sources first and still copies the answer when they cannot be read", async () => {
    const h = await harness({
      app: { sourceEntryDead: true, citations: [{ title: "合成来源一" }] },
      contract: copyContract,
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await h.records())[0]).toMatchObject({
      answerText: ANSWER,
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "source_panel_not_opened",
    });
  });

  it("does not keep the part of a list that was still moving when the swipes ran out", async () => {
    const h = await harness({
      app: {
        sourcesViewport: 3,
        citations: Array.from({ length: 7 }, (_, index) => ({ title: `合成来源${index + 1}` })),
        queries: "合成关键词",
      },
      contract: (contract) => Object.assign(contract.citations, { maxSwipes: 1 }),
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({
      answerText: ANSWER,
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "source_list_still_moving",
    });
    expect(record!.searchState!.searchQueries).toEqual([]);
    // The panel was left through its own close control.
    expect(h.driverCalls()).not.toContain("back");
  });

  it("leaves a panel whose close control is missing with the back key and keeps the sample", async () => {
    const h = await harness({
      app: { missing: "sources_close", citations: [{ title: "合成来源一" }] },
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await h.records())[0]).toMatchObject({
      answerText: ANSWER,
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "required_view_missing",
      screenshot: { file: "final.png" },
    });
    expect(h.driverCalls().filter((call) => call === "back")).toHaveLength(1);
    // The App is still in front after that one press.
    const directory = join(h.store.root, "batches", BATCH, "records", ANSWER_IDS[0]!);
    expect(await readFile(join(directory, "ui.xml"), "utf8")).toContain(
      "com.larus.nova:id/msg_action_regenerate",
    );
  });

  it("names a driver command that failed during the source read without failing the attempt", async () => {
    const h = await harness({
      app: {
        sourcesViewport: 3,
        citations: Array.from({ length: 7 }, (_, index) => ({ title: `合成来源${index + 1}` })),
      },
    });
    // The short answer needs no scrolling, so the only swipes are those of the source list.
    h.hooks.intercept = (method) => {
      if (method === "swipe")
        throw new DriverError("command_failed", "performActions", "unknown error");
    };
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await h.records())[0]).toMatchObject({
      answerText: ANSWER,
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "driver_command_failed",
    });
    expect(unreadable(h)).toEqual([
      expect.objectContaining({
        cause: "driver_command_failed",
        command: "performActions",
        errorName: "unknown error",
      }),
    ]);
    expect(JSON.stringify(h.logs)).not.toContain("合成来源");
  });

  it("copies nothing when the conversation does not come back after a failed source read", async () => {
    const h = await harness({
      app: { sourcePanelBacks: 9, citations: [{ title: "合成来源一" }] },
      contract: (contract) => {
        copyContract(contract);
        Object.assign(contract.citations, { close: { back: true } });
      },
    });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(unreadable(h)).toMatchObject([{ cause: "source_panel_not_closed" }]);
    expect(h.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      cause: "answer_end_not_visible",
    });
    expect(h.driverCalls()).not.toContain("readClipboard");
    expect((await h.records())[0]).toMatchObject({ answerText: null, citations: [] });
  });

  // A sheet in the conversation's own window leaves the conversation's views in the hierarchy:
  // "the end of the answer is visible" is then true with the panel still covering it.
  it("saves no sample whose source panel stays open over a conversation that is still in the hierarchy", async () => {
    const h = await harness({
      app: {
        sourcePanelOverConversation: true,
        sourcePanelBacks: 9,
        citations: [{ title: "合成来源一" }],
      },
      contract: (contract) => Object.assign(contract.citations, { close: { back: true } }),
    });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(unreadable(h)).toMatchObject([{ cause: "source_panel_not_closed" }]);
    expect(h.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      cause: "answer_end_not_visible",
    });
    const [record] = await h.records();
    expect(record).toMatchObject({ status: "failed", screenshot: { file: "failure.png" } });
    expect(record!.citationsStatus).toBeUndefined();
    // The screen that was not saved as a sample: the panel, with the answer controls under it.
    const directory = join(h.store.root, "batches", BATCH, "records", ANSWER_IDS[0]!);
    const screen = await readFile(join(directory, "ui.xml"), "utf8");
    expect(screen).toContain("com.larus.nova:id/bottom_sheet_title");
    expect(screen).toContain("com.larus.nova:id/msg_action_regenerate");
  });

  it("taps no copy control through a source panel that stays open over the conversation", async () => {
    const h = await harness({
      app: {
        sourcePanelOverConversation: true,
        sourcePanelBacks: 9,
        citations: [{ title: "合成来源一" }],
      },
      contract: (contract) => {
        copyContract(contract);
        Object.assign(contract.citations, { close: { back: true } });
      },
    });
    let tapsAfterCleanUp = 0;
    h.hooks.intercept = (method) => {
      if (method === "tap" && unreadable(h).length > 0) tapsAfterCleanUp += 1;
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(h.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      cause: "answer_end_not_visible",
    });
    // The copy control is in the hierarchy under the panel; a tap on it would land on the panel.
    expect(tapsAfterCleanUp).toBe(0);
    expect(h.driverCalls()).not.toContain("readClipboard");
    expect((await h.records())[0]).toMatchObject({ answerText: null, citations: [] });
  });

  it("saves no sample when the source panel shows only after its read was given up", async () => {
    // The tap on the source entry takes effect late: the conversation stays on screen until the
    // answer has been copied, then the panel is there.
    let conversation: MockScreen | undefined;
    let panelShown = false;
    const h = await harness({
      app: { citations: [{ title: "合成来源一" }] },
      contract: copyContract,
      screen: (screen) => {
        if (!screen.nodes.some((node) => node.id === "com.larus.nova:id/bottom_sheet_title")) {
          conversation = screen;
          return screen;
        }
        return panelShown ? screen : conversation!;
      },
    });
    h.hooks.intercept = (method) => {
      if (method === "readClipboard") panelShown = true;
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(unreadable(h)).toMatchObject([{ cause: "source_panel_not_opened" }]);
    expect(h.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      cause: "answer_end_not_visible",
    });
    const [record] = await h.records();
    expect(record).toMatchObject({ status: "failed", screenshot: { file: "failure.png" } });
    expect(record!.citationsStatus).toBeUndefined();
    const directory = join(h.store.root, "batches", BATCH, "records", ANSWER_IDS[0]!);
    expect(await readFile(join(directory, "ui.xml"), "utf8")).toContain(
      "com.larus.nova:id/bottom_sheet_title",
    );
  });

  it("stops pressing back when a blocker shows while a stuck source panel is being left", async () => {
    let backs = 0;
    const h = await harness({
      app: { sourcePanelBacks: 9, citations: [{ title: "合成来源一" }] },
      contract: (contract) => Object.assign(contract.citations, { close: { back: true } }),
      // Three presses of the read and one of the clean-up later, a rate-limit notice is on
      // screen; the panel's title is still in the hierarchy behind it.
      screen: (screen) =>
        backs >= 4
          ? {
              foreground: MOCK_PACKAGE,
              nodes: [
                { className: "android.widget.TextView", text: "合成提示：操作过于频繁" },
                {
                  id: "com.larus.nova:id/bottom_sheet_title",
                  className: "android.widget.TextView",
                  text: "参考资料",
                },
              ],
            }
          : screen,
    });
    h.hooks.intercept = (method) => {
      if (method === "back") backs += 1;
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "rate_limited",
      phase: "post_send",
    });
    // The notice was looked at, not backed out of: no press after it showed.
    expect(backs).toBe(4);
    expect(h.gate.decide(h.binding)).toMatchObject({ reason: "rate_limited_today" });
    expect(h.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      failureCode: "rate_limited",
      cause: "blocker_marker",
    });
  });

  it("does not swipe for the end of the answer on a blocker that replaced a source panel being left", async () => {
    let backs = 0;
    let gesturesOnNotice = 0;
    const h = await harness({
      app: { sourcePanelBacks: 9, citations: [{ title: "合成来源一" }] },
      contract: (contract) => {
        Object.assign(contract.citations, { close: { back: true } });
        // An App whose end of answer is brought on screen by swiping.
        contract.completion.reveal = [{ swipe: "up", count: 1 }];
      },
      // The first press of the clean-up is answered with a verification screen.
      screen: (screen) =>
        backs >= 4
          ? {
              foreground: MOCK_PACKAGE,
              nodes: [{ id: "com.larus.nova:id/mock_verification", text: "合成验证页" }],
            }
          : screen,
    });
    h.hooks.intercept = (method) => {
      if (method === "back") backs += 1;
      else if (backs >= 4 && (method === "swipe" || method === "tap")) gesturesOnNotice += 1;
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "challenge_required",
      phase: "post_send",
    });
    expect(backs).toBe(4);
    expect(gesturesOnNotice).toBe(0);
    expect((await h.records())[0]!.citationsStatus).toBeUndefined();
  });

  it("gives a blocker that shows instead of the source panel its own class", async () => {
    // The tap on the source entry is answered with a rate-limit notice.
    const h = await harness({
      app: { citations: [{ title: "合成来源一" }] },
      screen: (screen) =>
        screen.nodes.some((node) => node.id === "com.larus.nova:id/bottom_sheet_title")
          ? {
              foreground: MOCK_PACKAGE,
              nodes: [{ className: "android.widget.TextView", text: "合成提示：操作过于频繁" }],
            }
          : screen,
    });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "rate_limited",
      phase: "post_send",
    });
    // Not passed over as a source problem: the binding rests for the day.
    expect(unreadable(h)).toEqual([]);
    expect(h.gate.decide(h.binding)).toMatchObject({ reason: "rate_limited_today" });
    const [record] = await h.records();
    expect(record!.citationsStatus).toBeUndefined();
    // Nothing was pressed on that screen.
    expect(h.driverCalls()).not.toContain("back");
  });

  it("records 'not_declared' for a platform whose contract has no source section", async () => {
    const h = await harness({
      // The App shows a source entry; the contract does not know it.
      app: { citations: [{ title: "合成来源一" }] },
      contract: (contract) => void delete (contract as Record<string, unknown>).citations,
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({
      answerText: ANSWER,
      citations: [],
      citationsStatus: "not_declared",
    });
    expect(record).not.toHaveProperty("citationsIssue");
    // The entry was not opened.
    expect(unreadable(h)).toEqual([]);
    expect(h.driverCalls()).not.toContain("back");
  });

  it("records 'absent' when the App itself states that there are no sources", async () => {
    const h = await harness({
      app: { statedSources: 0, citations: [{ title: "合成来源一" }] },
      // The open panel shows its title and its close control, and no source.
      screen: (screen) =>
        screen.nodes.some((node) => node.id === "com.larus.nova:id/bottom_sheet_title")
          ? { ...screen, nodes: screen.nodes.filter((node) => node.id !== undefined) }
          : screen,
      contract: (contract) =>
        Object.assign(contract.citations, {
          count: {
            from: "panel",
            selector: { id: "com.larus.nova:id/bottom_sheet_title" },
            pattern: "参考资料\\s*(\\d+)",
          },
        }),
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({ citations: [], citationsStatus: "absent" });
    expect(record).not.toHaveProperty("citationsIssue");
    expect(unreadable(h)).toEqual([]);
  });
});

// design §16.6: an App whose source entry sits at the start of the answer and opens the tool
// steps of the answer; the search steps among them lead to one source list each.
describe("sources behind the tool steps of an answer", () => {
  const ANSWER = "合成回答第一段\n合成回答第二段";
  const viaContract = (contract: ReturnType<typeof referenceContract>) => {
    contract.citations = {
      entry: { textMatches: "使用 \\d+ 个工具.*", pick: "first" },
      entrySwipe: { swipe: "down", max: 40, stopWhenUnmoved: 2 },
      via: {
        panel: { text: "工具调用" },
        item: { textMatches: "搜索网页.*" },
        queryPrefix: "搜索网页",
        close: { back: true },
      },
      panel: { text: "网页搜索" },
      cards: { layout: [{ name: "title" }, { name: "site", optional: true }], sameHeight: true },
      close: { back: true },
    };
    // The search state has to come from the tool steps.
    delete (contract as Record<string, unknown>).searchState;
    (contract.answer.exclude as unknown[]).push({ id: "com.larus.nova:id/mock_tool_summary" });
  };
  // The same App read through its copy control: the sources are then read first, from the end
  // of the answer.
  const copyViaContract = (contract: ReturnType<typeof referenceContract>) => {
    viaContract(contract);
    contract.answer = { method: "copy", copy: { id: "com.larus.nova:id/msg_action_copy" } };
  };
  const SEARCH = { text: "搜索网页 合成关键词一", sources: [{ title: "合成来源一" }] };
  const OTHER_TOOL = { text: "获取数据 合成数据库" };
  const unreadable = (h: Awaited<ReturnType<typeof harness>>) =>
    h.logs.filter((line) => line.event === "app_citations_unreadable");
  const failureCause = (h: Awaited<ReturnType<typeof harness>>) =>
    h.logs.find((line) => line.event === "app_attempt_failure_cause");
  const backs = (h: Awaited<ReturnType<typeof harness>>) =>
    h.driverCalls().filter((call) => call === "back").length;
  const swipes = (h: Awaited<ReturnType<typeof harness>>, direction: "up" | "down") =>
    h.drivers.opened
      .flatMap((driver) => driver.swipes)
      .filter((swipe) => swipe.direction === direction);
  const finalScreen = (h: Awaited<ReturnType<typeof harness>>) =>
    readFile(join(h.store.root, "batches", BATCH, "records", ANSWER_IDS[0]!, "ui.xml"), "utf8");

  it("reads the list of every search step, joins them without duplicates and leaves both screens", async () => {
    const h = await harness({
      app: {
        toolSteps: [
          {
            text: "搜索网页 合成关键词一",
            sources: [{ title: "合成来源一", site: "合成站点一" }, { title: "合成来源二" }],
          },
          OTHER_TOOL,
          {
            text: "搜索网页 合成关键词二",
            sources: [{ title: "合成来源二" }, { title: "合成来源三", site: "合成站点三" }],
          },
        ],
      },
      contract: viaContract,
    });
    expect(await h.collect()).toEqual({
      answerId: ANSWER_IDS[0],
      status: "valid",
      failureCode: null,
      phase: null,
    });
    const [record] = await h.records();
    expect(record).toMatchObject({ answerText: ANSWER, citationsStatus: "read" });
    // The source both lists show is kept once; a card without a site is a source without one.
    expect(record!.citations).toEqual([
      { title: "合成来源一", site: "合成站点一", url: null },
      { title: "合成来源二", site: null, url: null },
      { title: "合成来源三", site: "合成站点三", url: null },
    ]);
    expect(record).not.toHaveProperty("citationsIssue");
    // A search step is there, so the answer was written with a web search.
    expect(record!.searchState).toEqual({
      webSearch: "on",
      deepThinking: "unknown",
      searchQueries: ["合成关键词一", "合成关键词二"],
    });
    // One press per list, one for the steps; the step that is no search was not opened.
    expect(backs(h)).toBe(3);
    expect(unreadable(h)).toEqual([]);
    const screen = await finalScreen(h);
    expect(screen).toContain("com.larus.nova:id/msg_action_regenerate");
    expect(screen).not.toContain("工具调用");
    expect(screen).not.toContain("网页搜索");
  });

  it("takes each keyword once and opens no more steps than the contract allows", async () => {
    const step = (keyword: string, title: string) => ({
      text: `搜索网页 ${keyword}`,
      sources: [{ title }],
    });
    const h = await harness({
      app: {
        toolSteps: [
          step("合成关键词一", "合成来源一"),
          // The same search again: the step is taken once.
          step("合成关键词一", "合成来源一"),
          step("合成关键词二", "合成来源二"),
          step("合成关键词三", "合成来源三"),
        ],
      },
      contract: (contract) => {
        viaContract(contract);
        Object.assign(contract.citations.via as object, { maxItems: 2 });
      },
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record!.citations.map((source) => source.title)).toEqual(["合成来源一", "合成来源二"]);
    expect(record!.searchState!.searchQueries).toEqual(["合成关键词一", "合成关键词二"]);
    expect(backs(h)).toBe(3);
  });

  it("records 'absent' when the tool steps hold no search step, and leaves their screen", async () => {
    const h = await harness({ app: { toolSteps: [OTHER_TOOL] }, contract: viaContract });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({ answerText: ANSWER, citations: [], citationsStatus: "absent" });
    expect(record).not.toHaveProperty("citationsIssue");
    expect(record!.searchState).toEqual({
      webSearch: "unknown",
      deepThinking: "unknown",
      searchQueries: [],
    });
    expect(unreadable(h)).toEqual([]);
    expect(backs(h)).toBe(1);
    expect(await finalScreen(h)).not.toContain("工具调用");
  });

  it("swipes back to an entry at the start of a long answer, further than one gesture call allows", async () => {
    const long = (max: number) =>
      harness({
        app: {
          toolSteps: [SEARCH],
          answer: Array.from({ length: 40 }, (_, index) => `合成段落${index + 1}`),
          viewport: 5,
        },
        contract: (contract) => {
          copyViaContract(contract);
          Object.assign(contract.citations.entrySwipe as object, { max });
        },
      });
    const found = await long(40);
    expect(await found.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await found.records())[0]).toMatchObject({
      citations: [{ title: "合成来源一", site: null, url: null }],
      citationsStatus: "read",
    });
    const made = swipes(found, "down");
    expect(made.length).toBeGreaterThan(MAX_SWIPES_PER_CALL);
    expect(made.length).toBeLessThan(40);
    // One swipe per call, with a look for the entry after each.
    expect(made.every((swipe) => swipe.count === 1)).toBe(true);
    // The answer was copied from its end, which the App's own control brought back.
    expect((await found.records())[0]!.answerText).toContain("合成段落40");

    // Ten swipes do not reach it, and the answer would be recorded as showing no sources.
    const short = await long(10);
    expect(await short.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await short.records())[0]).toMatchObject({ citations: [], citationsStatus: "absent" });
    expect(swipes(short, "down")).toHaveLength(10);
  });

  it("stops looking for the entry once the list no longer moves", async () => {
    const h = await harness({
      // An answer without tools: there is no entry anywhere.
      app: {
        answer: Array.from({ length: 12 }, (_, index) => `合成段落${index + 1}`),
        viewport: 5,
      },
      contract: copyViaContract,
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await h.records())[0]).toMatchObject({ citations: [], citationsStatus: "absent" });
    // Five swipes to the start of the conversation, two more that changed nothing.
    expect(swipes(h, "down")).toHaveLength(7);
    expect(unreadable(h)).toEqual([]);
  });

  it("makes every allowed swipe where it is not told to stop, as for an answer whose hierarchy never changes", async () => {
    // The whole answer is one text view: scrolling inside it leaves the hierarchy as it was.
    const tall = (entrySwipe: Record<string, unknown>, toolSteps?: MockChatOptions["toolSteps"]) =>
      harness({
        app: { tallAnswerRows: 12, viewport: 4, ...(toolSteps ? { toolSteps } : {}) },
        contract: (contract) => {
          copyViaContract(contract);
          contract.citations.entrySwipe = { swipe: "down", ...entrySwipe };
        },
      });
    const all = await tall({ max: 12 });
    expect(await all.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await all.records())[0]).toMatchObject({
      answerText: ANSWER,
      citationsStatus: "absent",
    });
    expect(swipes(all, "down")).toHaveLength(12);

    // With an entry above such an answer: found by swiping on through the unchanged hierarchy,
    // and missed by a contract that stops at the first swipes that change nothing.
    const found = await tall({ max: 12 }, [SEARCH]);
    expect(await found.collect()).toMatchObject({ status: "valid" });
    expect((await found.records())[0]).toMatchObject({ citationsStatus: "read" });
    const stopped = await tall({ max: 12, stopWhenUnmoved: 2 }, [SEARCH]);
    expect(await stopped.collect()).toMatchObject({ status: "valid" });
    expect((await stopped.records())[0]).toMatchObject({ citationsStatus: "absent" });
    expect(swipes(stopped, "down").length).toBeLessThan(swipes(found, "down").length);
  });

  it("keeps the sample without sources when the tool steps do not open", async () => {
    const h = await harness({
      app: { toolSteps: [SEARCH], sourceEntryDead: true },
      contract: viaContract,
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({
      answerText: ANSWER,
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "source_panel_not_opened",
      screenshot: { file: "final.png" },
    });
    // No search step was seen.
    expect(record!.searchState!.webSearch).toBe("unknown");
    expect(unreadable(h)).toMatchObject([{ cause: "source_panel_not_opened" }]);
    expect(backs(h)).toBe(0);
  });

  it("keeps the sample without sources when a search step does not open its list, and leaves the steps", async () => {
    const h = await harness({
      app: { toolSteps: [SEARCH], toolRowDead: true },
      contract: viaContract,
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({
      answerText: ANSWER,
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "source_panel_not_opened",
    });
    // The step itself was seen; its keyword is dropped with the sources.
    expect(record!.searchState).toMatchObject({ webSearch: "on", searchQueries: [] });
    expect(backs(h)).toBe(1);
    const screen = await finalScreen(h);
    expect(screen).toContain("com.larus.nova:id/msg_action_regenerate");
    expect(screen).not.toContain("工具调用");
  });

  it("leaves a source list that closes late, then the steps, and keeps the sample", async () => {
    const h = await harness({
      app: { toolSteps: [SEARCH], sourcePanelBacks: 4 },
      contract: viaContract,
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await h.records())[0]).toMatchObject({
      answerText: ANSWER,
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "source_panel_not_closed",
    });
    // Three presses of the read; the clean-up needs one for the list and one for the steps.
    expect(backs(h)).toBe(5);
    const screen = await finalScreen(h);
    expect(screen).toContain("com.larus.nova:id/msg_action_regenerate");
    expect(screen).not.toContain("网页搜索");
    expect(screen).not.toContain("工具调用");
  });

  it("saves no sample over a source list that does not close", async () => {
    const h = await harness({
      app: { toolSteps: [SEARCH], sourcePanelBacks: 9 },
      contract: viaContract,
    });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(unreadable(h)).toMatchObject([{ cause: "source_panel_not_closed" }]);
    expect(failureCause(h)).toMatchObject({ cause: "answer_end_not_visible" });
    // Bounded: three presses of the read, three of the clean-up, none for the steps under it.
    expect(backs(h)).toBe(6);
    const [record] = await h.records();
    expect(record).toMatchObject({ status: "failed", screenshot: { file: "failure.png" } });
    expect(record!.citationsStatus).toBeUndefined();
    expect(await finalScreen(h)).toContain("网页搜索");
  });

  it("does not take a list that closed onto the conversation for one that led back to the steps", async () => {
    const h = await harness({
      app: { toolSteps: [SEARCH], toolListExitsToChat: true },
      contract: viaContract,
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await h.records())[0]).toMatchObject({
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "source_panel_not_closed",
    });
    // Nothing was left to close: a second press would have left the App.
    expect(backs(h)).toBe(1);
    expect(await finalScreen(h)).toContain("com.larus.nova:id/msg_action_regenerate");
  });

  it("leaves tool steps that close late and keeps the sample; saves none when they stay", async () => {
    const closing = (presses: number, contract = viaContract) =>
      harness({ app: { toolSteps: [SEARCH], toolScreenBacks: presses }, contract });
    const late = await closing(4);
    expect(await late.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await late.records())[0]).toMatchObject({
      answerText: ANSWER,
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "source_panel_not_closed",
    });
    // One press for the list, three of the read for the steps, one of the clean-up.
    expect(backs(late)).toBe(5);
    expect(await finalScreen(late)).not.toContain("工具调用");

    const stuck = await closing(9);
    expect(await stuck.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(unreadable(stuck)).toMatchObject([{ cause: "source_panel_not_closed" }]);
    expect(failureCause(stuck)).toMatchObject({ cause: "answer_end_not_visible" });
    expect(backs(stuck)).toBe(7);
    expect((await stuck.records())[0]!.citationsStatus).toBeUndefined();
    expect(await finalScreen(stuck)).toContain("工具调用");

    // With the copy method nothing is copied through the steps that stayed.
    const uncopied = await closing(9, copyViaContract);
    expect(await uncopied.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
    });
    expect(failureCause(uncopied)).toMatchObject({ cause: "answer_end_not_visible" });
    expect(uncopied.driverCalls()).not.toContain("readClipboard");
    expect((await uncopied.records())[0]).toMatchObject({ answerText: null, citations: [] });
  });

  // As with a source panel: tool steps shown as a sheet leave the conversation's views in the
  // hierarchy, and "the end of the answer is visible" is true with the sheet still over it.
  it("saves no sample whose tool steps stay open over a conversation that is still in the hierarchy", async () => {
    const over = (contract: (contract: ReturnType<typeof referenceContract>) => void) =>
      harness({
        app: { toolSteps: [SEARCH], toolScreenBacks: 9, sourcePanelOverConversation: true },
        contract,
      });
    const h = await over(viaContract);
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(unreadable(h)).toMatchObject([{ cause: "source_panel_not_closed" }]);
    expect(failureCause(h)).toMatchObject({ cause: "answer_end_not_visible" });
    const [record] = await h.records();
    expect(record).toMatchObject({ status: "failed", screenshot: { file: "failure.png" } });
    expect(record!.citationsStatus).toBeUndefined();
    const screen = await finalScreen(h);
    expect(screen).toContain("工具调用");
    expect(screen).toContain("com.larus.nova:id/msg_action_regenerate");

    // The copy control under the sheet is not tapped either.
    const uncopied = await over(copyViaContract);
    expect(await uncopied.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
    });
    expect(failureCause(uncopied)).toMatchObject({ cause: "answer_end_not_visible" });
    expect(uncopied.driverCalls()).not.toContain("readClipboard");
  });

  it("saves no sample when the steps without a search step do not close", async () => {
    const h = await harness({
      app: { toolSteps: [OTHER_TOOL], toolScreenBacks: 9 },
      contract: viaContract,
    });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
    });
    expect(unreadable(h)).toMatchObject([{ cause: "source_panel_not_closed" }]);
    expect(failureCause(h)).toMatchObject({ cause: "answer_end_not_visible" });
  });

  it("stops pressing back when a blocker shows while the list and the steps are being left", async () => {
    let pressed = 0;
    const h = await harness({
      app: { toolSteps: [SEARCH], sourcePanelBacks: 4 },
      contract: viaContract,
      // Three presses of the read and one of the clean-up later the list is gone and a
      // rate-limit notice is on screen; the title of the steps is in the hierarchy behind it.
      screen: (screen) =>
        pressed >= 4
          ? {
              foreground: MOCK_PACKAGE,
              nodes: [
                { className: "android.widget.TextView", text: "合成提示：操作过于频繁" },
                { className: "android.widget.TextView", text: "工具调用" },
              ],
            }
          : screen,
    });
    h.hooks.intercept = (method) => {
      if (method === "back") pressed += 1;
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "rate_limited",
      phase: "post_send",
    });
    // The notice was looked at, not backed out of.
    expect(pressed).toBe(4);
    expect(h.gate.decide(h.binding)).toMatchObject({ reason: "rate_limited_today" });
    expect(failureCause(h)).toMatchObject({ failureCode: "rate_limited", cause: "blocker_marker" });
    expect((await h.records())[0]!.citationsStatus).toBeUndefined();
  });

  it("does not press back on a blocker that took the place of a list that was just read", async () => {
    // The list is read to its end (two swipes that change nothing, one export after each);
    // from the next look on, a verification screen is there instead.
    let ups = 0;
    let exported = false;
    let replaced = false;
    const h = await harness({
      app: { toolSteps: [SEARCH] },
      contract: viaContract,
      screen: (screen) =>
        replaced
          ? {
              foreground: MOCK_PACKAGE,
              nodes: [{ id: "com.larus.nova:id/mock_verification", text: "合成验证页" }],
            }
          : screen,
    });
    h.hooks.intercept = (method) => {
      if (method === "swipe") ups += 1;
      else if (method === "hierarchy") exported = ups >= 2;
      else if (exported) replaced = true;
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "challenge_required",
      phase: "post_send",
    });
    expect(backs(h)).toBe(0);
    // Not passed over as a source problem.
    expect(unreadable(h)).toEqual([]);
    expect((await h.records())[0]!.citationsStatus).toBeUndefined();
  });

  // One back press per screen is the way out. A press that is taken twice (or one made while a
  // screen is still on its way out) lands on the conversation and leaves it; nothing a source
  // read looks for is on screen then, and the read itself has ended well.
  it("saves no sample when the way back from the tool steps also left the conversation", async () => {
    const left = async (contract: (contract: ReturnType<typeof referenceContract>) => void) => {
      const h = await harness({ app: { toolSteps: [SEARCH] }, contract });
      let pressed = 0;
      h.hooks.intercept = (method) => {
        // The press that leaves the steps: the list took the first one.
        if (method === "back" && (pressed += 1) === 2) h.app.back();
      };
      return h;
    };
    // The answer is read from the hierarchy before its sources: it is complete by then.
    const h = await left(viaContract);
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_crashed",
      phase: "post_send",
    });
    expect(backs(h)).toBe(2);
    // The sources themselves were read; it is the conversation that is gone.
    expect(unreadable(h)).toEqual([]);
    expect(failureCause(h)).toMatchObject({
      failureCode: "app_crashed",
      cause: "foreground_other",
    });
    const [record] = await h.records();
    expect(record).toMatchObject({ status: "failed", screenshot: { file: "failure.png" } });
    expect(record!.citationsStatus).toBeUndefined();
    expect(await finalScreen(h)).not.toContain("com.larus.nova:id/msg_action_regenerate");

    // Steps without a search step are left by the same key: "no sources" is no licence either.
    const none = await harness({ app: { toolSteps: [OTHER_TOOL] }, contract: viaContract });
    none.hooks.intercept = (method) => {
      if (method === "back") none.app.back();
    };
    expect(await none.collect()).toMatchObject({ status: "failed", failureCode: "app_crashed" });
    expect((await none.records())[0]!.citationsStatus).toBeUndefined();

    // With the copy method nothing is copied off the screen the press led to.
    const uncopied = await left(copyViaContract);
    expect(await uncopied.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_crashed",
    });
    expect(uncopied.driverCalls()).not.toContain("readClipboard");
    expect((await uncopied.records())[0]).toMatchObject({ status: "failed", answerText: null });
  });

  it("takes the rows of the tool steps only once their screen has stopped changing", async () => {
    // The screen shows its title first; its rows are there two looks later.
    let looks = 0;
    const h = await harness({
      app: {
        toolSteps: [
          { text: "搜索网页 合成关键词一", sources: [{ title: "合成来源一" }] },
          { text: "搜索网页 合成关键词二", sources: [{ title: "合成来源二" }] },
        ],
      },
      contract: viaContract,
      screen: (screen) => {
        if (!screen.nodes.some((node) => node.text === "工具调用")) return screen;
        looks += 1;
        // First the title alone, then the title with the first row, then everything.
        const shown = looks <= 2 ? 1 : looks === 3 ? 2 : screen.nodes.length;
        return { ...screen, nodes: screen.nodes.slice(0, shown) };
      },
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    // Neither "no sources" from the first look nor the first row alone from the second.
    expect(record).toMatchObject({ citationsStatus: "read" });
    expect(record!.citations.map((source) => source.title)).toEqual(["合成来源一", "合成来源二"]);
    expect(record!.searchState).toMatchObject({
      webSearch: "on",
      searchQueries: ["合成关键词一", "合成关键词二"],
    });
    expect(backs(h)).toBe(3);
  });

  it("does not tap the next search step on a blocker that shows over the steps", async () => {
    // Back from the first list, a rate-limit notice is over the steps; their title and rows
    // are still in the hierarchy under it.
    let pressed = 0;
    let tapsOnNotice = 0;
    const h = await harness({
      app: {
        toolSteps: [
          { text: "搜索网页 合成关键词一", sources: [{ title: "合成来源一" }] },
          { text: "搜索网页 合成关键词二", sources: [{ title: "合成来源二" }] },
        ],
      },
      contract: viaContract,
      screen: (screen) =>
        pressed >= 1 && screen.nodes.some((node) => node.text === "工具调用")
          ? {
              ...screen,
              nodes: [
                { className: "android.widget.TextView", text: "合成提示：操作过于频繁" },
                ...screen.nodes,
              ],
            }
          : screen,
    });
    h.hooks.intercept = (method) => {
      if (method === "back") pressed += 1;
      else if (method === "tap" && pressed >= 1) tapsOnNotice += 1;
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "rate_limited",
      phase: "post_send",
    });
    // The notice was looked at: no row was tapped under it and it was not backed out of.
    expect(tapsOnNotice).toBe(0);
    expect(pressed).toBe(1);
    expect(unreadable(h)).toEqual([]);
    expect(h.gate.decide(h.binding)).toMatchObject({ reason: "rate_limited_today" });
    expect((await h.records())[0]!.citationsStatus).toBeUndefined();
  });

  it("opens a search step by comparing its text, never by a lookup built from it", async () => {
    // A keyword as a model may search for it: quotes and a backslash, which the selector
    // syntax of the device would have to carry unharmed.
    const keyword = '合成"关键词"\\一';
    const row = `搜索网页 ${keyword}`;
    const h = await harness({
      app: { toolSteps: [{ text: row, sources: [{ title: "合成来源一" }] }] },
      contract: viaContract,
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({
      citations: [{ title: "合成来源一", site: null, url: null }],
      citationsStatus: "read",
    });
    expect(record!.searchState!.searchQueries).toEqual([keyword]);
    // Every lookup carries conditions of the contract only.
    const lookups = h.drivers.opened.flatMap((driver) => driver.lookups);
    expect(lookups.length).toBeGreaterThan(0);
    expect(JSON.stringify(lookups)).not.toContain("关键词");
    expect(lookups).toContainEqual({ textMatches: "搜索网页.*" });
  });

  it("leaves the list and the steps through their own close controls where the contract names them", async () => {
    // Both screens carry a close control here; the back key is not needed.
    const CLOSE = "合成关闭";
    const h = await harness({
      app: {
        toolSteps: [
          { text: "搜索网页 合成关键词一", sources: [{ title: "合成来源一" }] },
          { text: "搜索网页 合成关键词二", sources: [{ title: "合成来源二" }] },
        ],
      },
      contract: (contract) => {
        viaContract(contract);
        Object.assign(contract.citations, { close: { desc: CLOSE } });
        Object.assign(contract.citations.via as object, { close: { desc: CLOSE } });
      },
      screen: (screen, app) =>
        screen.nodes.some((node) => node.text === "工具调用" || node.text === "网页搜索")
          ? { ...screen, nodes: [{ desc: CLOSE, onTap: () => app.back() }, ...screen.nodes] }
          : screen,
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({ citationsStatus: "read" });
    expect(record!.citations.map((source) => source.title)).toEqual(["合成来源一", "合成来源二"]);
    expect(backs(h)).toBe(0);
    const screen = await finalScreen(h);
    expect(screen).toContain("com.larus.nova:id/msg_action_regenerate");
    expect(screen).not.toContain(CLOSE);
  });

  it("reads every card of a short list whose first and last card carry the list's padding", async () => {
    // The whole list is on screen. Its two ends are taller than the cards between them and, in
    // a list this short, as many or more: they are not the measure for a whole card.
    const padded = (title: string) => ({ title, cardHeight: 190 });
    for (const sources of [
      [padded("合成来源一"), { title: "合成来源二", site: "合成站点二" }, padded("合成来源三")],
      [
        padded("合成来源一"),
        { title: "合成来源二" },
        { title: "合成来源三", site: "合成站点三" },
        padded("合成来源四"),
      ],
    ]) {
      const h = await harness({
        app: { toolSteps: [{ text: "搜索网页 合成关键词一", sources }] },
        contract: viaContract,
      });
      expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
      const [record] = await h.records();
      expect(record).toMatchObject({ citationsStatus: "read" });
      expect(record!.citations.map((source) => source.title)).toEqual(
        sources.map((source) => source.title),
      );
    }
  });

  it("does not read the half card at the edge of a list of equally tall cards", async () => {
    const citations = [
      { title: "合成来源一", site: "合成站点一" },
      // A whole card that shows no site.
      { title: "合成来源二" },
      { title: "合成来源三", site: "合成站点三" },
      { title: "合成来源四", site: "合成站点四" },
      { title: "合成来源五", site: "合成站点五" },
      { title: "合成来源六", site: "合成站点六" },
    ];
    const list = (sameHeight: boolean) =>
      harness({
        // A swipe moves the list by a screen; above the list's top edge the card that just left
        // still shows its title, as a lower card.
        app: {
          equalCards: true,
          cutTopCard: true,
          sourcesPageSwipe: true,
          sourcesViewport: 3,
          citations,
        },
        contract: (contract) => {
          const sources = contract.citations;
          delete sources.itemTitle;
          delete sources.searchQueries;
          Object.assign(sources, {
            panel: { text: "参考资料" },
            close: { back: true },
            cards: {
              layout: [{ name: "title" }, { name: "site", optional: true }],
              ...(sameHeight ? { sameHeight: true } : {}),
            },
          });
        },
      });
    const whole = await list(true);
    expect(await whole.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await whole.records())[0]!.citations).toEqual(
      citations.map((source) => ({ title: source.title, site: source.site ?? null, url: null })),
    );

    // Without it the half card fits the template as a source of its own, without a site, and
    // the third source is in the list twice.
    const misread = await list(false);
    expect(await misread.collect()).toMatchObject({ status: "valid" });
    const titles = (await misread.records())[0]!.citations.map((source) => source.title);
    expect(titles.filter((title) => title === "合成来源三")).toHaveLength(2);
    expect((await misread.records())[0]!.citations).toContainEqual({
      title: "合成来源三",
      site: null,
      url: null,
    });
  });
});

// design §16.7: an App whose answer shows two source lists behind two entries. The sources the
// answer cites sit behind an entry at its end that opens a panel stating their number; the
// pages its search steps returned sit behind the tool steps at its start. The contract
// declares one path for each and both are read, in the order written.
describe("several source lists of one answer", () => {
  const ANSWER = "合成回答第一段\n合成回答第二段";
  const SEARCH_ENTRY = "使用 \\d+ 个工具.*";
  type Contract = ReturnType<typeof referenceContract>;
  const twoPaths = (contract: Contract) => {
    const cited = {
      label: "cited",
      ...contract.citations,
      count: {
        from: "panel",
        selector: { id: "com.larus.nova:id/bottom_sheet_title" },
        pattern: "参考资料\\s*(\\d+)",
      },
    };
    const search = {
      label: "search",
      entry: { textMatches: SEARCH_ENTRY, pick: "first" },
      entrySwipe: { swipe: "down", max: 40, stopWhenUnmoved: 2 },
      via: {
        panel: { text: "工具调用" },
        item: { textMatches: "搜索网页.*" },
        queryPrefix: "搜索网页",
        close: { back: true },
      },
      panel: { text: "网页搜索" },
      cards: { layout: [{ name: "title" }, { name: "site", optional: true }], sameHeight: true },
      close: { back: true },
    };
    (contract as { citations: unknown }).citations = [cited, search];
    // The search state has to come from the paths.
    delete (contract as Record<string, unknown>).searchState;
    (contract.answer.exclude as unknown[]).push({ id: "com.larus.nova:id/mock_tool_summary" });
  };
  // The path of the given label, to change it.
  const path = (contract: Contract, label: "cited" | "search") =>
    (contract.citations as unknown as Record<string, unknown>[]).find(
      (item) => item.label === label,
    )!;
  const copyTwoPaths = (contract: Contract) => {
    twoPaths(contract);
    contract.answer = { method: "copy", copy: { id: "com.larus.nova:id/msg_action_copy" } };
  };
  // The cited panel is left by the back key, as a panel without a close control is.
  const citedLeftByBack = (contract: Contract) => {
    twoPaths(contract);
    path(contract, "cited").close = { back: true };
  };
  const OTHER_TOOL = { text: "获取数据 合成数据库" };
  // Two cited sources behind the entry at the end of the answer; three pages behind two search
  // steps at its start. "合成来源二" is in both lists, "合成来源三" in both search steps.
  const APP: Omit<MockChatOptions, "now"> = {
    sourceEntryAtEnd: true,
    statedSources: 2,
    citations: [{ title: "合成来源一" }, { title: "合成来源二" }],
    queries: "合成关键词一",
    toolSteps: [
      {
        text: "搜索网页 合成关键词一",
        sources: [{ title: "合成来源二" }, { title: "合成来源三", site: "合成站点三" }],
      },
      OTHER_TOOL,
      {
        text: "搜索网页 合成关键词二",
        sources: [{ title: "合成来源三", site: "合成站点三" }, { title: "合成来源四" }],
      },
    ],
  };
  const cited = (title: string) => ({ title, site: null, url: null, list: "cited" });
  const CITED = [cited("合成来源一"), cited("合成来源二")];
  const SEARCHED = [
    { title: "合成来源二", site: null, url: null, list: "search" },
    { title: "合成来源三", site: "合成站点三", url: null, list: "search" },
    { title: "合成来源四", site: null, url: null, list: "search" },
  ];
  type Harness = Awaited<ReturnType<typeof harness>>;
  const unreadable = (h: Harness) =>
    h.logs.filter((line) => line.event === "app_citations_unreadable");
  const failureCause = (h: Harness) =>
    h.logs.find((line) => line.event === "app_attempt_failure_cause");
  const backs = (h: Harness) => h.driverCalls().filter((call) => call === "back").length;
  const finalScreen = (h: Harness) =>
    readFile(join(h.store.root, "batches", BATCH, "records", ANSWER_IDS[0]!, "ui.xml"), "utf8");
  // Whether the entry of the search path was ever looked for.
  const lookedForSearchEntry = (h: Harness) =>
    h.drivers.opened
      .flatMap((driver) => driver.lookups)
      .some((selector) => selector.textMatches === SEARCH_ENTRY);
  const PANEL = "com.larus.nova:id/bottom_sheet_title";
  const showsPanel = (screen: MockScreen) => screen.nodes.some((node) => node.id === PANEL);

  it("reads both lists in the order written and names the list of every source", async () => {
    const h = await harness({ app: APP, contract: twoPaths });
    expect(await h.collect()).toEqual({
      answerId: ANSWER_IDS[0],
      status: "valid",
      failureCode: null,
      phase: null,
    });
    const [record] = await h.records();
    expect(record).toMatchObject({ answerText: ANSWER, citationsStatus: "read" });
    expect(record).not.toHaveProperty("citationsIssue");
    // The source both lists show is kept once for each; the one two search steps show is kept
    // once within its list.
    expect(record!.citations).toEqual([...CITED, ...SEARCHED]);
    expect(record!.citationLists).toEqual([
      { label: "cited", status: "read", count: 2 },
      { label: "search", status: "read", count: 3 },
    ]);
    // The keyword both paths give is kept once; a search step is there, so web search is on.
    expect(record!.searchState).toEqual({
      webSearch: "on",
      deepThinking: "unknown",
      searchQueries: ["合成关键词一", "合成关键词二"],
    });
    expect(unreadable(h)).toEqual([]);
    expect(h.logs.find((line) => line.event === "app_attempt_finished")).toMatchObject({
      citations: 5,
      citationsStatus: "read",
    });
    // A later run of the batch takes the cell for collected.
    expect(pendingCells([QUESTION], ["doubao"], await h.records())).toEqual([]);
    // The cited panel was left through its own control; one press per search list and one for
    // the steps.
    expect(backs(h)).toBe(3);
    const screen = await finalScreen(h);
    expect(screen).toContain("com.larus.nova:id/msg_action_regenerate");
    for (const left of [PANEL, "工具调用", "网页搜索"]) expect(screen).not.toContain(left);
  });

  it("reads both lists before the copy, from the end of a long answer and from its start", async () => {
    const h = await harness({
      app: {
        ...APP,
        answer: Array.from({ length: 12 }, (_, index) => `合成段落${index + 1}`),
        viewport: 8,
        // With the copy before either list, its clipboard preview would hide that list's entry
        // and the list would be recorded as "absent".
        copyOverlayMs: 6_000,
      },
      contract: copyTwoPaths,
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record!.citations).toEqual([...CITED, ...SEARCHED]);
    expect(record).toMatchObject({
      citationsStatus: "read",
      citationLists: [
        { label: "cited", status: "read", count: 2 },
        { label: "search", status: "read", count: 3 },
      ],
    });
    // The cited entry was on screen when the answer completed; the search entry was swiped to.
    const down = h.drivers.opened
      .flatMap((driver) => driver.swipes)
      .filter((swipe) => swipe.direction === "down");
    expect(down.length).toBeGreaterThan(0);
    // The answer was copied from its end, brought back after the second list.
    expect(record!.answerText).toBe(
      Array.from({ length: 12 }, (_, index) => `合成段落${index + 1}`).join("\n"),
    );
    expect(await finalScreen(h)).toContain("com.larus.nova:id/msg_action_regenerate");
  });

  it.each([
    ["its panel does not open", { panelEntryDead: true }, "source_panel_not_opened", {}],
    [
      "its length is not the stated number",
      { statedSources: 3 },
      "citation_count",
      { numbers: { stated: 3, read: 2 } },
    ],
  ] as const)(
    "leaves a first list out when %s and still reads the second",
    async (_, app, issue, detail) => {
      const h = await harness({
        // The cited panel is a sheet over the conversation: while it is open, the entry of the
        // search path is in the hierarchy under it and a tap on it opens nothing. The search
        // list can only be read once the first path has left its panel.
        app: { ...APP, queries: "合成关键词零", sourcePanelOverConversation: true, ...app },
        contract: twoPaths,
      });
      expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
      const [record] = await h.records();
      expect(record).toMatchObject({
        status: "valid",
        answerText: ANSWER,
        citationsStatus: "read",
      });
      expect(record).not.toHaveProperty("citationsIssue");
      expect(record!.citations).toEqual(SEARCHED);
      expect(record!.citationLists).toEqual([
        { label: "cited", status: "unreadable", issue, count: 0 },
        { label: "search", status: "read", count: 3 },
      ]);
      // The keyword of the list that was left out went with it.
      expect(record!.searchState!.searchQueries).toEqual(["合成关键词一", "合成关键词二"]);
      expect(unreadable(h)).toEqual([
        {
          event: "app_citations_unreadable",
          batchId: BATCH,
          device: DEVICE_A,
          platform: "doubao",
          answerId: ANSWER_IDS[0],
          list: "cited",
          cause: issue,
          ...detail,
        },
      ]);
      const screen = await finalScreen(h);
      expect(screen).toContain("com.larus.nova:id/msg_action_regenerate");
      for (const left of [PANEL, "工具调用", "网页搜索"]) expect(screen).not.toContain(left);
    },
  );

  it("keeps the first list when the second cannot be read, and leaves the second's screens", async () => {
    const h = await harness({ app: { ...APP, toolRowDead: true }, contract: twoPaths });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({ answerText: ANSWER, citationsStatus: "read" });
    expect(record).not.toHaveProperty("citationsIssue");
    expect(record!.citations).toEqual(CITED);
    expect(record!.citationLists).toEqual([
      { label: "cited", status: "read", count: 2 },
      { label: "search", status: "unreadable", issue: "source_panel_not_opened", count: 0 },
    ]);
    // A search step was seen, whatever became of its list; its keywords went with the list.
    expect(record!.searchState).toMatchObject({ webSearch: "on", searchQueries: ["合成关键词一"] });
    expect(unreadable(h)).toMatchObject([{ list: "search", cause: "source_panel_not_opened" }]);
    // The steps were left by the clean-up.
    expect(backs(h)).toBe(1);
    expect(await finalScreen(h)).not.toContain("工具调用");
  });

  it("records 'unreadable' with the cause of the first list when neither can be read", async () => {
    const h = await harness({
      app: { ...APP, statedSources: 3, toolRowDead: true },
      contract: twoPaths,
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({
      status: "valid",
      answerText: ANSWER,
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "citation_count",
      citationLists: [
        { label: "cited", status: "unreadable", issue: "citation_count", count: 0 },
        { label: "search", status: "unreadable", issue: "source_panel_not_opened", count: 0 },
      ],
    });
    expect(record!.searchState!.searchQueries).toEqual([]);
    expect(unreadable(h)).toMatchObject([
      { list: "cited", cause: "citation_count" },
      { list: "search", cause: "source_panel_not_opened" },
    ]);
    expect(await finalScreen(h)).toContain("com.larus.nova:id/msg_action_regenerate");
  });

  it("records 'absent' when the answer shows neither list", async () => {
    // No cited sources, and tool steps without a search step.
    const h = await harness({ app: { toolSteps: [OTHER_TOOL] }, contract: twoPaths });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({
      answerText: ANSWER,
      citations: [],
      citationsStatus: "absent",
      citationLists: [
        { label: "cited", status: "absent", count: 0 },
        { label: "search", status: "absent", count: 0 },
      ],
    });
    expect(record).not.toHaveProperty("citationsIssue");
    expect(record!.searchState).toEqual({
      webSearch: "unknown",
      deepThinking: "unknown",
      searchQueries: [],
    });
    expect(unreadable(h)).toEqual([]);
    expect(await finalScreen(h)).not.toContain("工具调用");
  });

  it("records 'read' when the answer shows only one of the lists", async () => {
    const h = await harness({ app: { toolSteps: APP.toolSteps }, contract: twoPaths });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    expect(record).toMatchObject({ answerText: ANSWER, citationsStatus: "read" });
    expect(record!.citations).toEqual(SEARCHED);
    expect(record!.citationLists).toEqual([
      { label: "cited", status: "absent", count: 0 },
      { label: "search", status: "read", count: 3 },
    ]);
    expect(unreadable(h)).toEqual([]);
  });

  it("stops at a blocker that shows while the first list's panel is being left", async () => {
    let pressed = 0;
    let gesturesOnNotice = 0;
    const h = await harness({
      app: { ...APP, sourcePanelBacks: 9 },
      contract: citedLeftByBack,
      // Three presses of the read and one of the clean-up later, a rate-limit notice is on
      // screen; the panel's title is still in the hierarchy behind it.
      screen: (screen) =>
        pressed >= 4
          ? {
              foreground: MOCK_PACKAGE,
              nodes: [
                { className: "android.widget.TextView", text: "合成提示：操作过于频繁" },
                { id: PANEL, className: "android.widget.TextView", text: "参考资料 2" },
              ],
            }
          : screen,
    });
    h.hooks.intercept = (method) => {
      if (method === "back") pressed += 1;
      else if (pressed >= 4 && (method === "tap" || method === "swipe")) gesturesOnNotice += 1;
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "rate_limited",
      phase: "post_send",
    });
    expect(failureCause(h)).toMatchObject({ failureCode: "rate_limited", cause: "blocker_marker" });
    // The notice was looked at only, and the second list was not started under it.
    expect(pressed).toBe(4);
    expect(gesturesOnNotice).toBe(0);
    expect(lookedForSearchEntry(h)).toBe(false);
    expect(unreadable(h)).toMatchObject([{ list: "cited", cause: "source_panel_not_closed" }]);
    expect(h.gate.decide(h.binding)).toMatchObject({ reason: "rate_limited_today" });
    const [record] = await h.records();
    expect(record).toMatchObject({ status: "failed", citations: [] });
    expect(record).not.toHaveProperty("citationsStatus");
    expect(record).not.toHaveProperty("citationLists");
  });

  it("does not look for the second entry on a blocker that shows after the first list was read", async () => {
    let panelSeen = false;
    let noticeShown = false;
    let gesturesOnNotice = 0;
    const h = await harness({
      app: APP,
      contract: twoPaths,
      // Once the cited panel has been open and is closed again, a verification screen shows.
      screen: (screen) => {
        if (showsPanel(screen)) panelSeen = true;
        else if (panelSeen) noticeShown = true;
        return noticeShown
          ? {
              foreground: MOCK_PACKAGE,
              nodes: [{ id: "com.larus.nova:id/mock_verification", text: "合成验证页" }],
            }
          : screen;
      },
    });
    h.hooks.intercept = (method) => {
      if (noticeShown && (method === "tap" || method === "swipe" || method === "back"))
        gesturesOnNotice += 1;
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "challenge_required",
      phase: "post_send",
    });
    expect(gesturesOnNotice).toBe(0);
    expect(lookedForSearchEntry(h)).toBe(false);
    expect(unreadable(h)).toEqual([]);
    expect((await h.records())[0]).not.toHaveProperty("citationLists");
  });

  it("saves no sample over a panel of the first list that stays open, and starts no second list", async () => {
    const h = await harness({
      app: { ...APP, sourcePanelBacks: 9 },
      contract: citedLeftByBack,
    });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(unreadable(h)).toMatchObject([{ list: "cited", cause: "source_panel_not_closed" }]);
    expect(failureCause(h)).toMatchObject({ cause: "answer_end_not_visible" });
    expect(lookedForSearchEntry(h)).toBe(false);
    const [record] = await h.records();
    expect(record).toMatchObject({
      status: "failed",
      citations: [],
      screenshot: { file: "failure.png" },
    });
    expect(record).not.toHaveProperty("citationsStatus");
    expect(record).not.toHaveProperty("citationLists");
    expect(await finalScreen(h)).toContain(PANEL);
  });

  it("saves no sample over tool steps of the second list that stay open, though the first was read", async () => {
    const h = await harness({ app: { ...APP, toolScreenBacks: 9 }, contract: twoPaths });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(unreadable(h)).toMatchObject([{ list: "search", cause: "source_panel_not_closed" }]);
    expect(failureCause(h)).toMatchObject({ cause: "answer_end_not_visible" });
    const [record] = await h.records();
    // The list that was read is not kept on a failure either.
    expect(record).toMatchObject({
      status: "failed",
      citations: [],
      screenshot: { file: "failure.png" },
    });
    expect(record).not.toHaveProperty("citationsStatus");
    expect(record).not.toHaveProperty("citationLists");
    expect(await finalScreen(h)).toContain("工具调用");
  });

  it("saves no sample when the panel of a list that was given up shows after the other list was read", async () => {
    // The tap on the cited entry takes effect late: its panel is over the conversation once
    // the search list has been read and both of its screens were left.
    let pressed = 0;
    const h = await harness({
      app: { ...APP, panelEntryDead: true, toolSteps: [APP.toolSteps![0]!] },
      contract: twoPaths,
      screen: (screen) =>
        pressed >= 2
          ? {
              ...screen,
              nodes: [
                ...screen.nodes,
                { id: PANEL, className: "android.widget.TextView", text: "参考资料 2" },
              ],
            }
          : screen,
    });
    h.hooks.intercept = (method) => {
      if (method === "back") pressed += 1;
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    // The sources as a whole were read: it is the list that failed which must not leave its
    // panel on a sample.
    expect(unreadable(h)).toMatchObject([{ list: "cited", cause: "source_panel_not_opened" }]);
    expect(failureCause(h)).toMatchObject({ cause: "answer_end_not_visible" });
    const [record] = await h.records();
    expect(record).toMatchObject({ status: "failed", screenshot: { file: "failure.png" } });
    // A failure carries neither the source status nor the list states; the sources it had read
    // by then still name their list.
    expect(record).not.toHaveProperty("citationsStatus");
    expect(record).not.toHaveProperty("citationLists");
    expect(record!.citations).toEqual(SEARCHED.slice(0, 2));
  });

  // The paths in the other order: the search path, read from the start of the answer, comes
  // first, and the cited path's entry is at the end of the answer.
  const searchFirst = (contract: Contract) => {
    copyTwoPaths(contract);
    (contract.citations as unknown as unknown[]).reverse();
  };

  it("finds the second entry at the end of a long answer after a first list read from its start", async () => {
    const answer = Array.from({ length: 12 }, (_, index) => `合成段落${index + 1}`);
    const h = await harness({ app: { ...APP, answer, viewport: 8 }, contract: searchFirst });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    const [record] = await h.records();
    // The order written is the order of the record.
    expect(record!.citations).toEqual([...SEARCHED, ...CITED]);
    expect(record).toMatchObject({
      answerText: answer.join("\n"),
      citationsStatus: "read",
      citationLists: [
        { label: "search", status: "read", count: 3 },
        { label: "cited", status: "read", count: 2 },
      ],
    });
    expect(record!.searchState!.searchQueries).toEqual(["合成关键词一", "合成关键词二"]);
    expect(unreadable(h)).toEqual([]);
    expect(await finalScreen(h)).toContain("com.larus.nova:id/msg_action_regenerate");
  });

  // One back press per screen is the way out of the steps. A press too many leaves the
  // conversation, and on a real App that does not lead to the launcher but to another screen of
  // the same App, which no look for a crash or blocker tells from the conversation.
  it.each([
    ["copy", searchFirst],
    [
      "hierarchy",
      (contract: Contract) => {
        twoPaths(contract);
        (contract.citations as unknown as unknown[]).reverse();
      },
    ],
  ] as const)(
    "starts no second list on the screen a back press too many led to (%s)",
    async (method, contract) => {
      let pressed = 0;
      let left = false;
      let gesturesElsewhere = 0;
      const h = await harness({
        app: APP,
        contract,
        // The App's list of conversations; one of its views is found by the cited path's entry.
        screen: (screen) =>
          left
            ? {
                foreground: MOCK_PACKAGE,
                nodes: [
                  { className: "android.widget.TextView", text: "合成历史对话" },
                  { id: "com.larus.nova:id/ll_reference_title", onTap: () => undefined },
                ],
              }
            : screen,
      });
      h.hooks.intercept = (call) => {
        // One press per search list; the third leaves the steps, and the conversation with them.
        if (call === "back" && (pressed += 1) === 3) left = true;
        else if (left && (call === "tap" || call === "swipe" || call === "back"))
          gesturesElsewhere += 1;
      };
      expect(await h.collect()).toMatchObject({
        status: "failed",
        failureCode: "app_contract_changed",
        phase: "post_send",
      });
      expect(failureCause(h)).toMatchObject({ cause: "answer_end_not_visible" });
      // The entry that screen shows was not tapped (the contract's way back to the end of the
      // answer found nothing to act on there), and the cited path was not begun: it is neither
      // read nor "unreadable".
      expect(gesturesElsewhere).toBe(0);
      expect(unreadable(h)).toEqual([]);
      expect(h.driverCalls()).not.toContain("readClipboard");
      const [record] = await h.records();
      expect(record).toMatchObject({
        status: "failed",
        citations: [],
        screenshot: { file: "failure.png" },
        ...(method === "copy" ? { answerText: null } : {}),
      });
      expect(record).not.toHaveProperty("citationsStatus");
      expect(record).not.toHaveProperty("citationLists");
    },
  );

  it("does not start a second list while the conversation is not back after a first that failed", async () => {
    let pressed = 0;
    let left = false;
    let gesturesElsewhere = 0;
    const h = await harness({
      // The cited panel is still there after the three presses of its read, which gives up;
      // the press of the clean-up closes it and is taken a second time by the conversation.
      app: { ...APP, sourcePanelBacks: 4 },
      contract: (contract) => {
        citedLeftByBack(contract);
        contract.answer = { method: "copy", copy: { id: "com.larus.nova:id/msg_action_copy" } };
      },
      screen: (screen) =>
        left
          ? {
              foreground: MOCK_PACKAGE,
              nodes: [
                { className: "android.widget.TextView", text: "合成历史对话" },
                { className: "android.widget.TextView", text: "使用 2 个工具，合成说明" },
              ],
            }
          : screen,
    });
    h.hooks.intercept = (call) => {
      if (call === "back" && (pressed += 1) === 4) left = true;
      else if (left && (call === "tap" || call === "swipe" || call === "back"))
        gesturesElsewhere += 1;
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
    });
    expect(unreadable(h)).toMatchObject([{ list: "cited", cause: "source_panel_not_closed" }]);
    expect(failureCause(h)).toMatchObject({ cause: "answer_end_not_visible" });
    expect(gesturesElsewhere).toBe(0);
    expect(lookedForSearchEntry(h)).toBe(false);
    expect(h.driverCalls()).not.toContain("readClipboard");
  });

  it("merges the keywords of several lists, each once", async () => {
    const h = await harness({
      // The cited panel's keyword line names one keyword twice; the search steps repeat it.
      app: { ...APP, queries: "合成关键词一、合成关键词零、合成关键词一" },
      contract: (contract) => {
        twoPaths(contract);
        Object.assign(path(contract, "cited").searchQueries as object, { separator: "、" });
      },
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await h.records())[0]!.searchState!.searchQueries).toEqual([
      "合成关键词一",
      "合成关键词零",
      "合成关键词二",
    ]);
  });

  // A contract with one path writes what it always wrote: the keyword line as the panel shows
  // it. Real panels repeat a keyword (research/driver-spike.md §11).
  it("keeps the keyword line of a single path as the panel shows it", async () => {
    const h = await harness({
      app: {
        citations: [{ title: "合成来源一" }],
        queries: "合成关键词一、合成关键词二、合成关键词一",
      },
      contract: (contract) =>
        void Object.assign(contract.citations.searchQueries as object, { separator: "、" }),
    });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect((await h.records())[0]!.searchState!.searchQueries).toEqual([
      "合成关键词一",
      "合成关键词二",
      "合成关键词一",
    ]);
  });

  // A contract with one path writes what it always wrote: no list on a source, no list states,
  // no list in the log.
  it("names no list where the contract declares one path", async () => {
    const one = async (app: Omit<MockChatOptions, "now">) => {
      const h = await harness({ app });
      expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
      const stored = await readFile(
        join(h.store.root, "batches", BATCH, "records", ANSWER_IDS[0]!, "record.json"),
        "utf8",
      );
      expect(stored).not.toContain('"list"');
      expect(stored).not.toContain("citationLists");
      return h;
    };
    const read = await one({ citations: [{ title: "合成来源一" }, { title: "合成来源二" }] });
    expect((await read.records())[0]).toMatchObject({
      citations: [
        { title: "合成来源一", site: null, url: null },
        { title: "合成来源二", site: null, url: null },
      ],
      citationsStatus: "read",
    });
    const failed = await one({ sourceEntryDead: true, citations: [{ title: "合成来源一" }] });
    expect((await failed.records())[0]).toMatchObject({
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "source_panel_not_opened",
    });
    expect(unreadable(failed)).toHaveLength(1);
    expect(unreadable(failed)[0]).not.toHaveProperty("list");
  });
});

// AC-A17 / design §16.3: recording is off unless it is switched on.
describe("recording switched off", () => {
  it("collects a valid sample without a video and without a single recorder command", async () => {
    const h = await harness({
      recording: "off",
      app: { citations: [{ title: "合成来源一" }] },
    });
    expect(await h.collect()).toEqual({
      answerId: ANSWER_IDS[0],
      status: "valid",
      failureCode: null,
      phase: null,
    });
    const [record] = await h.records();
    expect(record).toMatchObject({
      status: "valid",
      answerText: "合成回答第一段\n合成回答第二段",
      citationsStatus: "read",
      video: null,
      screenshot: { file: "final.png" },
      uiDump: { file: "ui.xml" },
    });
    expect(record).not.toHaveProperty("evidenceIssue");
    expect(await h.recordFiles()).toEqual(["final.png", "record.json", "ui.xml"]);
    // No screenrecord, pidof, pkill, pull or ffprobe.
    expect(h.recorderDevice.calls).toEqual([]);
    // The send is counted as always.
    expect(await h.store.readLedger()).toHaveLength(1);
  });

  it("keeps a failure's record without a video too", async () => {
    const h = await harness({ recording: "off", app: { neverCompletes: true } });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "collection_timeout",
      phase: "post_send",
    });
    expect((await h.records())[0]).toMatchObject({
      video: null,
      screenshot: { file: "failure.png" },
    });
    expect(h.recorderDevice.calls).toEqual([]);
  });

  it("marks the record evidence_incomplete only for a completion screenshot that cannot be taken", async () => {
    const h = await harness({ recording: "off" });
    h.hooks.intercept = (method) => {
      if (method === "screenshot") throw new DriverError("command_failed");
    };
    expect(await h.collect()).toMatchObject({
      status: "evidence_incomplete",
      evidenceIssue: "screenshot_failed",
    });
    expect((await h.records())[0]).toMatchObject({
      video: null,
      screenshot: null,
      citationsStatus: "absent",
    });
  });
});

// AC-A7 / design §10: one case per failure class.
describe("failure classes", () => {
  it("device_offline: an emulator that is not running fails the pre-check without an attempt", async () => {
    const h = await harness({ emulators: [emulatorB()] });
    expect(await h.collect()).toEqual({
      answerId: null,
      status: "failed",
      failureCode: "device_offline",
      phase: "pre_send",
    });
    expect(await h.records()).toEqual([]);
    expect(h.recorderDevice.started).toEqual([]);
    expect(h.drivers.opened).toEqual([]);
    await expectNothingSent(h);
  });

  it("device_offline: a device that stops answering after the send is recorded as such", async () => {
    const h = await harness();
    h.hooks.intercept = () => {
      if (h.app.sent.length > 0) throw new DriverError("unreachable");
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "device_offline",
      phase: "post_send",
    });
    const [record] = await h.records();
    // The question counts as sent; the video is kept; no screenshot could be taken.
    expect(await h.store.readLedger()).toHaveLength(1);
    expect(record).toMatchObject({
      video: { file: `${ANSWER_IDS[0]}.mp4` },
      screenshot: null,
      uiDump: null,
      answerText: null,
    });
  });

  it("device_config_mismatch: a device whose time zone was reset is not asked", async () => {
    const h = await harness({ emulators: [emulatorA({ timezone: "Asia/Taipei" }), emulatorB()] });
    expect(await h.collect()).toMatchObject({
      answerId: null,
      failureCode: "device_config_mismatch",
    });
    expect(await h.records()).toEqual([]);
    await expectNothingSent(h);
  });

  it("app_crashed: an App that never comes to the foreground fails before the send", async () => {
    const h = await harness({ app: { crash: "launch" } });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_crashed",
      phase: "pre_send",
    });
    await expectNothingSent(h);
  });

  it("waits for a slow cold start instead of calling the launcher a crash", async () => {
    const h = await harness({ app: { slowStartReads: 6 } });
    expect(await h.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect(h.app.sent).toHaveLength(1);
  });

  it("answers a not-responding dialog during the start with wait, a bounded number of times", async () => {
    const recovered = await harness({ app: { startupAnr: 5 } });
    expect(await recovered.collect()).toMatchObject({ status: "valid", failureCode: null });
    expect(recovered.app.running).toBe(true);

    const stuck = await harness({ app: { startupAnr: 6 } });
    expect(await stuck.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_crashed",
      phase: "pre_send",
    });
    await expectNothingSent(stuck);
  });

  it("keeps waiting while the foreground cannot be told, and fails the start when it never can", async () => {
    // During a start the focused window is briefly unknown.
    let looks = 0;
    const transient = await harness({
      screen: (screen) => ((looks += 1) <= 5 ? { ...screen, foreground: null } : screen),
    });
    expect(await transient.collect()).toMatchObject({ status: "valid", failureCode: null });

    // Nothing is typed into a window that was never confirmed to be the App.
    const never = await harness({ screen: (screen) => ({ ...screen, foreground: null }) });
    const startedAt = never.clock.now().getTime();
    expect(await never.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_crashed",
      phase: "pre_send",
    });
    await expectNothingSent(never);
    expect(never.driverCalls().filter((call) => call === "tap" || call === "setText")).toEqual([]);
    // Bounded by the start-up limit (default 90 s).
    const waited = never.clock.now().getTime() - startedAt;
    expect(waited).toBeGreaterThanOrEqual(90_000);
    expect(waited).toBeLessThan(100_000);
    // The App never came up, so nothing was recorded: no record, no answer ID.
    expect(await never.records()).toEqual([]);
    expect(never.logs).toContainEqual({
      event: "app_attempt_skipped",
      batchId: BATCH,
      device: DEVICE_A,
      platform: "doubao",
      failureCode: "app_crashed",
      cause: "startup_timeout",
    });
  });

  it("app_crashed: a start-up dialog without a wait choice is a crash at once, closed by its own button", async () => {
    const h = await harness({
      screen: (screen, app) =>
        app.running && screen.foreground === MOCK_PACKAGE
          ? {
              foreground: "android",
              nodes: [
                {
                  id: "android:id/aerr_close",
                  text: "关闭应用",
                  onTap: () => app.terminate(MOCK_PACKAGE),
                },
              ],
            }
          : screen,
    });
    const startedAt = h.clock.now().getTime();
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_crashed",
      phase: "pre_send",
    });
    await expectNothingSent(h);
    // No waiting, and the only tap is the dialog's close button.
    expect(h.clock.now().getTime()).toBe(startedAt);
    expect(h.driverCalls().filter((call) => call === "tap")).toHaveLength(1);
    expect(h.driverCalls()).not.toContain("terminateApp");
    expect(h.app.running).toBe(false);
    // The App is brought up before anything is recorded: a start that fails leaves no record.
    expect(await h.records()).toEqual([]);
    expect(h.logs).toContainEqual(
      expect.objectContaining({ event: "app_attempt_skipped", cause: "system_dialog" }),
    );
  });

  it("app_crashed: a not-responding dialog still showing at the start-up limit is not answered again", async () => {
    // One poll interval is the whole limit: the dialog is answered once and is still there.
    const h = await harness({ app: { startupAnr: 2 }, timings: { readyTimeoutMs: 3_000 } });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_crashed",
      phase: "pre_send",
    });
    await expectNothingSent(h);
    // One "wait", then the dialog's own close button.
    expect(h.driverCalls().filter((call) => call === "tap")).toHaveLength(2);
    expect(h.driverCalls()).not.toContain("terminateApp");
    expect(h.app.running).toBe(false);
    expect(await h.records()).toEqual([]);
    expect(h.logs).toContainEqual(
      expect.objectContaining({ event: "app_attempt_skipped", cause: "system_dialog" }),
    );
  });

  it("app_crashed: an App that leaves the foreground after the send is stopped for a fresh start", async () => {
    const h = await harness({ app: { crash: "after_send" } });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_crashed",
      phase: "post_send",
    });
    expect(h.driverCalls()).toContain("terminateApp");
    expect(h.app.running).toBe(false);
    // AC-A15: the failed attempt keeps its video, bound to its answer ID.
    const [record] = await h.records();
    expect(record!.video!.file).toBe(`${ANSWER_IDS[0]}.mp4`);
    expect(await h.recordFiles()).toContain(`${ANSWER_IDS[0]}.mp4`);
  });

  it("app_crashed: the system 'not responding' dialog is captured, then closed through its own button", async () => {
    const h = await harness({ app: { anr: true } });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_crashed",
      phase: "post_send",
    });
    // Closed via the dialog, not by stopping the package.
    expect(h.app.running).toBe(false);
    expect(h.driverCalls()).not.toContain("terminateApp");
    // The dialog is in the failure evidence.
    const directory = join(h.store.root, "batches", BATCH, "records", ANSWER_IDS[0]!);
    expect(await h.recordFiles()).toContain("failure.png");
    expect(await readFile(join(directory, "ui.xml"), "utf8")).toContain("android:id/aerr_wait");
  });

  it("login_required: a login screen is only looked at, and the failure keeps its video", async () => {
    const h = await harness({ app: { blocker: { code: "login_required", when: "launch" } } });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "login_required",
      phase: "pre_send",
    });
    await expectNothingSent(h);
    // Nothing was tapped or typed on that screen.
    expect(h.driverCalls().filter((call) => call === "tap" || call === "setText")).toEqual([]);
    const [record] = await h.records();
    expect(record).toMatchObject({
      sentAt: null,
      video: { file: `${ANSWER_IDS[0]}.mp4` },
      screenshot: { file: "failure.png" },
    });
  });

  it("challenge_required: a verification screen after the send is never interacted with", async () => {
    const h = await harness({
      app: { blocker: { code: "challenge_required", when: "after_send" } },
    });
    let afterSend: (keyof DeviceDriver)[] = [];
    h.hooks.intercept = (method) => {
      if (h.app.sent.length > 0) afterSend = [...afterSend, method];
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "challenge_required",
      phase: "post_send",
    });
    expect(afterSend.length).toBeGreaterThan(0);
    for (const method of afterSend)
      expect(["find", "foregroundPackage", "screenshot", "hierarchy", "home", "close"]).toContain(
        method,
      );
  });

  it("rate_limited: the binding stops for the rest of the day, across restarts", async () => {
    const h = await harness({ app: { blocker: { code: "rate_limited", when: "after_send" } } });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "rate_limited",
      phase: "post_send",
    });
    expect(h.gate.decide(h.binding)).toMatchObject({
      allowed: false,
      reason: "rate_limited_today",
    });
    const status = await h.store.readBindingStatus();
    expect(status.bindings[h.binding.key]).toMatchObject({
      reason: "rate_limited",
      day: "2026-11-15",
      answerId: ANSWER_IDS[0],
    });
    // A new process sees the same stop.
    const restarted = new RateGate({
      ledger: await h.store.readLedger(),
      bindingStatus: status,
      now: h.clock.now,
    });
    expect(restarted.decide(h.binding)).toMatchObject({ reason: "rate_limited_today" });
  });

  it("gives a blocker or crash that shows up while the answer is read its own class", async () => {
    // The screen changes after the completion marker was seen, when the answer is being read.
    const during = async (screen: MockScreen) => {
      let covered: MockScreen | undefined;
      const h = await harness({ screen: (own) => covered ?? own });
      h.hooks.intercept = (method) => {
        if (method === "hierarchy" && h.app.sent.length > 0) covered = screen;
      };
      return { h, outcome: await h.collect() };
    };

    const limited = await during({
      foreground: MOCK_PACKAGE,
      nodes: [{ className: "android.widget.TextView", text: "合成提示：操作过于频繁" }],
    });
    expect(limited.outcome).toMatchObject({
      status: "failed",
      failureCode: "rate_limited",
      phase: "post_send",
    });
    // Not a contract change: the binding stays stopped for the day, across restarts.
    expect(limited.h.gate.decide(limited.h.binding)).toMatchObject({
      reason: "rate_limited_today",
    });
    expect(
      (await limited.h.store.readBindingStatus()).bindings[limited.h.binding.key],
    ).toMatchObject({ reason: "rate_limited" });
    expect(limited.h.logs).toContainEqual(
      expect.objectContaining({
        event: "app_attempt_failure_cause",
        failureCode: "rate_limited",
        cause: "blocker_marker",
      }),
    );

    const gone = await during({
      foreground: "com.android.launcher3",
      nodes: [{ id: "com.android.launcher3:id/workspace", desc: "主屏幕" }],
    });
    expect(gone.outcome).toMatchObject({
      status: "failed",
      failureCode: "app_crashed",
      phase: "post_send",
    });
    expect(gone.h.driverCalls()).toContain("terminateApp");
  });

  it("emulator_rejected: the platform is marked unavailable with its evidence", async () => {
    const h = await harness({ app: { blocker: { code: "emulator_rejected", when: "launch" } } });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "emulator_rejected",
      phase: "pre_send",
    });
    expect((await h.store.readPlatformStatus()).platforms.doubao).toMatchObject({
      status: "unavailable",
      failureCode: "emulator_rejected",
      batchId: BATCH,
      answerId: ANSWER_IDS[0],
    });
    await expectNothingSent(h);
  });

  it.each([
    ["a required control is missing", { app: { missing: "new_conversation" } }, "pre_send"],
    ["an undeclared dialog covers the App", { app: { undeclaredDialog: "launch" } }, "pre_send"],
    ["the send control never appears", { app: { missing: "send" } }, "pre_send"],
    [
      "the composer keeps a different text",
      { question: "合成问题\n\n合成补充", app: { composerDropsBlankLines: true } },
      "pre_send",
    ],
    ["the answer is the question", { app: { echo: true } }, "post_send"],
    [
      "the copied answer is the question",
      {
        app: { echo: true },
        contract: (contract) =>
          void (contract.answer = {
            method: "copy",
            copy: { id: "com.larus.nova:id/msg_action_copy" },
          }),
      },
      "post_send",
    ],
    // The sent text is the bare question, and Apps re-space what they show: an echo differs
    // from the question in spaces only.
    [
      "the answer is the question as the App re-spaces it",
      { question: "合成ulinktek问题，合成补充", app: { echo: true, respaceBubble: true } },
      "post_send",
    ],
    [
      "the copied answer is the question as the App re-spaces it",
      {
        question: "合成ulinktek问题，合成补充",
        app: { echo: true, respaceBubble: true },
        contract: (contract) =>
          void (contract.answer = {
            method: "copy",
            copy: { id: "com.larus.nova:id/msg_action_copy" },
          }),
      },
      "post_send",
    ],
  ] satisfies [string, HarnessOptions, string][])(
    "app_contract_changed: %s",
    async (_name, options, phase) => {
      const h = await harness(options);
      expect(await h.collect()).toMatchObject({
        status: "failed",
        failureCode: "app_contract_changed",
        phase,
      });
      if (phase === "pre_send") await expectNothingSent(h);
      else expect(await h.store.readLedger()).toHaveLength(1);
      const [record] = await h.records();
      expect(record).toMatchObject({ answerText: null, screenshot: { file: "failure.png" } });
    },
  );

  it("app_contract_changed: several matches are not resolved by guessing", async () => {
    // Several text views match the second step; without an explicit `pick` it refuses to choose.
    const ambiguous = await harness({
      contract: (value) =>
        void Object.assign(value, {
          newConversation: {
            steps: [
              { tap: { id: "com.larus.nova:id/back_icon" } },
              { tap: { className: "android.widget.TextView" } },
            ],
            emptyMarker: { id: "com.larus.nova:id/larus_mode_switch_welcome_title" },
          },
        }),
    });
    expect(await ambiguous.collect()).toMatchObject({
      failureCode: "app_contract_changed",
      phase: "pre_send",
    });
    await expectNothingSent(ambiguous);
  });

  // AC-A8 and the contract rules: versions, language and review age must all agree.
  it.each([
    [
      "the installed version is not the reviewed one",
      { emulators: [emulatorA({ packages: { "com.larus.nova": "15.8.0" } }), emulatorB()] },
      undefined,
    ],
    [
      "the contract was reviewed for another version",
      { contract: (contract) => void (contract.appVersions = ["15.6.0"]) },
      "version_mismatch",
    ],
    [
      "the contract was reviewed in another language",
      { contract: (contract) => void (contract.locale = "en-US") },
      "locale_mismatch",
    ],
    [
      "the review is older than seven days",
      { contract: (contract) => void (contract.reviewedAt = "2026-11-08T03:59:59.000Z") },
      "review_expired",
    ],
    [
      "the review is dated in the future",
      { contract: (contract) => void (contract.reviewedAt = "2026-11-15T04:05:00.000Z") },
      "review_in_future",
    ],
  ] satisfies [string, HarnessOptions, string | undefined][])(
    "app_contract_unverified: %s",
    async (_name, options, reason) => {
      const h = await harness(options);
      expect(await h.collect()).toEqual({
        answerId: null,
        status: "failed",
        failureCode: "app_contract_unverified",
        phase: "pre_send",
      });
      expect(await h.records()).toEqual([]);
      expect(h.recorderDevice.started).toEqual([]);
      expect(h.drivers.opened).toEqual([]);
      await expectNothingSent(h);
      if (reason)
        expect(h.logs).toContainEqual({
          event: "app_contract_rejected",
          device: DEVICE_A,
          platform: "doubao",
          reason,
        });
    },
  );

  it("collection_timeout: an answer that never completes keeps its partial text but is not valid", async () => {
    const h = await harness({ app: { neverCompletes: true } });
    const startedAt = h.clock.now().getTime();
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "collection_timeout",
      phase: "post_send",
    });
    // Default limit: 300 s from the send.
    expect(h.clock.now().getTime() - startedAt).toBeGreaterThanOrEqual(300_000);
    const [record] = await h.records();
    expect(record).toMatchObject({ answerText: "合成回答第一段", completedAt: null });
    expect(record!.sentAt).not.toBeNull();
  });

  it("collection_timeout: the wait limit is configurable", async () => {
    const h = await harness({
      app: { generationMs: 60_000 },
      timings: { completionTimeoutMs: 30_000 },
    });
    expect(await h.collect()).toMatchObject({ failureCode: "collection_timeout" });
  });

  // AC-A14: no recording, no question.
  it("recording_failed: nothing is typed, sent or counted when the recorder does not start", async () => {
    const h = await harness({ recorder: { process: "never_appears" } });
    expect(await h.collect()).toEqual({
      answerId: ANSWER_IDS[0],
      status: "failed",
      failureCode: "recording_failed",
      phase: "pre_send",
    });
    await expectNothingSent(h);
    // The App was brought up first (a cold start is never recorded), but nothing was done in it.
    expect(h.driverCalls()[0]).toBe("activateApp");
    expect(h.driverCalls().at(-1)).toBe("close");
    expect(h.driverCalls().filter((call) => call === "tap" || call === "setText")).toEqual([]);
    expect(await h.records()).toMatchObject([
      { status: "failed", failureCode: "recording_failed", sentAt: null, video: null },
    ]);
    expect(await h.recordFiles()).toEqual(["record.json"]);
  });

  it("recording_failed: a recorder dying before the send stops the attempt unsent", async () => {
    const h = await harness();
    // The recorder process ends on its own while the question is being typed.
    h.hooks.intercept = (method) => {
      if (method === "setText") h.recorderDevice.crash();
    };
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "recording_failed",
      phase: "pre_send",
      evidenceIssue: "recorder_exited",
    });
    await expectNothingSent(h);
  });
});

describe("evidence of a sent question", () => {
  // AC-A14: an interrupted recording makes the sample evidence_incomplete and leaves the cell
  // pending, so a rerun of the batch collects it again.
  it("marks the record evidence_incomplete when the recording breaks after the send", async () => {
    const h = await harness();
    h.hooks.intercept = () => {
      if (h.app.sent.length > 0) h.recorderDevice.crash();
    };
    expect(await h.collect()).toEqual({
      answerId: ANSWER_IDS[0],
      status: "evidence_incomplete",
      failureCode: null,
      phase: "post_send",
      evidenceIssue: "recorder_exited",
    });
    const records = await h.records();
    expect(records[0]).toMatchObject({
      status: "evidence_incomplete",
      evidenceIssue: "recorder_exited",
      answerText: "合成回答第一段\n合成回答第二段",
      video: { file: `${ANSWER_IDS[0]}.mp4` },
    });
    // The send was counted, and the cell is still pending for the next run.
    expect(await h.store.readLedger()).toHaveLength(1);
    expect(pendingCells([QUESTION], ["doubao"], records)).toEqual([
      { platformCode: "doubao", questionText: QUESTION },
    ]);

    // The rerun asks again and the cell is done once a complete sample exists.
    h.hooks.intercept = undefined;
    h.clock.advance(600_000);
    const again = await h.collect();
    expect(again).toMatchObject({ answerId: ANSWER_IDS[1], status: "valid" });
    expect(h.app.sent).toEqual([QUESTION, QUESTION]);
    expect(pendingCells([QUESTION], ["doubao"], await h.records())).toEqual([]);
  });

  it("marks the record evidence_incomplete when the video is shorter than send to completion", async () => {
    const h = await harness({ recorder: { ffprobe: ffprobeAnswer("1.000000") } });
    expect(await h.collect()).toMatchObject({
      status: "evidence_incomplete",
      evidenceIssue: "too_short",
    });
  });

  it("marks the record evidence_incomplete when the completion screenshot cannot be taken", async () => {
    const h = await harness();
    h.hooks.intercept = (method) => {
      if (method === "screenshot") throw new DriverError("command_failed");
    };
    expect(await h.collect()).toMatchObject({
      status: "evidence_incomplete",
      evidenceIssue: "screenshot_failed",
    });
    expect((await h.records())[0]).toMatchObject({ screenshot: null, uiDump: { file: "ui.xml" } });
  });

  it("ends an attempt that was aborted before the send as a failure with nothing sent", async () => {
    const h = await harness();
    const abort = new AbortController();
    h.hooks.intercept = (method) => {
      if (method === "setText") abort.abort();
    };
    expect(await h.collect(abort.signal)).toMatchObject({
      status: "failed",
      failureCode: "collection_timeout",
      phase: "pre_send",
    });
    await expectNothingSent(h);
    expect(await h.records()).toHaveLength(1);
  });

  it("ends an attempt that is aborted while the App is still starting, with nothing sent", async () => {
    const h = await harness({ app: { slowStartReads: 1_000 } });
    const abort = new AbortController();
    h.hooks.intercept = (method) => {
      if (method === "foregroundPackage") abort.abort();
    };
    const startedAt = h.clock.now().getTime();
    expect(await h.collect(abort.signal)).toMatchObject({
      status: "failed",
      failureCode: "collection_timeout",
      phase: "pre_send",
    });
    await expectNothingSent(h);
    // Still starting: nothing was recorded yet, so there is no record.
    expect(await h.records()).toEqual([]);
    // Aborted at the first wait instead of waiting out the start-up limit.
    expect(h.clock.now().getTime()).toBe(startedAt);
    expect(h.drivers.opened.map((driver) => driver.closed)).toEqual([true]);
  });

  it("ends an attempt that is aborted while waiting for the answer with a record", async () => {
    const h = await harness({ app: { neverCompletes: true } });
    const abort = new AbortController();
    h.hooks.intercept = () => {
      if (h.app.sent.length > 0) abort.abort();
    };
    expect(await h.collect(abort.signal)).toMatchObject({
      status: "failed",
      failureCode: "collection_timeout",
      phase: "post_send",
    });
    // Aborted at once instead of waiting out the 300 s limit.
    expect(h.clock.now().getTime() - Date.parse("2026-11-15T04:00:00.000Z")).toBeLessThan(60_000);
  });
});

describe("login probe", () => {
  async function probe(
    options: { app?: Omit<MockChatOptions, "now">; reviewedAt?: string; popup?: boolean } = {},
  ) {
    const clock = steppingClock();
    const app = new MockChatApp({ now: clock.now, ...options.app });
    const drivers = new MockDrivers({ [DEVICE_A]: app });
    const config = parseBindings(bindingsFixture());
    const binding = resolveBindings(config)[0]!;
    const logs: Record<string, unknown>[] = [];
    const login = createLoginProbe({
      contracts: new ContractSource({
        load: async () => appContractSchema.parse(referenceContract(options.reviewedAt)),
      }),
      drivers,
      now: clock.now,
      sleep: clock.sleep,
      log: (line) => logs.push(line),
    });
    const result = await login(binding, {
      deviceId: DEVICE_A,
      serial: "emulator-5556",
      bootCompleted: true,
      actual: {
        androidVersion: "15",
        abi: "arm64-v8a",
        pageSize: "4k",
        locale: "zh-Hans-CN",
        timezone: "Asia/Shanghai",
      },
      mismatches: [],
      failure: null,
    });
    return { result, app, drivers, logs };
  }

  it("reports a logged-in account and leaves the App on the home screen", async () => {
    const { result, app, drivers } = await probe();
    expect(result).toBe("logged_in");
    expect(app.screen().foreground).toBe("com.android.launcher3");
    expect(drivers.opened[0]!.closed).toBe(true);
    // Looking only: nothing typed, nothing sent.
    expect(drivers.opened[0]!.calls).not.toContain("setText");
    expect(app.sent).toEqual([]);
  });
  it("reports login_required when the logged-in marker is not there", async () => {
    expect((await probe({ app: { loggedIn: false } })).result).toBe("login_required");
  });
  it("closes a declared popup first", async () => {
    expect((await probe({ app: { popup: true } })).result).toBe("logged_in");
  });
  it("reports the blocker it sees without touching it", async () => {
    const { result, drivers } = await probe({
      app: { blocker: { code: "challenge_required", when: "launch" } },
    });
    expect(result).toBe("challenge_required");
    expect(drivers.opened[0]!.calls).not.toContain("tap");
  });
  it("reports an App that does not start as app_crashed and logs the cause in fixed words", async () => {
    const { result, drivers, logs } = await probe({ app: { crash: "launch" } });
    expect(result).toBe("app_crashed");
    expect(logs).toEqual([
      {
        event: "app_login_probe_failed",
        device: DEVICE_A,
        platform: "doubao",
        failureCode: "app_crashed",
        cause: "startup_timeout",
      },
    ]);
    expect(drivers.opened[0]!.closed).toBe(true);
  });
  it("reports app_contract_unverified for a stale contract without opening a session", async () => {
    const { result, drivers } = await probe({ reviewedAt: "2026-11-01T00:00:00.000Z" });
    expect(result).toBe("app_contract_unverified");
    expect(drivers.opened).toEqual([]);
  });
});

// Design §16.8: an App that draws its own screen. Its controls are image views without any
// label and its texts are content descriptions (real device, Zhipu Qingyan 3.8.7; the App here
// is synthetic).
describe("an App whose controls carry no label", () => {
  const sources = [
    {
      title: "合成来源一",
      snippet: ["合成摘要第一行", "合成摘要第二行"],
      url: "https://example.invalid/1",
    },
    { title: "合成来源二", url: "http://example.invalid/2" },
    { title: "合成来源三", snippet: [], url: "https://example.invalid/3" },
  ];

  it("asks, copies the answer and reads title and link of every source", async () => {
    const h = await harness({ recording: "off", icons: { citations: sources } });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    expect(h.icons.sent).toEqual([QUESTION]);
    // No tap landed on the attach, voice, table, speak or share icon, on a mode switch, a
    // follow-up or a source card.
    expect(h.icons.strayTaps).toEqual([]);
    const [record] = await h.records();
    expect(record!.answerText).toBe("合成回答第一段\n合成回答第二段");
    expect(record!.citations).toEqual([
      { title: "合成来源一", site: null, url: "https://example.invalid/1" },
      { title: "合成来源二", site: null, url: "http://example.invalid/2" },
      { title: "合成来源三", site: null, url: "https://example.invalid/3" },
    ]);
    expect(record!.citationsStatus).toBe("read");
    expect(record!.searchState).toMatchObject({ webSearch: "on" });
  });

  it("leaves a conversation that is open for a new one before it asks", async () => {
    const h = await harness({ recording: "off", icons: { startInConversation: true } });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    expect(h.icons.sent).toEqual([QUESTION]);
    expect(h.icons.strayTaps).toEqual([]);
    const [record] = await h.records();
    expect(record!.citationsStatus).toBe("absent");
    expect(record!.searchState).toMatchObject({ webSearch: "unknown" });
  });

  // The paragraphs of an answer take a tap, as the mode switch of the home screen does, and one
  // of them can read "任务". A marker that is only those words is then there in a conversation,
  // and so is the `unless` of the step that would leave it.
  const LINE_LIKE_SWITCH = ["任务", "合成回答第二段"];
  it("leaves an open conversation one of whose lines reads like the mode switch", async () => {
    const h = await harness({
      recording: "off",
      // The conversation is open in the middle of its answer: neither the source entry nor the
      // row of icons is in the hierarchy, so nothing but the marker tells it from an empty one.
      // The marker is the two halves of the switch side by side; a line of an answer stands
      // alone in its row, whatever it reads.
      icons: { startInConversation: true, endBelowScreen: true, answer: LINE_LIKE_SWITCH },
    });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    expect(h.icons.sent).toEqual([QUESTION]);
    // No send in a conversation that was open, and no tap on anything else.
    expect(h.icons.strayTaps).toEqual([]);
    const [record] = await h.records();
    expect(record!.answerText).toBe(LINE_LIKE_SWITCH.join("\n"));
  });
  it("is held back by the answer's own controls where the marker is only the switch's words", async () => {
    const words = { desc: "任务", clickable: true };
    const h = await harness({
      recording: "off",
      icons: { startInConversation: true, answer: LINE_LIKE_SWITCH },
      iconContract: (contract) => {
        contract.newConversation.steps[1]!.unless = words;
        Object.assign(contract.newConversation, { emptyMarker: words });
      },
    });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "pre_send",
    });
    expect(h.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      cause: "conversation_not_empty",
    });
    expect(h.icons.sent).toEqual([]);
    expect(h.icons.strayTaps).toEqual([]);
    expect(await h.store.readLedger()).toEqual([]);
  });

  // Real device, 2026-11-16: a text set into the input without the keyboard is reported back
  // by the field's view, while the App still shows its placeholder and the icon at the right
  // of the composer still switches to voice input. The first attended question was "sent" by
  // tapping that icon.
  it("sends nothing when the App has not taken the text, although the field reports it", async () => {
    const h = await harness({
      recording: "off",
      icons: {},
      // Without the step that gives the input the keyboard.
      iconContract: (contract) => void (contract.composer.prepare = []),
    });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "pre_send",
    });
    // No send icon; the image views that do not say they take a tap are pictures, several.
    expect(h.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      cause: "selector_ambiguous",
    });
    expect(h.icons.sent).toEqual([]);
    expect(h.icons.strayTaps).toEqual([]);
    expect(await h.store.readLedger()).toEqual([]);
    expect(h.sentCallbacks()).toBe(0);
  });
  it("taps the voice switch where the send control is named by its place alone", async () => {
    const h = await harness({
      recording: "off",
      icons: {},
      timings: { completionTimeoutMs: 30_000 },
      iconContract: (contract) =>
        Object.assign(contract.composer, {
          prepare: [],
          send: { className: "android.widget.ImageView", unlabeled: true, pick: "last" },
        }),
    });
    // Counted as sent, never asked: what the selector above cost on the real device.
    expect(await h.collect()).toMatchObject({ status: "failed", phase: "post_send" });
    expect(h.icons.sent).toEqual([]);
    expect(h.icons.strayTaps).toEqual(["voice"]);
    expect(await h.store.readLedger()).toHaveLength(1);
  });
  it("gives the send icon time to be the only match while the screen still changes", async () => {
    // The hint above the composer, with its two pictures, is still on screen for a moment
    // after the text is typed: three image views meet the send selector, then one.
    const h = await harness({ recording: "off", icons: { hintLingersMs: 1_500 } });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    expect(h.icons.sent).toEqual([QUESTION]);
    expect(h.icons.strayTaps).toEqual([]);
  });

  it("does not take the row of icons for the end of the answer while it is being written", async () => {
    const h = await harness({ recording: "off", icons: { generationMs: 45_000 } });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    const [record] = await h.records();
    expect(
      Date.parse(record!.completedAt!.utc) - Date.parse(record!.sentAt!.utc),
    ).toBeGreaterThanOrEqual(45_000);
    expect(record!.answerText).toBe("合成回答第一段\n合成回答第二段");
  });

  it("reads a source list longer than the screen card by card", async () => {
    const many = Array.from({ length: 9 }, (_, index) => ({
      title: `合成来源${index + 1}`,
      url: `https://example.invalid/${index + 1}`,
    }));
    const h = await harness({
      recording: "off",
      icons: { citations: many, sourcesViewport: 4 },
    });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    const [record] = await h.records();
    expect(record!.citations.map((source) => source.title)).toEqual(
      many.map((source) => source.title),
    );
  });

  it("leaves the sources out when one of them has no link, and keeps the answer", async () => {
    const h = await harness({
      recording: "off",
      icons: { citations: [...sources, { title: "合成来源四", url: null }] },
    });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    const [record] = await h.records();
    expect(record!.answerText).toBe("合成回答第一段\n合成回答第二段");
    expect(record!.citations).toEqual([]);
    expect(record!.citationsStatus).toBe("unreadable");
    expect(record!.citationsIssue).toBe("citation_count");
    expect(h.icons.strayTaps).toEqual([]);
  });

  // The self-check's look at the login state, on the given device model and contract.
  function probeLogin(
    h: Awaited<ReturnType<typeof harness>>,
    drivers: MockDrivers = h.drivers,
    contract: unknown = iconAppContract(),
  ) {
    const probe = createLoginProbe({
      contracts: new ContractSource({ load: async () => appContractSchema.parse(contract) }),
      drivers,
      now: h.clock.now,
      sleep: h.clock.sleep,
    });
    return probe(h.binding, {
      deviceId: DEVICE_A,
      serial: "emulator-5556",
      bootCompleted: true,
      actual: {
        androidVersion: "15",
        abi: "arm64-v8a",
        pageSize: "4k",
        locale: "zh-Hans-CN",
        timezone: "Asia/Shanghai",
      },
      mismatches: [],
      failure: null,
    });
  }
  it("looks for the logged-in marker behind the menu icon", async () => {
    for (const [loggedIn, expected] of [
      [true, "logged_in"],
      [false, "login_required"],
    ] as const) {
      const h = await harness({ icons: { loggedIn } });
      expect(await probeLogin(h)).toBe(expected);
      expect(h.icons.sent).toEqual([]);
    }
  });
  // A selector that names a view by its class and its place fits a view of any screen. This
  // contract declares no blocker (none was ever seen in the App it is shaped after), so on a
  // screen it does not know nothing stops a step, and "the first icon" is whatever that screen
  // puts first.
  it("taps no icon of a screen it does not know where a step first waits for a view of its own", async () => {
    const taps: string[] = [];
    const icon = (name: string, beside = false): MockNode => ({
      className: "android.widget.ImageView",
      onTap: () => void taps.push(name),
      ...(beside ? { beside } : {}),
    });
    // A synthetic screen asking for a login: a close icon, a field, a box to tick and two more
    // ways to log in, none of them with a label.
    const wall: MockModel = {
      screen: () => ({
        foreground: MOCK_PACKAGE,
        nodes: [
          icon("close"),
          { desc: "合成登录页" },
          { className: "android.widget.EditText", text: "" },
          icon("agree"),
          icon("login_way_one"),
          icon("login_way_two", true),
        ],
      }),
      activate: () => undefined,
      terminate: () => undefined,
      back: () => undefined,
      home: () => undefined,
      swipe: () => undefined,
      clipboard: () => "",
    };
    const h = await harness({ icons: {} });
    const drivers = () => new MockDrivers({ [DEVICE_A]: wall });

    // The contract's first step waits for a view only the App's own chat screen has (the model
    // name of its top bar): the missing view ends the look and nothing is tapped.
    expect(await probeLogin(h, drivers())).toBe("app_contract_changed");
    expect(taps).toEqual([]);

    // Without that step the first icon of whatever screen is there is tapped.
    const unanchored = iconAppContract();
    unanchored.loggedIn.open.shift();
    expect(await probeLogin(h, drivers(), unanchored)).toBe("login_required");
    expect(taps).toEqual(["close"]);
    // On the App's own screens both look the same.
    expect(await probeLogin(h, h.drivers, unanchored)).toBe("logged_in");
  });
});

describe("notices the App writes in place of an answer", () => {
  const TEXT_VIEW = "android.widget.TextView";
  const QUOTA = "合成提示：额度已用尽 去升级";
  const BUSY = "合成提示：排队人数过多 去升级";
  const notices = [
    { marker: { className: TEXT_VIEW, text: QUOTA, clickable: false }, as: "rate_limited" },
    { marker: { className: TEXT_VIEW, text: BUSY, clickable: false }, as: "declined" },
  ];
  const withNotices = (contract: ReturnType<typeof referenceContract>) => {
    contract.completion.notices = notices;
  };
  const notice = (text: string): MockNode => ({ className: TEXT_VIEW, text, clickable: false });
  const contains = (nodes: readonly MockNode[], id: string): boolean =>
    nodes.some((node) => node.id === id || contains(node.children ?? [], id));
  // The App has the sent question on screen: from then on it is the conversation of this ask.
  const asked = (screen: MockScreen) => contains(screen.nodes, "com.larus.nova:id/mock_sent_text");
  const elapsed = (h: { clock: { now: () => Date } }, from: number) =>
    h.clock.now().getTime() - from;

  it("ends the wait at once as rate_limited when the account's quota notice replaces the answer", async () => {
    const h = await harness({
      app: { neverCompletes: true },
      contract: withNotices,
      screen: (screen) =>
        asked(screen) ? { ...screen, nodes: [...screen.nodes, notice(QUOTA)] } : screen,
    });
    const started = h.clock.now().getTime();
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "rate_limited",
      phase: "post_send",
    });
    expect(elapsed(h, started)).toBeLessThan(DEFAULT_TIMINGS.completionTimeoutMs);
    expect(h.gate.decide(h.binding)).toMatchObject({ reason: "rate_limited_today" });
    expect(h.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      failureCode: "rate_limited",
      cause: "notice_rate_limited",
    });
    // The notice is only looked at: its upgrade link is never tapped.
    expect(JSON.stringify(h.logs)).not.toContain("额度已用尽");
  });

  it("ends the wait at once as an ordinary timeout when the platform declines this question", async () => {
    const h = await harness({
      app: { neverCompletes: true },
      contract: withNotices,
      screen: (screen) =>
        asked(screen) ? { ...screen, nodes: [...screen.nodes, notice(BUSY)] } : screen,
    });
    const started = h.clock.now().getTime();
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "collection_timeout",
      phase: "post_send",
    });
    expect(elapsed(h, started)).toBeLessThan(DEFAULT_TIMINGS.completionTimeoutMs);
    // The account is fine: the binding is not stopped for the day.
    expect(h.gate.decide(h.binding)).not.toMatchObject({ reason: "rate_limited_today" });
    expect(h.logs.find((line) => line.event === "app_attempt_failure_cause")).toMatchObject({
      failureCode: "collection_timeout",
      cause: "platform_declined",
    });
    // Whatever partial text is on screen stays evidence of a failed record, never an answer.
    expect((await h.records())[0]).toMatchObject({
      status: "failed",
      failureCode: "collection_timeout",
    });
  });

  it("asks normally when a notice shows before the question is sent, as in an older conversation", async () => {
    const h = await harness({
      contract: withNotices,
      screen: (screen) =>
        asked(screen) ? screen : { ...screen, nodes: [...screen.nodes, notice(QUOTA)] },
    });
    expect(await h.collect()).toMatchObject({ status: "valid" });
    expect(h.gate.decide(h.binding)).not.toMatchObject({ reason: "rate_limited_today" });
  });

  it("keeps an answer that quotes a notice among other words", async () => {
    const h = await harness({
      app: { answer: ["合成回答第一段", `合成回答引用了“${BUSY}”这句话`] },
      contract: withNotices,
    });
    expect(await h.collect()).toMatchObject({ status: "valid" });
  });

  it("gives a login screen priority over a notice shown with it", async () => {
    const h = await harness({
      app: { blocker: { code: "login_required", when: "after_send" } },
      contract: withNotices,
      screen: (screen) => ({ ...screen, nodes: [...screen.nodes, notice(BUSY)] }),
    });
    expect(await h.collect()).toMatchObject({ status: "failed", failureCode: "login_required" });
  });

  it("changes nothing for a contract without notices", async () => {
    const h = await harness({
      app: { neverCompletes: true },
      screen: (screen) =>
        asked(screen) ? { ...screen, nodes: [...screen.nodes, notice(QUOTA)] } : screen,
    });
    expect(await h.collect()).toMatchObject({
      status: "failed",
      failureCode: "collection_timeout",
      phase: "post_send",
    });
    expect(h.gate.decide(h.binding)).not.toMatchObject({ reason: "rate_limited_today" });
  });
});
