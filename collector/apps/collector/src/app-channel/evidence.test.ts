import { createHash } from "node:crypto";
import {
  appendFile,
  lstat,
  mkdir,
  readFile,
  readdir,
  rm,
  symlink,
  writeFile,
} from "node:fs/promises";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import {
  EvidenceStore,
  appRecordSchema,
  fileEvidence,
  sha256File,
  sha256Text,
  videoCoversSpan,
  type AppRecord,
  type LedgerEntry,
} from "./evidence.ts";
import { appFailureCodeSchema } from "./failure.ts";
import {
  ANSWER_ID,
  DEVICE_A,
  DEVICE_B,
  SHA,
  evidenceDirectory,
  recordFixture,
} from "./test-fixtures.ts";
import { timestampPair } from "./time.ts";

const created: string[] = [];
const stores: EvidenceStore[] = [];
afterEach(async () => {
  for (const store of stores.splice(0)) await store.close().catch(() => undefined);
  for (const directory of created.splice(0)) await rm(directory, { recursive: true, force: true });
});
async function openStore(directory?: string): Promise<EvidenceStore> {
  const store = await EvidenceStore.open(directory ?? (await evidenceDirectory(created)));
  stores.push(store);
  return store;
}
const mode = async (path: string) => (await lstat(path)).mode & 0o777;
const ledgerEntry = (overrides: Partial<LedgerEntry> = {}): LedgerEntry => ({
  answerId: ANSWER_ID,
  batchId: "batch-1",
  deviceId: DEVICE_A,
  platformCode: "doubao",
  sentAt: "2026-11-15T04:00:20.000Z",
  ...overrides,
});

describe("evidence directory", () => {
  it("creates owner-only directories and holds a single-instance lock", async () => {
    const directory = await evidenceDirectory(created);
    const store = await openStore(directory);
    expect(store.root).toBe(directory);
    expect(await mode(directory)).toBe(0o700);
    expect(await mode(join(directory, "batches"))).toBe(0o700);
    await expect(EvidenceStore.open(directory)).rejects.toMatchObject({
      code: "evidence_unavailable",
    });
    await store.close();
    await (await EvidenceStore.open(directory)).close();
  });
  it("refuses relative paths, directories inside a repository and symlinked ancestors", async () => {
    await expect(EvidenceStore.open("aivc/evidence")).rejects.toMatchObject({
      code: "evidence_unavailable",
    });
    // This repository's own tree.
    await expect(
      EvidenceStore.open(join(import.meta.dirname, "aivc", "evidence")),
    ).rejects.toMatchObject({ code: "evidence_unavailable" });
    const directory = await evidenceDirectory(created);
    const base = join(directory, "..", "..");
    await mkdir(join(base, "real"), { mode: 0o700 });
    await symlink(join(base, "real"), join(base, "aivc"));
    await expect(EvidenceStore.open(directory)).rejects.toMatchObject({
      code: "evidence_unavailable",
    });
  });
  it("lays out batches/<batchId>/records/<answerId> and rejects unsafe names", async () => {
    const store = await openStore();
    const directory = await store.recordDirectory("batch-1", ANSWER_ID);
    expect(directory).toBe(join(store.root, "batches", "batch-1", "records", ANSWER_ID));
    expect(await mode(directory)).toBe(0o700);
    await expect(store.recordDirectory("..", ANSWER_ID)).rejects.toThrow();
    await expect(store.recordDirectory("a/b", ANSWER_ID)).rejects.toThrow();
    await expect(store.recordDirectory("batch-1", "../escape")).rejects.toThrow();
  });
});

describe("record.json", () => {
  it("writes a validated record atomically with private permissions", async () => {
    const store = await openStore();
    const record = recordFixture();
    const path = await store.writeRecord(record);
    expect(path).toBe(join(store.root, "batches", "batch-1", "records", ANSWER_ID, "record.json"));
    expect(await mode(path)).toBe(0o600);
    expect(JSON.parse(await readFile(path, "utf8"))).toEqual(record);
    // No temp file is left behind.
    expect(await readdir(join(path, ".."))).toEqual(["record.json"]);
    expect(await store.readRecords("batch-1")).toEqual([record]);
  });
  it("writes a record once; only the retention clean-up may replace it", async () => {
    const store = await openStore();
    await store.writeRecord(recordFixture());
    await expect(store.writeRecord(recordFixture())).rejects.toMatchObject({
      code: "evidence_corrupt",
    });
    const pruned = recordFixture();
    pruned.video = { ...pruned.video!, prunedAt: "2026-12-16T00:00:00.000Z" };
    await store.writeRecord(pruned, { replace: true });
    expect((await store.readRecords("batch-1"))[0]?.video?.prunedAt).toBe(
      "2026-12-16T00:00:00.000Z",
    );
  });
  it("skips an attempt directory without record.json and blocks on a corrupt record", async () => {
    const store = await openStore();
    await store.writeRecord(recordFixture());
    await store.recordDirectory("batch-1", "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    expect(await store.readRecords("batch-1")).toHaveLength(1);
    expect(await store.readRecords("batch-2")).toEqual([]);
    const other = await store.recordDirectory("batch-1", "cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    await writeFile(join(other, "record.json"), "{not json");
    await expect(store.readRecords("batch-1")).rejects.toMatchObject({ code: "evidence_corrupt" });
  });
  it("ignores entries of the records directory that are not answer IDs", async () => {
    const store = await openStore();
    await store.writeRecord(recordFixture());
    // macOS Finder leaves this behind when an operator opens the folder to watch a video.
    const records = join(store.root, "batches", "batch-1", "records");
    await writeFile(join(records, ".DS_Store"), "finder metadata");
    await mkdir(join(records, "notes"));
    expect(await store.readRecords("batch-1")).toEqual([recordFixture()]);
  });
  it("refuses to write an invalid record", async () => {
    const store = await openStore();
    await expect(
      store.writeRecord({ ...recordFixture(), answerText: null } as AppRecord),
    ).rejects.toThrow();
    expect(await store.readRecords("batch-1")).toEqual([]);
  });
});

// FR-A6 / design §5: the record carries every listed field.
describe("record schema", () => {
  const fields = [
    "answerId",
    "batchId",
    "sampleClass",
    "channel",
    "scriptRevision",
    "questionText",
    "sentText",
    "platformCode",
    "package",
    "appVersion",
    "deviceId",
    "adbSerial",
    "androidVersion",
    "abi",
    "deviceLocale",
    "deviceTimezone",
    "accountLabel",
    "startedAt",
    "sentAt",
    "completedAt",
    "status",
    "failureCode",
    "phase",
    "answerText",
    "citations",
    "searchState",
    "video",
    "screenshot",
    "uiDump",
  ] as const;
  it("has exactly the designed fields and requires each of them", () => {
    // Optional: the reason a record's evidence is unusable, and how the sources of a collected
    // answer were read (absent on failures and on records written before v1.1), list by list
    // where the contract declares several source lists; the leased task of a control_plane record;
    // the screen where a read of links stopped because its list did not move.
    expect(Object.keys(appRecordSchema.shape).sort()).toEqual(
      [
        ...fields,
        "evidenceIssue",
        "citationsStatus",
        "citationsIssue",
        "citationLists",
        "task",
        "linkStop",
      ].sort(),
    );
    // A control_plane record names its task; a local trial record never does.
    const task = { taskId: "0b9c3c5e-c4a1-11f0-9a3b-0242ac120002", leaseVersion: 1 };
    expect(
      appRecordSchema.safeParse(recordFixture({ sampleClass: "control_plane", task })).success,
    ).toBe(true);
    expect(appRecordSchema.safeParse(recordFixture({ sampleClass: "control_plane" })).success).toBe(
      false,
    );
    expect(appRecordSchema.safeParse(recordFixture({ task })).success).toBe(false);
    expect(appRecordSchema.safeParse(recordFixture()).success).toBe(true);
    for (const field of fields) {
      const record: Record<string, unknown> = recordFixture();
      delete record[field];
      expect(appRecordSchema.safeParse(record).success, field).toBe(false);
    }
    expect(appRecordSchema.safeParse({ ...recordFixture(), cookie: "x" }).success).toBe(false);
  });
  it("marks every record as a local trial sample of the App channel", () => {
    expect(appRecordSchema.safeParse({ ...recordFixture(), sampleClass: "formal" }).success).toBe(
      false,
    );
    expect(appRecordSchema.safeParse({ ...recordFixture(), channel: "consumer_web" }).success).toBe(
      false,
    );
  });
  it("stores each time in UTC and Asia/Shanghai as the same instant", () => {
    const record = recordFixture();
    expect(record.startedAt).toEqual({
      utc: "2026-11-15T04:00:00.000Z",
      shanghai: "2026-11-15T12:00:00.000+08:00",
    });
    expect(
      appRecordSchema.safeParse({
        ...record,
        startedAt: { utc: record.startedAt.utc, shanghai: "2026-11-15T13:00:00.000+08:00" },
      }).success,
    ).toBe(false);
  });
  it("binds the video to the answer ID by file name", () => {
    const record = recordFixture();
    expect(record.video?.file).toBe(`${record.answerId}.mp4`);
    expect(
      appRecordSchema.safeParse({ ...record, video: { ...record.video!, file: "other.mp4" } })
        .success,
    ).toBe(false);
  });
  // AC-A17: recording is off by default, so a valid sample needs no video; one that is there
  // must still be whole.
  it("accepts a valid sample without a video", () => {
    const record = recordFixture({ video: null });
    expect(appRecordSchema.safeParse(record).success).toBe(true);
    // The completion screenshot stays mandatory.
    expect(appRecordSchema.safeParse({ ...record, screenshot: null }).success).toBe(false);
  });
  it("accepts a valid sample only with complete evidence", () => {
    const valid = recordFixture();
    const invalid: Partial<AppRecord>[] = [
      // Not playable.
      { video: { ...valid.video!, durationMs: null } },
      // Shorter than the 60 s from send to completion.
      { video: { ...valid.video!, durationMs: 59_000 } },
      { screenshot: null },
      { answerText: "" },
      { sentAt: null },
      { completedAt: null },
      { failureCode: "collection_timeout" },
      { phase: "post_send" },
    ];
    for (const overrides of invalid)
      expect(appRecordSchema.safeParse({ ...valid, ...overrides }).success).toBe(false);
  });
  it("accepts failures with their class and phase, before and after the send", () => {
    const preSend = recordFixture({
      status: "failed",
      failureCode: "recording_failed",
      phase: "pre_send",
      sentAt: null,
      completedAt: null,
      answerText: null,
      citations: [],
      searchState: null,
      video: null,
      screenshot: null,
      uiDump: null,
    });
    expect(appRecordSchema.safeParse(preSend).success).toBe(true);
    // A timeout keeps the partial text and its video but is not a valid sample.
    const postSend = recordFixture({
      status: "failed",
      failureCode: "collection_timeout",
      phase: "post_send",
      completedAt: null,
      answerText: "合成的部分回答",
    });
    expect(appRecordSchema.safeParse(postSend).success).toBe(true);
    for (const overrides of [
      { failureCode: null },
      { phase: null },
      // Nothing was sent, so there is no send time; and the reverse.
      { sentAt: timestampPair(new Date("2026-11-15T04:00:20.000Z")) },
    ] satisfies Partial<AppRecord>[])
      expect(appRecordSchema.safeParse({ ...preSend, ...overrides }).success).toBe(false);
    expect(appRecordSchema.safeParse({ ...postSend, sentAt: null }).success).toBe(false);
    for (const code of appFailureCodeSchema.options)
      expect(appRecordSchema.safeParse({ ...postSend, failureCode: code }).success, code).toBe(
        true,
      );
  });
  it("accepts evidence_incomplete only after a send and without a failure class", () => {
    const incomplete = recordFixture({
      status: "evidence_incomplete",
      phase: "post_send",
      video: null,
      evidenceIssue: "pull_failed",
    });
    expect(appRecordSchema.safeParse(incomplete).success).toBe(true);
    expect(
      appRecordSchema.safeParse({ ...incomplete, phase: "pre_send", sentAt: null }).success,
    ).toBe(false);
    expect(appRecordSchema.safeParse({ ...incomplete, failureCode: "app_crashed" }).success).toBe(
      false,
    );
  });
  it("persists why the evidence is unusable, and never on a valid sample", () => {
    const incomplete = recordFixture({
      status: "evidence_incomplete",
      phase: "post_send",
      evidenceIssue: "too_short",
    });
    expect(appRecordSchema.parse(incomplete).evidenceIssue).toBe("too_short");
    // The reason is mandatory for evidence_incomplete and must be a known one.
    expect(appRecordSchema.safeParse({ ...incomplete, evidenceIssue: undefined }).success).toBe(
      false,
    );
    expect(appRecordSchema.safeParse({ ...incomplete, evidenceIssue: "unknown" }).success).toBe(
      false,
    );
    expect(
      appRecordSchema.safeParse({ ...incomplete, evidenceIssue: "screenshot_failed" }).success,
    ).toBe(true);
    // A failure may carry it too (its recording was also cut short).
    const failed = recordFixture({
      status: "failed",
      failureCode: "app_crashed",
      phase: "post_send",
      completedAt: null,
      answerText: null,
      evidenceIssue: "recorder_exited",
    });
    expect(appRecordSchema.safeParse(failed).success).toBe(true);
    expect(appRecordSchema.safeParse(recordFixture({ evidenceIssue: "too_short" })).success).toBe(
      false,
    );
  });
  // Design §16.2: how the sources were read is part of a collected answer.
  it("records how the sources were read, consistently with the source list", () => {
    const accepts = (overrides: Partial<AppRecord>) =>
      appRecordSchema.safeParse(recordFixture(overrides)).success;
    expect(accepts({ citationsStatus: "read" })).toBe(true);
    expect(accepts({ citations: [], citationsStatus: "absent" })).toBe(true);
    expect(accepts({ citations: [], citationsStatus: "not_declared" })).toBe(true);
    expect(
      accepts({ citations: [], citationsStatus: "unreadable", citationsIssue: "citation_count" }),
    ).toBe(true);
    expect(
      accepts({
        citations: [],
        citationsStatus: "unreadable",
        citationsIssue: "driver_command_failed",
      }),
    ).toBe(true);
    // An unknown state; "read" without a source; a list kept next to a read that failed.
    expect(accepts({ citationsStatus: "partial" as "read" })).toBe(false);
    expect(accepts({ citations: [], citationsStatus: "read" })).toBe(false);
    expect(accepts({ citationsStatus: "absent" })).toBe(false);
    expect(accepts({ citationsStatus: "unreadable", citationsIssue: "citation_count" })).toBe(
      false,
    );
    // The reason belongs to "unreadable" and to nothing else, and is a fixed word.
    expect(accepts({ citations: [], citationsStatus: "unreadable" })).toBe(false);
    expect(accepts({ citationsStatus: "read", citationsIssue: "citation_count" })).toBe(false);
    expect(accepts({ citationsIssue: "citation_count" })).toBe(false);
    expect(
      accepts({ citations: [], citationsStatus: "unreadable", citationsIssue: "参考 3 篇资料" }),
    ).toBe(false);
    // It describes a collected answer: evidence_incomplete carries it, a failure does not.
    expect(
      accepts({
        status: "evidence_incomplete",
        phase: "post_send",
        evidenceIssue: "screenshot_failed",
        screenshot: null,
        video: null,
        citationsStatus: "read",
      }),
    ).toBe(true);
    expect(
      accepts({
        status: "failed",
        failureCode: "collection_timeout",
        phase: "post_send",
        completedAt: null,
        citationsStatus: "read",
      }),
    ).toBe(false);
  });
  // Design §16.7: an answer with several source lists names the list of every source and says
  // how each list was read.
  it("records several source lists consistently with the sources and the overall status", () => {
    const source = (title: string, list?: string) => ({
      title,
      site: null,
      url: null,
      ...(list === undefined ? {} : { list }),
    });
    const both: Partial<AppRecord> = {
      // The same source in both lists is kept once for each.
      citations: [
        source("合成来源一", "cited"),
        source("合成来源二", "cited"),
        source("合成来源二", "search"),
      ],
      citationsStatus: "read",
      citationLists: [
        { label: "cited", status: "read", count: 2 },
        { label: "search", status: "read", count: 1 },
      ],
    };
    const accepts = (overrides: Partial<AppRecord>) =>
      appRecordSchema.safeParse(recordFixture({ ...both, ...overrides })).success;
    const lists = (...items: NonNullable<AppRecord["citationLists"]>) => items;
    const failed = { status: "unreadable", issue: "citation_count", count: 0 } as const;
    expect(accepts({})).toBe(true);
    // One list left out, the other read: read overall, without a cause of its own.
    expect(
      accepts({
        citations: [source("合成来源二", "search")],
        citationLists: lists(
          { label: "cited", ...failed },
          { label: "search", status: "read", count: 1 },
        ),
      }),
    ).toBe(true);
    // Neither read: unreadable overall, with the cause of the first list that failed.
    const neither = {
      citations: [],
      citationsStatus: "unreadable",
      citationsIssue: "citation_count",
      citationLists: lists(
        { label: "cited", status: "absent", count: 0 },
        { label: "search", ...failed },
        { label: "more", status: "unreadable", issue: "source_list_empty", count: 0 },
      ),
    } satisfies Partial<AppRecord>;
    expect(accepts(neither)).toBe(true);
    expect(accepts({ ...neither, citationsIssue: "source_list_empty" })).toBe(false);
    expect(
      accepts({
        citations: [],
        citationsStatus: "absent",
        citationLists: lists(
          { label: "cited", status: "absent", count: 0 },
          { label: "search", status: "absent", count: 0 },
        ),
      }),
    ).toBe(true);
    // It describes a collected answer: evidence_incomplete carries it, a failure does not. What
    // a failure had read by then still names its list.
    expect(
      accepts({
        status: "evidence_incomplete",
        phase: "post_send",
        evidenceIssue: "screenshot_failed",
        screenshot: null,
        video: null,
      }),
    ).toBe(true);
    const failure = {
      status: "failed",
      failureCode: "app_contract_changed",
      phase: "post_send",
      completedAt: null,
    } satisfies Partial<AppRecord>;
    expect(accepts(failure)).toBe(false);
    expect(accepts({ ...failure, citationsStatus: undefined, citationLists: undefined })).toBe(
      true,
    );

    // One list or four; a label twice; a label that is not one; a state a list cannot have.
    const [first, second] = both.citationLists!;
    const more = (label: string) => ({ label, status: "absent", count: 0 }) as const;
    expect(accepts({ citationLists: lists(first!) })).toBe(false);
    expect(accepts({ citationLists: lists(first!, second!, more("third")) })).toBe(true);
    expect(accepts({ citationLists: lists(first!, second!, more("third"), more("fourth")) })).toBe(
      false,
    );
    expect(accepts({ citationLists: lists(first!, { ...second!, label: "cited" }) })).toBe(false);
    for (const label of ["Search", "1st", "a".repeat(21), "合成", ""])
      expect(accepts({ citationLists: lists(first!, more(label)) }), label).toBe(false);
    expect(
      accepts({
        citationLists: lists(first!, { ...second!, status: "not_declared" as "absent" }),
      }),
    ).toBe(false);
    // The count is the number of sources of that list; a list holds sources exactly when it
    // was read; a cause belongs to an unreadable list and to nothing else.
    expect(accepts({ citationLists: lists(first!, { ...second!, count: 2 }) })).toBe(false);
    expect(accepts({ citationLists: lists({ ...first!, status: "absent" }, second!) })).toBe(false);
    expect(
      accepts({
        citations: both.citations!.slice(0, 2),
        citationLists: lists(first!, { label: "search", status: "read", count: 0 }),
      }),
    ).toBe(false);
    expect(accepts({ citationLists: lists(first!, { ...second!, issue: "citation_count" }) })).toBe(
      false,
    );
    expect(
      accepts({
        citations: both.citations!.slice(0, 2),
        citationLists: lists(first!, { label: "search", status: "unreadable", count: 0 }),
      }),
    ).toBe(false);
    expect(
      accepts({
        citations: both.citations!.slice(0, 2),
        citationLists: lists(first!, { label: "search", ...failed, issue: "参考 3 篇资料" }),
      }),
    ).toBe(false);
    // Every source names one of the lists, list after list in their order.
    expect(accepts({ citations: [...both.citations!.slice(0, 2), source("合成来源二")] })).toBe(
      false,
    );
    expect(
      accepts({ citations: [...both.citations!.slice(0, 2), source("合成来源二", "other")] }),
    ).toBe(false);
    expect(accepts({ citations: [...both.citations!].reverse() })).toBe(false);
    // The overall status is that of the lists taken together.
    expect(accepts({ citationsStatus: undefined })).toBe(false);
    expect(accepts({ citationsStatus: "absent" })).toBe(false);
    // Without the lists no source names one: a record of one list, old or new.
    expect(accepts({ citationLists: undefined })).toBe(false);
    expect(
      accepts({
        citations: [source("合成来源一")],
        citationLists: undefined,
        citationsStatus: "read",
      }),
    ).toBe(true);
  });
  it("writes and reads back a record with several source lists", async () => {
    const record = recordFixture({
      video: null,
      citations: [{ title: "合成来源", site: null, url: null, list: "search" }],
      citationsStatus: "read",
      citationLists: [
        { label: "cited", status: "unreadable", issue: "source_panel_not_opened", count: 0 },
        { label: "search", status: "read", count: 1 },
      ],
    });
    const store = await openStore();
    await store.writeRecord(record);
    expect(await store.readRecords("batch-1")).toEqual([record]);
  });
  // AC-A17: what v1.0 wrote (a video, the Web prefix in the sent text, no source status) is
  // still a record.
  it("still reads a record written before v1.1", async () => {
    const legacy = recordFixture({ sentText: "合成前缀\n\n合成问题" });
    expect(legacy.video).not.toBeNull();
    expect(legacy).not.toHaveProperty("citationsStatus");
    // Nor what came with several source lists: no list states, no list on a source.
    expect(legacy).not.toHaveProperty("citationLists");
    expect(legacy.citations[0]).not.toHaveProperty("list");
    expect(appRecordSchema.safeParse(legacy).success).toBe(true);
    const store = await openStore();
    await store.writeRecord(legacy);
    expect(await store.readRecords("batch-1")).toEqual([legacy]);
  });
  it("keeps citation URLs and sites null when the App does not show them", () => {
    const record = recordFixture({
      citations: [
        { title: "合成来源一", site: "合成站点", url: "https://example.invalid/a" },
        { title: "合成来源二", site: null, url: null },
      ],
    });
    expect(appRecordSchema.safeParse(record).success).toBe(true);
    expect(
      appRecordSchema.safeParse({ ...record, citations: [{ title: "只有标题" }] }).success,
    ).toBe(false);
  });
  // Task 11-24: the link read by opening each source.
  it("keeps the copied link next to the restored address, or the cause of a missing one", async () => {
    const copied = "https://example.com/p?id=1&theme=light";
    const record = recordFixture({
      video: null,
      citations: [
        {
          title: "合成来源一",
          site: null,
          url: "https://example.com/p?id=1",
          link: { copied, restored: true },
        },
        {
          title: "合成来源二",
          site: null,
          url: "https://link.example.cn/x",
          link: { copied: "https://link.example.cn/x", restored: false },
        },
        { title: "合成来源三", site: null, url: null, link: { issue: "page_not_opened" } },
      ],
      citationsStatus: "read",
    });
    expect(appRecordSchema.safeParse(record).success).toBe(true);
    const store = await openStore();
    await store.writeRecord(record);
    expect(await store.readRecords("batch-1")).toEqual([record]);

    const source = (citation: Record<string, unknown>) =>
      appRecordSchema.safeParse(
        recordFixture({ citations: [{ title: "合成来源", site: null, ...citation } as never] }),
      ).success;
    // A copied link is the source's url, restored or as it is.
    expect(source({ url: null, link: { copied, restored: true } })).toBe(false);
    expect(source({ url: "https://example.com/other", link: { copied, restored: false } })).toBe(
      false,
    );
    // Fixed cause words only, and one of the two forms.
    expect(source({ url: null, link: { issue: "time_budget" } })).toBe(true);
    expect(source({ url: null, link: { issue: "合成原因" } })).toBe(false);
    expect(source({ url: null, link: { issue: "time_budget", copied } })).toBe(false);
    expect(source({ url: copied, link: { copied, restored: false, title: "x" } })).toBe(false);
    expect(source({ url: copied, link: { copied: "", restored: false } })).toBe(false);
  });
  // Task 11-24 §12.2.
  it("keeps the screen at a stop of the link read, part by part, and reads records without it", () => {
    const evidence = { file: "links-stop.png", sha256: SHA, bytes: 10 };
    const xml = { file: "links-stop.xml", sha256: SHA, bytes: 20 };
    for (const linkStop of [
      { screenshot: evidence, hierarchy: xml },
      { screenshot: null, hierarchy: xml },
      { screenshot: null, hierarchy: null },
    ])
      expect(appRecordSchema.safeParse(recordFixture({ linkStop })).success).toBe(true);
    expect(recordFixture()).not.toHaveProperty("linkStop");
    expect(appRecordSchema.safeParse(recordFixture()).success).toBe(true);
    // Strict: both parts named, nothing else.
    for (const linkStop of [
      { screenshot: evidence },
      { screenshot: evidence, hierarchy: xml, note: "x" },
      { screenshot: { file: "links-stop.png" }, hierarchy: null },
    ])
      expect(
        appRecordSchema.safeParse({ ...recordFixture(), linkStop }).success,
        JSON.stringify(linkStop),
      ).toBe(false);
  });
  it("still reads a source without a link, as written before links were read", () => {
    const record = recordFixture();
    expect(record.citations[0]).not.toHaveProperty("link");
    expect(appRecordSchema.parse(record)).toEqual(record);
  });
});

describe("video completeness rule", () => {
  it("needs a playable video at least as long as the send-to-completion span", () => {
    const sent = "2026-11-15T04:00:00.000Z";
    const completed = "2026-11-15T04:03:20.000Z"; // 200 s
    expect(videoCoversSpan(200_000, sent, completed)).toBe(true);
    // Emulator video runs 4–6 % long: longer than wall clock is fine.
    expect(videoCoversSpan(213_500, sent, completed)).toBe(true);
    expect(videoCoversSpan(199_999, sent, completed)).toBe(false);
    expect(videoCoversSpan(null, sent, completed)).toBe(false);
    expect(videoCoversSpan(0, null, null)).toBe(false);
    // Nothing sent: playable is enough.
    expect(videoCoversSpan(1_500, null, null)).toBe(true);
  });
});

describe("send ledger", () => {
  it("appends one private line per send and reads them back after a restart", async () => {
    const directory = await evidenceDirectory(created);
    const store = await openStore(directory);
    const first = ledgerEntry();
    const second = ledgerEntry({
      answerId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
      deviceId: DEVICE_B,
      sentAt: "2026-11-15T04:02:20.000Z",
    });
    await store.appendLedger(first);
    await store.appendLedger(second);
    const path = join(directory, "ledger.jsonl");
    expect(await mode(path)).toBe(0o600);
    expect((await readFile(path, "utf8")).split("\n")).toEqual([
      JSON.stringify(first),
      JSON.stringify(second),
      "",
    ]);
    await store.close();
    expect(await (await openStore(directory)).readLedger()).toEqual([first, second]);
  });
  it("keeps every line when both device loops append at once", async () => {
    const store = await openStore();
    const entries = Array.from({ length: 20 }, (_, index) =>
      ledgerEntry({ sentAt: new Date(Date.UTC(2026, 10, 15, 4, index)).toISOString() }),
    );
    await Promise.all(entries.map((entry) => store.appendLedger(entry)));
    expect(await store.readLedger()).toHaveLength(20);
  });
  it("rejects an invalid entry and blocks on a corrupt ledger", async () => {
    const store = await openStore();
    await expect(store.appendLedger({ ...ledgerEntry(), sentAt: "yesterday" })).rejects.toThrow();
    expect(await store.readLedger()).toEqual([]);
    await store.appendLedger(ledgerEntry());
    await appendFile(join(store.root, "ledger.jsonl"), "{broken\n");
    await expect(store.readLedger()).rejects.toMatchObject({ code: "evidence_corrupt" });
  });
  it("never follows a symlink in place of the ledger", async () => {
    const store = await openStore();
    const outside = join(store.root, "..", "outside.jsonl");
    await writeFile(outside, "");
    await symlink(outside, join(store.root, "ledger.jsonl"));
    await expect(store.appendLedger(ledgerEntry())).rejects.toThrow();
    await expect(store.readLedger()).rejects.toMatchObject({ code: "evidence_unavailable" });
    expect(await readFile(outside, "utf8")).toBe("");
  });
});

describe("status files", () => {
  it("records an unavailable platform with its evidence reference and keeps the first one", async () => {
    const directory = await evidenceDirectory(created);
    const store = await openStore(directory);
    expect(await store.readPlatformStatus()).toEqual({ version: 1, platforms: {} });
    await store.markPlatformUnavailable("qwen", {
      failureCode: "emulator_rejected",
      at: new Date("2026-11-15T04:00:00.000Z"),
      batchId: "batch-1",
      answerId: ANSWER_ID,
    });
    await store.markPlatformUnavailable("qwen", {
      failureCode: "emulator_rejected",
      at: new Date("2026-11-16T04:00:00.000Z"),
      batchId: "batch-2",
      answerId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
    });
    expect(await mode(join(directory, "platform-status.json"))).toBe(0o600);
    await store.close();
    expect(await (await openStore(directory)).readPlatformStatus()).toEqual({
      version: 1,
      platforms: {
        qwen: {
          status: "unavailable",
          failureCode: "emulator_rejected",
          since: "2026-11-15T04:00:00.000Z",
          batchId: "batch-1",
          answerId: ANSWER_ID,
        },
      },
    });
  });
  it("records a rate-limited binding for its Asia/Shanghai day and drops earlier days", async () => {
    const store = await openStore();
    // 2026-11-15T17:00Z is already 2026-11-16 in Asia/Shanghai.
    await store.markBindingRateLimited(`${DEVICE_A}/doubao`, new Date("2026-11-15T04:00:00.000Z"));
    await store.markBindingRateLimited(
      `${DEVICE_B}/kimi`,
      new Date("2026-11-15T17:00:00.000Z"),
      ANSWER_ID,
    );
    expect(await store.readBindingStatus()).toEqual({
      version: 1,
      bindings: {
        [`${DEVICE_B}/kimi`]: {
          reason: "rate_limited",
          day: "2026-11-16",
          stoppedAt: "2026-11-15T17:00:00.000Z",
          answerId: ANSWER_ID,
        },
      },
    });
  });
  it("blocks on a corrupt status file instead of treating it as empty", async () => {
    const store = await openStore();
    await writeFile(join(store.root, "binding-status.json"), "{}");
    await expect(store.readBindingStatus()).rejects.toMatchObject({ code: "evidence_corrupt" });
  });
});

describe("hash helpers", () => {
  it("hashes text and files and describes an evidence file", async () => {
    const store = await openStore();
    const path = join(await store.recordDirectory("batch-1", ANSWER_ID), "final.png");
    await writeFile(path, "synthetic image bytes");
    const expected = createHash("sha256").update("synthetic image bytes").digest("hex");
    expect(sha256Text("synthetic image bytes")).toBe(expected);
    expect(await sha256File(path)).toBe(expected);
    expect(await fileEvidence(path)).toEqual({ file: "final.png", sha256: expected, bytes: 21 });
  });
});
