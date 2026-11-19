import { describe, expect, it } from "vitest";
import {
  appBindingReportResponseSchema,
  appFailureRequestSchema,
  appLeaseRequestSchema,
  appLeaseResponseSchema,
  appResultSubmissionSchema,
  estimatedJsonBytes,
} from "./index.ts";

const TASK = "0b9c3c5e-c4a1-11f0-9a3b-0242ac120002";
const BINDING = "6f1f5f2a-2b8e-4a53-9d36-1c4f0d0e8a11";
const leasedTask = {
  id: TASK,
  tenantId: "11111111-1111-4111-8111-111111111111",
  runId: "22222222-2222-4222-8222-222222222222",
  checkId: "33333333-3333-4333-8333-333333333333",
  brandProjectId: "44444444-4444-4444-8444-444444444444",
  platformCode: "doubao",
  platformEntryCode: "doubao.consumer_app",
  questionVersionId: "55555555-5555-4555-8555-555555555555",
  questionText: "哪款扫地机器人适合养猫家庭？",
  region: "cn",
  language: "zh-CN",
  channel: "consumer_app",
  promptRuleVersion: 1,
  deadlineAt: "2026-11-23T15:59:59.000000Z",
  leaseVersion: 1,
  leaseExpiresAt: "2026-11-17T08:01:00.123Z",
  bindingId: BINDING,
};
const validResult = {
  nodeId: "mac-local",
  taskId: TASK,
  leaseVersion: 1,
  idempotencyKey: "8f6d1a2e-0d7c-4a8e-9a55-2d2b1f0c9e10.1",
  collectorBuildRevision: "abc123",
  sentAt: "2026-11-17T08:00:10.000Z",
  completedAt: "2026-11-17T08:00:50.000Z",
  answerText: "推荐……",
  citations: [{ title: "评测", site: "example.com", url: "https://example.com/a", list: null }],
  citationsStatus: "read",
  citationsIssue: null,
  searchState: { webSearch: "on", deepThinking: "unknown", searchQueries: null },
  device: { deviceId: "AI_Apps_4KB", androidVersion: "15", abi: "arm64-v8a" },
  app: { package: "com.larus.nova", version: "15.7.0" },
  screenshotSha256: "a".repeat(64),
};

describe("App-channel control-plane contract (contract.md §9)", () => {
  it("decodes a lease with a task and an empty lease, but not both or neither", () => {
    expect(
      appLeaseResponseSchema.parse({ task: leasedTask, reason: null, retryAfterSeconds: null }).task
        ?.id,
    ).toBe(TASK);
    expect(
      appLeaseResponseSchema.parse({ task: null, reason: "no_task", retryAfterSeconds: 60 }).reason,
    ).toBe("no_task");
    expect(
      appLeaseResponseSchema.safeParse({ task: null, reason: null, retryAfterSeconds: null })
        .success,
    ).toBe(false);
    expect(
      appLeaseResponseSchema.safeParse({
        task: { ...leasedTask, channel: "consumer_web" },
        reason: null,
        retryAfterSeconds: null,
      }).success,
    ).toBe(false);
  });
  it("decodes a binding report with null echoes in rejected entries", () => {
    const parsed = appBindingReportResponseSchema.parse({
      dispatchProtocol: "device-v1",
      bindings: [
        {
          deviceId: "AI_Apps_4KB",
          platformCode: "doubao",
          bindingId: BINDING,
          status: "active",
          stopReason: null,
          cooldownUntil: null,
          minIntervalSeconds: 100,
          dailyQuota: null,
        },
      ],
      rejected: [{ deviceId: null, platformCode: null, code: "INVALID_BINDING", message: "…" }],
    });
    expect(parsed.bindings[0]?.minIntervalSeconds).toBe(100);
  });
  it("admits lease requests only with explicit online proof and device-v1", () => {
    const base = { nodeId: "n", bindingId: BINDING, leaseSeconds: 180 };
    expect(appLeaseRequestSchema.safeParse(base).success).toBe(false);
    expect(
      appLeaseRequestSchema.safeParse({
        ...base,
        dispatchProtocol: "device-v1",
        deviceOnline: false,
      }).success,
    ).toBe(false);
    expect(
      appLeaseRequestSchema.safeParse({
        ...base,
        dispatchProtocol: "device-v1",
        deviceOnline: true,
      }).success,
    ).toBe(true);
  });
  it("requires a task for pre_send and post_send failures and none for probe", () => {
    const base = { nodeId: "n", bindingId: BINDING, failureCode: "login_required" } as const;
    expect(appFailureRequestSchema.safeParse({ ...base, phase: "probe" }).success).toBe(true);
    expect(appFailureRequestSchema.safeParse({ ...base, phase: "pre_send" }).success).toBe(false);
    expect(
      appFailureRequestSchema.safeParse({
        ...base,
        phase: "post_send",
        sendState: "unknown",
        task: { taskId: TASK, leaseVersion: 2 },
      }).success,
    ).toBe(true);
    expect(
      appFailureRequestSchema.safeParse({
        ...base,
        failureCode: "recording_failed",
        phase: "probe",
      }).success,
    ).toBe(false);
  });
  it("accepts a timestamp only for a confirmed physical send and never downgrades an ambiguous tap", () => {
    const body = {
      nodeId: "n",
      bindingId: BINDING,
      failureCode: "app_crashed",
      task: { taskId: TASK, leaseVersion: 1 },
    };
    const valid = (patch: Record<string, unknown>) =>
      appFailureRequestSchema.safeParse({ ...body, ...patch }).success;
    const sentAt = "2026-11-18T00:00:00.000Z";
    expect(valid({ phase: "pre_send", sendState: "not_sent" })).toBe(true);
    expect(valid({ phase: "post_send", sendState: "not_sent" })).toBe(false);
    expect(valid({ phase: "post_send", sendState: "sent", sentAt })).toBe(true);
    expect(valid({ phase: "post_send", sendState: "sent" })).toBe(false);
    expect(valid({ phase: "post_send", sendState: "unknown", sentAt })).toBe(false);
    expect(valid({ phase: "pre_send", sendState: "unknown" })).toBe(false);
  });
  it("checks a result the way the control plane does", () => {
    expect(appResultSubmissionSchema.safeParse(validResult).success).toBe(true);
    const invalid = (patch: Record<string, unknown>) =>
      appResultSubmissionSchema.safeParse({ ...validResult, ...patch }).success;
    expect(invalid({ sentAt: "2026-11-17T16:00:10.000+08:00" })).toBe(false);
    expect(invalid({ answerText: "   " })).toBe(false);
    expect(invalid({ citationsStatus: "absent" })).toBe(false);
    expect(invalid({ citations: [], citationsStatus: "unreadable", citationsIssue: null })).toBe(
      false,
    );
    expect(
      invalid({ citations: [], citationsStatus: "unreadable", citationsIssue: "driver_x" }),
    ).toBe(true);
    expect(invalid({ screenshotSha256: "xyz" })).toBe(false);
    expect(invalid({ idempotencyKey: "short" })).toBe(false);
  });
  it("estimates sizes like the control plane: bytes plus commas and colons", () => {
    // {"a":"x,y"} is 11 bytes with one colon and one comma.
    expect(estimatedJsonBytes({ a: "x,y" })).toBe(13);
  });
});
