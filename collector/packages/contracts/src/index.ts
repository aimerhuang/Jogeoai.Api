import { z } from "zod";

export const regionSchema = z.enum(["cn", "overseas"]);
export const platformCodeSchema = z.enum([
  "doubao",
  "ernie",
  "zhipu_qingyan",
  "qwen",
  "kimi",
  "deepseek",
  "tencent_yuanbao",
  "chatgpt",
  "claude",
  "gemini",
]);

export const leaseRequestSchema = z.object({
  tenantId: z.uuid(),
  nodeId: z.string().min(1).max(128),
  region: regionSchema,
  supportedPlatformCodes: z.array(platformCodeSchema).min(1),
  capacity: z.number().int().min(1).max(50).default(1),
  leaseSeconds: z.number().int().min(5).max(900).default(60),
  // Registered account (session profile) doing the work. Required by the control plane once
  // the tenant registers profiles for a requested platform; capacity is then forced to 1.
  sessionProfileId: z.uuid().optional(),
});

export const leasedTaskSchema = z.object({
  id: z.uuid(),
  tenantId: z.uuid(),
  brandProjectId: z.uuid(),
  platformCode: platformCodeSchema,
  platformEntryCode: z.string().min(1),
  platformEntryVersionId: z.uuid(),
  platformEntryVersion: z.number().int().positive(),
  questionVersionId: z.uuid(),
  questionText: z.string().min(1),
  region: regionSchema,
  language: z.string().min(2),
  channel: z.literal("consumer_web"),
  leaseVersion: z.number().int().positive(),
  leaseExpiresAt: z.iso.datetime(),
  // Null for legacy leases. Optional so collectors still accept control planes that predate it.
  sessionProfileId: z.uuid().nullable().optional(),
});

export const leaseResponseSchema = z.object({ tasks: z.array(leasedTaskSchema) });

export const heartbeatRequestSchema = z.object({
  tenantId: z.uuid(),
  taskId: z.uuid(),
  nodeId: z.string().min(1).max(128),
  leaseVersion: z.number().int().positive(),
  extendSeconds: z.number().int().min(5).max(900).default(60),
});

export const heartbeatResponseSchema = z.object({ leaseExpiresAt: z.iso.datetime() });

export const resultSubmissionSchema = z
  .object({
    tenantId: z.uuid(),
    taskId: z.uuid(),
    nodeId: z.string().min(1).max(128),
    region: regionSchema,
    leaseVersion: z.number().int().positive(),
    idempotencyKey: z.string().min(8).max(200),
    egressCountry: z
      .string()
      .length(2)
      .transform((value) => value.toUpperCase()),
    collectorBuildRevision: z.string().min(1).max(128),
    answerText: z.string().min(1),
    citations: z.array(z.url()).max(100).default([]),
    // Optional, position-aligned with `citations`; null when a source has no readable title.
    citationTitles: z.array(z.string().min(1).max(512).nullable()).max(100).optional(),
    // Optional search keywords shown by the platform. Omitted when not exposed or unparseable.
    searchQueries: z.array(z.string().min(1).max(500)).max(32).optional(),
    completedAt: z.iso.datetime(),
  })
  .refine(
    (value) =>
      value.citationTitles === undefined || value.citationTitles.length === value.citations.length,
    { message: "citationTitles must align with citations", path: ["citationTitles"] },
  );

export const resultSubmissionResponseSchema = z.object({
  disposition: z.enum(["accepted", "duplicate", "stale"]),
  sampleId: z.uuid().nullable(),
});

export const sessionProfileStatusSchema = z.enum([
  "active",
  "cooling",
  "needs_verification",
  "expired",
  "disabled",
]);

// Classified collector failures the control plane accepts. Local-only conditions (stale lease,
// spooling, shutdown, approval guard) are never reported.
export const collectorFailureCodeSchema = z.enum([
  "login_required",
  "region_restricted",
  "challenge_required",
  "rate_limited",
  "page_contract_changed",
  "page_contract_unverified",
  "collection_timeout",
  "browser_failed",
]);

export const collectorPhaseSchema = z.enum(["probe", "pre_send", "post_send"]);

const reportedTaskSchema = z.object({
  taskId: z.uuid(),
  leaseVersion: z.number().int().positive(),
});

export const collectorFailureSchema = z
  .object({
    tenantId: z.uuid(),
    nodeId: z.string().min(1).max(128),
    region: regionSchema,
    sessionProfileId: z.uuid(),
    failureCode: collectorFailureCodeSchema,
    phase: collectorPhaseSchema,
    task: reportedTaskSchema.optional(),
  })
  .refine((value) => (value.phase === "probe") === (value.task === undefined), {
    message: "probe failures carry no task; pre_send and post_send failures require one",
    path: ["task"],
  });

export const collectorFailureResponseSchema = z.object({
  sessionProfileStatus: sessionProfileStatusSchema,
  // requeued: lease released with retry backoff; failed: retry limit reached;
  // stale: the reported lease is no longer current and the task was left alone.
  taskDisposition: z.enum(["requeued", "failed", "stale"]).nullable(),
  platformPaused: z.boolean(),
});

export const sessionEventKindSchema = z.enum([
  "challenge_wait_started",
  "challenge_cleared",
  "verified",
]);

export const sessionEventSchema = z
  .object({
    tenantId: z.uuid(),
    nodeId: z.string().min(1).max(128),
    region: regionSchema,
    sessionProfileId: z.uuid(),
    eventKind: sessionEventKindSchema,
    phase: collectorPhaseSchema,
    task: reportedTaskSchema.optional(),
  })
  .refine(
    (value) =>
      value.eventKind === "verified"
        ? value.phase === "probe" && value.task === undefined
        : value.phase !== "probe" && value.task !== undefined,
    {
      message: "verified is a probe event without a task; challenge events require a task",
      path: ["eventKind"],
    },
  );

export const sessionEventResponseSchema = z.object({
  sessionProfileStatus: sessionProfileStatusSchema,
});

// Read-only pre-check before a profile-mode collector loads the platform page (design §12.2).
// The lease call stays authoritative; eligible=true may still be followed by an empty lease.
export const eligibilityRequestSchema = z.object({
  tenantId: z.uuid(),
  nodeId: z.string().min(1).max(128),
  region: regionSchema,
  sessionProfileId: z.uuid(),
  platformCode: platformCodeSchema,
});

// Statuses only a human can clear; the collector exits for the operator instead of polling.
export const eligibilityStopReasonSchema = z.enum(["needs_verification", "expired", "disabled"]);

export const eligibilityResponseSchema = z
  .object({
    eligible: z.boolean(),
    // Seconds until the next check; bounded so polling stays inside the online window.
    retryAfterSeconds: z.number().int().min(30).max(120),
    stopReason: eligibilityStopReasonSchema.optional(),
  })
  .refine((value) => !(value.eligible && value.stopReason), {
    message: "an eligible profile carries no stop reason",
    path: ["stopReason"],
  });

export type LeaseRequest = z.infer<typeof leaseRequestSchema>;
export type LeasedTask = z.infer<typeof leasedTaskSchema>;
export type ResultSubmission = z.infer<typeof resultSubmissionSchema>;
export type ResultSubmissionResponse = z.infer<typeof resultSubmissionResponseSchema>;
export type SessionProfileStatus = z.infer<typeof sessionProfileStatusSchema>;
export type CollectorFailureCode = z.infer<typeof collectorFailureCodeSchema>;
export type CollectorFailure = z.infer<typeof collectorFailureSchema>;
export type CollectorFailureResponse = z.infer<typeof collectorFailureResponseSchema>;
export type SessionEvent = z.infer<typeof sessionEventSchema>;
export type SessionEventResponse = z.infer<typeof sessionEventResponseSchema>;
export type EligibilityRequest = z.infer<typeof eligibilityRequestSchema>;
export type EligibilityResponse = z.infer<typeof eligibilityResponseSchema>;
export type EligibilityStopReason = z.infer<typeof eligibilityStopReasonSchema>;

export * from "./app-channel.ts";
