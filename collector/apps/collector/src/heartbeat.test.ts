import { setTimeout as delay } from "node:timers/promises";
import { describe, expect, it, vi } from "vitest";
import type { ControlPlane } from "./control-plane-client.ts";
import { withHeartbeat } from "./heartbeat.ts";
import { CollectorError } from "./platform-adapter.ts";
import { fixtureConfig, fixtureTask } from "./test-fixtures.ts";

function client() {
  return {
    lease: vi.fn(),
    submit: vi.fn(),
    reportFailure: vi.fn(),
    sessionEvent: vi.fn(),
    eligibility: vi.fn().mockResolvedValue({ eligible: true, retryAfterSeconds: 30 }),
    heartbeat: vi.fn().mockImplementation(async () => ({
      leaseExpiresAt: new Date(Date.now() + 1_000).toISOString(),
    })),
  } satisfies ControlPlane;
}
describe("lease lifetime", () => {
  it("renews immediately and throughout collection, then stops", async () => {
    const api = client();
    const config = { ...fixtureConfig(), COLLECTOR_HEARTBEAT_INTERVAL_MS: 10 };
    const result = await withHeartbeat(
      fixtureTask(),
      config,
      api,
      new AbortController().signal,
      async () => {
        await delay(45);
        return "done";
      },
    );
    expect(result).toBe("done");
    expect(api.heartbeat.mock.calls.length).toBeGreaterThan(2);
    const count = api.heartbeat.mock.calls.length;
    await delay(30);
    expect(api.heartbeat).toHaveBeenCalledTimes(count);
  });
  it("does not begin work when initial renewal is stale", async () => {
    const api = client();
    api.heartbeat.mockRejectedValue(new CollectorError("stale_lease"));
    const work = vi.fn();
    await expect(
      withHeartbeat(fixtureTask(), fixtureConfig(), api, new AbortController().signal, work),
    ).rejects.toMatchObject({ code: "stale_lease" });
    expect(work).not.toHaveBeenCalled();
  });
  it("aborts active work on a stale heartbeat", async () => {
    const api = client();
    api.heartbeat
      .mockResolvedValueOnce({ leaseExpiresAt: new Date(Date.now() + 1_000).toISOString() })
      .mockRejectedValue(new CollectorError("stale_lease"));
    let active: AbortSignal | undefined;
    await expect(
      withHeartbeat(
        fixtureTask(),
        { ...fixtureConfig(), COLLECTOR_HEARTBEAT_INTERVAL_MS: 10 },
        api,
        new AbortController().signal,
        async (signal) => {
          active = signal;
          await delay(100, undefined, { signal });
        },
      ),
    ).rejects.toMatchObject({ code: "stale_lease" });
    expect(active?.aborted).toBe(true);
  });
  it("enforces local expiry and collection deadlines even when work hangs", async () => {
    const api = client();
    await expect(
      withHeartbeat(
        fixtureTask(),
        { ...fixtureConfig(), COLLECTOR_COLLECTION_TIMEOUT_MS: 20 },
        api,
        new AbortController().signal,
        () => new Promise(() => {}),
      ),
    ).rejects.toMatchObject({ code: "collection_timeout" });
  });
  it("aborts at the last confirmed lease expiry during a hanging renewal", async () => {
    const api = client();
    api.heartbeat
      .mockResolvedValueOnce({ leaseExpiresAt: new Date(Date.now() + 60).toISOString() })
      .mockImplementation(
        (_task, signal) =>
          new Promise((_, reject) => {
            signal.addEventListener("abort", () => reject(new CollectorError("stale_lease")), {
              once: true,
            });
          }),
      );
    await expect(
      withHeartbeat(
        fixtureTask(),
        { ...fixtureConfig(), COLLECTOR_HEARTBEAT_INTERVAL_MS: 10 },
        api,
        new AbortController().signal,
        () => new Promise(() => {}),
      ),
    ).rejects.toMatchObject({ code: "stale_lease" });
  });
  it("lets work extend the collection deadline while heartbeats keep renewing", async () => {
    const api = client();
    const config = {
      ...fixtureConfig(),
      COLLECTOR_COLLECTION_TIMEOUT_MS: 40,
      COLLECTOR_HEARTBEAT_INTERVAL_MS: 10,
    };
    const result = await withHeartbeat(
      fixtureTask(),
      config,
      api,
      new AbortController().signal,
      async (signal, lease) => {
        lease.extendDeadline(200);
        await delay(120, undefined, { signal });
        return "waited";
      },
    );
    expect(result).toBe("waited");
    expect(api.heartbeat.mock.calls.length).toBeGreaterThan(5);
  });
  it("aborts with collection_timeout when unused extension is returned", async () => {
    const api = client();
    await expect(
      withHeartbeat(
        fixtureTask(),
        { ...fixtureConfig(), COLLECTOR_COLLECTION_TIMEOUT_MS: 100 },
        api,
        new AbortController().signal,
        async (signal, lease) => {
          lease.extendDeadline(1_000);
          lease.extendDeadline(-1_090);
          await delay(500, undefined, { signal });
        },
      ),
    ).rejects.toMatchObject({ code: "collection_timeout" });
  });
});
