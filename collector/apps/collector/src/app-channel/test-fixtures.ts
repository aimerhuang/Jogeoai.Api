import { mkdir, mkdtemp, realpath, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { CommandResult, CommandRunner } from "./command.ts";
import type { AppRecord } from "./evidence.ts";
import { timestampPair, type Sleep } from "./time.ts";

// Synthetic fixtures only: no real device, account, answer or question bank.

export const DEVICE_A = "AI_Apps_4KB";
export const DEVICE_B = "AI_Apps_4KB_2";

// A small valid bindings file: two devices, two platforms, four bindings.
export function bindingsFixture() {
  const device = (id: string) => ({
    id,
    androidVersion: "15",
    abi: "arm64-v8a",
    pageSize: "4k",
    locale: "zh-Hans-CN",
    timezone: "Asia/Shanghai",
  });
  const binding = (deviceId: string, platform: string, version: string, label: string) => ({
    device: deviceId,
    platform,
    reviewedAppVersion: version,
    accountLabel: label,
    enabled: true as boolean,
    minIntervalSeconds: 600,
    hourlyQuota: 4,
    dailyQuota: 30,
  });
  return {
    version: 1,
    devices: [device(DEVICE_A), device(DEVICE_B)],
    platforms: {
      doubao: { package: "com.larus.nova", minIntervalSeconds: 120, dailyCap: 120 },
      kimi: { package: "com.moonshot.kimichat", minIntervalSeconds: 120, dailyCap: 120 },
    } as Record<string, { package: string; minIntervalSeconds: number; dailyCap: number }>,
    bindings: [
      binding(DEVICE_A, "doubao", "15.7.0", "doubao-a"),
      binding(DEVICE_A, "kimi", "3.1.8", "kimi-a"),
      binding(DEVICE_B, "doubao", "15.7.0", "doubao-b"),
      binding(DEVICE_B, "kimi", "3.1.8", "kimi-b"),
    ] as Record<string, unknown>[],
  };
}

export interface FakeEmulator {
  serial: string;
  avd: string;
  // Second column of `adb devices`.
  state?: string;
  bootCompleted?: string;
  release?: string;
  abi?: string;
  pageSize?: string;
  locales?: string;
  timezone?: string;
  // package -> installed versionName
  packages?: Record<string, string>;
}
export const emulatorA = (overrides: Partial<FakeEmulator> = {}): FakeEmulator => ({
  serial: "emulator-5556",
  avd: DEVICE_A,
  packages: { "com.larus.nova": "15.7.0", "com.moonshot.kimichat": "3.1.8" },
  ...overrides,
});
export const emulatorB = (overrides: Partial<FakeEmulator> = {}): FakeEmulator => ({
  serial: "emulator-5558",
  avd: DEVICE_B,
  packages: { "com.larus.nova": "15.7.0", "com.moonshot.kimichat": "3.1.8" },
  ...overrides,
});

const ok = (stdout: string): CommandResult => ({ code: 0, stdout, stderr: "" });
const failed: CommandResult = { code: 1, stdout: "", stderr: "" };

// Answers the read-only adb commands the inspector issues and records every call.
// `extraSerials` are other attached devices (for example a phone) listed by `adb devices`.
export function fakeAdb(
  emulators: FakeEmulator[],
  extraSerials: string[] = [],
): CommandRunner & { calls: string[][] } {
  const calls: string[][] = [];
  const shell = (emulator: FakeEmulator, args: string[]): CommandResult => {
    const command = args.join(" ");
    if (command === "getprop sys.boot_completed") return ok(`${emulator.bootCompleted ?? "1"}\n`);
    if (command === "getprop ro.build.version.release") return ok(`${emulator.release ?? "15"}\n`);
    if (command === "getprop ro.product.cpu.abi") return ok(`${emulator.abi ?? "arm64-v8a"}\n`);
    if (command === "getconf PAGE_SIZE") return ok(`${emulator.pageSize ?? "4096"}\n`);
    if (command === "settings get system system_locales")
      return ok(`${emulator.locales ?? "zh-Hans-CN"}\n`);
    if (command === "getprop persist.sys.timezone")
      return ok(`${emulator.timezone ?? "Asia/Shanghai"}\n`);
    if (args[0] === "dumpsys" && args[1] === "package" && args[2]) {
      const version = emulator.packages?.[args[2]];
      return ok(
        version === undefined
          ? `Unable to find package: ${args[2]}\n`
          : [
              "Activity Resolver Table:",
              "Packages:",
              `  Package [${args[2]}] (980f6c):`,
              "    versionCode=15070040 minSdk=21 targetSdk=35",
              `    versionName=${version}`,
              "",
            ].join("\n"),
      );
    }
    return failed;
  };
  return {
    calls,
    async run(_file, args) {
      calls.push([...args]);
      if (args.length === 1 && args[0] === "devices")
        return ok(
          [
            "List of devices attached",
            ...emulators.map((emulator) => `${emulator.serial}\t${emulator.state ?? "device"}`),
            ...extraSerials.map((serial) => `${serial}\tdevice`),
            "",
          ].join("\n"),
        );
      const emulator = emulators.find((item) => args[0] === "-s" && item.serial === args[1]);
      if (!emulator) return failed;
      if (args.slice(2).join(" ") === "emu avd name") return ok(`${emulator.avd}\r\nOK\r\n`);
      if (args[2] === "shell") return shell(emulator, args.slice(3));
      return failed;
    },
    start() {
      throw new Error("the inspector never starts a long-lived command");
    },
  };
}

// Private directory with the `aivc/` component the evidence root requires. macOS's tmpdir is
// reached through a symlink, so it is resolved first.
export async function evidenceDirectory(created: string[]): Promise<string> {
  const base = await mkdtemp(join(await realpath(tmpdir()), "app-channel-"));
  created.push(base);
  return join(base, "aivc", "evidence");
}

export const ANSWER_ID = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
export const SHA = "0".repeat(64);

// A complete valid record with synthetic content, as written with recording switched on. It has
// no `citationsStatus`, like the records written before that field existed.
export function recordFixture(overrides: Partial<AppRecord> = {}): AppRecord {
  return {
    answerId: ANSWER_ID,
    batchId: "batch-1",
    sampleClass: "local_trial",
    channel: "consumer_app",
    scriptRevision: "test-revision",
    questionText: "合成问题",
    sentText: "合成问题",
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
    startedAt: timestampPair(new Date("2026-11-15T04:00:00.000Z")),
    sentAt: timestampPair(new Date("2026-11-15T04:00:20.000Z")),
    completedAt: timestampPair(new Date("2026-11-15T04:01:20.000Z")),
    status: "valid",
    failureCode: null,
    phase: null,
    answerText: "合成回答",
    citations: [{ title: "合成来源", site: null, url: null }],
    searchState: { webSearch: "on", deepThinking: "unknown", searchQueries: ["合成关键词"] },
    video: {
      file: `${ANSWER_ID}.mp4`,
      sha256: SHA,
      bytes: 1024,
      durationMs: 95_000,
      startedAt: "2026-11-15T04:00:05.000Z",
      stoppedAt: "2026-11-15T04:01:35.000Z",
    },
    screenshot: { file: "final.png", sha256: SHA, bytes: 2048 },
    uiDump: { file: "ui.xml", sha256: SHA, bytes: 4096 },
    ...overrides,
  };
}

export const SYNTHETIC_VIDEO = "synthetic mp4 bytes";
export interface FakeRecorderOptions {
  // What the started `screenrecord` does. `runs`: it shows up in `pidof` and ends on SIGINT.
  // `never_appears`: the adb command stays up but no recorder process ever exists.
  // `exits_at_once`: the command ends right away (for example an unsupported option).
  // `ignores_interrupt`: it runs, but only SIGKILL ends it.
  process?: "runs" | "never_appears" | "exits_at_once" | "ignores_interrupt";
  // A recorder and a temp file left by an aborted run. `stops`: ends on SIGINT. `needs_kill`:
  // only SIGKILL ends it. `unkillable`: no signal ends it.
  leftover?: "stops" | "needs_kill" | "unkillable";
  // `adb pull` fails.
  pullFails?: boolean;
  // ffprobe answer; default: a readable 95 s video.
  ffprobe?: CommandResult;
}
export const ffprobeAnswer = (seconds: string, codecType = "video"): CommandResult => ({
  code: 0,
  stdout: JSON.stringify({ streams: [{ codec_type: codecType }], format: { duration: seconds } }),
  stderr: "",
});
const RECORDER_PROCESS = "screenrecord";
const DEVICE_VIDEO_PREFIX = "/sdcard/aivc-rec-";
const LEFTOVER_VIDEO = `${DEVICE_VIDEO_PREFIX}cccccccc-cccc-4ccc-8ccc-cccccccccccc.mp4`;
// Whether an adb command line (without the program) belongs to the recorder's guest protocol.
function isRecorderCommand(args: readonly string[]): boolean {
  if (args[0] !== "-s") return false;
  if (args[2] === "pull") return true;
  if (args[2] !== "shell") return false;
  if (args[3] === RECORDER_PROCESS) return true;
  if (args[3] === "pidof" || args[3] === "pkill") return args.at(-1) === RECORDER_PROCESS;
  return args[3] === "rm" && (args.at(-1)?.startsWith(DEVICE_VIDEO_PREFIX) ?? false);
}
// The device side of the recorder: the `screenrecord` process inside the emulator (seen through
// `pidof`, ended through `pkill`), its long-lived `adb shell screenrecord` command, the temp
// files on /sdcard, `adb pull`, and ffprobe on the pulled file. Nothing here touches a real
// device.
export function fakeRecorderDevice(options: FakeRecorderOptions = {}) {
  const calls: string[][] = [];
  // Every `adb shell screenrecord …` command line that was started.
  const started: string[][] = [];
  const files = new Set<string>(options.leftover ? [LEFTOVER_VIDEO] : []);
  let leftoverRunning = options.leftover !== undefined;
  // The recorder process of the last started command, and how its adb command ends.
  let running = false;
  let endCommand: (code: number | null) => void = () => undefined;
  const ok: CommandResult = { code: 0, stdout: "", stderr: "" };
  const failure: CommandResult = { code: 1, stdout: "", stderr: "" };
  const end = (code: number | null) => {
    running = false;
    endCommand(code);
  };
  const shell = (args: string[]): CommandResult => {
    if (args[0] === "pidof" && args[1] === RECORDER_PROCESS) {
      const pids = [...(leftoverRunning ? ["4000"] : []), ...(running ? ["4321"] : [])];
      return pids.length > 0 ? { ...ok, stdout: `${pids.join(" ")}\n` } : failure;
    }
    if (args[0] === "pkill" && args[2] === RECORDER_PROCESS) {
      if (!leftoverRunning && !running) return failure;
      const kill = args[1] === "-KILL";
      if (options.leftover === "stops" || (kill && options.leftover === "needs_kill"))
        leftoverRunning = false;
      // SIGINT lets the recorder finalize its file and exit 0.
      if (running && (kill || options.process !== "ignores_interrupt")) end(kill ? null : 0);
      return ok;
    }
    if (args[0] === "rm" && args[1] === "-f" && args[2]) {
      // The device shell expands the one pattern the recorder uses.
      const [prefix, suffix] = args[2].split("*");
      for (const file of [...files])
        if (suffix === undefined ? file === args[2] : file.startsWith(prefix!)) files.delete(file);
      return ok;
    }
    return failure;
  };
  const runner: CommandRunner = {
    async run(file, args) {
      calls.push([file, ...args]);
      if (file === "ffprobe") return options.ffprobe ?? ffprobeAnswer("95.000000");
      if (args[2] === "shell") return shell(args.slice(3));
      if (args[2] === "pull") {
        if (options.pullFails || !files.has(args[3]!)) return failure;
        // adb writes the file with the process umask; the recorder restricts it.
        await writeFile(args[4]!, SYNTHETIC_VIDEO, { mode: 0o644 });
        return ok;
      }
      return failure;
    },
    start(file, args) {
      if (!isRecorderCommand(args) || args[3] !== RECORDER_PROCESS)
        throw new Error("the recorder starts no long-lived command but screenrecord");
      calls.push([file, ...args]);
      started.push([file, ...args]);
      let resolve!: (result: { code: number | null }) => void;
      const exited = new Promise<{ code: number | null }>((done) => (resolve = done));
      endCommand = (code) => resolve({ code });
      if (options.process === "exits_at_once") end(1);
      else if (options.process !== "never_appears") {
        running = true;
        // Created at once, but empty for the first seconds.
        files.add(args.at(-1)!);
      }
      return { exited, kill: () => resolve({ code: null }) };
    },
  };
  return {
    runner,
    calls,
    started,
    // The recorder process ends on its own; what it wrote so far stays on the device.
    crash: () => {
      if (running) end(1);
    },
    // Device shell commands only, without `adb -s <serial> shell`.
    shellCalls: () =>
      calls.filter((call) => call[3] === "shell").map((call) => call.slice(4).join(" ")),
    // Recorder temp files currently on the device.
    deviceFiles: () => [...files].sort(),
  };
}

// Routes one command runner to several fakes: the inspector's adb answers plus one recorder fake
// per emulator serial. ffprobe goes to the fake of the device its file was pulled from.
export function combinedRunner(
  adb: CommandRunner,
  recorders: Record<string, ReturnType<typeof fakeRecorderDevice>>,
): CommandRunner {
  const pulledFrom = new Map<string, string>();
  const recorderFor = (args: readonly string[]) =>
    isRecorderCommand(args) ? recorders[args[1]!] : undefined;
  return {
    async run(file, args, options) {
      if (file === "ffprobe")
        return recorders[pulledFrom.get(args.at(-1)!) ?? ""]!.runner.run(file, args, options);
      const recorder = recorderFor(args);
      if (!recorder) return adb.run(file, args, options);
      if (args[2] === "pull") pulledFrom.set(args[4]!, args[1]!);
      return recorder.runner.run(file, args, options);
    },
    start(file, args, options) {
      return (recorderFor(args)?.runner ?? adb).start(file, args, options);
    },
  };
}

const START = "2026-11-15T04:00:00.000Z";

// Time that moves only when the flow sleeps. For one flow at a time.
export function steppingClock(start: string = START) {
  let time = Date.parse(start);
  const sleep: Sleep = async (ms) => {
    time += Math.max(0, ms);
  };
  return {
    now: () => new Date(time),
    sleep,
    advance: (ms: number) => void (time += ms),
  };
}

// Virtual time for concurrent loops: sleeps are queued and the clock jumps to the next one
// whenever everything is waiting.
export class VirtualClock {
  private time: number;
  private timers: { at: number; wake: () => void }[] = [];
  constructor(start: string = START) {
    this.time = Date.parse(start);
  }
  readonly now = () => new Date(this.time);
  readonly sleep: Sleep = (ms, signal) =>
    new Promise<void>((resolve) => {
      if (signal?.aborted) return resolve();
      const timer = { at: this.time + Math.max(0, ms), wake: resolve };
      this.timers.push(timer);
      signal?.addEventListener(
        "abort",
        () => {
          this.timers = this.timers.filter((item) => item !== timer);
          resolve();
        },
        { once: true },
      );
    });
  // Runs `work` to its end.
  async run<T>(work: Promise<T>): Promise<T> {
    let settled = false;
    work.then(
      () => void (settled = true),
      () => void (settled = true),
    );
    for (let idle = 0; !settled; ) {
      await new Promise((resolve) => setImmediate(resolve));
      if (settled) break;
      const next = this.timers.sort((a, b) => a.at - b.at).shift();
      if (!next) {
        if ((idle += 1) > 100_000) throw new Error("virtual clock: nothing left to wait for");
        continue;
      }
      idle = 0;
      this.time = Math.max(this.time, next.at);
      next.wake();
    }
    return work;
  }
}

// App screen contract shaped after the Doubao reference case (research/driver-spike.md §4) and
// matched by `MockChatApp`. It shows what the schema can express; it is not an operator-reviewed
// contract: the blocker and popup markers are invented and `reviewedAt` is a test value.
export function referenceContract(reviewedAt = "2026-11-14T04:00:00.000Z") {
  const id = (name: string) => ({ id: `com.larus.nova:id/${name}` });
  return {
    version: 1,
    platformCode: "doubao",
    package: "com.larus.nova",
    appVersions: ["15.7.0"],
    locale: "zh-Hans-CN",
    reviewedAt,
    loggedIn: {
      open: [{ tap: id("back_icon") }],
      marker: id("tv_username"),
      close: [{ back: true }],
    },
    blockers: {
      login_required: [id("mock_login_wall")],
      challenge_required: [id("mock_verification")],
      rate_limited: [{ textContains: "操作过于频繁" }],
      emulator_rejected: [{ textContains: "当前设备环境异常" }],
    },
    popups: [{ marker: id("tv_first_guide_title"), dismiss: id("tv_start_experience") }],
    newConversation: {
      steps: [{ tap: id("back_icon") }, { tap: { text: "新对话" } }],
      emptyMarker: id("larus_mode_switch_welcome_title"),
    },
    composer: {
      prepare: [{ tap: id("action_input"), unless: id("input_text") }],
      input: id("input_text"),
      send: id("action_send"),
    },
    completion: {
      reveal: [{ tap: id("fast_button_icon"), optional: true }],
      marker: id("msg_action_regenerate"),
    },
    answer: {
      method: "hierarchy",
      container: id("message_list"),
      question: id("mock_sent_text"),
      exclude: [
        id("tv_reference_title"),
        id("risk_warning_content"),
        id("time_text"),
        id("topic_text"),
      ],
      stitch: true,
    },
    citations: {
      entry: id("ll_reference_title"),
      panel: id("bottom_sheet_title"),
      itemTitle: id("tv_reference_content"),
      searchQueries: { after: id("bottom_sheet_title"), exclude: [id("tv_reference_index")] },
      close: id("bottom_sheet_close"),
    },
    searchState: { webSearchOn: id("ll_reference_title") },
  } as Record<string, unknown> & {
    appVersions: string[];
    locale: string;
    answer: Record<string, unknown>;
    citations: Record<string, unknown>;
    completion: Record<string, unknown>;
  };
}

// A contract for an App that draws its own screen (design §16.8, `MockIconApp`): its controls
// are image views without any label, and every text is a content description. Synthetic, like
// `referenceContract`; it keeps that fixture's platform and package so the same bindings apply.
export function iconAppContract(reviewedAt = "2026-11-14T04:00:00.000Z") {
  const icon = { className: "android.widget.ImageView", unlabeled: true, clickable: true };
  const id = (name: string) => ({ id: `com.larus.nova:id/${name}` });
  // The second half of the home screen's mode switch: two views side by side that take a tap.
  // By its words alone a line of an answer could pass for it, since those take a tap as well;
  // such a line stands alone in its row.
  const modeSwitch = {
    className: "android.view.View",
    descMatches: "对话|任务",
    clickable: true,
    row: { size: 2, at: 1 },
  };
  // A view with a label that only the App's own chat screen shows. A step that taps an icon by
  // its class and place first waits for it: such a selector fits a view of any screen.
  const chatScreen = { className: "android.widget.ImageView", descMatches: "合成模型.*" };
  const sourceEntry = { descMatches: "[0-9]+个来源" };
  // The first of the row of three icons below a finished answer.
  const copy = { ...icon, row: { size: 3, at: 0 }, pick: "last" };
  return {
    version: 1,
    platformCode: "doubao",
    package: "com.larus.nova",
    appVersions: ["15.7.0"],
    locale: "zh-Hans-CN",
    reviewedAt,
    loggedIn: {
      open: [{ waitFor: chatScreen }, { tap: { ...icon, pick: "first" } }],
      marker: id("tv_sidebar_name"),
      close: [{ tap: id("iv_sidebar_close") }],
    },
    blockers: {
      login_required: [],
      challenge_required: [],
      rate_limited: [],
      emulator_rejected: [],
    },
    popups: [],
    newConversation: {
      // The first of the two icons at the right of a conversation's top bar. The home screen
      // has no such icon, and there the first row of two is the composer's.
      steps: [
        { waitFor: chatScreen },
        {
          tap: { ...icon, row: { size: 2, at: 0 }, pick: "first" },
          optional: true,
          unless: modeSwitch,
        },
      ],
      emptyMarker: modeSwitch,
      emptyWhenAbsent: [sourceEntry, copy],
    },
    composer: {
      // The input takes a text only while it has the keyboard.
      prepare: [{ tap: { className: "android.widget.EditText" } }],
      input: { className: "android.widget.EditText" },
      // The one image view that neither has a label nor says that it takes a tap: the send
      // icon, which is there only once the App has the text. The icon in its place before
      // that switches to voice input, and says that it takes a tap.
      send: { className: "android.widget.ImageView", unlabeled: true, clickable: false },
    },
    completion: { reveal: [{ swipe: "up", count: 1 }], marker: copy },
    answer: { method: "copy", copy },
    citations: {
      entry: sourceEntry,
      panel: { desc: "纱罩" },
      cards: {
        from: "desc",
        layout: [
          { name: "title" },
          { name: "snippet", many: true, optional: true },
          { name: "url", pattern: "https?://\\S+" },
        ],
      },
      count: { from: "entry", pattern: "([0-9]+)个来源" },
      close: { ...icon, pick: "first" },
    },
    searchState: { webSearchOn: sourceEntry },
  } as Record<string, unknown> & {
    loggedIn: { open: Record<string, unknown>[] };
    newConversation: { steps: Record<string, unknown>[] };
    composer: Record<string, unknown>;
    citations: Record<string, unknown>;
    completion: Record<string, unknown>;
  };
}

// Private contract directory holding <platformCode>.json files with operator-only permissions.
export async function contractDirectory(
  created: string[],
  contracts: Record<string, unknown>,
): Promise<string> {
  const base = await mkdtemp(join(await realpath(tmpdir()), "app-contracts-"));
  created.push(base);
  const directory = join(base, "contracts");
  await mkdir(directory, { mode: 0o700 });
  for (const [platformCode, contract] of Object.entries(contracts))
    await writeFile(join(directory, `${platformCode}.json`), JSON.stringify(contract), {
      mode: 0o600,
    });
  return directory;
}
