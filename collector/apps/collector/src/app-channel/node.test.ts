import { randomUUID } from "node:crypto";
import { rm } from "node:fs/promises";
import type { AppLeasedTask, AppResultSubmission } from "@aivc/contracts";
import { afterEach, describe, expect, it } from "vitest";
import type { AttemptOutcome, AttemptRequest } from "./adapter.ts";
import { parseBindings, resolveBindings, type ResolvedBinding } from "./bindings.ts";
import { AppControlPlaneError, type AppControlPlane } from "./control-plane.ts";
import { EvidenceStore } from "./evidence.ts";
import { AppChannelError } from "./failure.ts";
import { controlPlaneFailureCode, resultSubmission, runNode, type NodeOptions } from "./node.ts";
import {
  DEVICE_A,
  DEVICE_B,
  bindingsFixture,
  evidenceDirectory,
  recordFixture,
} from "./test-fixtures.ts";
import { timestampPair } from "./time.ts";

const created: string[] = [];
afterEach(async () => {
  for (const directory of created.splice(0)) await rm(directory, { recursive: true, force: true });
});

const NODE = "mac-local";
const bindingIdOf = (binding: ResolvedBinding) =>
  `00000000-0000-4000-8000-0000000000${binding.device.id === DEVICE_A ? "a" : "b"}${
    binding.platformCode === "doubao" ? "1" : "2"
  }`;

function resolved(): ResolvedBinding[] {
  return resolveBindings(parseBindings(bindingsFixture()));
}
function taskFor(binding: ResolvedBinding, overrides: Partial<AppLeasedTask> = {}): AppLeasedTask {
  return {
    id: randomUUID(),
    tenantId: "11111111-1111-4111-8111-111111111111",
    runId: "22222222-2222-4222-8222-222222222222",
    checkId: "33333333-3333-4333-8333-333333333333",
    brandProjectId: "44444444-4444-4444-8444-444444444444",
    platformCode: binding.platformCode as AppLeasedTask["platformCode"],
    platformEntryCode: `${binding.platformCode}.consumer_app`,
    questionVersionId: "55555555-5555-4555-8555-555555555555",
    questionText: "合成租户提问",
    region: "cn",
    language: "zh-CN",
    channel: "consumer_app",
    promptRuleVersion: 1,
    deadlineAt: "2026-11-23T15:59:59.000Z",
    leaseVersion: 1,
    leaseExpiresAt: new Date(Date.now() + 180_000).toISOString(),
    bindingId: bindingIdOf(binding),
    ...overrides,
  };
}

// A control plane that hands out the queued tasks once each and records every call.
class FakeControlPlane implements AppControlPlane {
  readonly calls: { route: string; body: unknown }[] = [];
  readonly queue: AppLeasedTask[] = [];
  stopReasons = new Map<string, string>();
  results: (AppResultSubmission & { response?: "stale" | "unavailable" })[] = [];
  submitFailure?: AppControlPlaneError;
  constructor(private readonly bindings: ResolvedBinding[]) {}
  reportBindings: AppControlPlane["reportBindings"] = async (body) => {
    this.calls.push({ route: "bindings/report", body });
    return {
      dispatchProtocol: "device-v1",
      bindings: body.bindings.map((item) => {
        const binding = this.bindings.find(
          (candidate) =>
            candidate.device.id === item.deviceId && candidate.platformCode === item.platformCode,
        )!;
        const stopped =
          this.stopReasons.get(binding.key) ?? (item.health === "ok" ? null : item.health);
        return {
          deviceId: item.deviceId,
          platformCode: item.platformCode,
          bindingId: bindingIdOf(binding),
          status: stopped ? ("stopped" as const) : ("active" as const),
          stopReason: (stopped as never) ?? null,
          cooldownUntil: null,
          minIntervalSeconds: 100,
          dailyQuota: null,
        };
      }),
      rejected: [],
    };
  };
  lease: AppControlPlane["lease"] = async (body) => {
    this.calls.push({ route: "leases", body });
    const index = this.queue.findIndex((task) => task.bindingId === body.bindingId);
    if (index < 0) return { task: null, reason: "no_task", retryAfterSeconds: 1 };
    return { task: this.queue.splice(index, 1)[0]!, reason: null, retryAfterSeconds: null };
  };
  heartbeat: AppControlPlane["heartbeat"] = async (body) => {
    this.calls.push({ route: "heartbeats", body });
    return { leaseExpiresAt: new Date(Date.now() + 180_000).toISOString() };
  };
  reportSent: AppControlPlane["reportSent"] = async (body) => {
    this.calls.push({ route: "sent", body });
    return { disposition: "accepted" };
  };
  reportFailure: AppControlPlane["reportFailure"] = async (body) => {
    this.calls.push({ route: "failures", body });
    const stops = ["login_required", "emulator_rejected", "challenge_required"].includes(
      body.failureCode,
    );
    return {
      bindingStatus: stops ? "stopped" : "active",
      stopReason: stops ? (body.failureCode as never) : null,
      cooldownUntil: null,
      taskDisposition: body.task ? "requeued" : null,
      platformPaused: false,
      platformUnavailable: false,
    };
  };
  submitResult: AppControlPlane["submitResult"] = async (body) => {
    this.calls.push({ route: "results", body });
    if (this.submitFailure) throw this.submitFailure;
    this.results.push(body);
    return { disposition: "accepted", sampleId: randomUUID() };
  };
  routes<T = unknown>(route: string): T[] {
    return this.calls.filter((call) => call.route === route).map((call) => call.body as T);
  }
}

const onlineInspector = (bindings: ResolvedBinding[], offline: string[] = []) => ({
  async onlineDevices(deviceId?: string) {
    return new Map(
      bindings
        .filter((b) => (!deviceId || b.device.id === deviceId) && !offline.includes(b.device.id))
        .map((b) => [b.device.id, "serial"]),
    );
  },
});

// An attempt that writes the record the real adapter would write.
function recordingAttempt(
  store: EvidenceStore,
  result: (
    request: AttemptRequest,
  ) => Partial<ReturnType<typeof recordFixture>> | "precheck_failed",
  requests: AttemptRequest[] = [],
) {
  return async (request: AttemptRequest): Promise<AttemptOutcome> => {
    requests.push(request);
    const outcome = result(request);
    if (outcome === "precheck_failed")
      return { answerId: null, status: "failed", failureCode: "device_offline", phase: "pre_send" };
    const record = recordFixture({
      answerId: request.answerId!,
      batchId: request.batchId,
      sampleClass: "control_plane",
      task: request.task!,
      questionText: request.questionText,
      sentText: request.questionText,
      platformCode: request.binding.platformCode,
      deviceId: request.binding.device.id,
      accountLabel: request.binding.accountLabel,
      video: null,
      ...outcome,
    });
    if (record.sentAt) {
      await request.onSendIntent?.();
      await request.onConfirmedSend?.(record.sentAt.utc);
    }
    await store.writeRecord(record);
    return {
      answerId: record.answerId,
      status: record.status,
      failureCode: record.failureCode,
      phase: record.phase,
    };
  };
}

async function node(
  overrides: Partial<NodeOptions> & Pick<NodeOptions, "controlPlane" | "store" | "attempt">,
  until: () => boolean,
) {
  const bindings = resolved();
  const stop = new AbortController();
  const running = runNode({
    nodeId: NODE,
    bindings,
    inspector: onlineInspector(bindings),
    revision: "test-revision",
    stop: stop.signal,
    idleMaxMs: 5,
    reportIntervalMs: 60_000,
    graceMs: 1_000,
    ...overrides,
  });
  const deadline = Date.now() + 3_000;
  while (!until() && Date.now() < deadline) await new Promise((resolve) => setTimeout(resolve, 5));
  stop.abort();
  return running;
}

describe("App collector node (serve)", () => {
  it("does not lose a failure outbox when the server already granted another device the next lease", async () => {
    const bindings = resolved(),
      a = bindings[0]!;
    const b = bindings.find(
      (other) => other.device.id !== a.device.id && other.platformCode === a.platformCode,
    )!;
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane(bindings),
        task = taskFor(a);
      const report = cp.reportFailure;
      cp.reportFailure = async (...args) => {
        await report(...args);
        throw new AppControlPlaneError("unavailable");
      };
      cp.queue.push(task);
      await node(
        {
          bindings: [a],
          controlPlane: cp,
          store,
          attempt: recordingAttempt(store, () => "precheck_failed"),
        },
        () => cp.routes("failures").length > 0,
      );
      cp.queue.push({ ...task, bindingId: bindingIdOf(b), leaseVersion: 2 });
      const options = {
        bindings: [b],
        controlPlane: cp,
        store,
        attempt: recordingAttempt(store, () => ({})),
      };
      await node(options, () => cp.results.length > 0);
      expect((await store.readTaskState(task.id, 1))?.failure?.pending).toBe(true);
      expect(await store.readTaskState(task.id)).toMatchObject({
        leaseVersion: 2,
        state: "submitted",
      });
      const count = cp.routes("failures").length;
      cp.reportFailure = report;
      await node(options, () => cp.routes("failures").length > count);
      expect(cp.routes("failures").at(-1)).toMatchObject({
        bindingId: task.bindingId,
        sendState: "not_sent",
        task: { taskId: task.id, leaseVersion: 1 },
      });
      expect(await store.pendingTaskStates()).toHaveLength(0);
      expect(cp.results).toHaveLength(1);
    } finally {
      await store.close();
    }
  });
  it("applies a server device cooldown across that VM's Apps, not the other VM", async () => {
    const bindings = resolved();
    const a = bindings[0]!;
    const b = bindings.find(
      (binding) => binding.device.id !== a.device.id && binding.platformCode === a.platformCode,
    )!;
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane(bindings);
      cp.queue.push(taskFor(b));
      const original = cp.lease;
      const cooling = new Set(
        bindings.filter((binding) => binding.device.id === a.device.id).map(bindingIdOf),
      );
      const denied: string[] = [];
      cp.lease = async (body, signal) => {
        if (cooling.has(body.bindingId)) {
          denied.push(body.bindingId);
          return { task: null, reason: "device_cooldown", retryAfterSeconds: 100 };
        }
        return original(body, signal);
      };
      await node(
        { bindings, store, controlPlane: cp, attempt: recordingAttempt(store, () => ({})) },
        () => cp.results.length > 0 && denied.length > 0,
      );
      expect(denied).toHaveLength(1);
      expect(cp.results[0]?.device.deviceId).toBe(b.device.id);
    } finally {
      await store.close();
    }
  });
  it("does not lease or deliver task outboxes to an incompatible backend", async () => {
    const binding = resolved()[0]!;
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane([binding]);
      let reports = 0;
      cp.reportBindings = async () => {
        reports++;
        throw new AppControlPlaneError("unavailable", 200);
      };
      const task = taskFor(binding);
      await store.writeTaskState({
        version: 1,
        taskId: task.id,
        leaseVersion: 1,
        bindingId: task.bindingId,
        bindingKey: binding.key,
        deviceId: binding.device.id,
        nodeId: NODE,
        batchId: "node-2026-11-17",
        answerId: randomUUID(),
        state: "started",
        sendState: "sent",
        pendingSubmit: false,
        pendingSent: true,
        sentAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      });
      await node(
        {
          bindings: [binding],
          store,
          controlPlane: cp,
          attempt: recordingAttempt(store, () => ({})),
        },
        () => reports > 0,
      );
      expect(cp.routes("leases")).toHaveLength(0);
      expect(cp.routes("sent")).toHaveLength(0);
      expect(cp.routes("failures")).toHaveLength(0);
      expect((await store.readTaskState(task.id))?.pendingSent).toBe(true);
    } finally {
      await store.close();
    }
  });
  it.each(["sent", "not_sent", "unknown", "login", "safety"] as const)(
    "asks again on another device under a new lease unless a safety stop closed the task: %s",
    async (kind) => {
      const bindings = resolved();
      const first = bindings[0]!;
      const second = bindings.find(
        (b) => b.device.id !== first.device.id && b.platformCode === first.platformCode,
      )!;
      const store = await EvidenceStore.open(await evidenceDirectory(created));
      try {
        const cp = new FakeControlPlane(bindings);
        const task = taskFor(first);
        const timestamp = new Date().toISOString();
        const attempts: AttemptRequest[] = [];
        const fail = async (request: AttemptRequest): Promise<AttemptOutcome> => {
          attempts.push(request);
          if (kind === "sent" || kind === "unknown") await request.onSendIntent!();
          if (kind === "sent") await request.onConfirmedSend!(timestamp);
          return {
            answerId: null,
            status: "failed",
            failureCode:
              kind === "safety"
                ? "challenge_required"
                : kind === "login"
                  ? "login_required"
                  : "collection_timeout",
            phase: kind === "sent" || kind === "unknown" ? "post_send" : "pre_send",
          };
        };
        cp.queue.push(task);
        const options = { controlPlane: cp, store };
        await node(
          { ...options, bindings: [first], attempt: fail },
          () => cp.routes("failures").length === 1,
        );
        expect(cp.routes("failures")[0]).toMatchObject({
          sendState: kind === "safety" || kind === "login" ? "not_sent" : kind,
          task: { taskId: task.id, leaseVersion: 1 },
          ...(kind === "sent" ? { sentAt: timestamp } : {}),
        });
        expect(cp.routes("sent")).toHaveLength(kind === "sent" ? 1 : 0);
        // A repeated grant of the very same tuple must not repeat the physical attempt.
        cp.queue.push(task);
        await node({ ...options, bindings: [first], attempt: fail }, () => cp.queue.length === 0);
        expect(attempts).toHaveLength(1);
        cp.queue.push({ ...task, bindingId: bindingIdOf(second), leaseVersion: 2 });
        await node(
          {
            ...options,
            bindings: [second],
            attempt: recordingAttempt(store, () => ({}), attempts),
          },
          () => cp.results.length > 0 || cp.routes("failures").length > 1,
        );
        expect(await store.readTaskState(task.id, 1)).toMatchObject({
          bindingKey: first.key,
          state: "reported",
        });
        if (kind !== "safety") {
          expect(attempts).toHaveLength(2);
          expect(cp.results[0]).toMatchObject({
            taskId: task.id,
            leaseVersion: 2,
            device: { deviceId: second.device.id },
          });
        } else {
          expect(attempts).toHaveLength(1);
          expect(cp.results).toHaveLength(0);
        }
      } finally {
        await store.close();
      }
    },
  );
  it("does not tap when an acknowledged lease expires while the App is opening", async () => {
    const [binding] = resolved();
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane([binding!]);
      const start = Date.now();
      let clock = start;
      cp.queue.push(taskFor(binding!, { leaseExpiresAt: new Date(start + 2000).toISOString() }));
      let taps = 0;
      const attempt = async (r: AttemptRequest): Promise<AttemptOutcome> => {
        clock += 2001;
        try {
          await r.onSendIntent!();
          taps++;
        } catch (error) {
          expect(error).toBeInstanceOf(AppChannelError);
        }
        return {
          answerId: null,
          status: "failed",
          failureCode: "collection_timeout",
          phase: "pre_send",
        };
      };
      await node(
        { bindings: [binding!], store, controlPlane: cp, now: () => new Date(clock), attempt },
        () => cp.routes("failures").length > 0,
      );
      expect(taps).toBe(0);
      expect(cp.routes("failures")[0]).toMatchObject({ sendState: "not_sent" });
    } finally {
      await store.close();
    }
  });
  it("retains the confirmed send through a lost receipt and replays it without tapping", async () => {
    const binding = resolved()[0]!;
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane([binding]);
      const sent = cp.reportSent;
      let down = true;
      cp.reportSent = async (body, signal) => {
        const ack = await sent(body, signal);
        if (down) throw new AppControlPlaneError("unavailable");
        return ack;
      };
      const attempted: AttemptRequest[] = [];
      const options = {
        bindings: [binding],
        controlPlane: cp,
        store,
        attempt: recordingAttempt(store, () => ({}), attempted),
      };
      const t = taskFor(binding);
      cp.queue.push(t, taskFor(binding));
      await node(options, () => cp.results.length === 1);
      expect(await store.readTaskState(t.id, 1)).toMatchObject({
        pendingSent: true,
        state: "submitted",
      });
      expect(cp.queue).toHaveLength(1);
      down = false;
      await node(options, () => cp.results.length === 2);
      expect(attempted.map((r) => r.task?.taskId).filter((id) => id === t.id)).toHaveLength(1);
      const facts = cp.routes<{ taskId: string }>("sent").filter((f) => f.taskId === t.id);
      expect(facts.length).toBeGreaterThan(1);
      expect(facts.every((f) => JSON.stringify(f) === JSON.stringify(facts[0]))).toBe(true);
      expect(await store.readTaskState(t.id)).toMatchObject({ pendingSent: false });
    } finally {
      await store.close();
    }
  });
  it("requires a fresh target-only online check immediately before leasing", async () => {
    const binding = resolved()[0]!;
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane([binding]);
      const checked: (string | undefined)[] = [];
      cp.queue.push(taskFor(binding));
      await node(
        {
          bindings: [binding],
          controlPlane: cp,
          store,
          inspector: {
            async onlineDevices(id) {
              checked.push(id);
              return id ? new Map() : new Map([[binding.device.id, "serial"]]);
            },
          },
          attempt: recordingAttempt(store, () => ({})),
        },
        () => checked.includes(binding.device.id),
      );
      expect(checked).toEqual([undefined, binding.device.id]);
      expect(cp.routes("leases")).toHaveLength(0);
    } finally {
      await store.close();
    }
  });
  it("reports bindings, leases a task, asks exactly the leased question and submits the answer", async () => {
    const bindings = resolved();
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane(bindings);
      const task = taskFor(bindings[0]!);
      cp.queue.push(task);
      const requests: AttemptRequest[] = [];
      const result = await node(
        { controlPlane: cp, store, attempt: recordingAttempt(store, () => ({}), requests) },
        () => cp.results.length > 0,
      );
      expect(result.submitted).toBe(1);
      const report = cp.routes<{ bindings: { health: string }[] }>("bindings/report")[0]!;
      expect(report.bindings).toHaveLength(4);
      expect(report.bindings.every((item) => item.health === "ok")).toBe(true);
      expect(requests).toHaveLength(1);
      expect(requests[0]!.questionText).toBe("合成租户提问");
      expect(requests[0]!.binding.key).toBe(bindings[0]!.key);
      expect(requests[0]!.task).toEqual({ taskId: task.id, leaseVersion: 1 });
      const submitted = cp.results[0]!;
      expect(submitted).toMatchObject({
        nodeId: NODE,
        taskId: task.id,
        leaseVersion: 1,
        idempotencyKey: `${requests[0]!.answerId}.1`,
        answerText: "合成回答",
        citationsStatus: "read",
        citationsIssue: null,
        screenshotSha256: "0".repeat(64),
        device: { deviceId: DEVICE_A, androidVersion: "15", abi: "arm64-v8a" },
        app: { package: "com.larus.nova", version: "15.7.0" },
      });
      expect(await store.readTaskState(task.id)).toMatchObject({
        state: "submitted",
        pendingSubmit: false,
      });
    } finally {
      await store.close();
    }
  });

  it("reports a classified failure with the task and its phase, mapped to the control plane's code", async () => {
    const bindings = resolved();
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane(bindings);
      const task = taskFor(bindings[0]!);
      cp.queue.push(task);
      await node(
        {
          controlPlane: cp,
          store,
          attempt: recordingAttempt(store, () => ({
            status: "failed",
            failureCode: "app_contract_changed",
            phase: "pre_send",
            sentAt: null,
            completedAt: null,
            answerText: null,
            citations: [],
            screenshot: null,
          })),
        },
        () => cp.routes("failures").length > 0,
      );
      expect(cp.routes("failures")[0]).toEqual({
        nodeId: NODE,
        bindingId: task.bindingId,
        failureCode: "contract_changed",
        phase: "pre_send",
        sendState: "not_sent",
        task: { taskId: task.id, leaseVersion: 1 },
      });
      expect(cp.results).toHaveLength(0);
      expect(await store.readTaskState(task.id)).toMatchObject({ state: "reported" });
    } finally {
      await store.close();
    }
  });

  it("recovers a completed answer only under its original lease after restart", async () => {
    const bindings = resolved();
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const task = taskFor(bindings[0]!);
      // Before the restart: answer collected, submission never made.
      const answerId = randomUUID();
      const batchId = "node-2026-11-17";
      await store.writeRecord(
        recordFixture({
          answerId,
          batchId,
          sampleClass: "control_plane",
          task: { taskId: task.id, leaseVersion: 1 },
          video: null,
        }),
      );
      await store.writeTaskState({
        version: 1,
        taskId: task.id,
        leaseVersion: 1,
        bindingKey: bindings[0]!.key,
        batchId,
        answerId,
        state: "started",
        pendingSubmit: false,
        updatedAt: new Date().toISOString(),
      });
      const cp = new FakeControlPlane(bindings);
      cp.queue.push({ ...task, leaseVersion: 2 }, { ...task, leaseVersion: 3 });
      const requests: AttemptRequest[] = [];
      await node(
        { controlPlane: cp, store, attempt: recordingAttempt(store, () => ({}), requests) },
        () => cp.routes("failures").length >= 2,
      );
      expect(requests).toHaveLength(0);
      expect(cp.results).toHaveLength(1);
      expect(cp.results[0]).toMatchObject({ leaseVersion: 1, idempotencyKey: `${answerId}.1` });
    } finally {
      await store.close();
    }
  });

  it("never asks again after a restart when the question may have been sent without a record", async () => {
    const bindings = resolved();
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const task = taskFor(bindings[0]!);
      const answerId = randomUUID();
      await store.appendLedger({
        answerId,
        batchId: "node-2026-11-17",
        deviceId: DEVICE_A,
        platformCode: "doubao",
        sentAt: new Date().toISOString(),
      });
      await store.writeTaskState({
        version: 1,
        taskId: task.id,
        leaseVersion: 1,
        bindingKey: bindings[0]!.key,
        batchId: "node-2026-11-17",
        answerId,
        state: "started",
        pendingSubmit: false,
        updatedAt: new Date().toISOString(),
      });
      const cp = new FakeControlPlane(bindings);
      cp.queue.push({ ...task, leaseVersion: 2 }, { ...task, leaseVersion: 3 });
      const requests: AttemptRequest[] = [];
      await node(
        { controlPlane: cp, store, attempt: recordingAttempt(store, () => ({}), requests) },
        () => cp.routes("failures").length >= 3 || requests.length > 0,
      );
      expect(requests).toHaveLength(0);
      expect(cp.routes("failures")[0]).toMatchObject({
        failureCode: "app_crashed",
        phase: "post_send",
        sendState: "unknown",
        task: { taskId: task.id, leaseVersion: 1 },
      });
      expect(cp.routes("failures")[1]).toMatchObject({
        phase: "pre_send",
        sendState: "not_sent",
        task: { taskId: task.id, leaseVersion: 2 },
      });
    } finally {
      await store.close();
    }
  });

  it("never relabels an answer even if later re-leased to the original account", async () => {
    const bindings = resolved();
    const first = bindings[0]!;
    const other = bindings.find(
      (b) => b.platformCode === first.platformCode && b.device.id !== first.device.id,
    )!;
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane(bindings);
      const task = taskFor(first);
      const attempted: AttemptRequest[] = [];
      const options = {
        store,
        controlPlane: cp,
        attempt: recordingAttempt(store, () => ({}), attempted),
      };
      cp.queue.push(task);
      await node({ ...options, bindings: [first] }, () => cp.results.length === 1);
      cp.queue.push({ ...task, bindingId: bindingIdOf(other), leaseVersion: 2 });
      await node({ ...options, bindings: [other] }, () => cp.routes("failures").length > 0);
      expect(cp.routes("failures")).toEqual([
        {
          nodeId: NODE,
          bindingId: bindingIdOf(other),
          failureCode: "app_crashed",
          phase: "pre_send",
          sendState: "not_sent",
          task: { taskId: task.id, leaseVersion: 2 },
        },
      ]);
      cp.queue.push({ ...task, leaseVersion: 3 });
      await node({ ...options, bindings: [first] }, () => cp.routes("failures").length >= 2);
      expect(cp.results.map((r) => r.idempotencyKey)).toEqual([`${attempted[0]!.answerId}.1`]);
      expect(attempted).toHaveLength(1);
    } finally {
      await store.close();
    }
  });

  it("asks again on another device when the earlier answer came back stale", async () => {
    const bindings = resolved();
    const first = bindings[0]!;
    const other = bindings.find(
      (b) => b.platformCode === first.platformCode && b.device.id !== first.device.id,
    )!;
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane(bindings);
      const task = taskFor(first);
      const attempted: AttemptRequest[] = [];
      const options = {
        store,
        controlPlane: cp,
        attempt: recordingAttempt(store, () => ({}), attempted),
      };
      const accept = cp.submitResult;
      // The lease ran out before the answer arrived: the control plane keeps it for audit only.
      cp.submitResult = async (body, signal) => {
        if (body.leaseVersion !== 1) return accept(body, signal);
        cp.calls.push({ route: "results", body });
        return { disposition: "stale", sampleId: null };
      };
      cp.queue.push(task);
      await node({ ...options, bindings: [first] }, () => cp.routes("results").length === 1);
      cp.queue.push({ ...task, bindingId: bindingIdOf(other), leaseVersion: 2 });
      await node({ ...options, bindings: [other] }, () => cp.results.length === 1);
      expect(attempted).toHaveLength(2);
      expect(cp.results[0]).toMatchObject({
        taskId: task.id,
        leaseVersion: 2,
        device: { deviceId: other.device.id },
      });
      expect(cp.routes("failures")).toHaveLength(0);
    } finally {
      await store.close();
    }
  });

  it("finishes an old outbox submission before writing the state of the new lease", async () => {
    const binding = resolved()[0]!;
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const task = taskFor(binding);
      const cp = new FakeControlPlane([binding]);
      const attempted: AttemptRequest[] = [];
      const options = {
        store,
        controlPlane: cp,
        bindings: [binding],
        attempt: recordingAttempt(store, () => ({}), attempted),
      };
      cp.queue.push(task);
      cp.submitFailure = new AppControlPlaneError("unavailable", 503);
      await node(options, () => cp.routes("results").length > 0);
      cp.submitFailure = undefined;
      const started = Promise.withResolvers<void>();
      const finish = Promise.withResolvers<void>();
      const accepted = cp.submitResult;
      cp.submitResult = async (body, signal) => {
        if (body.leaseVersion === 2) return accepted(body, signal);
        started.resolve();
        await finish.promise;
        return { disposition: "duplicate", sampleId: null }; // earlier stale submission was recorded
      };
      const lease = cp.lease;
      cp.queue.push({ ...task, leaseVersion: 2 });
      cp.lease = async (...args) => {
        await started.promise;
        return lease(...args);
      };
      const run = await node(
        {
          ...options,
          log: (entry) => {
            if (entry.event === "app_task_leased") finish.resolve();
          },
        },
        () => cp.routes("failures").length >= 1,
      );
      expect(run.submitted).toBe(0);
      expect(await store.readTaskState(task.id)).toMatchObject({
        leaseVersion: 2,
        state: "reported",
        pendingSubmit: false,
      });
      expect(attempted).toHaveLength(1);
    } finally {
      await store.close();
    }
  });

  it("keeps a lost failure response across shutdown, blocks that binding, and retries the original lease before recovery", async () => {
    const [binding] = resolved();
    const bindings = [binding!];
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane(bindings);
      const task = taskFor(binding!);
      const received: Parameters<AppControlPlane["reportFailure"]>[0][] = [];
      let disconnected = true;
      cp.reportFailure = async (body) => {
        received.push(body);
        if (disconnected) throw new AppControlPlaneError("unavailable", 503);
        cp.stopReasons.set(binding!.key, "challenge_required");
        return {
          bindingStatus: "stopped",
          stopReason: "challenge_required",
          cooldownUntil: null,
          taskDisposition: "stale",
          platformPaused: false,
          platformUnavailable: false,
        };
      };
      cp.queue.push(task, { ...task, leaseVersion: 2 });
      const attempted: AttemptRequest[] = [];
      const options = {
        bindings,
        store,
        controlPlane: cp,
        reportIntervalMs: 15,
        attempt: async (request: AttemptRequest): Promise<AttemptOutcome> => {
          attempted.push(request);
          return {
            answerId: null,
            status: "failed",
            failureCode: "challenge_required",
            phase: "pre_send",
          };
        },
      };
      await node(options, () => cp.routes("bindings/report").length >= 3 && received.length >= 1);
      expect(attempted).toHaveLength(1);
      expect((await store.pendingTaskStates()).map((s) => s.taskId)).toEqual([task.id]);
      const sent = received[0];
      expect(sent).toMatchObject({
        bindingId: task.bindingId,
        failureCode: "challenge_required",
        phase: "pre_send",
        task: { taskId: task.id, leaseVersion: 1 },
      });
      disconnected = false;
      const firstRun = received.length;
      await node(
        options,
        () => received.length > firstRun && cp.routes("bindings/report").length >= 5,
      );
      expect(received.slice(firstRun)).toEqual([sent]);
      expect(await store.pendingTaskStates()).toHaveLength(0);
      expect(attempted).toHaveLength(1);
      // The operator recovered the binding; there is positive evidence that lease 1 never sent.
      cp.stopReasons.clear();
      await node(
        { ...options, attempt: recordingAttempt(store, () => ({}), attempted) },
        () => cp.results.length > 0,
      );
      expect(attempted).toHaveLength(2);
      expect(cp.results[0]?.leaseVersion).toBe(2);
    } finally {
      await store.close();
    }
  });

  it("keeps an answer whose submission failed and submits it once the control plane is back", async () => {
    const bindings = resolved();
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane(bindings);
      cp.submitFailure = new AppControlPlaneError("unavailable", 503);
      const task = taskFor(bindings[0]!);
      cp.queue.push(task);
      await node(
        { controlPlane: cp, store, attempt: recordingAttempt(store, () => ({})) },
        () => cp.routes("results").length > 0,
      );
      expect(cp.results).toHaveLength(0);
      expect(await store.pendingTaskStates()).toHaveLength(1);
      // Next start, control plane back: the pending answer goes out.
      cp.submitFailure = undefined;
      await node(
        { controlPlane: cp, store, attempt: recordingAttempt(store, () => ({})) },
        () => cp.results.length > 0,
      );
      expect(cp.results[0]).toMatchObject({ taskId: task.id, leaseVersion: 1 });
      expect(await store.pendingTaskStates()).toHaveLength(0);
    } finally {
      await store.close();
    }
  });

  it("holds an outbox answer across an account change until its original binding is reported", async () => {
    const binding = resolved()[0]!;
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane([binding]);
      const requests: AttemptRequest[] = [];
      const options = {
        store,
        controlPlane: cp,
        bindings: [binding],
        attempt: recordingAttempt(store, () => ({}), requests),
      };
      const task = taskFor(binding);
      cp.queue.push(task);
      cp.submitFailure = new AppControlPlaneError("unavailable", 503);
      await node(options, () => cp.routes("results").length > 0);
      const submissions = cp.routes("results").length;
      cp.submitFailure = undefined;
      const reports = cp.routes("bindings/report").length;
      await node(
        { ...options, bindings: [{ ...binding, accountLabel: "replacement" }] },
        () => cp.routes("bindings/report").length > reports,
      );
      expect(cp.routes("results")).toHaveLength(submissions);
      expect(await store.readTaskState(task.id)).toMatchObject({ pendingSubmit: true });
      // Restoring the local file alone cannot confirm the server has the original account again.
      const report = cp.reportBindings;
      let reportFailed = false;
      cp.reportBindings = async () => {
        reportFailed = true;
        throw new AppControlPlaneError("unavailable", 503);
      };
      await node(options, () => reportFailed);
      expect(cp.routes("results")).toHaveLength(submissions);
      cp.reportBindings = report;
      await node(options, () => cp.results.length === 1);
      expect(cp.results[0]).toMatchObject({ taskId: task.id, leaseVersion: 1 });
      expect(requests).toHaveLength(1);
    } finally {
      await store.close();
    }
  });

  it("does not lease for a binding the control plane has stopped, or for an offline device", async () => {
    const bindings = resolved();
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane(bindings);
      cp.stopReasons.set(bindings[0]!.key, "login_required");
      const requests: AttemptRequest[] = [];
      await node(
        {
          controlPlane: cp,
          store,
          inspector: onlineInspector(bindings, [DEVICE_B]),
          attempt: recordingAttempt(store, () => ({}), requests),
        },
        () => cp.routes("leases").length >= 1,
      );
      const leased = new Set(
        cp.routes<{ bindingId: string }>("leases").map((body) => body.bindingId),
      );
      expect(leased.has(bindingIdOf(bindings[0]!))).toBe(false);
      // Device B is offline: reported as device_offline, so stopped and never leased.
      const report = cp.routes<{ bindings: { deviceId: string; health: string }[] }>(
        "bindings/report",
      )[0]!;
      expect(
        report.bindings
          .filter((item) => item.deviceId === DEVICE_B)
          .every((item) => item.health === "device_offline"),
      ).toBe(true);
      for (const binding of bindings.filter((candidate) => candidate.device.id === DEVICE_B))
        expect(leased.has(bindingIdOf(binding))).toBe(false);
      expect(requests).toHaveLength(0);
    } finally {
      await store.close();
    }
  });

  it("stops leasing on a stop request and lets the attempt in flight finish and submit", async () => {
    const bindings = resolved();
    const store = await EvidenceStore.open(await evidenceDirectory(created));
    try {
      const cp = new FakeControlPlane(bindings);
      cp.queue.push(taskFor(bindings[0]!));
      const stop = new AbortController();
      let release!: () => void;
      const started = new Promise<void>((resolve) => (release = resolve));
      const inner = recordingAttempt(store, () => ({}));
      const running = runNode({
        nodeId: NODE,
        bindings,
        inspector: onlineInspector(bindings),
        revision: "r",
        stop: stop.signal,
        idleMaxMs: 5,
        graceMs: 1_000,
        controlPlane: cp,
        store,
        attempt: async (request) => {
          release();
          await new Promise((resolve) => setTimeout(resolve, 50));
          return inner(request);
        },
      });
      await started;
      stop.abort();
      const leasesAtStop = cp.routes("leases").length;
      await running;
      expect(cp.results).toHaveLength(1);
      expect(cp.routes("leases")).toHaveLength(leasesAtStop);
    } finally {
      await store.close();
    }
  });
});

describe("mapping to the control plane", () => {
  it("maps local failure codes (design §7)", () => {
    expect(controlPlaneFailureCode("device_config_mismatch")).toBe("device_offline");
    expect(controlPlaneFailureCode("app_contract_changed")).toBe("contract_changed");
    expect(controlPlaneFailureCode("app_contract_unverified")).toBe("contract_unverified");
    expect(controlPlaneFailureCode("recording_failed")).toBe("collection_timeout");
    expect(controlPlaneFailureCode("challenge_required")).toBe("challenge_required");
  });
  it("builds a result the contract accepts, with unreadable sources and an incomplete screenshot", () => {
    const record = recordFixture({
      status: "evidence_incomplete",
      evidenceIssue: "screenshot_failed",
      phase: "post_send",
      screenshot: null,
      video: null,
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "driver_timeout",
      completedAt: timestampPair(new Date("2026-11-15T04:01:20.000Z")),
    });
    const body = resultSubmission(record, {
      nodeId: NODE,
      taskId: randomUUID(),
      leaseVersion: 3,
      revision: "r",
      now: new Date(),
    });
    expect(body).toMatchObject({
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "driver_timeout",
      screenshotSha256: null,
      sentAt: "2026-11-15T04:00:20.000Z",
    });
    // Over-long URLs are dropped, not cut; site and list become null when absent.
    const long = resultSubmission(
      recordFixture({
        citations: [
          { title: "  标题 ", site: " ", url: `https://example.com/${"a".repeat(2100)}` },
        ],
        citationsStatus: "read",
      }),
      { nodeId: NODE, taskId: randomUUID(), leaseVersion: 1, revision: "r", now: new Date() },
    );
    expect(long.citations).toEqual([{ title: "标题", site: null, url: null, list: null }]);
  });
});
