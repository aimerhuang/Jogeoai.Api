import { chmod, lstat, statfs } from "node:fs/promises";
import { join } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { z } from "zod";
import type { CommandResult, CommandRunner, RunningCommand } from "./command.ts";
import { fileEvidence, videoCoversSpan, type VideoEvidence } from "./evidence.ts";
import { AppChannelError, AppSetupError } from "./failure.ts";

// Screen recording as evidence (PRD FR-A8, design §9), used only when recording is switched on
// (`APP_COLLECTOR_RECORD_SCREEN=on`; it is off by default, design §16.3). The emulator's own
// `screenrecord` is driven straight through adb, not through the automation driver, so a crashed
// driver session can still finish the video. One video belongs to one question and is named
// after its answer ID.
//
// Recording through the emulator console on the host (`adb emu screenrecord`) was tried on
// 2026-11-16 and removed: on its first real run it crashed the emulator process (SIGSEGV in the
// emulator's scaled frame read-back). It must not be reintroduced without proof that it cannot
// crash the emulator.

export type RecordingIssue =
  // The recorder had already ended when the stop was requested.
  "recorder_exited" | "stop_timeout" | "pull_failed" | "not_playable" | "too_short";
export interface RecordingOutcome {
  // false after a send means the record is `evidence_incomplete`.
  complete: boolean;
  issue: RecordingIssue | null;
  // Whatever file was pulled, also for an incomplete recording, so a failure keeps its video.
  video: VideoEvidence | null;
}
export interface ActiveRecording {
  readonly answerId: string;
  readonly startedAt: string;
  // false once the recorder process ended on its own. Check it right before sending: a
  // question is never sent without a running recording.
  isRecording(): boolean;
  // `sentAt` / `completedAt` (UTC ISO) are the span the video has to cover; leave them null when
  // nothing was sent.
  stop(span?: { sentAt: string | null; completedAt: string | null }): Promise<RecordingOutcome>;
}

export interface ScreenRecorderOptions {
  runner: CommandRunner;
  adbPath?: string;
  ffprobePath?: string;
  // Defaults measured in stage 0: about 8 MB for 200 s of real content.
  size?: string;
  bitRate?: number;
  now?: () => Date;
  sleep?: (ms: number) => Promise<void>;
  startConfirmTimeoutMs?: number;
  stopTimeoutMs?: number;
  // Pause between the recorder's exit and the pull.
  settleMs?: number;
}

const PROCESS = "screenrecord";
const DEVICE_DIRECTORY = "/sdcard";
const DEVICE_PREFIX = "aivc-rec-";
const POLL_MS = 250;

const ffprobeSchema = z.object({
  streams: z.array(z.object({ codec_type: z.string() })).min(1),
  format: z.object({ duration: z.string() }),
});

export class ScreenRecorder {
  private readonly runner: CommandRunner;
  private readonly adbPath: string;
  private readonly ffprobePath: string;
  private readonly size: string;
  private readonly bitRate: number;
  private readonly now: () => Date;
  private readonly sleep: (ms: number) => Promise<void>;
  private readonly startConfirmTimeoutMs: number;
  private readonly stopTimeoutMs: number;
  private readonly settleMs: number;

  constructor(options: ScreenRecorderOptions) {
    this.runner = options.runner;
    this.adbPath = options.adbPath ?? "adb";
    this.ffprobePath = options.ffprobePath ?? "ffprobe";
    this.size = options.size ?? "720x1600";
    this.bitRate = options.bitRate ?? 2_000_000;
    this.now = options.now ?? (() => new Date());
    this.sleep = options.sleep ?? ((ms) => delay(ms));
    this.startConfirmTimeoutMs = options.startConfirmTimeoutMs ?? 5_000;
    this.stopTimeoutMs = options.stopTimeoutMs ?? 15_000;
    this.settleMs = options.settleMs ?? 1_000;
  }

  // Starts recording the device screen (no audio) into `directory/<answerId>.mp4`. `serial`
  // comes from `DeviceInspector`, `directory` from `EvidenceStore.recordDirectory`. Throws
  // `recording_failed` (pre_send) when the recorder is not confirmed running: the caller then
  // sends nothing and writes no ledger line.
  async start(target: {
    serial: string;
    answerId: string;
    directory: string;
  }): Promise<ActiveRecording> {
    const { serial, answerId } = target;
    const remote = `${DEVICE_DIRECTORY}/${DEVICE_PREFIX}${z.uuid().parse(answerId)}.mp4`;
    const local = join(target.directory, `${answerId}.mp4`);

    await this.clearLeftovers(serial);
    const startedAt = this.now().toISOString();
    const child = this.runner.start(this.adbPath, [
      "-s",
      serial,
      "shell",
      PROCESS,
      "--time-limit",
      "0",
      "--size",
      this.size,
      "--bit-rate",
      String(this.bitRate),
      remote,
    ]);
    let exited = false;
    void child.exited.then(() => {
      exited = true;
    });

    // Process existence is the only start signal: the output file stays at 0 bytes for seconds.
    if (!(await this.confirmStarted(serial, () => exited))) {
      await this.signal(serial, "-INT");
      child.kill("SIGKILL");
      await this.shell(serial, ["rm", "-f", remote]);
      throw new AppChannelError("recording_failed", "pre_send");
    }

    let outcome: Promise<RecordingOutcome> | undefined;
    return {
      answerId,
      startedAt,
      isRecording: () => !exited,
      stop: (span) =>
        (outcome ??= this.finish(
          { serial, answerId, remote, local, startedAt, child },
          span,
          () => exited,
        )),
    };
  }

  private async confirmStarted(serial: string, exited: () => boolean): Promise<boolean> {
    for (let waited = 0; waited <= this.startConfirmTimeoutMs; waited += POLL_MS) {
      if (exited()) return false;
      if ((await this.recorderPids(serial)).length > 0) return !exited();
      await this.sleep(POLL_MS);
    }
    return false;
  }

  private async finish(
    recording: {
      serial: string;
      answerId: string;
      remote: string;
      local: string;
      startedAt: string;
      child: RunningCommand;
    },
    span: { sentAt: string | null; completedAt: string | null } | undefined,
    exited: () => boolean,
  ): Promise<RecordingOutcome> {
    const { serial, remote, local, child } = recording;
    let issue: RecordingIssue | null = null;
    if (exited()) issue = "recorder_exited";
    else {
      // SIGINT inside the device lets screenrecord finalize the MP4 before it exits.
      await this.signal(serial, "-INT");
      const stopped = await Promise.race([
        child.exited.then(() => true),
        this.sleep(this.stopTimeoutMs).then(() => false),
      ]);
      if (!stopped) {
        issue = "stop_timeout";
        await this.signal(serial, "-KILL");
        child.kill("SIGKILL");
      }
    }
    const stoppedAt = this.now().toISOString();
    await this.sleep(this.settleMs);

    const pulled = await this.adb(["-s", serial, "pull", remote, local], 120_000);
    let isFile = false;
    if (pulled.code === 0) {
      try {
        isFile = (await lstat(local)).isFile();
      } catch {
        isFile = false;
      }
    }
    if (!isFile) return { complete: false, issue: issue ?? "pull_failed", video: null };
    await chmod(local, 0o600);
    await this.shell(serial, ["rm", "-f", remote]);

    const durationMs = await this.probeDuration(local);
    const video: VideoEvidence = {
      ...(await fileEvidence(local)),
      durationMs,
      startedAt: recording.startedAt,
      stoppedAt,
    };
    if (issue === null) {
      if (durationMs === null) issue = "not_playable";
      else if (!videoCoversSpan(durationMs, span?.sentAt ?? null, span?.completedAt ?? null))
        issue = "too_short";
    }
    return { complete: issue === null, issue, video };
  }

  // Duration of a readable video in milliseconds; null when ffprobe cannot read the file as one.
  private async probeDuration(path: string): Promise<number | null> {
    const result = await this.runner.run(
      this.ffprobePath,
      [
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
      { timeoutMs: 30_000 },
    );
    if (result.code !== 0) return null;
    try {
      const probe = ffprobeSchema.parse(JSON.parse(result.stdout));
      const seconds = Number(probe.format.duration);
      if (probe.streams[0]!.codec_type !== "video" || !Number.isFinite(seconds) || seconds <= 0)
        return null;
      return Math.round(seconds * 1_000);
    } catch {
      return null;
    }
  }

  // A recorder or temp file left by an aborted run would make "is it recording" lie and would
  // let two recorders run on one device.
  private async clearLeftovers(serial: string): Promise<void> {
    if ((await this.recorderPids(serial)).length > 0) {
      await this.signal(serial, "-INT");
      if (!(await this.waitUntilGone(serial))) {
        await this.signal(serial, "-KILL");
        if (!(await this.waitUntilGone(serial)))
          throw new AppChannelError("recording_failed", "pre_send");
      }
    }
    // The device shell expands the pattern; no host shell is involved.
    await this.shell(serial, ["rm", "-f", `${DEVICE_DIRECTORY}/${DEVICE_PREFIX}*.mp4`]);
  }
  private async waitUntilGone(serial: string): Promise<boolean> {
    for (let waited = 0; waited <= this.stopTimeoutMs; waited += POLL_MS) {
      if ((await this.recorderPids(serial)).length === 0) return true;
      await this.sleep(POLL_MS);
    }
    return false;
  }

  private async recorderPids(serial: string): Promise<string[]> {
    const result = await this.shell(serial, ["pidof", PROCESS]);
    return result.stdout.split(/\s+/u).filter((pid) => /^\d+$/u.test(pid));
  }
  private signal(serial: string, signal: "-INT" | "-KILL"): Promise<CommandResult> {
    return this.shell(serial, ["pkill", signal, PROCESS]);
  }
  private shell(serial: string, args: string[]): Promise<CommandResult> {
    return this.adb(["-s", serial, "shell", ...args]);
  }
  private adb(args: string[], timeoutMs = 15_000): Promise<CommandResult> {
    return this.runner.run(this.adbPath, args, { timeoutMs });
  }
}

// Recording switched off (design §16.3, the default): the flow runs as with a recorder, but no
// adb command is issued, nothing can stop a send, and the record has no video.
export function disabledScreenRecorder(
  now: () => Date = () => new Date(),
): Pick<ScreenRecorder, "start"> {
  return {
    start: async ({ answerId }) => ({
      answerId,
      startedAt: now().toISOString(),
      isRecording: () => true,
      stop: async () => ({ complete: true, issue: null, video: null }),
    }),
  };
}

export const DEFAULT_MIN_FREE_BYTES = 10 * 1024 ** 3;

// Run before a batch starts: no batch begins when the evidence volume is nearly full.
export async function assertDiskSpace(
  directory: string,
  minFreeBytes: number = DEFAULT_MIN_FREE_BYTES,
  stat: (path: string) => Promise<{ bavail: number; bsize: number }> = statfs,
): Promise<number> {
  let free: number;
  try {
    const stats = await stat(directory);
    free = stats.bavail * stats.bsize;
  } catch {
    throw new AppSetupError("evidence_unavailable");
  }
  if (free < minFreeBytes) throw new AppSetupError("disk_space_low");
  return free;
}
