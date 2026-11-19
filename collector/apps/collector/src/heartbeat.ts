import { setTimeout as delay } from "node:timers/promises";
import type { CollectorConfig } from "@aivc/config";
import type { LeasedTask } from "@aivc/contracts";
import type { ControlPlane } from "./control-plane-client.ts";
import { CollectorError } from "./platform-adapter.ts";

export interface LeaseControls {
  // Moves the local collection deadline by `ms` (negative returns unused time). Used only by
  // the operator challenge wait; heartbeats keep renewing the lease meanwhile. No-op once the
  // work has finished or ownership is lost.
  extendDeadline(ms: number): void;
}

export async function withHeartbeat<T>(
  task: LeasedTask,
  config: CollectorConfig,
  client: ControlPlane,
  parent: AbortSignal,
  work: (signal: AbortSignal, lease: LeaseControls) => Promise<T>,
): Promise<T> {
  const ownership = new AbortController();
  const loopStop = new AbortController();
  const signal = AbortSignal.any([parent, ownership.signal]);
  let expiryTimer: ReturnType<typeof setTimeout>;
  const setExpiry = (expiresAt: string) => {
    clearTimeout(expiryTimer);
    const remaining = Date.parse(expiresAt) - Date.now();
    if (!Number.isFinite(remaining) || remaining <= 0) throw new CollectorError("stale_lease");
    expiryTimer = setTimeout(() => ownership.abort(new CollectorError("stale_lease")), remaining);
  };
  setExpiry(task.leaseExpiresAt);
  let deadlineAt = Date.now() + config.COLLECTOR_COLLECTION_TIMEOUT_MS;
  let deadlineTimer: ReturnType<typeof setTimeout> | undefined;
  let finished = false;
  const armDeadline = () => {
    clearTimeout(deadlineTimer);
    deadlineTimer = setTimeout(
      () => ownership.abort(new CollectorError("collection_timeout")),
      Math.max(0, deadlineAt - Date.now()),
    );
  };
  armDeadline();
  const lease: LeaseControls = {
    extendDeadline(ms) {
      if (finished || signal.aborted || !Number.isFinite(ms) || ms === 0) return;
      deadlineAt += ms;
      armDeadline();
    },
  };
  const heartbeatSignal = AbortSignal.any([signal, loopStop.signal]);
  let initialRenewal: Promise<void>;
  const renew = async () => {
    setExpiry((await client.heartbeat(task, heartbeatSignal)).leaseExpiresAt);
  };
  const loop = (async () => {
    try {
      initialRenewal = renew();
      await initialRenewal;
      while (!heartbeatSignal.aborted) {
        await delay(config.COLLECTOR_HEARTBEAT_INTERVAL_MS, undefined, { signal: heartbeatSignal });
        await renew();
      }
    } catch (error) {
      if (!loopStop.signal.aborted)
        ownership.abort(
          error instanceof CollectorError ? error : new CollectorError("control_plane_unavailable"),
        );
    }
  })();
  let onAbort: () => void = () => undefined;
  try {
    await initialRenewal!;
    signal.throwIfAborted();
    const cancelled = new Promise<never>((_, reject) => {
      onAbort = () =>
        reject(
          signal.reason instanceof CollectorError ? signal.reason : new CollectorError("stopped"),
        );
      signal.addEventListener("abort", onAbort, { once: true });
      if (signal.aborted) onAbort();
    });
    return await Promise.race([work(signal, lease), cancelled]);
  } finally {
    finished = true;
    loopStop.abort();
    clearTimeout(expiryTimer!);
    clearTimeout(deadlineTimer);
    signal.removeEventListener("abort", onAbort);
    await loop;
  }
}
