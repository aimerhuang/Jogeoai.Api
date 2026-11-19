import { mkdtemp, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { loadCollectorConfig } from "@aivc/config";
import {
  AdapterRegistry,
  CollectorError,
  createSubmission,
  mockAdapter,
  type ProbeOptions,
  safeFailure,
} from "./platform-adapter.ts";
import { APPROVED_QUESTIONS, assertApprovedTask, reserveAttempt } from "./approval-guard.ts";
import { exitCodeFor, runCollector } from "./runner.ts";
import { collectorEnv, fixtureConfig, fixtureTask } from "./test-fixtures.ts";

const directories: string[] = [];
afterEach(async () => {
  for (const directory of directories.splice(0))
    await rm(directory, { recursive: true, force: true });
});
async function setup() {
  const directory = await mkdtemp(join(tmpdir(), "aivc-runner-"));
  directories.push(directory);
  const config = { ...fixtureConfig(), COLLECTOR_SPOOL_DIRECTORY: directory };
  const task = fixtureTask();
  const client = {
    lease: vi.fn().mockResolvedValue([task]),
    heartbeat: vi.fn().mockResolvedValue({ leaseExpiresAt: task.leaseExpiresAt }),
    submit: vi.fn().mockResolvedValue({ disposition: "accepted", sampleId: task.id }),
    reportFailure: vi.fn().mockResolvedValue({
      sessionProfileStatus: "active",
      taskDisposition: "requeued",
      platformPaused: false,
    }),
    sessionEvent: vi.fn().mockResolvedValue({ sessionProfileStatus: "active" }),
    eligibility: vi.fn().mockResolvedValue({ eligible: true, retryAfterSeconds: 30 }),
  };
  return {
    config,
    task,
    client,
    directory,
    registry: new AdapterRegistry([mockAdapter("doubao")]),
  };
}
describe("bounded Collector lifecycle and retries", () => {
  it("preserves mock mode and removes durable spool only after acceptance", async () => {
    const { config, client, registry, directory } = await setup();
    expect(
      await runCollector(config, client, registry, new AbortController().signal, vi.fn()),
    ).toBe(1);
    expect(client.submit).toHaveBeenCalledTimes(1);
    expect(await readdir(directory)).toEqual([]);
  });
  it("stops before leasing on unhealthy probe; errors never create synthetic results", async () => {
    const { config, client } = await setup();
    const adapter = {
      ...mockAdapter("doubao"),
      probe: vi.fn().mockResolvedValue({ status: "login_required" }),
    };
    await expect(
      runCollector(
        config,
        client,
        new AdapterRegistry([adapter]),
        new AbortController().signal,
        vi.fn(),
      ),
    ).rejects.toMatchObject({ code: "login_required" });
    expect(client.lease).not.toHaveBeenCalled();
    expect(client.submit).not.toHaveBeenCalled();
  });
  it("does not submit when adapter collection fails", async () => {
    const { config, client, directory } = await setup();
    const adapter = {
      ...mockAdapter("doubao"),
      collect: vi.fn().mockRejectedValue(new Error("private body")),
    };
    await expect(
      runCollector(
        config,
        client,
        new AdapterRegistry([adapter]),
        new AbortController().signal,
        vi.fn(),
      ),
    ).rejects.toThrow();
    expect(client.submit).not.toHaveBeenCalled();
    expect(await readdir(directory)).toEqual([]);
  });
  it("spools ambiguous submission failure and replays the identical payload before new leases", async () => {
    const { config, client, registry, directory } = await setup();
    client.submit.mockRejectedValueOnce(new Error("network"));
    await expect(
      runCollector(config, client, registry, new AbortController().signal, vi.fn()),
    ).rejects.toMatchObject({ code: "submission_spooled" });
    expect(await readdir(directory)).toHaveLength(1);
    const first = client.submit.mock.calls[0]?.[0];
    client.lease.mockResolvedValue([]);
    client.submit.mockResolvedValue({ disposition: "duplicate", sampleId: fixtureTask().id });
    await runCollector(config, client, registry, new AbortController().signal, vi.fn());
    expect(client.submit.mock.calls[1]?.[0]).toEqual(first);
    expect(await readdir(directory)).toEqual([]);
  });
  it("stops after stale result disposition without taking another lease", async () => {
    const { config, client, registry, directory } = await setup();
    client.submit.mockResolvedValue({ disposition: "stale", sampleId: null });
    await expect(
      runCollector(config, client, registry, new AbortController().signal, vi.fn()),
    ).rejects.toMatchObject({ code: "stale_lease" });
    expect(client.lease).toHaveBeenCalledTimes(1);
    expect(await readdir(directory)).toEqual([]);
  });
  it("rejects unrelated projects and modified keywords before collection", async () => {
    const config = loadCollectorConfig({ ...collectorEnv, COLLECTOR_MODE: "real" });
    expect(() =>
      assertApprovedTask({ ...fixtureTask(), questionText: "unapproved" }, config),
    ).toThrowError("task_not_approved");
    expect(() =>
      assertApprovedTask({ ...fixtureTask(), brandProjectId: fixtureTask().id }, config),
    ).toThrowError("task_not_approved");
  });
  it("persists attempt identity so restarting cannot repeat external searches", async () => {
    const { directory } = await setup();
    const config = loadCollectorConfig({
      ...collectorEnv,
      COLLECTOR_SESSION_PROFILE_ID: undefined,
      COLLECTOR_MODE: "real",
    });
    await reserveAttempt(directory, fixtureTask(), config);
    await expect(
      reserveAttempt(directory, { ...fixtureTask(), leaseVersion: 2 }, config),
    ).rejects.toThrowError("task_not_approved");
  });
  it("enforces the four-task budget across consecutive leases and restarts", async () => {
    const { config, client, registry, directory } = await setup();
    const real = {
      ...config,
      COLLECTOR_SESSION_PROFILE_ID: undefined,
      COLLECTOR_MODE: "real" as const,
      COLLECTOR_RUN_ONCE: "false" as const,
      COLLECTOR_MIN_REQUEST_INTERVAL_MS: 0,
    };
    let issued = 0;
    client.lease.mockImplementation(async () => [
      {
        ...fixtureTask(),
        id: `33333333-3333-4333-8333-33333333333${issued}`,
        questionText: APPROVED_QUESTIONS[issued++ % 4]!,
      },
    ]);
    expect(
      await runCollector(real, client, registry, new AbortController().signal, vi.fn(), directory),
    ).toBe(4);
    expect(client.lease).toHaveBeenCalledTimes(4);
    expect(client.submit).toHaveBeenCalledTimes(4);
    await expect(
      runCollector(real, client, registry, new AbortController().signal, vi.fn(), directory),
    ).rejects.toMatchObject({ code: "task_not_approved" });
    // The exhausted ledger stops the restart before a lease, so no control-plane attempt is burned.
    expect(client.lease).toHaveBeenCalledTimes(4);
    expect(client.submit).toHaveBeenCalledTimes(4);
  });
  it("registry fails closed and safe error projection strips arbitrary text", () => {
    expect(() => new AdapterRegistry([]).get("doubao")).toThrowError("unsupported_platform");
    expect(safeFailure(new Error("password and private response"))).toBe("browser_failed");
    expect(safeFailure(new CollectorError("rate_limited"))).toBe("rate_limited");
  });
  it("refuses prompt echoes at the real-result submission boundary", () => {
    const task = fixtureTask();
    expect(() =>
      createSubmission(
        task,
        {
          answerText: ` ${task.questionText} `,
          citations: [],
          completedAt: new Date().toISOString(),
        },
        { ...fixtureConfig(), COLLECTOR_MODE: "real" },
      ),
    ).toThrowError("page_contract_changed");
  });
  it("idempotency includes task/version/answer and canonical citation set, not completion time", () => {
    const base = {
      answerText: "fixture",
      citations: ["https://example.com/a", "https://example.com/b"],
      completedAt: new Date().toISOString(),
    };
    const first = createSubmission(fixtureTask(), base, fixtureConfig());
    const second = createSubmission(
      fixtureTask(),
      {
        ...base,
        citations: [...base.citations].reverse(),
        completedAt: "2026-01-01T00:00:00.000Z",
      },
      fixtureConfig(),
    );
    expect(first.idempotencyKey).toBe(second.idempotencyKey);
    expect(
      createSubmission(fixtureTask(), { ...base, answerText: "other" }, fixtureConfig())
        .idempotencyKey,
    ).not.toBe(first.idempotencyKey);
  });
});

describe("classified failure reports and challenge wait wiring", () => {
  const profileId = collectorEnv.COLLECTOR_SESSION_PROFILE_ID;
  const failing = (
    error: Error,
    beforeThrow?: (context: Parameters<ReturnType<typeof mockAdapter>["collect"]>[2]) => void,
  ) => ({
    ...mockAdapter("doubao"),
    collect: vi.fn(async (_task, _signal, context) => {
      beforeThrow?.(context);
      throw error;
    }) as ReturnType<typeof mockAdapter>["collect"],
  });
  const run = (
    setup: Awaited<ReturnType<typeof setupRun>>,
    adapter: ReturnType<typeof mockAdapter>,
    notify = vi.fn(async () => undefined),
  ) =>
    runCollector(
      setup.config,
      setup.client,
      new AdapterRegistry([adapter]),
      new AbortController().signal,
      vi.fn(),
      undefined,
      undefined,
      { notify },
    );
  const setupRun = setup;
  it("reports a pre-send challenge, notifies without question text and rethrows (AC-9)", async () => {
    const context = await setupRun();
    const notify = vi.fn(async () => undefined);
    await expect(
      run(context, failing(new CollectorError("challenge_required")), notify),
    ).rejects.toMatchObject({ code: "challenge_required" });
    expect(context.client.reportFailure).toHaveBeenCalledTimes(1);
    expect(context.client.reportFailure.mock.calls[0]?.[0]).toEqual({
      tenantId: collectorEnv.COLLECTOR_TENANT_ID,
      nodeId: collectorEnv.COLLECTOR_NODE_ID,
      region: "cn",
      sessionProfileId: profileId,
      failureCode: "challenge_required",
      phase: "pre_send",
      task: { taskId: context.task.id, leaseVersion: context.task.leaseVersion },
    });
    expect(notify.mock.calls).toEqual([
      [
        {
          platform: "doubao",
          reason: "challenge_required",
          phase: "pre_send",
          sessionProfileId: profileId,
          taskId: context.task.id,
        },
      ],
    ]);
    expect(JSON.stringify(notify.mock.calls)).not.toContain(context.task.questionText);
    expect(context.client.submit).not.toHaveBeenCalled();
  });
  it("reports post_send once the adapter has submitted", async () => {
    const context = await setupRun();
    await expect(
      run(
        context,
        failing(new CollectorError("rate_limited"), (collectContext) =>
          collectContext?.onSubmitted?.(),
        ),
      ),
    ).rejects.toMatchObject({ code: "rate_limited" });
    expect(context.client.reportFailure.mock.calls[0]?.[0]).toMatchObject({
      failureCode: "rate_limited",
      phase: "post_send",
    });
  });
  it("reports an unhealthy probe with phase probe and no task, before any lease", async () => {
    const context = await setupRun();
    const notify = vi.fn(async () => undefined);
    const adapter = {
      ...mockAdapter("doubao"),
      probe: vi.fn().mockResolvedValue({ status: "login_required" }),
    };
    await expect(run(context, adapter, notify)).rejects.toMatchObject({ code: "login_required" });
    expect(context.client.reportFailure.mock.calls).toEqual([
      [
        {
          tenantId: collectorEnv.COLLECTOR_TENANT_ID,
          nodeId: collectorEnv.COLLECTOR_NODE_ID,
          region: "cn",
          sessionProfileId: profileId,
          failureCode: "login_required",
          phase: "probe",
        },
        expect.any(AbortSignal),
      ],
    ]);
    expect(notify).toHaveBeenCalledWith(
      expect.objectContaining({ reason: "login_required", phase: "probe" }),
    );
    expect(context.client.lease).not.toHaveBeenCalled();
  });
  it("does not report local conditions outside the failure contract", async () => {
    const context = await setupRun();
    await expect(run(context, failing(new CollectorError("stale_lease")))).rejects.toMatchObject({
      code: "stale_lease",
    });
    expect(context.client.reportFailure).not.toHaveBeenCalled();
    context.client.submit.mockRejectedValueOnce(new Error("network"));
    await expect(run(context, mockAdapter("doubao"))).rejects.toMatchObject({
      code: "submission_spooled",
    });
    expect(context.client.reportFailure).not.toHaveBeenCalled();
  });
  it("never masks the original error when the report itself fails", async () => {
    const context = await setupRun();
    context.client.reportFailure.mockRejectedValue(new CollectorError("control_plane_unavailable"));
    await expect(
      run(context, failing(new CollectorError("page_contract_changed"))),
    ).rejects.toMatchObject({ code: "page_contract_changed" });
    expect(context.client.reportFailure).toHaveBeenCalledTimes(1);
  });
  it("skips control-plane reports without a configured session profile", async () => {
    const context = await setupRun();
    context.config = { ...context.config, COLLECTOR_SESSION_PROFILE_ID: undefined };
    await expect(
      run(context, failing(new CollectorError("challenge_required"))),
    ).rejects.toMatchObject({ code: "challenge_required" });
    expect(context.client.reportFailure).not.toHaveBeenCalled();
    expect(context.client.lease).toHaveBeenCalledTimes(1);
  });
  it("hands the adapter a waiter that sends challenge session events and notifies", async () => {
    const context = await setupRun();
    const notify = vi.fn(async () => undefined);
    const adapter = {
      ...mockAdapter("doubao"),
      collect: vi.fn(async (task, signal, collectContext) => {
        let polls = 0;
        await collectContext!.challengeWaiter!.wait({
          phase: "pre_send",
          pollMs: 1,
          signal,
          check: async () => (polls++ === 0 ? "challenge_required" : undefined),
        });
        return mockAdapter("doubao").collect(task, signal);
      }) as ReturnType<typeof mockAdapter>["collect"],
    };
    expect(await run(context, adapter, notify)).toBe(1);
    const event = (eventKind: string) => ({
      tenantId: collectorEnv.COLLECTOR_TENANT_ID,
      nodeId: collectorEnv.COLLECTOR_NODE_ID,
      region: "cn",
      sessionProfileId: profileId,
      eventKind,
      phase: "pre_send",
      task: { taskId: context.task.id, leaseVersion: context.task.leaseVersion },
    });
    expect(context.client.sessionEvent.mock.calls.map(([body]) => body)).toEqual([
      event("challenge_wait_started"),
      event("challenge_cleared"),
    ]);
    expect(notify).toHaveBeenCalledWith(
      expect.objectContaining({ reason: "challenge_wait_started", phase: "pre_send" }),
    );
    expect(context.client.reportFailure).not.toHaveBeenCalled();
    expect(context.client.submit).toHaveBeenCalledTimes(1);
  });
  it("waits and notifies without any control-plane session call when no profile is configured", async () => {
    const context = await setupRun();
    context.config = { ...context.config, COLLECTOR_SESSION_PROFILE_ID: undefined };
    const notify = vi.fn(async () => undefined);
    const adapter = {
      ...mockAdapter("doubao"),
      collect: vi.fn(async (task, signal, collectContext) => {
        let polls = 0;
        await collectContext!.challengeWaiter!.wait({
          phase: "pre_send",
          pollMs: 1,
          signal,
          check: async () => (polls++ === 0 ? "challenge_required" : undefined),
        });
        return mockAdapter("doubao").collect(task, signal);
      }) as ReturnType<typeof mockAdapter>["collect"],
    };
    expect(await run(context, adapter, notify)).toBe(1);
    expect(notify).toHaveBeenCalledWith(
      expect.objectContaining({ reason: "challenge_wait_started", phase: "pre_send" }),
    );
    expect(JSON.stringify(notify.mock.calls)).not.toContain(context.task.questionText);
    expect(context.client.sessionEvent).not.toHaveBeenCalled();
    expect(context.client.reportFailure).not.toHaveBeenCalled();
    expect(context.client.eligibility).not.toHaveBeenCalled();
    expect(context.client.submit).toHaveBeenCalledTimes(1);
  });
  it("omits the waiter when waiting is disabled", async () => {
    const context = await setupRun();
    context.config = { ...context.config, COLLECTOR_CHALLENGE_WAIT_MS: 0 };
    const adapter = failing(new CollectorError("challenge_required"), (collectContext) => {
      expect(collectContext?.challengeWaiter).toBeUndefined();
    });
    await expect(run(context, adapter)).rejects.toMatchObject({ code: "challenge_required" });
    expect(adapter.collect).toHaveBeenCalledTimes(1);
  });
  it("maps operator-action failures to a distinct exit code", () => {
    expect(exitCodeFor("challenge_required")).toBe(3);
    expect(exitCodeFor("login_required")).toBe(3);
    expect(exitCodeFor("rate_limited")).toBe(1);
    expect(exitCodeFor("browser_failed")).toBe(1);
  });
});

describe("resident profile-mode loop (design §12.3)", () => {
  async function residentSetup(extra: Partial<ReturnType<typeof fixtureConfig>> = {}) {
    const context = await setup();
    const ledger = await mkdtemp(join(tmpdir(), "aivc-resident-ledger-"));
    directories.push(ledger);
    const config = {
      ...context.config,
      COLLECTOR_MODE: "real" as const,
      COLLECTOR_RUN_ONCE: "false" as const,
      COLLECTOR_MIN_REQUEST_INTERVAL_MS: 0,
      COLLECTOR_MAX_TASKS_PER_RUN: 2,
      ...extra,
    };
    const calls: string[] = [];
    const controller = new AbortController();
    const adapter = {
      ...mockAdapter("doubao"),
      probe: vi.fn(async (...args: [AbortSignal, ProbeOptions?]) => {
        void args;
        calls.push("probe");
        return { status: "ready" as const };
      }),
    };
    context.client.eligibility.mockImplementation(async () => {
      calls.push("eligibility");
      return { eligible: true, retryAfterSeconds: 30 };
    });
    const log = vi.fn();
    const run = () =>
      runCollector(
        config,
        context.client,
        new AdapterRegistry([adapter]),
        controller.signal,
        log,
        ledger,
      );
    return { ...context, config, calls, controller, adapter, log, run };
  }

  it("checks eligibility before every probe and keeps working past the run budget and empty queues", async () => {
    const context = await residentSetup();
    // Five tasks, two asking the same approved question, against a run budget of two.
    const queue = [0, 1, 2, 3, 0].map((question, index) => ({
      ...fixtureTask(),
      id: `33333333-3333-4333-8333-33333333333${index}`,
      questionText: APPROVED_QUESTIONS[question]!,
    }));
    let empty = 0;
    context.client.lease.mockImplementation(async () => {
      context.calls.push("lease");
      const next = queue.shift();
      if (next) return [next];
      // Two empty leases in a row do not end the resident loop; shutdown does.
      if (++empty === 2) context.controller.abort();
      return [];
    });
    await expect(context.run()).rejects.toThrow();
    expect(context.client.submit).toHaveBeenCalledTimes(5);
    expect(context.calls.slice(0, 6)).toEqual([
      "eligibility",
      "probe",
      "lease",
      "eligibility",
      "probe",
      "lease",
    ]);
    expect(context.calls.filter((call) => call === "eligibility")).toHaveLength(7);
    expect(context.calls.filter((call) => call === "probe")).toHaveLength(7);
    // Only the resident loop may check the loaded page in place.
    expect(context.adapter.probe.mock.calls.every((call) => call[1]?.inPlace === true)).toBe(true);
    expect(context.client.reportFailure).not.toHaveBeenCalled();
  });

  it("sleeps retryAfterSeconds without touching the page when not eligible", async () => {
    const context = await residentSetup();
    context.client.eligibility.mockImplementation(async () => {
      context.calls.push("eligibility");
      setTimeout(() => context.controller.abort(), 20);
      return { eligible: false, retryAfterSeconds: 30 };
    });
    await expect(context.run()).rejects.toThrow();
    // Still inside the 30 s wait when shutdown arrived: one check, no probe, no lease.
    expect(context.calls).toEqual(["eligibility"]);
    expect(context.client.lease).not.toHaveBeenCalled();
    expect(context.log).toHaveBeenCalledWith({ event: "not_eligible", count: 30 });
  });

  it("finishes a RUN_ONCE cycle when not eligible", async () => {
    const context = await residentSetup({ COLLECTOR_RUN_ONCE: "true" });
    context.client.eligibility.mockResolvedValue({ eligible: false, retryAfterSeconds: 120 });
    expect(await context.run()).toBe(0);
    expect(context.adapter.probe).not.toHaveBeenCalled();
    expect(context.client.lease).not.toHaveBeenCalled();
  });

  it.each(["needs_verification", "expired", "disabled"] as const)(
    "exits for the operator when the control plane reports %s",
    async (stopReason) => {
      const context = await residentSetup();
      context.client.eligibility.mockResolvedValue({
        eligible: false,
        retryAfterSeconds: 120,
        stopReason,
      });
      const error = await context.run().catch((caught: unknown) => caught);
      expect(error).toMatchObject({ code: "session_profile_stopped" });
      expect(exitCodeFor(safeFailure(error))).toBe(3);
      expect(context.adapter.probe).not.toHaveBeenCalled();
      expect(context.client.lease).not.toHaveBeenCalled();
      // The account already has its status; nothing new is reported.
      expect(context.client.reportFailure).not.toHaveBeenCalled();
      expect(context.log).toHaveBeenCalledWith(
        expect.objectContaining({ event: "session_profile_stopped", status: stopReason }),
      );
    },
  );

  it("never asks for eligibility without a profile or in mock mode", async () => {
    const legacy = await residentSetup({ COLLECTOR_SESSION_PROFILE_ID: undefined });
    legacy.client.lease.mockResolvedValue([]);
    expect(await legacy.run()).toBe(0);
    expect(legacy.client.eligibility).not.toHaveBeenCalled();
    // Without a profile every probe navigates: no in-place option is passed.
    expect(legacy.adapter.probe.mock.calls).toEqual([[expect.any(AbortSignal)]]);
    const mock = await setup();
    expect(
      await runCollector(
        mock.config,
        mock.client,
        mock.registry,
        new AbortController().signal,
        vi.fn(),
      ),
    ).toBe(1);
    expect(mock.client.eligibility).not.toHaveBeenCalled();
  });
});
