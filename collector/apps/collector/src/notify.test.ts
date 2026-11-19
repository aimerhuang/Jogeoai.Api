import type { execFile } from "node:child_process";
import { chmod, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createNotifier, type OperatorNotice } from "./notify.ts";
import { fixtureTask } from "./test-fixtures.ts";

const directories: string[] = [];
afterEach(async () => {
  vi.unstubAllEnvs();
  for (const directory of directories.splice(0))
    await rm(directory, { recursive: true, force: true });
});
const notice: OperatorNotice = {
  platform: "doubao",
  reason: "challenge_required",
  phase: "pre_send",
  sessionProfileId: "66666666-6666-4666-8666-666666666666",
  taskId: fixtureTask().id,
};
async function script(body: string) {
  const directory = await mkdtemp(join(tmpdir(), "aivc-notify-"));
  directories.push(directory);
  const path = join(directory, "notify.sh");
  await writeFile(path, `#!/bin/sh\n${body.replaceAll("$DIR", directory)}\n`);
  await chmod(path, 0o700);
  return { directory, path };
}

describe("operator notification", () => {
  it("logs and pipes only identifiers to the command, with an allowlisted environment", async () => {
    vi.stubEnv("COLLECTOR_SERVICE_TOKEN", "secret-token-that-must-not-leak-000");
    const { directory, path } = await script('cat > "$DIR/payload.json"\nenv > "$DIR/env.txt"');
    const log = vi.fn();
    await createNotifier({ log, command: path, platform: "linux" })(notice);
    const expected = {
      event: "operator_action_required",
      platform: "doubao",
      reason: "challenge_required",
      phase: "pre_send",
      sessionProfileId: notice.sessionProfileId,
      taskId: notice.taskId,
    };
    expect(log.mock.calls).toEqual([[expected]]);
    const payload = await readFile(join(directory, "payload.json"), "utf8");
    expect(JSON.parse(payload)).toEqual(expected);
    expect(payload).not.toContain(fixtureTask().questionText);
    const environment = await readFile(join(directory, "env.txt"), "utf8");
    expect(environment).not.toContain("COLLECTOR_SERVICE_TOKEN");
    expect(environment).not.toContain("secret-token-that-must-not-leak");
  });
  it("never rejects when the command fails or does not exist", async () => {
    const { path } = await script("echo 'private output' >&2\nexit 7");
    const log = vi.fn();
    const notify = createNotifier({ log, command: path, platform: "linux" });
    await expect(notify(notice)).resolves.toBeUndefined();
    await expect(
      createNotifier({ log, command: "/nonexistent/aivc-notify", platform: "linux" })(notice),
    ).resolves.toBeUndefined();
    const failures = log.mock.calls.filter(([entry]) => entry.event !== "operator_action_required");
    expect(failures).toEqual([
      [{ event: "operator_notification_failed", channel: "command", reason: "challenge_required" }],
      [{ event: "operator_notification_failed", channel: "command", reason: "challenge_required" }],
    ]);
    expect(JSON.stringify(log.mock.calls)).not.toContain("private output");
  });
  it("never rejects when logging throws", async () => {
    const notify = createNotifier({
      log: () => {
        throw new Error("log sink down");
      },
      platform: "linux",
    });
    await expect(notify(notice)).resolves.toBeUndefined();
  });
  it("shows a fixed macOS notification without a shell", async () => {
    const run = vi.fn((_file: string, _args: string[], _options: unknown, callback: () => void) => {
      callback();
      return { stdin: null };
    });
    await createNotifier({
      log: vi.fn(),
      platform: "darwin",
      execFile: run as unknown as typeof execFile,
    })(notice);
    expect(run).toHaveBeenCalledTimes(1);
    const [file, args] = run.mock.calls[0]!;
    expect(file).toBe("osascript");
    expect(args).toEqual([
      "-e",
      'display notification "Check the collector profile window (doubao: challenge_required)" with title "AIVC Collector"',
    ]);
  });
});
