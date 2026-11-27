import { randomUUID } from "node:crypto";
import { describe, expect, it } from "vitest";
import type { AttemptOutcome, AttemptRequest, AttemptRunner } from "./adapter.ts";
import { parseBindings, resolveBindings, type ResolvedBinding } from "./bindings.ts";
import { RateGate } from "./rate-gate.ts";
import { cellKey, pendingCells, runBatch, type BatchOptions, type Cell } from "./scheduler.ts";
import {
  DEVICE_A,
  DEVICE_B,
  VirtualClock,
  bindingsFixture,
  recordFixture,
} from "./test-fixtures.ts";

const QUESTIONS = ["合成问题一", "合成问题二", "合成问题三"];
const BATCH = "batch-1";

interface Plan {
  // Time from the start of the attempt to the send, and from the send to the end.
  preSendMs?: number;
  durationMs?: number;
  // false: the attempt ends before anything is sent.
  send?: boolean;
  outcome?: Partial<AttemptOutcome>;
  throws?: Error;
}
// Scheduling only: attempts are simulated on the virtual clock, with the real rate gate.
function scenario(
  plan: (request: AttemptRequest, attemptNumber: number) => Plan = () => ({}),
  options: {
    bindings?: ResolvedBinding[];
    gate?: (clock: VirtualClock) => RateGate;
  } = {},
) {
  const clock = new VirtualClock();
  const bindings = options.bindings ?? resolveBindings(parseBindings(bindingsFixture()));
  const gate =
    options.gate?.(clock) ??
    new RateGate({ ledger: [], bindingStatus: { version: 1, bindings: {} }, now: clock.now });
  const attempts: {
    device: string;
    platform: string;
    question: string;
    start: number;
    end: number;
    aborted: boolean;
  }[] = [];
  const sends: { binding: string; platform: string; at: number }[] = [];
  const logs: Record<string, unknown>[] = [];
  let count = 0;
  const attempt: AttemptRunner = async (request) => {
    const start = clock.now().getTime();
    const step = plan(request, (count += 1));
    if (step.throws) throw step.throws;
    await clock.sleep(step.preSendMs ?? 30_000, request.signal);
    const sent = step.send !== false && !request.signal.aborted;
    if (sent) {
      const at = clock.now();
      gate.noteSent({
        answerId: randomUUID(),
        batchId: request.batchId,
        deviceId: request.binding.device.id,
        platformCode: request.binding.platformCode,
        sentAt: at.toISOString(),
      });
      sends.push({
        binding: request.binding.key,
        platform: request.binding.platformCode,
        at: at.getTime(),
      });
      request.onSent();
      await clock.sleep(step.durationMs ?? 60_000, request.signal);
    }
    attempts.push({
      device: request.binding.device.id,
      platform: request.binding.platformCode,
      question: request.questionText,
      start,
      end: clock.now().getTime(),
      aborted: request.signal.aborted,
    });
    const failed =
      step.outcome?.failureCode ?? (request.signal.aborted ? "collection_timeout" : null);
    return {
      answerId: randomUUID(),
      status: failed ? "failed" : "valid",
      failureCode: failed,
      phase: failed ? (sent ? "post_send" : "pre_send") : null,
      ...step.outcome,
    };
  };
  const run = (overrides: Partial<BatchOptions> = {}) =>
    clock.run(
      runBatch({
        batchId: BATCH,
        bindings,
        cells: pendingCells(QUESTIONS, ["doubao", "kimi"], []),
        gate,
        attempt,
        now: clock.now,
        sleep: clock.sleep,
        log: (line) => logs.push(line),
        ...overrides,
      }),
    );
  return { clock, bindings, gate, attempts, sends, logs, run };
}
const cellsOf = (attempts: { platform: string; question: string }[]) =>
  attempts.map((attempt) => cellKey(attempt.platform, attempt.question));
const fail = (failureCode: AttemptOutcome["failureCode"]): Plan => ({ outcome: { failureCode } });

describe("pending cells", () => {
  // AC-A4: a cell with a valid record is not asked again; failures stay pending.
  it("is every question on every platform minus the cells with a valid record", () => {
    const records = [
      recordFixture({ platformCode: "doubao", questionText: QUESTIONS[0]! }),
      recordFixture({
        platformCode: "doubao",
        questionText: QUESTIONS[1]!,
        status: "evidence_incomplete",
        phase: "post_send",
        evidenceIssue: "too_short",
      }),
      recordFixture({
        platformCode: "kimi",
        questionText: QUESTIONS[2]!,
        status: "failed",
        failureCode: "collection_timeout",
        phase: "post_send",
        completedAt: null,
      }),
    ];
    const pending = pendingCells(QUESTIONS, ["doubao", "kimi"], records);
    expect(pending).toEqual([
      { platformCode: "doubao", questionText: QUESTIONS[1]! },
      { platformCode: "doubao", questionText: QUESTIONS[2]! },
      { platformCode: "kimi", questionText: QUESTIONS[0]! },
      { platformCode: "kimi", questionText: QUESTIONS[1]! },
      { platformCode: "kimi", questionText: QUESTIONS[2]! },
    ] satisfies Cell[]);
  });
  // AC-A17: a sample written before v1.1 (with a video, the Web prefix in its sent text and no
  // source status) still closes its cell; so does one collected without a video.
  it("counts valid samples of either record shape", () => {
    const records = [
      recordFixture({
        platformCode: "doubao",
        questionText: QUESTIONS[0]!,
        sentText: `合成前缀\n\n${QUESTIONS[0]!}`,
      }),
      recordFixture({
        platformCode: "doubao",
        questionText: QUESTIONS[1]!,
        sentText: QUESTIONS[1]!,
        video: null,
        citations: [],
        citationsStatus: "unreadable",
        citationsIssue: "source_list_empty",
      }),
    ];
    expect(pendingCells(QUESTIONS, ["doubao"], records)).toEqual(
      QUESTIONS.slice(2).map((questionText) => ({ platformCode: "doubao", questionText })),
    );
  });
  it("identifies a cell without the question text", () => {
    expect(cellKey("doubao", QUESTIONS[0]!)).toMatch(/^doubao\/[0-9a-f]{64}$/u);
  });
});

describe("batch scheduling", () => {
  // AC-A5.
  it("runs one attempt at a time per device while the two devices overlap", async () => {
    const s = scenario();
    const result = await s.run();
    expect(result).toMatchObject({ attempts: 6, valid: 6, notCollected: 0, interrupted: false });
    for (const device of [DEVICE_A, DEVICE_B]) {
      const own = s.attempts
        .filter((attempt) => attempt.device === device)
        .sort((a, b) => a.start - b.start);
      expect(own.length).toBeGreaterThan(0);
      for (let index = 1; index < own.length; index += 1)
        expect(own[index]!.start).toBeGreaterThanOrEqual(own[index - 1]!.end);
    }
    const a = s.attempts.filter((attempt) => attempt.device === DEVICE_A);
    const b = s.attempts.filter((attempt) => attempt.device === DEVICE_B);
    expect(a.some((x) => b.some((y) => x.start < y.end && y.start < x.end))).toBe(true);
    // The log alone proves it: every attempt has a start and an end line with device and time.
    const lines = s.logs.filter((line) =>
      ["app_attempt_scheduled", "app_attempt_done"].includes(line.event as string),
    );
    expect(lines).toHaveLength(12);
    for (const line of lines)
      expect(line).toMatchObject({ device: expect.any(String), at: expect.any(String) });
  });

  it("never gives one cell to two devices and yields one valid sample per cell", async () => {
    const s = scenario();
    await s.run();
    const cells = cellsOf(s.attempts);
    expect(new Set(cells).size).toBe(cells.length);
    expect(new Set(cells)).toEqual(
      new Set(
        pendingCells(QUESTIONS, ["doubao", "kimi"], []).map((cell) =>
          cellKey(cell.platformCode, cell.questionText),
        ),
      ),
    );
  });

  // AC-A6.
  it("keeps every send inside the binding and platform limits", async () => {
    // A slow start (over two minutes before the send) must not let the other device send on
    // the same platform inside the platform interval.
    const s = scenario(() => ({ preSendMs: 150_000 }));
    await s.run();
    const byBinding = new Map<string, number[]>();
    const byPlatform = new Map<string, number[]>();
    for (const send of s.sends) {
      byBinding.set(send.binding, [...(byBinding.get(send.binding) ?? []), send.at]);
      byPlatform.set(send.platform, [...(byPlatform.get(send.platform) ?? []), send.at]);
    }
    const gaps = (times: number[]) =>
      times
        .sort((x, y) => x - y)
        .slice(1)
        .map((time, index) => time - times[index]!);
    for (const times of byBinding.values())
      for (const gap of gaps(times)) expect(gap).toBeGreaterThanOrEqual(600_000);
    for (const times of byPlatform.values())
      for (const gap of gaps(times)) expect(gap).toBeGreaterThanOrEqual(120_000);
    expect(s.sends).toHaveLength(6);
  });

  it("picks the binding that has waited longest, so a device alternates its platforms", async () => {
    const s = scenario(undefined, {
      bindings: resolveBindings(parseBindings(bindingsFixture())).filter(
        (binding) => binding.device.id === DEVICE_A,
      ),
    });
    await s.run();
    expect(s.attempts.map((attempt) => attempt.platform)).toEqual([
      "doubao",
      "kimi",
      "doubao",
      "kimi",
      "doubao",
      "kimi",
    ]);
  });

  // AC-A4.
  it("asks nothing for cells that already have a valid record when a batch is run again", async () => {
    const s = scenario();
    const records = QUESTIONS.map((questionText) =>
      recordFixture({ platformCode: "doubao", questionText }),
    );
    const result = await s.run({ cells: pendingCells(QUESTIONS, ["doubao", "kimi"], records) });
    expect(result).toMatchObject({ attempts: 3, valid: 3, notCollected: 0 });
    expect(s.attempts.every((attempt) => attempt.platform === "kimi")).toBe(true);

    const done = scenario();
    const all = ["doubao", "kimi"].flatMap((platformCode) =>
      QUESTIONS.map((questionText) =>
        recordFixture({ platformCode: platformCode as "doubao", questionText }),
      ),
    );
    expect(
      await done.run({ cells: pendingCells(QUESTIONS, ["doubao", "kimi"], all) }),
    ).toMatchObject({ attempts: 0, valid: 0, notCollected: 0 });
    expect(done.attempts).toEqual([]);
  });

  it("stops only the binding that needs a human; the others collect its cells", async () => {
    const s = scenario((request) =>
      request.binding.key === `${DEVICE_A}/doubao` ? fail("login_required") : {},
    );
    const result = await s.run();
    expect(result.stoppedBindings).toEqual({ [`${DEVICE_A}/doubao`]: "login_required" });
    expect(result.stoppedDevices).toEqual({});
    expect(result).toMatchObject({ valid: 6, notCollected: 0 });
    // One failed attempt on the stopped binding, then never again.
    expect(
      s.attempts.filter((attempt) => attempt.device === DEVICE_A && attempt.platform === "doubao"),
    ).toHaveLength(1);
    expect(s.attempts.filter((attempt) => attempt.device === DEVICE_A).length).toBeGreaterThan(1);
  });

  it("reports the remaining cells as not collected when both bindings of a platform stop", async () => {
    const s = scenario((request) =>
      request.binding.platformCode === "doubao" ? fail("app_contract_changed") : {},
    );
    const result = await s.run();
    expect(result.stoppedBindings).toEqual({
      [`${DEVICE_A}/doubao`]: "app_contract_changed",
      [`${DEVICE_B}/doubao`]: "app_contract_changed",
    });
    expect(result).toMatchObject({
      valid: 3,
      notCollected: 3,
      notCollectedByPlatform: { doubao: 3 },
    });
    expect(s.logs).toContainEqual({
      event: "app_platform_not_collected",
      batchId: BATCH,
      platform: "doubao",
      cells: 3,
    });
    // The other platform was not affected.
    expect(s.attempts.filter((attempt) => attempt.platform === "kimi")).toHaveLength(3);
  });

  it("stops a binding after two App crashes in a row, not after one", async () => {
    const once = scenario((request, number) => (number === 1 ? fail("app_crashed") : {}));
    expect((await once.run()).stoppedBindings).toEqual({});
    expect((await once.run({ cells: [] })).attempts).toBe(0);

    const twice = scenario((request) =>
      request.binding.key === `${DEVICE_A}/kimi` ? fail("app_crashed") : {},
    );
    const result = await twice.run();
    expect(result.stoppedBindings).toEqual({ [`${DEVICE_A}/kimi`]: "app_crashed" });
    expect(
      twice.attempts.filter(
        (attempt) => attempt.device === DEVICE_A && attempt.platform === "kimi",
      ),
    ).toHaveLength(2);
    expect(result.valid).toBe(6);
  });

  it("stops a device after two recording failures in a row and after one offline result", async () => {
    const recording = scenario((request) =>
      request.binding.device.id === DEVICE_B
        ? { send: false, outcome: { failureCode: "recording_failed" } }
        : {},
    );
    const result = await recording.run();
    expect(result.stoppedDevices).toEqual({ [DEVICE_B]: "recording_failed" });
    expect(recording.attempts.filter((attempt) => attempt.device === DEVICE_B)).toHaveLength(2);
    // Nothing was sent from that device, and the other one finished the batch.
    expect(recording.sends.every((send) => send.binding.startsWith(`${DEVICE_A}/`))).toBe(true);
    expect(result).toMatchObject({ valid: 6, notCollected: 0 });

    const offline = scenario((request) =>
      request.binding.device.id === DEVICE_B
        ? { send: false, outcome: { answerId: null, failureCode: "device_offline" } }
        : {},
    );
    const offlineResult = await offline.run();
    expect(offlineResult.stoppedDevices).toEqual({ [DEVICE_B]: "device_offline" });
    expect(offline.attempts.filter((attempt) => attempt.device === DEVICE_B)).toHaveLength(1);
    expect(offlineResult.valid).toBe(6);
  });

  it("ends when App crashes and recording failures take turns before anything is sent", async () => {
    // Neither failure sends, so neither is slowed down by the limits: the streaks must end it.
    const s = scenario((request, number) =>
      request.binding.device.id === DEVICE_A
        ? {
            send: false,
            outcome: { failureCode: number % 2 === 1 ? "app_crashed" : "recording_failed" },
          }
        : { send: false, outcome: { answerId: null, failureCode: "device_offline" } },
    );
    const result = await s.run();
    expect(result.stoppedBindings).toEqual({
      [`${DEVICE_A}/doubao`]: "app_crashed",
      [`${DEVICE_A}/kimi`]: "app_crashed",
    });
    expect(result.attempts).toBeLessThan(12);
    expect(s.sends).toEqual([]);
    expect(result).toMatchObject({ valid: 0, notCollected: 6 });
  });

  it("stops every binding of a platform whose App rejects the emulator", async () => {
    const s = scenario((request) =>
      request.binding.platformCode === "kimi"
        ? { send: false, outcome: { failureCode: "emulator_rejected" } }
        : {},
    );
    const result = await s.run();
    expect(result.stoppedBindings).toEqual({
      [`${DEVICE_A}/kimi`]: "emulator_rejected",
      [`${DEVICE_B}/kimi`]: "emulator_rejected",
    });
    expect(s.attempts.filter((attempt) => attempt.platform === "kimi")).toHaveLength(1);
    expect(result.notCollectedByPlatform).toEqual({ kimi: 3 });
  });

  it("asks a cell again after incomplete evidence or a timeout, a bounded number of times", async () => {
    const question = QUESTIONS[0]!;
    const s = scenario((request) =>
      request.questionText === question && request.binding.platformCode === "doubao"
        ? { outcome: { status: "evidence_incomplete", failureCode: null, phase: "post_send" } }
        : request.questionText === question
          ? fail("collection_timeout")
          : {},
    );
    const result = await s.run();
    // Two tries each for the two failing cells, one for each of the other four.
    expect(s.attempts.filter((attempt) => attempt.question === question)).toHaveLength(4);
    expect(result).toMatchObject({ attempts: 8, valid: 4, notCollected: 2 });
    expect(result.stoppedBindings).toEqual({});
  });

  it("ends the run for a binding that is blocked until tomorrow instead of waiting for midnight", async () => {
    const s = scenario(undefined, {
      gate: (clock) =>
        new RateGate({
          ledger: [],
          bindingStatus: {
            version: 1,
            bindings: {
              [`${DEVICE_A}/doubao`]: {
                reason: "rate_limited",
                day: "2026-11-15",
                stoppedAt: "2026-11-15T03:00:00.000Z",
              },
            },
          },
          now: clock.now,
        }),
    });
    const result = await s.run();
    expect(result.stoppedBindings).toEqual({ [`${DEVICE_A}/doubao`]: "rate_limited_today" });
    expect(result.valid).toBe(6);
    // Finished the same day (Asia/Shanghai), hours before midnight.
    expect(s.clock.now().toISOString() < "2026-11-15T10:00:00.000Z").toBe(true);
  });

  it("stops a binding whose 24-hour quota is used up for this run, also when its next slot is close", async () => {
    const config = bindingsFixture();
    config.bindings[0]!.dailyQuota = 1;
    const s = scenario(undefined, {
      bindings: resolveBindings(parseBindings(config)),
      // The one send of the last 24 hours leaves the window five minutes into the run.
      gate: (clock) =>
        new RateGate({
          ledger: [
            {
              answerId: randomUUID(),
              batchId: "batch-0",
              deviceId: DEVICE_A,
              platformCode: "doubao",
              sentAt: "2026-11-14T04:05:00.000Z",
            },
          ],
          bindingStatus: { version: 1, bindings: {} },
          now: clock.now,
        }),
    });
    const result = await s.run();
    expect(result.stoppedBindings).toEqual({ [`${DEVICE_A}/doubao`]: "binding_daily_quota" });
    // Nothing was sent on that binding; the other device collected the platform's cells.
    expect(s.sends.filter((send) => send.binding === `${DEVICE_A}/doubao`)).toEqual([]);
    expect(result.valid).toBe(6);
  });

  it("starts no more attempts than the limit", async () => {
    const s = scenario();
    const result = await s.run({ limit: 1 });
    expect(result).toMatchObject({ attempts: 1, valid: 1, notCollected: 5 });
    expect(s.attempts).toHaveLength(1);
  });

  it("on a stop request starts nothing new and lets attempts in flight finish", async () => {
    const stop = new AbortController();
    const s = scenario((request, number) => {
      if (number === 2) stop.abort();
      return {};
    });
    const result = await s.run({ stop: stop.signal });
    // Both attempts end inside the default grace period of two minutes.
    expect(result).toMatchObject({ attempts: 2, valid: 2, interrupted: true, notCollected: 4 });
    expect(s.attempts.every((attempt) => !attempt.aborted)).toBe(true);
    expect(s.logs.map((line) => line.event)).toContain("app_batch_stopping");
  });

  // Task 11-24: work that can be cut short (reading links) ends before the grace period does.
  it("tells attempts in flight of a stop request at once, before their own signal aborts", async () => {
    const stop = new AbortController();
    const requests: AttemptRequest[] = [];
    const s = scenario((request, number) => {
      requests.push(request);
      if (number === 2) stop.abort();
      return {};
    });
    await s.run({ stop: stop.signal });
    expect(requests).toHaveLength(2);
    for (const request of requests) {
      expect(request.stopping).toBe(stop.signal);
      expect(request.stopping?.aborted).toBe(true);
    }
    // The grace period did not end: the attempts finished with their own signal untouched.
    expect(s.attempts.every((attempt) => !attempt.aborted)).toBe(true);
    // Without a stop signal there is nothing to pass on.
    requests.length = 0;
    const unstoppable = scenario((request) => {
      requests.push(request);
      return {};
    });
    await unstoppable.run({ limit: 1 });
    expect(requests[0]!.stopping).toBeUndefined();
  });

  it("aborts attempts still in flight when the grace period ends, and they end with an outcome", async () => {
    const stop = new AbortController();
    const s = scenario((request, number) => {
      if (number === 2) stop.abort();
      return { durationMs: 3_600_000 };
    });
    const result = await s.run({ stop: stop.signal, graceMs: 30_000 });
    expect(result).toMatchObject({ attempts: 2, valid: 0, interrupted: true });
    expect(s.attempts).toHaveLength(2);
    expect(s.attempts.every((attempt) => attempt.aborted)).toBe(true);
    // Bounded: far less than the hour the attempts would have taken.
    expect(s.clock.now().getTime() - Date.parse("2026-11-15T04:00:00.000Z")).toBeLessThan(120_000);
  });

  it("does not start at all when the stop was requested before the batch began", async () => {
    const stop = new AbortController();
    stop.abort();
    const s = scenario();
    expect(await s.run({ stop: stop.signal })).toMatchObject({ attempts: 0, interrupted: true });
  });

  it("ends the whole run on an unclassified error after the other device has stopped", async () => {
    const s = scenario((request, number) =>
      number === 2 ? { throws: new Error("evidence directory lost") } : {},
    );
    await expect(s.run()).rejects.toThrow("evidence directory lost");
    // The attempt in flight on the other device was not cut off.
    expect(s.attempts).toHaveLength(1);
    expect(s.attempts[0]!.aborted).toBe(false);
  });

  it("logs no question text", async () => {
    const s = scenario();
    await s.run();
    const text = JSON.stringify(s.logs);
    for (const question of QUESTIONS) expect(text).not.toContain(question);
  });
});
