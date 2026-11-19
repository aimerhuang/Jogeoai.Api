import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { AppAdapter, createLoginProbe } from "./app-channel/adapter.ts";
import { AppiumDrivers } from "./app-channel/appium-driver.ts";
import {
  bindingsFilePath,
  loadBindingsFile,
  resolveBindings,
  type AppPlatformCode,
} from "./app-channel/bindings.ts";
import { checkBindings, type LoginProbe } from "./app-channel/check.ts";
import { systemCommandRunner, type CommandRunner } from "./app-channel/command.ts";
import { ContractSource } from "./app-channel/contract.ts";
import { AppControlPlaneClient } from "./app-channel/control-plane.ts";
import { DeviceInspector } from "./app-channel/devices.ts";
import type { DriverProvider } from "./app-channel/driver.ts";
import { loadAppChannelEnv, type AppChannelEnv } from "./app-channel/env.ts";
import { EvidenceStore, batchIdSchema, sha256Text } from "./app-channel/evidence.ts";
import { AppSetupError, type AppSetupIssue } from "./app-channel/failure.ts";
import { RateGate } from "./app-channel/rate-gate.ts";
import { ScreenRecorder, assertDiskSpace, disabledScreenRecorder } from "./app-channel/recorder.ts";
import { runNode } from "./app-channel/node.ts";
import { pruneVideos, writeBatchReport, type BatchReport } from "./app-channel/report.ts";
import { pendingCells, questionHash, runBatch } from "./app-channel/scheduler.ts";
import { realSleep, type Sleep } from "./app-channel/time.ts";
import { loadApprovedQuestions } from "./approval-guard.ts";
import { CollectorError } from "./platform-adapter.ts";

// App-channel script (design §11):
//
//   check                        read-only self-check of every binding
//   run --batch <id> [...]       run or resume a local trial batch (attended)
//   serve                        control-plane mode: resident node that leases tasks from the
//                                Jogeoai control plane and submits answers (task
//                                11-16-app-collector-control-plane; explicit opt-in)
//   report --batch <id>          batch summary and per-platform conclusion
//   prune [--days <n>]           delete videos past the retention period
//
// Exit codes: 0 done; 1 usage, configuration or environment problem; 2 an enabled binding does
// not pass the self-check (for `run`: no binding in scope can work, nothing was attempted);
// 3 `run` was stopped by a signal. `serve` ends with 0 after a stop request.
export const APP_COLLECT_EXIT_CODES = {
  finished: 0,
  failed: 1,
  checkFailed: 2,
  interrupted: 3,
} as const;

const AVAILABLE_COMMANDS = ["check", "run", "serve", "report", "prune"] as const;

type Log = (line: Record<string, unknown>) => void;
export interface AppCollectOptions {
  argv: readonly string[];
  env?: NodeJS.ProcessEnv;
  runner?: CommandRunner;
  // Replaces the contract-based login check of `check`.
  probeLogin?: LoginProbe;
  // Device automation; default: the script's own Appium server, started on first use.
  drivers?: DriverProvider;
  // Stop request (SIGINT/SIGTERM).
  signal?: AbortSignal;
  now?: () => Date;
  sleep?: Sleep;
  diskStat?: Parameters<typeof assertDiskSpace>[2];
  // `serve`: HTTP client of the control plane.
  fetch?: typeof fetch;
  log?: Log;
  logError?: Log;
}

// Structured JSON only: identifiers, classes and counts. Never question text, answers or
// on-screen text.
const SERVICE = "collector-app";
const defaultLog: Log = (line) =>
  console.log(
    JSON.stringify({ level: "info", service: SERVICE, time: new Date().toISOString(), ...line }),
  );
const defaultLogError: Log = (line) =>
  console.error(
    JSON.stringify({ level: "error", service: SERVICE, time: new Date().toISOString(), ...line }),
  );

export async function runAppCollect(options: AppCollectOptions): Promise<number> {
  const log = options.log ?? defaultLog;
  const logError = options.logError ?? defaultLogError;
  try {
    switch (options.argv[0]) {
      case "check":
        return await check(options, log);
      case "run":
        return await run(options, log);
      case "serve":
        return await serve(options, log);
      case "report":
        return await report(options, log);
      case "prune":
        return await prune(options, log);
      default:
        throw new AppSetupError("invalid_command");
    }
  } catch (error) {
    if (!(error instanceof AppSetupError)) throw error;
    logError({
      event: "app_collect_failed",
      code: error.code,
      ...(error.code === "invalid_command" ? { availableCommands: AVAILABLE_COMMANDS } : {}),
      ...(error.issues.length > 0 ? { issues: error.issues } : {}),
    });
    return APP_COLLECT_EXIT_CODES.failed;
  }
}

const usage = (path: string, message: string) =>
  new AppSetupError("invalid_command", [{ path, message }]);

type OptionSpec = NonNullable<Parameters<typeof parseArgs>[0]>["options"];
function parseOptions<T extends NonNullable<OptionSpec>>(argv: readonly string[], spec: T) {
  try {
    return parseArgs({ args: argv.slice(1), options: spec, strict: true, allowPositionals: false })
      .values;
  } catch {
    throw usage("arguments", "unknown or malformed option");
  }
}
function batchArgument(value: unknown): string {
  const parsed = batchIdSchema.safeParse(value);
  if (!parsed.success) throw usage("--batch", "is required: letters, digits, '-' and '_'");
  return parsed.data;
}
function positiveInteger(value: string | undefined, name: string): number | undefined {
  if (value === undefined) return undefined;
  if (!/^[1-9]\d{0,5}$/u.test(value)) throw usage(name, "must be a positive integer");
  return Number(value);
}
function required<K extends keyof AppChannelEnv>(
  env: AppChannelEnv,
  keys: readonly K[],
): AppChannelEnv & { [P in K]-?: NonNullable<AppChannelEnv[P]> } {
  const issues: AppSetupIssue[] = keys
    .filter((key) => env[key] === undefined)
    .map((key) => ({ path: key, message: "is required for this command" }));
  if (issues.length > 0) throw new AppSetupError("environment_invalid", issues);
  return env as AppChannelEnv & { [P in K]-?: NonNullable<AppChannelEnv[P]> };
}
const unique = <T>(values: readonly T[]) => [...new Set(values)];

function inspectorFor(options: AppCollectOptions, env: AppChannelEnv, devices: InspectorDevices) {
  return new DeviceInspector({
    runner: options.runner ?? systemCommandRunner,
    devices,
    adbPath: env.APP_COLLECTOR_ADB_PATH,
  });
}
type InspectorDevices = ConstructorParameters<typeof DeviceInspector>[0]["devices"];
function driversFor(options: AppCollectOptions, env: AppChannelEnv): DriverProvider {
  return (
    options.drivers ??
    new AppiumDrivers({
      runner: options.runner ?? systemCommandRunner,
      env: options.env ?? process.env,
      port: env.APP_COLLECTOR_APPIUM_PORT,
      logFile: env.APP_COLLECTOR_APPIUM_LOG_FILE,
      sleep: options.sleep,
    })
  );
}

// Read-only (PRD FR-A2). The bindings file is validated before the first adb command. With a
// contract directory each passing binding's App is also opened to look for its logged-in marker;
// without one login stays `not_checked` and no automation server is started.
async function check(options: AppCollectOptions, log: Log): Promise<number> {
  const env = loadAppChannelEnv(options.env);
  const { config } = await loadBindingsFile(bindingsFilePath(env));
  let drivers: DriverProvider | undefined;
  let probeLogin = options.probeLogin;
  if (!probeLogin && env.APP_COLLECTOR_CONTRACT_DIR) {
    drivers = driversFor(options, env);
    probeLogin = createLoginProbe({
      contracts: ContractSource.fromDirectory(env.APP_COLLECTOR_CONTRACT_DIR, log),
      drivers,
      now: options.now,
      sleep: options.sleep,
      signal: options.signal,
      log,
    });
  }
  let results;
  try {
    results = await checkBindings({
      bindings: resolveBindings(config),
      inspector: inspectorFor(options, env, config.devices),
      probeLogin,
    });
  } finally {
    await drivers?.close().catch(() => undefined);
  }
  for (const result of results) log({ event: "app_binding_check", ...result });
  const count = (status: string) => results.filter((result) => result.status === status).length;
  const failed = count("fail");
  log({
    event: "app_check_summary",
    bindings: results.length,
    passed: count("pass"),
    failed,
    disabled: count("disabled"),
    loginChecked: probeLogin !== undefined,
  });
  return failed > 0 ? APP_COLLECT_EXIT_CODES.checkFailed : APP_COLLECT_EXIT_CODES.finished;
}

// The Web channel's approved list (or its built-in smoke list): nothing else is ever sent, and
// each question is sent as it is, without the Web channel's prefix.
async function approvedQuestions(env: AppChannelEnv): Promise<readonly string[]> {
  if (env.COLLECTOR_APPROVED_QUESTIONS_FILE)
    required(env, ["COLLECTOR_TENANT_ID", "COLLECTOR_EXPECTED_PROJECT_ID"]);
  try {
    return await loadApprovedQuestions({
      COLLECTOR_APPROVED_QUESTIONS_FILE: env.COLLECTOR_APPROVED_QUESTIONS_FILE,
      COLLECTOR_TENANT_ID: env.COLLECTOR_TENANT_ID ?? "",
      COLLECTOR_EXPECTED_PROJECT_ID: env.COLLECTOR_EXPECTED_PROJECT_ID,
    });
  } catch (error) {
    if (error instanceof CollectorError) throw new AppSetupError("approved_questions_invalid");
    throw error;
  }
}

// Commit of this checkout, "-dirty" when the working tree differs; "unknown" without git.
async function scriptRevision(env: AppChannelEnv, runner: CommandRunner): Promise<string> {
  if (env.APP_COLLECTOR_SCRIPT_REVISION) return env.APP_COLLECTOR_SCRIPT_REVISION;
  const directory = fileURLToPath(new URL(".", import.meta.url));
  const head = await runner.run("git", ["-C", directory, "rev-parse", "--short=12", "HEAD"]);
  const commit = head.stdout.trim();
  if (head.code !== 0 || !/^[0-9a-f]{7,40}$/u.test(commit)) return "unknown";
  const status = await runner.run("git", ["-C", directory, "status", "--porcelain"]);
  return status.code === 0 && status.stdout.trim() === "" ? commit : `${commit}-dirty`;
}

function logReport(summary: BatchReport, log: Log): void {
  for (const platform of summary.platforms) log({ event: "app_report_platform", ...platform });
  for (const binding of summary.bindings) log({ event: "app_report_binding", ...binding });
  log({ event: "app_report_summary", batchId: summary.batchId, ...summary.totals });
}

async function run(options: AppCollectOptions, log: Log): Promise<number> {
  const args = parseOptions(options.argv, {
    batch: { type: "string" },
    platform: { type: "string", multiple: true },
    device: { type: "string", multiple: true },
    question: { type: "string", multiple: true },
    limit: { type: "string" },
  });
  const batchId = batchArgument(args.batch);
  const limit = positiveInteger(args.limit, "--limit");
  const env = required(loadAppChannelEnv(options.env), [
    "APP_COLLECTOR_EVIDENCE_DIR",
    "APP_COLLECTOR_CONTRACT_DIR",
  ]);
  const loaded = await loadBindingsFile(bindingsFilePath(env));
  const bindings = resolveBindings(loaded.config);

  // Scope: only names the configuration and the approved list know.
  const platforms = args.platform ? unique(args.platform) : undefined;
  const devices = args.device ? unique(args.device) : undefined;
  for (const platform of platforms ?? [])
    if (!Object.hasOwn(loaded.config.platforms, platform))
      throw usage("--platform", "is not a platform of the bindings file");
  for (const device of devices ?? [])
    if (!loaded.config.devices.some((declared) => declared.id === device))
      throw usage("--device", "is not a device of the bindings file");
  const approved = await approvedQuestions(env);
  const questions = args.question ? unique(args.question) : approved;
  if (questions.some((question) => !approved.includes(question)))
    throw usage("--question", "must be the exact text of an approved question");

  const now = options.now ?? (() => new Date());
  const runner = options.runner ?? systemCommandRunner;
  const store = await EvidenceStore.open(env.APP_COLLECTOR_EVIDENCE_DIR);
  let drivers: DriverProvider | undefined;
  try {
    await assertDiskSpace(store.root, env.APP_COLLECTOR_MIN_FREE_GB * 1024 ** 3, options.diskStat);

    // Bindings check (read-only): only bindings that pass take part in this run.
    const scoped = bindings.filter(
      (binding) =>
        (platforms === undefined || platforms.includes(binding.platformCode)) &&
        (devices === undefined || devices.includes(binding.device.id)),
    );
    const inspector = inspectorFor(options, env, loaded.config.devices);
    const checks = await checkBindings({ bindings: scoped, inspector });
    for (const result of checks) log({ event: "app_binding_check", ...result });
    // Platforms judged unavailable stay out until an operator removes them from the file.
    const unavailable = (await store.readPlatformStatus()).platforms;
    for (const platform of unique(scoped.map((binding) => binding.platformCode)))
      if (unavailable[platform])
        log({
          event: "app_platform_skipped",
          batchId,
          platform,
          reason: unavailable[platform].failureCode,
        });
    const working = scoped.filter(
      (binding, index) => checks[index]!.status === "pass" && !unavailable[binding.platformCode],
    );

    // The plan covers every enabled binding in scope, so a platform that cannot work now shows
    // up as not collected instead of disappearing.
    const plannedPlatforms = unique(
      scoped.filter((binding) => binding.enabled).map((binding) => binding.platformCode),
    );
    drivers = driversFor(options, env);
    const tools = await drivers.describe();
    const revision = await scriptRevision(env, runner);
    await store.recordBatchRun(
      batchId,
      {
        startedAt: now().toISOString(),
        scope: {
          platforms: (platforms as AppPlatformCode[] | undefined) ?? null,
          devices: devices ?? null,
          questions: args.question ? questions.length : null,
          limit: limit ?? null,
        },
        approvedQuestionsSha256: sha256Text(JSON.stringify(approved)),
        bindingsSha256: loaded.sha256,
        scriptRevision: revision,
        tools: tools.tools,
        helperPackages: tools.helperPackages,
      },
      plannedPlatforms.flatMap((platformCode) =>
        questions.map((question) => ({ platformCode, questionSha256: questionHash(question) })),
      ),
    );

    let interrupted = false;
    if (working.length > 0) {
      const gate = new RateGate({
        ledger: await store.readLedger(),
        bindingStatus: await store.readBindingStatus(),
        now,
      });
      const adapter = adapterFor(options, env, { store, gate, inspector, drivers, revision, log });
      const result = await runBatch({
        batchId,
        bindings: working,
        cells: pendingCells(
          questions,
          unique(working.map((binding) => binding.platformCode)),
          await store.readRecords(batchId),
        ),
        gate,
        attempt: adapter.collect,
        stop: options.signal,
        limit,
        now,
        sleep: options.sleep ?? realSleep,
        log,
      });
      interrupted = result.interrupted;
    }
    logReport(await writeBatchReport(store, batchId, bindings, now()), log);
    if (interrupted) return APP_COLLECT_EXIT_CODES.interrupted;
    return working.length > 0
      ? APP_COLLECT_EXIT_CODES.finished
      : APP_COLLECT_EXIT_CODES.checkFailed;
  } finally {
    await drivers?.close().catch(() => undefined);
    await store.close();
  }
}

interface AdapterParts {
  store: EvidenceStore;
  gate?: RateGate;
  inspector: DeviceInspector;
  drivers: DriverProvider;
  revision: string;
  log: Log;
}
function adapterFor(
  options: AppCollectOptions,
  env: AppChannelEnv & { APP_COLLECTOR_CONTRACT_DIR: string },
  parts: AdapterParts,
): AppAdapter {
  const now = options.now ?? (() => new Date());
  return new AppAdapter({
    store: parts.store,
    gate: parts.gate,
    // Off by default: no recorder command and no ffprobe are run.
    recorder:
      env.APP_COLLECTOR_RECORD_SCREEN === "on"
        ? new ScreenRecorder({
            runner: options.runner ?? systemCommandRunner,
            adbPath: env.APP_COLLECTOR_ADB_PATH,
            ffprobePath: env.APP_COLLECTOR_FFPROBE_PATH,
            size: env.APP_COLLECTOR_RECORD_SIZE,
            bitRate: env.APP_COLLECTOR_RECORD_BIT_RATE,
            now,
            sleep: options.sleep,
          })
        : disabledScreenRecorder(now),
    inspector: parts.inspector,
    contracts: ContractSource.fromDirectory(env.APP_COLLECTOR_CONTRACT_DIR, parts.log),
    drivers: parts.drivers,
    scriptRevision: parts.revision,
    timings: { completionTimeoutMs: env.APP_COLLECTOR_COLLECTION_TIMEOUT_SECONDS * 1_000 },
    now,
    sleep: options.sleep,
    log: parts.log,
  });
}

// Control-plane mode (task 11-16-app-collector-control-plane design §1, §4). Runs until a stop
// request; only questions of leased tasks are sent. Nothing here handles a login, CAPTCHA or device
// verification: such a binding is reported and stays stopped until an operator has dealt with it.
async function serve(options: AppCollectOptions, log: Log): Promise<number> {
  parseOptions(options.argv, {});
  const env = required(loadAppChannelEnv(options.env), [
    "APP_COLLECTOR_EVIDENCE_DIR",
    "APP_COLLECTOR_CONTRACT_DIR",
    "APP_COLLECTOR_API_URL",
    "APP_COLLECTOR_NODE_ID",
    "APP_COLLECTOR_NODE_TOKEN",
  ]);
  const loaded = await loadBindingsFile(bindingsFilePath(env));
  const bindings = resolveBindings(loaded.config);
  const now = options.now ?? (() => new Date());
  const runner = options.runner ?? systemCommandRunner;
  const store = await EvidenceStore.open(env.APP_COLLECTOR_EVIDENCE_DIR);
  let drivers: DriverProvider | undefined;
  try {
    await assertDiskSpace(store.root, env.APP_COLLECTOR_MIN_FREE_GB * 1024 ** 3, options.diskStat);
    const inspector = inspectorFor(options, env, loaded.config.devices);
    drivers = driversFor(options, env);
    const revision = await scriptRevision(env, runner);
    const adapter = adapterFor(options, env, { store, inspector, drivers, revision, log });
    log({
      event: "app_serve_started",
      nodeId: env.APP_COLLECTOR_NODE_ID,
      bindingsSha256: loaded.sha256,
      revision,
    });
    await runNode({
      nodeId: env.APP_COLLECTOR_NODE_ID,
      controlPlane: new AppControlPlaneClient(
        {
          apiUrl: env.APP_COLLECTOR_API_URL,
          nodeToken: env.APP_COLLECTOR_NODE_TOKEN,
          timeoutMs: env.APP_COLLECTOR_HTTP_TIMEOUT_MS,
        },
        options.fetch,
      ),
      store,
      bindings,
      inspector,
      attempt: adapter.collect,
      revision,
      stop: options.signal ?? new AbortController().signal,
      leaseSeconds: env.APP_COLLECTOR_LEASE_SECONDS,
      now,
      sleep: options.sleep,
      log,
    });
    return APP_COLLECT_EXIT_CODES.finished;
  } finally {
    await drivers?.close().catch(() => undefined);
    await store.close();
  }
}

async function report(options: AppCollectOptions, log: Log): Promise<number> {
  const args = parseOptions(options.argv, { batch: { type: "string" } });
  const batchId = batchArgument(args.batch);
  const env = required(loadAppChannelEnv(options.env), ["APP_COLLECTOR_EVIDENCE_DIR"]);
  const { config } = await loadBindingsFile(bindingsFilePath(env));
  const store = await EvidenceStore.open(env.APP_COLLECTOR_EVIDENCE_DIR);
  try {
    if (!(await store.listBatchIds()).includes(batchId))
      throw usage("--batch", "is not a batch of the evidence directory");
    logReport(
      await writeBatchReport(
        store,
        batchId,
        resolveBindings(config),
        (options.now ?? (() => new Date()))(),
      ),
      log,
    );
    return APP_COLLECT_EXIT_CODES.finished;
  } finally {
    await store.close();
  }
}

async function prune(options: AppCollectOptions, log: Log): Promise<number> {
  const args = parseOptions(options.argv, { days: { type: "string" } });
  const requested = positiveInteger(args.days, "--days");
  const env = required(loadAppChannelEnv(options.env), ["APP_COLLECTOR_EVIDENCE_DIR"]);
  const days = requested ?? env.APP_COLLECTOR_VIDEO_RETENTION_DAYS;
  const store = await EvidenceStore.open(env.APP_COLLECTOR_EVIDENCE_DIR);
  try {
    const result = await pruneVideos(store, { days, now: (options.now ?? (() => new Date()))() });
    log({ event: "app_prune_finished", days, ...result });
    return APP_COLLECT_EXIT_CODES.finished;
  } finally {
    await store.close();
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  // The first signal asks for an orderly stop: no new attempt, attempts in flight end in a
  // record, recorders and the automation server are stopped and the evidence lock is released.
  // Further signals are ignored so that clean-up is never cut short.
  const stop = new AbortController();
  const onSignal = () => stop.abort();
  process.on("SIGINT", onSignal);
  process.on("SIGTERM", onSignal);
  try {
    process.exitCode = await runAppCollect({ argv: process.argv.slice(2), signal: stop.signal });
  } catch {
    // Unexpected errors may carry device or file text; only a fixed code is printed.
    defaultLogError({ event: "app_collect_failed", code: "unexpected_error" });
    process.exitCode = APP_COLLECT_EXIT_CODES.failed;
  } finally {
    process.off("SIGINT", onSignal);
    process.off("SIGTERM", onSignal);
  }
}
