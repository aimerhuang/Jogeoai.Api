import { bindingKey, type ResolvedBinding } from "./bindings.ts";
import type { BindingStatus, LedgerEntry } from "./evidence.ts";
import { nextShanghaiMidnight, shanghaiDay } from "./time.ts";

// Two-level send limits (PRD FR-A5, design §7; values changed by KD-21 on 2026-11-17). Every
// counter is derived from the send ledger, so restarting the script never resets a count. The two
// intervals always apply; the counts only where the bindings file sets them: a binding's daily
// quota is the last 24 hours and its hourly quota the last 60 minutes, like a Web account's; the
// platform's daily cap and a rate-limit stop follow the Asia/Shanghai calendar day.

const HOUR_MS = 3_600_000;
const DAY_MS = 24 * HOUR_MS;

export type GateBlock =
  | "rate_limited_today"
  | "binding_min_interval"
  | "binding_hourly_quota"
  | "binding_daily_quota"
  | "platform_min_interval"
  | "platform_daily_cap";
// `retryAt` is the earliest moment the named condition can clear.
export type GateDecision = { allowed: true } | { allowed: false; reason: GateBlock; retryAt: Date };

export interface BindingCounts {
  bindingLastHour: number;
  bindingLast24h: number;
  platformToday: number;
}

export interface RateGateOptions {
  // `EvidenceStore.readLedger()` and `readBindingStatus()` at startup.
  ledger: readonly LedgerEntry[];
  bindingStatus: BindingStatus;
  now?: () => Date;
}

export class RateGate {
  private readonly sent: { key: string; platformCode: string; at: number }[];
  // Binding key -> Asia/Shanghai day it is stopped for.
  private readonly stoppedDay: Map<string, string>;
  private readonly now: () => Date;

  constructor(options: RateGateOptions) {
    this.sent = options.ledger.map(toSent);
    this.stoppedDay = new Map(
      Object.entries(options.bindingStatus.bindings).map(([key, entry]) => [key, entry.day]),
    );
    this.now = options.now ?? (() => new Date());
  }

  // Mirror of `EvidenceStore.appendLedger`: call it with the same entry once the append resolved.
  noteSent(entry: LedgerEntry): void {
    this.sent.push(toSent(entry));
  }
  // Mirror of `EvidenceStore.markBindingRateLimited`.
  noteRateLimited(key: string, at: Date): void {
    this.stoppedDay.set(key, shanghaiDay(at));
  }

  counts(binding: ResolvedBinding): BindingCounts {
    const window = this.window(binding, this.now());
    return {
      bindingLastHour: window.ownLastHour.length,
      bindingLast24h: window.ownLastDay.length,
      platformToday: window.platformToday,
    };
  }

  // The binding's most recent send, across restarts; undefined when it never sent. The scheduler
  // uses it to pick the binding that has waited longest.
  lastSentAt(binding: ResolvedBinding): Date | undefined {
    const last = this.window(binding, this.now()).ownLast;
    return last === undefined ? undefined : new Date(last);
  }

  // May this binding send one more question now? When several conditions block, the one that
  // clears last is reported, so waiting until `retryAt` is never too short.
  decide(binding: ResolvedBinding): GateDecision {
    const now = this.now();
    const midnight = nextShanghaiMidnight(now);
    const window = this.window(binding, now);
    const blocks: { reason: GateBlock; retryAt: Date }[] = [];

    // A rate-limit notice stops the binding for the rest of that day, across restarts.
    if (this.stoppedDay.get(binding.key) === shanghaiDay(now))
      blocks.push({ reason: "rate_limited_today", retryAt: midnight });

    const interval = (last: number | undefined, seconds: number, reason: GateBlock) => {
      if (last === undefined) return;
      const retryAt = last + seconds * 1_000;
      if (retryAt > now.getTime()) blocks.push({ reason, retryAt: new Date(retryAt) });
    };
    interval(window.ownLast, binding.limits.minIntervalSeconds, "binding_min_interval");
    interval(
      window.platformLast,
      binding.platformLimits.minIntervalSeconds,
      "platform_min_interval",
    );

    const { hourlyQuota, dailyQuota } = binding.limits;
    if (hourlyQuota !== null && window.ownLastHour.length >= hourlyQuota)
      blocks.push({
        reason: "binding_hourly_quota",
        // The moment enough of the window's oldest sends have slid out of it.
        retryAt: new Date(window.ownLastHour[window.ownLastHour.length - hourlyQuota]! + HOUR_MS),
      });
    if (dailyQuota !== null && window.ownLastDay.length >= dailyQuota)
      blocks.push({
        reason: "binding_daily_quota",
        retryAt: new Date(window.ownLastDay[window.ownLastDay.length - dailyQuota]! + DAY_MS),
      });
    const { dailyCap } = binding.platformLimits;
    if (dailyCap !== null && window.platformToday >= dailyCap)
      blocks.push({ reason: "platform_daily_cap", retryAt: midnight });

    if (blocks.length === 0) return { allowed: true };
    const last = blocks.reduce((latest, block) =>
      block.retryAt.getTime() > latest.retryAt.getTime() ? block : latest,
    );
    return { allowed: false, ...last };
  }

  // The binding's own sends and its platform's sends (both devices) relative to `now`.
  private window(binding: ResolvedBinding, now: Date) {
    const today = shanghaiDay(now);
    const isToday = (at: number) => shanghaiDay(new Date(at)) === today;
    const own = this.sent.filter((entry) => entry.key === binding.key).map((entry) => entry.at);
    const platform = this.sent
      .filter((entry) => entry.platformCode === binding.platformCode)
      .map((entry) => entry.at);
    return {
      ownLast: own.length > 0 ? Math.max(...own) : undefined,
      platformLast: platform.length > 0 ? Math.max(...platform) : undefined,
      ownLastHour: own.filter((at) => at > now.getTime() - HOUR_MS).sort((a, b) => a - b),
      ownLastDay: own.filter((at) => at > now.getTime() - DAY_MS).sort((a, b) => a - b),
      platformToday: platform.filter(isToday).length,
    };
  }
}

function toSent(entry: LedgerEntry) {
  return {
    key: bindingKey(entry.deviceId, entry.platformCode),
    platformCode: entry.platformCode,
    at: Date.parse(entry.sentAt),
  };
}
