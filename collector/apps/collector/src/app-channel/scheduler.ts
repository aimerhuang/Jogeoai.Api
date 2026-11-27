import type { AttemptOutcome, AttemptRunner } from "./adapter.ts";
import type { AppPlatformCode, ResolvedBinding } from "./bindings.ts";
import { sha256Text, type AppRecord } from "./evidence.ts";
import type { AppFailureCode } from "./failure.ts";
import type { GateBlock, RateGate } from "./rate-gate.ts";
import { realSleep, type Sleep } from "./time.ts";

// Batch scheduling (PRD FR-A4, design §8). One process, one serial loop per device: a device
// never runs two attempts at once, and the loops of different devices run side by side. The unit
// of work is a cell, one question on one platform; a cell is taken out of the pending set inside
// the single-threaded event loop, so two devices never work on the same one.

type Log = (line: Record<string, unknown>) => void;

export interface Cell {
  platformCode: AppPlatformCode;
  questionText: string;
}
// Identity of a cell in batch.json, logs and the pending set: the question appears as a hash.
export function questionHash(questionText: string): string {
  return sha256Text(questionText);
}
export function cellKey(platformCode: string, questionText: string): string {
  return `${platformCode}/${questionHash(questionText)}`;
}

// Cells that still need a sample: every question on every platform, minus those that already
// have a `valid` record in this batch. `failed` and `evidence_incomplete` records leave a cell
// pending, so it is collected again.
export function pendingCells(
  questions: readonly string[],
  platforms: readonly AppPlatformCode[],
  records: readonly Pick<AppRecord, "status" | "platformCode" | "questionText">[],
): Cell[] {
  const done = new Set(
    records
      .filter((record) => record.status === "valid")
      .map((record) => cellKey(record.platformCode, record.questionText)),
  );
  return platforms.flatMap((platformCode) =>
    questions
      .filter((questionText) => !done.has(cellKey(platformCode, questionText)))
      .map((questionText) => ({ platformCode, questionText })),
  );
}

// Why a binding or device takes no more work in this run. Gate blocks that are counted in days
// end the run for the binding instead of making the batch wait. A rate-limit stop and the
// platform cap last until Asia/Shanghai midnight. A binding's daily quota is a rolling 24 hours
// (rate-gate.ts): it frees one send at a time, usually hours away, but only minutes away when a
// run starts about a day after the previous one. The binding is stopped for this run in both
// cases, which never sends more than the limits allow; its cells are reported as not collected
// and a later run of the same batch goes on with them.
const DAY_BLOCKS = ["rate_limited_today", "binding_daily_quota", "platform_daily_cap"] as const;
type DayBlock = Extract<GateBlock, (typeof DAY_BLOCKS)[number]>;
export type StopReason = AppFailureCode | DayBlock;
const isDayBlock = (reason: GateBlock): reason is DayBlock =>
  (DAY_BLOCKS as readonly GateBlock[]).includes(reason);
const MAX_CONSECUTIVE = 2;

export interface BatchOptions {
  batchId: string;
  // Enabled bindings in scope, in configuration order.
  bindings: readonly ResolvedBinding[];
  // Pending cells in the order they should be asked (see `pendingCells`).
  cells: readonly Cell[];
  gate: RateGate;
  attempt: AttemptRunner;
  // Stop request (SIGINT/SIGTERM): no new attempt starts; attempts in flight get `graceMs`
  // (default 120 s) to finish and are then aborted, which ends them as failures with a record.
  stop?: AbortSignal;
  graceMs?: number;
  // Upper bound of attempts started in this run.
  limit?: number;
  // A cell whose question was sent this often without a valid sample is left for the next run
  // (default 2).
  maxAttemptsPerCell?: number;
  now?: () => Date;
  sleep?: Sleep;
  log?: Log;
}
export interface BatchResult {
  attempts: number;
  valid: number;
  // Cells of this run's plan that end without a valid sample.
  notCollected: number;
  notCollectedByPlatform: Partial<Record<AppPlatformCode, number>>;
  stoppedBindings: Record<string, StopReason>;
  stoppedDevices: Record<string, StopReason>;
  interrupted: boolean;
}

export async function runBatch(options: BatchOptions): Promise<BatchResult> {
  const { batchId, bindings, gate } = options;
  const now = options.now ?? (() => new Date());
  const sleep = options.sleep ?? realSleep;
  const log = options.log ?? (() => undefined);
  const limit = options.limit ?? Number.POSITIVE_INFINITY;
  const maxAttemptsPerCell = options.maxAttemptsPerCell ?? 2;

  // Platform -> questions still to ask, in order.
  const pending = new Map<AppPlatformCode, string[]>();
  for (const cell of options.cells)
    pending.set(cell.platformCode, [...(pending.get(cell.platformCode) ?? []), cell.questionText]);
  const planned = options.cells.length;
  const validByPlatform = new Map<AppPlatformCode, number>();
  // Cells being worked on, and how often each was sent without a valid sample in this run.
  const inFlight = new Map<string, AppPlatformCode>();
  const tries = new Map<string, number>();
  // Platforms with an attempt that has not sent yet. The platform interval is counted from the
  // send, so the other device waits until that send is in the ledger (or the attempt ended).
  const sending = new Set<AppPlatformCode>();
  const stoppedBindings = new Map<string, StopReason>();
  const stoppedDevices = new Map<string, StopReason>();
  const crashes = new Map<string, number>();
  const recordingFailures = new Map<string, number>();
  let attempts = 0;
  let valid = 0;
  let stopping = false;
  let fatal: { error: unknown } | undefined;

  // Wakes every waiting loop: something a decision depends on has changed.
  const waiters: (() => void)[] = [];
  const wake = () => {
    for (const waiter of waiters.splice(0)) waiter();
  };
  const changed = () => new Promise<void>((resolve) => void waiters.push(resolve));

  const attemptAbort = new AbortController();
  const graceAbort = new AbortController();
  const requestStop = () => {
    if (stopping) return;
    stopping = true;
    log({ event: "app_batch_stopping", batchId, inFlight: inFlight.size });
    wake();
    void sleep(options.graceMs ?? 120_000, graceAbort.signal).then(() => attemptAbort.abort());
  };
  if (options.stop?.aborted) requestStop();
  else options.stop?.addEventListener("abort", requestStop, { once: true });

  const stopBinding = (binding: ResolvedBinding, reason: StopReason) => {
    if (stoppedBindings.has(binding.key)) return;
    stoppedBindings.set(binding.key, reason);
    log({
      event: "app_binding_stopped",
      batchId,
      device: binding.device.id,
      platform: binding.platformCode,
      reason,
    });
  };
  const stopDevice = (deviceId: string, reason: StopReason) => {
    if (stoppedDevices.has(deviceId)) return;
    stoppedDevices.set(deviceId, reason);
    log({ event: "app_device_stopped", batchId, device: deviceId, reason });
  };

  // The one place an attempt's result changes scheduling state (design §10).
  const apply = (binding: ResolvedBinding, questionText: string, outcome: AttemptOutcome) => {
    const deviceId = binding.device.id;
    const key = cellKey(binding.platformCode, questionText);
    // An attempt that had no recording says nothing about the App, so it does not end a crash
    // streak: otherwise crashes and recording failures taking turns would retry without end,
    // since neither sends anything and so neither is slowed down by the limits.
    if (outcome.failureCode !== "app_crashed" && outcome.failureCode !== "recording_failed")
      crashes.delete(binding.key);
    if (outcome.failureCode !== "recording_failed") recordingFailures.delete(deviceId);
    if (outcome.status === "valid") {
      valid += 1;
      validByPlatform.set(
        binding.platformCode,
        (validByPlatform.get(binding.platformCode) ?? 0) + 1,
      );
      return;
    }
    // Not collected yet: back to the front of its platform's queue. Only attempts that sent the
    // question use up the cell's tries; a failure before the send says nothing about the cell
    // and always ends in a stopped binding or device.
    if (outcome.phase === "post_send") tries.set(key, (tries.get(key) ?? 0) + 1);
    if ((tries.get(key) ?? 0) < maxAttemptsPerCell)
      pending.get(binding.platformCode)!.unshift(questionText);
    switch (outcome.failureCode) {
      case null:
        // evidence_incomplete: the answer was read but its evidence is unusable.
        return;
      case "collection_timeout":
        return;
      case "device_offline":
      case "device_config_mismatch":
        stopDevice(deviceId, outcome.failureCode);
        return;
      case "app_crashed": {
        const count = (crashes.get(binding.key) ?? 0) + 1;
        crashes.set(binding.key, count);
        if (count >= MAX_CONSECUTIVE) stopBinding(binding, outcome.failureCode);
        return;
      }
      case "recording_failed": {
        const count = (recordingFailures.get(deviceId) ?? 0) + 1;
        recordingFailures.set(deviceId, count);
        if (count >= MAX_CONSECUTIVE) stopDevice(deviceId, outcome.failureCode);
        return;
      }
      case "login_required":
      case "challenge_required":
      case "rate_limited":
      case "app_contract_changed":
      case "app_contract_unverified":
        stopBinding(binding, outcome.failureCode);
        return;
      case "emulator_rejected":
        // The platform's App entry is unavailable on every device.
        for (const other of bindings)
          if (other.platformCode === binding.platformCode) stopBinding(other, outcome.failureCode);
        return;
    }
  };

  const run = async (binding: ResolvedBinding, questionText: string) => {
    const platformCode = binding.platformCode;
    const key = cellKey(platformCode, questionText);
    attempts += 1;
    inFlight.set(key, platformCode);
    sending.add(platformCode);
    const sent = () => {
      if (sending.delete(platformCode)) wake();
    };
    const identity = {
      batchId,
      device: binding.device.id,
      platform: platformCode,
      question: questionHash(questionText).slice(0, 12),
    };
    log({ event: "app_attempt_scheduled", ...identity, at: now().toISOString() });
    try {
      const outcome = await options.attempt({
        batchId,
        binding,
        questionText,
        signal: attemptAbort.signal,
        // A stop request at once, so that what can be cut short ends before the grace period.
        stopping: options.stop,
        onSent: sent,
      });
      inFlight.delete(key);
      apply(binding, questionText, outcome);
      log({
        event: "app_attempt_done",
        ...identity,
        at: now().toISOString(),
        answerId: outcome.answerId,
        status: outcome.status,
        failureCode: outcome.failureCode,
        phase: outcome.phase,
      });
    } catch (error) {
      // Not a classified failure: the whole run ends after the other loops have stopped.
      inFlight.delete(key);
      pending.get(platformCode)!.unshift(questionText);
      fatal ??= { error };
      requestStop();
    } finally {
      sent();
      wake();
    }
  };

  const deviceLoop = async (deviceId: string) => {
    for (;;) {
      if (stopping || stoppedDevices.has(deviceId) || attempts >= limit) return;
      const mine = bindings.filter(
        (binding) => binding.device.id === deviceId && !stoppedBindings.has(binding.key),
      );
      const hasWork = (binding: ResolvedBinding) =>
        (pending.get(binding.platformCode)?.length ?? 0) > 0;
      // A cell another device is working on may come back, so its platform still counts.
      const mayGetWork = (binding: ResolvedBinding) =>
        hasWork(binding) || [...inFlight.values()].includes(binding.platformCode);
      if (!mine.some(mayGetWork)) return;

      const ready: ResolvedBinding[] = [];
      let earliest: number | undefined;
      let stoppedOne = false;
      for (const binding of mine.filter(hasWork)) {
        if (sending.has(binding.platformCode)) continue;
        const decision = gate.decide(binding);
        if (decision.allowed) ready.push(binding);
        else if (isDayBlock(decision.reason)) {
          stopBinding(binding, decision.reason);
          stoppedOne = true;
        } else earliest = Math.min(earliest ?? Infinity, decision.retryAt.getTime());
      }
      if (stoppedOne) continue;
      if (ready.length > 0) {
        // The binding that has waited longest; one that never sent comes first.
        const waitedSince = (binding: ResolvedBinding) =>
          gate.lastSentAt(binding)?.getTime() ?? Number.NEGATIVE_INFINITY;
        const binding = ready.reduce((best, candidate) =>
          waitedSince(candidate) < waitedSince(best) ? candidate : best,
        );
        await run(binding, pending.get(binding.platformCode)!.shift()!);
        continue;
      }
      // Nothing is allowed yet: sleep until the earliest limit clears or something changes.
      const timer = new AbortController();
      await Promise.race([
        changed(),
        ...(earliest === undefined
          ? []
          : [sleep(Math.max(0, earliest - now().getTime()), timer.signal)]),
      ]);
      timer.abort();
    }
  };

  const deviceIds = [...new Set(bindings.map((binding) => binding.device.id))];
  log({
    event: "app_batch_started",
    batchId,
    devices: deviceIds.length,
    bindings: bindings.length,
    cells: planned,
  });
  await Promise.all(deviceIds.map((deviceId) => deviceLoop(deviceId)));
  graceAbort.abort();
  options.stop?.removeEventListener("abort", requestStop);
  if (fatal) throw fatal.error;

  // What this run planned minus what became valid, per platform.
  const notCollectedByPlatform: Partial<Record<AppPlatformCode, number>> = {};
  for (const cell of options.cells)
    notCollectedByPlatform[cell.platformCode] =
      (notCollectedByPlatform[cell.platformCode] ?? 0) + 1;
  for (const platformCode of Object.keys(notCollectedByPlatform) as AppPlatformCode[]) {
    const left = notCollectedByPlatform[platformCode]! - (validByPlatform.get(platformCode) ?? 0);
    if (left > 0) {
      notCollectedByPlatform[platformCode] = left;
      log({ event: "app_platform_not_collected", batchId, platform: platformCode, cells: left });
    } else delete notCollectedByPlatform[platformCode];
  }
  const result: BatchResult = {
    attempts,
    valid,
    notCollected: planned - valid,
    notCollectedByPlatform,
    stoppedBindings: Object.fromEntries(stoppedBindings),
    stoppedDevices: Object.fromEntries(stoppedDevices),
    interrupted: stopping,
  };
  log({ event: "app_batch_finished", batchId, ...result });
  return result;
}
