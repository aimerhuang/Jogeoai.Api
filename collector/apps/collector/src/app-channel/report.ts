import type { AppPlatformCode, DeviceDeclaration, ResolvedBinding } from "./bindings.ts";
import {
  citationsStatusSchema,
  linkIssueSchema,
  sha256Text,
  type AppRecord,
  type BatchFile,
  type CitationsStatus,
  type EvidenceStore,
  type LinkIssue,
  type PlatformStatus,
} from "./evidence.ts";
import type { AppFailureCode } from "./failure.ts";

// Batch summary and per-platform conclusion (PRD FR-A6, AC-A12). Everything is derived from
// batch.json, the records and the bindings file; it holds counts, codes and versions only, no
// question or answer text.

type FailureCounts = Partial<Record<AppFailureCode, number>>;
interface Counts {
  // Records, one per attempt that got as far as an answer ID.
  attempts: number;
  valid: number;
  failed: FailureCounts;
  evidenceIncomplete: number;
}
export interface PlatformReport extends Counts {
  platformCode: AppPlatformCode;
  package: string;
  // Cells of the batch plan, and those of them without a valid sample.
  planned: number;
  notCollected: number;
  // Valid samples by how their sources were read. Samples written before the status existed
  // are in none of the four, so the sum can be lower than `valid`.
  citations: Record<CitationsStatus, number>;
  // Sources of valid samples whose contract had every source opened to read its link: how many,
  // how many with a link, how many of those kept as copied because it could not be restored, and
  // the others by cause. Sources read without opening them are in none of these.
  links: LinkCounts;
  // usable: at least one valid sample in this batch. unavailable: the App entry was judged
  // unavailable or every binding is disabled. not_verified: neither yet.
  conclusion: "usable" | "unavailable" | "not_verified";
  appVersions: string[];
  // System image facts of the devices this platform is bound on.
  devices: DeviceDeclaration[];
  // Failure classes seen, the unavailability class, and why bindings are disabled.
  reasons: string[];
}
export interface LinkCounts {
  sources: number;
  withUrl: number;
  notRestored: number;
  issues: Record<LinkIssue, number>;
}
export interface BindingReport extends Counts {
  device: string;
  platform: AppPlatformCode;
  accountLabel: string;
  enabled: boolean;
  disabledReason?: string;
}
export interface BatchReport {
  version: 1;
  batchId: string;
  generatedAt: string;
  totals: Counts & { planned: number; notCollected: number };
  platforms: PlatformReport[];
  bindings: BindingReport[];
}

function count(records: readonly AppRecord[]): Counts {
  const failed: FailureCounts = {};
  for (const record of records)
    if (record.status === "failed" && record.failureCode)
      failed[record.failureCode] = (failed[record.failureCode] ?? 0) + 1;
  return {
    attempts: records.length,
    valid: records.filter((record) => record.status === "valid").length,
    failed,
    evidenceIncomplete: records.filter((record) => record.status === "evidence_incomplete").length,
  };
}
const unique = <T>(values: readonly T[]) => [...new Set(values)];
function countCitations(records: readonly AppRecord[]): Record<CitationsStatus, number> {
  const valid = records.filter((record) => record.status === "valid");
  return Object.fromEntries(
    citationsStatusSchema.options.map((status) => [
      status,
      valid.filter((record) => record.citationsStatus === status).length,
    ]),
  ) as Record<CitationsStatus, number>;
}

function countLinks(records: readonly AppRecord[]): LinkCounts {
  const sources = records
    .filter((record) => record.status === "valid")
    .flatMap((record) => record.citations)
    .flatMap((source) => (source.link ? [source.link] : []));
  const copied = sources.flatMap((link) => ("copied" in link ? [link] : []));
  return {
    sources: sources.length,
    withUrl: copied.length,
    notRestored: copied.filter((link) => !link.restored).length,
    issues: Object.fromEntries(
      linkIssueSchema.options.map((issue) => [
        issue,
        sources.filter((link) => "issue" in link && link.issue === issue).length,
      ]),
    ) as Record<LinkIssue, number>,
  };
}

export interface ReportInput {
  batchId: string;
  // undefined: the batch was never run; nothing is planned.
  batch: BatchFile | undefined;
  records: readonly AppRecord[];
  // Every binding of the bindings file, disabled ones included.
  bindings: readonly ResolvedBinding[];
  platformStatus: PlatformStatus;
  now: Date;
}
export function buildReport(input: ReportInput): BatchReport {
  const { records, bindings } = input;
  const cells = input.batch?.cells ?? [];
  const validCells = new Set(
    records
      .filter((record) => record.status === "valid")
      .map((record) => `${record.platformCode}/${sha256Text(record.questionText)}`),
  );
  const collected = (platformCode?: AppPlatformCode) =>
    cells.filter(
      (cell) =>
        (platformCode === undefined || cell.platformCode === platformCode) &&
        validCells.has(`${cell.platformCode}/${cell.questionSha256}`),
    ).length;
  const plannedFor = (platformCode: AppPlatformCode) =>
    cells.filter((cell) => cell.platformCode === platformCode).length;

  const platforms = unique(bindings.map((binding) => binding.platformCode)).map(
    (platformCode): PlatformReport => {
      const own = bindings.filter((binding) => binding.platformCode === platformCode);
      const ownRecords = records.filter((record) => record.platformCode === platformCode);
      const counts = count(ownRecords);
      const enabled = own.filter((binding) => binding.enabled);
      const unavailable = input.platformStatus.platforms[platformCode];
      const validVersions = ownRecords
        .filter((record) => record.status === "valid")
        .map((record) => record.appVersion);
      const planned = plannedFor(platformCode);
      return {
        platformCode,
        package: own[0]!.package,
        planned,
        ...counts,
        notCollected: planned - collected(platformCode),
        citations: countCitations(ownRecords),
        links: countLinks(ownRecords),
        conclusion:
          counts.valid > 0
            ? "usable"
            : unavailable !== undefined || enabled.length === 0
              ? "unavailable"
              : "not_verified",
        appVersions: unique(
          validVersions.length > 0
            ? validVersions
            : ownRecords.length > 0
              ? ownRecords.map((record) => record.appVersion)
              : own.map((binding) => binding.reviewedAppVersion),
        ),
        devices: (enabled.length > 0 ? enabled : own).map((binding) => binding.device),
        reasons: unique([
          ...(unavailable ? [unavailable.failureCode] : []),
          ...Object.keys(counts.failed),
          ...(counts.evidenceIncomplete > 0 ? ["evidence_incomplete"] : []),
          ...own.flatMap((binding) => (binding.disabledReason ? [binding.disabledReason] : [])),
          ...(counts.attempts === 0 && enabled.length > 0 && !unavailable ? ["no_attempt"] : []),
        ]),
      };
    },
  );

  return {
    version: 1,
    batchId: input.batchId,
    generatedAt: input.now.toISOString(),
    totals: {
      planned: cells.length,
      ...count(records),
      notCollected: cells.length - collected(),
    },
    platforms,
    bindings: bindings.map((binding) => ({
      device: binding.device.id,
      platform: binding.platformCode,
      accountLabel: binding.accountLabel,
      enabled: binding.enabled,
      ...(binding.disabledReason === undefined ? {} : { disabledReason: binding.disabledReason }),
      ...count(
        records.filter(
          (record) =>
            record.deviceId === binding.device.id && record.platformCode === binding.platformCode,
        ),
      ),
    })),
  };
}

// Builds the report of a batch from the evidence directory and writes summary.json.
export async function writeBatchReport(
  store: EvidenceStore,
  batchId: string,
  bindings: readonly ResolvedBinding[],
  now: Date,
): Promise<BatchReport> {
  const report = buildReport({
    batchId,
    batch: await store.readBatch(batchId),
    records: await store.readRecords(batchId),
    bindings,
    platformStatus: await store.readPlatformStatus(),
    now,
  });
  await store.writeSummary(batchId, report);
  return report;
}

export interface PruneResult {
  batches: number;
  records: number;
  pruned: number;
  bytes: number;
}
// Retention clean-up (PRD FR-A8): videos older than `days` are deleted and the time is written
// into their record; the record, screenshot and hierarchy stay.
export async function pruneVideos(
  store: EvidenceStore,
  options: { days: number; now: Date },
): Promise<PruneResult> {
  const cutoff = options.now.getTime() - options.days * 86_400_000;
  const result: PruneResult = { batches: 0, records: 0, pruned: 0, bytes: 0 };
  for (const batchId of await store.listBatchIds()) {
    result.batches += 1;
    for (const record of await store.readRecords(batchId)) {
      result.records += 1;
      const video = record.video;
      if (!video || video.prunedAt !== undefined || Date.parse(video.stoppedAt) > cutoff) continue;
      // File first: an interruption leaves a record without `prunedAt`, and the next run repeats
      // the (then harmless) removal.
      await store.removeVideo(batchId, record.answerId);
      await store.writeRecord(
        { ...record, video: { ...video, prunedAt: options.now.toISOString() } },
        { replace: true },
      );
      result.pruned += 1;
      result.bytes += video.bytes;
    }
  }
  return result;
}
