import { spawn } from "node:child_process";
import { lstat, readFile } from "node:fs/promises";
import { isAbsolute, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { fileURLToPath } from "node:url";
import { platformCodeSchema } from "@aivc/contracts";
import { z } from "zod";
import { createNotifier, type Notifier } from "./notify.ts";
import { COLLECTOR_EXIT_CODES } from "./runner.ts";

// Account-pool supervisor (design §9): one collector child per locally configured account.
// It never decides who may work; the control plane's lease transaction does. It only keeps
// each child running and stops restarting an account that needs a human.

const absolutePath = z
  .string()
  .min(1)
  .max(1024)
  .refine(isAbsolute, "must be an absolute path")
  .transform((value) => resolve(value));

export const poolEntrySchema = z
  .object({
    sessionProfileId: z.uuid(),
    // `claude` has no consumer-Web adapter; children would reject it at startup anyway.
    platform: platformCodeSchema.exclude(["claude"]),
    // Always required: it holds the Chromium profile, or with `cdpUrl` the attempt ledger.
    profileDirectory: absolutePath,
    // Spools are bound to one node ID and locked by one process, so each child needs its own.
    spoolDirectory: absolutePath,
    cdpUrl: z
      .url()
      .refine((value) => {
        const url = new URL(value);
        return (
          ["http:", "https:"].includes(url.protocol) &&
          ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname)
        );
      }, "cdpUrl must be a local http(s) endpoint")
      .optional(),
    // Operator alias for logs only; never account plaintext.
    label: z
      .string()
      .regex(/^[A-Za-z0-9_.-]{1,64}$/u)
      .optional(),
  })
  .strict();

export type PoolEntry = z.infer<typeof poolEntrySchema>;

export const poolFileSchema = z
  .array(poolEntrySchema)
  .min(1)
  .max(50)
  .superRefine((entries, context) => {
    const seen = new Map<string, string>();
    const claim = (kind: string, value: string, index: number, field: string) => {
      const key = `${kind}:${value}`;
      if (seen.has(key)) {
        context.addIssue({
          code: "custom",
          path: [index, field],
          message: `duplicates ${seen.get(key)}`,
        });
      } else seen.set(key, `entry ${index} ${field}`);
    };
    entries.forEach((entry, index) => {
      claim("profile", entry.sessionProfileId, index, "sessionProfileId");
      // Profile and spool directories share one namespace: no directory may serve two roles.
      claim("directory", entry.profileDirectory, index, "profileDirectory");
      claim("directory", entry.spoolDirectory, index, "spoolDirectory");
      if (entry.cdpUrl) claim("cdp", entry.cdpUrl, index, "cdpUrl");
      if (entry.label) claim("label", entry.label, index, "label");
    });
  });

const MAX_POOL_FILE_BYTES = 256 * 1024;

export class PoolConfigError extends Error {
  public constructor(
    message: string,
    public readonly issues: { path: string; message: string }[] = [],
  ) {
    super(message);
    this.name = "PoolConfigError";
  }
}

export async function loadPoolFile(path: string): Promise<PoolEntry[]> {
  if (!isAbsolute(path)) throw new PoolConfigError("COLLECTOR_POOL_FILE must be an absolute path");
  let raw: string;
  try {
    const stats = await lstat(path);
    if (!stats.isFile() || stats.isSymbolicLink()) {
      throw new PoolConfigError("COLLECTOR_POOL_FILE must be a regular file, not a symlink");
    }
    if (stats.size > MAX_POOL_FILE_BYTES) {
      throw new PoolConfigError("COLLECTOR_POOL_FILE is too large");
    }
    raw = await readFile(path, "utf8");
  } catch (error) {
    if (error instanceof PoolConfigError) throw error;
    throw new PoolConfigError("COLLECTOR_POOL_FILE cannot be read");
  }
  let json: unknown;
  try {
    json = JSON.parse(raw);
  } catch {
    throw new PoolConfigError("COLLECTOR_POOL_FILE is not valid JSON");
  }
  const parsed = poolFileSchema.safeParse(json);
  if (!parsed.success) {
    // Paths and validator messages only; never echo the file's values.
    throw new PoolConfigError(
      "COLLECTOR_POOL_FILE is invalid",
      parsed.error.issues.map((issue) => ({
        path: issue.path.map(String).join("."),
        message: issue.message,
      })),
    );
  }
  return parsed.data;
}

export const poolConfigSchema = z.object({
  COLLECTOR_POOL_FILE: absolutePath,
  COLLECTOR_POOL_IDLE_MS: z.coerce.number().int().min(1_000).max(3_600_000).default(60_000),
  // Gap between starting consecutive entries, so accounts do not load their pages together.
  COLLECTOR_POOL_STAGGER_MS: z.coerce.number().int().min(0).max(600_000).default(15_000),
  // Children append `-<first 8 chars of the profile id>`; keep the result within 128 chars.
  COLLECTOR_NODE_ID: z.string().min(1).max(119),
  COLLECTOR_NOTIFY_COMMAND: absolutePath.optional(),
});

export type PoolConfig = z.infer<typeof poolConfigSchema>;

export function loadPoolConfig(env: NodeJS.ProcessEnv = process.env): PoolConfig {
  const parsed = poolConfigSchema.safeParse(env);
  if (!parsed.success) {
    throw new PoolConfigError(
      "Pool environment is invalid",
      parsed.error.issues.map((issue) => ({
        path: issue.path.map(String).join("."),
        message: issue.message,
      })),
    );
  }
  return parsed.data;
}

export function childNodeId(baseNodeId: string, entry: PoolEntry): string {
  return `${baseNodeId}-${entry.sessionProfileId.slice(0, 8)}`;
}

// Control-plane-only secrets a shell sourced from the repository `.env` may carry. A collector
// never receives database credentials or the per-region API tokens (it gets only its own
// COLLECTOR_SERVICE_TOKEN); quality-guidelines.md "Region-bound Collector control-plane APIs".
const CONTROL_PLANE_ONLY_VARIABLES = [
  "DATABASE_URL",
  "COLLECTOR_CN_SERVICE_TOKEN",
  "COLLECTOR_OVERSEAS_SERVICE_TOKEN",
] as const;

// Everything else (API, token, tenant, region, page contracts, spool key, ...) is inherited.
export function childEnvironment(
  entry: PoolEntry,
  parentEnv: NodeJS.ProcessEnv,
  baseNodeId: string,
): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = { ...parentEnv };
  for (const name of CONTROL_PLANE_ONLY_VARIABLES) delete env[name];
  for (const name of Object.keys(env)) if (name.startsWith("POSTGRES_")) delete env[name];
  delete env.COLLECTOR_POOL_FILE;
  delete env.COLLECTOR_POOL_IDLE_MS;
  delete env.COLLECTOR_POOL_STAGGER_MS;
  // A targeted single-task retry is an attended one-off, never something every child repeats.
  delete env.COLLECTOR_RETRY_TASK_ID;
  delete env.COLLECTOR_BROWSER_CDP_URL;
  env.COLLECTOR_SESSION_PROFILE_ID = entry.sessionProfileId;
  env.COLLECTOR_PROFILE_DIRECTORY = entry.profileDirectory;
  env.COLLECTOR_SPOOL_DIRECTORY = entry.spoolDirectory;
  env.COLLECTOR_SUPPORTED_PLATFORMS = entry.platform;
  env.COLLECTOR_NODE_ID = childNodeId(baseNodeId, entry);
  if (entry.cdpUrl) env.COLLECTOR_BROWSER_CDP_URL = entry.cdpUrl;
  return env;
}

export interface PoolChildExit {
  code: number | null;
  signal: NodeJS.Signals | null;
}

export interface PoolChild {
  readonly exited: Promise<PoolChildExit>;
  kill(signal: NodeJS.Signals): void;
}

export type SpawnCollector = (entry: PoolEntry, env: NodeJS.ProcessEnv) => PoolChild;

export type RestartDecision =
  | { kind: "idle"; delayMs: number }
  | { kind: "backoff"; delayMs: number }
  | { kind: "stop" };

export interface RestartPolicy {
  idleMs: number;
  failureBackoffMs: number;
  maxFailureBackoffMs: number;
}

// Exit codes come from COLLECTOR_EXIT_CODES in runner.ts:
//   0 -> finished: restart after the idle delay. Profile-mode children are resident (design
//        §12.3) and poll eligibility themselves, so this is only a fallback for them.
//   3 -> the account needs a human: never restart until the operator restarts the pool
//   anything else, including a signal death -> restart with doubling backoff
export function decideRestart(
  exit: PoolChildExit,
  consecutiveFailures: number,
  policy: RestartPolicy,
): RestartDecision {
  if (exit.code === COLLECTOR_EXIT_CODES.finished) return { kind: "idle", delayMs: policy.idleMs };
  if (exit.code === COLLECTOR_EXIT_CODES.needsOperator) return { kind: "stop" };
  const exponent = Math.min(Math.max(consecutiveFailures, 0), 20);
  return {
    kind: "backoff",
    delayMs: Math.min(policy.failureBackoffMs * 2 ** exponent, policy.maxFailureBackoffMs),
  };
}

// Identifiers, enum values and counts only.
export type PoolLog = (metadata: {
  event: string;
  sessionProfileId?: string;
  platform?: string;
  label?: string;
  exitCode?: number | null;
  signal?: string | null;
  delayMs?: number;
  count?: number;
}) => void;

export interface PoolOptions {
  entries: PoolEntry[];
  env: NodeJS.ProcessEnv;
  baseNodeId: string;
  idleMs: number;
  // Delay between the first start of consecutive entries (entry i starts after i × staggerMs).
  staggerMs?: number;
  failureBackoffMs?: number;
  maxFailureBackoffMs?: number;
  spawn: SpawnCollector;
  log: PoolLog;
  notify?: Notifier;
  // Abort to shut down: running children receive `signal.reason` (a signal name) or SIGTERM.
  signal: AbortSignal;
  // Must resolve (not reject) early when `signal` aborts.
  sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
  now?: () => number;
}

export interface PoolResult {
  // Profiles whose collector exited with 3 and were not restarted.
  needsOperator: string[];
}

const defaultSleep = (ms: number, signal: AbortSignal) =>
  delay(ms, undefined, { signal }).catch(() => undefined);

function shutdownSignal(signal: AbortSignal): NodeJS.Signals {
  return signal.reason === "SIGINT" || signal.reason === "SIGTERM" ? signal.reason : "SIGTERM";
}

export async function runPool(options: PoolOptions): Promise<PoolResult> {
  const policy: RestartPolicy = {
    idleMs: options.idleMs,
    failureBackoffMs: options.failureBackoffMs ?? 30_000,
    maxFailureBackoffMs: options.maxFailureBackoffMs ?? 600_000,
  };
  const sleep = options.sleep ?? defaultSleep;
  const now = options.now ?? Date.now;
  const running = new Set<PoolChild>();
  const forward = () => {
    const name = shutdownSignal(options.signal);
    for (const child of running) {
      try {
        child.kill(name);
      } catch {
        /* Already gone. */
      }
    }
  };
  options.signal.addEventListener("abort", forward, { once: true });

  const staggerMs = options.staggerMs ?? 0;
  const supervise = async (
    entry: PoolEntry,
    index: number,
  ): Promise<"shutdown" | "needs_operator"> => {
    const identity = {
      sessionProfileId: entry.sessionProfileId,
      platform: entry.platform,
      ...(entry.label ? { label: entry.label } : {}),
    };
    if (index > 0 && staggerMs > 0) {
      options.log({ event: "pool_child_staggered", ...identity, delayMs: index * staggerMs });
      await sleep(index * staggerMs, options.signal);
    }
    let failures = 0;
    while (!options.signal.aborted) {
      const child = options.spawn(entry, childEnvironment(entry, options.env, options.baseNodeId));
      const startedAt = now();
      running.add(child);
      options.log({ event: "pool_child_started", ...identity });
      const exit = await child.exited;
      running.delete(child);
      // A resident child that worked longer than the maximum backoff was healthy: its failure
      // is a new incident, not the next step of an old streak.
      if (now() - startedAt >= policy.maxFailureBackoffMs) failures = 0;
      options.log({
        event: "pool_child_exited",
        ...identity,
        exitCode: exit.code,
        signal: exit.signal,
      });
      if (options.signal.aborted) break;
      const decision = decideRestart(exit, failures, policy);
      if (decision.kind === "stop") {
        options.log({ event: "pool_entry_stopped", ...identity });
        try {
          await options.notify?.({
            platform: entry.platform,
            reason: "pool_entry_stopped",
            sessionProfileId: entry.sessionProfileId,
          });
        } catch {
          /* Notification is best-effort. */
        }
        return "needs_operator";
      }
      failures = decision.kind === "backoff" ? failures + 1 : 0;
      options.log({
        event: decision.kind === "idle" ? "pool_child_idle" : "pool_child_backoff",
        ...identity,
        delayMs: decision.delayMs,
        ...(decision.kind === "backoff" ? { count: failures } : {}),
      });
      await sleep(decision.delayMs, options.signal);
    }
    return "shutdown";
  };

  try {
    options.log({ event: "pool_started", count: options.entries.length });
    const outcomes = await Promise.all(options.entries.map(supervise));
    const needsOperator = options.entries
      .filter((_, index) => outcomes[index] === "needs_operator")
      .map((entry) => entry.sessionProfileId);
    options.log({ event: "pool_finished", count: needsOperator.length });
    return { needsOperator };
  } finally {
    options.signal.removeEventListener("abort", forward);
  }
}

const mainPath = fileURLToPath(new URL("./main.ts", import.meta.url));
const repositoryRoot = fileURLToPath(new URL("../../../", import.meta.url));

// Same entry as `pnpm collector:run` (tsx + main.ts). Children get their own process group so a
// terminal Ctrl+C reaches only the supervisor, which forwards exactly one signal: main.ts
// handles the first signal gracefully but a second one would kill it without cleanup.
export const spawnCollectorProcess: SpawnCollector = (_entry, env) => {
  const child = spawn(process.execPath, ["--import", "tsx", mainPath], {
    cwd: repositoryRoot,
    env,
    stdio: ["ignore", "inherit", "inherit"],
    detached: true,
  });
  const exited = new Promise<PoolChildExit>((resolveExit) => {
    child.once("error", () => resolveExit({ code: null, signal: null }));
    child.once("exit", (code, signal) => resolveExit({ code, signal }));
  });
  return {
    exited,
    kill(signal) {
      if (child.exitCode === null && child.signalCode === null) child.kill(signal);
    },
  };
};

async function main(): Promise<void> {
  const log: PoolLog = (metadata) =>
    console.log(JSON.stringify({ level: "info", service: "collector-pool", ...metadata }));
  let config: PoolConfig;
  let entries: PoolEntry[];
  try {
    config = loadPoolConfig();
    entries = await loadPoolFile(config.COLLECTOR_POOL_FILE);
  } catch (error) {
    console.error(
      JSON.stringify({
        level: "error",
        service: "collector-pool",
        event: "collector_pool_failed",
        message: error instanceof PoolConfigError ? error.message : "pool startup failed",
        ...(error instanceof PoolConfigError && error.issues.length > 0
          ? { issues: error.issues }
          : {}),
      }),
    );
    process.exitCode = COLLECTOR_EXIT_CODES.failed;
    return;
  }
  const controller = new AbortController();
  const onSignal = (signal: NodeJS.Signals) => {
    if (controller.signal.aborted) {
      log({ event: "pool_shutdown_pending", signal });
      return;
    }
    log({ event: "pool_shutdown_requested", signal });
    controller.abort(signal);
  };
  process.on("SIGINT", onSignal);
  process.on("SIGTERM", onSignal);
  try {
    const result = await runPool({
      entries,
      env: process.env,
      baseNodeId: config.COLLECTOR_NODE_ID,
      idleMs: config.COLLECTOR_POOL_IDLE_MS,
      staggerMs: config.COLLECTOR_POOL_STAGGER_MS,
      spawn: spawnCollectorProcess,
      log,
      notify: createNotifier({ log, command: config.COLLECTOR_NOTIFY_COMMAND }),
      signal: controller.signal,
    });
    // Every entry stopped for a human (no shutdown request): surface it as exit code 3.
    process.exitCode =
      !controller.signal.aborted && result.needsOperator.length > 0
        ? COLLECTOR_EXIT_CODES.needsOperator
        : COLLECTOR_EXIT_CODES.finished;
  } finally {
    process.removeListener("SIGINT", onSignal);
    process.removeListener("SIGTERM", onSignal);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main();
}
