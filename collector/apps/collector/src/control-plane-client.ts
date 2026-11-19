import type { CollectorConfig } from "@aivc/config";
import {
  collectorFailureResponseSchema,
  collectorFailureSchema,
  eligibilityRequestSchema,
  eligibilityResponseSchema,
  heartbeatResponseSchema,
  leaseResponseSchema,
  resultSubmissionResponseSchema,
  sessionEventResponseSchema,
  sessionEventSchema,
  type CollectorFailure,
  type CollectorFailureResponse,
  type EligibilityResponse,
  type LeasedTask,
  type ResultSubmission,
  type ResultSubmissionResponse,
  type SessionEvent,
  type SessionEventResponse,
} from "@aivc/contracts";
import type { z } from "zod";
import { CollectorError } from "./platform-adapter.ts";

export interface ControlPlane {
  // Profile mode only: read-only check before the platform page is loaded (design §12.2).
  eligibility(signal: AbortSignal): Promise<EligibilityResponse>;
  lease(signal: AbortSignal): Promise<LeasedTask[]>;
  heartbeat(task: LeasedTask, signal: AbortSignal): Promise<{ leaseExpiresAt: string }>;
  submit(submission: ResultSubmission, signal: AbortSignal): Promise<ResultSubmissionResponse>;
  // Releases the lease immediately with a classified failure and applies the account effect.
  reportFailure(failure: CollectorFailure, signal: AbortSignal): Promise<CollectorFailureResponse>;
  sessionEvent(event: SessionEvent, signal: AbortSignal): Promise<SessionEventResponse>;
}

export type ControlPlaneHttpConfig = Pick<
  CollectorConfig,
  "COLLECTOR_API_URL" | "COLLECTOR_SERVICE_TOKEN" | "COLLECTOR_HTTP_TIMEOUT_MS"
>;

// Bounded, redirect-free JSON POST. Never includes HTTP bodies in errors or logs.
async function postJson<T>(
  config: ControlPlaneHttpConfig,
  request: typeof fetch,
  path: string,
  body: unknown,
  decoder: z.ZodType<T>,
  parent: AbortSignal,
): Promise<T> {
  const signal = AbortSignal.any([parent, AbortSignal.timeout(config.COLLECTOR_HTTP_TIMEOUT_MS)]);
  try {
    const response = await request(new URL(path, config.COLLECTOR_API_URL), {
      method: "POST",
      redirect: "error",
      signal,
      headers: {
        authorization: `Bearer ${config.COLLECTOR_SERVICE_TOKEN}`,
        "content-type": "application/json",
      },
      body: JSON.stringify(body),
    });
    if (response.status === 409) throw new CollectorError("stale_lease");
    if (!response.ok) throw new CollectorError("control_plane_unavailable");
    // Bound both download and decode.
    if (!response.body) throw new CollectorError("invalid_response");
    const reader = response.body.getReader();
    const chunks: Uint8Array[] = [];
    let bytes = 0;
    try {
      while (true) {
        const part = await reader.read();
        if (part.done) break;
        bytes += part.value.byteLength;
        if (bytes > 2_000_000) throw new CollectorError("invalid_response");
        chunks.push(part.value);
      }
    } finally {
      await reader.cancel().catch(() => undefined);
    }
    const decoded = decoder.safeParse(JSON.parse(Buffer.concat(chunks).toString("utf8")));
    if (!decoded.success) throw new CollectorError("invalid_response");
    return decoded.data;
  } catch (error) {
    if (error instanceof CollectorError) throw error;
    throw new CollectorError(parent.aborted ? "stopped" : "control_plane_unavailable");
  }
}

// Shared by the collector and the operator `probe` command (which has no lease config).
export function postSessionEvent(
  config: ControlPlaneHttpConfig,
  event: SessionEvent,
  signal: AbortSignal,
  request: typeof fetch = fetch,
): Promise<SessionEventResponse> {
  const body = sessionEventSchema.safeParse(event);
  if (!body.success) return Promise.reject(new CollectorError("invalid_response"));
  return postJson(
    config,
    request,
    "/internal/collector/session-events",
    body.data,
    sessionEventResponseSchema,
    signal,
  );
}

export class ControlPlaneClient implements ControlPlane {
  constructor(
    private readonly config: CollectorConfig,
    private readonly request: typeof fetch = fetch,
  ) {}
  private post<T>(path: string, body: unknown, decoder: z.ZodType<T>, parent: AbortSignal) {
    return postJson(this.config, this.request, path, body, decoder, parent);
  }
  eligibility(signal: AbortSignal): Promise<EligibilityResponse> {
    // Real mode configures exactly one platform; without a profile there is nothing to check.
    const body = eligibilityRequestSchema.safeParse({
      tenantId: this.config.COLLECTOR_TENANT_ID,
      nodeId: this.config.COLLECTOR_NODE_ID,
      region: this.config.COLLECTOR_REGION,
      sessionProfileId: this.config.COLLECTOR_SESSION_PROFILE_ID,
      platformCode: this.config.COLLECTOR_SUPPORTED_PLATFORMS[0],
    });
    if (!body.success || this.config.COLLECTOR_SUPPORTED_PLATFORMS.length !== 1)
      return Promise.reject(new CollectorError("profile_unavailable"));
    return this.post(
      "/internal/collector/eligibility",
      body.data,
      eligibilityResponseSchema,
      signal,
    );
  }
  async lease(signal: AbortSignal): Promise<LeasedTask[]> {
    const tasks = (
      await this.post(
        "/internal/collector/leases",
        {
          tenantId: this.config.COLLECTOR_TENANT_ID,
          nodeId: this.config.COLLECTOR_NODE_ID,
          region: this.config.COLLECTOR_REGION,
          supportedPlatformCodes: this.config.COLLECTOR_SUPPORTED_PLATFORMS,
          capacity: this.config.COLLECTOR_CAPACITY,
          leaseSeconds: this.config.COLLECTOR_LEASE_SECONDS,
          ...(this.config.COLLECTOR_SESSION_PROFILE_ID
            ? { sessionProfileId: this.config.COLLECTOR_SESSION_PROFILE_ID }
            : {}),
        },
        leaseResponseSchema,
        signal,
      )
    ).tasks;
    // A lease granted to another account must never be worked under this profile.
    const profileId = this.config.COLLECTOR_SESSION_PROFILE_ID;
    if (tasks.some((task) => task.sessionProfileId && task.sessionProfileId !== profileId))
      throw new CollectorError("invalid_response");
    return tasks;
  }
  heartbeat(task: LeasedTask, signal: AbortSignal) {
    return this.post(
      "/internal/collector/heartbeats",
      {
        tenantId: task.tenantId,
        taskId: task.id,
        nodeId: this.config.COLLECTOR_NODE_ID,
        leaseVersion: task.leaseVersion,
        extendSeconds: this.config.COLLECTOR_LEASE_SECONDS,
      },
      heartbeatResponseSchema,
      signal,
    );
  }
  submit(submission: ResultSubmission, signal: AbortSignal) {
    return this.post(
      "/internal/collector/results",
      submission,
      resultSubmissionResponseSchema,
      signal,
    );
  }
  reportFailure(failure: CollectorFailure, signal: AbortSignal) {
    const body = collectorFailureSchema.safeParse(failure);
    if (!body.success) return Promise.reject(new CollectorError("invalid_response"));
    return this.post(
      "/internal/collector/failures",
      body.data,
      collectorFailureResponseSchema,
      signal,
    );
  }
  sessionEvent(event: SessionEvent, signal: AbortSignal) {
    return postSessionEvent(this.config, event, signal, this.request);
  }
}
