import { describe, expect, it } from "vitest";

import {
  collectorFailureResponseSchema,
  collectorFailureSchema,
  eligibilityRequestSchema,
  eligibilityResponseSchema,
  leaseRequestSchema,
  leasedTaskSchema,
  resultSubmissionSchema,
  sessionEventSchema,
} from "./index.ts";

const legacySubmission = {
  tenantId: "11111111-1111-4111-8111-111111111111",
  taskId: "77777777-7777-4777-8777-777777777777",
  nodeId: "test-node",
  region: "cn",
  leaseVersion: 1,
  idempotencyKey: "test-idempotency-key",
  egressCountry: "cn",
  collectorBuildRevision: "test",
  answerText: "synthetic answer",
  citations: ["https://example.com/a", "https://example.com/b"],
  completedAt: "2026-10-21T06:00:00.000Z",
};

describe("result submission contract", () => {
  it("keeps accepting a legacy payload without titles or search queries", () => {
    const parsed = resultSubmissionSchema.parse(legacySubmission);
    expect(parsed).toEqual({ ...legacySubmission, egressCountry: "CN" });
    expect(parsed).not.toHaveProperty("citationTitles");
    expect(parsed).not.toHaveProperty("searchQueries");
  });
  it("accepts aligned citation titles (nullable) and search queries", () => {
    const parsed = resultSubmissionSchema.parse({
      ...legacySubmission,
      citationTitles: ["来源标题 A", null],
      searchQueries: ["管道内窥镜 品牌", "工业内窥镜 推荐"],
    });
    expect(parsed.citationTitles).toEqual(["来源标题 A", null]);
    expect(parsed.searchQueries).toEqual(["管道内窥镜 品牌", "工业内窥镜 推荐"]);
  });
  it("accepts empty aligned arrays", () => {
    expect(
      resultSubmissionSchema.safeParse({
        ...legacySubmission,
        citations: [],
        citationTitles: [],
        searchQueries: [],
      }).success,
    ).toBe(true);
  });
  it.each([
    ["fewer titles than citations", { citationTitles: ["only one"] }],
    ["more titles than citations", { citationTitles: ["a", "b", "c"] }],
    ["titles without citations", { citations: [], citationTitles: ["a"] }],
  ])("rejects misaligned titles: %s", (_label, patch) => {
    const result = resultSubmissionSchema.safeParse({ ...legacySubmission, ...patch });
    expect(result.success).toBe(false);
    expect(result.error?.issues[0]?.path).toEqual(["citationTitles"]);
  });
  it.each([
    ["empty title", { citationTitles: ["", null] }],
    ["overlong title", { citationTitles: ["题".repeat(513), null] }],
    ["empty query", { searchQueries: [""] }],
    ["overlong query", { searchQueries: ["词".repeat(501)] }],
    ["too many queries", { searchQueries: Array.from({ length: 33 }, (_, i) => `q${i}`) }],
  ])("rejects out-of-range optional fields: %s", (_label, patch) => {
    expect(resultSubmissionSchema.safeParse({ ...legacySubmission, ...patch }).success).toBe(false);
  });
  it("accepts the documented maximum sizes", () => {
    expect(
      resultSubmissionSchema.safeParse({
        ...legacySubmission,
        citationTitles: ["题".repeat(512), null],
        searchQueries: Array.from({ length: 32 }, () => "词".repeat(500)),
      }).success,
    ).toBe(true);
  });
});

const profileId = "22222222-2222-4222-8222-222222222222";
const reportBase = {
  tenantId: legacySubmission.tenantId,
  nodeId: "test-node",
  region: "cn",
  sessionProfileId: profileId,
};
const reportedTask = { taskId: legacySubmission.taskId, leaseVersion: 3 };

describe("lease contract with session profiles", () => {
  const legacyLease = {
    tenantId: legacySubmission.tenantId,
    nodeId: "test-node",
    region: "cn",
    supportedPlatformCodes: ["doubao"],
  };
  it("keeps accepting a lease request without a session profile", () => {
    const parsed = leaseRequestSchema.parse(legacyLease);
    expect(parsed).not.toHaveProperty("sessionProfileId");
    expect(parsed.capacity).toBe(1);
  });
  it("accepts a session profile id and rejects a malformed one", () => {
    expect(
      leaseRequestSchema.parse({ ...legacyLease, sessionProfileId: profileId }).sessionProfileId,
    ).toBe(profileId);
    expect(
      leaseRequestSchema.safeParse({ ...legacyLease, sessionProfileId: "profile-1" }).success,
    ).toBe(false);
  });
  it("accepts leased tasks with, without, or with a null session profile", () => {
    const task = {
      id: legacySubmission.taskId,
      tenantId: legacySubmission.tenantId,
      brandProjectId: "33333333-3333-4333-8333-333333333333",
      platformCode: "doubao",
      platformEntryCode: "doubao.consumer_web",
      platformEntryVersionId: "44444444-4444-4444-8444-444444444444",
      platformEntryVersion: 1,
      questionVersionId: "55555555-5555-4555-8555-555555555555",
      questionText: "synthetic question",
      region: "cn",
      language: "zh-CN",
      channel: "consumer_web",
      leaseVersion: 1,
      leaseExpiresAt: "2026-10-28T06:00:00.000Z",
    };
    expect(leasedTaskSchema.safeParse(task).success).toBe(true);
    expect(leasedTaskSchema.safeParse({ ...task, sessionProfileId: null }).success).toBe(true);
    expect(leasedTaskSchema.parse({ ...task, sessionProfileId: profileId }).sessionProfileId).toBe(
      profileId,
    );
  });
});

describe("collector failure contract", () => {
  it("accepts a pre-send challenge with its task and a probe failure without one", () => {
    expect(
      collectorFailureSchema.safeParse({
        ...reportBase,
        failureCode: "challenge_required",
        phase: "pre_send",
        task: reportedTask,
      }).success,
    ).toBe(true);
    expect(
      collectorFailureSchema.safeParse({
        ...reportBase,
        failureCode: "login_required",
        phase: "probe",
      }).success,
    ).toBe(true);
  });
  it.each([
    [
      "probe with a task",
      { failureCode: "challenge_required", phase: "probe", task: reportedTask },
    ],
    ["post-send without a task", { failureCode: "rate_limited", phase: "post_send" }],
    ["local-only code", { failureCode: "stale_lease", phase: "pre_send", task: reportedTask }],
    [
      "missing profile",
      { sessionProfileId: undefined, failureCode: "rate_limited", phase: "probe" },
    ],
  ])("rejects %s", (_label, patch) => {
    expect(collectorFailureSchema.safeParse({ ...reportBase, ...patch }).success).toBe(false);
  });
  it("describes the profile and task outcome", () => {
    expect(
      collectorFailureResponseSchema.safeParse({
        sessionProfileStatus: "needs_verification",
        taskDisposition: "requeued",
        platformPaused: false,
      }).success,
    ).toBe(true);
    expect(
      collectorFailureResponseSchema.safeParse({
        sessionProfileStatus: "paused",
        taskDisposition: null,
        platformPaused: true,
      }).success,
    ).toBe(false);
  });
});

describe("session event contract", () => {
  it("accepts verified only from a task-less probe", () => {
    expect(
      sessionEventSchema.safeParse({ ...reportBase, eventKind: "verified", phase: "probe" })
        .success,
    ).toBe(true);
    expect(
      sessionEventSchema.safeParse({
        ...reportBase,
        eventKind: "verified",
        phase: "pre_send",
        task: reportedTask,
      }).success,
    ).toBe(false);
  });
  it("requires a task for challenge wait events", () => {
    expect(
      sessionEventSchema.safeParse({
        ...reportBase,
        eventKind: "challenge_wait_started",
        phase: "post_send",
        task: reportedTask,
      }).success,
    ).toBe(true);
    expect(
      sessionEventSchema.safeParse({
        ...reportBase,
        eventKind: "challenge_cleared",
        phase: "pre_send",
      }).success,
    ).toBe(false);
  });
});

describe("eligibility contract", () => {
  const request = {
    tenantId: "11111111-1111-4111-8111-111111111111",
    nodeId: "pool-node-66666666",
    region: "cn",
    sessionProfileId: "66666666-6666-4666-8666-666666666666",
    platformCode: "doubao",
  };
  it("requires a session profile and one known platform", () => {
    expect(eligibilityRequestSchema.parse(request)).toEqual(request);
    expect(
      eligibilityRequestSchema.safeParse({ ...request, sessionProfileId: undefined }).success,
    ).toBe(false);
    expect(eligibilityRequestSchema.safeParse({ ...request, platformCode: "other" }).success).toBe(
      false,
    );
    expect(eligibilityRequestSchema.safeParse({ ...request, region: "eu" }).success).toBe(false);
  });
  it("bounds retryAfterSeconds to whole seconds between 30 and 120", () => {
    for (const retryAfterSeconds of [30, 75, 120])
      expect(
        eligibilityResponseSchema.safeParse({ eligible: false, retryAfterSeconds }).success,
      ).toBe(true);
    for (const retryAfterSeconds of [29, 121, 45.5, -1])
      expect(
        eligibilityResponseSchema.safeParse({ eligible: false, retryAfterSeconds }).success,
      ).toBe(false);
  });
  it("accepts operator stop reasons only on an ineligible answer", () => {
    for (const stopReason of ["needs_verification", "expired", "disabled"])
      expect(
        eligibilityResponseSchema.safeParse({ eligible: false, retryAfterSeconds: 120, stopReason })
          .success,
      ).toBe(true);
    expect(
      eligibilityResponseSchema.safeParse({
        eligible: false,
        retryAfterSeconds: 120,
        stopReason: "cooling",
      }).success,
    ).toBe(false);
    expect(
      eligibilityResponseSchema.safeParse({
        eligible: true,
        retryAfterSeconds: 30,
        stopReason: "disabled",
      }).success,
    ).toBe(false);
  });
});
