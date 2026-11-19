import { createServer } from "node:http";
import { describe, expect, it, vi } from "vitest";
import { ControlPlaneClient, postSessionEvent } from "./control-plane-client.ts";
import { collectorEnv, fixtureConfig, fixtureTask } from "./test-fixtures.ts";

describe("typed safe control-plane client", () => {
  it("uses frozen heartbeat contract and does not follow redirects", async () => {
    const config = fixtureConfig();
    const task = fixtureTask();
    const request = vi
      .fn<typeof fetch>()
      .mockResolvedValue(new Response(JSON.stringify({ leaseExpiresAt: task.leaseExpiresAt })));
    await expect(
      new ControlPlaneClient(config, request).heartbeat(task, new AbortController().signal),
    ).resolves.toEqual({ leaseExpiresAt: task.leaseExpiresAt });
    const [url, init] = request.mock.calls[0]!;
    expect(String(url)).toBe("http://127.0.0.1:4314/internal/collector/heartbeats");
    expect(init?.redirect).toBe("error");
    expect(JSON.parse(String(init?.body))).toEqual({
      tenantId: task.tenantId,
      taskId: task.id,
      nodeId: config.COLLECTOR_NODE_ID,
      leaseVersion: 1,
      extendSeconds: 60,
    });
  });
  it.each([409, 401, 500])("never includes sensitive HTTP %s bodies in errors", async (status) => {
    const request = vi
      .fn<typeof fetch>()
      .mockResolvedValue(new Response("sensitive page secret", { status }));
    await expect(
      new ControlPlaneClient(fixtureConfig(), request).lease(new AbortController().signal),
    ).rejects.toMatchObject({
      message: status === 409 ? "stale_lease" : "control_plane_unavailable",
    });
  });
  it("rejects malformed successful responses", async () => {
    const request = vi
      .fn<typeof fetch>()
      .mockResolvedValue(new Response('{"tasks":[{"raw":"secret"}]}'));
    await expect(
      new ControlPlaneClient(fixtureConfig(), request).lease(new AbortController().signal),
    ).rejects.toMatchObject({ code: "invalid_response" });
  });
  it("bounds an actual hanging HTTP request", async () => {
    const server = createServer(() => {});
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address();
    if (!address || typeof address === "string") throw new Error("test_server");
    try {
      const config = {
        ...fixtureConfig(),
        COLLECTOR_HTTP_TIMEOUT_MS: 100,
        COLLECTOR_API_URL: `http://127.0.0.1:${address.port}`,
      };
      await expect(
        new ControlPlaneClient(config).lease(new AbortController().signal),
      ).rejects.toMatchObject({ code: "control_plane_unavailable" });
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });
  it("sends the configured session profile on lease and refuses another account's task", async () => {
    const config = fixtureConfig();
    const task = fixtureTask();
    const request = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ tasks: [{ ...task, sessionProfileId: null }] })),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            tasks: [{ ...task, sessionProfileId: "77777777-7777-4777-8777-777777777777" }],
          }),
        ),
      );
    const client = new ControlPlaneClient(config, request);
    await expect(client.lease(new AbortController().signal)).resolves.toHaveLength(1);
    expect(JSON.parse(String(request.mock.calls[0]![1]?.body))).toMatchObject({
      sessionProfileId: collectorEnv.COLLECTOR_SESSION_PROFILE_ID,
      capacity: 1,
    });
    await expect(client.lease(new AbortController().signal)).rejects.toMatchObject({
      code: "invalid_response",
    });
    const legacy = vi.fn<typeof fetch>().mockResolvedValue(new Response('{"tasks":[]}'));
    await new ControlPlaneClient(
      { ...config, COLLECTOR_SESSION_PROFILE_ID: undefined },
      legacy,
    ).lease(new AbortController().signal);
    expect(JSON.parse(String(legacy.mock.calls[0]![1]?.body))).not.toHaveProperty(
      "sessionProfileId",
    );
  });
  it("posts validated failure reports and session events", async () => {
    const config = fixtureConfig();
    const task = fixtureTask();
    const request = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            sessionProfileStatus: "needs_verification",
            taskDisposition: "requeued",
            platformPaused: false,
          }),
        ),
      )
      .mockResolvedValueOnce(new Response(JSON.stringify({ sessionProfileStatus: "active" })));
    const client = new ControlPlaneClient(config, request);
    const identity = {
      tenantId: task.tenantId,
      nodeId: config.COLLECTOR_NODE_ID,
      region: "cn" as const,
      sessionProfileId: collectorEnv.COLLECTOR_SESSION_PROFILE_ID,
    };
    const failure = {
      ...identity,
      failureCode: "challenge_required" as const,
      phase: "pre_send" as const,
      task: { taskId: task.id, leaseVersion: task.leaseVersion },
    };
    await expect(client.reportFailure(failure, new AbortController().signal)).resolves.toEqual({
      sessionProfileStatus: "needs_verification",
      taskDisposition: "requeued",
      platformPaused: false,
    });
    expect(String(request.mock.calls[0]![0])).toBe(
      "http://127.0.0.1:4314/internal/collector/failures",
    );
    expect(JSON.parse(String(request.mock.calls[0]![1]?.body))).toEqual(failure);
    await client.sessionEvent(
      { ...identity, eventKind: "verified", phase: "probe" },
      new AbortController().signal,
    );
    expect(String(request.mock.calls[1]![0])).toBe(
      "http://127.0.0.1:4314/internal/collector/session-events",
    );
    // Contract violations are refused locally, before any request.
    await expect(
      client.reportFailure({ ...failure, phase: "probe" }, new AbortController().signal),
    ).rejects.toMatchObject({ code: "invalid_response" });
    await expect(
      postSessionEvent(
        config,
        { ...identity, eventKind: "challenge_cleared", phase: "pre_send" },
        new AbortController().signal,
        request,
      ),
    ).rejects.toMatchObject({ code: "invalid_response" });
    expect(request).toHaveBeenCalledTimes(2);
  });
  it("checks eligibility for the configured profile and platform before any page access", async () => {
    const config = fixtureConfig();
    const request = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ eligible: false, retryAfterSeconds: 45 })),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            eligible: false,
            retryAfterSeconds: 120,
            stopReason: "needs_verification",
          }),
        ),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ eligible: true, retryAfterSeconds: 5 })),
      );
    const client = new ControlPlaneClient(config, request);
    await expect(client.eligibility(new AbortController().signal)).resolves.toEqual({
      eligible: false,
      retryAfterSeconds: 45,
    });
    const [url, init] = request.mock.calls[0]!;
    expect(String(url)).toBe("http://127.0.0.1:4314/internal/collector/eligibility");
    expect(JSON.parse(String(init?.body))).toEqual({
      tenantId: collectorEnv.COLLECTOR_TENANT_ID,
      nodeId: collectorEnv.COLLECTOR_NODE_ID,
      region: "cn",
      sessionProfileId: collectorEnv.COLLECTOR_SESSION_PROFILE_ID,
      platformCode: "doubao",
    });
    await expect(client.eligibility(new AbortController().signal)).resolves.toMatchObject({
      stopReason: "needs_verification",
    });
    // An out-of-bounds retry interval is a contract violation, not a hot loop.
    await expect(client.eligibility(new AbortController().signal)).rejects.toMatchObject({
      code: "invalid_response",
    });
    // Without a profile there is nothing to check; no request is sent.
    await expect(
      new ControlPlaneClient(
        { ...config, COLLECTOR_SESSION_PROFILE_ID: undefined },
        request,
      ).eligibility(new AbortController().signal),
    ).rejects.toMatchObject({ code: "profile_unavailable" });
    expect(request).toHaveBeenCalledTimes(3);
  });
});
