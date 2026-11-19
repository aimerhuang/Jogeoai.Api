import { createHash } from "node:crypto";
import { lstat, readFile, readdir, rm } from "node:fs/promises";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { parseBindings, resolveBindings } from "./bindings.ts";
import type { CommandResult, CommandRunner } from "./command.ts";
import { EvidenceStore, appRecordSchema } from "./evidence.ts";
import { RateGate } from "./rate-gate.ts";
import { ScreenRecorder, assertDiskSpace, disabledScreenRecorder } from "./recorder.ts";
import {
  ANSWER_ID,
  bindingsFixture,
  evidenceDirectory,
  fakeRecorderDevice as fakeDevice,
  ffprobeAnswer as probe,
  recordFixture,
  SYNTHETIC_VIDEO,
  type FakeRecorderOptions as FakeDeviceOptions,
} from "./test-fixtures.ts";

const SERIAL = "emulator-5556";
const VIDEO = SYNTHETIC_VIDEO;
const MP4 = `${ANSWER_ID}.mp4`;
const REMOTE = `/sdcard/aivc-rec-${ANSWER_ID}.mp4`;
const SHELL = ["adb", "-s", SERIAL, "shell"];
const PIDOF = "pidof screenrecord";
const INTERRUPT = "pkill -INT screenrecord";
const KILL = "pkill -KILL screenrecord";
const CLEAR = "rm -f /sdcard/aivc-rec-*.mp4";
const RECORD = `screenrecord --time-limit 0 --size 720x1600 --bit-rate 2000000 ${REMOTE}`;
const SPAN = { sentAt: "2026-11-15T04:00:20.000Z", completedAt: "2026-11-15T04:01:20.000Z" };

const created: string[] = [];
const stores: EvidenceStore[] = [];
afterEach(async () => {
  for (const store of stores.splice(0)) await store.close().catch(() => undefined);
  for (const directory of created.splice(0)) await rm(directory, { recursive: true, force: true });
});
async function evidence() {
  const store = await EvidenceStore.open(await evidenceDirectory(created));
  stores.push(store);
  return { store, directory: await store.recordDirectory("batch-1", ANSWER_ID) };
}

// Real waits are shortened; their order is kept.
const shortSleep = (ms: number) =>
  new Promise<void>((resolve) => setTimeout(resolve, Math.min(ms, 2)));
function recorderFor(runner: CommandRunner) {
  let tick = Date.parse("2026-11-15T04:00:05.000Z");
  return new ScreenRecorder({
    runner,
    now: () => new Date((tick += 1_000)),
    sleep: shortSleep,
  });
}
const startOn = (device: ReturnType<typeof fakeDevice>, directory: string) =>
  recorderFor(device.runner).start({ serial: SERIAL, answerId: ANSWER_ID, directory });

describe("screen recorder", () => {
  // AC-A13: the video is named after the answer ID and the recorded hash matches the file.
  it("records inside the device and leaves <answerId>.mp4 with its hash, size and duration", async () => {
    const { directory } = await evidence();
    const device = fakeDevice();
    const recording = await startOn(device, directory);
    const path = join(directory, MP4);
    // Leftovers are looked for, the recorder is started, and its process confirms the start.
    expect(device.calls).toEqual([
      [...SHELL, "pidof", "screenrecord"],
      [...SHELL, "rm", "-f", "/sdcard/aivc-rec-*.mp4"],
      [
        ...SHELL,
        "screenrecord",
        "--time-limit",
        "0",
        "--size",
        "720x1600",
        "--bit-rate",
        "2000000",
        REMOTE,
      ],
      [...SHELL, "pidof", "screenrecord"],
    ]);
    expect(device.started).toEqual([device.calls[2]]);
    expect(recording.answerId).toBe(ANSWER_ID);
    expect(recording.isRecording()).toBe(true);
    // The file was never looked at: it stays empty for seconds, so its growth is no start signal.
    expect(device.deviceFiles()).toEqual([REMOTE]);
    expect(await readdir(directory)).toEqual([]);

    const outcome = await recording.stop(SPAN);
    expect(await readdir(directory)).toEqual([MP4]);
    expect(outcome).toEqual({
      complete: true,
      issue: null,
      video: {
        file: MP4,
        sha256: createHash("sha256")
          .update(await readFile(path))
          .digest("hex"),
        bytes: VIDEO.length,
        durationMs: 95_000,
        startedAt: recording.startedAt,
        stoppedAt: outcome.video!.stoppedAt,
      },
    });
    expect(Date.parse(outcome.video!.stoppedAt)).toBeGreaterThan(Date.parse(recording.startedAt));
    expect((await lstat(path)).mode & 0o777).toBe(0o600);
    expect(recording.isRecording()).toBe(false);
    // SIGINT inside the device, then the pull, the removal of the device's copy and the probe.
    expect(device.calls.slice(4)).toEqual([
      [...SHELL, "pkill", "-INT", "screenrecord"],
      ["adb", "-s", SERIAL, "pull", REMOTE, path],
      [...SHELL, "rm", "-f", REMOTE],
      [
        "ffprobe",
        "-v",
        "error",
        "-select_streams",
        "v:0",
        "-show_entries",
        "stream=codec_type:format=duration",
        "-of",
        "json",
        path,
      ],
    ]);
    expect(device.deviceFiles()).toEqual([]);
    // The video fits a valid record.
    expect(appRecordSchema.safeParse(recordFixture({ video: outcome.video })).success).toBe(true);
  });
  it("runs a stop only once, however often it is asked for", async () => {
    const { directory } = await evidence();
    const device = fakeDevice();
    const recording = await startOn(device, directory);
    const [first, second] = await Promise.all([recording.stop(SPAN), recording.stop()]);
    expect(second).toBe(first);
    const commands = device.calls.length;
    expect(await recording.stop()).toBe(first);
    expect(device.calls).toHaveLength(commands);
    expect(device.shellCalls().filter((command) => command === INTERRUPT)).toHaveLength(1);
    expect(device.calls.filter((call) => call[3] === "pull")).toHaveLength(1);
  });
  it("uses the configured programs, size and bit rate", async () => {
    const { directory } = await evidence();
    const device = fakeDevice();
    const calls: string[][] = [];
    const runner: CommandRunner = {
      // The fake knows the programs by their default names.
      run: (file, args, options) => {
        calls.push([file, ...args]);
        return device.runner.run(file.split("/").at(-1)!, args, options);
      },
      start: (file, args, options) => {
        calls.push([file, ...args]);
        return device.runner.start(file.split("/").at(-1)!, args, options);
      },
    };
    const recorder = new ScreenRecorder({
      runner,
      adbPath: "/opt/android/adb",
      ffprobePath: "/opt/tools/bin/ffprobe",
      size: "540x1200",
      bitRate: 1_000_000,
      sleep: async () => undefined,
    });
    const recording = await recorder.start({ serial: SERIAL, answerId: ANSWER_ID, directory });
    expect(device.started[0]!.slice(5)).toEqual([
      "--time-limit",
      "0",
      "--size",
      "540x1200",
      "--bit-rate",
      "1000000",
      REMOTE,
    ]);
    expect((await recording.stop()).complete).toBe(true);
    expect([...new Set(calls.map((call) => call[0]))]).toEqual([
      "/opt/android/adb",
      "/opt/tools/bin/ffprobe",
    ]);
  });

  // AC-A14: without a confirmed recording nothing is sent and no quota is used.
  it.each([
    ["the recorder process never appears", { process: "never_appears" }],
    ["the recorder process exits at once", { process: "exits_at_once" }],
  ] satisfies [string, FakeDeviceOptions][])(
    "fails as recording_failed before the send when %s, leaving ledger and limits untouched",
    async (_name, options) => {
      const { store, directory } = await evidence();
      const device = fakeDevice(options);
      const kills: (NodeJS.Signals | undefined)[] = [];
      const runner: CommandRunner = {
        run: device.runner.run,
        start: (file, args, startOptions) => {
          const child = device.runner.start(file, args, startOptions);
          return {
            exited: child.exited,
            kill: (signal) => {
              kills.push(signal);
              child.kill(signal);
            },
          };
        },
      };
      await expect(
        recorderFor(runner).start({ serial: SERIAL, answerId: ANSWER_ID, directory }),
      ).rejects.toMatchObject({ code: "recording_failed", phase: "pre_send" });

      expect(await store.readLedger()).toEqual([]);
      const [binding] = resolveBindings(parseBindings(bindingsFixture()));
      const gate = new RateGate({
        ledger: await store.readLedger(),
        bindingStatus: await store.readBindingStatus(),
      });
      expect(gate.counts(binding!)).toEqual({
        bindingLastHour: 0,
        bindingLast24h: 0,
        platformToday: 0,
      });
      expect(gate.decide(binding!)).toEqual({ allowed: true });
      // Nothing is left recording or lying around: the one recorder that was started is
      // interrupted, its adb command killed and its temp file removed. No second start, no pull.
      expect(device.started).toHaveLength(1);
      expect(device.shellCalls().slice(-2)).toEqual([INTERRUPT, `rm -f ${REMOTE}`]);
      expect(kills).toEqual(["SIGKILL"]);
      expect(device.calls.some((call) => call[3] === "pull" || call[0] === "ffprobe")).toBe(false);
      expect(device.deviceFiles()).toEqual([]);
      expect(await readdir(directory)).toEqual([]);
      // The failure is a pre-send record without a video.
      expect(
        appRecordSchema.safeParse(
          recordFixture({
            status: "failed",
            failureCode: "recording_failed",
            phase: "pre_send",
            sentAt: null,
            completedAt: null,
            answerText: null,
            citations: [],
            searchState: null,
            video: null,
            screenshot: null,
            uiDump: null,
          }),
        ).success,
      ).toBe(true);
    },
  );

  it("ends a recorder and removes temp files left by an aborted run before starting its own", async () => {
    const { directory } = await evidence();
    const device = fakeDevice({ leftover: "stops" });
    expect(device.deviceFiles()).toHaveLength(1);
    const recording = await startOn(device, directory);
    expect(device.shellCalls()).toEqual([PIDOF, INTERRUPT, PIDOF, CLEAR, RECORD, PIDOF]);
    // Only this recording's file is on the device now.
    expect(device.deviceFiles()).toEqual([REMOTE]);
    expect(recording.isRecording()).toBe(true);
    expect(await recording.stop(SPAN)).toMatchObject({ complete: true, issue: null });
  });
  it("kills a leftover recorder that ignores the interrupt", async () => {
    const { directory } = await evidence();
    const device = fakeDevice({ leftover: "needs_kill" });
    const recorder = new ScreenRecorder({ runner: device.runner, sleep: async () => undefined });
    const recording = await recorder.start({ serial: SERIAL, answerId: ANSWER_ID, directory });
    const commands = device.shellCalls();
    expect(commands.filter((command) => command !== PIDOF)).toEqual([
      INTERRUPT,
      KILL,
      CLEAR,
      RECORD,
    ]);
    expect(device.deviceFiles()).toEqual([REMOTE]);
    expect((await recording.stop()).complete).toBe(true);
  });
  it("starts no second recorder next to a leftover one that cannot be ended", async () => {
    const { directory } = await evidence();
    const device = fakeDevice({ leftover: "unkillable" });
    const recorder = new ScreenRecorder({ runner: device.runner, sleep: async () => undefined });
    await expect(
      recorder.start({ serial: SERIAL, answerId: ANSWER_ID, directory }),
    ).rejects.toMatchObject({ code: "recording_failed", phase: "pre_send" });
    expect(device.started).toEqual([]);
    expect(await readdir(directory)).toEqual([]);
  });

  // AC-A14: an interruption after the send makes the record evidence_incomplete.
  it("reports a recorder that had already exited as incomplete and still keeps the video", async () => {
    const { store, directory } = await evidence();
    const device = fakeDevice();
    const recording = await startOn(device, directory);
    device.crash();
    await Promise.resolve();
    expect(recording.isRecording()).toBe(false);

    const outcome = await recording.stop(SPAN);
    expect(outcome).toMatchObject({
      complete: false,
      issue: "recorder_exited",
      video: { file: MP4, durationMs: 95_000 },
    });
    expect(await readdir(directory)).toEqual([MP4]);
    // There was no process left to signal.
    expect(device.shellCalls()).not.toContain(INTERRUPT);
    expect(device.shellCalls()).not.toContain(KILL);

    const record = recordFixture({
      status: "evidence_incomplete",
      phase: "post_send",
      video: outcome.video,
      evidenceIssue: outcome.issue!,
    });
    await store.writeRecord(record);
    const stored = await store.readRecords("batch-1");
    expect(stored).toEqual([record]);
    // Not a valid sample, so the batch has nothing that covers this question yet.
    expect(stored.filter((item) => item.status === "valid")).toEqual([]);
  });
  it("kills a recorder that ignores the interrupt and reports the stop as timed out", async () => {
    const { directory } = await evidence();
    const device = fakeDevice({ process: "ignores_interrupt" });
    const recording = await startOn(device, directory);
    expect(await recording.stop(SPAN)).toMatchObject({
      complete: false,
      issue: "stop_timeout",
      video: { file: MP4 },
    });
    const commands = device.shellCalls();
    expect(commands.indexOf(INTERRUPT)).toBeGreaterThan(-1);
    expect(commands.indexOf(KILL)).toBeGreaterThan(commands.indexOf(INTERRUPT));
    expect(recording.isRecording()).toBe(false);
  });
  it("reports a failed pull as incomplete without a video", async () => {
    const { directory } = await evidence();
    const device = fakeDevice({ pullFails: true });
    const recording = await startOn(device, directory);
    expect(await recording.stop(SPAN)).toEqual({
      complete: false,
      issue: "pull_failed",
      video: null,
    });
    expect(await readdir(directory)).toEqual([]);
    expect(device.calls.some((call) => call[0] === "ffprobe")).toBe(false);
  });
  it("reports a pull that left no file as incomplete without a video", async () => {
    const { directory } = await evidence();
    const device = fakeDevice();
    const runner: CommandRunner = {
      // adb reports success but nothing arrives.
      run: async (file, args, options) =>
        args[2] === "pull"
          ? { code: 0, stdout: "", stderr: "" }
          : device.runner.run(file, args, options),
      start: device.runner.start,
    };
    const recording = await recorderFor(runner).start({
      serial: SERIAL,
      answerId: ANSWER_ID,
      directory,
    });
    expect(await recording.stop(SPAN)).toEqual({
      complete: false,
      issue: "pull_failed",
      video: null,
    });
  });
  it("keeps the earlier reason when the pull fails after the recorder had exited", async () => {
    const { directory } = await evidence();
    const device = fakeDevice({ pullFails: true });
    const recording = await startOn(device, directory);
    device.crash();
    await Promise.resolve();
    expect(await recording.stop(SPAN)).toEqual({
      complete: false,
      issue: "recorder_exited",
      video: null,
    });
  });
  it("reports a video shorter than the send-to-completion span as incomplete", async () => {
    const { directory } = await evidence();
    const device = fakeDevice({ ffprobe: probe("10.000000") });
    const recording = await startOn(device, directory);
    expect(await recording.stop(SPAN)).toMatchObject({
      complete: false,
      issue: "too_short",
      video: { durationMs: 10_000 },
    });
  });
  it("does not compare the duration with wall-clock time, frame count or file size", async () => {
    const { directory } = await evidence();
    // The guest clock drifts: a 19-byte file whose video runs 6 % longer than the 200 s it
    // covers, and far longer than the few (fake) seconds between start and stop.
    const device = fakeDevice({ ffprobe: probe("213.500000") });
    const recording = await startOn(device, directory);
    const outcome = await recording.stop({
      sentAt: "2026-11-15T04:00:10.000Z",
      completedAt: "2026-11-15T04:03:30.000Z",
    });
    expect(outcome).toMatchObject({ complete: true, video: { bytes: 19, durationMs: 213_500 } });
    expect(
      Date.parse(outcome.video!.stoppedAt) - Date.parse(outcome.video!.startedAt),
    ).toBeLessThan(213_500);
    expect(device.calls.find((call) => call[0] === "ffprobe")?.join(" ")).not.toMatch(
      /nb_frames|size/u,
    );
  });
  it("accepts any playable video when nothing was sent", async () => {
    const { directory } = await evidence();
    const device = fakeDevice({ ffprobe: probe("3.200000") });
    const recording = await startOn(device, directory);
    expect(await recording.stop()).toMatchObject({ complete: true, video: { durationMs: 3_200 } });
  });
  it.each([
    ["ffprobe rejects the file", { code: 1, stdout: "{}", stderr: "moov atom not found" }],
    ["ffprobe prints no stream", { code: 0, stdout: '{"streams":[],"format":{}}', stderr: "" }],
    ["the stream is not a video", probe("95.000000", "audio")],
    ["the duration is zero", probe("0.000000")],
    ["the duration is not a number", probe("N/A")],
    ["the output is not JSON", { code: 0, stdout: "N/A", stderr: "" }],
  ] satisfies [string, CommandResult][])(
    "reports an unreadable video as incomplete when %s, keeping the file and its hash",
    async (_name, ffprobe) => {
      const { directory } = await evidence();
      const device = fakeDevice({ ffprobe });
      const recording = await startOn(device, directory);
      const outcome = await recording.stop({ sentAt: null, completedAt: null });
      expect(outcome).toMatchObject({
        complete: false,
        issue: "not_playable",
        video: { file: MP4, durationMs: null, bytes: VIDEO.length },
      });
      expect(outcome.video!.sha256).toBe(
        createHash("sha256")
          .update(await readFile(join(directory, MP4)))
          .digest("hex"),
      );
    },
  );
  it("rejects an answer ID that is not a UUID before touching the device", async () => {
    const { directory } = await evidence();
    const device = fakeDevice();
    await expect(
      recorderFor(device.runner).start({ serial: SERIAL, answerId: "../../etc", directory }),
    ).rejects.toThrow();
    expect(device.calls).toEqual([]);
  });
});

// AC-A17 / design §16.3: with recording switched off nothing is run and nothing is recorded.
describe("recording switched off", () => {
  it("starts no command, never stops a send and gives a complete outcome without a video", async () => {
    const { directory } = await evidence();
    const now = () => new Date("2026-11-15T04:00:05.000Z");
    const recording = await disabledScreenRecorder(now).start({
      serial: SERIAL,
      answerId: ANSWER_ID,
      directory,
    });
    expect(recording.answerId).toBe(ANSWER_ID);
    expect(recording.startedAt).toBe("2026-11-15T04:00:05.000Z");
    expect(recording.isRecording()).toBe(true);
    expect(await recording.stop(SPAN)).toEqual({ complete: true, issue: null, video: null });
    expect(recording.isRecording()).toBe(true);
    expect(await recording.stop()).toEqual({ complete: true, issue: null, video: null });
    expect(await readdir(directory)).toEqual([]);
  });
});

describe("free disk space check", () => {
  const volume = (freeBytes: number) => async () => ({ bavail: freeBytes / 4096, bsize: 4096 });
  it("refuses to start below the floor and reports the free space otherwise", async () => {
    const floor = 10 * 1024 ** 3;
    await expect(
      assertDiskSpace("/aivc/evidence", floor, volume(floor - 4096)),
    ).rejects.toMatchObject({ code: "disk_space_low" });
    expect(await assertDiskSpace("/aivc/evidence", floor, volume(floor))).toBe(floor);
    // Default floor: 10 GB.
    await expect(
      assertDiskSpace("/aivc/evidence", undefined, volume(9 * 1024 ** 3)),
    ).rejects.toMatchObject({ code: "disk_space_low" });
    expect(await assertDiskSpace("/aivc/evidence", 1024 ** 3, volume(9 * 1024 ** 3))).toBe(
      9 * 1024 ** 3,
    );
  });
  it("reads the real volume of an existing directory", async () => {
    const { store } = await evidence();
    expect(await assertDiskSpace(store.root, 1)).toBeGreaterThan(0);
    await expect(assertDiskSpace(join(store.root, "missing"), 1)).rejects.toMatchObject({
      code: "evidence_unavailable",
    });
  });
});
