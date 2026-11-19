import { randomUUID } from "node:crypto";
import { rm } from "node:fs/promises";
import { afterEach, describe, expect, it } from "vitest";
import { parseBindings, resolveBindings, type ResolvedBinding } from "./bindings.ts";
import { EvidenceStore, type BindingStatus, type LedgerEntry } from "./evidence.ts";
import { RateGate } from "./rate-gate.ts";
import { DEVICE_A, DEVICE_B, bindingsFixture, evidenceDirectory } from "./test-fixtures.ts";
import { nextShanghaiMidnight, shanghaiDay, toShanghaiIso } from "./time.ts";

const created: string[] = [];
afterEach(async () => {
  for (const directory of created.splice(0)) await rm(directory, { recursive: true, force: true });
});

function bindings(mutate?: (config: ReturnType<typeof bindingsFixture>) => void) {
  const config = bindingsFixture();
  mutate?.(config);
  const resolved = resolveBindings(parseBindings(config));
  const find = (device: string, platform: string): ResolvedBinding =>
    resolved.find((item) => item.key === `${device}/${platform}`)!;
  return {
    doubaoA: find(DEVICE_A, "doubao"),
    doubaoB: find(DEVICE_B, "doubao"),
    kimiA: find(DEVICE_A, "kimi"),
  };
}
const sent = (binding: ResolvedBinding, sentAt: string): LedgerEntry => ({
  answerId: randomUUID(),
  batchId: "batch-1",
  deviceId: binding.device.id,
  platformCode: binding.platformCode,
  sentAt,
});
const noStops: BindingStatus = { version: 1, bindings: {} };
// A gate whose clock the test moves.
function gateAt(ledger: LedgerEntry[], bindingStatus: BindingStatus = noStops) {
  const clock = { now: new Date("2026-11-15T04:00:00.000Z") };
  const gate = new RateGate({ ledger, bindingStatus, now: () => clock.now });
  return {
    gate,
    at(iso: string) {
      clock.now = new Date(iso);
      return gate;
    },
  };
}

describe("Asia/Shanghai calendar", () => {
  it("derives the business day and its end from UTC instants", () => {
    expect(toShanghaiIso(new Date("2026-11-15T04:00:00.000Z"))).toBe(
      "2026-11-15T12:00:00.000+08:00",
    );
    expect(shanghaiDay(new Date("2026-11-15T15:59:59.999Z"))).toBe("2026-11-15");
    expect(shanghaiDay(new Date("2026-11-15T16:00:00.000Z"))).toBe("2026-11-16");
    expect(nextShanghaiMidnight(new Date("2026-11-15T04:00:00.000Z")).toISOString()).toBe(
      "2026-11-15T16:00:00.000Z",
    );
    expect(nextShanghaiMidnight(new Date("2026-11-15T16:00:00.000Z")).toISOString()).toBe(
      "2026-11-16T16:00:00.000Z",
    );
  });
});

describe("two-level rate gate", () => {
  it("allows a binding with no sends", () => {
    const { doubaoA } = bindings();
    const { gate } = gateAt([]);
    expect(gate.decide(doubaoA)).toEqual({ allowed: true });
    expect(gate.counts(doubaoA)).toEqual({
      bindingLastHour: 0,
      bindingLast24h: 0,
      platformToday: 0,
    });
  });
  it("holds a binding for its minimum interval and the platform's other binding for the platform interval", () => {
    const { doubaoA, doubaoB, kimiA } = bindings();
    const clock = gateAt([sent(doubaoA, "2026-11-15T04:00:00.000Z")]);
    expect(clock.at("2026-11-15T04:09:59.000Z").decide(doubaoA)).toEqual({
      allowed: false,
      reason: "binding_min_interval",
      retryAt: new Date("2026-11-15T04:10:00.000Z"),
    });
    expect(clock.at("2026-11-15T04:10:00.000Z").decide(doubaoA)).toEqual({ allowed: true });
    // The other device's account shares only the platform interval (120 s).
    expect(clock.at("2026-11-15T04:01:59.000Z").decide(doubaoB)).toEqual({
      allowed: false,
      reason: "platform_min_interval",
      retryAt: new Date("2026-11-15T04:02:00.000Z"),
    });
    expect(clock.at("2026-11-15T04:02:00.000Z").decide(doubaoB)).toEqual({ allowed: true });
    // Another platform on the same device is not affected.
    expect(clock.at("2026-11-15T04:00:01.000Z").decide(kimiA)).toEqual({ allowed: true });
  });
  it("limits a binding to its hourly quota over a sliding 60 minutes", () => {
    const { doubaoA } = bindings();
    const clock = gateAt(
      ["04:00", "04:10", "04:20", "04:30"].map((time) =>
        sent(doubaoA, `2026-11-15T${time}:00.000Z`),
      ),
    );
    expect(clock.at("2026-11-15T04:45:00.000Z").counts(doubaoA).bindingLastHour).toBe(4);
    expect(clock.at("2026-11-15T04:45:00.000Z").decide(doubaoA)).toEqual({
      allowed: false,
      reason: "binding_hourly_quota",
      retryAt: new Date("2026-11-15T05:00:00.000Z"),
    });
    expect(clock.at("2026-11-15T05:00:00.000Z").decide(doubaoA)).toEqual({ allowed: true });
  });
  it("counts the binding's daily quota over the last 24 hours, like a Web account", () => {
    const { doubaoA } = bindings((config) => void (config.bindings[0]!.dailyQuota = 2));
    // 07:00 and 09:00 on 2026-11-15 in Shanghai.
    const clock = gateAt([
      sent(doubaoA, "2026-11-14T23:00:00.000Z"),
      sent(doubaoA, "2026-11-15T01:00:00.000Z"),
    ]);
    expect(clock.at("2026-11-15T03:00:00.000Z").counts(doubaoA).bindingLast24h).toBe(2);
    // Shanghai midnight does not open a new quota: both sends are still inside 24 hours.
    expect(clock.at("2026-11-15T16:00:00.000Z").decide(doubaoA)).toEqual({
      allowed: false,
      reason: "binding_daily_quota",
      retryAt: new Date("2026-11-15T23:00:00.000Z"),
    });
    // The oldest send leaves the window 24 hours after it was made.
    expect(clock.at("2026-11-15T23:00:00.000Z").counts(doubaoA).bindingLast24h).toBe(1);
    expect(clock.at("2026-11-15T23:00:00.000Z").decide(doubaoA)).toEqual({ allowed: true });
  });
  it("applies the platform daily cap to both devices together", () => {
    const { doubaoA, doubaoB, kimiA } = bindings(
      (config) => void (config.platforms.doubao!.dailyCap = 3),
    );
    const clock = gateAt([
      sent(doubaoA, "2026-11-15T01:00:00.000Z"),
      sent(doubaoB, "2026-11-15T01:30:00.000Z"),
      sent(doubaoA, "2026-11-15T02:00:00.000Z"),
    ]);
    const gate = clock.at("2026-11-15T06:00:00.000Z");
    expect(gate.counts(doubaoB)).toEqual({
      bindingLastHour: 0,
      bindingLast24h: 1,
      platformToday: 3,
    });
    for (const binding of [doubaoA, doubaoB])
      expect(gate.decide(binding)).toEqual({
        allowed: false,
        reason: "platform_daily_cap",
        retryAt: new Date("2026-11-15T16:00:00.000Z"),
      });
    expect(gate.decide(kimiA)).toEqual({ allowed: true });
  });
  it("reports the condition that clears last", () => {
    const { doubaoA } = bindings((config) => void (config.bindings[0]!.dailyQuota = 1));
    const clock = gateAt([sent(doubaoA, "2026-11-15T04:00:00.000Z")]);
    // Both the interval (04:10Z) and the daily quota (24 hours after the send) block.
    expect(clock.at("2026-11-15T04:05:00.000Z").decide(doubaoA)).toMatchObject({
      reason: "binding_daily_quota",
      retryAt: new Date("2026-11-16T04:00:00.000Z"),
    });
  });
  it("counts a send as soon as it is noted", () => {
    const { doubaoA } = bindings();
    const clock = gateAt([]);
    clock.gate.noteSent(sent(doubaoA, "2026-11-15T04:00:00.000Z"));
    expect(clock.at("2026-11-15T04:00:01.000Z").decide(doubaoA)).toMatchObject({
      allowed: false,
      reason: "binding_min_interval",
    });
  });
  it("tells when a binding last sent, across restarts and for itself only", () => {
    const { doubaoA, doubaoB, kimiA } = bindings();
    const { gate } = gateAt([
      sent(doubaoA, "2026-11-14T10:00:00.000Z"),
      sent(doubaoA, "2026-11-15T03:00:00.000Z"),
      sent(doubaoB, "2026-11-15T03:30:00.000Z"),
    ]);
    expect(gate.lastSentAt(doubaoA)).toEqual(new Date("2026-11-15T03:00:00.000Z"));
    expect(gate.lastSentAt(kimiA)).toBeUndefined();
    gate.noteSent(sent(kimiA, "2026-11-15T03:59:00.000Z"));
    expect(gate.lastSentAt(kimiA)).toEqual(new Date("2026-11-15T03:59:00.000Z"));
  });
  it("stops a rate-limited binding for the rest of its Asia/Shanghai day only", () => {
    const { doubaoA, doubaoB } = bindings();
    const clock = gateAt([]);
    clock.gate.noteRateLimited(doubaoA.key, new Date("2026-11-15T04:00:00.000Z"));
    expect(clock.at("2026-11-15T15:00:00.000Z").decide(doubaoA)).toEqual({
      allowed: false,
      reason: "rate_limited_today",
      retryAt: new Date("2026-11-15T16:00:00.000Z"),
    });
    expect(clock.at("2026-11-15T15:00:00.000Z").decide(doubaoB)).toEqual({ allowed: true });
    expect(clock.at("2026-11-15T16:00:00.000Z").decide(doubaoA)).toEqual({ allowed: true });
  });
});

// AC-A6: restarting the script does not reset the day's counters.
describe("limits across a restart", () => {
  it("derives the same counts and decision from the ledger after the store is reopened", async () => {
    const directory = await evidenceDirectory(created);
    const { doubaoA, doubaoB } = bindings();
    const now = () => new Date("2026-11-15T04:35:00.000Z");

    const first = await EvidenceStore.open(directory);
    const gate = new RateGate({
      ledger: await first.readLedger(),
      bindingStatus: await first.readBindingStatus(),
      now,
    });
    for (const entry of [
      sent(doubaoA, "2026-11-15T04:00:00.000Z"),
      sent(doubaoB, "2026-11-15T04:05:00.000Z"),
      sent(doubaoA, "2026-11-15T04:10:00.000Z"),
      sent(doubaoA, "2026-11-15T04:20:00.000Z"),
      sent(doubaoA, "2026-11-15T04:30:00.000Z"),
    ]) {
      await first.appendLedger(entry);
      gate.noteSent(entry);
    }
    const before = { counts: gate.counts(doubaoA), decision: gate.decide(doubaoA) };
    expect(before.counts).toEqual({ bindingLastHour: 4, bindingLast24h: 4, platformToday: 5 });
    expect(before.decision).toMatchObject({ allowed: false, reason: "binding_hourly_quota" });
    await first.close();

    // A new process: nothing in memory, only the evidence directory.
    const second = await EvidenceStore.open(directory);
    const restarted = new RateGate({
      ledger: await second.readLedger(),
      bindingStatus: await second.readBindingStatus(),
      now,
    });
    expect({ counts: restarted.counts(doubaoA), decision: restarted.decide(doubaoA) }).toEqual(
      before,
    );
    expect(restarted.counts(doubaoB)).toEqual({
      bindingLastHour: 1,
      bindingLast24h: 1,
      platformToday: 5,
    });
    await second.close();
  });
  it("keeps a rate-limited binding stopped after a restart and releases it the next day", async () => {
    const directory = await evidenceDirectory(created);
    const { doubaoA, doubaoB } = bindings();
    const first = await EvidenceStore.open(directory);
    await first.markBindingRateLimited(doubaoA.key, new Date("2026-11-15T04:00:00.000Z"));
    await first.close();

    const second = await EvidenceStore.open(directory);
    const clock = gateAt(await second.readLedger(), await second.readBindingStatus());
    await second.close();
    expect(clock.at("2026-11-15T12:00:00.000Z").decide(doubaoA)).toMatchObject({
      allowed: false,
      reason: "rate_limited_today",
    });
    expect(clock.at("2026-11-15T12:00:00.000Z").decide(doubaoB)).toEqual({ allowed: true });
    expect(clock.at("2026-11-15T16:00:01.000Z").decide(doubaoA)).toEqual({ allowed: true });
  });
});

describe("default limits (KD-21, 2026-11-17)", () => {
  const defaults = () =>
    bindings((config) => {
      for (const binding of config.bindings) {
        delete binding.minIntervalSeconds;
        delete binding.hourlyQuota;
        delete binding.dailyQuota;
      }
      for (const platform of Object.values(config.platforms)) {
        delete (platform as { minIntervalSeconds?: number }).minIntervalSeconds;
        delete (platform as { dailyCap?: number }).dailyCap;
      }
    });
  it("spaces one binding 100 s apart and one platform 30 s apart, with no counts", () => {
    const { doubaoA, doubaoB } = defaults();
    // 200 sends spaced 100 s apart: far beyond the old hourly and daily quotas.
    const start = Date.parse("2026-11-15T00:00:00.000Z");
    const ledger = Array.from({ length: 200 }, (_, index) =>
      sent(doubaoA, new Date(start + index * 100_000).toISOString()),
    );
    const last = start + 199 * 100_000;
    const clock = gateAt(ledger);
    expect(clock.at(new Date(last + 99_999).toISOString()).decide(doubaoA)).toMatchObject({
      allowed: false,
      reason: "binding_min_interval",
    });
    expect(clock.at(new Date(last + 100_000).toISOString()).decide(doubaoA)).toEqual({
      allowed: true,
    });
    // The other device's binding of the same platform waits for the 30 s platform interval only.
    expect(clock.at(new Date(last + 29_999).toISOString()).decide(doubaoB)).toMatchObject({
      allowed: false,
      reason: "platform_min_interval",
    });
    expect(clock.at(new Date(last + 30_000).toISOString()).decide(doubaoB)).toEqual({
      allowed: true,
    });
  });
});
