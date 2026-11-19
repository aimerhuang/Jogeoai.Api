import { randomUUID } from "node:crypto";
import type {
  AppPlatformCode,
  AppControlPlaneFailureCode,
  AppLeaseEmptyReason,
  AppLeasedTask,
  AppResultSubmission,
} from "@aivc/contracts";
import { APP_RESULT_LIMITS, appPlatformCodeSchema, estimatedJsonBytes } from "@aivc/contracts";
import type { AttemptRunner } from "./adapter.ts";
import type { ResolvedBinding } from "./bindings.ts";
import {
  AppControlPlaneError,
  type AppControlPlane,
  type BindingReportResponse,
} from "./control-plane.ts";
import type { DeviceInspector } from "./devices.ts";
import type { AppRecord, EvidenceStore, TaskState } from "./evidence.ts";
import { AppChannelError, AppSetupError, type AppFailureCode } from "./failure.ts";
import { realSleep, shanghaiDay, type Sleep } from "./time.ts";

// Control-plane mode (`serve`, task 11-16-app-collector-control-plane design §4–§7): a resident
// node that reports its bindings, leases App-channel tasks from the Jogeoai control plane, runs
// each on its device with the reviewed-contract adapter and submits the answer. One serial loop per
// device; the devices run side by side. Only questions the control plane hands out are sent, as
// they are. Login, CAPTCHA and device verification are only recognised and reported: the binding
// then stops until an operator has dealt with it on the emulator.

type Log = (line: Record<string, unknown>) => void;
type RemoteBinding = BindingReportResponse["bindings"][number];

export interface NodeOptions {
  nodeId: string;
  controlPlane: AppControlPlane;
  store: Pick<
    EvidenceStore,
    | "readRecord"
    | "readTaskState"
    | "writeTaskState"
    | "pendingTaskStates"
    | "readLedger"
    | "taskAttempts"
  >;
  // Every binding of the file; only enabled ones are reported and worked.
  bindings: readonly ResolvedBinding[];
  inspector: Pick<DeviceInspector, "onlineDevices">;
  attempt: AttemptRunner;
  revision: string;
  // Stop request: no new lease; the attempt in flight finishes, or is aborted after `graceMs`.
  stop: AbortSignal;
  leaseSeconds?: number;
  reportIntervalMs?: number;
  unavailableBackoffMs?: number;
  graceMs?: number;
  // Longest sleep of an idle device loop, so a binding report's changes are picked up.
  idleMaxMs?: number;
  now?: () => Date;
  sleep?: Sleep;
  log?: Log;
  newAnswerId?: () => string;
}
export interface NodeResult {
  leases: number;
  submitted: number;
  failuresReported: number;
}

const DEFAULTS = {
  leaseSeconds: 180,
  reportIntervalMs: 5 * 60_000,
  unavailableBackoffMs: 60_000,
  graceMs: 10 * 60_000,
  idleMaxMs: 60_000,
};
// Applies when a lease is refused without a retry time (403, unexpected 4xx).
const REFUSED_BACKOFF_MS = 10 * 60_000;

// design §7.
export function controlPlaneFailureCode(code: AppFailureCode): AppControlPlaneFailureCode {
  switch (code) {
    case "device_offline":
    case "device_config_mismatch":
      return "device_offline";
    case "app_contract_changed":
      return "contract_changed";
    case "app_contract_unverified":
      return "contract_unverified";
    case "recording_failed":
    case "collection_timeout":
      return "collection_timeout";
    case "app_crashed":
    case "login_required":
    case "challenge_required":
    case "rate_limited":
    case "emulator_rejected":
      return code;
  }
}
// Codes after which the binding itself stops (contract.md §9.5) and so may be reported as `probe`.
const BINDING_LEVEL: readonly AppControlPlaneFailureCode[] = [
  "login_required",
  "challenge_required",
  "rate_limited",
  "emulator_rejected",
];
// Of those, the codes after which the control plane also closes the task instead of offering
// it to another device. A login that ran out stops the binding but not the task.
const TASK_CLOSING: readonly AppControlPlaneFailureCode[] = [
  "challenge_required",
  "rate_limited",
  "emulator_rejected",
];

// contract.md §9.6 from a record with an answer. Values longer than the control plane accepts are
// cut or left out (a URL that is too long becomes null), never invented.
export function resultSubmission(
  record: AppRecord,
  context: { nodeId: string; taskId: string; leaseVersion: number; revision: string; now: Date },
): AppResultSubmission {
  const blank = (value: string | null | undefined) =>
    value === null || value === undefined || value.trim() === "" ? null : value;
  const citations = record.citations
    .slice(0, APP_RESULT_LIMITS.citations)
    .map((source) => ({
      title: source.title.trim().slice(0, APP_RESULT_LIMITS.titleChars),
      site: blank(source.site)?.slice(0, APP_RESULT_LIMITS.siteChars) ?? null,
      url:
        source.url !== null && source.url.length <= APP_RESULT_LIMITS.urlChars
          ? blank(source.url)
          : null,
      list: source.list ?? null,
    }))
    .filter((source) => source.title.length > 0);
  while (
    citations.length > 1 &&
    estimatedJsonBytes(citations) > APP_RESULT_LIMITS.citationsEstimatedBytes
  )
    citations.pop();
  const status =
    record.citationsStatus === "read" && citations.length === 0
      ? "unreadable"
      : (record.citationsStatus ?? (citations.length > 0 ? "read" : "absent"));
  const queries = record.searchState?.searchQueries
    .map((query) => query.trim().slice(0, APP_RESULT_LIMITS.searchQueryChars))
    .filter((query) => query.length > 0)
    .slice(0, APP_RESULT_LIMITS.searchQueries);
  if (!record.sentAt) throw new AppSetupError("evidence_corrupt");
  return {
    nodeId: context.nodeId,
    taskId: context.taskId,
    leaseVersion: context.leaseVersion,
    // A new lease of the same task gets a new key: the control plane checks the key before the
    // lease, so the old key would only ever return the earlier (possibly stale) outcome.
    idempotencyKey: `${record.answerId}.${context.leaseVersion}`,
    collectorBuildRevision: context.revision.slice(0, 128),
    sentAt: record.sentAt.utc,
    completedAt: record.completedAt?.utc ?? context.now.toISOString(),
    answerText: record.answerText ?? "",
    citations: status === "read" ? citations : [],
    citationsStatus: status,
    citationsIssue:
      status === "unreadable"
        ? (record.citationsIssue ?? (record.citationsStatus === "read" ? "too_large" : "unknown"))
        : null,
    searchState: record.searchState
      ? {
          webSearch: record.searchState.webSearch,
          deepThinking: record.searchState.deepThinking,
          searchQueries: queries ?? null,
        }
      : null,
    device: {
      deviceId: record.deviceId,
      androidVersion: record.androidVersion,
      abi: record.abi,
    },
    app: { package: record.package, version: record.appVersion },
    screenshotSha256: record.screenshot?.sha256 ?? null,
  };
}

// Not a type guard on purpose: a record without an answer can still be a failed record.
const hasAnswer = (record: AppRecord | undefined): boolean =>
  record !== undefined &&
  (record.status === "valid" || record.status === "evidence_incomplete") &&
  !!record.answerText;

const matchesEvidenceBinding = (record: AppRecord, binding: ResolvedBinding): boolean =>
  record.deviceId === binding.device.id &&
  record.platformCode === binding.platformCode &&
  record.accountLabel === binding.accountLabel;

interface BindingSlot {
  binding: ResolvedBinding;
  remote?: RemoteBinding;
  nextLeaseAt: number;
  revision: number;
}

export async function runNode(options: NodeOptions): Promise<NodeResult> {
  const settings = { ...DEFAULTS, ...definedOnly(options) };
  const { controlPlane: cp, store, nodeId } = options;
  const now = options.now ?? (() => new Date());
  const sleep = options.sleep ?? realSleep;
  const log = options.log ?? (() => undefined);
  const newAnswerId = options.newAnswerId ?? randomUUID;
  const result: NodeResult = { leases: 0, submitted: 0, failuresReported: 0 };

  // Only App platforms the control plane knows; the bindings file schema is wider.
  const enabled = options.bindings.filter(
    (binding): binding is ResolvedBinding & { platformCode: AppPlatformCode } =>
      binding.enabled && appPlatformCodeSchema.safeParse(binding.platformCode).success,
  );
  const slots = new Map<string, BindingSlot>(
    enabled.map((binding) => [binding.key, { binding, nextLeaseAt: 0, revision: 0 }]),
  );
  // Until the server acknowledges a binding-level failure, even an "active" health report
  // cannot permit more work on that binding. Known pending holds are restored before leasing.
  const bindingHolds = new Map<string, string>(); // task ID -> binding ID
  const awaitingSent = new Map<string, string>(); // attempt -> device; durable until server receipts
  const attemptKey = (s: TaskState) => `${s.taskId.toLowerCase()}.${s.leaseVersion}`;
  const busy = new Map<string, Promise<void>>();
  const forTask = async (taskId: string, work: () => Promise<void>) => {
    const key = taskId.toLowerCase();
    const earlier = busy.get(key);
    const next = earlier ? earlier.then(work) : work();
    busy.set(key, next);
    try {
      await next;
    } finally {
      if (busy.get(key) === next) busy.delete(key);
    }
  };
  const deviceNextAt = new Map<string, number>();
  const platformNextAt = new Map<string, number>();
  let stopping = options.stop.aborted;
  let reported = false;

  // Wakes idle loops: a stop request or a binding report.
  const waiters: (() => void)[] = [];
  const wake = () => {
    for (const waiter of waiters.splice(0)) waiter();
  };
  const changed = () => new Promise<void>((resolve) => void waiters.push(resolve));
  const nap = async (ms: number) => {
    const timer = new AbortController();
    await Promise.race([changed(), sleep(Math.max(0, ms), timer.signal)]);
    timer.abort();
  };

  // Attempts in flight are aborted only after the grace period that follows a stop request.
  const graceOver = new AbortController();
  const graceTimer = new AbortController();
  const onStop = () => {
    if (stopping) return;
    stopping = true;
    log({ event: "app_node_stopping" });
    wake();
    void sleep(settings.graceMs, graceTimer.signal).then(() => graceOver.abort());
  };
  if (stopping) onStop();
  else options.stop.addEventListener("abort", onStop, { once: true });
  // Requests are not cut short by a stop request: the answer in flight still gets submitted.
  const requestSignal = graceOver.signal;

  const applyRemote = (
    remote: {
      status: "active" | "stopped";
      stopReason: RemoteBinding["stopReason"];
      cooldownUntil: string | null;
    },
    slot: BindingSlot,
  ) => {
    if (!slot.remote) return;
    slot.revision++;
    slot.remote = {
      ...slot.remote,
      status: remote.status,
      stopReason: remote.stopReason,
      cooldownUntil: remote.cooldownUntil,
    };
  };

  // ---- binding report (design §4)
  const online = async (deviceId?: string) => {
    try {
      return await options.inspector.onlineDevices(deviceId);
    } catch (error) {
      if (!(error instanceof AppSetupError || error instanceof AppChannelError)) throw error;
      log({ event: "app_online_check_failed", device: deviceId, code: error.code });
      return new Map<string, string>();
    }
  };
  const reportBindings = async () => {
    const devices = await online();
    const items = enabled.map((binding) => ({
      deviceId: binding.device.id,
      platformCode: binding.platformCode,
      accountLabel: binding.accountLabel,
      appPackage: binding.package,
      reviewedAppVersion: binding.reviewedAppVersion,
      installedAppVersion: null,
      androidVersion: binding.device.androidVersion,
      health: devices.has(binding.device.id) ? ("ok" as const) : ("device_offline" as const),
    }));
    try {
      const before = new Map([...slots.values()].map((slot) => [slot, slot.revision]));
      const response = await cp.reportBindings(
        { nodeId, collectorBuildRevision: options.revision, bindings: items },
        requestSignal,
      );
      if (response.dispatchProtocol !== "device-v1") {
        reported = false;
        log({ event: "app_dispatch_protocol_mismatch" });
        return;
      }
      for (const slot of slots.values())
        if (slot.revision === before.get(slot)) slot.remote = undefined;
      for (const remote of response.bindings) {
        const slot = slots.get(`${remote.deviceId}/${remote.platformCode}`);
        if (slot && slot.revision === before.get(slot)) slot.remote = remote;
      }
      for (const rejection of response.rejected)
        log({
          event: "app_binding_rejected",
          device: rejection.deviceId,
          platform: rejection.platformCode,
          code: rejection.code,
        });
      reported = true;
      log({
        event: "app_bindings_reported",
        bindings: items.length,
        active: response.bindings.filter((binding) => binding.status === "active").length,
        stopped: response.bindings
          .filter((binding) => binding.status === "stopped")
          .map((binding) => ({
            device: binding.deviceId,
            platform: binding.platformCode,
            reason: binding.stopReason,
          })),
        rejected: response.rejected.length,
        unhealthy: items
          .filter((item) => item.health !== "ok")
          .map((item) => ({
            device: item.deviceId,
            platform: item.platformCode,
            health: item.health,
          })),
      });
    } catch (error) {
      if (!(error instanceof AppControlPlaneError)) throw error;
      if (error.kind !== "unavailable" || error.status === 200) reported = false;
      log({ event: "app_bindings_report_failed", kind: error.kind, code: error.code });
    }
    wake();
  };

  // ---- submissions (design §5, §6)
  const submit = async (state: TaskState): Promise<void> => {
    if (!reported) return;
    const record = await store.readRecord(state.batchId, state.answerId);
    const identity = {
      task: state.taskId,
      leaseVersion: state.leaseVersion,
      answerId: state.answerId,
    };
    if (!record || !hasAnswer(record)) {
      log({ event: "app_result_missing_record", ...identity });
      await store.writeTaskState({
        ...state,
        pendingSubmit: false,
        updatedAt: now().toISOString(),
      });
      return;
    }
    const slot = slots.get(state.bindingKey);
    // Reporting can relabel the same remote binding. Only submit once this run has
    // confirmed the local device/platform/account with the control plane.
    if (
      !slot?.remote ||
      (state.nodeId && state.nodeId !== nodeId) ||
      (state.bindingId && slot.remote.bindingId.toLowerCase() !== state.bindingId.toLowerCase()) ||
      record.task?.taskId.toLowerCase() !== state.taskId.toLowerCase() ||
      record.task.leaseVersion !== state.leaseVersion ||
      !matchesEvidenceBinding(record, slot.binding)
    ) {
      log({ event: "app_result_waiting_for_binding", ...identity });
      return;
    }
    const body = resultSubmission(record, {
      nodeId,
      taskId: state.taskId,
      leaseVersion: state.leaseVersion,
      // A restart (even onto a new build) must reproduce the same body for this lease/key.
      revision: record.scriptRevision,
      now: new Date(record.startedAt.utc),
    });
    let final: Partial<TaskState>;
    try {
      const response = await cp.submitResult(body, requestSignal);
      log({ event: "app_result_submitted", ...identity, disposition: response.disposition });
      // A replay of a stale submission also returns duplicate, but without a sample ID.
      const accepted =
        response.disposition === "accepted" ||
        (response.disposition === "duplicate" && response.sampleId !== null);
      final = accepted ? { state: "submitted", pendingSubmit: false } : { pendingSubmit: false };
      if (accepted) result.submitted += 1;
    } catch (error) {
      if (!(error instanceof AppControlPlaneError)) throw error;
      log({ event: "app_result_submit_failed", ...identity, kind: error.kind, code: error.code });
      if (error.kind === "unavailable") return; // retried later
      final = { pendingSubmit: false };
    }
    await store.writeTaskState({ ...state, ...final, updatedAt: now().toISOString() });
  };
  // ---- failure reports (design §7)
  const holdPending = (state: TaskState) => {
    if (state.nodeId && state.nodeId !== nodeId) return;
    if (state.pendingSent) {
      const device = state.deviceId ?? slots.get(state.bindingKey)?.binding.device.id;
      if (device) awaitingSent.set(attemptKey(state), device);
    }
    if (!state.failure?.pending || !BINDING_LEVEL.includes(state.failure.failureCode)) return;
    bindingHolds.set(attemptKey(state), state.failure.bindingId.toLowerCase());
  };
  const deliverSent = async (state: TaskState): Promise<TaskState> => {
    if (!state.pendingSent || !state.sentAt || !state.bindingId || state.nodeId !== nodeId)
      return state;
    holdPending(state);
    if (!reported) return state;
    try {
      await cp.reportSent(
        {
          nodeId,
          bindingId: state.bindingId,
          taskId: state.taskId,
          leaseVersion: state.leaseVersion,
          sentAt: state.sentAt,
        },
        requestSignal,
      );
    } catch (error) {
      if (!(error instanceof AppControlPlaneError)) throw error;
      log({
        event: "app_sent_report_failed",
        task: state.taskId,
        leaseVersion: state.leaseVersion,
        kind: error.kind,
      });
      return state;
    }
    const next = { ...state, pendingSent: false, updatedAt: now().toISOString() };
    await store.writeTaskState(next);
    awaitingSent.delete(attemptKey(state));
    return next;
  };
  const deliverFailure = async (state: TaskState): Promise<boolean> => {
    if (!reported) return false;
    const { bindingId, failureCode, phase } = state.failure!;
    const identity = {
      task: state.taskId,
      leaseVersion: state.leaseVersion,
      binding: bindingId,
    };
    try {
      const response = await cp.reportFailure(
        {
          nodeId,
          bindingId,
          failureCode,
          phase,
          sendState: state.sendState ?? (phase === "pre_send" ? "not_sent" : "unknown"),
          ...(state.sentAt && state.sendState === "sent" ? { sentAt: state.sentAt } : {}),
          task: { taskId: state.taskId, leaseVersion: state.leaseVersion },
        },
        requestSignal,
      );
      result.failuresReported += 1;
      const slot = [...slots.values()].find(
        (candidate) => candidate.remote?.bindingId.toLowerCase() === bindingId.toLowerCase(),
      );
      if (slot && (response.taskDisposition !== "stale" || response.bindingStatus === "stopped"))
        applyRemote(
          {
            status: response.bindingStatus,
            stopReason: response.stopReason,
            cooldownUntil: response.cooldownUntil,
          },
          slot,
        );
      log({
        event: "app_failure_reported",
        ...identity,
        failureCode,
        phase,
        bindingStatus: response.bindingStatus,
        stopReason: response.stopReason,
        taskDisposition: response.taskDisposition,
        platformPaused: response.platformPaused,
        platformUnavailable: response.platformUnavailable,
      });
      await store.writeTaskState({
        ...state,
        state: "reported",
        failure: { ...state.failure!, pending: false },
        updatedAt: now().toISOString(),
      });
      bindingHolds.delete(attemptKey(state));
      return true;
    } catch (error) {
      if (!(error instanceof AppControlPlaneError)) throw error;
      log({
        event: "app_failure_report_failed",
        ...identity,
        failureCode,
        kind: error.kind,
        code: error.code,
      });
      return false;
    }
  };

  const reportFailure = async (
    state: TaskState,
    bindingId: string,
    failureCode: AppControlPlaneFailureCode,
  ) => {
    const queued: TaskState = {
      ...state,
      state: "started",
      pendingSubmit: false,
      finishedAt: state.finishedAt ?? now().toISOString(),
      failure: {
        bindingId,
        failureCode,
        phase: state.sendState === "not_sent" ? "pre_send" : "post_send",
        pending: true,
      },
      updatedAt: now().toISOString(),
    };
    holdPending(queued);
    await store.writeTaskState(queued);
    await deliverFailure(queued);
  };

  const recover = async (initial: TaskState) => {
    if (initial.nodeId && initial.nodeId !== nodeId) return;
    const state = await deliverSent(initial);
    if (state.failure?.pending) {
      await deliverFailure(state);
      return;
    }
    if (state.pendingSubmit) {
      await submit(state);
      return;
    }
    if (state.state !== "started" || state.failure) return;
    const record = await store.readRecord(state.batchId, state.answerId);
    if (hasAnswer(record)) {
      const collected = {
        ...state,
        finishedAt: record?.completedAt?.utc ?? state.updatedAt,
        state: "collected" as const,
        pendingSubmit: true,
      };
      await store.writeTaskState(collected);
      await submit(collected);
      return;
    }
    const originalId = state.bindingId ?? slots.get(state.bindingKey)?.remote?.bindingId;
    if (!originalId) return;
    const hasLegacyIntent =
      !state.sendState &&
      (await store.readLedger()).some((entry) => entry.answerId === state.answerId);
    const sendState =
      state.sendState ??
      (!hasLegacyIntent && record?.phase === "pre_send" ? "not_sent" : "unknown");
    await reportFailure(
      { ...state, sendState },
      originalId,
      record?.failureCode ? controlPlaneFailureCode(record.failureCode) : "app_crashed",
    );
  };
  const flushPending = async () => {
    for (const snapshot of await store.pendingTaskStates()) {
      if (requestSignal.aborted) return;
      // An active attempt owns its own delivery. Never send or write back an old snapshot.
      if (busy.has(snapshot.taskId.toLowerCase())) continue;
      await forTask(snapshot.taskId, async () => {
        const state = await store.readTaskState(snapshot.taskId, snapshot.leaseVersion);
        if (state) await recover(state);
      });
    }
  };

  // ---- one leased task (design §5, §6)
  const execute = async (slot: BindingSlot, task: AppLeasedTask) => {
    const { binding } = slot;
    const answerId = newAnswerId();
    const batchId = `node-${shanghaiDay(now())}`;
    const base = {
      nodeId,
      taskId: task.id,
      leaseVersion: task.leaseVersion,
      bindingId: task.bindingId,
      bindingKey: binding.key,
      deviceId: binding.device.id,
      batchId,
      answerId,
    };
    let current: TaskState = {
      version: 1,
      ...base,
      state: "started",
      pendingSubmit: false,
      sendState: "not_sent",
      updatedAt: now().toISOString(),
    };
    const existing = await store.readTaskState(task.id, task.leaseVersion);
    if (existing) {
      await recover(existing);
      return; // A lease identifies one physical attempt even if the previous one did not send.
    }
    const history = await store.taskAttempts(task.id);
    if (history.some((s) => s.leaseVersion > task.leaseVersion)) return;
    for (const previous of history) {
      if (previous.state === "started" || previous.pendingSubmit || previous.pendingSent)
        await recover(previous);
      const saved = (await store.readTaskState(task.id, previous.leaseVersion))!;
      const sameDevice =
        (saved.deviceId ?? saved.bindingKey.split("/")[0]) === binding.device.id &&
        (!saved.nodeId || saved.nodeId === nodeId);
      const closing =
        saved.failure && TASK_CLOSING.includes(saved.failure.failureCode)
          ? saved.failure
          : undefined;
      // The control plane decides whether a task is asked again: a newer lease supersedes the
      // earlier attempt whatever its send state was, and an earlier answer that was not
      // accepted stays with its own lease as stale. Still refused, because the control plane
      // never grants them: the same device again, and a task whose answer was accepted.
      // A task that a verification or rate-limit stop closed is never moved to another
      // device; only the stopped binding itself may take it up again, once its report was
      // delivered and nothing can have been sent.
      const refused = closing
        ? saved.state !== "reported" ||
          closing.pending ||
          (saved.sendState ?? (closing.phase === "pre_send" ? "not_sent" : "unknown")) ===
            "unknown" ||
          !sameDevice ||
          saved.bindingKey !== binding.key ||
          closing.bindingId.toLowerCase() !== task.bindingId.toLowerCase()
        : sameDevice || saved.state === "submitted";
      if (refused) {
        // A refused new lease has its own accurately not-sent attempt; it cannot modify the
        // earlier send fact.
        await reportFailure(current, task.bindingId, "app_crashed");
        return;
      }
    }
    await store.writeTaskState(current);

    // Heartbeats: renew well before expiry; a stale lease aborts the attempt.
    const attemptAbort = new AbortController();
    let leaseUntil = Date.parse(task.leaseExpiresAt);
    const assertLiveLease = () => {
      if (
        attemptAbort.signal.aborted ||
        graceOver.signal.aborted ||
        !(now().getTime() < leaseUntil)
      )
        throw new AppChannelError("collection_timeout", "pre_send");
    };
    const heartbeatStop = new AbortController();
    const heartbeats = (async () => {
      const every = Math.max(5, Math.floor(settings.leaseSeconds / 3)) * 1_000;
      for (;;) {
        await sleep(every, heartbeatStop.signal).catch(() => undefined);
        if (heartbeatStop.signal.aborted) return;
        try {
          const renewed = await cp.heartbeat(
            {
              nodeId,
              taskId: task.id,
              leaseVersion: task.leaseVersion,
              extendSeconds: settings.leaseSeconds,
            },
            requestSignal,
          );
          leaseUntil = Date.parse(renewed.leaseExpiresAt);
        } catch (error) {
          if (!(error instanceof AppControlPlaneError)) return;
          log({ event: "app_heartbeat_failed", task: task.id, kind: error.kind, code: error.code });
          if (error.kind === "stale") {
            attemptAbort.abort();
            return;
          }
        }
      }
    })();

    let outcome;
    try {
      outcome = await options.attempt({
        batchId,
        binding,
        questionText: task.questionText,
        signal: AbortSignal.any([attemptAbort.signal, graceOver.signal]),
        onSendIntent: async () => {
          assertLiveLease();
          current = { ...current, sendState: "unknown", updatedAt: now().toISOString() };
          await store.writeTaskState(current);
          // No tap is allowed after a slow disk write consumed the remaining lease.
          try {
            assertLiveLease();
          } catch (error) {
            current = { ...current, sendState: "not_sent", updatedAt: now().toISOString() };
            await store.writeTaskState(current);
            throw error;
          }
        },
        onConfirmedSend: async (sentAt) => {
          current = {
            ...current,
            sendState: "sent",
            sentAt,
            pendingSent: true,
            updatedAt: now().toISOString(),
          };
          await store.writeTaskState(current);
          current = await deliverSent(current);
        },
        onSent: () => undefined,
        answerId,
        task: { taskId: task.id, leaseVersion: task.leaseVersion },
      });
    } finally {
      heartbeatStop.abort();
      await heartbeats;
    }
    log({
      event: "app_task_attempted",
      task: task.id,
      device: binding.device.id,
      platform: binding.platformCode,
      answerId: outcome.answerId,
      status: outcome.status,
      failureCode: outcome.failureCode,
      phase: outcome.phase,
    });

    const record =
      outcome.answerId === null ? undefined : await store.readRecord(batchId, outcome.answerId);
    if (hasAnswer(record)) {
      const state = {
        ...current,
        finishedAt: record?.completedAt?.utc ?? now().toISOString(),
        state: "collected" as const,
        pendingSubmit: true,
      };
      await store.writeTaskState(state);
      await submit(state);
      return;
    }
    // No answer: a classified failure (or an attempt aborted because the lease went stale).
    // Only the still-live adapter knows that cancellation after intent preceded the tap.
    // A restart with the same on-disk intent remains unknown.
    if (current.sendState === "unknown" && outcome.phase === "pre_send")
      current = { ...current, sendState: "not_sent" };
    await reportFailure(
      current,
      task.bindingId,
      controlPlaneFailureCode(outcome.failureCode ?? record?.failureCode ?? "collection_timeout"),
    );
  };

  // ---- lease refusals (design §4)
  const applyEmpty = (
    slot: BindingSlot,
    reason: AppLeaseEmptyReason,
    retryAfterSeconds: number,
  ) => {
    const until = now().getTime() + retryAfterSeconds * 1_000;
    if (reason === "platform_paused" || reason === "platform_unavailable")
      platformNextAt.set(slot.binding.platformCode, until);
    else if (
      reason === "device_busy" ||
      reason === "device_offline" ||
      reason === "device_cooldown"
    )
      deviceNextAt.set(slot.binding.device.id, until);
    else slot.nextLeaseAt = until;
  };

  const deviceLoop = async (deviceId: string) => {
    const mine = [...slots.values()].filter((slot) => slot.binding.device.id === deviceId);
    let cursor = 0;
    while (!stopping) {
      const at = now().getTime();
      const wakeTimes: number[] = [at + settings.idleMaxMs];
      const ready: BindingSlot[] = [];
      const deviceAt = deviceNextAt.get(deviceId) ?? 0;
      if (reported && deviceAt <= at && ![...awaitingSent.values()].includes(deviceId))
        for (let index = 0; index < mine.length; index++) {
          const slot = mine[(cursor + index) % mine.length]!;
          const remote = slot.remote;
          if (!remote || remote.status !== "active") continue;
          if ([...bindingHolds.values()].includes(remote.bindingId.toLowerCase())) continue;
          const platformAt = platformNextAt.get(slot.binding.platformCode) ?? 0;
          const notBefore = Math.max(platformAt, slot.nextLeaseAt);
          if (notBefore > at) {
            wakeTimes.push(notBefore);
            continue;
          }
          ready.push(slot);
        }
      else if (deviceAt > at) wakeTimes.push(deviceAt);
      if (ready.length === 0) {
        await nap(Math.min(...wakeTimes) - at);
        continue;
      }
      const slot = ready[0]!;
      cursor = (mine.indexOf(slot) + 1) % mine.length;
      let response;
      const leasedBindingId = slot.remote!.bindingId;
      if (!(await online(deviceId)).has(deviceId)) {
        deviceNextAt.set(deviceId, now().getTime() + settings.unavailableBackoffMs);
        continue;
      }
      if (
        stopping ||
        !reported ||
        slot.remote?.status !== "active" ||
        slot.remote.bindingId !== leasedBindingId
      )
        continue;
      try {
        response = await cp.lease(
          {
            nodeId,
            bindingId: leasedBindingId,
            leaseSeconds: settings.leaseSeconds,
            dispatchProtocol: "device-v1",
            deviceOnline: true,
          },
          requestSignal,
        );
      } catch (error) {
        if (!(error instanceof AppControlPlaneError)) throw error;
        log({
          event: "app_lease_failed",
          device: deviceId,
          platform: slot.binding.platformCode,
          kind: error.kind,
          code: error.code,
        });
        if (error.kind === "unavailable")
          deviceNextAt.set(deviceId, now().getTime() + settings.unavailableBackoffMs);
        else slot.nextLeaseAt = now().getTime() + REFUSED_BACKOFF_MS;
        continue;
      }
      if (!response.task) {
        applyEmpty(slot, response.reason!, response.retryAfterSeconds ?? 60);
        continue;
      }
      result.leases += 1;
      log({
        event: "app_task_leased",
        task: response.task.id,
        device: deviceId,
        platform: slot.binding.platformCode,
        leaseVersion: response.task.leaseVersion,
      });
      // The task belongs to this binding by construction; anything else is never worked on.
      if (
        response.task.bindingId.toLowerCase() !== leasedBindingId.toLowerCase() ||
        response.task.platformCode !== slot.binding.platformCode
      ) {
        log({ event: "app_task_mismatch", task: response.task.id, device: deviceId });
        slot.nextLeaseAt = now().getTime() + REFUSED_BACKOFF_MS;
        continue;
      }
      const task = response.task;
      await forTask(task.id, () => execute(slot, task));
    }
  };

  const reportLoop = async () => {
    while (!stopping) {
      await reportBindings();
      await flushPending();
      if (stopping) return;
      await nap(settings.reportIntervalMs);
    }
  };

  log({ event: "app_node_started", nodeId, bindings: enabled.length });
  try {
    for (const pending of await store.pendingTaskStates()) holdPending(pending);
    await Promise.all([
      reportLoop(),
      ...[...new Set(enabled.map((binding) => binding.device.id))].map(deviceLoop),
    ]);
    await flushPending();
  } finally {
    graceTimer.abort();
    options.stop.removeEventListener("abort", onStop);
  }
  log({ event: "app_node_stopped", ...result });
  return result;
}

function definedOnly(options: NodeOptions) {
  const keys = [
    "leaseSeconds",
    "reportIntervalMs",
    "unavailableBackoffMs",
    "graceMs",
    "idleMaxMs",
  ] as const;
  return Object.fromEntries(
    keys.filter((key) => options[key] !== undefined).map((key) => [key, options[key]]),
  ) as Partial<typeof DEFAULTS>;
}
