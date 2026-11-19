import { chmod, mkdtemp, readdir, readFile, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { loadCollectorConfig } from "@aivc/config";
import type { LeasedTask } from "@aivc/contracts";
import {
  APPROVED_QUESTIONS,
  assertApprovedTask,
  assertAttemptCapacity,
  loadApprovedQuestions,
  reserveAttempt,
} from "./approval-guard.ts";
import { AdapterRegistry, CollectorError, mockAdapter } from "./platform-adapter.ts";
import { runCollector } from "./runner.ts";
import { collectorEnv, fixtureTask } from "./test-fixtures.ts";

const directories: string[] = [];
afterEach(async () => {
  for (const directory of directories.splice(0))
    await rm(directory, { recursive: true, force: true });
});

const approvedQuestion = "市政和工业管道检测需要采购推杆式内窥镜，分别推荐哪些厂家和型号？";
const approvedFile = (overrides: Record<string, unknown> = {}) => ({
  version: 1,
  tenantId: collectorEnv.COLLECTOR_TENANT_ID,
  projectId: collectorEnv.COLLECTOR_EXPECTED_PROJECT_ID,
  approvedAt: "2026-10-21T06:00:00.000Z",
  questions: [approvedQuestion, "下水道管道检测机器人有哪些品牌？"],
  ...overrides,
});

async function tempDirectory(): Promise<string> {
  const directory = await mkdtemp(join(tmpdir(), "aivc-approved-"));
  directories.push(directory);
  return directory;
}
async function writeApproved(content: unknown, mode = 0o600): Promise<string> {
  const path = join(await tempDirectory(), "approved-questions.json");
  await writeFile(path, typeof content === "string" ? content : JSON.stringify(content), {
    mode,
  });
  await chmod(path, mode);
  return path;
}
// Without a session profile: the bounded legacy run and lifetime ledger (design §12.1).
const realConfig = (file?: string, extra: Record<string, string> = {}) =>
  loadCollectorConfig({
    ...collectorEnv,
    COLLECTOR_SESSION_PROFILE_ID: undefined,
    COLLECTOR_MODE: "real",
    ...(file ? { COLLECTOR_APPROVED_QUESTIONS_FILE: file } : {}),
    ...extra,
  });
const taskFor = (questionText: string, index = 0) => ({
  ...fixtureTask(),
  id: `33333333-3333-4333-8333-3333333333${String(index).padStart(2, "0")}`,
  questionText,
});

describe("operator-approved question list", () => {
  it("keeps the built-in list when no file is configured", async () => {
    const config = realConfig();
    const approved = await loadApprovedQuestions(config);
    expect(approved).toBe(APPROVED_QUESTIONS);
    expect(() => assertApprovedTask(fixtureTask(), config, approved)).not.toThrow();
    expect(() => assertApprovedTask(fixtureTask(), config)).not.toThrow();
    expect(() => assertApprovedTask(taskFor(approvedQuestion), config, approved)).toThrowError(
      "task_not_approved",
    );
  });
  it("accepts an exact listed question and replaces the built-in list", async () => {
    const config = realConfig(await writeApproved(approvedFile()));
    const approved = await loadApprovedQuestions(config);
    expect(approved).toEqual(approvedFile().questions);
    expect(() => assertApprovedTask(taskFor(approvedQuestion), config, approved)).not.toThrow();
    await reserveAttempt(await tempDirectory(), taskFor(approvedQuestion), config, approved);
    // Built-in smoke questions are not implicitly approved for the configured binding.
    expect(() => assertApprovedTask(fixtureTask(), config, approved)).toThrowError(
      "task_not_approved",
    );
  });
  it.each([
    ["unlisted question", "其他问题"],
    ["trailing whitespace", `${approvedQuestion} `],
    ["leading whitespace", ` ${approvedQuestion}`],
  ])("rejects a miss without normalization: %s", async (_label, questionText) => {
    const config = realConfig(await writeApproved(approvedFile()));
    const approved = await loadApprovedQuestions(config);
    expect(() => assertApprovedTask(taskFor(questionText), config, approved)).toThrowError(
      "task_not_approved",
    );
    await expect(
      reserveAttempt(await tempDirectory(), taskFor(questionText), config, approved),
    ).rejects.toThrowError("task_not_approved");
  });
  it("fails closed when a configured list was not loaded", async () => {
    const config = realConfig(await writeApproved(approvedFile()));
    expect(() => assertApprovedTask(taskFor(approvedQuestion), config)).toThrowError(
      "task_not_approved",
    );
    await expect(
      reserveAttempt(await tempDirectory(), taskFor(approvedQuestion), config),
    ).rejects.toThrowError("task_not_approved");
  });
  it.each([
    ["tenant mismatch", { tenantId: "99999999-9999-4999-8999-999999999999" }],
    ["project mismatch", { projectId: "99999999-9999-4999-8999-999999999999" }],
    ["unsupported version", { version: 2 }],
    ["empty list", { questions: [] }],
    ["duplicate questions", { questions: [approvedQuestion, approvedQuestion] }],
    ["empty question", { questions: [""] }],
    ["overlong question", { questions: ["问".repeat(501)] }],
    ["too many questions", { questions: Array.from({ length: 51 }, (_, i) => `问题 ${i}`) }],
    ["invalid approval time", { approvedAt: "yesterday" }],
    ["unknown key", { note: "extra" }],
  ])("rejects an invalid or mis-bound file: %s", async (_label, overrides) => {
    const config = realConfig(await writeApproved(approvedFile(overrides)));
    await expect(loadApprovedQuestions(config)).rejects.toThrowError("task_not_approved");
  });
  it("accepts the 50-question maximum", async () => {
    const questions = Array.from({ length: 50 }, (_, i) => `问题 ${i}`);
    const config = realConfig(await writeApproved(approvedFile({ questions })));
    expect(await loadApprovedQuestions(config)).toEqual(questions);
  });
  it.each([0o644, 0o640, 0o604, 0o660])("rejects permissions wider than 0600: %s", async (mode) => {
    const config = realConfig(await writeApproved(approvedFile(), mode));
    await expect(loadApprovedQuestions(config)).rejects.toThrowError("task_not_approved");
  });
  it("rejects a symlink even when its target is a valid private file", async () => {
    const target = await writeApproved(approvedFile());
    const link = join(await tempDirectory(), "approved-link.json");
    await symlink(target, link);
    await expect(loadApprovedQuestions(realConfig(link))).rejects.toThrowError("task_not_approved");
    expect(await loadApprovedQuestions(realConfig(target))).toHaveLength(2);
  });
  it("rejects a missing file, a directory and malformed JSON", async () => {
    const directory = await tempDirectory();
    await chmod(directory, 0o700);
    for (const path of [join(directory, "missing.json"), directory]) {
      await expect(loadApprovedQuestions(realConfig(path))).rejects.toThrowError(
        "task_not_approved",
      );
    }
    await expect(
      loadApprovedQuestions(realConfig(await writeApproved("{not json"))),
    ).rejects.toThrowError("task_not_approved");
  });
  it("accepts a ten-task cap and the config rejects eleven", async () => {
    expect(() => realConfig(undefined, { COLLECTOR_MAX_TASKS_PER_RUN: "11" })).toThrow();
    const questions = Array.from({ length: 11 }, (_, i) => `批准问题 ${i}`);
    const config = realConfig(await writeApproved(approvedFile({ questions })), {
      COLLECTOR_MAX_TASKS_PER_RUN: "10",
    });
    const approved = await loadApprovedQuestions(config);
    const ledger = await tempDirectory();
    for (let i = 0; i < 10; i += 1)
      await reserveAttempt(ledger, taskFor(questions[i]!, i), config, approved);
    await expect(
      reserveAttempt(ledger, taskFor(questions[10]!, 10), config, approved),
    ).rejects.toThrowError("task_not_approved");
  });
});

describe("runner startup validation", () => {
  function client() {
    const task = taskFor(approvedQuestion);
    return {
      lease: vi.fn().mockResolvedValue([task]),
      heartbeat: vi.fn().mockResolvedValue({ leaseExpiresAt: task.leaseExpiresAt }),
      submit: vi.fn().mockResolvedValue({ disposition: "accepted", sampleId: task.id }),
      reportFailure: vi.fn().mockResolvedValue({
        sessionProfileStatus: "active",
        taskDisposition: "requeued",
        platformPaused: false,
      }),
      sessionEvent: vi.fn().mockResolvedValue({ sessionProfileStatus: "active" }),
      eligibility: vi.fn().mockResolvedValue({ eligible: true, retryAfterSeconds: 30 }),
    };
  }
  it("exits on a bad approved file before replaying spool or leasing", async () => {
    const spool = await tempDirectory();
    const config = {
      ...realConfig(await writeApproved(approvedFile(), 0o644)),
      COLLECTOR_SPOOL_DIRECTORY: spool,
    };
    const control = client();
    await expect(
      runCollector(
        config,
        control,
        new AdapterRegistry([mockAdapter("doubao")]),
        new AbortController().signal,
        vi.fn(),
        await tempDirectory(),
      ),
    ).rejects.toMatchObject({ code: "task_not_approved" });
    expect(control.lease).not.toHaveBeenCalled();
    expect(control.submit).not.toHaveBeenCalled();
  });
  it("loads a valid file itself and collects a listed question", async () => {
    const config = {
      ...realConfig(await writeApproved(approvedFile()), { COLLECTOR_MAX_TASKS_PER_RUN: "1" }),
      COLLECTOR_SPOOL_DIRECTORY: await tempDirectory(),
    };
    const control = client();
    expect(
      await runCollector(
        config,
        control,
        new AdapterRegistry([mockAdapter("doubao")]),
        new AbortController().signal,
        vi.fn(),
        await tempDirectory(),
      ),
    ).toBe(1);
    expect(control.submit).toHaveBeenCalledTimes(1);
  });
});

describe("eight approved tasks under an eight-task cap (live run shape)", () => {
  const questions = Array.from({ length: 8 }, (_, i) => `批准问题 ${i}：管道内窥镜怎么选？`);
  const tasks = questions.map((question, i) => taskFor(question, i));
  async function setup() {
    const file = await writeApproved(approvedFile({ questions }));
    const spool = await tempDirectory();
    const ledger = await tempDirectory();
    const config = (extra: Record<string, string> = {}) => ({
      ...realConfig(file, {
        COLLECTOR_MAX_TASKS_PER_RUN: "8",
        COLLECTOR_RUN_ONCE: "false",
        ...extra,
      }),
      COLLECTOR_SPOOL_DIRECTORY: spool,
      COLLECTOR_MIN_REQUEST_INTERVAL_MS: 0,
    });
    const queue: LeasedTask[] = [];
    const control = {
      lease: vi.fn(async () => (queue.length ? [queue.shift()!] : [])),
      heartbeat: vi.fn(async () => ({ leaseExpiresAt: tasks[0]!.leaseExpiresAt })),
      submit: vi.fn(async () => ({ disposition: "accepted" as const, sampleId: tasks[0]!.id })),
      reportFailure: vi.fn().mockResolvedValue({
        sessionProfileStatus: "active",
        taskDisposition: "requeued",
        platformPaused: false,
      }),
      sessionEvent: vi.fn().mockResolvedValue({ sessionProfileStatus: "active" }),
      eligibility: vi.fn().mockResolvedValue({ eligible: true, retryAfterSeconds: 30 }),
    };
    const run = (
      extra: Record<string, string> = {},
      registry = new AdapterRegistry([mockAdapter("doubao")]),
    ) =>
      runCollector(config(extra), control, registry, new AbortController().signal, vi.fn(), ledger);
    return { queue, control, run };
  }
  it("runs all eight distinct tasks, never leases a ninth, and a restart stops before leasing", async () => {
    const { queue, control, run } = await setup();
    queue.push(...tasks);
    expect(await run()).toBe(8);
    expect(control.lease).toHaveBeenCalledTimes(8);
    expect(control.submit).toHaveBeenCalledTimes(8);
    queue.push(taskFor(questions[0]!, 9));
    await expect(run()).rejects.toMatchObject({ code: "task_not_approved" });
    expect(control.lease).toHaveBeenCalledTimes(8);
  });
  it("retries a failed task explicitly, then stops before leasing once the ledger is full", async () => {
    const { queue, control, run } = await setup();
    let failed = false;
    const flaky = {
      ...mockAdapter("doubao"),
      collect: vi.fn(async (task: LeasedTask, signal: AbortSignal) => {
        if (task.id === tasks[2]!.id && !failed) {
          failed = true;
          throw new CollectorError("collection_timeout");
        }
        return mockAdapter("doubao").collect(task, signal);
      }),
    };
    queue.push(...tasks);
    await expect(run({}, new AdapterRegistry([flaky]))).rejects.toMatchObject({
      code: "collection_timeout",
    });
    expect(control.submit).toHaveBeenCalledTimes(2);
    // Plain restart: the failed task is in the ledger, so it is refused (after its lease).
    queue.splice(0, queue.length, tasks[2]!);
    await expect(run()).rejects.toMatchObject({ code: "task_not_approved" });
    // Explicit retry with the approved list still enforced.
    queue.splice(0, queue.length, tasks[2]!);
    expect(
      await run({ COLLECTOR_MAX_TASKS_PER_RUN: "1", COLLECTOR_RETRY_TASK_ID: tasks[2]!.id }),
    ).toBe(1);
    // Ledger now holds 4 entries; four more fit, then the run stops before leasing task 7.
    queue.splice(0, queue.length, ...tasks.slice(3));
    const leasesBefore = control.lease.mock.calls.length;
    await expect(run()).rejects.toMatchObject({ code: "task_not_approved" });
    expect(control.lease.mock.calls.length - leasesBefore).toBe(4);
    expect(queue).toEqual([tasks[7]]);
    expect(
      await run({ COLLECTOR_MAX_TASKS_PER_RUN: "1", COLLECTOR_RETRY_TASK_ID: tasks[7]!.id }),
    ).toBe(1);
    expect(control.submit).toHaveBeenCalledTimes(8);
  });
  it("refuses an explicit retry whose question is not on the approved list", async () => {
    const config = realConfig(await writeApproved(approvedFile({ questions })), {
      COLLECTOR_RETRY_TASK_ID: taskFor("其他问题", 9).id,
    });
    const approved = await loadApprovedQuestions(config);
    expect(() => assertApprovedTask(taskFor("其他问题", 9), config, approved)).toThrowError(
      "task_not_approved",
    );
    await expect(
      reserveAttempt(await tempDirectory(), taskFor("其他问题", 9), config, approved),
    ).rejects.toThrowError("task_not_approved");
  });
});

describe("profile-mode attempt ledger (design §12.1)", () => {
  const profileConfig = (extra: Record<string, string> = {}) =>
    loadCollectorConfig({ ...collectorEnv, COLLECTOR_MODE: "real", ...extra });
  async function ledgerLines(directory: string): Promise<Record<string, unknown>[]> {
    const [file] = await readdir(directory);
    if (!file) return [];
    return (await readFile(join(directory, file), "utf8"))
      .split("\n")
      .filter(Boolean)
      .map((line) => JSON.parse(line) as Record<string, unknown>);
  }

  it("has no lifetime cap and lets the same question run again on another task", async () => {
    const config = profileConfig({ COLLECTOR_MAX_TASKS_PER_RUN: "1" });
    const ledger = await tempDirectory();
    for (let i = 0; i < 3; i += 1) {
      await assertAttemptCapacity(ledger, config);
      await reserveAttempt(ledger, taskFor(APPROVED_QUESTIONS[0], i), config);
    }
    await assertAttemptCapacity(ledger, config);
    expect(await ledgerLines(ledger)).toHaveLength(3);
    await expect(assertAttemptCapacity(undefined, config)).rejects.toThrowError(
      "profile_unavailable",
    );
  });

  it("dedupes only on task and lease version, and records the lease version", async () => {
    const config = profileConfig();
    const ledger = await tempDirectory();
    const task = fixtureTask();
    await reserveAttempt(ledger, task, config);
    await expect(reserveAttempt(ledger, task, config)).rejects.toThrowError("task_not_approved");
    // The control plane re-leased the task after a crash: a new lease version may run.
    await reserveAttempt(ledger, { ...task, leaseVersion: 2 }, config);
    await expect(reserveAttempt(ledger, { ...task, leaseVersion: 2 }, config)).rejects.toThrowError(
      "task_not_approved",
    );
    expect(await ledgerLines(ledger)).toEqual([
      { taskId: task.id, questionHash: expect.any(String), leaseVersion: 1 },
      { taskId: task.id, questionHash: expect.any(String), leaseVersion: 2 },
    ]);
  });

  it("still enforces the approved-question list and writes nothing for a refused task", async () => {
    const config = profileConfig();
    const ledger = await tempDirectory();
    await expect(reserveAttempt(ledger, taskFor("其他问题"), config)).rejects.toThrowError(
      "task_not_approved",
    );
    expect(await ledgerLines(ledger)).toEqual([]);
    const listed = profileConfig({
      COLLECTOR_APPROVED_QUESTIONS_FILE: await writeApproved(approvedFile()),
    });
    const approved = await loadApprovedQuestions(listed);
    await reserveAttempt(ledger, taskFor(approvedQuestion), listed, approved);
    await expect(
      reserveAttempt(ledger, taskFor(APPROVED_QUESTIONS[0], 1), listed, approved),
    ).rejects.toThrowError("task_not_approved");
  });

  it("reads legacy lines and keeps writing legacy lines without a profile", async () => {
    const ledger = await tempDirectory();
    const task = fixtureTask();
    // Legacy run first: its line has no lease version and keeps the old shape.
    await reserveAttempt(ledger, task, realConfig());
    expect(Object.keys((await ledgerLines(ledger))[0]!)).toEqual(["taskId", "questionHash"]);
    // A versionless legacy line never blocks a profile-mode lease of the same task.
    await reserveAttempt(ledger, { ...task, leaseVersion: 3 }, profileConfig());
    // Back without a profile: every line (including profile-mode ones) counts toward the cap.
    await expect(
      reserveAttempt(
        ledger,
        taskFor(APPROVED_QUESTIONS[1], 1),
        realConfig(undefined, {
          COLLECTOR_MAX_TASKS_PER_RUN: "2",
        }),
      ),
    ).rejects.toThrowError("task_not_approved");
  });
});
