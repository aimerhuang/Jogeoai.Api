import { z } from "zod";

// App-channel collector interface of the Jogeoai control plane, routes under
// /internal/collector/app/ (Jogeoai.Api task 11-16-app-channel-monitoring-api, contract.md §9,
// v0.5 plus the device-v1 dispatch extension). Nullable fields are written as null by the control plane.
// Identifiers are matched as GUID-shaped strings: the control plane mixes MySQL UUID() values and
// .NET Guid values, so no version or variant bits are assumed.

const id = z.guid();
const nodeId = z.string().trim().min(1).max(128);
// Times from the control plane end in Z; an explicit zero offset is accepted as well.
const instant = z.iso.datetime({ offset: true });
const utcInstant = z.iso
  .datetime({ offset: true })
  .refine((value) => value.endsWith("Z") || value.endsWith("+00:00"), "must be UTC");
export const APP_DISPATCH_PROTOCOL = "device-v1" as const;
const dispatchProtocol = z.literal(APP_DISPATCH_PROTOCOL);

export const appPlatformCodeSchema = z.enum([
  "doubao",
  "ernie",
  "zhipu_qingyan",
  "qwen",
  "kimi",
  "deepseek",
  "tencent_yuanbao",
]);
export type AppPlatformCode = z.infer<typeof appPlatformCodeSchema>;

// §9.2: why a binding is stopped. The collector may report all but `disabled_by_operator`.
export const appBindingStopReasonSchema = z.enum([
  "device_offline",
  "version_mismatch",
  "contract_unverified",
  "login_required",
  "challenge_required",
  "contract_changed",
  "emulator_rejected",
  "disabled_by_operator",
]);
export type AppBindingStopReason = z.infer<typeof appBindingStopReasonSchema>;
export const appBindingHealthSchema = z.enum([
  "ok",
  ...appBindingStopReasonSchema.exclude(["disabled_by_operator"]).options,
]);
export type AppBindingHealth = z.infer<typeof appBindingHealthSchema>;

// ---- §9.2 POST bindings/report

export const appBindingReportItemSchema = z
  .object({
    deviceId: z.string().trim().min(1).max(128),
    platformCode: appPlatformCodeSchema,
    accountLabel: z.string().trim().min(1).max(128),
    appPackage: z.string().trim().min(1).max(255),
    reviewedAppVersion: z.string().max(64).nullable(),
    installedAppVersion: z.string().max(64).nullable(),
    androidVersion: z.string().max(32).nullable(),
    health: appBindingHealthSchema,
  })
  .strict();
export const appBindingReportRequestSchema = z
  .object({
    nodeId,
    collectorBuildRevision: z.string().trim().min(1).max(128),
    bindings: z.array(appBindingReportItemSchema).max(200),
  })
  .strict();
export const appRegisteredBindingSchema = z.object({
  deviceId: z.string().min(1),
  platformCode: appPlatformCodeSchema,
  bindingId: id,
  status: z.enum(["active", "stopped"]),
  stopReason: appBindingStopReasonSchema.nullable(),
  cooldownUntil: instant.nullable(),
  minIntervalSeconds: z.number().int().positive(),
  dailyQuota: z.number().int().positive().nullable(),
});
export const appBindingRejectionSchema = z.object({
  // Echoes of the request: null when the request left the field out.
  deviceId: z.string().nullable(),
  platformCode: z.string().nullable(),
  code: z.string().min(1),
  message: z.string(),
});
export const appBindingReportResponseSchema = z.object({
  dispatchProtocol,
  bindings: z.array(appRegisteredBindingSchema),
  rejected: z.array(appBindingRejectionSchema),
});

// ---- §9.3 POST leases

export const appLeaseRequestSchema = z
  .object({
    nodeId,
    bindingId: id,
    leaseSeconds: z.number().int().min(5).max(900),
    dispatchProtocol,
    deviceOnline: z.literal(true),
  })
  .strict();
export const appLeasedTaskSchema = z.object({
  id,
  tenantId: id,
  runId: id,
  checkId: id,
  brandProjectId: id,
  platformCode: appPlatformCodeSchema,
  platformEntryCode: z.string().min(1),
  // The question's identifier (not a question version), as on the Web channel.
  questionVersionId: id,
  questionText: z.string().min(1),
  region: z.string().min(1),
  language: z.string().min(1),
  channel: z.literal("consumer_app"),
  promptRuleVersion: z.number().int(),
  deadlineAt: instant,
  leaseVersion: z.number().int().positive(),
  leaseExpiresAt: instant,
  bindingId: id,
});
export type AppLeasedTask = z.infer<typeof appLeasedTaskSchema>;
// In the control plane's order of evaluation.
export const appLeaseEmptyReasonSchema = z.enum([
  "binding_stopped",
  "binding_cooldown",
  "device_busy",
  "device_cooldown",
  "device_offline",
  "binding_interval",
  "binding_quota",
  "platform_paused",
  "platform_unavailable",
  "platform_interval",
  "platform_daily_cap",
  "credit_insufficient",
  "no_task",
]);
export type AppLeaseEmptyReason = z.infer<typeof appLeaseEmptyReasonSchema>;
export const appLeaseResponseSchema = z
  .object({
    task: appLeasedTaskSchema.nullable(),
    reason: appLeaseEmptyReasonSchema.nullable(),
    retryAfterSeconds: z.number().int().nonnegative().nullable(),
  })
  .refine((value) => (value.task === null) === (value.reason !== null), {
    message: "exactly one of task and reason",
    path: ["reason"],
  });
export type AppLeaseResponse = z.infer<typeof appLeaseResponseSchema>;

// ---- §9.4 POST heartbeats

export const appHeartbeatRequestSchema = z
  .object({
    nodeId,
    taskId: id,
    leaseVersion: z.number().int().positive(),
    extendSeconds: z.number().int().min(5).max(900),
  })
  .strict();
export const appHeartbeatResponseSchema = z.object({ leaseExpiresAt: instant });

// device-v1: physical send acknowledgement, not the lease time or the result submission time.
export const appSentRequestSchema = z
  .object({
    nodeId,
    bindingId: id,
    taskId: id,
    leaseVersion: z.number().int().positive(),
    sentAt: utcInstant,
  })
  .strict();
export const appSentResponseSchema = z.object({
  disposition: z.enum(["accepted", "duplicate", "stale"]),
});
export const appSendStateSchema = z.enum(["not_sent", "sent", "unknown"]);
export type AppSendState = z.infer<typeof appSendStateSchema>;

// ---- §9.5 POST failures

export const appControlPlaneFailureCodeSchema = z.enum([
  "login_required",
  "challenge_required",
  "rate_limited",
  "emulator_rejected",
  "contract_changed",
  "contract_unverified",
  "version_mismatch",
  "device_offline",
  "app_crashed",
  "collection_timeout",
]);
export type AppControlPlaneFailureCode = z.infer<typeof appControlPlaneFailureCodeSchema>;
export const appFailureRequestSchema = z
  .object({
    nodeId,
    bindingId: id,
    failureCode: appControlPlaneFailureCodeSchema,
    phase: z.enum(["probe", "pre_send", "post_send"]),
    sendState: appSendStateSchema.optional(),
    sentAt: utcInstant.optional(),
    task: z.object({ taskId: id, leaseVersion: z.number().int().positive() }).strict().optional(),
  })
  .strict()
  .refine((value) => (value.phase === "probe") === (value.task === undefined), {
    message: "probe carries no task; pre_send and post_send must",
    path: ["task"],
  })
  .refine(
    (v) =>
      v.phase === "probe"
        ? v.sendState === undefined
        : v.phase === "pre_send"
          ? v.sendState === "not_sent"
          : v.sendState === "sent" || v.sendState === "unknown",
    {
      path: ["sendState"],
      message: "send state must match task phase",
    },
  )
  .refine((v) => (v.sendState === "sent") === (v.sentAt !== undefined), {
    path: ["sentAt"],
    message: "set only for a confirmed send",
  });
export type AppFailureRequest = z.infer<typeof appFailureRequestSchema>;
export const appFailureResponseSchema = z.object({
  bindingStatus: z.enum(["active", "stopped"]),
  stopReason: appBindingStopReasonSchema.nullable(),
  cooldownUntil: instant.nullable(),
  taskDisposition: z.enum(["requeued", "failed", "stale"]).nullable(),
  platformPaused: z.boolean(),
  platformUnavailable: z.boolean(),
});
export type AppFailureResponse = z.infer<typeof appFailureResponseSchema>;

// ---- §9.6 POST results

const sourceListLabel = z.string().regex(/^[a-z][a-z0-9_]{0,19}$/u);
const visibleState = z.enum(["on", "off", "unknown"]);
export const APP_RESULT_LIMITS = {
  answerBytes: 1024 * 1024,
  citations: 100,
  citationsEstimatedBytes: 262_144,
  titleChars: 512,
  siteChars: 255,
  urlChars: 2048,
  searchQueries: 32,
  searchQueryChars: 500,
} as const;
export const appResultCitationSchema = z
  .object({
    title: z.string().trim().min(1).max(APP_RESULT_LIMITS.titleChars),
    site: z.string().max(APP_RESULT_LIMITS.siteChars).nullable(),
    url: z.string().max(APP_RESULT_LIMITS.urlChars).nullable(),
    list: sourceListLabel.nullable(),
  })
  .strict();
export const appResultSubmissionSchema = z
  .object({
    nodeId,
    taskId: id,
    leaseVersion: z.number().int().positive(),
    idempotencyKey: z.string().min(8).max(191),
    collectorBuildRevision: z.string().trim().min(1).max(128),
    sentAt: utcInstant,
    completedAt: utcInstant,
    answerText: z.string().refine((value) => value.trim().length > 0, "must not be blank"),
    citations: z.array(appResultCitationSchema).max(APP_RESULT_LIMITS.citations),
    citationsStatus: z.enum(["read", "absent", "unreadable", "not_declared"]),
    citationsIssue: z
      .string()
      .regex(/^[a-z][a-z0-9_]{0,63}$/u)
      .nullable(),
    searchState: z
      .object({
        webSearch: visibleState,
        deepThinking: visibleState,
        searchQueries: z
          .array(z.string().trim().min(1).max(APP_RESULT_LIMITS.searchQueryChars))
          .max(APP_RESULT_LIMITS.searchQueries)
          .nullable(),
      })
      .strict()
      .nullable(),
    device: z
      .object({
        deviceId: z.string().trim().min(1).max(128),
        androidVersion: z.string().trim().min(1).max(32),
        abi: z.string().max(32).nullable(),
      })
      .strict(),
    app: z
      .object({
        package: z.string().trim().min(1).max(255),
        version: z.string().trim().min(1).max(64),
      })
      .strict(),
    screenshotSha256: z
      .string()
      .regex(/^[0-9a-fA-F]{64}$/u)
      .nullable(),
  })
  .strict()
  .superRefine((value, context) => {
    if ((value.citationsStatus === "unreadable") !== (value.citationsIssue !== null))
      context.addIssue({
        code: "custom",
        path: ["citationsIssue"],
        message: "set exactly when the sources were unreadable",
      });
    if ((value.citationsStatus === "read") !== value.citations.length > 0)
      context.addIssue({
        code: "custom",
        path: ["citations"],
        message: "non-empty exactly when the sources were read",
      });
    if (Buffer.byteLength(value.answerText, "utf8") > APP_RESULT_LIMITS.answerBytes)
      context.addIssue({ code: "custom", path: ["answerText"], message: "longer than 1 MiB" });
    if (estimatedJsonBytes(value.citations) > APP_RESULT_LIMITS.citationsEstimatedBytes)
      context.addIssue({ code: "custom", path: ["citations"], message: "too large" });
  });
export type AppResultSubmission = z.infer<typeof appResultSubmissionSchema>;
export const appResultResponseSchema = z.object({
  disposition: z.enum(["accepted", "duplicate", "stale"]),
  sampleId: id.nullable(),
});
export type AppResultResponse = z.infer<typeof appResultResponseSchema>;

// The control plane's size estimate (§9.6 limits): UTF-8 bytes of the serialized value plus the
// number of commas and colons in it, those inside strings included. Never lower than the real size.
export function estimatedJsonBytes(value: unknown): number {
  const text = JSON.stringify(value);
  let separators = 0;
  for (const character of text) if (character === "," || character === ":") separators += 1;
  return Buffer.byteLength(text, "utf8") + separators;
}

// ---- Errors (§9.1): every listed error has this body.

export const appControlPlaneErrorSchema = z.object({
  error: z.object({ code: z.string().min(1), message: z.string() }),
});
