import { mkdtemp, readFile, readdir, realpath, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { APP_COLLECT_EXIT_CODES, runAppCollect } from "./app-collect.ts";
import { APPROVED_QUESTIONS } from "./approval-guard.ts";
import { loadAppChannelEnv } from "./app-channel/env.ts";
import { EvidenceStore, appRecordSchema, type AppRecord } from "./app-channel/evidence.ts";
import { MockChatApp, MockDrivers, type MockChatOptions } from "./app-channel/mock-driver.ts";
import {
  DEVICE_A,
  DEVICE_B,
  VirtualClock,
  bindingsFixture,
  combinedRunner,
  contractDirectory,
  emulatorA,
  emulatorB,
  evidenceDirectory,
  fakeAdb,
  fakeRecorderDevice,
  ffprobeAnswer,
  recordFixture,
  referenceContract,
  steppingClock,
  type FakeEmulator,
} from "./app-channel/test-fixtures.ts";

const directories: string[] = [];
afterEach(async () => {
  for (const directory of directories.splice(0))
    await rm(directory, { recursive: true, force: true });
});
async function bindingsFile(content: unknown): Promise<string> {
  const directory = await mkdtemp(join(tmpdir(), "aivc-app-collect-"));
  directories.push(directory);
  const path = join(directory, "app-bindings.json");
  await writeFile(path, JSON.stringify(content));
  return path;
}
async function run(
  argv: string[],
  options: { emulators?: FakeEmulator[]; bindings?: unknown; env?: NodeJS.ProcessEnv } = {},
) {
  const lines: Record<string, unknown>[] = [];
  const errors: Record<string, unknown>[] = [];
  const runner = fakeAdb(options.emulators ?? [emulatorA(), emulatorB()]);
  const code = await runAppCollect({
    argv,
    env: {
      APP_COLLECTOR_BINDINGS_FILE: await bindingsFile(options.bindings ?? bindingsFixture()),
      ...options.env,
    },
    runner,
    log: (line) => lines.push(line),
    logError: (line) => errors.push(line),
  });
  return { code, lines, errors, runner };
}

describe("app-collect entry", () => {
  it("rejects unknown and missing subcommands with a clear error", async () => {
    for (const argv of [[], ["collect"], ["--help"]]) {
      const { code, errors, runner } = await run(argv);
      expect(code).toBe(APP_COLLECT_EXIT_CODES.failed);
      expect(errors).toEqual([
        {
          event: "app_collect_failed",
          code: "invalid_command",
          availableCommands: ["check", "run", "serve", "report", "prune"],
        },
      ]);
      expect(runner.calls).toEqual([]);
    }
  });
  it("rejects malformed arguments of run, report and prune before touching anything", async () => {
    const cases: [string[], string][] = [
      [["run"], "--batch"],
      [["run", "--batch", "../escape"], "--batch"],
      [["run", "--batch", "b1", "--limit", "0"], "--limit"],
      [["run", "--batch", "b1", "--unknown"], "arguments"],
      [["run", "--batch", "b1", "extra"], "arguments"],
      [["report"], "--batch"],
      [["prune", "--days", "soon"], "--days"],
    ];
    for (const [argv, path] of cases) {
      const { code, errors, runner } = await run(argv);
      expect(code).toBe(APP_COLLECT_EXIT_CODES.failed);
      expect(errors).toHaveLength(1);
      expect(errors[0]).toMatchObject({ code: "invalid_command", issues: [{ path }] });
      expect(runner.calls).toEqual([]);
    }
  });
  it("requires the evidence and contract directories for the commands that use them", async () => {
    const cases: [string[], string[]][] = [
      [
        ["run", "--batch", "b1"],
        ["APP_COLLECTOR_EVIDENCE_DIR", "APP_COLLECTOR_CONTRACT_DIR"],
      ],
      [["report", "--batch", "b1"], ["APP_COLLECTOR_EVIDENCE_DIR"]],
      [["prune"], ["APP_COLLECTOR_EVIDENCE_DIR"]],
      [
        ["serve"],
        [
          "APP_COLLECTOR_EVIDENCE_DIR",
          "APP_COLLECTOR_CONTRACT_DIR",
          "APP_COLLECTOR_API_URL",
          "APP_COLLECTOR_NODE_ID",
          "APP_COLLECTOR_NODE_TOKEN",
        ],
      ],
    ];
    for (const [argv, paths] of cases) {
      const { code, errors, runner } = await run(argv);
      expect(code).toBe(APP_COLLECT_EXIT_CODES.failed);
      expect(errors[0]).toMatchObject({ code: "environment_invalid" });
      expect((errors[0]!.issues as { path: string }[]).map((issue) => issue.path)).toEqual(paths);
      expect(runner.calls).toEqual([]);
    }
  });
  it("refuses to serve against the Mock or fixture queue and never prints the node token", async () => {
    for (const url of ["http://127.0.0.1:4311", "http://localhost:4312"]) {
      const { code, errors, runner } = await run(["serve"], {
        env: {
          APP_COLLECTOR_API_URL: url,
          APP_COLLECTOR_NODE_ID: "mac-local",
          APP_COLLECTOR_NODE_TOKEN: "synthetic-node-token-0123456789",
        },
      });
      expect(code).toBe(APP_COLLECT_EXIT_CODES.failed);
      expect(errors[0]).toMatchObject({
        code: "environment_invalid",
        issues: [{ path: "APP_COLLECTOR_API_URL" }],
      });
      expect(JSON.stringify(errors)).not.toContain("synthetic-node-token");
      expect(runner.calls).toEqual([]);
    }
  });
});

describe("app-collect check", () => {
  it("prints one line per binding and a summary, and exits 0 when every enabled binding passes", async () => {
    const bindings = bindingsFixture();
    Object.assign(bindings.bindings[3]!, { enabled: false, disabledReason: "not readable" });
    const { code, lines, errors } = await run(["check"], { bindings });
    expect(code).toBe(APP_COLLECT_EXIT_CODES.finished);
    expect(errors).toEqual([]);
    expect(
      lines.slice(0, 4).map((line) => [line.event, line.device, line.platform, line.status]),
    ).toEqual([
      ["app_binding_check", "AI_Apps_4KB", "doubao", "pass"],
      ["app_binding_check", "AI_Apps_4KB", "kimi", "pass"],
      ["app_binding_check", "AI_Apps_4KB_2", "doubao", "pass"],
      ["app_binding_check", "AI_Apps_4KB_2", "kimi", "disabled"],
    ]);
    expect(lines[3]).toMatchObject({ disabledReason: "not readable" });
    expect(lines[4]).toEqual({
      event: "app_check_summary",
      bindings: 4,
      passed: 3,
      failed: 0,
      disabled: 1,
      loginChecked: false,
    });
  });
  it("exits 2 when an enabled binding fails and still reports the others", async () => {
    const { code, lines } = await run(["check"], {
      emulators: [emulatorA({ timezone: "Asia/Taipei" }), emulatorB()],
    });
    expect(code).toBe(APP_COLLECT_EXIT_CODES.checkFailed);
    expect(lines[0]).toMatchObject({
      status: "fail",
      reason: "device_config_mismatch",
      mismatches: [{ field: "timezone", declared: "Asia/Shanghai", actual: "Asia/Taipei" }],
    });
    expect(lines.at(-1)).toMatchObject({ passed: 2, failed: 2, disabled: 0 });
  });
  // AC-A2: an invalid configuration stops the script before any device is contacted.
  it("exits 1 on an invalid bindings file without running a single adb command", async () => {
    const invalid: [string, (config: ReturnType<typeof bindingsFixture>) => void][] = [
      ["bindings.0.platform", (config) => void (config.bindings[0]!.platform = "not_a_platform")],
      [
        "bindings.4.platform",
        (config) => void config.bindings.push({ ...config.bindings[0]!, accountLabel: "doubao-c" }),
      ],
      ["bindings.2.accountLabel", (config) => void (config.bindings[2]!.accountLabel = "doubao-a")],
    ];
    for (const [path, mutate] of invalid) {
      const bindings = bindingsFixture();
      mutate(bindings);
      const { code, lines, errors, runner } = await run(["check"], { bindings });
      expect(code).toBe(APP_COLLECT_EXIT_CODES.failed);
      expect(lines).toEqual([]);
      expect(errors).toHaveLength(1);
      expect(errors[0]).toMatchObject({ event: "app_collect_failed", code: "bindings_invalid" });
      expect((errors[0]!.issues as { path: string }[]).map((issue) => issue.path)).toEqual([path]);
      expect(runner.calls).toEqual([]);
    }
  });
  it("exits 1 on an invalid environment before reading anything", async () => {
    const { code, errors, runner } = await run(["check"], {
      env: { APP_COLLECTOR_BINDINGS_FILE: "relative/app-bindings.json" },
    });
    expect(code).toBe(APP_COLLECT_EXIT_CODES.failed);
    expect(errors[0]).toMatchObject({
      code: "environment_invalid",
      issues: [{ path: "APP_COLLECTOR_BINDINGS_FILE" }],
    });
    expect(runner.calls).toEqual([]);
  });
  it("issues read-only adb commands only", async () => {
    const { runner } = await run(["check"]);
    const allowed =
      /^(devices|-s emulator-\d+ (emu avd name|shell (getprop \S+|getconf PAGE_SIZE|settings get system system_locales|dumpsys package \S+)))$/u;
    for (const call of runner.calls) expect(call.join(" ")).toMatch(allowed);
  });
});

describe("App-channel environment", () => {
  it("applies defaults and treats empty values as unset", () => {
    expect(loadAppChannelEnv({ APP_COLLECTOR_EVIDENCE_DIR: "", UNRELATED: "x" })).toEqual({
      APP_COLLECTOR_COLLECTION_TIMEOUT_SECONDS: 300,
      APP_COLLECTOR_VIDEO_RETENTION_DAYS: 30,
      APP_COLLECTOR_MIN_FREE_GB: 10,
      // AC-A17: recording is off unless it is switched on.
      APP_COLLECTOR_RECORD_SCREEN: "off",
      APP_COLLECTOR_RECORD_SIZE: "720x1600",
      APP_COLLECTOR_RECORD_BIT_RATE: 2_000_000,
      // Control-plane mode (`serve`) only.
      APP_COLLECTOR_LEASE_SECONDS: 180,
      APP_COLLECTOR_HTTP_TIMEOUT_MS: 15_000,
    });
  });
  it("accepts overrides and rejects relative paths and malformed values", () => {
    expect(
      loadAppChannelEnv({
        APP_COLLECTOR_EVIDENCE_DIR: "/data/aivc/evidence",
        APP_COLLECTOR_MIN_FREE_GB: "20",
        APP_COLLECTOR_RECORD_SCREEN: "on",
        APP_COLLECTOR_RECORD_SIZE: "540x1200",
        APP_COLLECTOR_RECORD_BIT_RATE: "1000000",
        APP_COLLECTOR_FFPROBE_PATH: "/opt/homebrew/bin/ffprobe",
      }),
    ).toMatchObject({
      APP_COLLECTOR_RECORD_SCREEN: "on",
      APP_COLLECTOR_EVIDENCE_DIR: "/data/aivc/evidence",
      APP_COLLECTOR_MIN_FREE_GB: 20,
      APP_COLLECTOR_RECORD_SIZE: "540x1200",
      APP_COLLECTOR_RECORD_BIT_RATE: 1_000_000,
      APP_COLLECTOR_FFPROBE_PATH: "/opt/homebrew/bin/ffprobe",
    });
    for (const env of [
      { APP_COLLECTOR_EVIDENCE_DIR: "evidence" },
      { APP_COLLECTOR_RECORD_SCREEN: "true" },
      { APP_COLLECTOR_RECORD_SIZE: "720" },
      { APP_COLLECTOR_RECORD_BIT_RATE: "1000" },
      { APP_COLLECTOR_FFPROBE_PATH: "ffprobe" },
      { APP_COLLECTOR_MIN_FREE_GB: "0" },
      { APP_COLLECTOR_ADB_PATH: "adb" },
      { APP_COLLECTOR_CONTRACT_DIR: "contracts" },
      { APP_COLLECTOR_COLLECTION_TIMEOUT_SECONDS: "5" },
      { APP_COLLECTOR_VIDEO_RETENTION_DAYS: "0" },
      { APP_COLLECTOR_SCRIPT_REVISION: "bad revision" },
      { APP_COLLECTOR_APPIUM_PORT: "80" },
      { COLLECTOR_TENANT_ID: "not-a-uuid" },
    ])
      expect(() => loadAppChannelEnv(env)).toThrowError("environment_invalid");
  });
});

// `check` with a contract directory also looks whether each account is logged in.
describe("app-collect check with contracts", () => {
  function devices(options: Partial<Record<string, Omit<MockChatOptions, "now">>> = {}) {
    const now = () => new Date("2026-11-15T04:00:00.000Z");
    return new MockDrivers({
      [DEVICE_A]: new MockChatApp({ now, ...options[DEVICE_A] }),
      [DEVICE_B]: new MockChatApp({ now, ...options[DEVICE_B] }),
    });
  }

  it("keeps login not_checked and starts no automation without a contract directory", async () => {
    const drivers = devices();
    const lines: Record<string, unknown>[] = [];
    const code = await runAppCollect({
      argv: ["check"],
      env: { APP_COLLECTOR_BINDINGS_FILE: await bindingsFile(bindingsFixture()) },
      runner: fakeAdb([emulatorA(), emulatorB()]),
      drivers,
      log: (line) => lines.push(line),
    });
    expect(code).toBe(APP_COLLECT_EXIT_CODES.finished);
    expect(lines.slice(0, 4).map((line) => line.login)).toEqual(Array(4).fill("not_checked"));
    expect(lines.at(-1)).toMatchObject({ loginChecked: false });
    expect(drivers.opened).toEqual([]);
    expect(drivers.closed).toBe(false);
  });
  it("reports logged_in, login_required and app_contract_unverified per binding", async () => {
    // doubao has a contract; kimi has none. The account on device B is logged out.
    const contracts = await contractDirectory(directories, { doubao: referenceContract() });
    const drivers = devices({ [DEVICE_B]: { loggedIn: false } });
    const lines: Record<string, unknown>[] = [];
    const runner = fakeAdb([emulatorA(), emulatorB()]);
    const clock = steppingClock();
    const code = await runAppCollect({
      argv: ["check"],
      env: {
        APP_COLLECTOR_BINDINGS_FILE: await bindingsFile(bindingsFixture()),
        APP_COLLECTOR_CONTRACT_DIR: contracts,
      },
      runner,
      drivers,
      now: clock.now,
      sleep: clock.sleep,
      log: (line) => lines.push(line),
    });
    expect(code).toBe(APP_COLLECT_EXIT_CODES.checkFailed);
    const checks = lines.filter((line) => line.event === "app_binding_check");
    expect(
      checks.map((line) => [line.device, line.platform, line.status, line.reason, line.login]),
    ).toEqual([
      [DEVICE_A, "doubao", "pass", undefined, "logged_in"],
      [DEVICE_A, "kimi", "fail", "app_contract_unverified", "failed"],
      [DEVICE_B, "doubao", "fail", "login_required", "failed"],
      [DEVICE_B, "kimi", "fail", "app_contract_unverified", "failed"],
    ]);
    expect(lines).toContainEqual({
      event: "app_contract_rejected",
      device: DEVICE_A,
      platform: "kimi",
      reason: "missing",
    });
    expect(lines.at(-1)).toMatchObject({ passed: 1, failed: 3, loginChecked: true });
    // Sessions only for the bindings with a usable contract; nothing typed; all closed.
    expect(drivers.opened).toHaveLength(2);
    for (const driver of drivers.opened) {
      expect(driver.calls).not.toContain("setText");
      expect(driver.closed).toBe(true);
    }
    expect(drivers.closed).toBe(true);
    // The device side stays read-only: no recorder, nothing but the inspector's commands.
    expect(runner.calls.some((call) => call.join(" ").includes("screenrecord"))).toBe(false);
  });
});

describe("app-collect run, report and prune", () => {
  interface World {
    evidence: string;
    env: NodeJS.ProcessEnv;
    clock: VirtualClock;
    apps: Record<string, MockChatApp>;
    drivers: MockDrivers;
    recorders: Record<string, ReturnType<typeof fakeRecorderDevice>>;
    lines: Record<string, unknown>[];
    errors: Record<string, unknown>[];
    invoke(argv: string[], extra?: { signal?: AbortSignal; freeBytes?: number }): Promise<number>;
    records(batchId: string): Promise<AppRecord[]>;
  }
  async function world(
    options: {
      emulators?: FakeEmulator[];
      contracts?: Record<string, unknown>;
      apps?: Partial<Record<string, Omit<MockChatOptions, "now">>>;
      env?: NodeJS.ProcessEnv;
    } = {},
  ): Promise<World> {
    const clock = new VirtualClock();
    const evidence = await evidenceDirectory(directories);
    const apps = {
      [DEVICE_A]: new MockChatApp({ now: clock.now, ...options.apps?.[DEVICE_A] }),
      [DEVICE_B]: new MockChatApp({ now: clock.now, ...options.apps?.[DEVICE_B] }),
    };
    const drivers = new MockDrivers(apps);
    // A video far longer than any virtual wait, so time jumps never shorten the evidence.
    const video = { ffprobe: ffprobeAnswer("1000000.000000") };
    const recorders = {
      "emulator-5556": fakeRecorderDevice(video),
      "emulator-5558": fakeRecorderDevice(video),
    };
    const runner = combinedRunner(
      fakeAdb(options.emulators ?? [emulatorA(), emulatorB()]),
      recorders,
    );
    const env = {
      APP_COLLECTOR_BINDINGS_FILE: await bindingsFile(bindingsFixture()),
      APP_COLLECTOR_EVIDENCE_DIR: evidence,
      APP_COLLECTOR_CONTRACT_DIR: await contractDirectory(
        directories,
        options.contracts ?? { doubao: referenceContract() },
      ),
      APP_COLLECTOR_SCRIPT_REVISION: "test-revision",
      ...options.env,
    };
    const lines: Record<string, unknown>[] = [];
    const errors: Record<string, unknown>[] = [];
    return {
      evidence,
      env,
      clock,
      apps,
      drivers,
      recorders,
      lines,
      errors,
      invoke: (argv, extra = {}) =>
        clock.run(
          runAppCollect({
            argv,
            env,
            runner,
            drivers,
            signal: extra.signal,
            now: clock.now,
            sleep: clock.sleep,
            diskStat: async () => ({
              bavail: (extra.freeBytes ?? 64 * 1024 ** 3) / 4096,
              bsize: 4096,
            }),
            log: (line) => lines.push(line),
            logError: (line) => errors.push(line),
          }),
        ),
      async records(batchId) {
        const store = await EvidenceStore.open(evidence);
        try {
          return await store.readRecords(batchId);
        } finally {
          await store.close();
        }
      },
    };
  }
  const sentBy = (w: World) => [...w.apps[DEVICE_A]!.sent, ...w.apps[DEVICE_B]!.sent];
  const batchFile = async (w: World, batchId: string, name: string) =>
    JSON.parse(await readFile(join(w.evidence, "batches", batchId, name), "utf8"));

  it("collects a scoped batch on both devices and resumes it without asking twice", async () => {
    const w = await world();
    // First run: at most two attempts.
    expect(await w.invoke(["run", "--batch", "b1", "--platform", "doubao", "--limit", "2"])).toBe(
      APP_COLLECT_EXIT_CODES.finished,
    );
    let records = await w.records("b1");
    expect(records).toHaveLength(2);
    expect(records.every((record) => record.status === "valid")).toBe(true);
    expect(records.every((record) => record.scriptRevision === "test-revision")).toBe(true);
    expect(sentBy(w)).toHaveLength(2);

    // AC-A4: the second run asks only what is still missing; every cell ends with one sample.
    expect(await w.invoke(["run", "--batch", "b1", "--platform", "doubao"])).toBe(
      APP_COLLECT_EXIT_CODES.finished,
    );
    records = await w.records("b1");
    expect(records).toHaveLength(4);
    expect(records.map((record) => record.questionText).sort()).toEqual(
      [...APPROVED_QUESTIONS].sort(),
    );
    // AC-A9: every sent text is an approved question as it is, and the records say so.
    expect(sentBy(w).sort()).toEqual([...APPROVED_QUESTIONS].sort());
    expect(records.every((record) => record.sentText === record.questionText)).toBe(true);
    // A third run has nothing left to ask.
    expect(await w.invoke(["run", "--batch", "b1", "--platform", "doubao"])).toBe(
      APP_COLLECT_EXIT_CODES.finished,
    );
    expect(sentBy(w)).toHaveLength(4);

    // batch.json names what each run used; summary.json has the totals.
    const batch = await batchFile(w, "b1", "batch.json");
    expect(batch.cells).toHaveLength(4);
    expect(batch.runs).toHaveLength(3);
    expect(batch.runs[0]).toMatchObject({
      scope: { platforms: ["doubao"], devices: null, questions: null, limit: 2 },
      scriptRevision: "test-revision",
      tools: { "mock-driver": "1" },
      helperPackages: [],
    });
    expect(batch.runs[0].approvedQuestionsSha256).toMatch(/^[0-9a-f]{64}$/u);
    expect(batch.runs[0].bindingsSha256).toMatch(/^[0-9a-f]{64}$/u);
    const summary = await batchFile(w, "b1", "summary.json");
    expect(summary.totals).toMatchObject({ planned: 4, valid: 4, notCollected: 0, attempts: 4 });
    expect(summary.platforms[0]).toMatchObject({ platformCode: "doubao", conclusion: "usable" });

    // Logs: identifiers, classes and counts only.
    const log = JSON.stringify(w.lines);
    for (const question of APPROVED_QUESTIONS) expect(log).not.toContain(question);
    expect(log).not.toContain("合成回答");
    expect(w.errors).toEqual([]);
    // Everything was shut down and the evidence lock released.
    expect(w.drivers.closed).toBe(true);
    expect(await readdir(w.evidence)).not.toContain(".collector.lock");
  });

  it("stops a platform without a contract and reports its cells as not collected", async () => {
    const w = await world();
    expect(await w.invoke(["run", "--batch", "b2", "--question", APPROVED_QUESTIONS[0]])).toBe(
      APP_COLLECT_EXIT_CODES.finished,
    );
    const records = await w.records("b2");
    expect(records.map((record) => [record.platformCode, record.status])).toEqual([
      ["doubao", "valid"],
    ]);
    const stopped = w.lines.filter((line) => line.event === "app_binding_stopped");
    expect(stopped.map((line) => [line.platform, line.reason]).sort()).toEqual([
      ["kimi", "app_contract_unverified"],
      ["kimi", "app_contract_unverified"],
    ]);
    const summary = await batchFile(w, "b2", "summary.json");
    expect(summary.totals).toMatchObject({ planned: 2, valid: 1, notCollected: 1 });
    expect(summary.platforms.map((p: { conclusion: string }) => p.conclusion)).toEqual([
      "usable",
      "not_verified",
    ]);
  });

  it("uses the operator's approved-question file and refuses one bound to another project", async () => {
    const tenantId = "11111111-1111-4111-8111-111111111111";
    const projectId = "22222222-2222-4222-8222-222222222222";
    const directory = await mkdtemp(join(await realpath(tmpdir()), "aivc-approved-"));
    directories.push(directory);
    const file = join(directory, "approved.json");
    await writeFile(
      file,
      JSON.stringify({
        version: 1,
        tenantId,
        projectId,
        approvedAt: "2026-11-15T00:00:00.000Z",
        questions: ["合成已审批问题"],
      }),
      { mode: 0o600 },
    );
    const env = {
      COLLECTOR_APPROVED_QUESTIONS_FILE: file,
      COLLECTOR_TENANT_ID: tenantId,
      COLLECTOR_EXPECTED_PROJECT_ID: projectId,
    };
    const w = await world({ env });
    const argv = ["run", "--batch", "b3", "--platform", "doubao", "--device", DEVICE_A];
    expect(await w.invoke(argv)).toBe(APP_COLLECT_EXIT_CODES.finished);
    expect(sentBy(w)).toEqual(["合成已审批问题"]);
    // A built-in smoke question is not in this list, so it cannot be put in scope.
    expect(await w.invoke([...argv, "--question", APPROVED_QUESTIONS[0]])).toBe(
      APP_COLLECT_EXIT_CODES.failed,
    );
    expect(w.errors.at(-1)).toMatchObject({
      code: "invalid_command",
      issues: [{ path: "--question" }],
    });

    const other = await world({
      env: { ...env, COLLECTOR_EXPECTED_PROJECT_ID: "33333333-3333-4333-8333-333333333333" },
    });
    expect(await other.invoke(argv)).toBe(APP_COLLECT_EXIT_CODES.failed);
    expect(other.errors).toEqual([
      { event: "app_collect_failed", code: "approved_questions_invalid" },
    ]);
    const unbound = await world({ env: { COLLECTOR_APPROVED_QUESTIONS_FILE: file } });
    expect(await unbound.invoke(argv)).toBe(APP_COLLECT_EXIT_CODES.failed);
    expect(unbound.errors[0]).toMatchObject({ code: "environment_invalid" });
    expect(sentBy(other)).toEqual([]);
  });

  it("rejects a scope the configuration does not know", async () => {
    const w = await world();
    for (const [argv, path] of [
      [["--platform", "chatgpt"], "--platform"],
      [["--device", "Pixel_9"], "--device"],
      [["--question", "未审批的问题"], "--question"],
    ] as const) {
      expect(await w.invoke(["run", "--batch", "b4", ...argv])).toBe(APP_COLLECT_EXIT_CODES.failed);
      expect(w.errors.at(-1)).toMatchObject({ code: "invalid_command", issues: [{ path }] });
    }
    expect(sentBy(w)).toEqual([]);
    expect(await readdir(join(w.evidence, "..")).catch(() => [])).not.toContain("b4");
  });

  it("does not start a batch when the evidence volume is nearly full", async () => {
    const w = await world();
    expect(await w.invoke(["run", "--batch", "b5"], { freeBytes: 5 * 1024 ** 3 })).toBe(
      APP_COLLECT_EXIT_CODES.failed,
    );
    expect(w.errors).toEqual([{ event: "app_collect_failed", code: "disk_space_low" }]);
    expect(w.drivers.opened).toEqual([]);
    // The lock was released: the next command can open the directory.
    expect(await w.records("b5")).toEqual([]);
  });

  it("exits 2 without an attempt when no binding in scope can work, and still writes the plan", async () => {
    const w = await world({ emulators: [] });
    expect(await w.invoke(["run", "--batch", "b6", "--platform", "doubao"])).toBe(
      APP_COLLECT_EXIT_CODES.checkFailed,
    );
    expect(w.drivers.opened).toEqual([]);
    expect(
      w.lines.filter((line) => line.event === "app_binding_check").map((line) => line.reason),
    ).toEqual(["device_offline", "device_offline"]);
    const summary = await batchFile(w, "b6", "summary.json");
    expect(summary.totals).toMatchObject({ planned: 4, valid: 0, notCollected: 4, attempts: 0 });
  });

  it("skips a platform that was judged unavailable until an operator removes the entry", async () => {
    const w = await world({
      apps: {
        [DEVICE_A]: { blocker: { code: "emulator_rejected", when: "launch" } },
        [DEVICE_B]: { blocker: { code: "emulator_rejected", when: "launch" } },
      },
    });
    const argv = ["run", "--batch", "b7", "--platform", "doubao"];
    expect(await w.invoke(argv)).toBe(APP_COLLECT_EXIT_CODES.finished);
    // One attempt found the rejection; the other binding of the platform was not tried.
    expect(await w.records("b7")).toMatchObject([
      { status: "failed", failureCode: "emulator_rejected", phase: "pre_send" },
    ]);
    const opened = w.drivers.opened.length;
    // The next batch does not open the App again.
    expect(await w.invoke(["run", "--batch", "b8", "--platform", "doubao"])).toBe(
      APP_COLLECT_EXIT_CODES.checkFailed,
    );
    expect(w.drivers.opened).toHaveLength(opened);
    expect(w.lines).toContainEqual({
      event: "app_platform_skipped",
      batchId: "b8",
      platform: "doubao",
      reason: "emulator_rejected",
    });
    const summary = await batchFile(w, "b8", "summary.json");
    expect(summary.platforms[0]).toMatchObject({
      conclusion: "unavailable",
      reasons: ["emulator_rejected"],
    });
    expect(sentBy(w)).toEqual([]);
  });

  it("exits 3 on a stop request, with recorders, sessions and the lock cleaned up", async () => {
    const w = await world({
      apps: { [DEVICE_A]: { neverCompletes: true } },
      env: { APP_COLLECTOR_RECORD_SCREEN: "on" },
    });
    const stop = new AbortController();
    const recorder = fakeRecorderDevice({ ffprobe: ffprobeAnswer("1000000.000000") });
    // The stop arrives while the question is waiting for its answer.
    const original = w.clock.sleep;
    const invoke = w.clock.run(
      runAppCollect({
        argv: ["run", "--batch", "b9", "--platform", "doubao", "--device", DEVICE_A],
        env: w.env,
        runner: combinedRunner(fakeAdb([emulatorA(), emulatorB()]), { "emulator-5556": recorder }),
        drivers: w.drivers,
        signal: stop.signal,
        now: w.clock.now,
        sleep: (ms, signal) => {
          if (w.apps[DEVICE_A]!.sent.length > 0) stop.abort();
          return original(ms, signal);
        },
        diskStat: async () => ({ bavail: 16 * 1024 ** 2, bsize: 4096 }),
        log: (line) => w.lines.push(line),
        logError: (line) => w.errors.push(line),
      }),
    );
    expect(await invoke).toBe(APP_COLLECT_EXIT_CODES.interrupted);
    // The attempt in flight ended in a record once the grace period was over; nothing new began.
    expect(await w.records("b9")).toMatchObject([
      { status: "failed", failureCode: "collection_timeout", phase: "post_send" },
    ]);
    expect(w.apps[DEVICE_A]!.sent).toHaveLength(1);
    expect(w.lines.map((line) => line.event)).toContain("app_batch_stopping");
    // The recorder was stopped and its temp file taken off the device.
    expect(recorder.started).toHaveLength(1);
    expect(recorder.deviceFiles()).toEqual([]);
    expect(w.drivers.opened.every((driver) => driver.closed)).toBe(true);
    expect(w.drivers.closed).toBe(true);
    expect(await readdir(w.evidence)).not.toContain(".collector.lock");
    // The summary of the interrupted batch is still written.
    expect((await batchFile(w, "b9", "summary.json")).totals).toMatchObject({ valid: 0 });
  });

  it("reports a finished batch and refuses an unknown one", async () => {
    const w = await world();
    await w.invoke(["run", "--batch", "b10", "--platform", "doubao", "--limit", "1"]);
    w.lines.length = 0;
    expect(await w.invoke(["report", "--batch", "b10"])).toBe(APP_COLLECT_EXIT_CODES.finished);
    expect(w.lines.map((line) => line.event)).toEqual([
      "app_report_platform",
      "app_report_platform",
      "app_report_binding",
      "app_report_binding",
      "app_report_binding",
      "app_report_binding",
      "app_report_summary",
    ]);
    expect(w.lines[0]).toMatchObject({
      platformCode: "doubao",
      planned: 4,
      valid: 1,
      notCollected: 3,
      conclusion: "usable",
      appVersions: ["15.7.0"],
    });
    expect(w.lines.at(-1)).toMatchObject({ batchId: "b10", planned: 4, valid: 1, attempts: 1 });
    expect(await w.invoke(["report", "--batch", "nope"])).toBe(APP_COLLECT_EXIT_CODES.failed);
    expect(w.errors.at(-1)).toMatchObject({
      code: "invalid_command",
      issues: [{ path: "--batch" }],
    });
  });

  it("records inside the device with the configured size and bit rate, one video per answer", async () => {
    const w = await world({
      env: {
        APP_COLLECTOR_RECORD_SCREEN: "on",
        APP_COLLECTOR_RECORD_SIZE: "540x1200",
        APP_COLLECTOR_RECORD_BIT_RATE: "1000000",
      },
    });
    await w.invoke([
      "run",
      "--batch",
      "b12",
      "--platform",
      "doubao",
      "--device",
      DEVICE_A,
      "--limit",
      "2",
    ]);
    const records = await w.records("b12");
    expect(records.map((record) => record.status)).toEqual(["valid", "valid"]);
    const starts = w.recorders["emulator-5556"]!.started;
    expect(starts).toHaveLength(2);
    expect(w.recorders["emulator-5558"]!.started).toEqual([]);
    for (const start of starts)
      expect(start.slice(3, -1)).toEqual([
        "shell",
        "screenrecord",
        "--time-limit",
        "0",
        "--size",
        "540x1200",
        "--bit-rate",
        "1000000",
      ]);
    // Each recording goes to a device temp file named after its answer; it is pulled into the
    // record directory of that answer and removed from the device.
    expect(starts.map((start) => start.at(-1)).sort()).toEqual(
      records.map((record) => `/sdcard/aivc-rec-${record.answerId}.mp4`).sort(),
    );
    expect(w.recorders["emulator-5556"]!.deviceFiles()).toEqual([]);
    for (const record of records)
      expect(
        (await readdir(join(w.evidence, "batches", "b12", "records", record.answerId))).sort(),
      ).toEqual([`${record.answerId}.mp4`, "final.png", "record.json", "ui.xml"].sort());
  });

  // AC-A17: by default nothing is recorded, and nothing of the recorder is needed.
  it("records nothing by default: no recorder command, no ffprobe, a valid sample without a video", async () => {
    const w = await world();
    const argv = ["run", "--batch", "b13", "--platform", "doubao", "--limit", "2"];
    expect(await w.invoke(argv)).toBe(APP_COLLECT_EXIT_CODES.finished);
    const records = await w.records("b13");
    expect(records.map((record) => record.status)).toEqual(["valid", "valid"]);
    for (const record of records) {
      expect(record).toMatchObject({
        video: null,
        screenshot: { file: "final.png" },
        citationsStatus: "absent",
      });
      expect(
        (await readdir(join(w.evidence, "batches", "b13", "records", record.answerId))).sort(),
      ).toEqual(["final.png", "record.json", "ui.xml"]);
    }
    // Neither device saw screenrecord, pidof, pkill or a pull, and ffprobe was never run.
    for (const recorder of Object.values(w.recorders)) expect(recorder.calls).toEqual([]);
    // The disk-space floor still applies.
    expect(
      await w.invoke(["run", "--batch", "b14", "--platform", "doubao"], {
        freeBytes: 5 * 1024 ** 3,
      }),
    ).toBe(APP_COLLECT_EXIT_CODES.failed);
    expect(w.errors).toEqual([{ event: "app_collect_failed", code: "disk_space_low" }]);
    // The report counts the samples by the state of their sources.
    const summary = await batchFile(w, "b13", "summary.json");
    expect(summary.platforms[0]).toMatchObject({
      platformCode: "doubao",
      valid: 2,
      citations: { read: 0, absent: 2, unreadable: 0, not_declared: 0 },
    });
    // With nothing recorded there is nothing to prune.
    const later = new VirtualClock("2026-12-31T04:00:00.000Z");
    await runAppCollect({
      argv: ["prune", "--days", "1"],
      env: w.env,
      now: later.now,
      log: (line) => w.lines.push(line),
    });
    expect(w.lines.at(-1)).toMatchObject({ event: "app_prune_finished", records: 2, pruned: 0 });
  });

  // AC-A17: a batch that v1.0 started (records with a video, the Web prefix in the sent text and
  // no source status) is resumed, reported and pruned as before.
  it("resumes, reports and prunes a batch holding records written before v1.1", async () => {
    const w = await world();
    const question = APPROVED_QUESTIONS[0];
    const legacy = recordFixture({
      batchId: "b15",
      questionText: question,
      sentText: `合成前缀\n\n${question}`,
    });
    expect(legacy.video).not.toBeNull();
    expect(legacy).not.toHaveProperty("citationsStatus");
    const store = await EvidenceStore.open(w.evidence);
    try {
      const directory = await store.recordDirectory("b15", legacy.answerId);
      await store.writeRecord(legacy);
      await writeFile(join(directory, legacy.video!.file), "synthetic mp4 bytes");
    } finally {
      await store.close();
    }

    // Resume: the cell with the old sample is not asked again; the other one is.
    const scope = ["--platform", "doubao", "--device", DEVICE_A];
    expect(
      await w.invoke([
        "run",
        "--batch",
        "b15",
        ...scope,
        "--question",
        question,
        "--question",
        APPROVED_QUESTIONS[1],
      ]),
    ).toBe(APP_COLLECT_EXIT_CODES.finished);
    expect(sentBy(w)).toEqual([APPROVED_QUESTIONS[1]]);
    const records = await w.records("b15");
    expect(records).toHaveLength(2);
    expect(records.find((record) => record.answerId === legacy.answerId)).toEqual(legacy);

    // Report: both samples are valid; only the new one has a source status.
    w.lines.length = 0;
    expect(await w.invoke(["report", "--batch", "b15"])).toBe(APP_COLLECT_EXIT_CODES.finished);
    expect(w.lines[0]).toMatchObject({
      event: "app_report_platform",
      platformCode: "doubao",
      planned: 2,
      valid: 2,
      notCollected: 0,
      citations: { read: 0, absent: 1, unreadable: 0, not_declared: 0 },
    });

    // Prune: the old video goes, the record stays a valid record.
    const later = new VirtualClock("2026-12-31T04:00:00.000Z");
    expect(
      await runAppCollect({
        argv: ["prune", "--days", "30"],
        env: w.env,
        now: later.now,
        log: (line) => w.lines.push(line),
      }),
    ).toBe(APP_COLLECT_EXIT_CODES.finished);
    expect(w.lines.at(-1)).toMatchObject({ event: "app_prune_finished", records: 2, pruned: 1 });
    const pruned = (await w.records("b15")).find((record) => record.answerId === legacy.answerId)!;
    expect(pruned).toMatchObject({
      status: "valid",
      sentText: legacy.sentText,
      video: { prunedAt: "2026-12-31T04:00:00.000Z" },
    });
    expect(appRecordSchema.safeParse(pruned).success).toBe(true);
    expect(
      await readdir(join(w.evidence, "batches", "b15", "records", legacy.answerId)),
    ).not.toContain(legacy.video!.file);
  });

  it("prunes videos past the retention period and keeps the records", async () => {
    const w = await world({ env: { APP_COLLECTOR_RECORD_SCREEN: "on" } });
    await w.invoke(["run", "--batch", "b11", "--platform", "doubao", "--limit", "1"]);
    const [record] = await w.records("b11");
    const directory = join(w.evidence, "batches", "b11", "records", record!.answerId);
    expect(await readdir(directory)).toContain(`${record!.answerId}.mp4`);

    // Inside the default 30 days nothing is removed.
    expect(await w.invoke(["prune"])).toBe(APP_COLLECT_EXIT_CODES.finished);
    expect(w.lines.at(-1)).toMatchObject({ event: "app_prune_finished", days: 30, pruned: 0 });
    // Two days later a one-day period removes the video and marks the record.
    const later = new VirtualClock("2026-11-18T04:00:00.000Z");
    const code = await runAppCollect({
      argv: ["prune", "--days", "1"],
      env: w.env,
      now: later.now,
      log: (line) => w.lines.push(line),
    });
    expect(code).toBe(APP_COLLECT_EXIT_CODES.finished);
    expect(w.lines.at(-1)).toMatchObject({ event: "app_prune_finished", days: 1, pruned: 1 });
    expect((await readdir(directory)).sort()).toEqual(["final.png", "record.json", "ui.xml"]);
    const [pruned] = await w.records("b11");
    expect(pruned).toMatchObject({
      status: "valid",
      video: { prunedAt: "2026-11-18T04:00:00.000Z" },
    });
  });
});
