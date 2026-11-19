import { mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { OperatorNotice } from "./notify.ts";
import {
  type PoolChild,
  type PoolChildExit,
  type PoolEntry,
  childEnvironment,
  decideRestart,
  loadPoolConfig,
  loadPoolFile,
  runPool,
} from "./pool.ts";

const directories: string[] = [];
afterEach(async () => {
  for (const directory of directories.splice(0))
    await rm(directory, { recursive: true, force: true });
});

const PROFILE_A = "11111111-1111-4111-8111-111111111111";
const PROFILE_B = "22222222-2222-4222-8222-222222222222";

function entry(overrides: Partial<PoolEntry> = {}): PoolEntry {
  return {
    sessionProfileId: PROFILE_A,
    platform: "doubao",
    profileDirectory: "/var/aivc/profiles/doubao-a",
    spoolDirectory: "/var/aivc/spools/doubao-a",
    ...overrides,
  };
}

async function poolFile(content: string): Promise<string> {
  const directory = await mkdtemp(join(tmpdir(), "aivc-pool-"));
  directories.push(directory);
  const path = join(directory, "pool.json");
  await writeFile(path, content, { mode: 0o600 });
  return path;
}

describe("pool restart policy", () => {
  const policy = { idleMs: 60_000, failureBackoffMs: 30_000, maxFailureBackoffMs: 600_000 };

  it("restarts after the idle delay when a collector finished", () => {
    expect(decideRestart({ code: 0, signal: null }, 4, policy)).toEqual({
      kind: "idle",
      delayMs: 60_000,
    });
  });

  it("never restarts an account that needs the operator", () => {
    expect(decideRestart({ code: 3, signal: null }, 0, policy)).toEqual({ kind: "stop" });
  });

  it("backs off failures exponentially up to the cap, including signal deaths", () => {
    const delays = [0, 1, 2, 3, 4, 5, 30].map(
      (failures) =>
        decideRestart({ code: 1, signal: null }, failures, policy) as { delayMs: number },
    );
    expect(delays.map((decision) => decision.delayMs)).toEqual([
      30_000, 60_000, 120_000, 240_000, 480_000, 600_000, 600_000,
    ]);
    expect(decideRestart({ code: null, signal: "SIGKILL" }, 0, policy)).toEqual({
      kind: "backoff",
      delayMs: 30_000,
    });
  });
});

describe("pool file validation", () => {
  it("accepts profile-directory and CDP entries", async () => {
    const path = await poolFile(
      JSON.stringify([
        entry({ label: "doubao-a" }),
        entry({
          sessionProfileId: PROFILE_B,
          profileDirectory: "/var/aivc/profiles/doubao-b",
          spoolDirectory: "/var/aivc/spools/doubao-b",
          cdpUrl: "http://127.0.0.1:9223",
        }),
      ]),
    );
    const entries = await loadPoolFile(path);
    expect(entries).toHaveLength(2);
    expect(entries[1]?.cdpUrl).toBe("http://127.0.0.1:9223");
  });

  it.each([
    ["empty list", []],
    ["relative profile directory", [entry({ profileDirectory: "profiles/a" })]],
    ["missing spool directory", [{ ...entry(), spoolDirectory: undefined }]],
    ["unknown key", [{ ...entry(), cookies: "x" }]],
    ["unsupported platform", [{ ...entry(), platform: "claude" }]],
    ["remote CDP endpoint", [entry({ cdpUrl: "http://192.0.2.10:9222" })]],
    ["unsafe label", [entry({ label: "alice@example.com" })]],
    ["duplicate profile id", [entry(), entry({ profileDirectory: "/b", spoolDirectory: "/c" })]],
    [
      "shared spool directory",
      [entry(), entry({ sessionProfileId: PROFILE_B, profileDirectory: "/b" })],
    ],
    [
      "spool reused as a profile directory",
      [
        entry(),
        entry({
          sessionProfileId: PROFILE_B,
          profileDirectory: "/var/aivc/spools/doubao-a",
          spoolDirectory: "/c",
        }),
      ],
    ],
  ])("rejects %s", async (_name, content) => {
    await expect(loadPoolFile(await poolFile(JSON.stringify(content)))).rejects.toThrow(
      "COLLECTOR_POOL_FILE is invalid",
    );
  });

  it("rejects relative paths, symlinks and malformed JSON without echoing content", async () => {
    await expect(loadPoolFile("pool.json")).rejects.toThrow("absolute path");
    const target = await poolFile(JSON.stringify([entry()]));
    const link = join(directories[0]!, "link.json");
    await symlink(target, link);
    await expect(loadPoolFile(link)).rejects.toThrow("not a symlink");
    const malformed = await poolFile("{secret-looking-value");
    await expect(loadPoolFile(malformed)).rejects.toThrowError(
      /^COLLECTOR_POOL_FILE is not valid JSON$/u,
    );
  });

  it("validates the pool environment", () => {
    expect(
      loadPoolConfig({ COLLECTOR_POOL_FILE: "/etc/aivc/pool.json", COLLECTOR_NODE_ID: "mac-cn" }),
    ).toMatchObject({ COLLECTOR_POOL_IDLE_MS: 60_000, COLLECTOR_POOL_STAGGER_MS: 15_000 });
    expect(
      loadPoolConfig({
        COLLECTOR_POOL_FILE: "/etc/aivc/pool.json",
        COLLECTOR_NODE_ID: "mac-cn",
        COLLECTOR_POOL_STAGGER_MS: "0",
      }),
    ).toMatchObject({ COLLECTOR_POOL_STAGGER_MS: 0 });
    expect(() =>
      loadPoolConfig({
        COLLECTOR_POOL_FILE: "/etc/aivc/pool.json",
        COLLECTOR_NODE_ID: "mac-cn",
        COLLECTOR_POOL_STAGGER_MS: "-1",
      }),
    ).toThrow("Pool environment is invalid");
    expect(() =>
      loadPoolConfig({ COLLECTOR_POOL_FILE: "pool.json", COLLECTOR_NODE_ID: "mac-cn" }),
    ).toThrow("Pool environment is invalid");
    expect(() =>
      loadPoolConfig({
        COLLECTOR_POOL_FILE: "/etc/aivc/pool.json",
        COLLECTOR_NODE_ID: "mac-cn",
        COLLECTOR_POOL_IDLE_MS: "10",
      }),
    ).toThrow("Pool environment is invalid");
  });
});

describe("child environment", () => {
  it("overrides account fields, drops pool-only, one-off and control-plane-only settings, inherits the rest", () => {
    const env = childEnvironment(
      entry({ cdpUrl: "http://127.0.0.1:9223" }),
      {
        COLLECTOR_API_URL: "http://127.0.0.1:4314",
        COLLECTOR_SERVICE_TOKEN: "inherited-token-value-0000000",
        COLLECTOR_POOL_FILE: "/etc/aivc/pool.json",
        COLLECTOR_POOL_IDLE_MS: "60000",
        COLLECTOR_POOL_STAGGER_MS: "15000",
        COLLECTOR_RETRY_TASK_ID: "33333333-3333-4333-8333-333333333333",
        COLLECTOR_SUPPORTED_PLATFORMS: "qwen",
        COLLECTOR_SESSION_PROFILE_ID: PROFILE_B,
        DATABASE_URL: "postgres://app:secret@localhost/db",
        POSTGRES_APP_PASSWORD: "secret",
        COLLECTOR_CN_SERVICE_TOKEN: "cn-api-token-value-000000000",
        COLLECTOR_OVERSEAS_SERVICE_TOKEN: "overseas-api-token-value-0000",
      },
      "mac-cn",
    );
    expect(env).toEqual({
      COLLECTOR_API_URL: "http://127.0.0.1:4314",
      COLLECTOR_SERVICE_TOKEN: "inherited-token-value-0000000",
      COLLECTOR_SESSION_PROFILE_ID: PROFILE_A,
      COLLECTOR_PROFILE_DIRECTORY: "/var/aivc/profiles/doubao-a",
      COLLECTOR_SPOOL_DIRECTORY: "/var/aivc/spools/doubao-a",
      COLLECTOR_SUPPORTED_PLATFORMS: "doubao",
      COLLECTOR_NODE_ID: "mac-cn-11111111",
      COLLECTOR_BROWSER_CDP_URL: "http://127.0.0.1:9223",
    });
  });

  it("does not leak an inherited CDP endpoint into a profile-directory entry", () => {
    const env = childEnvironment(
      entry(),
      { COLLECTOR_BROWSER_CDP_URL: "http://127.0.0.1:9222" },
      "n",
    );
    expect(env.COLLECTOR_BROWSER_CDP_URL).toBeUndefined();
  });
});

interface FakeChild extends PoolChild {
  finish(exit: PoolChildExit): void;
  killedWith: NodeJS.Signals[];
}

function fakeChild(autoExit?: PoolChildExit): FakeChild {
  let finish!: (exit: PoolChildExit) => void;
  const exited = new Promise<PoolChildExit>((resolve) => {
    finish = resolve;
  });
  const child: FakeChild = {
    exited,
    finish,
    killedWith: [],
    kill(signal) {
      child.killedWith.push(signal);
      finish({ code: null, signal });
    },
  };
  if (autoExit) finish(autoExit);
  return child;
}

describe("pool supervisor", () => {
  it("restarts per exit code and stops an account that needs the operator", async () => {
    const codes = [0, 1, 1, 0, 3];
    const spawned: NodeJS.ProcessEnv[] = [];
    const sleeps: number[] = [];
    const notices: OperatorNotice[] = [];
    const log = vi.fn();
    const result = await runPool({
      entries: [entry({ label: "doubao-a" })],
      env: { COLLECTOR_TENANT_ID: "tenant" },
      baseNodeId: "mac-cn",
      idleMs: 60_000,
      spawn: (_entry, env) => {
        spawned.push(env);
        return fakeChild({ code: codes[spawned.length - 1] ?? 0, signal: null });
      },
      log,
      notify: async (notice) => {
        notices.push(notice);
      },
      signal: new AbortController().signal,
      sleep: async (ms) => {
        sleeps.push(ms);
      },
    });
    expect(result).toEqual({ needsOperator: [PROFILE_A] });
    expect(spawned).toHaveLength(5);
    expect(spawned.every((env) => env.COLLECTOR_SESSION_PROFILE_ID === PROFILE_A)).toBe(true);
    // idle, backoff 1, backoff 2, then a success resets the failure streak.
    expect(sleeps).toEqual([60_000, 30_000, 60_000, 60_000]);
    expect(notices).toEqual([
      { platform: "doubao", reason: "pool_entry_stopped", sessionProfileId: PROFILE_A },
    ]);
    expect(log).toHaveBeenCalledWith(
      expect.objectContaining({ event: "pool_entry_stopped", sessionProfileId: PROFILE_A }),
    );
  });

  it("starts a new failure streak after a child stayed up longer than the maximum backoff", async () => {
    // Uptimes per child: short, short, long (healthy resident run), short; then the operator stop.
    const uptimes = [1_000, 1_000, 3_600_000, 1_000, 0];
    const codes = [1, 1, 1, 1, 3];
    let clock = 0;
    let spawnedCount = 0;
    const sleeps: number[] = [];
    await runPool({
      entries: [entry()],
      env: {},
      baseNodeId: "mac-cn",
      idleMs: 60_000,
      spawn: () => {
        const index = spawnedCount++;
        // runPool reads the clock right after spawn; the child "runs" for its uptime.
        return {
          exited: Promise.resolve().then(() => {
            clock += uptimes[index] ?? 0;
            return { code: codes[index] ?? 3, signal: null };
          }),
          kill: () => undefined,
        };
      },
      log: () => undefined,
      signal: new AbortController().signal,
      sleep: async (ms) => {
        sleeps.push(ms);
      },
      now: () => clock,
    });
    // 30 s, 60 s, then the long run resets the streak: 30 s and 60 s again (not 120 s / 240 s).
    expect(sleeps).toEqual([30_000, 60_000, 30_000, 60_000]);
  });

  it("keeps other accounts running when one stops", async () => {
    const controller = new AbortController();
    const children: FakeChild[] = [];
    const resultPromise = runPool({
      entries: [
        entry(),
        entry({
          sessionProfileId: PROFILE_B,
          profileDirectory: "/b",
          spoolDirectory: "/c",
        }),
      ],
      env: {},
      baseNodeId: "mac-cn",
      idleMs: 1_000,
      spawn: (spawnedEntry) => {
        const child = fakeChild(
          spawnedEntry.sessionProfileId === PROFILE_A ? { code: 3, signal: null } : undefined,
        );
        children.push(child);
        return child;
      },
      log: () => undefined,
      signal: controller.signal,
      sleep: async () => undefined,
    });
    await vi.waitFor(() => expect(children).toHaveLength(2));
    controller.abort("SIGINT");
    const result = await resultPromise;
    expect(result).toEqual({ needsOperator: [PROFILE_A] });
    expect(children[1]?.killedWith).toEqual(["SIGINT"]);
  });

  it("forwards one shutdown signal to running children and waits for them", async () => {
    const controller = new AbortController();
    const children: FakeChild[] = [];
    let settled = false;
    const resultPromise = runPool({
      entries: [entry()],
      env: {},
      baseNodeId: "mac-cn",
      idleMs: 1_000,
      spawn: () => {
        const child = fakeChild();
        // Simulates graceful cleanup: exit is reported after the signal, not synchronously.
        child.kill = (signal) => {
          child.killedWith.push(signal);
          setTimeout(() => child.finish({ code: 0, signal: null }), 5);
        };
        children.push(child);
        return child;
      },
      log: () => undefined,
      signal: controller.signal,
    }).then((result) => {
      settled = true;
      return result;
    });
    await vi.waitFor(() => expect(children).toHaveLength(1));
    controller.abort("SIGTERM");
    expect(settled).toBe(false);
    expect(await resultPromise).toEqual({ needsOperator: [] });
    expect(children).toHaveLength(1);
    expect(children[0]?.killedWith).toEqual(["SIGTERM"]);
  });

  it("stops waiting between restarts as soon as shutdown is requested", async () => {
    const controller = new AbortController();
    let spawns = 0;
    const result = await runPool({
      entries: [entry()],
      env: {},
      baseNodeId: "mac-cn",
      idleMs: 3_600_000,
      spawn: () => {
        spawns += 1;
        setTimeout(() => controller.abort("SIGINT"), 5);
        return fakeChild({ code: 0, signal: null });
      },
      log: () => undefined,
      signal: controller.signal,
    });
    expect(result).toEqual({ needsOperator: [] });
    expect(spawns).toBe(1);
  });

  it("staggers the first start of each entry and skips it on shutdown", async () => {
    const PROFILE_C = "33333333-3333-4333-8333-333333333333";
    const entries = [
      entry(),
      entry({ sessionProfileId: PROFILE_B, profileDirectory: "/b", spoolDirectory: "/b-spool" }),
      entry({ sessionProfileId: PROFILE_C, profileDirectory: "/c", spoolDirectory: "/c-spool" }),
    ];
    const start = async (abortOnStagger: boolean) => {
      const events: string[] = [];
      const controller = new AbortController();
      const result = await runPool({
        entries,
        env: {},
        baseNodeId: "mac-cn",
        idleMs: 1_000,
        staggerMs: 15_000,
        spawn: (spawned) => {
          events.push(`spawn:${spawned.sessionProfileId.slice(0, 1)}`);
          return fakeChild({ code: 3, signal: null });
        },
        log: () => undefined,
        signal: controller.signal,
        sleep: async (ms) => {
          events.push(`sleep:${ms}`);
          if (abortOnStagger) controller.abort("SIGTERM");
        },
      });
      return { events, result };
    };
    // Entry i starts i × 15 s after the pool: the first immediately, the others in order.
    expect(await start(false)).toEqual({
      events: ["spawn:1", "sleep:15000", "sleep:30000", "spawn:2", "spawn:3"],
      result: { needsOperator: [PROFILE_A, PROFILE_B, PROFILE_C] },
    });
    // Shutdown during the stagger: waiting entries are never started.
    expect(await start(true)).toEqual({
      events: ["spawn:1", "sleep:15000", "sleep:30000"],
      result: { needsOperator: [] },
    });
  });
});
