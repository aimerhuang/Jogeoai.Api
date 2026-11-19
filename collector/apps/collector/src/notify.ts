import { execFile } from "node:child_process";
import type { CollectorFailureCode, LeasedTask } from "@aivc/contracts";
import { allowlistedEnvironment } from "./session-profile.ts";

export type OperatorActionReason =
  | "challenge_wait_started"
  // The account-pool supervisor stopped restarting a profile whose collector exited with 3.
  | "pool_entry_stopped"
  | Extract<CollectorFailureCode, "challenge_required" | "login_required">;
// Only identifiers and enum values. Question text, answers and page content never enter a
// notification, its log line or the command payload.
export interface OperatorNotice {
  platform: LeasedTask["platformCode"];
  reason: OperatorActionReason;
  // Absent for supervisor notices, which are not tied to one collection phase.
  phase?: "probe" | "pre_send" | "post_send";
  sessionProfileId?: string;
  taskId?: string;
}
export type Notifier = (notice: OperatorNotice) => Promise<void>;
export type NotifyLog = (metadata: {
  event: string;
  platform?: string;
  reason?: string;
  phase?: string;
  sessionProfileId?: string;
  taskId?: string;
  channel?: string;
}) => void;

export interface NotifierOptions {
  log: NotifyLog;
  // COLLECTOR_NOTIFY_COMMAND: absolute executable path, run without a shell.
  command?: string;
  platform?: NodeJS.Platform;
  execFile?: typeof execFile;
  timeoutMs?: number;
}

const SAFE_TOKEN = /^[a-z_]{1,40}$/u;

// Every channel is best-effort: the returned promise never rejects, so a broken notifier can
// never fail or delay collection beyond its own timeout.
export function createNotifier(options: NotifierOptions): Notifier {
  const run = options.execFile ?? execFile;
  const os = options.platform ?? process.platform;
  const timeout = options.timeoutMs ?? 10_000;
  const exec = (
    channel: string,
    file: string,
    args: string[],
    stdin: string | undefined,
    notice: OperatorNotice,
  ) =>
    new Promise<void>((resolve) => {
      const failed = () =>
        options.log({ event: "operator_notification_failed", channel, reason: notice.reason });
      try {
        const child = run(
          file,
          args,
          {
            env: allowlistedEnvironment(),
            timeout,
            windowsHide: true,
            maxBuffer: 64 * 1024,
          },
          (error) => {
            // Never log command output or error text; it is outside our control.
            if (error) failed();
            resolve();
          },
        );
        child.stdin?.on("error", () => undefined);
        child.stdin?.end(stdin ?? "");
      } catch {
        failed();
        resolve();
      }
    });
  return async (notice) => {
    const payload = {
      event: "operator_action_required",
      platform: notice.platform,
      reason: notice.reason,
      ...(notice.phase ? { phase: notice.phase } : {}),
      ...(notice.sessionProfileId ? { sessionProfileId: notice.sessionProfileId } : {}),
      ...(notice.taskId ? { taskId: notice.taskId } : {}),
    } as const;
    try {
      options.log(payload);
    } catch {
      /* Logging failure must not fail collection. */
    }
    const channels: Promise<void>[] = [];
    if (os === "darwin") {
      // Fixed text built only from enum values; anything else falls back to a generic message.
      const detail =
        SAFE_TOKEN.test(notice.platform) && SAFE_TOKEN.test(notice.reason)
          ? `${notice.platform}: ${notice.reason}`
          : "operator action required";
      channels.push(
        exec(
          "macos",
          "osascript",
          [
            "-e",
            `display notification "Check the collector profile window (${detail})" with title "AIVC Collector"`,
          ],
          undefined,
          notice,
        ),
      );
    }
    if (options.command)
      channels.push(exec("command", options.command, [], JSON.stringify(payload), notice));
    await Promise.all(channels);
  };
}
