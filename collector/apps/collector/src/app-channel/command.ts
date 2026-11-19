import { execFile, spawn } from "node:child_process";
import { allowlistedEnvironment } from "../session-profile.ts";

// `code` is null when the command could not be started, timed out or died from a signal.
export interface CommandResult {
  code: number | null;
  stdout: string;
  stderr: string;
}
export interface RunningCommand {
  readonly exited: Promise<{ code: number | null }>;
  kill(signal?: NodeJS.Signals): void;
}
// The only way this slice starts external programs (adb, ffprobe, Appium). Injected everywhere
// so tests never need a device. Output may contain device or app text: callers parse it, never
// log it.
export interface CommandRunner {
  run(
    file: string,
    args: readonly string[],
    options?: { timeoutMs?: number },
  ): Promise<CommandResult>;
  // Long-lived command (the device's screen recorder behind `adb shell`, the local Appium
  // server); output is discarded. `env` adds named variables to the allowlisted environment,
  // nothing else.
  start(
    file: string,
    args: readonly string[],
    options?: { env?: Readonly<Record<string, string>> },
  ): RunningCommand;
}

const DEFAULT_TIMEOUT_MS = 15_000;
// `dumpsys package` prints a few hundred kilobytes for a large app.
const MAX_OUTPUT_BYTES = 32 * 1024 * 1024;

// No shell, and the same allowlisted environment as Chromium and the notifier: tokens, keys and
// proxy settings a shell may have sourced never reach adb or ffprobe.
export const systemCommandRunner: CommandRunner = {
  run(file, args, options) {
    return new Promise((resolve) => {
      try {
        execFile(
          file,
          [...args],
          {
            env: allowlistedEnvironment(),
            timeout: options?.timeoutMs ?? DEFAULT_TIMEOUT_MS,
            maxBuffer: MAX_OUTPUT_BYTES,
            windowsHide: true,
            encoding: "utf8",
          },
          (error, stdout, stderr) => {
            const code = error ? (typeof error.code === "number" ? error.code : null) : 0;
            resolve({ code, stdout, stderr });
          },
        );
      } catch {
        resolve({ code: null, stdout: "", stderr: "" });
      }
    });
  },
  start(file, args, options) {
    // Own process group, like the pool's children: a terminal Ctrl+C reaches only this script,
    // which then ends its attempts in order and stops the command itself.
    const child = spawn(file, [...args], {
      env: { ...allowlistedEnvironment(), ...options?.env },
      stdio: "ignore",
      detached: true,
    });
    const exited = new Promise<{ code: number | null }>((resolve) => {
      child.once("error", () => resolve({ code: null }));
      child.once("exit", (code) => resolve({ code }));
    });
    return {
      exited,
      kill(signal) {
        if (child.exitCode === null && child.signalCode === null) child.kill(signal);
      },
    };
  },
};
