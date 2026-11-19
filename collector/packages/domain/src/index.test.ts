import { describe, expect, it } from "vitest";

import {
  aggregateBrandGeoMetricInputs,
  assertSupportedAnalysisSubjectType,
  buildTaskBusinessKey,
  calculateBrandGeoMetrics,
  deriveBrandGeoMetricInputs,
  hashBrandMetricMemberships,
  hashResultPayload,
  type BrandGeoMetricInputs,
  type BrandMetricMembership,
} from "./index.ts";

const taskKeyInput = {
  tenantId: "11111111-1111-4111-8111-111111111111",
  brandProjectId: "22222222-2222-4222-8222-222222222222",
  questionVersionId: "50000000-0000-4000-8000-000000000001",
  platformEntryCode: "chatgpt.consumer_web",
  region: "overseas" as const,
  language: "zh-CN",
  channel: "consumer_web",
  scheduledDate: "2026-09-01",
  sampleIndex: 1,
  runKind: "scheduled" as const,
};

describe("task identity", () => {
  it("is deterministic and changes when a matrix dimension changes", () => {
    expect(buildTaskBusinessKey(taskKeyInput)).toBe(buildTaskBusinessKey(taskKeyInput));
    expect(buildTaskBusinessKey({ ...taskKeyInput, sampleIndex: 2 })).not.toBe(
      buildTaskBusinessKey(taskKeyInput),
    );
  });

  it("normalizes citation order in the payload hash", () => {
    expect(hashResultPayload("answer", ["https://b.test", "https://a.test"])).toBe(
      hashResultPayload("answer", ["https://a.test", "https://b.test"]),
    );
  });
});

const baseMetricInputs: BrandGeoMetricInputs = {
  plannedTaskCount: 10,
  completedTaskCount: 8,
  failedTaskCount: 1,
  missingTaskCount: 1,
  validAnswerCount: 8,
  mentionedAnswerCount: 4,
  rankSum: 10,
  rankCount: 4,
  firstRankCount: 1,
  top3Count: 3,
  totalMentionCount: 6,
  allMonitoredBrandMentionCount: 15,
  citedAnswerCount: 2,
  positiveCount: 2,
  neutralCount: 1,
  negativeCount: 1,
};

describe("brand GEO metrics", () => {
  it("returns transparent numerators, denominators, and values", () => {
    const metrics = Object.fromEntries(
      calculateBrandGeoMetrics(baseMetricInputs).map((metric) => [metric.code, metric]),
    );

    expect(metrics.valid_sample_count).toMatchObject({
      numerator: 8,
      denominator: 10,
      value: 8,
    });
    expect(metrics.mention_rate).toMatchObject({ numerator: 4, denominator: 8, value: 0.5 });
    expect(metrics.average_first_rank).toMatchObject({
      numerator: 10,
      denominator: 4,
      value: 2.5,
    });
    expect(metrics.first_place_rate?.value).toBe(0.125);
    expect(metrics.top3_rate?.value).toBe(0.375);
    expect(metrics.share_of_voice?.value).toBe(0.4);
    expect(metrics.citation_rate?.value).toBe(0.25);
    expect(metrics.sentiment_positive_rate?.value).toBe(0.5);
    expect(metrics.sentiment_neutral_rate?.value).toBe(0.25);
    expect(metrics.sentiment_negative_rate?.value).toBe(0.25);
  });

  it("aggregates raw components before calculating weighted rates and ranks", () => {
    const firstGroup: BrandGeoMetricInputs = {
      plannedTaskCount: 2,
      completedTaskCount: 2,
      failedTaskCount: 0,
      missingTaskCount: 0,
      validAnswerCount: 2,
      mentionedAnswerCount: 1,
      rankSum: 1,
      rankCount: 1,
      firstRankCount: 1,
      top3Count: 1,
      totalMentionCount: 1,
      allMonitoredBrandMentionCount: 3,
      citedAnswerCount: 1,
      positiveCount: 1,
      neutralCount: 0,
      negativeCount: 0,
    };
    const secondGroup: BrandGeoMetricInputs = {
      plannedTaskCount: 8,
      completedTaskCount: 8,
      failedTaskCount: 0,
      missingTaskCount: 0,
      validAnswerCount: 8,
      mentionedAnswerCount: 3,
      rankSum: 9,
      rankCount: 3,
      firstRankCount: 0,
      top3Count: 3,
      totalMentionCount: 6,
      allMonitoredBrandMentionCount: 7,
      citedAnswerCount: 1,
      positiveCount: 0,
      neutralCount: 2,
      negativeCount: 1,
    };

    const metrics = Object.fromEntries(
      calculateBrandGeoMetrics(aggregateBrandGeoMetricInputs([firstGroup, secondGroup])).map(
        (metric) => [metric.code, metric.value],
      ),
    );

    expect(metrics.mention_rate).toBe(0.4);
    expect(metrics.average_first_rank).toBe(2.5);
    expect(metrics.share_of_voice).toBe(0.7);
    expect(metrics.sentiment_positive_rate).toBe(0.25);
  });

  it("returns null for rates with no denominator and rejects impossible counts", () => {
    const empty: BrandGeoMetricInputs = {
      plannedTaskCount: 0,
      completedTaskCount: 0,
      failedTaskCount: 0,
      missingTaskCount: 0,
      validAnswerCount: 0,
      mentionedAnswerCount: 0,
      rankSum: 0,
      rankCount: 0,
      firstRankCount: 0,
      top3Count: 0,
      totalMentionCount: 0,
      allMonitoredBrandMentionCount: 0,
      citedAnswerCount: 0,
      positiveCount: 0,
      neutralCount: 0,
      negativeCount: 0,
    };
    const metrics = calculateBrandGeoMetrics(empty);
    expect(metrics.find((metric) => metric.code === "mention_rate")?.value).toBeNull();
    expect(metrics.find((metric) => metric.code === "average_first_rank")?.value).toBeNull();

    expect(() =>
      calculateBrandGeoMetrics({ ...baseMetricInputs, mentionedAnswerCount: 9, rankCount: 9 }),
    ).toThrow("mentionedAnswerCount cannot exceed validAnswerCount");
  });

  it("derives additive counts from auditable task memberships", () => {
    const zeroContribution = {
      mentionCount: 0,
      firstRank: null,
      cited: false,
      sentiment: null,
    } as const;
    const inputs = deriveBrandGeoMetricInputs([
      {
        taskId: "mentioned",
        answerSampleId: "sample-mentioned",
        brandFactId: "fact-mentioned",
        shareOfVoiceFacts: [
          {
            brandId: "brand-primary",
            brandFactId: "fact-mentioned",
            brandRuleVersionId: "rule-primary",
            mentionCount: 2,
          },
          {
            brandId: "brand-competitor",
            brandFactId: "fact-competitor-mentioned",
            brandRuleVersionId: "rule-competitor",
            mentionCount: 3,
          },
        ],
        inclusionStatus: "valid_mentioned",
        contribution: {
          mentionCount: 2,
          firstRank: 2,
          cited: true,
          sentiment: "positive",
        },
      },
      {
        taskId: "unmentioned",
        answerSampleId: "sample-unmentioned",
        brandFactId: "fact-unmentioned",
        shareOfVoiceFacts: [
          {
            brandId: "brand-primary",
            brandFactId: "fact-unmentioned",
            brandRuleVersionId: "rule-primary",
            mentionCount: 0,
          },
          {
            brandId: "brand-competitor",
            brandFactId: "fact-competitor-unmentioned",
            brandRuleVersionId: "rule-competitor",
            mentionCount: 1,
          },
        ],
        inclusionStatus: "valid_unmentioned",
        contribution: {
          mentionCount: 0,
          firstRank: null,
          cited: false,
          sentiment: null,
        },
      },
      {
        taskId: "invalid",
        answerSampleId: "sample-invalid",
        brandFactId: null,
        inclusionStatus: "invalid",
        contribution: zeroContribution,
      },
      {
        taskId: "failed",
        answerSampleId: null,
        brandFactId: null,
        inclusionStatus: "failed",
        contribution: zeroContribution,
      },
      {
        taskId: "missing",
        answerSampleId: null,
        brandFactId: null,
        inclusionStatus: "missing",
        contribution: zeroContribution,
      },
    ]);

    expect(inputs).toMatchObject({
      plannedTaskCount: 5,
      completedTaskCount: 3,
      failedTaskCount: 1,
      missingTaskCount: 1,
      validAnswerCount: 2,
      mentionedAnswerCount: 1,
      rankSum: 2,
      totalMentionCount: 2,
      allMonitoredBrandMentionCount: 6,
      citedAnswerCount: 1,
      positiveCount: 1,
    });
    expect(
      calculateBrandGeoMetrics(inputs).find((metric) => metric.code === "mention_rate")?.value,
    ).toBe(0.5);
  });

  it("hashes the exact membership lineage independent of input order", () => {
    const mentionedMembership: BrandMetricMembership = {
      taskId: "task-2",
      answerSampleId: "sample-2",
      brandFactId: "fact-2",
      shareOfVoiceFacts: [
        {
          brandId: "brand-primary",
          brandFactId: "fact-2",
          brandRuleVersionId: "rule-primary",
          mentionCount: 2,
        },
        {
          brandId: "brand-competitor",
          brandFactId: "fact-2-competitor",
          brandRuleVersionId: "rule-competitor",
          mentionCount: 3,
        },
      ],
      inclusionStatus: "valid_mentioned",
      contribution: {
        mentionCount: 2,
        firstRank: 2,
        cited: true,
        sentiment: "positive",
      },
    };
    const unmentionedMembership: BrandMetricMembership = {
      taskId: "task-1",
      answerSampleId: "sample-1",
      brandFactId: "fact-1",
      shareOfVoiceFacts: [
        {
          brandId: "brand-primary",
          brandFactId: "fact-1",
          brandRuleVersionId: "rule-primary",
          mentionCount: 0,
        },
        {
          brandId: "brand-competitor",
          brandFactId: "fact-1-competitor",
          brandRuleVersionId: "rule-competitor",
          mentionCount: 3,
        },
      ],
      inclusionStatus: "valid_unmentioned",
      contribution: {
        mentionCount: 0,
        firstRank: null,
        cited: false,
        sentiment: null,
      },
    };

    expect(hashBrandMetricMemberships([mentionedMembership, unmentionedMembership])).toBe(
      hashBrandMetricMemberships([unmentionedMembership, mentionedMembership]),
    );
    expect(
      hashBrandMetricMemberships([
        unmentionedMembership,
        { ...mentionedMembership, answerSampleId: "different-sample" },
      ]),
    ).not.toBe(hashBrandMetricMemberships([mentionedMembership, unmentionedMembership]));
    expect(
      hashBrandMetricMemberships([
        unmentionedMembership,
        { ...mentionedMembership, appliedCorrectionIds: ["correction-2", "correction-1"] },
      ]),
    ).toBe(
      hashBrandMetricMemberships([
        unmentionedMembership,
        { ...mentionedMembership, appliedCorrectionIds: ["correction-1", "correction-2"] },
      ]),
    );
    expect(
      hashBrandMetricMemberships([
        unmentionedMembership,
        { ...mentionedMembership, appliedCorrectionIds: ["correction-1"] },
      ]),
    ).not.toBe(hashBrandMetricMemberships([mentionedMembership, unmentionedMembership]));
    expect(
      hashBrandMetricMemberships([
        unmentionedMembership,
        {
          ...mentionedMembership,
          shareOfVoiceFacts: mentionedMembership.shareOfVoiceFacts?.map((fact) =>
            fact.brandId === "brand-competitor" ? { ...fact, mentionCount: 4 } : fact,
          ),
        },
      ]),
    ).not.toBe(hashBrandMetricMemberships([mentionedMembership, unmentionedMembership]));
  });

  it("rejects duplicate or internally inconsistent metric memberships", () => {
    const membership: BrandMetricMembership = {
      taskId: "task-1",
      answerSampleId: "sample-1",
      brandFactId: "fact-1",
      shareOfVoiceFacts: [
        {
          brandId: "brand-primary",
          brandFactId: "fact-1",
          brandRuleVersionId: "rule-primary",
          mentionCount: 1,
        },
      ],
      inclusionStatus: "valid_mentioned",
      contribution: {
        mentionCount: 1,
        firstRank: 1,
        cited: false,
        sentiment: "neutral",
      },
    };

    expect(() => deriveBrandGeoMetricInputs([membership, membership])).toThrow(
      "Metric membership contains duplicate task",
    );
    expect(() =>
      deriveBrandGeoMetricInputs([{ ...membership, inclusionStatus: "valid_unmentioned" }]),
    ).toThrow("Unmentioned memberships require");
    expect(() =>
      deriveBrandGeoMetricInputs([{ ...membership, appliedCorrectionIds: ["same", "same"] }]),
    ).toThrow("Applied correction IDs must be non-empty and unique");
  });
});

describe("analysis subject boundary", () => {
  it("accepts brands in V1 and rejects reserved future commerce subjects", () => {
    expect(() => assertSupportedAnalysisSubjectType("brand")).not.toThrow();
    expect(() => assertSupportedAnalysisSubjectType("product")).toThrow(
      "Unsupported analysis subject type: product",
    );
  });
});
