import { createHash } from "node:crypto";

export const MAX_ACTIVE_QUESTIONS = 30;
export const TASK_BUSINESS_KEY_VERSION = "v1";
export const ANALYSIS_SUBJECT_TYPES = ["brand"] as const;
export const BRAND_GEO_METRIC_CODES = [
  "valid_sample_count",
  "mention_rate",
  "average_first_rank",
  "first_place_rate",
  "top3_rate",
  "share_of_voice",
  "citation_rate",
  "sentiment_positive_rate",
  "sentiment_neutral_rate",
  "sentiment_negative_rate",
] as const;

export type Region = "cn" | "overseas";
export type TaskRunKind = "scheduled" | "backfill" | "manual";
export type SubmissionDisposition = "accepted" | "duplicate" | "stale";
export type AnalysisSubjectType = (typeof ANALYSIS_SUBJECT_TYPES)[number];
export type BrandGeoMetricCode = (typeof BRAND_GEO_METRIC_CODES)[number];

export class DomainError extends Error {
  public constructor(
    public readonly code: string,
    message: string,
    public readonly statusCode = 400,
  ) {
    super(message);
    this.name = "DomainError";
  }
}

export function assertSupportedRegion(region: string): asserts region is Region {
  if (region !== "cn" && region !== "overseas") {
    throw new DomainError("INVALID_REGION", `Unsupported collector region: ${region}`);
  }
}

export function assertSupportedAnalysisSubjectType(
  subjectType: string,
): asserts subjectType is AnalysisSubjectType {
  if (!ANALYSIS_SUBJECT_TYPES.some((candidate) => candidate === subjectType)) {
    throw new DomainError(
      "UNSUPPORTED_ANALYSIS_SUBJECT_TYPE",
      `Unsupported analysis subject type: ${subjectType}`,
    );
  }
}

export interface TaskBusinessKeyInput {
  tenantId: string;
  brandProjectId: string;
  questionVersionId: string;
  platformEntryCode: string;
  region: Region;
  language: string;
  channel: string;
  scheduledDate: string;
  sampleIndex: number;
  runKind: TaskRunKind;
}

export function buildTaskBusinessKey(input: TaskBusinessKeyInput): string {
  return [
    TASK_BUSINESS_KEY_VERSION,
    input.tenantId,
    input.brandProjectId,
    input.questionVersionId,
    input.platformEntryCode,
    input.region,
    input.language,
    input.channel,
    input.scheduledDate,
    input.sampleIndex.toString(),
    input.runKind,
  ].join(":");
}

export function hashResultPayload(answerText: string, citations: readonly string[]): string {
  return createHash("sha256")
    .update(JSON.stringify({ answerText, citations: [...citations].sort() }))
    .digest("hex");
}

export interface BrandGeoMetricInputs {
  plannedTaskCount: number;
  completedTaskCount: number;
  failedTaskCount: number;
  missingTaskCount: number;
  validAnswerCount: number;
  mentionedAnswerCount: number;
  rankSum: number;
  rankCount: number;
  firstRankCount: number;
  top3Count: number;
  totalMentionCount: number;
  allMonitoredBrandMentionCount: number;
  citedAnswerCount: number;
  positiveCount: number;
  neutralCount: number;
  negativeCount: number;
}

export interface BrandGeoMetricObservation {
  code: BrandGeoMetricCode;
  numerator: number;
  denominator: number | null;
  value: number | null;
  participatingSampleCount: number;
}

export type BrandMetricMembershipStatus =
  | "valid_mentioned"
  | "valid_unmentioned"
  | "invalid"
  | "failed"
  | "missing";

export type BrandSentiment = "positive" | "neutral" | "negative";

export interface BrandMetricContribution {
  mentionCount: number;
  firstRank: number | null;
  cited: boolean;
  sentiment: BrandSentiment | null;
}

export interface BrandShareOfVoiceFact {
  brandId: string;
  brandFactId: string;
  brandRuleVersionId: string;
  mentionCount: number;
  appliedMentionCountCorrectionId?: string;
}

export interface BrandMetricMembership {
  taskId: string;
  answerSampleId: string | null;
  brandFactId: string | null;
  appliedCorrectionIds?: readonly string[];
  shareOfVoiceFacts?: readonly BrandShareOfVoiceFact[];
  inclusionStatus: BrandMetricMembershipStatus;
  contribution: BrandMetricContribution;
}

const BRAND_GEO_INPUT_FIELDS = [
  "plannedTaskCount",
  "completedTaskCount",
  "failedTaskCount",
  "missingTaskCount",
  "validAnswerCount",
  "mentionedAnswerCount",
  "rankSum",
  "rankCount",
  "firstRankCount",
  "top3Count",
  "totalMentionCount",
  "allMonitoredBrandMentionCount",
  "citedAnswerCount",
  "positiveCount",
  "neutralCount",
  "negativeCount",
] as const satisfies readonly (keyof BrandGeoMetricInputs)[];

export function aggregateBrandGeoMetricInputs(
  inputs: readonly BrandGeoMetricInputs[],
): BrandGeoMetricInputs {
  const aggregate = Object.fromEntries(
    BRAND_GEO_INPUT_FIELDS.map((field) => [field, 0]),
  ) as unknown as BrandGeoMetricInputs;

  for (const input of inputs) {
    assertValidBrandGeoMetricInputs(input);
    for (const field of BRAND_GEO_INPUT_FIELDS) {
      aggregate[field] += input[field];
    }
  }

  assertValidBrandGeoMetricInputs(aggregate);
  return aggregate;
}

export function calculateBrandGeoMetrics(
  input: BrandGeoMetricInputs,
): readonly BrandGeoMetricObservation[] {
  assertValidBrandGeoMetricInputs(input);

  return [
    countObservation(
      "valid_sample_count",
      input.validAnswerCount,
      input.plannedTaskCount,
      input.validAnswerCount,
    ),
    ratioObservation(
      "mention_rate",
      input.mentionedAnswerCount,
      input.validAnswerCount,
      input.validAnswerCount,
    ),
    ratioObservation("average_first_rank", input.rankSum, input.rankCount, input.rankCount),
    ratioObservation(
      "first_place_rate",
      input.firstRankCount,
      input.validAnswerCount,
      input.validAnswerCount,
    ),
    ratioObservation("top3_rate", input.top3Count, input.validAnswerCount, input.validAnswerCount),
    ratioObservation(
      "share_of_voice",
      input.totalMentionCount,
      input.allMonitoredBrandMentionCount,
      input.validAnswerCount,
    ),
    ratioObservation(
      "citation_rate",
      input.citedAnswerCount,
      input.validAnswerCount,
      input.validAnswerCount,
    ),
    ratioObservation(
      "sentiment_positive_rate",
      input.positiveCount,
      input.mentionedAnswerCount,
      input.mentionedAnswerCount,
    ),
    ratioObservation(
      "sentiment_neutral_rate",
      input.neutralCount,
      input.mentionedAnswerCount,
      input.mentionedAnswerCount,
    ),
    ratioObservation(
      "sentiment_negative_rate",
      input.negativeCount,
      input.mentionedAnswerCount,
      input.mentionedAnswerCount,
    ),
  ];
}

export function deriveBrandGeoMetricInputs(
  memberships: readonly BrandMetricMembership[],
): BrandGeoMetricInputs {
  const taskIds = new Set<string>();
  const derived: BrandGeoMetricInputs = {
    plannedTaskCount: memberships.length,
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

  for (const membership of memberships) {
    assertValidBrandMetricMembership(membership);
    if (taskIds.has(membership.taskId)) {
      throw new DomainError(
        "INVALID_METRIC_MEMBERSHIP",
        `Metric membership contains duplicate task: ${membership.taskId}`,
      );
    }
    taskIds.add(membership.taskId);

    switch (membership.inclusionStatus) {
      case "valid_mentioned": {
        const contribution = membership.contribution;
        derived.completedTaskCount += 1;
        derived.validAnswerCount += 1;
        derived.mentionedAnswerCount += 1;
        derived.rankCount += 1;
        derived.rankSum += contribution.firstRank!;
        derived.firstRankCount += contribution.firstRank === 1 ? 1 : 0;
        derived.top3Count += contribution.firstRank! <= 3 ? 1 : 0;
        derived.totalMentionCount += contribution.mentionCount;
        derived.allMonitoredBrandMentionCount += sumShareOfVoiceMentions(membership);
        derived.citedAnswerCount += contribution.cited ? 1 : 0;
        derived.positiveCount += contribution.sentiment === "positive" ? 1 : 0;
        derived.neutralCount += contribution.sentiment === "neutral" ? 1 : 0;
        derived.negativeCount += contribution.sentiment === "negative" ? 1 : 0;
        break;
      }
      case "valid_unmentioned":
        derived.completedTaskCount += 1;
        derived.validAnswerCount += 1;
        derived.allMonitoredBrandMentionCount += sumShareOfVoiceMentions(membership);
        derived.citedAnswerCount += membership.contribution.cited ? 1 : 0;
        break;
      case "invalid":
        derived.completedTaskCount += 1;
        break;
      case "failed":
        derived.failedTaskCount += 1;
        break;
      case "missing":
        derived.missingTaskCount += 1;
        break;
    }
  }

  assertValidBrandGeoMetricInputs(derived);
  return derived;
}

export function hashBrandMetricMemberships(memberships: readonly BrandMetricMembership[]): string {
  deriveBrandGeoMetricInputs(memberships);
  const stableMemberships = [...memberships]
    .sort((left, right) => (left.taskId < right.taskId ? -1 : left.taskId > right.taskId ? 1 : 0))
    .map((membership) => ({
      taskId: membership.taskId,
      answerSampleId: membership.answerSampleId,
      brandFactId: membership.brandFactId,
      appliedCorrectionIds: [...(membership.appliedCorrectionIds ?? [])].sort(),
      inclusionStatus: membership.inclusionStatus,
      contribution: {
        mentionCount: membership.contribution.mentionCount,
        firstRank: membership.contribution.firstRank,
        cited: membership.contribution.cited,
        sentiment: membership.contribution.sentiment,
      },
      shareOfVoiceFacts: [...(membership.shareOfVoiceFacts ?? [])]
        .sort((left, right) =>
          left.brandFactId < right.brandFactId ? -1 : left.brandFactId > right.brandFactId ? 1 : 0,
        )
        .map((fact) => ({
          brandId: fact.brandId,
          brandFactId: fact.brandFactId,
          brandRuleVersionId: fact.brandRuleVersionId,
          mentionCount: fact.mentionCount,
          appliedMentionCountCorrectionId: fact.appliedMentionCountCorrectionId ?? null,
        })),
    }));
  return createHash("sha256").update(JSON.stringify(stableMemberships)).digest("hex");
}

function assertValidBrandMetricMembership(membership: BrandMetricMembership): void {
  if (membership.taskId.length === 0) {
    throw new DomainError("INVALID_METRIC_MEMBERSHIP", "Metric membership requires a taskId");
  }

  const contribution = membership.contribution;
  const correctionIds = membership.appliedCorrectionIds ?? [];
  const shareOfVoiceFacts = membership.shareOfVoiceFacts ?? [];
  if (
    correctionIds.some((correctionId) => correctionId.length === 0) ||
    new Set(correctionIds).size !== correctionIds.length
  ) {
    throw new DomainError(
      "INVALID_METRIC_MEMBERSHIP",
      "Applied correction IDs must be non-empty and unique",
    );
  }
  for (const [field, value] of [["mentionCount", contribution.mentionCount]] as const) {
    if (!Number.isSafeInteger(value) || value < 0) {
      throw new DomainError(
        "INVALID_METRIC_MEMBERSHIP",
        `${field} must be a non-negative safe integer`,
      );
    }
  }

  const shareBrandIds = new Set<string>();
  const shareFactIds = new Set<string>();
  for (const fact of shareOfVoiceFacts) {
    if (
      fact.brandId.length === 0 ||
      fact.brandFactId.length === 0 ||
      fact.brandRuleVersionId.length === 0
    ) {
      throw new DomainError(
        "INVALID_METRIC_MEMBERSHIP",
        "Share-of-voice facts require brand, fact, and rule version IDs",
      );
    }
    if (!Number.isSafeInteger(fact.mentionCount) || fact.mentionCount < 0) {
      throw new DomainError(
        "INVALID_METRIC_MEMBERSHIP",
        "Share-of-voice fact mentionCount must be a non-negative safe integer",
      );
    }
    if (fact.appliedMentionCountCorrectionId === "") {
      throw new DomainError(
        "INVALID_METRIC_MEMBERSHIP",
        "Share-of-voice correction IDs must be non-empty",
      );
    }
    if (shareBrandIds.has(fact.brandId) || shareFactIds.has(fact.brandFactId)) {
      throw new DomainError(
        "INVALID_METRIC_MEMBERSHIP",
        "Share-of-voice facts must contain unique brands and facts",
      );
    }
    shareBrandIds.add(fact.brandId);
    shareFactIds.add(fact.brandFactId);
  }

  const isZeroContribution =
    contribution.mentionCount === 0 &&
    contribution.firstRank === null &&
    contribution.cited === false &&
    contribution.sentiment === null;

  switch (membership.inclusionStatus) {
    case "valid_mentioned":
      if (
        membership.answerSampleId === null ||
        membership.brandFactId === null ||
        contribution.mentionCount === 0 ||
        shareOfVoiceFacts.length === 0 ||
        contribution.firstRank === null ||
        !Number.isSafeInteger(contribution.firstRank) ||
        contribution.firstRank <= 0 ||
        contribution.sentiment === null
      ) {
        throw new DomainError(
          "INVALID_METRIC_MEMBERSHIP",
          "Mentioned memberships require a sample, fact, rank, sentiment, and valid mention counts",
        );
      }
      assertTargetShareOfVoiceFact(membership, shareOfVoiceFacts);
      break;
    case "valid_unmentioned":
      if (
        membership.answerSampleId === null ||
        membership.brandFactId === null ||
        shareOfVoiceFacts.length === 0 ||
        contribution.mentionCount !== 0 ||
        contribution.firstRank !== null ||
        contribution.sentiment !== null
      ) {
        throw new DomainError(
          "INVALID_METRIC_MEMBERSHIP",
          "Unmentioned memberships require a sample and fact without rank or sentiment",
        );
      }
      assertTargetShareOfVoiceFact(membership, shareOfVoiceFacts);
      break;
    case "invalid":
      if (
        membership.answerSampleId === null ||
        membership.brandFactId !== null ||
        correctionIds.length > 0 ||
        shareOfVoiceFacts.length > 0 ||
        !isZeroContribution
      ) {
        throw new DomainError(
          "INVALID_METRIC_MEMBERSHIP",
          "Invalid memberships require only an answer sample and zero contribution",
        );
      }
      break;
    case "failed":
    case "missing":
      if (
        membership.answerSampleId !== null ||
        membership.brandFactId !== null ||
        correctionIds.length > 0 ||
        shareOfVoiceFacts.length > 0 ||
        !isZeroContribution
      ) {
        throw new DomainError(
          "INVALID_METRIC_MEMBERSHIP",
          "Failed and missing memberships cannot reference samples or metric contributions",
        );
      }
      break;
  }
}

function assertTargetShareOfVoiceFact(
  membership: BrandMetricMembership,
  facts: readonly BrandShareOfVoiceFact[],
): void {
  const targetFact = facts.find((fact) => fact.brandFactId === membership.brandFactId);
  if (!targetFact || targetFact.mentionCount !== membership.contribution.mentionCount) {
    throw new DomainError(
      "INVALID_METRIC_MEMBERSHIP",
      "Share-of-voice facts must include the target brand fact with the same mention count",
    );
  }
}

function sumShareOfVoiceMentions(membership: BrandMetricMembership): number {
  return (membership.shareOfVoiceFacts ?? []).reduce((sum, fact) => sum + fact.mentionCount, 0);
}

function assertValidBrandGeoMetricInputs(input: BrandGeoMetricInputs): void {
  for (const field of BRAND_GEO_INPUT_FIELDS) {
    const value = input[field];
    if (!Number.isSafeInteger(value) || value < 0) {
      throw new DomainError("INVALID_METRIC_INPUT", `${field} must be a non-negative safe integer`);
    }
  }

  if (
    input.plannedTaskCount !==
    input.completedTaskCount + input.failedTaskCount + input.missingTaskCount
  ) {
    throw new DomainError(
      "INVALID_METRIC_INPUT",
      "plannedTaskCount must equal completed, failed, and missing task counts",
    );
  }
  if (input.validAnswerCount > input.completedTaskCount) {
    throw new DomainError(
      "INVALID_METRIC_INPUT",
      "validAnswerCount cannot exceed completedTaskCount",
    );
  }
  if (input.mentionedAnswerCount > input.validAnswerCount) {
    throw new DomainError(
      "INVALID_METRIC_INPUT",
      "mentionedAnswerCount cannot exceed validAnswerCount",
    );
  }
  if (input.rankCount !== input.mentionedAnswerCount) {
    throw new DomainError("INVALID_METRIC_INPUT", "rankCount must equal mentionedAnswerCount");
  }
  if (input.firstRankCount > input.top3Count || input.top3Count > input.rankCount) {
    throw new DomainError(
      "INVALID_METRIC_INPUT",
      "First-place and Top-3 counts must be nested within mentioned samples",
    );
  }
  if (input.rankSum < input.rankCount) {
    throw new DomainError("INVALID_METRIC_INPUT", "rankSum cannot be lower than rankCount");
  }
  if (input.totalMentionCount > input.allMonitoredBrandMentionCount) {
    throw new DomainError(
      "INVALID_METRIC_INPUT",
      "totalMentionCount cannot exceed allMonitoredBrandMentionCount",
    );
  }
  if (input.citedAnswerCount > input.validAnswerCount) {
    throw new DomainError(
      "INVALID_METRIC_INPUT",
      "citedAnswerCount cannot exceed validAnswerCount",
    );
  }

  const sentimentLabeledCount = input.positiveCount + input.neutralCount + input.negativeCount;
  if (sentimentLabeledCount !== input.mentionedAnswerCount) {
    throw new DomainError(
      "INVALID_METRIC_INPUT",
      "Positive, neutral, and negative counts must equal mentionedAnswerCount",
    );
  }
}

function countObservation(
  code: BrandGeoMetricCode,
  numerator: number,
  denominator: number,
  participatingSampleCount: number,
): BrandGeoMetricObservation {
  return {
    code,
    numerator,
    denominator,
    value: numerator,
    participatingSampleCount,
  };
}

function ratioObservation(
  code: BrandGeoMetricCode,
  numerator: number,
  denominator: number,
  participatingSampleCount: number,
): BrandGeoMetricObservation {
  return {
    code,
    numerator,
    denominator,
    value: denominator === 0 ? null : numerator / denominator,
    participatingSampleCount,
  };
}
