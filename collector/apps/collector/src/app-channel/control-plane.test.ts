import { describe, expect, it } from "vitest";
import type { AppControlPlaneError } from "./control-plane.ts";
import { AppControlPlaneClient, controlPlaneUrlProblem } from "./control-plane.ts";

const BINDING = "6f1f5f2a-2b8e-4a53-9d36-1c4f0d0e8a11";
const config = {
  apiUrl: "http://127.0.0.1:5260",
  nodeToken: "node-token-secret",
  timeoutMs: 1_000,
};

function fakeFetch(status: number, body: unknown) {
  const seen: { url: string; init: RequestInit }[] = [];
  const request = (async (url: URL | string, init?: RequestInit) => {
    seen.push({ url: String(url), init: init! });
    return new Response(typeof body === "string" ? body : JSON.stringify(body), { status });
  }) as typeof fetch;
  return { request, seen };
}
const signal = new AbortController().signal;
const lease = {
  nodeId: "mac-local",
  bindingId: BINDING,
  leaseSeconds: 180,
  dispatchProtocol: "device-v1",
  deviceOnline: true,
} as const;

describe("App control-plane client", () => {
  it("does not accept a legacy registration response as proof the server supports device-v1", async () => {
    const { request } = fakeFetch(200, { bindings: [], rejected: [] });
    await expect(
      new AppControlPlaneClient(config, request).reportBindings(
        { nodeId: "n", collectorBuildRevision: "r", bindings: [] },
        signal,
      ),
    ).rejects.toMatchObject({ kind: "unavailable" });
  });
  it("posts the confirmed send to its own idempotent endpoint with the task, attempt and timestamp", async () => {
    const { request, seen } = fakeFetch(200, { disposition: "duplicate" });
    const body = {
      nodeId: "n",
      bindingId: BINDING,
      taskId: "11111111-1111-4111-8111-111111111111",
      leaseVersion: 2,
      sentAt: "2026-11-18T08:10:11.112Z",
    };
    expect(await new AppControlPlaneClient(config, request).reportSent(body, signal)).toEqual({
      disposition: "duplicate",
    });
    expect(seen[0]!.url).toBe("http://127.0.0.1:5260/internal/collector/app/sent");
    expect(JSON.parse(String(seen[0]!.init.body))).toEqual(body);
  });
  it("posts to /internal/collector/app/<route> with the node token and decodes the response", async () => {
    const { request, seen } = fakeFetch(200, {
      task: null,
      reason: "no_task",
      retryAfterSeconds: 60,
    });
    const response = await new AppControlPlaneClient(config, request).lease(lease, signal);
    expect(response.reason).toBe("no_task");
    expect(seen[0]!.url).toBe("http://127.0.0.1:5260/internal/collector/app/leases");
    expect(seen[0]!.init.redirect).toBe("error");
    expect((seen[0]!.init.headers as Record<string, string>).authorization).toBe(
      "Bearer node-token-secret",
    );
    expect(JSON.parse(String(seen[0]!.init.body))).toEqual(lease);
  });
  it("classifies errors: 409 stale, 4xx rejected with its code, 5xx and bad bodies unavailable", async () => {
    const error = async (status: number, body: unknown) =>
      new AppControlPlaneClient(config, fakeFetch(status, body).request).lease(lease, signal).then(
        () => undefined,
        (caught: unknown) => caught as AppControlPlaneError,
      );
    expect(await error(409, { error: { code: "STALE_LEASE", message: "" } })).toMatchObject({
      kind: "stale",
      code: "STALE_LEASE",
    });
    expect(await error(403, { error: { code: "APP_NODE_DISABLED", message: "" } })).toMatchObject({
      kind: "rejected",
      status: 403,
      code: "APP_NODE_DISABLED",
    });
    // A framework 400 without the agreed shape is still a refusal.
    expect(await error(400, "not json")).toMatchObject({ kind: "rejected", code: null });
    expect(await error(500, "Internal Server Error")).toMatchObject({ kind: "unavailable" });
    expect(await error(200, { unexpected: true })).toMatchObject({ kind: "unavailable" });
  });
  it("maps network failures to unavailable and never sends a body the contract refuses", async () => {
    const throwing = (async () => {
      throw new TypeError("fetch failed");
    }) as typeof fetch;
    await expect(
      new AppControlPlaneClient(config, throwing).lease(lease, signal),
    ).rejects.toMatchObject({ kind: "unavailable" });
    const { request, seen } = fakeFetch(200, {});
    await expect(
      new AppControlPlaneClient(config, request).lease({ ...lease, leaseSeconds: 1_000 }, signal),
    ).rejects.toMatchObject({ kind: "invalid_request" });
    expect(seen).toHaveLength(0);
  });
  it("refuses the Mock and fixture queues and URLs with credentials", () => {
    expect(controlPlaneUrlProblem("http://127.0.0.1:5260")).toBeUndefined();
    expect(controlPlaneUrlProblem("http://127.0.0.1:4311")).toMatch(/Mock/u);
    expect(controlPlaneUrlProblem("http://localhost:4312/")).toMatch(/fixture/u);
    expect(controlPlaneUrlProblem("http://user:pass@example.com")).toMatch(/credentials/u);
    expect(controlPlaneUrlProblem("ftp://example.com")).toMatch(/http/u);
  });
});
