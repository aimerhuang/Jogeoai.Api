import type {
  CollectorFailure,
  CollectorFailureResponse,
  EligibilityRequest,
  EligibilityResponse,
  LeaseRequest,
  SessionEvent,
  SessionEventResponse,
  SessionProfileStatus,
} from "@aivc/contracts";
import { DomainError, type Region } from "@aivc/domain";
import type postgres from "postgres";

type Database = ReturnType<typeof postgres>;
type Transaction = postgres.TransactionSql;

/** Seconds a profile counts as online after its last lease call (design §3 step 4). */
export const SESSION_PROFILE_ONLINE_WINDOW_SECONDS = 300;

export interface SessionProfileRow {
  id: string;
  tenant_id: string;
  platform_code: string;
  region: Region;
  label: string;
  status: SessionProfileStatus;
  hourly_quota: number;
  daily_quota: number;
  min_interval_seconds: number;
  cooldown_until: Date | null;
  consecutive_challenges: number;
  last_leased_at: Date | null;
  last_seen_at: Date | null;
}

interface PlatformDispatchStateRow {
  min_interval_seconds: number;
  daily_cap: number;
  last_dispatched_at: Date | null;
  paused_at: Date | null;
  breaker_profile_threshold: number;
  breaker_window_minutes: number;
  retry_backoff_seconds: number;
  max_task_failures: number;
  rate_limit_cooldown_base_seconds: number;
  rate_limit_cooldown_max_seconds: number;
  probe_failure_offline_minutes: number;
}

interface ProfileEligibilityRow {
  id: string;
  last_leased_at: Date | null;
  eligible: boolean;
  online: boolean;
}

export interface ProfileLeasedTaskRow {
  id: string;
}

type HealthEventKind =
  | "profile_registered"
  | "challenge_wait_started"
  | "challenge_cleared"
  | "challenge_timeout"
  | "failure_reported"
  | "verified"
  | "cooldown_expired"
  | "status_changed_by_operator"
  | "breaker_tripped"
  | "breaker_reset"
  | "platform_settings_changed";

interface HealthEventInput {
  tenantId: string;
  region: Region;
  platformCode: string;
  sessionProfileId: string | null;
  eventKind: HealthEventKind;
  fromStatus: SessionProfileStatus | null;
  toStatus: SessionProfileStatus | null;
  failureCode?: string | null;
  phase?: "probe" | "pre_send" | "post_send" | null;
  taskId?: string | null;
  leaseVersion?: number | null;
  nodeId?: string | null;
}

function sessionProfileForbidden(): DomainError {
  return new DomainError(
    "SESSION_PROFILE_FORBIDDEN",
    "Session profile is not authorized for this tenant, region or platform",
    403,
  );
}

async function insertHealthEvent(transaction: Transaction, event: HealthEventInput): Promise<void> {
  await transaction`
    INSERT INTO session_health_events (
      tenant_id, region, platform_code, session_profile_id, event_kind, failure_code,
      phase, task_id, lease_version, node_id, from_status, to_status
    ) VALUES (
      ${event.tenantId}, ${event.region}, ${event.platformCode}, ${event.sessionProfileId},
      ${event.eventKind}, ${event.failureCode ?? null}, ${event.phase ?? null},
      ${event.taskId ?? null}, ${event.leaseVersion ?? null}, ${event.nodeId ?? null},
      ${event.fromStatus}, ${event.toStatus}
    )
  `;
}

/**
 * Locks the profile row (first lock in the profile -> platform -> task order) and rejects a
 * profile that is invisible to the tenant or registered for another region.
 *
 * FOR NO KEY UPDATE, not FOR UPDATE: result submissions hold their task lock and then take
 * FOR KEY SHARE on this row through the answer_samples / collection_attempts foreign keys.
 * FOR UPDATE would block that check and let a report waiting on the task close a deadlock.
 */
async function lockSessionProfile(
  transaction: Transaction,
  input: { tenantId: string; sessionProfileId: string; region: Region },
): Promise<SessionProfileRow> {
  const rows = await transaction<SessionProfileRow[]>`
    SELECT
      id, tenant_id, platform_code, region, label, status, hourly_quota, daily_quota,
      min_interval_seconds, cooldown_until, consecutive_challenges, last_leased_at, last_seen_at
    FROM session_profiles
    WHERE tenant_id = ${input.tenantId} AND id = ${input.sessionProfileId}
    FOR NO KEY UPDATE
  `;
  const profile = rows[0];
  if (!profile || profile.region !== input.region) {
    throw sessionProfileForbidden();
  }
  return profile;
}

/** Second lock in the order; creates the platform row with design §11 defaults on first use. */
async function lockPlatformDispatchState(
  transaction: Transaction,
  input: { tenantId: string; region: Region; platformCode: string },
): Promise<PlatformDispatchStateRow> {
  await transaction`
    INSERT INTO platform_dispatch_states (tenant_id, region, platform_code)
    VALUES (${input.tenantId}, ${input.region}, ${input.platformCode})
    ON CONFLICT (tenant_id, region, platform_code) DO NOTHING
  `;
  const rows = await transaction<PlatformDispatchStateRow[]>`
    SELECT
      min_interval_seconds, daily_cap, last_dispatched_at, paused_at,
      breaker_profile_threshold, breaker_window_minutes, retry_backoff_seconds,
      max_task_failures, rate_limit_cooldown_base_seconds, rate_limit_cooldown_max_seconds,
      probe_failure_offline_minutes
    FROM platform_dispatch_states
    WHERE tenant_id = ${input.tenantId}
      AND region = ${input.region}
      AND platform_code = ${input.platformCode}
    FOR UPDATE
  `;
  const state = rows[0];
  if (!state) {
    throw new DomainError("PLATFORM_DISPATCH_STATE_MISSING", "Platform state was not found", 500);
  }
  return state;
}

/** True when the tenant registered any profile for a requested platform in this region. */
export async function tenantUsesSessionProfiles(
  transaction: Transaction,
  request: Pick<LeaseRequest, "tenantId" | "region" | "supportedPlatformCodes">,
): Promise<boolean> {
  const rows = await transaction<{ found: number }[]>`
    SELECT 1 AS found
    FROM session_profiles
    WHERE tenant_id = ${request.tenantId}
      AND region = ${request.region}
      AND platform_code = ANY(${transaction.array(request.supportedPlatformCodes)})
    LIMIT 1
  `;
  return rows.length > 0;
}

function compareLeastRecentlyUsed(
  left: Pick<ProfileEligibilityRow, "id" | "last_leased_at">,
  right: Pick<ProfileEligibilityRow, "id" | "last_leased_at">,
): number {
  const leftTime = left.last_leased_at?.getTime() ?? Number.NEGATIVE_INFINITY;
  const rightTime = right.last_leased_at?.getTime() ?? Number.NEGATIVE_INFINITY;
  if (leftTime !== rightTime) {
    return leftTime < rightTime ? -1 : 1;
  }
  return left.id < right.id ? -1 : left.id > right.id ? 1 : 0;
}

/** Statuses only a human can clear; a resident collector stops instead of polling. */
const OPERATOR_STOP_STATUSES = ["needs_verification", "expired", "disabled"] as const;
type OperatorStopStatus = (typeof OPERATOR_STOP_STATUSES)[number];

function isOperatorStopStatus(status: SessionProfileStatus): status is OperatorStopStatus {
  return (OPERATOR_STOP_STATUSES as readonly string[]).includes(status);
}

/**
 * Column defaults of `platform_dispatch_states` (migration 0016). The read-only eligibility check
 * never creates the row, so a platform without one is evaluated with these values.
 */
const PLATFORM_DISPATCH_DEFAULTS = {
  min_interval_seconds: 120,
  daily_cap: 120,
  probe_failure_offline_minutes: 15,
} as const;

/** Polling bounds for `retryAfterSeconds`; the upper bound keeps polls inside the online window. */
export const ELIGIBILITY_RETRY_MIN_SECONDS = 30;
export const ELIGIBILITY_RETRY_MAX_SECONDS = 120;

type PlatformGateState = Pick<
  PlatformDispatchStateRow,
  "min_interval_seconds" | "daily_cap" | "paused_at" | "probe_failure_offline_minutes"
>;

interface ProfileDispatchRequest {
  tenantId: string;
  nodeId: string;
  region: Region;
  sessionProfileId: string;
  platformCodes: readonly string[];
}

type DispatchBlocker =
  | { kind: "operator_stop"; status: OperatorStopStatus }
  // Cooling, hourly/daily quota or per-account minimum interval.
  | { kind: "profile" }
  // An older, eligible, online profile of the same platform goes first.
  | { kind: "fairness" }
  | { kind: "platform_paused" }
  | {
      kind: "platform_gate";
      intervalRemainingSeconds: number;
      secondsUntilCapReset: number | null;
    };

interface DispatchEvaluation {
  profile: SessionProfileRow;
  blocker: DispatchBlocker | null;
}

/** Lease-path touch: marks the profile online and promotes an expired cooldown (with event). */
async function touchProfileForLease(
  transaction: Transaction,
  request: ProfileDispatchRequest,
  profile: SessionProfileRow,
): Promise<void> {
  const promoted = await transaction<{ id: string }[]>`
    UPDATE session_profiles
    SET
      last_seen_at = now(),
      status = CASE
        WHEN status = 'cooling' AND cooldown_until <= now() THEN 'active'
        ELSE status
      END,
      cooldown_until = CASE
        WHEN status = 'cooling' AND cooldown_until <= now() THEN NULL
        ELSE cooldown_until
      END,
      updated_at = now()
    WHERE tenant_id = ${request.tenantId} AND id = ${profile.id}
    RETURNING CASE WHEN ${profile.status} = 'cooling' AND status = 'active' THEN id END AS id
  `;
  if (promoted[0]?.id) {
    await insertHealthEvent(transaction, {
      tenantId: request.tenantId,
      region: profile.region,
      platformCode: profile.platform_code,
      sessionProfileId: profile.id,
      eventKind: "cooldown_expired",
      fromStatus: "cooling",
      toStatus: "active",
      nodeId: request.nodeId,
    });
  }
}

/** Read-only platform state for eligibility: no row creation, no row lock. */
async function readPlatformGateState(
  transaction: Transaction,
  input: { tenantId: string; region: Region; platformCode: string },
): Promise<PlatformGateState> {
  const rows = await transaction<PlatformGateState[]>`
    SELECT min_interval_seconds, daily_cap, paused_at, probe_failure_offline_minutes
    FROM platform_dispatch_states
    WHERE tenant_id = ${input.tenantId}
      AND region = ${input.region}
      AND platform_code = ${input.platformCode}
  `;
  return rows[0] ?? { ...PLATFORM_DISPATCH_DEFAULTS, paused_at: null };
}

/** Tasks one profile could take now; shared by the lease UPDATE and the eligibility check. */
function leasableTaskPredicate(
  transaction: Transaction,
  input: { tenantId: string; region: Region; platformCode: string },
) {
  return transaction`
    tenant_id = ${input.tenantId}
    AND region = ${input.region}
    AND platform_code = ${input.platformCode}
    AND scheduled_date <= current_date
    AND (not_before IS NULL OR not_before <= now())
    AND (
      status = 'pending'
      OR (status = 'leased' AND lease_expires_at <= now())
    )
  `;
}

/**
 * Design §3 steps 2–5, shared by the lease path and the eligibility check (§12.2). Lock order is
 * profile -> platform; only the lease path creates/locks the platform row and promotes cooldowns.
 * Pending-task selection (step 6) stays with the caller.
 */
async function evaluateProfileDispatch(
  transaction: Transaction,
  request: ProfileDispatchRequest,
  mode: "lease" | "eligibility",
): Promise<DispatchEvaluation> {
  const profile = await lockSessionProfile(transaction, request);
  if (!request.platformCodes.some((code) => code === profile.platform_code)) {
    throw sessionProfileForbidden();
  }
  if (mode === "lease") {
    await touchProfileForLease(transaction, request, profile);
  } else {
    await transaction`
      UPDATE session_profiles
      SET last_seen_at = now()
      WHERE tenant_id = ${request.tenantId} AND id = ${profile.id}
    `;
  }

  const platformInput = {
    tenantId: request.tenantId,
    region: profile.region,
    platformCode: profile.platform_code,
  };
  const platform: PlatformGateState =
    mode === "lease"
      ? await lockPlatformDispatchState(transaction, platformInput)
      : await readPlatformGateState(transaction, platformInput);

  if (isOperatorStopStatus(profile.status)) {
    return { profile, blocker: { kind: "operator_stop", status: profile.status } };
  }

  const profiles = await transaction<ProfileEligibilityRow[]>`
    WITH usage AS (
      SELECT
        p.*,
        (
          SELECT count(*)
          FROM collection_attempts AS attempt
          WHERE attempt.tenant_id = p.tenant_id
            AND attempt.session_profile_id = p.id
            AND attempt.event_kind = 'leased'
            AND attempt.created_at > now() - interval '1 hour'
        ) AS leased_last_hour,
        (
          SELECT count(*)
          FROM collection_attempts AS attempt
          WHERE attempt.tenant_id = p.tenant_id
            AND attempt.session_profile_id = p.id
            AND attempt.event_kind = 'leased'
            AND attempt.created_at > now() - interval '24 hours'
        ) AS leased_last_day
      FROM session_profiles AS p
      WHERE p.tenant_id = ${request.tenantId}
        AND p.region = ${profile.region}
        AND p.platform_code = ${profile.platform_code}
    )
    SELECT
      id,
      last_leased_at,
      (
        (status = 'active' OR (status = 'cooling' AND cooldown_until <= now()))
        AND leased_last_hour < hourly_quota
        AND leased_last_day < daily_quota
        AND (
          last_leased_at IS NULL
          OR last_leased_at + make_interval(secs => min_interval_seconds) <= now()
        )
      ) AS eligible,
      (
        last_seen_at IS NOT NULL
        AND last_seen_at > now() - make_interval(secs => ${SESSION_PROFILE_ONLINE_WINDOW_SECONDS})
        -- Design §12.4: a recent probe failure without a later lease takes the profile out of
        -- other profiles' fairness queue, even while its eligibility polls keep it "seen".
        AND NOT (
          last_probe_failed_at IS NOT NULL
          AND last_probe_failed_at >
            now() - make_interval(mins => ${platform.probe_failure_offline_minutes})
          AND (last_leased_at IS NULL OR last_leased_at < last_probe_failed_at)
        )
      ) AS online
    FROM usage
  `;
  const self = profiles.find((row) => row.id === profile.id);
  if (!self?.eligible) {
    return { profile, blocker: { kind: "profile" } };
  }
  // `online` only matters for competitors: the rule never blocks the requesting profile itself.
  const olderOnlineCompetitor = profiles.some(
    (row) =>
      row.id !== self.id && row.eligible && row.online && compareLeastRecentlyUsed(row, self) < 0,
  );
  if (olderOnlineCompetitor) {
    return { profile, blocker: { kind: "fairness" } };
  }

  if (platform.paused_at !== null) {
    return { profile, blocker: { kind: "platform_paused" } };
  }
  const platformUsage = await transaction<
    { interval_remaining_seconds: number; leased_today: number; seconds_until_day_end: number }[]
  >`
    WITH state AS (
      SELECT (
        SELECT s.last_dispatched_at
        FROM platform_dispatch_states AS s
        WHERE s.tenant_id = ${request.tenantId}
          AND s.region = ${profile.region}
          AND s.platform_code = ${profile.platform_code}
      ) AS last_dispatched_at
    )
    SELECT
      CASE
        WHEN state.last_dispatched_at IS NULL THEN 0
        ELSE GREATEST(
          0,
          extract(epoch FROM state.last_dispatched_at
            + make_interval(secs => ${platform.min_interval_seconds}) - now())
        )
      END::float8 AS interval_remaining_seconds,
      (
        SELECT count(*)::integer
        FROM collection_attempts AS attempt
        JOIN collection_tasks AS task
          ON task.tenant_id = attempt.tenant_id AND task.id = attempt.task_id
        WHERE attempt.tenant_id = ${request.tenantId}
          AND attempt.region = ${profile.region}
          AND attempt.event_kind = 'leased'
          AND task.platform_code = ${profile.platform_code}
          AND attempt.created_at >=
            (date_trunc('day', now() AT TIME ZONE 'Asia/Shanghai') AT TIME ZONE 'Asia/Shanghai')
      ) AS leased_today,
      extract(epoch FROM (
        (date_trunc('day', now() AT TIME ZONE 'Asia/Shanghai') + interval '1 day')
          AT TIME ZONE 'Asia/Shanghai'
      ) - now())::float8 AS seconds_until_day_end
    FROM state
  `;
  const gate = platformUsage[0];
  if (!gate) {
    throw new DomainError("PLATFORM_DISPATCH_STATE_MISSING", "Platform state was not found", 500);
  }
  const capReached = gate.leased_today >= platform.daily_cap;
  if (gate.interval_remaining_seconds > 0 || capReached) {
    return {
      profile,
      blocker: {
        kind: "platform_gate",
        intervalRemainingSeconds: gate.interval_remaining_seconds,
        secondsUntilCapReset: capReached ? gate.seconds_until_day_end : null,
      },
    };
  }
  return { profile, blocker: null };
}

/**
 * Profile lease path (design §3 steps 2–7). Runs inside the caller's tenant transaction and
 * returns the ids of granted tasks (at most one). Every "not now" decision returns [].
 */
export async function leaseTaskIdsForSessionProfile(
  transaction: Transaction,
  request: LeaseRequest & { sessionProfileId: string },
): Promise<ProfileLeasedTaskRow[]> {
  const { profile, blocker } = await evaluateProfileDispatch(
    transaction,
    {
      tenantId: request.tenantId,
      nodeId: request.nodeId,
      region: request.region,
      sessionProfileId: request.sessionProfileId,
      platformCodes: request.supportedPlatformCodes,
    },
    "lease",
  );
  if (blocker) {
    return [];
  }

  const tasks = await transaction<ProfileLeasedTaskRow[]>`
    WITH candidates AS (
      SELECT id
      FROM collection_tasks
      WHERE ${leasableTaskPredicate(transaction, {
        tenantId: request.tenantId,
        region: profile.region,
        platformCode: profile.platform_code,
      })}
      ORDER BY scheduled_date, queued_at, id
      FOR UPDATE SKIP LOCKED
      LIMIT 1
    )
    UPDATE collection_tasks AS task
    SET
      status = 'leased',
      lease_version = task.lease_version + 1,
      lease_owner = ${request.nodeId},
      lease_expires_at = now() + make_interval(secs => ${request.leaseSeconds}),
      lease_session_profile_id = ${profile.id},
      started_at = COALESCE(task.started_at, now())
    FROM candidates
    WHERE task.id = candidates.id
    RETURNING task.id
  `;
  if (tasks.length === 0) {
    return [];
  }

  await transaction`
    UPDATE session_profiles
    SET last_leased_at = now(), updated_at = now()
    WHERE tenant_id = ${request.tenantId} AND id = ${profile.id}
  `;
  await transaction`
    UPDATE platform_dispatch_states
    SET last_dispatched_at = now(), updated_at = now()
    WHERE tenant_id = ${request.tenantId}
      AND region = ${profile.region}
      AND platform_code = ${profile.platform_code}
  `;
  return tasks;
}

/** Clamp to the polling bounds; `null` (operator action or unknown) waits the maximum. */
function clampRetryAfter(seconds: number | null): number {
  if (seconds === null || !Number.isFinite(seconds)) return ELIGIBILITY_RETRY_MAX_SECONDS;
  return Math.min(
    ELIGIBILITY_RETRY_MAX_SECONDS,
    Math.max(ELIGIBILITY_RETRY_MIN_SECONDS, Math.ceil(seconds)),
  );
}

/**
 * Seconds until every profile-level blocker has cleared: cooldown end, per-account minimum
 * interval, and the moment enough leased attempts slide out of the hourly/daily quota windows.
 */
async function profileBlockerSeconds(
  transaction: Transaction,
  input: { tenantId: string; sessionProfileId: string },
): Promise<number> {
  const rows = await transaction<{ seconds: number }[]>`
    SELECT GREATEST(
      0,
      CASE
        WHEN p.status = 'cooling' AND p.cooldown_until > now()
          THEN extract(epoch FROM p.cooldown_until - now())
        ELSE 0
      END,
      CASE
        WHEN p.last_leased_at IS NULL THEN 0
        ELSE extract(epoch FROM p.last_leased_at + make_interval(secs => p.min_interval_seconds) - now())
      END,
      COALESCE((
        SELECT extract(epoch FROM ranked.created_at + interval '1 hour' - now())
        FROM (
          SELECT
            attempt.created_at,
            row_number() OVER (ORDER BY attempt.created_at DESC) AS position
          FROM collection_attempts AS attempt
          WHERE attempt.tenant_id = p.tenant_id
            AND attempt.session_profile_id = p.id
            AND attempt.event_kind = 'leased'
            AND attempt.created_at > now() - interval '1 hour'
        ) AS ranked
        WHERE ranked.position = p.hourly_quota
      ), 0),
      COALESCE((
        SELECT extract(epoch FROM ranked.created_at + interval '24 hours' - now())
        FROM (
          SELECT
            attempt.created_at,
            row_number() OVER (ORDER BY attempt.created_at DESC) AS position
          FROM collection_attempts AS attempt
          WHERE attempt.tenant_id = p.tenant_id
            AND attempt.session_profile_id = p.id
            AND attempt.event_kind = 'leased'
            AND attempt.created_at > now() - interval '24 hours'
        ) AS ranked
        WHERE ranked.position = p.daily_quota
      ), 0)
    )::float8 AS seconds
    FROM session_profiles AS p
    WHERE p.tenant_id = ${input.tenantId} AND p.id = ${input.sessionProfileId}
  `;
  return rows[0]?.seconds ?? 0;
}

/**
 * Seconds until a currently blocked task (retry backoff or an unexpired lease) becomes leasable,
 * or null when nothing is queued for today.
 */
async function nextLeasableTaskSeconds(
  transaction: Transaction,
  input: { tenantId: string; region: Region; platformCode: string },
): Promise<number | null> {
  const rows = await transaction<{ seconds: number | null }[]>`
    SELECT extract(epoch FROM min(
      GREATEST(
        COALESCE(not_before, '-infinity'::timestamptz),
        CASE WHEN status = 'leased' THEN lease_expires_at ELSE '-infinity'::timestamptz END
      )
    ) - now())::float8 AS seconds
    FROM collection_tasks
    WHERE tenant_id = ${input.tenantId}
      AND region = ${input.region}
      AND platform_code = ${input.platformCode}
      AND scheduled_date <= current_date
      AND status IN ('pending', 'leased')
  `;
  return rows[0]?.seconds ?? null;
}

/**
 * Read-only pre-check before a collector loads the platform page (design §12.2). Applies the
 * same profile, fairness, platform-gate and pending-task rules as the lease path but takes no
 * lease and no task row lock; the only write is `last_seen_at`, which keeps the profile online
 * for least-recently-used fairness. The lease stays authoritative: an eligible answer may still
 * be followed by an empty lease after a race.
 */
export async function checkSessionProfileEligibility(
  database: Database,
  request: EligibilityRequest,
): Promise<EligibilityResponse> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${request.tenantId}, true)`;
    const { profile, blocker } = await evaluateProfileDispatch(
      transaction,
      {
        tenantId: request.tenantId,
        nodeId: request.nodeId,
        region: request.region,
        sessionProfileId: request.sessionProfileId,
        platformCodes: [request.platformCode],
      },
      "eligibility",
    );
    const taskInput = {
      tenantId: request.tenantId,
      region: profile.region,
      platformCode: profile.platform_code,
    };
    if (!blocker) {
      const available = await transaction<{ found: number }[]>`
        SELECT 1 AS found
        FROM collection_tasks
        WHERE ${leasableTaskPredicate(transaction, taskInput)}
        LIMIT 1
      `;
      if (available.length > 0) {
        return { eligible: true, retryAfterSeconds: ELIGIBILITY_RETRY_MIN_SECONDS };
      }
      return {
        eligible: false,
        retryAfterSeconds: clampRetryAfter(await nextLeasableTaskSeconds(transaction, taskInput)),
      };
    }
    switch (blocker.kind) {
      case "operator_stop":
        return {
          eligible: false,
          retryAfterSeconds: ELIGIBILITY_RETRY_MAX_SECONDS,
          stopReason: blocker.status,
        };
      case "profile":
        return {
          eligible: false,
          retryAfterSeconds: clampRetryAfter(await profileBlockerSeconds(transaction, request)),
        };
      case "fairness":
        // The older profile is online and will lease shortly; check again soon.
        return { eligible: false, retryAfterSeconds: ELIGIBILITY_RETRY_MIN_SECONDS };
      case "platform_paused":
        // Only an operator clears the breaker.
        return { eligible: false, retryAfterSeconds: ELIGIBILITY_RETRY_MAX_SECONDS };
      case "platform_gate":
        return {
          eligible: false,
          retryAfterSeconds: clampRetryAfter(
            Math.max(blocker.intervalRemainingSeconds, blocker.secondsUntilCapReset ?? 0),
          ),
        };
    }
  });
}

interface ReportedTaskRow {
  id: string;
  status: "pending" | "leased" | "completed" | "failed";
  region: Region;
  platform_code: string;
  lease_version: number;
  lease_owner: string | null;
  lease_session_profile_id: string | null;
  failure_count: number;
}

async function lockReportedTask(
  transaction: Transaction,
  input: { tenantId: string; taskId: string; region: Region; platformCode: string },
): Promise<ReportedTaskRow> {
  const rows = await transaction<ReportedTaskRow[]>`
    SELECT
      id, status, region, platform_code, lease_version, lease_owner,
      lease_session_profile_id, failure_count
    FROM collection_tasks
    WHERE tenant_id = ${input.tenantId} AND id = ${input.taskId}
    FOR UPDATE
  `;
  const task = rows[0];
  if (!task) {
    throw new DomainError("TASK_NOT_FOUND", "Task was not found in the current tenant", 404);
  }
  if (task.region !== input.region || task.platform_code !== input.platformCode) {
    throw sessionProfileForbidden();
  }
  return task;
}

/** Number of rate limits since the profile last proved healthy (accepted sample or verified). */
async function rateLimitStreak(
  transaction: Transaction,
  input: { tenantId: string; sessionProfileId: string },
): Promise<number> {
  const rows = await transaction<{ streak: number }[]>`
    SELECT count(*)::integer AS streak
    FROM session_health_events AS event
    WHERE event.tenant_id = ${input.tenantId}
      AND event.session_profile_id = ${input.sessionProfileId}
      AND event.event_kind = 'failure_reported'
      AND event.failure_code = 'rate_limited'
      AND event.created_at > GREATEST(
        COALESCE((
          SELECT max(attempt.created_at)
          FROM collection_attempts AS attempt
          WHERE attempt.tenant_id = ${input.tenantId}
            AND attempt.session_profile_id = ${input.sessionProfileId}
            AND attempt.event_kind = 'accepted'
        ), '-infinity'::timestamptz),
        COALESCE((
          SELECT max(verified.created_at)
          FROM session_health_events AS verified
          WHERE verified.tenant_id = ${input.tenantId}
            AND verified.session_profile_id = ${input.sessionProfileId}
            AND verified.event_kind = 'verified'
        ), '-infinity'::timestamptz)
      )
  `;
  return rows[0]?.streak ?? 0;
}

/**
 * Applies a classified collector failure (design §4) in one transaction: profile health,
 * immediate lease release with retry backoff, a failed attempt row, and breaker evaluation.
 */
export async function reportCollectorFailure(
  database: Database,
  report: CollectorFailure,
): Promise<CollectorFailureResponse> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${report.tenantId}, true)`;
    const profile = await lockSessionProfile(transaction, report);
    const platform = await lockPlatformDispatchState(transaction, {
      tenantId: report.tenantId,
      region: profile.region,
      platformCode: profile.platform_code,
    });
    const task = report.task
      ? await lockReportedTask(transaction, {
          tenantId: report.tenantId,
          taskId: report.task.taskId,
          region: profile.region,
          platformCode: profile.platform_code,
        })
      : undefined;

    // Operator-disabled profiles keep their status; the event still records the failure.
    const keepStatus = profile.status === "disabled";
    let toStatus: SessionProfileStatus = profile.status;
    if (!keepStatus && report.failureCode === "challenge_required") {
      toStatus = "needs_verification";
    } else if (!keepStatus && report.failureCode === "login_required") {
      toStatus = "expired";
    } else if (
      report.failureCode === "rate_limited" &&
      (profile.status === "active" || profile.status === "cooling")
    ) {
      // A rate limit never downgrades a profile that already needs a human (verification or
      // login) into an automatically expiring cooldown.
      toStatus = "cooling";
    }

    if (report.failureCode === "rate_limited" && toStatus === "cooling") {
      const streak = (await rateLimitStreak(transaction, report)) + 1;
      const cooldownSeconds = Math.min(
        platform.rate_limit_cooldown_base_seconds * 2 ** Math.min(streak - 1, 20),
        platform.rate_limit_cooldown_max_seconds,
      );
      await transaction`
        UPDATE session_profiles
        SET
          status = 'cooling',
          cooldown_until = now() + make_interval(secs => ${cooldownSeconds}),
          updated_at = now()
        WHERE tenant_id = ${report.tenantId} AND id = ${profile.id}
      `;
    } else {
      await transaction`
        UPDATE session_profiles
        SET
          status = ${toStatus},
          cooldown_until = CASE WHEN ${toStatus} = 'cooling' THEN cooldown_until ELSE NULL END,
          consecutive_challenges = consecutive_challenges
            + CASE WHEN ${report.failureCode} = 'challenge_required' THEN 1 ELSE 0 END,
          updated_at = now()
        WHERE tenant_id = ${report.tenantId} AND id = ${profile.id}
      `;
    }

    if (report.phase === "probe") {
      // Design §12.4: leaves other profiles' fairness queue until a later lease or the window ends.
      await transaction`
        UPDATE session_profiles
        SET last_probe_failed_at = now(), updated_at = now()
        WHERE tenant_id = ${report.tenantId} AND id = ${profile.id}
      `;
    }

    await insertHealthEvent(transaction, {
      tenantId: report.tenantId,
      region: profile.region,
      platformCode: profile.platform_code,
      sessionProfileId: profile.id,
      eventKind: "failure_reported",
      failureCode: report.failureCode,
      phase: report.phase,
      taskId: report.task?.taskId ?? null,
      leaseVersion: report.task?.leaseVersion ?? null,
      nodeId: report.nodeId,
      fromStatus: profile.status,
      toStatus,
    });

    let taskDisposition: CollectorFailureResponse["taskDisposition"] = null;
    if (task && report.task) {
      const leaseIsCurrent =
        task.status === "leased" &&
        task.lease_version === report.task.leaseVersion &&
        task.lease_owner === report.nodeId &&
        task.lease_session_profile_id === profile.id;
      if (leaseIsCurrent) {
        const exhausted = task.failure_count + 1 >= platform.max_task_failures;
        await transaction`
          UPDATE collection_tasks
          SET
            status = ${exhausted ? "failed" : "pending"},
            lease_owner = NULL,
            lease_expires_at = NULL,
            failure_count = failure_count + 1,
            last_failure_code = ${report.failureCode},
            not_before = CASE
              WHEN ${exhausted} THEN not_before
              ELSE now() + make_interval(secs => ${platform.retry_backoff_seconds})
            END
          WHERE tenant_id = ${report.tenantId} AND id = ${task.id}
        `;
        taskDisposition = exhausted ? "failed" : "requeued";
      } else {
        taskDisposition = "stale";
      }
      await transaction`
        INSERT INTO collection_attempts (
          tenant_id, task_id, lease_version, node_id, region, event_kind,
          session_profile_id, failure_code
        ) VALUES (
          ${report.tenantId}, ${task.id}, ${report.task.leaseVersion}, ${report.nodeId},
          ${profile.region}, 'failed', ${profile.id}, ${report.failureCode}
        )
      `;
    }

    let platformPaused = platform.paused_at !== null;
    if (report.failureCode === "challenge_required" && !platformPaused) {
      const challenged = await transaction<{ profiles: number }[]>`
        SELECT count(DISTINCT event.session_profile_id)::integer AS profiles
        FROM session_health_events AS event
        WHERE event.tenant_id = ${report.tenantId}
          AND event.region = ${profile.region}
          AND event.platform_code = ${profile.platform_code}
          AND event.event_kind = 'failure_reported'
          AND event.failure_code = 'challenge_required'
          AND event.created_at >
            now() - make_interval(mins => ${platform.breaker_window_minutes})
          AND event.created_at > COALESCE((
            SELECT max(reset.created_at)
            FROM session_health_events AS reset
            WHERE reset.tenant_id = ${report.tenantId}
              AND reset.region = ${profile.region}
              AND reset.platform_code = ${profile.platform_code}
              AND reset.event_kind = 'breaker_reset'
          ), '-infinity'::timestamptz)
      `;
      if ((challenged[0]?.profiles ?? 0) >= platform.breaker_profile_threshold) {
        await transaction`
          UPDATE platform_dispatch_states
          SET paused_at = now(), pause_reason = 'challenge_breaker', updated_at = now()
          WHERE tenant_id = ${report.tenantId}
            AND region = ${profile.region}
            AND platform_code = ${profile.platform_code}
        `;
        await insertHealthEvent(transaction, {
          tenantId: report.tenantId,
          region: profile.region,
          platformCode: profile.platform_code,
          sessionProfileId: profile.id,
          eventKind: "breaker_tripped",
          failureCode: null,
          nodeId: report.nodeId,
          fromStatus: toStatus,
          toStatus,
        });
        platformPaused = true;
      }
    }

    return { sessionProfileStatus: toStatus, taskDisposition, platformPaused };
  });
}

/**
 * Records a non-failure session event. `verified` (sent only after an operator's ready probe)
 * restores the profile to active and clears challenge/cooldown state; disabled stays disabled.
 */
export async function recordSessionEvent(
  database: Database,
  event: SessionEvent,
): Promise<SessionEventResponse> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${event.tenantId}, true)`;
    const profile = await lockSessionProfile(transaction, event);
    if (event.task) {
      await lockReportedTask(transaction, {
        tenantId: event.tenantId,
        taskId: event.task.taskId,
        region: profile.region,
        platformCode: profile.platform_code,
      });
    }

    let toStatus: SessionProfileStatus = profile.status;
    if (event.eventKind === "verified") {
      toStatus = profile.status === "disabled" ? "disabled" : "active";
      await transaction`
        UPDATE session_profiles
        SET
          status = ${toStatus},
          cooldown_until = NULL,
          consecutive_challenges = 0,
          updated_at = now()
        WHERE tenant_id = ${event.tenantId} AND id = ${profile.id}
      `;
    }

    await insertHealthEvent(transaction, {
      tenantId: event.tenantId,
      region: profile.region,
      platformCode: profile.platform_code,
      sessionProfileId: profile.id,
      eventKind: event.eventKind,
      phase: event.phase,
      taskId: event.task?.taskId ?? null,
      leaseVersion: event.task?.leaseVersion ?? null,
      nodeId: event.nodeId,
      fromStatus: profile.status,
      toStatus,
    });
    return { sessionProfileStatus: toStatus };
  });
}

export interface RegisterSessionProfileInput {
  tenantId: string;
  platformCode: string;
  region: Region;
  label: string;
  hourlyQuota?: number;
  dailyQuota?: number;
  minIntervalSeconds?: number;
}

/** Operator registration of one authorized account; stores only non-sensitive metadata. */
export async function registerSessionProfile(
  database: Database,
  input: RegisterSessionProfileInput,
): Promise<string> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${input.tenantId}, true)`;
    const rows = await transaction<{ id: string }[]>`
      INSERT INTO session_profiles (
        tenant_id, platform_code, region, label, hourly_quota, daily_quota, min_interval_seconds
      ) VALUES (
        ${input.tenantId}, ${input.platformCode}, ${input.region}, ${input.label},
        ${input.hourlyQuota ?? 4}, ${input.dailyQuota ?? 30}, ${input.minIntervalSeconds ?? 600}
      )
      RETURNING id
    `;
    const id = rows[0]?.id;
    if (!id) {
      throw new DomainError("SESSION_PROFILE_WRITE_FAILED", "Session profile was not saved", 500);
    }
    await insertHealthEvent(transaction, {
      tenantId: input.tenantId,
      region: input.region,
      platformCode: input.platformCode,
      sessionProfileId: id,
      eventKind: "profile_registered",
      fromStatus: "active",
      toStatus: "active",
    });
    return id;
  });
}

/** Operator enable/disable. Writes `status_changed_by_operator`. */
export async function setSessionProfileStatus(
  database: Database,
  input: { tenantId: string; sessionProfileId: string; status: "active" | "disabled" },
): Promise<void> {
  await database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${input.tenantId}, true)`;
    const rows = await transaction<
      Pick<SessionProfileRow, "region" | "platform_code" | "status">[]
    >`
      SELECT region, platform_code, status
      FROM session_profiles
      WHERE tenant_id = ${input.tenantId} AND id = ${input.sessionProfileId}
      FOR NO KEY UPDATE
    `;
    const profile = rows[0];
    if (!profile) {
      throw new DomainError("SESSION_PROFILE_NOT_FOUND", "Session profile was not found", 404);
    }
    await transaction`
      UPDATE session_profiles
      SET
        status = ${input.status},
        cooldown_until = NULL,
        consecutive_challenges = CASE
          WHEN ${input.status} = 'active' THEN 0 ELSE consecutive_challenges
        END,
        updated_at = now()
      WHERE tenant_id = ${input.tenantId} AND id = ${input.sessionProfileId}
    `;
    await insertHealthEvent(transaction, {
      tenantId: input.tenantId,
      region: profile.region,
      platformCode: profile.platform_code,
      sessionProfileId: input.sessionProfileId,
      eventKind: "status_changed_by_operator",
      fromStatus: profile.status,
      toStatus: input.status,
    });
  });
}

export interface ConfigurePlatformDispatchInput {
  tenantId: string;
  region: Region;
  platformCode: string;
  minIntervalSeconds?: number;
  dailyCap?: number;
  breakerProfileThreshold?: number;
  breakerWindowMinutes?: number;
  retryBackoffSeconds?: number;
  maxTaskFailures?: number;
  rateLimitCooldownBaseSeconds?: number;
  rateLimitCooldownMaxSeconds?: number;
  probeFailureOfflineMinutes?: number;
}

/** Operator platform gate settings; omitted fields keep their current (or default) value. */
export async function configurePlatformDispatch(
  database: Database,
  input: ConfigurePlatformDispatchInput,
): Promise<void> {
  await database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${input.tenantId}, true)`;
    await lockPlatformDispatchState(transaction, input);
    await transaction`
      UPDATE platform_dispatch_states
      SET
        min_interval_seconds = COALESCE(${input.minIntervalSeconds ?? null}::integer, min_interval_seconds),
        daily_cap = COALESCE(${input.dailyCap ?? null}::integer, daily_cap),
        breaker_profile_threshold = COALESCE(
          ${input.breakerProfileThreshold ?? null}::integer, breaker_profile_threshold
        ),
        breaker_window_minutes = COALESCE(
          ${input.breakerWindowMinutes ?? null}::integer, breaker_window_minutes
        ),
        retry_backoff_seconds = COALESCE(
          ${input.retryBackoffSeconds ?? null}::integer, retry_backoff_seconds
        ),
        max_task_failures = COALESCE(${input.maxTaskFailures ?? null}::integer, max_task_failures),
        rate_limit_cooldown_base_seconds = COALESCE(
          ${input.rateLimitCooldownBaseSeconds ?? null}::integer, rate_limit_cooldown_base_seconds
        ),
        rate_limit_cooldown_max_seconds = COALESCE(
          ${input.rateLimitCooldownMaxSeconds ?? null}::integer, rate_limit_cooldown_max_seconds
        ),
        probe_failure_offline_minutes = COALESCE(
          ${input.probeFailureOfflineMinutes ?? null}::integer, probe_failure_offline_minutes
        ),
        updated_at = now()
      WHERE tenant_id = ${input.tenantId}
        AND region = ${input.region}
        AND platform_code = ${input.platformCode}
    `;
    await insertHealthEvent(transaction, {
      tenantId: input.tenantId,
      region: input.region,
      platformCode: input.platformCode,
      sessionProfileId: null,
      eventKind: "platform_settings_changed",
      fromStatus: null,
      toStatus: null,
    });
  });
}

/** Operator-only breaker release (R3.6). Returns false when the platform was not paused. */
export async function resumePlatformDispatch(
  database: Database,
  input: { tenantId: string; region: Region; platformCode: string },
): Promise<boolean> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${input.tenantId}, true)`;
    const state = await lockPlatformDispatchState(transaction, input);
    if (state.paused_at === null) {
      return false;
    }
    await transaction`
      UPDATE platform_dispatch_states
      SET paused_at = NULL, pause_reason = NULL, updated_at = now()
      WHERE tenant_id = ${input.tenantId}
        AND region = ${input.region}
        AND platform_code = ${input.platformCode}
    `;
    await insertHealthEvent(transaction, {
      tenantId: input.tenantId,
      region: input.region,
      platformCode: input.platformCode,
      sessionProfileId: null,
      eventKind: "breaker_reset",
      fromStatus: null,
      toStatus: null,
    });
    return true;
  });
}

export interface SessionProfileMetrics {
  id: string;
  label: string;
  platformCode: string;
  region: Region;
  status: SessionProfileStatus;
  cooldownUntil: string | null;
  hourlyQuota: number;
  dailyQuota: number;
  minIntervalSeconds: number;
  consecutiveChallenges: number;
  lastLeasedAt: string | null;
  lastSeenAt: string | null;
  leasedLastHour: number;
  leasedLast24h: number;
  acceptedLast24h: number;
  /** Challenge encounters: every wait start, plus challenge reports not ending such a wait. */
  challengesLast24h: number;
  /** challengesLast24h / leasedLast24h; null when nothing was leased (never 0 by default). */
  challengeRateLast24h: number | null;
  lastChallengeAt: string | null;
  /** Last failure reported during probe (design §12.4). */
  lastProbeFailedAt: string | null;
  /** True while a recent probe failure without a later lease removes it from fairness. */
  excludedFromFairness: boolean;
}

interface SessionProfileMetricsRow {
  id: string;
  label: string;
  platform_code: string;
  region: Region;
  status: SessionProfileStatus;
  cooldown_until: Date | null;
  hourly_quota: number;
  daily_quota: number;
  min_interval_seconds: number;
  consecutive_challenges: number;
  last_leased_at: Date | null;
  last_seen_at: Date | null;
  leased_last_hour: number;
  leased_last_day: number;
  accepted_last_day: number;
  challenges_last_day: number;
  last_challenge_at: Date | null;
  last_probe_failed_at: Date | null;
  excluded_from_fairness: boolean;
}

const isoOrNull = (value: Date | null): string | null => (value ? value.toISOString() : null);

/**
 * Operator view of every registered profile (R4.1). Counts come from attempt and health-event
 * evidence, never from mutable counters. A challenge that times out a wait (wait start followed
 * by a `challenge_required` report on the same lease) counts once.
 */
export async function listSessionProfileMetrics(
  database: Database,
  input: { tenantId: string; platformCode?: string; region?: Region },
): Promise<SessionProfileMetrics[]> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${input.tenantId}, true)`;
    const rows = await transaction<SessionProfileMetricsRow[]>`
      SELECT
        p.id, p.label, p.platform_code, p.region, p.status, p.cooldown_until, p.hourly_quota,
        p.daily_quota, p.min_interval_seconds, p.consecutive_challenges, p.last_leased_at,
        p.last_seen_at,
        (
          SELECT count(*)::integer
          FROM collection_attempts AS attempt
          WHERE attempt.tenant_id = p.tenant_id
            AND attempt.session_profile_id = p.id
            AND attempt.event_kind = 'leased'
            AND attempt.created_at > now() - interval '1 hour'
        ) AS leased_last_hour,
        (
          SELECT count(*)::integer
          FROM collection_attempts AS attempt
          WHERE attempt.tenant_id = p.tenant_id
            AND attempt.session_profile_id = p.id
            AND attempt.event_kind = 'leased'
            AND attempt.created_at > now() - interval '24 hours'
        ) AS leased_last_day,
        (
          SELECT count(*)::integer
          FROM collection_attempts AS attempt
          WHERE attempt.tenant_id = p.tenant_id
            AND attempt.session_profile_id = p.id
            AND attempt.event_kind = 'accepted'
            AND attempt.created_at > now() - interval '24 hours'
        ) AS accepted_last_day,
        (
          SELECT count(*)::integer
          FROM session_health_events AS event
          WHERE event.tenant_id = p.tenant_id
            AND event.session_profile_id = p.id
            AND event.created_at > now() - interval '24 hours'
            AND (
              event.event_kind = 'challenge_wait_started'
              OR (
                event.event_kind = 'failure_reported'
                AND event.failure_code = 'challenge_required'
                AND NOT COALESCE((
                  SELECT prior.event_kind = 'challenge_wait_started'
                  FROM session_health_events AS prior
                  WHERE event.task_id IS NOT NULL
                    AND prior.tenant_id = event.tenant_id
                    AND prior.session_profile_id = event.session_profile_id
                    AND prior.task_id = event.task_id
                    AND prior.lease_version = event.lease_version
                    AND prior.event_kind IN ('challenge_wait_started', 'challenge_cleared')
                    AND prior.created_at <= event.created_at
                  ORDER BY prior.created_at DESC
                  LIMIT 1
                ), false)
              )
            )
        ) AS challenges_last_day,
        (
          SELECT max(event.created_at)
          FROM session_health_events AS event
          WHERE event.tenant_id = p.tenant_id
            AND event.session_profile_id = p.id
            AND (
              event.event_kind = 'challenge_wait_started'
              OR (event.event_kind = 'failure_reported' AND event.failure_code = 'challenge_required')
            )
        ) AS last_challenge_at,
        p.last_probe_failed_at,
        (
          p.last_probe_failed_at IS NOT NULL
          AND p.last_probe_failed_at > now() - make_interval(mins => COALESCE((
            SELECT state.probe_failure_offline_minutes
            FROM platform_dispatch_states AS state
            WHERE state.tenant_id = p.tenant_id
              AND state.region = p.region
              AND state.platform_code = p.platform_code
          ), ${PLATFORM_DISPATCH_DEFAULTS.probe_failure_offline_minutes}))
          AND (p.last_leased_at IS NULL OR p.last_leased_at < p.last_probe_failed_at)
        ) AS excluded_from_fairness
      FROM session_profiles AS p
      WHERE p.tenant_id = ${input.tenantId}
        AND (${input.platformCode ?? null}::text IS NULL OR p.platform_code = ${input.platformCode ?? null})
        AND (${input.region ?? null}::text IS NULL OR p.region = ${input.region ?? null})
      ORDER BY p.region, p.platform_code, p.label, p.id
    `;
    return rows.map((row) => ({
      id: row.id,
      label: row.label,
      platformCode: row.platform_code,
      region: row.region,
      status: row.status,
      cooldownUntil: isoOrNull(row.cooldown_until),
      hourlyQuota: row.hourly_quota,
      dailyQuota: row.daily_quota,
      minIntervalSeconds: row.min_interval_seconds,
      consecutiveChallenges: row.consecutive_challenges,
      lastLeasedAt: isoOrNull(row.last_leased_at),
      lastSeenAt: isoOrNull(row.last_seen_at),
      leasedLastHour: row.leased_last_hour,
      leasedLast24h: row.leased_last_day,
      acceptedLast24h: row.accepted_last_day,
      challengesLast24h: row.challenges_last_day,
      challengeRateLast24h:
        row.leased_last_day === 0 ? null : row.challenges_last_day / row.leased_last_day,
      lastChallengeAt: isoOrNull(row.last_challenge_at),
      lastProbeFailedAt: isoOrNull(row.last_probe_failed_at),
      excludedFromFairness: row.excluded_from_fairness,
    }));
  });
}

export interface PlatformDispatchStateView {
  region: Region;
  platformCode: string;
  minIntervalSeconds: number;
  dailyCap: number;
  lastDispatchedAt: string | null;
  pausedAt: string | null;
  pauseReason: string | null;
  breakerProfileThreshold: number;
  breakerWindowMinutes: number;
  retryBackoffSeconds: number;
  maxTaskFailures: number;
  rateLimitCooldownBaseSeconds: number;
  rateLimitCooldownMaxSeconds: number;
  probeFailureOfflineMinutes: number;
}

/** Operator view of platform gates; a platform without a row still uses design defaults. */
export async function listPlatformDispatchStates(
  database: Database,
  input: { tenantId: string },
): Promise<PlatformDispatchStateView[]> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${input.tenantId}, true)`;
    const rows = await transaction<
      (PlatformDispatchStateRow & {
        region: Region;
        platform_code: string;
        pause_reason: string | null;
      })[]
    >`
      SELECT
        region, platform_code, min_interval_seconds, daily_cap, last_dispatched_at, paused_at,
        pause_reason, breaker_profile_threshold, breaker_window_minutes, retry_backoff_seconds,
        max_task_failures, rate_limit_cooldown_base_seconds, rate_limit_cooldown_max_seconds,
        probe_failure_offline_minutes
      FROM platform_dispatch_states
      WHERE tenant_id = ${input.tenantId}
      ORDER BY region, platform_code
    `;
    return rows.map((row) => ({
      region: row.region,
      platformCode: row.platform_code,
      minIntervalSeconds: row.min_interval_seconds,
      dailyCap: row.daily_cap,
      lastDispatchedAt: isoOrNull(row.last_dispatched_at),
      pausedAt: isoOrNull(row.paused_at),
      pauseReason: row.pause_reason,
      breakerProfileThreshold: row.breaker_profile_threshold,
      breakerWindowMinutes: row.breaker_window_minutes,
      retryBackoffSeconds: row.retry_backoff_seconds,
      maxTaskFailures: row.max_task_failures,
      rateLimitCooldownBaseSeconds: row.rate_limit_cooldown_base_seconds,
      rateLimitCooldownMaxSeconds: row.rate_limit_cooldown_max_seconds,
      probeFailureOfflineMinutes: row.probe_failure_offline_minutes,
    }));
  });
}
