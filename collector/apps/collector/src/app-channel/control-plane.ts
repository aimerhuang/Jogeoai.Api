import {
  appBindingReportRequestSchema,
  appBindingReportResponseSchema,
  appControlPlaneErrorSchema,
  appFailureRequestSchema,
  appFailureResponseSchema,
  appHeartbeatRequestSchema,
  appHeartbeatResponseSchema,
  appLeaseRequestSchema,
  appLeaseResponseSchema,
  appResultResponseSchema,
  appResultSubmissionSchema,
  appSentRequestSchema,
  appSentResponseSchema,
  type AppFailureRequest,
  type AppFailureResponse,
  type AppLeaseResponse,
  type AppResultResponse,
  type AppResultSubmission,
} from "@aivc/contracts";
import type { z } from "zod";

// HTTP client of the App-channel collector routes (Jogeoai.Api contract.md §9, frozen v0.5).
// One node token per computer; it never appears in logs or errors. Request and response bodies are
// never logged either: they hold question and answer text.

export type BindingReportRequest = z.input<typeof appBindingReportRequestSchema>;
export type BindingReportResponse = z.infer<typeof appBindingReportResponseSchema>;
export type LeaseRequest = z.input<typeof appLeaseRequestSchema>;
export type HeartbeatRequest = z.input<typeof appHeartbeatRequestSchema>;
export type SentRequest = z.input<typeof appSentRequestSchema>;
export type SentResponse = z.infer<typeof appSentResponseSchema>;

export interface AppControlPlane {
  reportBindings(body: BindingReportRequest, signal: AbortSignal): Promise<BindingReportResponse>;
  lease(body: LeaseRequest, signal: AbortSignal): Promise<AppLeaseResponse>;
  heartbeat(body: HeartbeatRequest, signal: AbortSignal): Promise<{ leaseExpiresAt: string }>;
  reportSent(body: SentRequest, signal: AbortSignal): Promise<SentResponse>;
  reportFailure(body: AppFailureRequest, signal: AbortSignal): Promise<AppFailureResponse>;
  submitResult(body: AppResultSubmission, signal: AbortSignal): Promise<AppResultResponse>;
}

// `unavailable`: network error, timeout, 5xx, or a body that is not the agreed shape — retry later.
// `stale`: 409 (STALE_LEASE, or RESULT_IDEMPOTENCY_CONFLICT on results) — the lease is not ours.
// `rejected`: any other 4xx; `code` is the control plane's error code when the body has one.
// `invalid_request`: the collector's own body failed the contract before it was sent.
export type ControlPlaneErrorKind = "unavailable" | "stale" | "rejected" | "invalid_request";
export class AppControlPlaneError extends Error {
  constructor(
    readonly kind: ControlPlaneErrorKind,
    readonly status: number | null = null,
    readonly code: string | null = null,
  ) {
    super(code ? `${kind}:${code}` : kind);
    this.name = "AppControlPlaneError";
  }
}

export interface AppControlPlaneConfig {
  apiUrl: string;
  nodeToken: string;
  timeoutMs: number;
}

const MAX_RESPONSE_BYTES = 2_000_000;

export class AppControlPlaneClient implements AppControlPlane {
  constructor(
    private readonly config: AppControlPlaneConfig,
    private readonly request: typeof fetch = fetch,
  ) {}

  reportBindings(body: BindingReportRequest, signal: AbortSignal) {
    return this.post(
      "bindings/report",
      appBindingReportRequestSchema,
      body,
      appBindingReportResponseSchema,
      signal,
    );
  }
  lease(body: LeaseRequest, signal: AbortSignal) {
    return this.post("leases", appLeaseRequestSchema, body, appLeaseResponseSchema, signal);
  }
  heartbeat(body: HeartbeatRequest, signal: AbortSignal) {
    return this.post(
      "heartbeats",
      appHeartbeatRequestSchema,
      body,
      appHeartbeatResponseSchema,
      signal,
    );
  }
  reportFailure(body: AppFailureRequest, signal: AbortSignal) {
    return this.post("failures", appFailureRequestSchema, body, appFailureResponseSchema, signal);
  }
  reportSent(body: SentRequest, signal: AbortSignal) {
    return this.post("sent", appSentRequestSchema, body, appSentResponseSchema, signal);
  }
  submitResult(body: AppResultSubmission, signal: AbortSignal) {
    return this.post("results", appResultSubmissionSchema, body, appResultResponseSchema, signal);
  }

  private async post<I, O>(
    route: string,
    requestSchema: z.ZodType<I>,
    body: unknown,
    responseSchema: z.ZodType<O>,
    parent: AbortSignal,
  ): Promise<O> {
    const checked = requestSchema.safeParse(body);
    if (!checked.success) throw new AppControlPlaneError("invalid_request");
    const signal = AbortSignal.any([parent, AbortSignal.timeout(this.config.timeoutMs)]);
    let response: Response;
    try {
      response = await this.request(
        new URL(`/internal/collector/app/${route}`, this.config.apiUrl),
        {
          method: "POST",
          redirect: "error",
          signal,
          headers: {
            authorization: `Bearer ${this.config.nodeToken}`,
            "content-type": "application/json",
          },
          body: JSON.stringify(checked.data),
        },
      );
    } catch {
      throw new AppControlPlaneError("unavailable");
    }
    let json: unknown;
    try {
      json = JSON.parse(await readBounded(response));
    } catch {
      json = undefined;
    }
    if (response.ok) {
      const decoded = responseSchema.safeParse(json);
      if (!decoded.success) throw new AppControlPlaneError("unavailable", response.status);
      return decoded.data;
    }
    const code = appControlPlaneErrorSchema.safeParse(json).data?.error.code ?? null;
    if (response.status === 409) throw new AppControlPlaneError("stale", 409, code);
    if (response.status >= 400 && response.status < 500)
      throw new AppControlPlaneError("rejected", response.status, code);
    throw new AppControlPlaneError("unavailable", response.status, code);
  }
}

async function readBounded(response: Response): Promise<string> {
  if (!response.body) return "";
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let bytes = 0;
  try {
    for (;;) {
      const part = await reader.read();
      if (part.done) break;
      bytes += part.value.byteLength;
      if (bytes > MAX_RESPONSE_BYTES) throw new Error("response too large");
      chunks.push(part.value);
    }
  } finally {
    await reader.cancel().catch(() => undefined);
  }
  return Buffer.concat(chunks).toString("utf8");
}

// A real collector never points at this repository's Mock queue (4311) or the development fixture
// queue (4312); the URL must be http(s) without credentials, query or fragment.
export const FORBIDDEN_CONTROL_PLANE_PORTS = ["4311", "4312"] as const;
export function controlPlaneUrlProblem(value: string): string | undefined {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return "must be an absolute URL";
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") return "must be http or https";
  if (url.username || url.password || url.search || url.hash)
    return "must not carry credentials, a query or a fragment";
  if ((FORBIDDEN_CONTROL_PLANE_PORTS as readonly string[]).includes(url.port))
    return "must not be the Mock queue (4311) or the development fixture queue (4312)";
  return undefined;
}
