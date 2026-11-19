import { unlink } from "node:fs/promises";
import { setTimeout as delay } from "node:timers/promises";
import type { CollectorConfig } from "@aivc/config";
import {
  collectorFailureCodeSchema,
  type LeasedTask,
  type ResultSubmission,
} from "@aivc/contracts";
import type { ControlPlane } from "./control-plane-client.ts";
import {
  type ApprovedQuestions,
  assertApprovedTask,
  assertAttemptCapacity,
  loadApprovedQuestions,
  reserveAttempt,
} from "./approval-guard.ts";
import { createChallengeWaiter } from "./challenge-wait.ts";
import { withHeartbeat } from "./heartbeat.ts";
import type { Notifier } from "./notify.ts";
import {
  type AdapterRegistry,
  type CollectionPhase,
  CollectorError,
  createSubmission,
  type FailureCode,
  safeFailure,
} from "./platform-adapter.ts";
import { flushSpool, spoolSubmission } from "./spool.ts";

// Identifiers, enum values and counts only; never question text, answers or page content.
export type SafeLog = (metadata: {
  event: string;
  taskId?: string;
  disposition?: string;
  count?: number;
  code?: string;
  phase?: string;
  platform?: string;
  reason?: string;
  sessionProfileId?: string;
  channel?: string;
  status?: string;
}) => void;
// Collector process exit codes, read by the account-pool supervisor:
//   0  finished: empty queue, task budget reached, RUN_ONCE done or shutdown signal. A resident
//      profile-mode collector (design §12.3) only finishes on shutdown or RUN_ONCE.
//   1  any other failure (configuration, browser, control plane, page contract, spool, ...)
//   3  the account needs a human: challenge_required, login_required, or the control plane
//      reports needs_verification / expired / disabled. Do not restart this profile until the
//      operator has resolved it and `probe` reported `verified`.
export const COLLECTOR_EXIT_CODES = { finished: 0, failed: 1, needsOperator: 3 } as const;
export function exitCodeFor(code: FailureCode): 1 | 3 {
  return code === "challenge_required" ||
    code === "login_required" ||
    code === "session_profile_stopped"
    ? COLLECTOR_EXIT_CODES.needsOperator
    : COLLECTOR_EXIT_CODES.failed;
}
export interface RunnerOptions {
  notify?: Notifier;
}
export async function runCollector(
  config: CollectorConfig,
  client: ControlPlane,
  registry: AdapterRegistry,
  signal: AbortSignal,
  log: SafeLog,
  profileDirectory?: string,
  approvedQuestions?: ApprovedQuestions,
  options: RunnerOptions = {},
): Promise<number> {
  // Validate the operator-approved list before any spool replay or lease.
  const approved =
    approvedQuestions ??
    (config.COLLECTOR_MODE === "real" ? await loadApprovedQuestions(config) : undefined);
  let processed = 0;
  const sessionProfileId = config.COLLECTOR_SESSION_PROFILE_ID;
  // Real mode with a registered account runs as a resident loop (design §12.3): it asks the
  // control plane before loading the page, keeps the browser open between tasks and only stops
  // on shutdown, RUN_ONCE, an account that needs a human, or an error. The control plane's
  // quotas replace the per-run task budget. Without a profile the bounded run is unchanged.
  const resident = config.COLLECTOR_MODE === "real" && Boolean(sessionProfileId);
  const notify = (
    platform: LeasedTask["platformCode"],
    reason: Parameters<Notifier>[0]["reason"],
    phase: "probe" | CollectionPhase,
    task?: LeasedTask,
  ) => {
    void options
      .notify?.({ platform, reason, phase, sessionProfileId, taskId: task?.id })
      .catch(() => undefined);
  };
  // Best-effort: releases the lease and applies the account effect now. If the report fails the
  // lease simply expires (previous behaviour); the original error is always rethrown by callers.
  const reportFailure = async (
    error: unknown,
    platform: LeasedTask["platformCode"],
    phase: "probe" | CollectionPhase,
    task?: LeasedTask,
  ) => {
    const code = collectorFailureCodeSchema.safeParse(safeFailure(error));
    if (!code.success) return;
    // Account-affecting failures that need a human in the profile window.
    if (code.data === "challenge_required" || code.data === "login_required")
      notify(platform, code.data, phase, task);
    if (!sessionProfileId || signal.aborted) return;
    try {
      const response = await client.reportFailure(
        {
          tenantId: config.COLLECTOR_TENANT_ID,
          nodeId: config.COLLECTOR_NODE_ID,
          region: config.COLLECTOR_REGION,
          sessionProfileId,
          failureCode: code.data,
          phase,
          ...(task ? { task: { taskId: task.id, leaseVersion: task.leaseVersion } } : {}),
        },
        signal,
      );
      log({
        event: "failure_reported",
        taskId: task?.id,
        code: code.data,
        phase,
        status: response.sessionProfileStatus,
        disposition: response.taskDisposition ?? undefined,
      });
    } catch (reportError) {
      log({
        event: "failure_report_failed",
        taskId: task?.id,
        code: safeFailure(reportError),
        phase,
      });
    }
  };
  const submit = async (submission: ResultSubmission, submitSignal: AbortSignal) => {
    if (
      submission.tenantId !== config.COLLECTOR_TENANT_ID ||
      submission.region !== config.COLLECTOR_REGION ||
      submission.nodeId !== config.COLLECTOR_NODE_ID
    )
      throw new CollectorError("task_not_approved");
    const result = await client.submit(submission, submitSignal);
    log({ event: "result_submission", taskId: submission.taskId, disposition: result.disposition });
    return result;
  };
  // Replays preserve exact idempotency keys and precede all new browser traffic.
  const flushed = await flushSpool(
    config.COLLECTOR_SPOOL_DIRECTORY,
    config.COLLECTOR_SPOOL_KEY,
    async (submission) => {
      await submit(submission, signal);
    },
  );
  log({ event: "spool_flushed", count: flushed });
  do {
    signal.throwIfAborted();
    if (
      config.COLLECTOR_MODE === "real" &&
      !resident &&
      processed >= config.COLLECTOR_MAX_TASKS_PER_RUN
    )
      break;
    // A full lifetime ledger or missing profile stops before leasing, never after.
    if (config.COLLECTOR_MODE === "real") await assertAttemptCapacity(profileDirectory, config);
    if (resident) {
      // Asked before every probe, so an account that may not work now never loads the page.
      const answer = await client.eligibility(signal);
      if (answer.stopReason) {
        log({ event: "session_profile_stopped", status: answer.stopReason, sessionProfileId });
        throw new CollectorError("session_profile_stopped");
      }
      if (!answer.eligible) {
        log({ event: "not_eligible", count: answer.retryAfterSeconds });
        if (config.COLLECTOR_RUN_ONCE === "true") break;
        await delay(answer.retryAfterSeconds * 1_000, undefined, { signal });
        continue;
      }
    }
    for (const platform of config.COLLECTOR_SUPPORTED_PLATFORMS) {
      const adapter = registry.get(platform);
      // Only the resident loop may reuse the already loaded page (design §12.3).
      const probe = resident
        ? await adapter.probe(signal, { inPlace: true })
        : await adapter.probe(signal);
      if (probe.status !== "ready") {
        const error = new CollectorError(probe.status);
        await reportFailure(error, platform, "probe");
        throw error;
      }
    }
    const tasks = await client.lease(signal);
    if (tasks.length > config.COLLECTOR_CAPACITY) throw new CollectorError("invalid_response");
    tasks.forEach((task) => assertApprovedTask(task, config, approved));
    for (const task of tasks) {
      // Only failures raised while the adapter works are reported; spool, approval and lease
      // ownership errors stay local (they are not in the contract's failure codes anyway).
      let collecting = false;
      let submitted = false;
      try {
        await withHeartbeat(task, config, client, signal, async (activeSignal, lease) => {
          if (config.COLLECTOR_MODE === "real") {
            if (!profileDirectory) throw new CollectorError("profile_unavailable");
            await reserveAttempt(profileDirectory, task, config, approved);
          }
          activeSignal.throwIfAborted();
          const challengeWaiter =
            config.COLLECTOR_CHALLENGE_WAIT_MS > 0
              ? createChallengeWaiter({
                  budgetMs: config.COLLECTOR_CHALLENGE_WAIT_MS,
                  extendDeadline: lease.extendDeadline,
                  onEvent: async (eventKind, waitPhase) => {
                    log({ event: eventKind, taskId: task.id, phase: waitPhase });
                    if (eventKind === "challenge_wait_started")
                      notify(task.platformCode, eventKind, waitPhase, task);
                    if (!sessionProfileId) return;
                    await client.sessionEvent(
                      {
                        tenantId: config.COLLECTOR_TENANT_ID,
                        nodeId: config.COLLECTOR_NODE_ID,
                        region: config.COLLECTOR_REGION,
                        sessionProfileId,
                        eventKind,
                        phase: waitPhase,
                        task: { taskId: task.id, leaseVersion: task.leaseVersion },
                      },
                      activeSignal,
                    );
                  },
                })
              : undefined;
          collecting = true;
          const result = await registry.get(task.platformCode).collect(task, activeSignal, {
            challengeWaiter,
            onSubmitted: () => {
              submitted = true;
            },
          });
          collecting = false;
          activeSignal.throwIfAborted();
          const submission = createSubmission(task, result, config);
          const path = await spoolSubmission(
            config.COLLECTOR_SPOOL_DIRECTORY,
            config.COLLECTOR_SPOOL_KEY,
            submission,
          );
          activeSignal.throwIfAborted();
          try {
            const disposition = await submit(submission, activeSignal);
            await unlink(path);
            if (disposition.disposition === "stale") throw new CollectorError("stale_lease");
          } catch (error) {
            if (error instanceof CollectorError && error.code === "stale_lease") throw error;
            throw new CollectorError("submission_spooled");
          }
        });
      } catch (error) {
        if (collecting)
          await reportFailure(error, task.platformCode, submitted ? "post_send" : "pre_send", task);
        throw error;
      }
      processed += 1;
      if (
        config.COLLECTOR_MODE === "real" &&
        (resident || processed < config.COLLECTOR_MAX_TASKS_PER_RUN)
      ) {
        await delay(config.COLLECTOR_MIN_REQUEST_INTERVAL_MS, undefined, { signal });
      }
    }
    if (config.COLLECTOR_RUN_ONCE === "true") break;
    if (tasks.length === 0 && resident) {
      // Eligible but nothing granted (another account or collector won the race): pause before
      // the next eligibility check instead of polling in a tight loop.
      await delay(config.COLLECTOR_MIN_REQUEST_INTERVAL_MS, undefined, { signal });
      continue;
    }
    if (tasks.length === 0 && config.COLLECTOR_MODE === "real") break;
    if (tasks.length === 0) await delay(config.COLLECTOR_POLL_INTERVAL_MS, undefined, { signal });
  } while (!signal.aborted);
  return processed;
}
