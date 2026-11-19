import { createHash } from "node:crypto";
import { mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { loadCollectorConfig } from "@aivc/config";
import { assertApprovedTask, reserveAttempt } from "./approval-guard.ts";
import { bindRealSpool } from "./spool-binding.ts";
import { collectorEnv, fixtureTask } from "./test-fixtures.ts";

// Legacy ledger semantics (no session profile, design §12.1).
const legacyEnv = { ...collectorEnv, COLLECTOR_SESSION_PROFILE_ID: undefined };
const directories: string[] = [];
afterEach(async () => {
  for (const directory of directories.splice(0))
    await rm(directory, { recursive: true, force: true });
});
async function setup() {
  const directory = await mkdtemp(join(tmpdir(), "aivc-platform-binding-"));
  directories.push(directory);
  const doubao = loadCollectorConfig({
    ...legacyEnv,
    COLLECTOR_MODE: "real",
    COLLECTOR_SPOOL_DIRECTORY: directory,
  });
  const ernie = loadCollectorConfig({
    ...legacyEnv,
    COLLECTOR_MODE: "real",
    COLLECTOR_SPOOL_DIRECTORY: directory,
    COLLECTOR_SUPPORTED_PLATFORMS: "ernie",
  });
  const ernieTask = {
    ...fixtureTask(),
    platformCode: "ernie" as const,
    platformEntryCode: "ernie.consumer_web",
  };
  return { directory, doubao, ernie, ernieTask };
}
describe("real platform isolation with legacy Doubao compatibility", () => {
  it("isolates Qingyan approval, attempts and spool from both legacy platforms", async () => {
    const { directory, doubao, ernie, ernieTask } = await setup();
    const qingyan = loadCollectorConfig({
      ...legacyEnv,
      COLLECTOR_MODE: "real",
      COLLECTOR_SPOOL_DIRECTORY: directory,
      COLLECTOR_SUPPORTED_PLATFORMS: "zhipu_qingyan",
    });
    const task = {
      ...fixtureTask(),
      platformCode: "zhipu_qingyan" as const,
      platformEntryCode: "zhipu_qingyan.consumer_web",
    };
    expect(() => assertApprovedTask(task, qingyan)).not.toThrow();
    for (const other of [doubao, ernie])
      expect(() => assertApprovedTask(task, other)).toThrow("task_not_approved");
    expect(() => assertApprovedTask(ernieTask, qingyan)).toThrow("task_not_approved");
    expect(() =>
      assertApprovedTask({ ...task, platformEntryCode: "ernie.consumer_web" }, qingyan),
    ).toThrow("task_not_approved");
    await reserveAttempt(directory, fixtureTask(), doubao);
    await reserveAttempt(directory, ernieTask, ernie);
    await reserveAttempt(directory, task, qingyan);
    await expect(reserveAttempt(directory, { ...task, leaseVersion: 2 }, qingyan)).rejects.toThrow(
      "task_not_approved",
    );
    expect(await readdir(directory)).toHaveLength(3);
    // Existing unbound ledger contents must not be adopted as a fresh spool.
    await expect(bindRealSpool(qingyan)).rejects.toThrow("task_not_approved");
    const spoolDirectory = (await setup()).directory;
    const spoolConfig = { ...qingyan, COLLECTOR_SPOOL_DIRECTORY: spoolDirectory };
    await bindRealSpool(spoolConfig);
    await bindRealSpool(spoolConfig);
    for (const other of [doubao, ernie])
      await expect(
        bindRealSpool({ ...other, COLLECTOR_SPOOL_DIRECTORY: spoolDirectory }),
      ).rejects.toThrow("task_not_approved");
  });
  it("approves the selected platform only, with the exact entry", async () => {
    const { ernie, ernieTask } = await setup();
    expect(() => assertApprovedTask(ernieTask, ernie)).not.toThrow();
    expect(() => assertApprovedTask(fixtureTask(), ernie)).toThrow("task_not_approved");
    expect(() =>
      assertApprovedTask({ ...ernieTask, platformEntryCode: "doubao.consumer_web" }, ernie),
    ).toThrow("task_not_approved");
  });
  it("retains the legacy Doubao ledger while allowing the same question on Ernie once", async () => {
    const { directory, doubao, ernie, ernieTask } = await setup();
    const hash = createHash("sha256")
      .update(
        JSON.stringify([
          doubao.COLLECTOR_API_URL,
          doubao.COLLECTOR_TENANT_ID,
          doubao.COLLECTOR_EXPECTED_PROJECT_ID,
        ]),
      )
      .digest("hex");
    const path = join(directory, `attempts-${hash}.jsonl`);
    const legacy =
      JSON.stringify({
        taskId: fixtureTask().id,
        questionHash: createHash("sha256").update(fixtureTask().questionText).digest("hex"),
      }) + "\n";
    await writeFile(path, legacy, { mode: 0o600 });
    await expect(reserveAttempt(directory, fixtureTask(), doubao)).rejects.toThrow(
      "task_not_approved",
    );
    await reserveAttempt(directory, ernieTask, ernie);
    await expect(
      reserveAttempt(directory, { ...ernieTask, leaseVersion: 2 }, ernie),
    ).rejects.toThrow("task_not_approved");
    expect(await readFile(path, "utf8")).toBe(legacy);
    expect(await readdir(directory)).toHaveLength(2);
  });
  it("accepts legacy Doubao spool binding and refuses replay under Ernie", async () => {
    const { directory, doubao, ernie } = await setup();
    const legacy = createHash("sha256")
      .update(
        JSON.stringify([
          doubao.COLLECTOR_MODE,
          doubao.COLLECTOR_API_URL,
          doubao.COLLECTOR_TENANT_ID,
          doubao.COLLECTOR_EXPECTED_PROJECT_ID,
          doubao.COLLECTOR_NODE_ID,
        ]),
      )
      .digest("hex");
    await writeFile(join(directory, ".binding"), legacy, { mode: 0o600 });
    await bindRealSpool(doubao);
    await expect(bindRealSpool(ernie)).rejects.toThrow("task_not_approved");
    expect(await readFile(join(directory, ".binding"), "utf8")).toBe(legacy);
  });
  it("refuses reverse platform spool reuse", async () => {
    const { doubao, ernie } = await setup();
    await bindRealSpool(ernie);
    await bindRealSpool(ernie);
    await expect(bindRealSpool(doubao)).rejects.toThrow("task_not_approved");
  });
});
