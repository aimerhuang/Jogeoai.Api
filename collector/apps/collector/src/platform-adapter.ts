import { createHash } from "node:crypto";
import type { CollectorConfig } from "@aivc/config";
import { resultSubmissionSchema, type LeasedTask, type ResultSubmission } from "@aivc/contracts";
import { isWebQuestionEcho } from "./web-question.ts";

export type FailureCode =
  | "login_required"
  | "region_restricted"
  | "challenge_required"
  | "rate_limited"
  | "page_contract_changed"
  | "page_contract_unverified"
  | "unsupported_platform"
  | "collection_timeout"
  | "browser_failed"
  | "stale_lease"
  | "control_plane_unavailable"
  | "invalid_response"
  | "task_not_approved"
  | "profile_unavailable"
  // The control plane reports a status only a human can clear (needs_verification, expired,
  // disabled). Local only: never reported back as a failure.
  | "session_profile_stopped"
  | "stopped"
  | "submission_spooled";
export class CollectorError extends Error {
  constructor(readonly code: FailureCode) {
    super(code);
    this.name = "CollectorError";
  }
}
export function safeFailure(error: unknown): FailureCode {
  return error instanceof CollectorError ? error.code : "browser_failed";
}
export type PlatformProbeResult = { status: "ready" | FailureCode };
export interface ProbeOptions {
  // Resident profile-mode collectors only (design §12.3): an already loaded entry page whose
  // reviewed markers are all visible is checked in place instead of being reloaded. Without it
  // every probe navigates to the entry page (single-account and operator `probe` behaviour).
  inPlace?: boolean;
}
export interface CollectionResult {
  answerText: string;
  citations: string[];
  // Optional, position-aligned with `citations`; null when a source title is unreadable.
  citationTitles?: (string | null)[];
  // Optional search keywords; omitted when the platform does not expose them reliably.
  searchQueries?: string[];
  completedAt: string;
}
export type CollectionPhase = "pre_send" | "post_send";
export interface ChallengeWaitRequest {
  phase: CollectionPhase;
  pollMs: number;
  signal: AbortSignal;
  // Rechecks the visible page state. Visibility only: never interacts with a challenge.
  check: () => Promise<FailureCode | undefined>;
}
export interface ChallengeWaiter {
  // Waits while `check` reports `challenge_required`. Resolves with the blocker visible after
  // the challenge cleared (undefined when the page is clear) and throws `challenge_required`
  // once the wait budget is spent.
  wait(request: ChallengeWaitRequest): Promise<FailureCode | undefined>;
}
// Optional per-task hooks supplied by the runner. Adapters that ignore them keep the previous
// behaviour: a visible challenge fails the task immediately.
export interface CollectContext {
  challengeWaiter?: ChallengeWaiter;
  // Called immediately before the question is submitted; later failures are post-send.
  onSubmitted?: () => void;
}
export interface PlatformAdapter {
  readonly platformCode: LeasedTask["platformCode"];
  probe(signal: AbortSignal, options?: ProbeOptions): Promise<PlatformProbeResult>;
  collect(
    task: LeasedTask,
    signal: AbortSignal,
    context?: CollectContext,
  ): Promise<CollectionResult>;
}
export class AdapterRegistry {
  private readonly adapters = new Map<LeasedTask["platformCode"], PlatformAdapter>();
  constructor(adapters: PlatformAdapter[]) {
    for (const adapter of adapters) this.adapters.set(adapter.platformCode, adapter);
  }
  get(platform: LeasedTask["platformCode"]): PlatformAdapter {
    const adapter = this.adapters.get(platform);
    if (!adapter) throw new CollectorError("unsupported_platform");
    return adapter;
  }
}
export function mockAdapter(platformCode: LeasedTask["platformCode"]): PlatformAdapter {
  return {
    platformCode,
    async probe() {
      return { status: "ready" };
    },
    async collect(task, signal) {
      signal.throwIfAborted();
      return {
        answerText: `合成采集回答：${task.questionText}\n平台：${task.platformCode}`,
        citations: [`https://example.invalid/${task.platformCode}/${task.id}`],
        completedAt: new Date().toISOString(),
      };
    },
  };
}
// Jogeoai (.NET) trims keywords with char.IsWhiteSpace (which also covers U+0085, unlike JS trim)
// and rejects blank ones; System.Text.Json rejects lone UTF-16 surrogates; and the 64 KB
// search_queries check is estimated at up to 6 wire bytes per UTF-16 unit (non-BMP characters
// are escaped as \uXXXX\uXXXX). A rejected submission is lost after the question was already sent
// and wedges the spool, so metadata that could be rejected is dropped here, never submitted.
const MAX_SEARCH_QUERY_UNITS = 10_000; // 6 × 10 000 + 4 × 32 < 65 536
const blankForControlPlane = (value: string) => value.replace(/[\s\u0085]/gu, "") === "";
export function createSubmission(
  task: LeasedTask,
  result: CollectionResult,
  config: CollectorConfig,
): ResultSubmission {
  if (config.COLLECTOR_MODE === "real" && isWebQuestionEcho(result.answerText, task.questionText)) {
    throw new CollectorError("page_contract_changed");
  }
  // Titles and keywords are best-effort metadata: misaligned or out-of-range values are dropped
  // instead of failing an otherwise valid answer.
  const titles =
    result.citationTitles?.length === result.citations.length ? result.citationTitles : undefined;
  const citations: string[] = [];
  const citationTitles: (string | null)[] = [];
  result.citations.forEach((url, index) => {
    const raw = titles?.[index];
    const title =
      typeof raw === "string" && raw.length >= 1 && raw.length <= 512 && raw.isWellFormed()
        ? raw
        : null;
    const existing = citations.indexOf(url);
    if (existing === -1) {
      citations.push(url);
      citationTitles.push(title);
    } else if (citationTitles[existing] === null) {
      // Keep the first readable title of a duplicated URL.
      citationTitles[existing] = title;
    }
  });
  const queries = result.searchQueries;
  const searchQueries =
    queries &&
    queries.length <= 32 &&
    queries.every(
      (query) => query.length <= 500 && query.isWellFormed() && !blankForControlPlane(query),
    ) &&
    queries.reduce((units, query) => units + query.length, 0) <= MAX_SEARCH_QUERY_UNITS
      ? queries
      : undefined;
  // Idempotency stays answer + canonical URL set; titles/keywords never change it.
  const idempotencyKey = createHash("sha256")
    .update(JSON.stringify([task.id, task.leaseVersion, result.answerText, [...citations].sort()]))
    .digest("hex");
  return resultSubmissionSchema.parse({
    tenantId: task.tenantId,
    taskId: task.id,
    nodeId: config.COLLECTOR_NODE_ID,
    region: config.COLLECTOR_REGION,
    leaseVersion: task.leaseVersion,
    idempotencyKey,
    egressCountry: config.COLLECTOR_EGRESS_COUNTRY,
    collectorBuildRevision: config.COLLECTOR_BUILD_REVISION,
    answerText: result.answerText,
    citations,
    ...(titles ? { citationTitles } : {}),
    ...(searchQueries ? { searchQueries } : {}),
    completedAt: result.completedAt,
  });
}
