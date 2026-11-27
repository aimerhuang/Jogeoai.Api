import { createHash, randomBytes } from "node:crypto";
import { constants } from "node:fs";
import { lstat, mkdir, open, readdir, rename, unlink } from "node:fs/promises";
import { basename, dirname, join, resolve } from "node:path";
import { appControlPlaneFailureCodeSchema, platformCodeSchema } from "@aivc/contracts";
import { z } from "zod";
import { lockProfile } from "../session-profile.ts";
import {
  AppSetupError,
  appCollectionPhaseSchema,
  appFailureCodeSchema,
  type AppFailureCode,
} from "./failure.ts";
import { shanghaiDay, timestampPairSchema } from "./time.ts";

// Local evidence directory (design §5). It lives outside every repository, is private to the
// operator (0700 directories, 0600 files, no symlinks) and is locked by one script instance.
//
//   <evidence>/ledger.jsonl                               one line per question sent
//   <evidence>/platform-status.json                       platforms judged unavailable
//   <evidence>/binding-status.json                        bindings stopped for the day
//   <evidence>/batches/<batchId>/records/<answerId>/      record.json, the screenshot, the UI
//                                                         dump, <answerId>.mp4 when recording is on
//   <evidence>/control-plane/tasks/<taskId>.json          `serve` only: what happened to a leased
//                                                         task on this node (task state below)

const sha256Schema = z.string().regex(/^[0-9a-f]{64}$/u);
const fileNameSchema = z.string().regex(/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/u);
const deviceIdSchema = z.string().regex(/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/u);
// Also a directory name: one path component, never "." or "..".
export const batchIdSchema = z.string().regex(/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/u);

export const fileEvidenceSchema = z
  .object({
    file: fileNameSchema,
    sha256: sha256Schema,
    bytes: z.number().int().nonnegative(),
  })
  .strict();
export type FileEvidence = z.infer<typeof fileEvidenceSchema>;

export const videoEvidenceSchema = z
  .object({
    file: fileNameSchema,
    sha256: sha256Schema,
    bytes: z.number().int().nonnegative(),
    // null: there is a file but it could not be read as a video.
    durationMs: z.number().int().nonnegative().nullable(),
    startedAt: z.iso.datetime(),
    stoppedAt: z.iso.datetime(),
    // Set when the retention clean-up removed the file; the record itself is kept.
    prunedAt: z.iso.datetime().optional(),
  })
  .strict();
export type VideoEvidence = z.infer<typeof videoEvidenceSchema>;

// Name of one source list of an answer that shows several (design §16.7): the `label` an
// operator gives the list's path in the contract, repeated on every source read from it.
export const sourceListLabelSchema = z.string().regex(/^[a-z][a-z0-9_]{0,19}$/u);

// Why the link of a source was not got, where the contract has every source opened to read it
// (contract `links`). Fixed words, never text from a screen.
//   page_not_opened      a tap on the title, and a second one, opened no page
//   share_missing        the page shows no share control
//   copy_missing         the share sheet shows no "copy link"
//   clipboard_unchanged  the clipboard still held the previous source's link after two copies
//   not_a_link           what the copy gave is not a web address
//   page_not_closed      the page, or the panel behind it, did not come back; no source after it
//                        was opened
//   not_attempted        not opened: the page before it did not close, or a stop was requested
//   not_reached          not opened: the panel stopped moving before its row came on screen
//   time_budget          not opened: the time allowed for the answer's links was up
//   row_not_found        its row was on screen but could not be found again to tap it
//   share_external       the share control opened a sheet of another package (Android's own)
//                        that the contract does not declare, or whose copy gave nothing
//   left_app             the source opened another App, which the back key left again
export const linkIssueSchema = z.enum([
  "page_not_opened",
  "share_missing",
  "copy_missing",
  "clipboard_unchanged",
  "not_a_link",
  "page_not_closed",
  "not_attempted",
  "not_reached",
  "time_budget",
  "row_not_found",
  "share_external",
  "left_app",
]);
export type LinkIssue = z.infer<typeof linkIssueSchema>;
// How the link of a source was read: the text the copy gave, as it is, and whether `url` is the
// source's address restored from it (links.ts) or that text itself; or why there is none.
export const citationLinkSchema = z.union([
  z.object({ copied: z.string().min(1), restored: z.boolean() }).strict(),
  z.object({ issue: linkIssueSchema }).strict(),
]);
export type CitationLink = z.infer<typeof citationLinkSchema>;

// Only what the App shows. `site` and `url` are null when the screen does not expose them;
// they are never guessed or completed.
export const citationSchema = z
  .object({
    title: z.string().min(1),
    site: z.string().min(1).nullable(),
    url: z.string().min(1).nullable(),
    // Which of the answer's source lists this source was read from. Only where the contract
    // declares several lists; a source that two lists show is then kept once per list.
    list: sourceListLabelSchema.optional(),
    // Only where the contract has every source opened to read its link. Absent otherwise, so a
    // record of a contract without `links` is what it always was.
    link: citationLinkSchema.optional(),
  })
  .strict()
  // A copied link is the source's `url`: restored from it, or the text itself.
  .refine(
    (source) =>
      source.link === undefined ||
      !("copied" in source.link) ||
      (source.url !== null && (source.link.restored || source.url === source.link.copied)),
    { path: ["url"], message: "must be the copied link or what was restored from it" },
  );
const visibleStateSchema = z.enum(["on", "off", "unknown"]);
export const searchStateSchema = z
  .object({
    webSearch: visibleStateSchema,
    deepThinking: visibleStateSchema,
    searchQueries: z.array(z.string().min(1)),
  })
  .strict();

// Why the evidence of a sent question is unusable. The first five are the recorder's
// `RecordingIssue` and occur only with recording switched on; `screenshot_failed` means the
// completion screenshot could not be taken.
export const evidenceIssueSchema = z.enum([
  "recorder_exited",
  "stop_timeout",
  "pull_failed",
  "not_playable",
  "too_short",
  "screenshot_failed",
]);
export type EvidenceIssue = z.infer<typeof evidenceIssueSchema>;

// The one completeness rule for a recording (design §9): playable, and not shorter than the span
// from sending to completion. A video's duration is never exactly the wall-clock time (the
// emulator's clock drifts: recordings ran 4–6 % long) and a static screen emits almost no frames,
// so wall-clock equality, frame count and file size are never part of it.
export function videoCoversSpan(
  durationMs: number | null,
  sentAt: string | null,
  completedAt: string | null,
): boolean {
  if (durationMs === null || durationMs <= 0) return false;
  if (sentAt === null || completedAt === null) return true;
  return durationMs >= Date.parse(completedAt) - Date.parse(sentAt);
}

// How the sources of a collected answer were read (design §16.2). Sources are best effort and
// never decide whether a sample is valid.
//   read          at least one source, and as many as the App states where the contract says so
//   absent        the answer shows no source entry, or the App states that there are none
//   unreadable    there is an entry but its list could not be read whole; the list is then empty
//   not_declared  the platform's contract has no source section
export const citationsStatusSchema = z.enum(["read", "absent", "unreadable", "not_declared"]);
export type CitationsStatus = z.infer<typeof citationsStatusSchema>;
// The fixed cause word of a source read that failed (`driver_<kind>` for a driver command),
// never text from a screen.
const citationsIssueSchema = z.string().regex(/^[a-z][a-z0-9_]{0,63}$/u);
// How one of several source lists was read: the same states, per list. `count` is the number of
// sources kept from it, 0 unless it was read.
export const citationListSchema = z
  .object({
    label: sourceListLabelSchema,
    status: citationsStatusSchema.exclude(["not_declared"]),
    issue: citationsIssueSchema.optional(),
    count: z.number().int().nonnegative(),
  })
  .strict();
export type CitationList = z.infer<typeof citationListSchema>;
// The state of all lists taken together: read when any list was read, else unreadable when any
// was (with the cause of the first such list), else absent.
export function overallCitations(
  lists: readonly Pick<CitationList, "status" | "issue">[],
): Pick<CitationList, "status" | "issue"> {
  if (lists.some((list) => list.status === "read")) return { status: "read" };
  const failed = lists.find((list) => list.status === "unreadable");
  return failed ? { status: "unreadable", issue: failed.issue } : { status: "absent" };
}

export const appRecordSchema = z
  .object({
    // Identity. The answer ID is generated before recording starts and names the record
    // directory and the video file.
    answerId: z.uuid(),
    batchId: batchIdSchema,
    // local_trial: the local script's sample, never a formal sample, metric input or export row.
    // control_plane: collected for a task leased from the control plane (`serve`); the formal
    // sample is what the control plane accepted, this record is its local evidence.
    sampleClass: z.enum(["local_trial", "control_plane"]),
    // Only on control_plane records: the leased task the attempt was made for.
    task: z
      .object({ taskId: z.guid(), leaseVersion: z.number().int().positive() })
      .strict()
      .optional(),
    // A string in local records only; the contract's channel enum is unchanged.
    channel: z.literal("consumer_app"),
    scriptRevision: z.string().min(1).max(128),
    // Question: the approved original and the text actually sent. The two are equal; records
    // written before v1.1 hold the Web channel's prefix in `sentText`.
    questionText: z.string().min(1),
    sentText: z.string().min(1),
    // Ownership.
    platformCode: platformCodeSchema,
    package: z.string().min(1).max(128),
    appVersion: z.string().min(1).max(64),
    deviceId: deviceIdSchema,
    adbSerial: z.string().min(1).max(64),
    androidVersion: z.string().min(1).max(16),
    abi: z.string().min(1).max(32),
    deviceLocale: z.string().min(1).max(64),
    deviceTimezone: z.string().min(1).max(64),
    accountLabel: z.string().min(1).max(64),
    // Times, each in UTC and Asia/Shanghai. `sentAt` is the ledger append made right before the
    // send tap, so it is null exactly when nothing can have been sent.
    startedAt: timestampPairSchema,
    sentAt: timestampPairSchema.nullable(),
    completedAt: timestampPairSchema.nullable(),
    // Result.
    status: z.enum(["valid", "failed", "evidence_incomplete"]),
    failureCode: appFailureCodeSchema.nullable(),
    // Failure phase; null for a valid sample.
    phase: appCollectionPhaseSchema.nullable(),
    // A failed record may keep partial text (collection_timeout); it never counts as valid.
    answerText: z.string().nullable(),
    citations: z.array(citationSchema),
    // Absent on a failed record and on records written before v1.1.
    citationsStatus: citationsStatusSchema.optional(),
    // Only with `unreadable`: the fixed cause word of the check that failed.
    citationsIssue: citationsIssueSchema.optional(),
    // Only where the contract declares several source lists, and like `citationsStatus` absent
    // on a failed record: one entry per list, in the order they were read. `citations` then
    // holds the lists one after the other, every source naming its list, and `citationsStatus`
    // is the state of all of them taken together.
    citationLists: z.array(citationListSchema).min(2).max(3).optional(),
    searchState: searchStateSchema.nullable(),
    // Evidence, each with its hash. `video` is null when recording is switched off.
    video: videoEvidenceSchema.nullable(),
    screenshot: fileEvidenceSchema.nullable(),
    uiDump: fileEvidenceSchema.nullable(),
    // Only where reading the links of the sources ended because the list stopped moving: the
    // screen and hierarchy at that moment (`links-stop.png`, `links-stop.xml`), for finding out
    // why its last sources were not reached (design of task 11-24, §12.2). A part that could not
    // be taken is null. Local evidence only; never part of a result submission.
    linkStop: z
      .object({
        screenshot: fileEvidenceSchema.nullable(),
        hierarchy: fileEvidenceSchema.nullable(),
      })
      .strict()
      .optional(),
    // Why the evidence is unusable: always present on `evidence_incomplete`, optional on a
    // failure whose recording also had a problem, never on a valid sample.
    evidenceIssue: evidenceIssueSchema.optional(),
  })
  .strict()
  .superRefine((record, context) => {
    const issue = (path: string, message: string) =>
      context.addIssue({ code: "custom", path: [path], message });
    if ((record.sampleClass === "control_plane") !== (record.task !== undefined))
      issue("task", "must be set exactly on control_plane records");
    if (record.video && record.video.file !== `${record.answerId}.mp4`)
      issue("video", "file name must be the answer ID");
    if ((record.citationsStatus === "unreadable") !== (record.citationsIssue !== undefined))
      issue("citationsIssue", "must be set exactly when the sources were unreadable");
    if (record.citationsStatus !== undefined) {
      if (record.status === "failed")
        issue("citationsStatus", "must be absent for a failed record");
      if ((record.citationsStatus === "read") !== record.citations.length > 0)
        issue("citations", "must hold sources exactly when they were read");
    }
    const lists = record.citationLists;
    if (lists === undefined) {
      // A failure carries no list states, as it carries no source status; what it had read of
      // several lists by then still names its list.
      if (record.status !== "failed" && record.citations.some((source) => source.list))
        issue("citations", "a source names its list only where the record holds the lists");
    } else {
      const labels = lists.map((list) => list.label);
      if (new Set(labels).size !== labels.length) issue("citationLists", "labels must be unique");
      for (const list of lists) {
        if ((list.status === "unreadable") !== (list.issue !== undefined))
          issue("citationLists", "a cause must be set exactly for an unreadable list");
        if ((list.status === "read") !== list.count > 0)
          issue("citationLists", "a list must hold sources exactly when it was read");
        if (record.citations.filter((source) => source.list === list.label).length !== list.count)
          issue("citationLists", "count must be the number of sources of that list");
      }
      // Every source names one of the lists, and the lists follow each other in their order.
      const order = record.citations.map((source) => labels.indexOf(source.list ?? ""));
      if (order.some((index, position) => index < 0 || index < (order[position - 1] ?? 0)))
        issue("citations", "must hold the sources of the lists, list after list");
      const overall = overallCitations(lists);
      if (record.citationsStatus !== overall.status || record.citationsIssue !== overall.issue)
        issue("citationsStatus", "must be the state of the lists taken together");
    }
    if (record.status === "valid") {
      if (record.failureCode !== null) issue("failureCode", "must be null for a valid sample");
      if (record.phase !== null) issue("phase", "must be null for a valid sample");
      if (!record.answerText) issue("answerText", "is required for a valid sample");
      if (record.sentAt === null) issue("sentAt", "is required for a valid sample");
      if (record.completedAt === null) issue("completedAt", "is required for a valid sample");
      if (record.screenshot === null) issue("screenshot", "is required for a valid sample");
      // A video is not required (recording is off by default); one that is there must be whole.
      if (
        record.video !== null &&
        !videoCoversSpan(
          record.video.durationMs,
          record.sentAt?.utc ?? null,
          record.completedAt?.utc ?? null,
        )
      )
        issue("video", "the video of a valid sample must be playable and cover send to completion");
      if (record.evidenceIssue !== undefined)
        issue("evidenceIssue", "must be absent for a valid sample");
      return;
    }
    if (record.phase === null) issue("phase", "is required for a failure");
    const unconfirmedTap =
      record.sampleClass === "control_plane" &&
      record.status === "failed" &&
      record.phase === "post_send" &&
      record.sentAt === null;
    if (!unconfirmedTap && (record.phase === "pre_send") !== (record.sentAt === null))
      issue("sentAt", "must be set exactly when the phase is post_send");
    if (record.status === "failed" && record.failureCode === null)
      issue("failureCode", "is required for a failed record");
    if (record.status === "evidence_incomplete") {
      // The collection itself succeeded after the question was sent; only its evidence (the
      // video, or the completion screenshot) is unusable.
      if (record.failureCode !== null) issue("failureCode", "must be null for evidence_incomplete");
      if (record.phase !== "post_send") issue("phase", "evidence_incomplete is always post_send");
      if (record.evidenceIssue === undefined)
        issue("evidenceIssue", "is required for evidence_incomplete");
    }
  });
export type AppRecord = z.infer<typeof appRecordSchema>;

// Appended and fsynced right before the send tap. The only source of rate-limit counters, so a
// restart never resets them and a question that may have been sent is always counted.
export const ledgerEntrySchema = z
  .object({
    answerId: z.uuid(),
    batchId: batchIdSchema,
    deviceId: deviceIdSchema,
    platformCode: platformCodeSchema,
    sentAt: z.iso.datetime(),
  })
  .strict();
export type LedgerEntry = z.infer<typeof ledgerEntrySchema>;

// Platforms whose App entry was judged unavailable (emulator_rejected). An entry stays until an
// operator removes it by hand; it points at the record holding the evidence.
export const platformStatusSchema = z
  .object({
    version: z.literal(1),
    platforms: z.partialRecord(
      platformCodeSchema,
      z
        .object({
          status: z.literal("unavailable"),
          failureCode: appFailureCodeSchema,
          since: z.iso.datetime(),
          batchId: batchIdSchema,
          answerId: z.uuid(),
        })
        .strict(),
    ),
  })
  .strict();
export type PlatformStatus = z.infer<typeof platformStatusSchema>;

// Bindings that showed a rate-limit notice. `day` is the Asia/Shanghai calendar day the stop
// applies to; entries of earlier days no longer apply and are dropped on the next write.
export const bindingStatusSchema = z
  .object({
    version: z.literal(1),
    bindings: z.record(
      z.string().min(1).max(160),
      z
        .object({
          reason: z.literal("rate_limited"),
          day: z.string().regex(/^\d{4}-\d{2}-\d{2}$/u),
          stoppedAt: z.iso.datetime(),
          answerId: z.uuid().optional(),
        })
        .strict(),
    ),
  })
  .strict();
export type BindingStatus = z.infer<typeof bindingStatusSchema>;

// batch.json: what a batch planned and which configuration each run of it used. A batch may be
// run several times (resume, wider scope); `cells` is the union of every run's plan. Questions
// appear as hashes only; the records hold the text.
export const batchCellSchema = z
  .object({ platformCode: platformCodeSchema, questionSha256: sha256Schema })
  .strict();
export type BatchCell = z.infer<typeof batchCellSchema>;
export const batchRunSchema = z
  .object({
    startedAt: z.iso.datetime(),
    scope: z
      .object({
        // null: not restricted.
        platforms: z.array(platformCodeSchema).nullable(),
        devices: z.array(deviceIdSchema).nullable(),
        questions: z.number().int().positive().nullable(),
        limit: z.number().int().positive().nullable(),
      })
      .strict(),
    // Hash of the approved question list as loaded, and of the bindings file as read.
    approvedQuestionsSha256: sha256Schema,
    bindingsSha256: sha256Schema,
    scriptRevision: z.string().min(1).max(128),
    // Versions of the automation tools and the helper packages the driver installs on a device.
    tools: z.record(z.string().min(1).max(64), z.string().min(1).max(64)),
    helperPackages: z.array(z.string().min(1).max(128)),
  })
  .strict();
export type BatchRun = z.infer<typeof batchRunSchema>;
export const batchFileSchema = z
  .object({
    version: z.literal(1),
    batchId: batchIdSchema,
    createdAt: z.iso.datetime(),
    cells: z.array(batchCellSchema),
    runs: z.array(batchRunSchema).min(1),
  })
  .strict();
export type BatchFile = z.infer<typeof batchFileSchema>;

// `serve` only: one state per physical attempt (task + lease version), keeping the most recent
// attempt at the original v1 path. Retrying a receipt never sends another question.
//   started    an attempt began; `answerId` is the answer ID it uses (found in the ledger once sent)
//   collected  the attempt produced an answer (valid or evidence_incomplete record)
//   submitted  the control plane accepted the answer (accepted or duplicate)
//   reported   a failure was reported for the attempt
// `pendingSubmit` marks a collected answer whose submission under `leaseVersion` has not had a
// final response yet; it is retried until it has.
export const taskStateSchema = z
  .object({
    version: z.literal(1),
    taskId: z.guid(),
    leaseVersion: z.number().int().positive(),
    bindingKey: z.string().min(1).max(160),
    bindingId: z.guid().optional(),
    nodeId: z.string().min(1).max(128).optional(),
    deviceId: z.string().min(1).max(128).optional(),
    finishedAt: z.iso.datetime().optional(),
    batchId: batchIdSchema,
    answerId: z.uuid(),
    state: z.enum(["started", "collected", "submitted", "reported"]),
    pendingSubmit: z.boolean(),
    // Send facts are persisted independently of the failure class. `unknown` means that the
    // process crossed the send-intent boundary but never confirmed the tap; it must never be
    // treated as a safe pre-send retry.
    sendState: z.enum(["not_sent", "sent", "unknown"]).optional(),
    sentAt: z.iso.datetime().optional(),
    pendingSent: z.boolean().optional(),
    // Optional for v1 files from before failures were spooled. Retain phase after delivery:
    // a preflight failure may have no record, but can safely be retried on a new lease.
    failure: z
      .object({
        bindingId: z.guid(),
        failureCode: appControlPlaneFailureCodeSchema,
        phase: z.enum(["pre_send", "post_send"]),
        pending: z.boolean(),
      })
      .strict()
      .optional(),
    updatedAt: z.iso.datetime(),
  })
  .strict()
  .refine((s) => (s.sendState === "sent") === (s.sentAt !== undefined), {
    path: ["sentAt"],
    message: "only a confirmed send has a timestamp",
  })
  .refine((s) => !s.pendingSent || (!!s.nodeId && !!s.bindingId && s.sendState === "sent"), {
    path: ["pendingSent"],
    message: "send receipts require the original source and a confirmed send",
  });
export type TaskState = z.infer<typeof taskStateSchema>;

export function sha256Text(text: string): string {
  return createHash("sha256").update(text).digest("hex");
}
export async function sha256File(path: string): Promise<string> {
  const hash = createHash("sha256");
  const file = await open(path, constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    for await (const chunk of file.createReadStream({ autoClose: false }))
      hash.update(chunk as Buffer);
  } finally {
    await file.close();
  }
  return hash.digest("hex");
}
// Name, hash and size of a file already written into a record directory.
export async function fileEvidence(path: string): Promise<FileEvidence> {
  const stats = await lstat(path);
  if (!stats.isFile()) throw new AppSetupError("evidence_unavailable");
  return { file: basename(path), sha256: await sha256File(path), bytes: stats.size };
}

const unavailable = () => new AppSetupError("evidence_unavailable");
const corrupt = () => new AppSetupError("evidence_corrupt");
const isMissing = (error: unknown) => (error as NodeJS.ErrnoException).code === "ENOENT";

async function ensurePrivateDirectory(path: string): Promise<void> {
  try {
    await mkdir(path, { mode: 0o700 });
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "EEXIST") throw unavailable();
  }
  const stats = await lstat(path);
  if (!stats.isDirectory() || (stats.mode & 0o077) !== 0 || stats.uid !== process.getuid?.())
    throw unavailable();
}

async function syncDirectory(path: string): Promise<void> {
  const directory = await open(
    path,
    constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW,
  );
  try {
    await directory.sync();
  } finally {
    await directory.close();
  }
}

// Temp file + fsync + rename: readers see the old content or the new, never a partial file.
// `rename` replaces a symlink at the target instead of writing through it.
export async function writeJsonAtomic(path: string, value: unknown): Promise<void> {
  const temporary = `${path}.${randomBytes(6).toString("hex")}.tmp`;
  const file = await open(temporary, "wx", 0o600);
  try {
    await file.writeFile(`${JSON.stringify(value, null, 2)}\n`);
    await file.sync();
  } finally {
    await file.close();
  }
  await rename(temporary, path);
}
// Text of a private regular file, or undefined when it does not exist. Never follows a symlink.
async function readPrivateFile(path: string): Promise<string | undefined> {
  let file;
  try {
    file = await open(path, constants.O_RDONLY | constants.O_NOFOLLOW);
  } catch (error) {
    if (isMissing(error)) return undefined;
    throw unavailable();
  }
  try {
    if (!(await file.stat()).isFile()) throw unavailable();
    return await file.readFile("utf8");
  } finally {
    await file.close();
  }
}
async function readJson<T>(path: string, schema: z.ZodType<T>): Promise<T | undefined> {
  const text = await readPrivateFile(path);
  if (text === undefined) return undefined;
  try {
    return schema.parse(JSON.parse(text));
  } catch {
    // Preserve and investigate; never overwrite evidence to force progress.
    throw corrupt();
  }
}

export class EvidenceStore {
  // Status updates are read-modify-write and both device loops share one store.
  private queue: Promise<unknown> = Promise.resolve();

  private constructor(
    readonly root: string,
    private readonly unlock: () => Promise<void>,
  ) {}

  // Validates the directory (absolute, an `aivc/` component, outside every Git checkout, no
  // symlinked ancestor, owner-only) and takes the single-instance lock.
  static async open(directory: string): Promise<EvidenceStore> {
    let unlock: () => Promise<void>;
    try {
      unlock = await lockProfile(directory);
    } catch {
      throw unavailable();
    }
    try {
      // lockProfile refused symlinked ancestors, so the resolved path is the real one.
      const root = resolve(directory);
      await ensurePrivateDirectory(join(root, "batches"));
      return new EvidenceStore(root, unlock);
    } catch (error) {
      await unlock();
      throw error;
    }
  }
  async close(): Promise<void> {
    await this.queue.catch(() => undefined);
    await this.unlock();
  }

  async batchDirectory(batchId: string): Promise<string> {
    const directory = join(this.root, "batches", batchIdSchema.parse(batchId));
    await ensurePrivateDirectory(directory);
    await ensurePrivateDirectory(join(directory, "records"));
    return directory;
  }
  // Directory holding record.json, <answerId>.mp4, the screenshot and the UI dump.
  async recordDirectory(batchId: string, answerId: string): Promise<string> {
    const directory = join(await this.batchDirectory(batchId), "records", z.uuid().parse(answerId));
    await ensurePrivateDirectory(directory);
    return directory;
  }

  // Validates and writes record.json in one atomic step. A record is written once; `replace` is
  // for the retention clean-up, which only adds the prune time.
  async writeRecord(input: AppRecord, options: { replace?: boolean } = {}): Promise<string> {
    const record = appRecordSchema.parse(input);
    const path = join(await this.recordDirectory(record.batchId, record.answerId), "record.json");
    if (!options.replace && (await readPrivateFile(path)) !== undefined) throw corrupt();
    await writeJsonAtomic(path, record);
    return path;
  }
  // Every record of a batch. An attempt that died before its record was written has a directory
  // without record.json and is skipped: it is not a sample. Only answer-ID directories are
  // records; anything else in the folder (a Finder `.DS_Store`, an operator's notes) is ignored,
  // like the non-batch names in `listBatchIds`.
  async readRecords(batchId: string): Promise<AppRecord[]> {
    const directory = join(this.root, "batches", batchIdSchema.parse(batchId), "records");
    let names: string[];
    try {
      names = (await readdir(directory)).filter((name) => z.uuid().safeParse(name).success).sort();
    } catch (error) {
      if (isMissing(error)) return [];
      throw unavailable();
    }
    const records: AppRecord[] = [];
    for (const name of names) {
      const record = await readJson(join(directory, name, "record.json"), appRecordSchema);
      if (record === undefined) continue;
      if (record.answerId !== name || record.batchId !== batchId) throw corrupt();
      records.push(record);
    }
    return records;
  }

  async listBatchIds(): Promise<string[]> {
    try {
      return (await readdir(join(this.root, "batches")))
        .filter((name) => batchIdSchema.safeParse(name).success)
        .sort();
    } catch {
      throw unavailable();
    }
  }
  async readBatch(batchId: string): Promise<BatchFile | undefined> {
    const batch = await readJson(
      join(this.root, "batches", batchIdSchema.parse(batchId), "batch.json"),
      batchFileSchema,
    );
    if (batch !== undefined && batch.batchId !== batchId) throw corrupt();
    return batch;
  }
  // Creates batch.json or adds this run to it: the plan grows by the run's cells and the run's
  // configuration is appended; earlier runs are never rewritten.
  async recordBatchRun(
    batchId: string,
    run: BatchRun,
    cells: readonly BatchCell[],
  ): Promise<BatchFile> {
    const directory = await this.batchDirectory(batchId);
    const existing = await this.readBatch(batchId);
    const key = (cell: BatchCell) => `${cell.platformCode}/${cell.questionSha256}`;
    const known = new Set(existing?.cells.map(key));
    const batch = batchFileSchema.parse({
      version: 1,
      batchId,
      createdAt: existing?.createdAt ?? run.startedAt,
      cells: [...(existing?.cells ?? []), ...cells.filter((cell) => !known.has(key(cell)))],
      runs: [...(existing?.runs ?? []), run],
    });
    await writeJsonAtomic(join(directory, "batch.json"), batch);
    return batch;
  }
  // summary.json is derived from the records and may be rewritten at any time.
  async writeSummary(batchId: string, summary: unknown): Promise<string> {
    const path = join(await this.batchDirectory(batchId), "summary.json");
    await writeJsonAtomic(path, summary);
    return path;
  }
  // Retention clean-up: removes <answerId>.mp4 and nothing else. A file that is already gone is
  // not an error, so an interrupted clean-up can be repeated.
  async removeVideo(batchId: string, answerId: string): Promise<void> {
    const id = z.uuid().parse(answerId);
    const path = join(
      this.root,
      "batches",
      batchIdSchema.parse(batchId),
      "records",
      id,
      `${id}.mp4`,
    );
    try {
      await unlink(path);
    } catch (error) {
      if (!isMissing(error)) throw unavailable();
    }
  }

  // Call right before the send tap and wait for it: append + fsync.
  appendLedger(input: LedgerEntry): Promise<void> {
    return this.serialized(async () => {
      const entry = ledgerEntrySchema.parse(input);
      const file = await open(
        join(this.root, "ledger.jsonl"),
        constants.O_WRONLY | constants.O_APPEND | constants.O_CREAT | constants.O_NOFOLLOW,
        0o600,
      ).catch(() => {
        throw unavailable();
      });
      try {
        await file.writeFile(`${JSON.stringify(entry)}\n`);
        await file.sync();
      } finally {
        await file.close();
      }
    });
  }
  // A line that does not parse blocks the script: counting fewer sends than happened would
  // loosen the limits.
  async readLedger(): Promise<LedgerEntry[]> {
    const text = (await readPrivateFile(join(this.root, "ledger.jsonl"))) ?? "";
    try {
      return text
        .split("\n")
        .filter(Boolean)
        .map((line) => ledgerEntrySchema.parse(JSON.parse(line)));
    } catch {
      throw corrupt();
    }
  }

  async readPlatformStatus(): Promise<PlatformStatus> {
    return (
      (await readJson(join(this.root, "platform-status.json"), platformStatusSchema)) ?? {
        version: 1,
        platforms: {},
      }
    );
  }
  markPlatformUnavailable(
    platformCode: LedgerEntry["platformCode"],
    evidence: { failureCode: AppFailureCode; at: Date; batchId: string; answerId: string },
  ): Promise<void> {
    return this.serialized(async () => {
      const status = await this.readPlatformStatus();
      // Keep the first judgement and its evidence reference.
      if (status.platforms[platformCode]) return;
      status.platforms[platformCode] = {
        status: "unavailable",
        failureCode: evidence.failureCode,
        since: evidence.at.toISOString(),
        batchId: evidence.batchId,
        answerId: evidence.answerId,
      };
      await writeJsonAtomic(
        join(this.root, "platform-status.json"),
        platformStatusSchema.parse(status),
      );
    });
  }

  async readBindingStatus(): Promise<BindingStatus> {
    return (
      (await readJson(join(this.root, "binding-status.json"), bindingStatusSchema)) ?? {
        version: 1,
        bindings: {},
      }
    );
  }
  // The binding (see `bindingKey`) stops for the rest of the Asia/Shanghai day of `at`.
  markBindingRateLimited(key: string, at: Date, answerId?: string): Promise<void> {
    return this.serialized(async () => {
      const status = await this.readBindingStatus();
      const day = shanghaiDay(at);
      const bindings = Object.fromEntries(
        Object.entries(status.bindings).filter(([, entry]) => entry.day >= day),
      );
      bindings[key] = {
        reason: "rate_limited",
        day,
        stoppedAt: at.toISOString(),
        ...(answerId === undefined ? {} : { answerId }),
      };
      await writeJsonAtomic(
        join(this.root, "binding-status.json"),
        bindingStatusSchema.parse({ version: 1, bindings }),
      );
    });
  }

  // One record, or undefined when the attempt left no record.json.
  async readRecord(batchId: string, answerId: string): Promise<AppRecord | undefined> {
    const record = await readJson(
      join(
        this.root,
        "batches",
        batchIdSchema.parse(batchId),
        "records",
        z.uuid().parse(answerId),
        "record.json",
      ),
      appRecordSchema,
    );
    if (record !== undefined && (record.answerId !== answerId || record.batchId !== batchId))
      throw corrupt();
    return record;
  }

  private async taskDirectory(): Promise<string> {
    const directory = join(this.root, "control-plane");
    await ensurePrivateDirectory(directory);
    await ensurePrivateDirectory(join(directory, "tasks"));
    return join(directory, "tasks");
  }
  async readTaskState(taskId: string, leaseVersion?: number): Promise<TaskState | undefined> {
    const prefix = join(await this.taskDirectory(), z.guid().parse(taskId).toLowerCase());
    let state = await readJson(`${prefix}.json`, taskStateSchema);
    if (leaseVersion !== undefined && state?.leaseVersion !== leaseVersion) {
      state = await readJson(
        `${prefix}.${z.number().int().positive().parse(leaseVersion)}.json`,
        taskStateSchema,
      );
      if (state && state.leaseVersion !== leaseVersion) throw corrupt();
    }
    if (state !== undefined && state.taskId.toLowerCase() !== taskId.toLowerCase()) throw corrupt();
    return state;
  }
  writeTaskState(input: TaskState): Promise<void> {
    return this.serialized(async () => {
      const state = taskStateSchema.parse(input);
      const directory = await this.taskDirectory();
      const prefix = join(directory, state.taskId.toLowerCase());
      const latest = await this.readTaskState(state.taskId);
      if (latest && latest.leaseVersion > state.leaseVersion) {
        await writeJsonAtomic(`${prefix}.${state.leaseVersion}.json`, state);
      } else {
        if (latest && latest.leaseVersion < state.leaseVersion)
          await writeJsonAtomic(`${prefix}.${latest.leaseVersion}.json`, latest);
        await writeJsonAtomic(`${prefix}.json`, state);
      }
      // An atomic rename alone can revert on loss of power. Before allowing a click,
      // persist the renamed entry and newly created directories, back to the evidence root.
      await syncDirectory(directory);
      await syncDirectory(dirname(directory));
      await syncDirectory(this.root);
    });
  }
  private async attempts(taskId?: string): Promise<TaskState[]> {
    const directory = await this.taskDirectory();
    let names: string[];
    try {
      names = (await readdir(directory)).filter(
        (name) =>
          /^[0-9a-f-]{36}(?:\.\d+)?\.json$/u.test(name) &&
          (!taskId || name.startsWith(`${taskId}.`)),
      );
    } catch {
      throw unavailable();
    }
    const states = new Map<string, TaskState>();
    for (const name of names) {
      const state = await readJson(join(directory, name), taskStateSchema);
      if (state) {
        const id = state.taskId.toLowerCase();
        if (name !== `${id}.json` && name !== `${id}.${state.leaseVersion}.json`) throw corrupt();
        const key = `${id}.${state.leaseVersion}`;
        if (!states.has(key) || name === `${id}.json`) states.set(key, state);
      }
    }
    return [...states.values()].sort((a, b) => a.leaseVersion - b.leaseVersion);
  }
  taskAttempts(taskId: string): Promise<TaskState[]> {
    return this.attempts(z.guid().parse(taskId).toLowerCase());
  }
  async pendingTaskStates(): Promise<TaskState[]> {
    return (await this.attempts())
      .filter(
        (s) => s.state === "started" || s.pendingSubmit || s.pendingSent || s.failure?.pending,
      )
      .sort((a, b) => a.updatedAt.localeCompare(b.updatedAt));
  }

  private serialized<T>(task: () => Promise<T>): Promise<T> {
    const result = this.queue.then(task, task);
    this.queue = result.catch(() => undefined);
    return result;
  }
}
