import { execFile } from "node:child_process";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

import type {
  CollectorFailure,
  EligibilityRequest,
  LeaseRequest,
  LeasedTask,
} from "@aivc/contracts";
import {
  FIXTURE_IDS,
  checkSessionProfileEligibility,
  configurePlatformDispatch,
  createDatabase,
  generateDailyTasks,
  leaseTasks,
  listPlatformDispatchStates,
  listSessionProfileMetrics,
  migrateDatabase,
  recordSessionEvent,
  registerSessionProfile,
  reportCollectorFailure,
  resetDatabaseForTests,
  resumePlatformDispatch,
  seedFixture,
  setSessionProfileStatus,
  submitTaskResult,
  type DatabaseTransaction,
  type RegisterSessionProfileInput,
} from "@aivc/database";
import { afterAll, beforeAll, beforeEach, describe, expect, it } from "vitest";

const databaseUrl =
  process.env.DATABASE_URL ??
  "postgres://ai_visibility:local-development-only@localhost:55432/ai_visibility";
const database = createDatabase(databaseUrl);
const scheduledDate = "2026-09-01";
const tenantId = FIXTURE_IDS.tenantId;

async function asTenant<T>(
  work: (transaction: DatabaseTransaction) => Promise<T>,
  tenant: string = tenantId,
): Promise<T> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${tenant}, true)`;
    return work(transaction);
  }) as Promise<T>;
}

async function register(
  label: string,
  overrides: Partial<RegisterSessionProfileInput> = {},
): Promise<string> {
  return registerSessionProfile(database, {
    tenantId,
    platformCode: "doubao",
    region: "cn",
    label,
    ...overrides,
  });
}

function profileLease(sessionProfileId: string, overrides: Partial<LeaseRequest> = {}) {
  return leaseTasks(database, {
    tenantId,
    nodeId: `node-${sessionProfileId.slice(0, 8)}`,
    region: "cn",
    supportedPlatformCodes: ["doubao"],
    capacity: 1,
    leaseSeconds: 60,
    sessionProfileId,
    ...overrides,
  });
}

function failure(
  sessionProfileId: string,
  task: LeasedTask | undefined,
  overrides: Partial<CollectorFailure> = {},
): CollectorFailure {
  return {
    tenantId,
    nodeId: `node-${sessionProfileId.slice(0, 8)}`,
    region: "cn",
    sessionProfileId,
    failureCode: "challenge_required",
    phase: task ? "pre_send" : "probe",
    ...(task ? { task: { taskId: task.id, leaseVersion: task.leaseVersion } } : {}),
    ...overrides,
  };
}

/** Open platform gate so account-level rules are tested in isolation. */
async function openPlatformGate(): Promise<void> {
  await configurePlatformDispatch(database, {
    tenantId,
    region: "cn",
    platformCode: "doubao",
    minIntervalSeconds: 0,
    dailyCap: 1000,
  });
}

async function profileRow(id: string) {
  const rows = await asTenant(
    (transaction) => transaction<
      {
        status: string;
        consecutive_challenges: number;
        cooldown_seconds: number | null;
      }[]
    >`
      SELECT
        status,
        consecutive_challenges,
        extract(epoch FROM cooldown_until - now())::float8 AS cooldown_seconds
      FROM session_profiles
      WHERE id = ${id}
    `,
  );
  return rows[0]!;
}

async function taskRow(id: string) {
  const rows = await asTenant(
    (transaction) => transaction<
      {
        status: string;
        lease_owner: string | null;
        failure_count: number;
        last_failure_code: string | null;
        lease_session_profile_id: string | null;
        backoff_seconds: number | null;
      }[]
    >`
      SELECT
        status, lease_owner, failure_count, last_failure_code, lease_session_profile_id,
        extract(epoch FROM not_before - now())::float8 AS backoff_seconds
      FROM collection_tasks
      WHERE id = ${id}
    `,
  );
  return rows[0]!;
}

async function healthEvents(profileId: string) {
  return asTenant(
    (transaction) => transaction<
      { event_kind: string; failure_code: string | null; from_status: string; to_status: string }[]
    >`
      SELECT event_kind, failure_code, from_status, to_status
      FROM session_health_events
      WHERE session_profile_id = ${profileId}
      ORDER BY created_at, event_kind
    `,
  );
}

/** Leaves exactly one leasable doubao task so retry/backoff behaviour is unambiguous. */
async function isolateOneDoubaoTask(): Promise<string> {
  return asTenant(async (transaction) => {
    const rows = await transaction<{ id: string }[]>`
      SELECT id FROM collection_tasks
      WHERE platform_code = 'doubao'
      ORDER BY scheduled_date, queued_at, id
      LIMIT 1
    `;
    const keep = rows[0]!.id;
    await transaction`
      UPDATE collection_tasks
      SET scheduled_date = date '2999-01-01'
      WHERE platform_code = 'doubao' AND id <> ${keep}
    `;
    return keep;
  });
}

async function makeNotBeforeElapsed(taskId: string): Promise<void> {
  await asTenant(async (transaction) => {
    await transaction`
      UPDATE collection_tasks SET not_before = now() - interval '1 second' WHERE id = ${taskId}
    `;
  });
}

beforeAll(async () => {
  await migrateDatabase(database);
});

beforeEach(async () => {
  await resetDatabaseForTests(database);
  await seedFixture(database);
  await generateDailyTasks(database, {
    tenantId,
    brandProjectId: FIXTURE_IDS.brandProjectId,
    scheduledDate,
  });
});

afterAll(async () => {
  await database.end();
});

describe("account pool leasing", () => {
  it("rotates online profiles least-recently-used first within hourly quota (AC-1)", async () => {
    await openPlatformGate();
    const first = await register("profile-a", { hourlyQuota: 2, minIntervalSeconds: 0 });
    const second = await register("profile-b", { hourlyQuota: 2, minIntervalSeconds: 0 });

    const grants: string[] = [];
    const [a1] = await profileLease(first);
    expect(a1?.sessionProfileId).toBe(first);
    grants.push(first);

    const [b1] = await profileLease(second);
    expect(b1?.sessionProfileId).toBe(second);
    grants.push(second);
    // b just leased; a is online, eligible and less recently used, so b must wait.
    expect(await profileLease(second)).toEqual([]);

    expect(await profileLease(first)).toHaveLength(1);
    grants.push(first);
    expect(await profileLease(first)).toEqual([]);
    expect(await profileLease(second)).toHaveLength(1);
    grants.push(second);

    expect(grants).toEqual([first, second, first, second]);
    // Both profiles reached hourly_quota = 2.
    expect(await profileLease(first)).toEqual([]);
    expect(await profileLease(second)).toEqual([]);

    const attempts = await asTenant(
      (transaction) => transaction<{ session_profile_id: string; count: number }[]>`
        SELECT session_profile_id, count(*)::integer AS count
        FROM collection_attempts
        WHERE event_kind = 'leased'
        GROUP BY session_profile_id
      `,
    );
    expect(Object.fromEntries(attempts.map((row) => [row.session_profile_id, row.count]))).toEqual({
      [first]: 2,
      [second]: 2,
    });
  });

  it("enforces the per-profile minimum interval even without competitors", async () => {
    await openPlatformGate();
    const only = await register("profile-solo", { minIntervalSeconds: 600 });
    expect(await profileLease(only)).toHaveLength(1);
    expect(await profileLease(only)).toEqual([]);
    await asTenant(async (transaction) => {
      await transaction`
        UPDATE session_profiles SET last_leased_at = now() - interval '601 seconds' WHERE id = ${only}
      `;
    });
    expect(await profileLease(only)).toHaveLength(1);
  });

  it("caps platform dispatch independently of the number of profiles (AC-2)", async () => {
    const profiles = await Promise.all(
      ["gate-a", "gate-b", "gate-c"].map((label) =>
        register(label, { hourlyQuota: 100, dailyQuota: 100, minIntervalSeconds: 0 }),
      ),
    );
    const [a, b, c] = profiles as [string, string, string];

    // Default platform min interval (120 s) blocks the second grant, whoever asks.
    expect(await profileLease(a)).toHaveLength(1);
    expect(await profileLease(b)).toEqual([]);
    expect(await profileLease(c)).toEqual([]);
    const defaults = await asTenant(
      (transaction) => transaction<{ min_interval_seconds: number; daily_cap: number }[]>`
        SELECT min_interval_seconds, daily_cap FROM platform_dispatch_states
        WHERE platform_code = 'doubao'
      `,
    );
    expect(defaults).toEqual([{ min_interval_seconds: 120, daily_cap: 120 }]);

    await asTenant(async (transaction) => {
      await transaction`
        UPDATE platform_dispatch_states
        SET last_dispatched_at = now() - interval '121 seconds'
        WHERE platform_code = 'doubao'
      `;
      // b and c have never leased, so LRU ties break on the random id; take c offline so this
      // step tests the platform gate, not the fairness tie-break.
      await transaction`UPDATE session_profiles SET last_seen_at = NULL WHERE id = ${c}`;
    });
    expect(await profileLease(b)).toHaveLength(1);

    await configurePlatformDispatch(database, {
      tenantId,
      region: "cn",
      platformCode: "doubao",
      minIntervalSeconds: 0,
      dailyCap: 3,
    });
    expect(await profileLease(c)).toHaveLength(1);
    // Three grants today: every profile is now refused by the daily cap.
    expect(await profileLease(a)).toEqual([]);
    expect(await profileLease(b)).toEqual([]);
    expect(await profileLease(c)).toEqual([]);
  });

  it("keeps legacy leasing for tenants and platforms without profiles (AC-7)", async () => {
    const [legacy] = await leaseTasks(database, {
      tenantId,
      nodeId: "legacy-cn",
      region: "cn",
      supportedPlatformCodes: ["doubao", "qwen"],
      capacity: 2,
      leaseSeconds: 60,
    });
    expect(legacy?.sessionProfileId).toBeNull();

    await register("doubao-only");
    // A profile-less request that includes a pooled platform gets nothing ...
    expect(
      await leaseTasks(database, {
        tenantId,
        nodeId: "legacy-cn",
        region: "cn",
        supportedPlatformCodes: ["doubao", "qwen"],
        capacity: 1,
        leaseSeconds: 60,
      }),
    ).toEqual([]);
    // ... while unpooled platforms and other regions keep the legacy behaviour.
    const qwenTasks = await leaseTasks(database, {
      tenantId,
      nodeId: "legacy-cn",
      region: "cn",
      supportedPlatformCodes: ["qwen"],
      capacity: 3,
      leaseSeconds: 60,
    });
    expect(qwenTasks).toHaveLength(3);
    expect(qwenTasks.every((task) => task.sessionProfileId === null)).toBe(true);
    expect(
      await leaseTasks(database, {
        tenantId,
        nodeId: "legacy-overseas",
        region: "overseas",
        supportedPlatformCodes: ["chatgpt"],
        capacity: 1,
        leaseSeconds: 60,
      }),
    ).toHaveLength(1);
  });

  it("serializes concurrent profile leases through the platform gate", async () => {
    const profiles = await Promise.all(
      ["race-a", "race-b", "race-c", "race-d"].map((label) =>
        register(label, { minIntervalSeconds: 0 }),
      ),
    );
    const results = await Promise.all(profiles.map((profile) => profileLease(profile)));
    // Default platform min interval (120 s) admits exactly one grant across all profiles.
    expect(results.flat()).toHaveLength(1);
  });

  it("forces capacity 1 on the profile path", async () => {
    await openPlatformGate();
    const profile = await register("capacity", { minIntervalSeconds: 0 });
    expect(await profileLease(profile, { capacity: 5 })).toHaveLength(1);
  });
});

describe("eligibility pre-check (design §12.2)", () => {
  function eligibility(sessionProfileId: string, overrides: Partial<EligibilityRequest> = {}) {
    return checkSessionProfileEligibility(database, {
      tenantId,
      nodeId: `node-${sessionProfileId.slice(0, 8)}`,
      region: "cn",
      sessionProfileId,
      platformCode: "doubao",
      ...overrides,
    });
  }

  async function sideEffectSnapshot(profileId: string) {
    return asTenant(async (transaction) => {
      const [counts] = await transaction<
        {
          attempts: number;
          leased_tasks: number;
          platform_rows: number;
          health_events: number;
        }[]
      >`
        SELECT
          (SELECT count(*)::integer FROM collection_attempts) AS attempts,
          (SELECT count(*)::integer FROM collection_tasks WHERE status <> 'pending') AS leased_tasks,
          (SELECT count(*)::integer FROM platform_dispatch_states) AS platform_rows,
          (SELECT count(*)::integer FROM session_health_events) AS health_events
      `;
      const [profile] = await transaction<
        {
          status: string;
          updated_at: Date;
          last_leased_at: Date | null;
          cooldown_until: Date | null;
        }[]
      >`
        SELECT status, updated_at, last_leased_at, cooldown_until
        FROM session_profiles WHERE id = ${profileId}
      `;
      return { ...counts!, profile: profile! };
    });
  }

  async function lastSeenAgeSeconds(profileId: string): Promise<number | null> {
    const rows = await asTenant(
      (transaction) => transaction<{ age: number | null }[]>`
        SELECT extract(epoch FROM now() - last_seen_at)::float8 AS age
        FROM session_profiles WHERE id = ${profileId}
      `,
    );
    return rows[0]?.age ?? null;
  }

  it("answers eligible without a lease, a task lock or any write besides last_seen_at", async () => {
    const profile = await register("eligible");
    await asTenant(async (transaction) => {
      await transaction`UPDATE session_profiles SET last_seen_at = NULL WHERE id = ${profile}`;
    });
    const before = await sideEffectSnapshot(profile);

    expect(await eligibility(profile)).toEqual({ eligible: true, retryAfterSeconds: 30 });

    // Read-only apart from last_seen_at: no attempt, lease, platform row or health event.
    expect(await sideEffectSnapshot(profile)).toEqual(before);
    expect(before.platform_rows).toBe(0);
    expect(await lastSeenAgeSeconds(profile)).toBeLessThan(5);

    // The task the check saw is still leasable by another transaction (no row lock held).
    const [granted] = await profileLease(profile);
    expect(granted?.sessionProfileId).toBe(profile);
  });

  it("marks an operator-only status with a stop reason (and the lease agrees)", async () => {
    await openPlatformGate();
    const verification = await register("needs-verification");
    const expired = await register("expired");
    const disabled = await register("disabled");
    await asTenant(async (transaction) => {
      await transaction`
        UPDATE session_profiles SET status = 'needs_verification' WHERE id = ${verification}
      `;
      await transaction`UPDATE session_profiles SET status = 'expired' WHERE id = ${expired}`;
    });
    await setSessionProfileStatus(database, {
      tenantId,
      sessionProfileId: disabled,
      status: "disabled",
    });
    for (const [profile, stopReason] of [
      [verification, "needs_verification"],
      [expired, "expired"],
      [disabled, "disabled"],
    ] as const) {
      expect(await eligibility(profile)).toEqual({
        eligible: false,
        retryAfterSeconds: 120,
        stopReason,
      });
      expect(await profileLease(profile)).toEqual([]);
    }
  });

  it("returns the per-account minimum interval and cooldown remainder, clamped to 30–120", async () => {
    await openPlatformGate();
    const profile = await register("interval", { minIntervalSeconds: 600 });
    expect(await profileLease(profile)).toHaveLength(1);

    // 600 s remaining -> clamped to the 120 s maximum.
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 120 });
    await asTenant(async (transaction) => {
      await transaction`
        UPDATE session_profiles
        SET last_leased_at = now() - interval '555 seconds'
        WHERE id = ${profile}
      `;
    });
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 45 });
    await asTenant(async (transaction) => {
      await transaction`
        UPDATE session_profiles
        SET last_leased_at = now() - interval '590 seconds'
        WHERE id = ${profile}
      `;
    });
    // 10 s remaining -> clamped to the 30 s minimum.
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 30 });
    expect(await profileLease(profile)).toEqual([]);

    await asTenant(async (transaction) => {
      await transaction`
        UPDATE session_profiles
        SET
          last_leased_at = now() - interval '1 hour',
          status = 'cooling',
          cooldown_until = now() + interval '50 seconds'
        WHERE id = ${profile}
      `;
    });
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 50 });
    expect(await profileLease(profile)).toEqual([]);

    await asTenant(async (transaction) => {
      await transaction`
        UPDATE session_profiles SET cooldown_until = now() - interval '1 second' WHERE id = ${profile}
      `;
    });
    expect(await eligibility(profile)).toEqual({ eligible: true, retryAfterSeconds: 30 });
    // Eligibility never promotes the cooldown; the authoritative lease does.
    expect((await profileRow(profile)).status).toBe("cooling");
    expect(await profileLease(profile)).toHaveLength(1);
    expect((await profileRow(profile)).status).toBe("active");
  });

  it("computes when enough leases slide out of the hourly and daily quota windows", async () => {
    await openPlatformGate();
    const profile = await register("quota", {
      hourlyQuota: 1,
      dailyQuota: 2,
      minIntervalSeconds: 0,
    });
    const taskId = await isolateOneDoubaoTask();
    const insertLeased = (age: string) =>
      asTenant(async (transaction) => {
        await transaction`
          INSERT INTO collection_attempts (
            tenant_id, task_id, lease_version, node_id, region, event_kind,
            session_profile_id, created_at
          ) VALUES (
            ${tenantId}, ${taskId}, 1, 'history', 'cn', 'leased',
            ${profile}, now() - ${age}::interval
          )
        `;
      });

    // Hourly quota 1: the only lease in the window clears 3600 - 3560 = 40 s from now.
    await insertLeased("3560 seconds");
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 40 });
    expect(await profileLease(profile)).toEqual([]);

    // Daily quota 2 reached as well: the binding window is now the 24 h one.
    await insertLeased("23 hours");
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 120 });
    expect(await profileLease(profile)).toEqual([]);
  });

  it("asks a younger profile to wait for an older online one (fairness)", async () => {
    await openPlatformGate();
    const older = await register("fair-older", { minIntervalSeconds: 0 });
    const younger = await register("fair-younger", { minIntervalSeconds: 0 });
    await asTenant(async (transaction) => {
      await transaction`
        UPDATE session_profiles
        SET last_leased_at = now() - interval '1 hour', last_seen_at = now()
        WHERE id = ${older}
      `;
      await transaction`
        UPDATE session_profiles
        SET last_leased_at = now() - interval '10 minutes', last_seen_at = now()
        WHERE id = ${younger}
      `;
    });
    expect(await eligibility(younger)).toEqual({ eligible: false, retryAfterSeconds: 30 });
    expect(await profileLease(younger)).toEqual([]);
    expect(await eligibility(older)).toEqual({ eligible: true, retryAfterSeconds: 30 });

    // Once the older profile drops out of the online window the younger one may work.
    await asTenant(async (transaction) => {
      await transaction`
        UPDATE session_profiles SET last_seen_at = now() - interval '10 minutes' WHERE id = ${older}
      `;
    });
    expect(await eligibility(younger)).toEqual({ eligible: true, retryAfterSeconds: 30 });
    expect(await profileLease(younger)).toHaveLength(1);
  });

  it("applies the platform breaker, interval and daily cap", async () => {
    const profile = await register("platform", {
      hourlyQuota: 100,
      dailyQuota: 100,
      minIntervalSeconds: 0,
    });
    expect(await profileLease(profile)).toHaveLength(1);

    // Default 120 s platform interval just started.
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 120 });
    await asTenant(async (transaction) => {
      await transaction`
        UPDATE platform_dispatch_states
        SET last_dispatched_at = now() - interval '60 seconds'
        WHERE platform_code = 'doubao'
      `;
    });
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 60 });
    expect(await profileLease(profile)).toEqual([]);

    await configurePlatformDispatch(database, {
      tenantId,
      region: "cn",
      platformCode: "doubao",
      minIntervalSeconds: 0,
      dailyCap: 1,
    });
    // Daily cap reached: resets at the next Asia/Shanghai midnight, clamped to 120 s (less only
    // when the test runs within two minutes of that midnight).
    const capped = await eligibility(profile);
    expect(capped).toMatchObject({ eligible: false });
    expect(capped.stopReason).toBeUndefined();
    expect(capped.retryAfterSeconds).toBeGreaterThanOrEqual(30);
    expect(capped.retryAfterSeconds).toBeLessThanOrEqual(120);
    expect(await profileLease(profile)).toEqual([]);

    await configurePlatformDispatch(database, {
      tenantId,
      region: "cn",
      platformCode: "doubao",
      dailyCap: 100,
    });
    await asTenant(async (transaction) => {
      await transaction`
        UPDATE platform_dispatch_states
        SET paused_at = now(), pause_reason = 'challenge_breaker'
        WHERE platform_code = 'doubao'
      `;
    });
    // Only an operator resumes a paused platform: wait the maximum, but no stop reason.
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 120 });
    expect(await profileLease(profile)).toEqual([]);
    expect(
      await resumePlatformDispatch(database, { tenantId, region: "cn", platformCode: "doubao" }),
    ).toBe(true);
    expect(await eligibility(profile)).toEqual({ eligible: true, retryAfterSeconds: 30 });
  });

  it("waits for the next retry backoff or lease expiry when no task is leasable", async () => {
    await openPlatformGate();
    const profile = await register("queue", { minIntervalSeconds: 0 });
    const taskId = await isolateOneDoubaoTask();
    await asTenant(async (transaction) => {
      await transaction`
        UPDATE collection_tasks SET not_before = now() + interval '75 seconds' WHERE id = ${taskId}
      `;
    });
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 75 });
    expect(await profileLease(profile)).toEqual([]);

    await makeNotBeforeElapsed(taskId);
    const other = await register("queue-other", { minIntervalSeconds: 0 });
    await asTenant(async (transaction) => {
      // Keep `profile` from winning the fairness tie-break against `other`.
      await transaction`UPDATE session_profiles SET last_seen_at = NULL WHERE id = ${profile}`;
    });
    expect(await profileLease(other, { leaseSeconds: 90 })).toHaveLength(1);
    // The only task is leased for 90 s by another profile.
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 90 });

    await asTenant(async (transaction) => {
      await transaction`
        UPDATE collection_tasks SET scheduled_date = date '2999-01-01' WHERE platform_code = 'doubao'
      `;
    });
    // Nothing queued for today: no known clear time, so the maximum.
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 120 });
    expect(await profileLease(profile)).toEqual([]);
  });

  it("rejects forged, cross-region, cross-platform and cross-tenant profiles like the lease", async () => {
    const doubao = await register("eligibility-trusted");
    const overseas = await register("eligibility-overseas", {
      platformCode: "chatgpt",
      region: "overseas",
    });
    const forbidden = { code: "SESSION_PROFILE_FORBIDDEN", statusCode: 403 };
    await expect(eligibility(randomUUID())).rejects.toMatchObject(forbidden);
    await expect(eligibility(overseas, { platformCode: "chatgpt" })).rejects.toMatchObject(
      forbidden,
    );
    await expect(eligibility(doubao, { platformCode: "qwen" })).rejects.toMatchObject(forbidden);

    const otherTenantId = "99999999-9999-4999-8999-999999999991";
    await asTenant(async (transaction) => {
      await transaction`
        INSERT INTO tenants (id, slug, name) VALUES (${otherTenantId}, 'pool-other-2', 'Pool Other')
      `;
    }, otherTenantId);
    const foreign = await registerSessionProfile(database, {
      tenantId: otherTenantId,
      platformCode: "doubao",
      region: "cn",
      label: "foreign",
    });
    await expect(eligibility(foreign)).rejects.toMatchObject(forbidden);
    expect(await lastSeenAgeSeconds(doubao)).toBeNull();
  });
});

describe("probe-failed profiles leave the fairness queue (design §12.4)", () => {
  function eligibility(sessionProfileId: string) {
    return checkSessionProfileEligibility(database, {
      tenantId,
      nodeId: `node-${sessionProfileId.slice(0, 8)}`,
      region: "cn",
      sessionProfileId,
      platformCode: "doubao",
    });
  }

  /** Older profile A and younger profile B, both online and otherwise eligible. */
  async function olderAndYounger(): Promise<{ older: string; younger: string }> {
    await openPlatformGate();
    const older = await register("probe-older", { minIntervalSeconds: 0 });
    const younger = await register("probe-younger", { minIntervalSeconds: 0 });
    await asTenant(async (transaction) => {
      await transaction`
        UPDATE session_profiles
        SET last_leased_at = now() - interval '1 hour', last_seen_at = now()
        WHERE id = ${older}
      `;
      await transaction`
        UPDATE session_profiles
        SET last_leased_at = now() - interval '10 minutes', last_seen_at = now()
        WHERE id = ${younger}
      `;
    });
    return { older, younger };
  }

  function probeFailure(profile: string) {
    return reportCollectorFailure(
      database,
      failure(profile, undefined, { failureCode: "page_contract_changed", phase: "probe" }),
    );
  }

  async function setProbeTimes(
    profile: string,
    times: { probeFailedAgo: string | null; leasedAgo?: string },
  ): Promise<void> {
    await asTenant(async (transaction) => {
      await transaction`
        UPDATE session_profiles
        SET
          last_probe_failed_at = now() - ${times.probeFailedAgo}::interval,
          last_leased_at = COALESCE(now() - ${times.leasedAgo ?? null}::interval, last_leased_at),
          last_seen_at = now()
        WHERE id = ${profile}
      `;
    });
  }

  /** Lease and eligibility must agree that the younger profile is blocked by fairness. */
  async function expectBlockedByFairness(profile: string): Promise<void> {
    expect(await eligibility(profile)).toEqual({ eligible: false, retryAfterSeconds: 30 });
    expect(await profileLease(profile)).toEqual([]);
  }

  async function metricsOf(profile: string) {
    const profiles = await listSessionProfileMetrics(database, { tenantId });
    return profiles.find((row) => row.id === profile)!;
  }

  it("lets a younger healthy profile lease while an older one keeps failing probes", async () => {
    const { older, younger } = await olderAndYounger();
    await expectBlockedByFairness(younger);

    // A keeps failing its probe and polling eligibility, so it stays "seen" (the starvation).
    for (let round = 0; round < 3; round += 1) {
      const response = await probeFailure(older);
      // Status effects are unchanged for a non-challenge probe failure.
      expect(response).toEqual({
        sessionProfileStatus: "active",
        taskDisposition: null,
        platformPaused: false,
      });
      await eligibility(older);
    }
    const olderMetrics = await metricsOf(older);
    expect(olderMetrics.status).toBe("active");
    expect(Date.parse(olderMetrics.lastProbeFailedAt!)).toBeGreaterThan(Date.now() - 60_000);
    expect(olderMetrics.excludedFromFairness).toBe(true);
    expect(Date.parse(olderMetrics.lastSeenAt!)).toBeGreaterThan(Date.now() - 60_000);
    expect(await metricsOf(younger)).toMatchObject({
      lastProbeFailedAt: null,
      excludedFromFairness: false,
    });

    expect(await eligibility(younger)).toEqual({ eligible: true, retryAfterSeconds: 30 });
    const [granted] = await profileLease(younger);
    expect(granted?.sessionProfileId).toBe(younger);
  });

  it("only records the probe time for probe-phase failures", async () => {
    const { older, younger } = await olderAndYounger();
    const [task] = await profileLease(older);
    expect(task?.sessionProfileId).toBe(older);
    await reportCollectorFailure(
      database,
      failure(older, task, { failureCode: "page_contract_changed", phase: "pre_send" }),
    );
    expect((await metricsOf(older)).lastProbeFailedAt).toBeNull();

    // A probe-phase challenge keeps its usual status effect and also records the probe time.
    expect(
      await reportCollectorFailure(database, failure(younger, undefined, { phase: "probe" })),
    ).toMatchObject({ sessionProfileStatus: "needs_verification" });
    expect(await metricsOf(younger)).toMatchObject({
      status: "needs_verification",
      consecutiveChallenges: 1,
      excludedFromFairness: true,
    });
  });

  it("restores the older profile's priority after the offline window", async () => {
    const { older, younger } = await olderAndYounger();
    await probeFailure(older);
    expect(await eligibility(younger)).toEqual({ eligible: true, retryAfterSeconds: 30 });

    // Default window is 15 minutes: at 14 minutes the exclusion holds, at 16 it has ended.
    await setProbeTimes(older, { probeFailedAgo: "14 minutes" });
    expect(await eligibility(younger)).toEqual({ eligible: true, retryAfterSeconds: 30 });
    await setProbeTimes(older, { probeFailedAgo: "16 minutes" });
    expect((await metricsOf(older)).excludedFromFairness).toBe(false);
    await expectBlockedByFairness(younger);
  });

  it("ends the exclusion early once the failing profile leases again", async () => {
    const { older, younger } = await olderAndYounger();
    await probeFailure(older);

    // The rule never blocks the failing profile itself: it is otherwise eligible and the oldest.
    expect(await eligibility(older)).toEqual({ eligible: true, retryAfterSeconds: 30 });
    const [granted] = await profileLease(older);
    expect(granted?.sessionProfileId).toBe(older);
    expect((await metricsOf(older)).excludedFromFairness).toBe(false);

    // A lease after the failure counts as healthy again; it is still older than B -> B waits.
    await setProbeTimes(older, { probeFailedAgo: "10 minutes", leasedAgo: "5 minutes" });
    await setProbeTimes(younger, { probeFailedAgo: null, leasedAgo: "1 minute" });
    await expectBlockedByFairness(younger);

    // Same timestamps but the lease predates the failure -> excluded again.
    await setProbeTimes(older, { probeFailedAgo: "10 minutes", leasedAgo: "11 minutes" });
    expect(await eligibility(younger)).toEqual({ eligible: true, retryAfterSeconds: 30 });
    expect(await profileLease(younger)).toHaveLength(1);
  });

  it("uses the configurable per-platform offline window", async () => {
    const { older, younger } = await olderAndYounger();
    await probeFailure(older);
    await setProbeTimes(older, { probeFailedAgo: "20 minutes" });
    await expectBlockedByFairness(younger);

    await configurePlatformDispatch(database, {
      tenantId,
      region: "cn",
      platformCode: "doubao",
      probeFailureOfflineMinutes: 30,
    });
    expect(await listPlatformDispatchStates(database, { tenantId })).toEqual([
      expect.objectContaining({ platformCode: "doubao", probeFailureOfflineMinutes: 30 }),
    ]);
    expect((await metricsOf(older)).excludedFromFairness).toBe(true);
    expect(await eligibility(younger)).toEqual({ eligible: true, retryAfterSeconds: 30 });

    await configurePlatformDispatch(database, {
      tenantId,
      region: "cn",
      platformCode: "doubao",
      probeFailureOfflineMinutes: 10,
    });
    expect((await metricsOf(older)).excludedFromFairness).toBe(false);
    await expectBlockedByFairness(younger);

    for (const invalid of [0, 1441]) {
      await expect(
        configurePlatformDispatch(database, {
          tenantId,
          region: "cn",
          platformCode: "doubao",
          probeFailureOfflineMinutes: invalid,
        }),
      ).rejects.toMatchObject({ code: "23514" });
    }
  });
});

describe("account pool trust boundary", () => {
  it("rejects forged, cross-region, cross-platform and cross-tenant profiles (AC-6)", async () => {
    const doubao = await register("trusted");
    const overseas = await register("overseas", { platformCode: "chatgpt", region: "overseas" });

    const forbidden = { code: "SESSION_PROFILE_FORBIDDEN", statusCode: 403 };
    await expect(profileLease(randomUUID())).rejects.toMatchObject(forbidden);
    await expect(profileLease(overseas)).rejects.toMatchObject(forbidden);
    await expect(profileLease(doubao, { supportedPlatformCodes: ["qwen"] })).rejects.toMatchObject(
      forbidden,
    );
    await expect(
      reportCollectorFailure(database, failure(overseas, undefined)),
    ).rejects.toMatchObject(forbidden);

    const otherTenantId = "99999999-9999-4999-8999-999999999990";
    await asTenant(async (transaction) => {
      await transaction`
        INSERT INTO tenants (id, slug, name) VALUES (${otherTenantId}, 'pool-other', 'Pool Other')
      `;
    }, otherTenantId);
    const foreign = await registerSessionProfile(database, {
      tenantId: otherTenantId,
      platformCode: "doubao",
      region: "cn",
      label: "foreign",
    });
    await expect(profileLease(foreign)).rejects.toMatchObject(forbidden);
    await expect(
      recordSessionEvent(database, {
        tenantId,
        nodeId: "node",
        region: "cn",
        sessionProfileId: foreign,
        eventKind: "verified",
        phase: "probe",
      }),
    ).rejects.toMatchObject(forbidden);

    const visible = await asTenant(
      (transaction) => transaction<{ count: number }[]>`
        SELECT count(*)::integer AS count FROM session_profiles
      `,
      otherTenantId,
    );
    expect(visible[0]?.count).toBe(1);

    await expect(
      registerSessionProfile(database, {
        tenantId,
        platformCode: "doubao",
        region: "overseas",
        label: "wrong-region",
      }),
    ).rejects.toThrow();
  });

  it("copies the lease profile into the accepted sample (AC-6)", async () => {
    await openPlatformGate();
    const profile = await register("sampler", { minIntervalSeconds: 0 });
    const [task] = await profileLease(profile);
    const accepted = await submitTaskResult(database, {
      tenantId,
      taskId: task!.id,
      nodeId: `node-${profile.slice(0, 8)}`,
      region: "cn",
      leaseVersion: task!.leaseVersion,
      idempotencyKey: `pool-${task!.id}`,
      egressCountry: "CN",
      collectorBuildRevision: "integration-test",
      answerText: "synthetic pooled answer",
      citations: [],
      completedAt: new Date().toISOString(),
    });
    expect(accepted.disposition).toBe("accepted");

    const lineage = await asTenant(
      (transaction) => transaction<{ sample_profile: string; attempt_profile: string }[]>`
        SELECT
          sample.session_profile_id AS sample_profile,
          attempt.session_profile_id AS attempt_profile
        FROM answer_samples AS sample
        JOIN collection_attempts AS attempt
          ON attempt.task_id = sample.task_id AND attempt.event_kind = 'accepted'
        WHERE sample.id = ${accepted.sampleId}
      `,
    );
    expect(lineage).toEqual([{ sample_profile: profile, attempt_profile: profile }]);
  });

  it("rejects a sample whose profile differs from the task lease", async () => {
    const profile = await register("not-the-lessee");
    const [task] = await leaseTasks(database, {
      tenantId,
      nodeId: "legacy-cn",
      region: "cn",
      supportedPlatformCodes: ["qwen"],
      capacity: 1,
      leaseSeconds: 60,
    });
    await expect(
      asTenant(async (transaction) => {
        const submissions = await transaction<{ id: string }[]>`
          INSERT INTO result_submissions (
            tenant_id, task_id, lease_version, node_id, region, idempotency_key, payload_hash,
            disposition, answer_text, egress_country, collector_build_revision, completed_at
          ) VALUES (
            ${tenantId}, ${task!.id}, ${task!.leaseVersion}, 'legacy-cn', 'cn', 'forged-profile',
            'hash', 'accepted', 'answer', 'CN', 'test', now()
          )
          RETURNING id
        `;
        await transaction`
          INSERT INTO answer_samples (
            tenant_id, task_id, result_submission_id, collector_node_id, collector_region,
            egress_country, collector_build_revision, valid, session_profile_id
          ) VALUES (
            ${tenantId}, ${task!.id}, ${submissions[0]!.id}, 'legacy-cn', 'cn', 'CN', 'test',
            true, ${profile}
          )
        `;
      }),
    ).rejects.toMatchObject({ code: "23514" });
  });
});

describe("collector failure reports", () => {
  it("releases the lease and marks a challenged profile for verification (AC-3)", async () => {
    await openPlatformGate();
    const profile = await register("challenged", { minIntervalSeconds: 0 });
    const [task] = await profileLease(profile);

    expect(await reportCollectorFailure(database, failure(profile, task))).toEqual({
      sessionProfileStatus: "needs_verification",
      taskDisposition: "requeued",
      platformPaused: false,
    });

    const releasedTask = await taskRow(task!.id);
    expect(releasedTask).toMatchObject({
      status: "pending",
      lease_owner: null,
      failure_count: 1,
      last_failure_code: "challenge_required",
    });
    expect(releasedTask.backoff_seconds).toBeGreaterThan(1790);
    expect(releasedTask.backoff_seconds).toBeLessThanOrEqual(1800);
    expect(await profileRow(profile)).toMatchObject({
      status: "needs_verification",
      consecutive_challenges: 1,
    });
    expect(await healthEvents(profile)).toContainEqual({
      event_kind: "failure_reported",
      failure_code: "challenge_required",
      from_status: "active",
      to_status: "needs_verification",
    });
    const failedAttempts = await asTenant(
      (transaction) => transaction<{ failure_code: string; session_profile_id: string }[]>`
        SELECT failure_code, session_profile_id
        FROM collection_attempts
        WHERE task_id = ${task!.id} AND event_kind = 'failed'
      `,
    );
    expect(failedAttempts).toEqual([
      { failure_code: "challenge_required", session_profile_id: profile },
    ]);

    // The challenged profile is no longer selected.
    expect(await profileLease(profile)).toEqual([]);
  });

  it("does not block result submission's profile foreign-key check on a profile lock", async () => {
    // Lock order is profile -> platform -> task. A submission holds its task lock and then
    // checks the answer_samples -> session_profiles foreign key (FOR KEY SHARE). If the profile
    // lock were FOR UPDATE, a report waiting on the platform gate would block that check, and
    // any report waiting on the task would close a deadlock cycle.
    await openPlatformGate();
    const profile = await register("lock-order", { minIntervalSeconds: 0 });
    const [task] = await profileLease(profile);

    let releaseGate!: () => void;
    const gateReleased = new Promise<void>((resolve) => (releaseGate = resolve));
    let gateHeld!: () => void;
    const gateLocked = new Promise<void>((resolve) => (gateHeld = resolve));
    const holder = asTenant(async (transaction) => {
      await transaction`
        SELECT 1 FROM platform_dispatch_states WHERE platform_code = 'doubao' FOR UPDATE
      `;
      gateHeld();
      await gateReleased;
    });
    await gateLocked;
    // Locks the profile, then waits for the platform row held above.
    const report = reportCollectorFailure(
      database,
      failure(profile, undefined, { failureCode: "browser_failed" }),
    );
    await new Promise((resolve) => setTimeout(resolve, 300));

    const submission = submitTaskResult(database, {
      tenantId,
      taskId: task!.id,
      nodeId: `node-${profile.slice(0, 8)}`,
      region: "cn",
      leaseVersion: task!.leaseVersion,
      idempotencyKey: `lock-order-${task!.id}`,
      egressCountry: "CN",
      collectorBuildRevision: "integration-test",
      answerText: "synthetic answer during a pending failure report",
      citations: [],
      completedAt: new Date().toISOString(),
    });
    const outcome = await Promise.race([
      submission.then((response) => response.disposition),
      new Promise<string>((resolve) => setTimeout(() => resolve("blocked"), 5_000)),
    ]);
    releaseGate();
    await Promise.all([holder, report, submission]);
    expect(outcome).toBe("accepted");
  });

  it("only records the profile effect for a stale lease", async () => {
    await openPlatformGate();
    const profile = await register("stale-reporter", { minIntervalSeconds: 0 });
    const [task] = await profileLease(profile);
    const response = await reportCollectorFailure(
      database,
      failure(
        profile,
        { ...task!, leaseVersion: task!.leaseVersion + 1 },
        {
          failureCode: "login_required",
        },
      ),
    );
    expect(response).toEqual({
      sessionProfileStatus: "expired",
      taskDisposition: "stale",
      platformPaused: false,
    });
    expect(await taskRow(task!.id)).toMatchObject({ status: "leased", failure_count: 0 });
  });

  it("blocks every profile until backoff and fails the task after 3 failures (AC-4)", async () => {
    await openPlatformGate();
    const taskId = await isolateOneDoubaoTask();
    const first = await register("retry-a", { minIntervalSeconds: 0, hourlyQuota: 10 });
    const second = await register("retry-b", { minIntervalSeconds: 0, hourlyQuota: 10 });

    const [leased] = await profileLease(first);
    expect(leased?.id).toBe(taskId);
    await reportCollectorFailure(
      database,
      failure(first, leased, { failureCode: "page_contract_changed" }),
    );
    // Not before the backoff, for any profile.
    expect(await profileLease(second)).toEqual([]);
    expect(await profileLease(first)).toEqual([]);

    await makeNotBeforeElapsed(taskId);
    const [retry] = await profileLease(second);
    expect(retry?.id).toBe(taskId);
    expect(
      (
        await reportCollectorFailure(
          database,
          failure(second, retry, { failureCode: "collection_timeout", phase: "post_send" }),
        )
      ).taskDisposition,
    ).toBe("requeued");

    await makeNotBeforeElapsed(taskId);
    const [last] = await profileLease(first);
    expect(last?.id).toBe(taskId);
    expect(
      (
        await reportCollectorFailure(
          database,
          failure(first, last, { failureCode: "browser_failed" }),
        )
      ).taskDisposition,
    ).toBe("failed");
    expect(await taskRow(taskId)).toMatchObject({ status: "failed", failure_count: 3 });

    await makeNotBeforeElapsed(taskId);
    expect(await profileLease(second)).toEqual([]);
  });

  it("cools rate-limited profiles with doubling backoff and promotes them afterwards", async () => {
    await openPlatformGate();
    const profile = await register("rate-limited", { minIntervalSeconds: 0 });

    await reportCollectorFailure(
      database,
      failure(profile, undefined, { failureCode: "rate_limited" }),
    );
    const firstCooldown = await profileRow(profile);
    expect(firstCooldown.status).toBe("cooling");
    expect(firstCooldown.cooldown_seconds).toBeGreaterThan(1790);
    expect(firstCooldown.cooldown_seconds).toBeLessThanOrEqual(1800);
    expect(await profileLease(profile)).toEqual([]);

    await reportCollectorFailure(
      database,
      failure(profile, undefined, { failureCode: "rate_limited" }),
    );
    const secondCooldown = await profileRow(profile);
    expect(secondCooldown.cooldown_seconds).toBeGreaterThan(3590);
    expect(secondCooldown.cooldown_seconds).toBeLessThanOrEqual(3600);

    for (let index = 0; index < 6; index += 1) {
      await reportCollectorFailure(
        database,
        failure(profile, undefined, { failureCode: "rate_limited" }),
      );
    }
    expect((await profileRow(profile)).cooldown_seconds).toBeLessThanOrEqual(8 * 3600);
    expect((await profileRow(profile)).cooldown_seconds).toBeGreaterThan(8 * 3600 - 10);

    await asTenant(async (transaction) => {
      await transaction`
        UPDATE session_profiles SET cooldown_until = now() - interval '1 second' WHERE id = ${profile}
      `;
    });
    expect(await profileLease(profile)).toHaveLength(1);
    expect(await profileRow(profile)).toMatchObject({ status: "active", cooldown_seconds: null });
    expect((await healthEvents(profile)).map((event) => event.event_kind)).toContain(
      "cooldown_expired",
    );
  });

  it("never turns a profile awaiting a human into a self-expiring cooldown", async () => {
    await openPlatformGate();
    const challenged = await register("late-rate-limit", { minIntervalSeconds: 0 });
    const expired = await register("expired-rate-limit", { minIntervalSeconds: 0 });
    await reportCollectorFailure(database, failure(challenged, undefined));
    await reportCollectorFailure(
      database,
      failure(expired, undefined, { failureCode: "login_required" }),
    );

    for (const [profile, status] of [
      [challenged, "needs_verification"],
      [expired, "expired"],
    ] as const) {
      expect(
        await reportCollectorFailure(
          database,
          failure(profile, undefined, { failureCode: "rate_limited" }),
        ),
      ).toMatchObject({ sessionProfileStatus: status });
      expect(await profileRow(profile)).toMatchObject({ status, cooldown_seconds: null });
    }
  });

  it("pauses a platform after two challenged profiles until an operator resumes it (AC-5)", async () => {
    await openPlatformGate();
    const first = await register("breaker-a", { minIntervalSeconds: 0 });
    const second = await register("breaker-b", { minIntervalSeconds: 0 });
    const third = await register("breaker-c", { minIntervalSeconds: 0 });

    const [firstTask] = await profileLease(first);
    expect((await reportCollectorFailure(database, failure(first, firstTask))).platformPaused).toBe(
      false,
    );
    const [secondTask] = await profileLease(second);
    expect(await reportCollectorFailure(database, failure(second, secondTask))).toMatchObject({
      platformPaused: true,
    });
    expect((await healthEvents(second)).map((event) => event.event_kind)).toContain(
      "breaker_tripped",
    );

    expect(await profileLease(third)).toEqual([]);
    expect(
      await resumePlatformDispatch(database, { tenantId, region: "cn", platformCode: "doubao" }),
    ).toBe(true);
    const [thirdTask] = await profileLease(third);
    expect(thirdTask).toBeDefined();

    // Challenges before the reset no longer count towards the breaker window.
    expect((await reportCollectorFailure(database, failure(third, thirdTask))).platformPaused).toBe(
      false,
    );
  });
});

describe("session events", () => {
  it("restores a verified profile and clears its challenge streak (AC-11)", async () => {
    await openPlatformGate();
    const profile = await register("verify-me", { minIntervalSeconds: 0 });
    const [task] = await profileLease(profile);
    await recordSessionEvent(database, {
      tenantId,
      nodeId: "node",
      region: "cn",
      sessionProfileId: profile,
      eventKind: "challenge_wait_started",
      phase: "pre_send",
      task: { taskId: task!.id, leaseVersion: task!.leaseVersion },
    });
    expect((await profileRow(profile)).status).toBe("active");
    await reportCollectorFailure(database, failure(profile, task));
    expect(await profileRow(profile)).toMatchObject({
      status: "needs_verification",
      consecutive_challenges: 1,
    });

    expect(
      await recordSessionEvent(database, {
        tenantId,
        nodeId: "node",
        region: "cn",
        sessionProfileId: profile,
        eventKind: "verified",
        phase: "probe",
      }),
    ).toEqual({ sessionProfileStatus: "active" });
    expect(await profileRow(profile)).toMatchObject({
      status: "active",
      consecutive_challenges: 0,
    });
    expect((await healthEvents(profile)).map((event) => event.event_kind)).toEqual(
      expect.arrayContaining([
        "profile_registered",
        "challenge_wait_started",
        "failure_reported",
        "verified",
      ]),
    );
  });

  it("keeps health events append-only", async () => {
    const profile = await register("immutable");
    await expect(
      asTenant(async (transaction) => {
        await transaction`
          UPDATE session_health_events SET to_status = 'disabled' WHERE session_profile_id = ${profile}
        `;
      }),
    ).rejects.toThrow("immutable");
    await expect(
      asTenant(async (transaction) => {
        await transaction`DELETE FROM session_health_events WHERE session_profile_id = ${profile}`;
      }),
    ).rejects.toThrow("immutable");
  });
});

describe("operator metrics", () => {
  it("reports per-profile status, quotas, usage and challenge metrics (R4.1, AC-12)", async () => {
    await openPlatformGate();
    const busy = await register("metrics-busy", { hourlyQuota: 10, minIntervalSeconds: 0 });
    const idle = await register("metrics-idle", { hourlyQuota: 5, dailyQuota: 20 });

    // Task 1: a challenge appears and clears during the wait; the answer is accepted.
    const [first] = await profileLease(busy);
    const firstTask = { taskId: first!.id, leaseVersion: first!.leaseVersion };
    for (const eventKind of ["challenge_wait_started", "challenge_cleared"] as const) {
      await recordSessionEvent(database, {
        tenantId,
        nodeId: "node",
        region: "cn",
        sessionProfileId: busy,
        eventKind,
        phase: "pre_send",
        task: firstTask,
      });
    }
    await submitTaskResult(database, {
      tenantId,
      taskId: first!.id,
      nodeId: `node-${busy.slice(0, 8)}`,
      region: "cn",
      leaseVersion: first!.leaseVersion,
      idempotencyKey: `metrics-${first!.id}`,
      egressCountry: "CN",
      collectorBuildRevision: "integration-test",
      answerText: "synthetic metrics answer",
      citations: [],
      completedAt: new Date().toISOString(),
    });

    // Task 2: the wait times out; the wait start and its report are one encounter.
    const [second] = await profileLease(busy);
    await recordSessionEvent(database, {
      tenantId,
      nodeId: "node",
      region: "cn",
      sessionProfileId: busy,
      eventKind: "challenge_wait_started",
      phase: "post_send",
      task: { taskId: second!.id, leaseVersion: second!.leaseVersion },
    });
    await reportCollectorFailure(database, failure(busy, second, { phase: "post_send" }));

    // Recovery, then a probe-time challenge without a task: a separate encounter.
    await recordSessionEvent(database, {
      tenantId,
      nodeId: "node",
      region: "cn",
      sessionProfileId: busy,
      eventKind: "verified",
      phase: "probe",
    });
    await reportCollectorFailure(database, failure(busy, undefined));

    // A lease from two hours ago counts for 24 h but not for the last hour.
    await asTenant(async (transaction) => {
      await transaction`
        INSERT INTO collection_attempts (
          tenant_id, task_id, lease_version, node_id, region, event_kind, session_profile_id,
          created_at
        ) VALUES (
          ${tenantId}, ${first!.id}, ${first!.leaseVersion}, 'node-old', 'cn', 'leased',
          ${busy}, now() - interval '2 hours'
        )
      `;
    });

    const profiles = await listSessionProfileMetrics(database, { tenantId });
    expect(profiles.map((profile) => profile.id)).toEqual([busy, idle]);
    const [busyMetrics, idleMetrics] = profiles;
    expect(busyMetrics).toMatchObject({
      label: "metrics-busy",
      platformCode: "doubao",
      region: "cn",
      status: "needs_verification",
      cooldownUntil: null,
      hourlyQuota: 10,
      dailyQuota: 30,
      minIntervalSeconds: 0,
      consecutiveChallenges: 1,
      leasedLastHour: 2,
      leasedLast24h: 3,
      acceptedLast24h: 1,
      challengesLast24h: 3,
      challengeRateLast24h: 1,
      // The probe-time challenge happened after the last lease.
      excludedFromFairness: true,
    });
    expect(Date.parse(busyMetrics!.lastChallengeAt!)).toBeGreaterThan(Date.now() - 60_000);
    expect(Date.parse(busyMetrics!.lastProbeFailedAt!)).toBeGreaterThan(Date.now() - 60_000);
    expect(busyMetrics!.lastLeasedAt).not.toBeNull();
    expect(idleMetrics).toEqual({
      id: idle,
      label: "metrics-idle",
      platformCode: "doubao",
      region: "cn",
      status: "active",
      cooldownUntil: null,
      hourlyQuota: 5,
      dailyQuota: 20,
      minIntervalSeconds: 600,
      consecutiveChallenges: 0,
      lastLeasedAt: null,
      lastSeenAt: null,
      leasedLastHour: 0,
      leasedLast24h: 0,
      acceptedLast24h: 0,
      challengesLast24h: 0,
      challengeRateLast24h: null,
      lastChallengeAt: null,
      lastProbeFailedAt: null,
      excludedFromFairness: false,
    });

    expect(await listSessionProfileMetrics(database, { tenantId, platformCode: "qwen" })).toEqual(
      [],
    );
    expect(await listSessionProfileMetrics(database, { tenantId, region: "overseas" })).toEqual([]);
    expect(await listSessionProfileMetrics(database, { tenantId: randomUUID() })).toEqual([]);
  });

  it("drives profiles and platform gates through the operator CLI", async () => {
    const cli = fileURLToPath(new URL("../../packages/database/src/cli.ts", import.meta.url));
    const root = fileURLToPath(new URL("../../", import.meta.url));
    const run = async (...args: string[]) => {
      try {
        const { stdout } = await promisify(execFile)(
          process.execPath,
          ["--import", "tsx", cli, ...args],
          {
            cwd: root,
            env: { PATH: process.env.PATH, DATABASE_URL: databaseUrl, AIVC_TENANT_ID: tenantId },
          },
        );
        return { exitCode: 0, output: JSON.parse(stdout.trim()) as Record<string, unknown> };
      } catch (error) {
        const failed = error as { code: number; stderr: string };
        return {
          exitCode: failed.code,
          output: JSON.parse(
            failed.stderr.split("\n").find((line) => line.startsWith("{"))!,
          ) as Record<string, unknown>,
        };
      }
    };

    const added = await run(
      "profiles",
      "add",
      "--platform",
      "doubao",
      "--region",
      "cn",
      "--label",
      "cli-a",
      "--hourly",
      "6",
      "--min-interval",
      "300",
    );
    expect(added).toMatchObject({ exitCode: 0, output: { ok: true } });
    const profileId = added.output.sessionProfileId as string;

    expect(await run("profiles", "set-status", profileId, "disabled")).toMatchObject({
      exitCode: 0,
      output: { ok: true, status: "disabled" },
    });
    expect(await run("profiles", "set-status", profileId, "needs_verification")).toMatchObject({
      exitCode: 2,
      output: { ok: false, code: "USAGE" },
    });
    expect(
      await run("profiles", "set-status", randomUUID(), "active", "--tenant", tenantId),
    ).toMatchObject({ exitCode: 1, output: { ok: false, code: "SESSION_PROFILE_NOT_FOUND" } });

    const listed = await run("profiles", "list", "--platform", "doubao");
    expect(listed.exitCode).toBe(0);
    expect(listed.output.profiles).toEqual([
      expect.objectContaining({
        id: profileId,
        status: "disabled",
        hourlyQuota: 6,
        minIntervalSeconds: 300,
        leasedLastHour: 0,
        challengeRateLast24h: null,
        lastProbeFailedAt: null,
        excludedFromFairness: false,
      }),
    ]);

    expect(
      await run(
        "platforms",
        "set",
        "--platform",
        "doubao",
        "--region",
        "cn",
        "--min-interval",
        "90",
        "--daily-cap",
        "50",
      ),
    ).toMatchObject({
      exitCode: 0,
      output: {
        ok: true,
        platform: {
          minIntervalSeconds: 90,
          dailyCap: 50,
          pausedAt: null,
          probeFailureOfflineMinutes: 15,
        },
      },
    });
    expect(
      await run(
        "platforms",
        "set",
        "--platform",
        "doubao",
        "--region",
        "cn",
        "--probe-offline",
        "30",
      ),
    ).toMatchObject({
      exitCode: 0,
      output: { ok: true, platform: { dailyCap: 50, probeFailureOfflineMinutes: 30 } },
    });
    for (const invalid of ["0", "1441", "abc"]) {
      expect(
        await run(
          "platforms",
          "set",
          "--platform",
          "doubao",
          "--region",
          "cn",
          "--probe-offline",
          invalid,
        ),
      ).toMatchObject({ exitCode: 2, output: { ok: false, code: "USAGE" } });
    }
    expect(await run("platforms", "set", "--platform", "doubao", "--region", "cn")).toMatchObject({
      exitCode: 2,
    });
    // Region must match the platform's registered region (composite foreign key).
    expect(
      await run(
        "platforms",
        "set",
        "--platform",
        "doubao",
        "--region",
        "overseas",
        "--daily-cap",
        "1",
      ),
    ).toMatchObject({ exitCode: 1, output: { ok: false, sqlState: "23503" } });
    expect(
      await run("platforms", "resume", "--platform", "doubao", "--region", "cn"),
    ).toMatchObject({ exitCode: 0, output: { ok: true, resumed: false } });

    const platforms = await listPlatformDispatchStates(database, { tenantId });
    expect(platforms).toEqual([
      expect.objectContaining({
        platformCode: "doubao",
        region: "cn",
        dailyCap: 50,
        probeFailureOfflineMinutes: 30,
      }),
    ]);
    const events = await asTenant(
      (transaction) => transaction<{ event_kind: string }[]>`
        SELECT event_kind FROM session_health_events
        WHERE session_profile_id = ${profileId} OR session_profile_id IS NULL
        ORDER BY created_at, event_kind
      `,
    );
    expect(events.map((event) => event.event_kind)).toEqual([
      "profile_registered",
      "status_changed_by_operator",
      "platform_settings_changed",
      "platform_settings_changed",
    ]);
  }, 60_000);
});
