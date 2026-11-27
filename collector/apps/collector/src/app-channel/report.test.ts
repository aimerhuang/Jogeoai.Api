import { readFile, readdir, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { parseBindings, resolveBindings } from "./bindings.ts";
import {
  EvidenceStore,
  sha256Text,
  type AppRecord,
  type BatchFile,
  type BatchRun,
} from "./evidence.ts";
import { buildReport, pruneVideos, writeBatchReport } from "./report.ts";
import {
  DEVICE_A,
  DEVICE_B,
  bindingsFixture,
  evidenceDirectory,
  recordFixture,
} from "./test-fixtures.ts";

const created: string[] = [];
const stores: EvidenceStore[] = [];
afterEach(async () => {
  for (const store of stores.splice(0)) await store.close().catch(() => undefined);
  for (const directory of created.splice(0)) await rm(directory, { recursive: true, force: true });
});
async function openStore(): Promise<EvidenceStore> {
  const store = await EvidenceStore.open(await evidenceDirectory(created));
  stores.push(store);
  return store;
}

const QUESTIONS = ["合成问题一", "合成问题二", "合成问题三"];
const NOW = new Date("2026-11-15T10:00:00.000Z");
const uuid = (index: number) => `${String(index).padStart(8, "0")}-aaaa-4aaa-8aaa-aaaaaaaaaaaa`;
const run: BatchRun = {
  startedAt: "2026-11-15T04:00:00.000Z",
  scope: { platforms: null, devices: null, questions: null, limit: null },
  approvedQuestionsSha256: "1".repeat(64),
  bindingsSha256: "2".repeat(64),
  scriptRevision: "test-revision",
  tools: { appium: "3.9.1" },
  helperPackages: ["io.appium.settings"],
};
const plan = (platforms: string[]) =>
  platforms.flatMap((platformCode) =>
    QUESTIONS.map((question) => ({
      platformCode: platformCode as "doubao",
      questionSha256: sha256Text(question),
    })),
  );
const batch = (platforms: string[]): BatchFile => ({
  version: 1,
  batchId: "batch-1",
  createdAt: run.startedAt,
  cells: plan(platforms),
  runs: [run],
});
let nextRecord = 0;
const record = (overrides: Partial<AppRecord>): AppRecord => {
  const answerId = uuid((nextRecord += 1));
  const base = recordFixture();
  return recordFixture({
    answerId,
    video: { ...base.video!, file: `${answerId}.mp4` },
    ...overrides,
  });
};
const failed = (overrides: Partial<AppRecord>): AppRecord =>
  record({
    status: "failed",
    phase: "post_send",
    completedAt: null,
    answerText: null,
    citations: [],
    searchState: null,
    ...overrides,
  });

// Two devices, doubao and kimi enabled on both, ernie disabled on both.
function bindings() {
  const config = bindingsFixture();
  config.platforms.ernie = { package: "com.baidu.newapp", minIntervalSeconds: 120, dailyCap: 120 };
  for (const [device, label] of [
    [DEVICE_A, "ernie-a"],
    [DEVICE_B, "ernie-b"],
  ])
    config.bindings.push({
      device,
      platform: "ernie",
      reviewedAppVersion: "5.22.0.10",
      accountLabel: label,
      enabled: false,
      disabledReason: "chat content is not readable",
      minIntervalSeconds: 600,
      hourlyQuota: 4,
      dailyQuota: 30,
    });
  return resolveBindings(parseBindings(config));
}

describe("batch report", () => {
  const records = [
    // doubao: two valid cells, one cell that timed out once and was then collected on device B.
    record({ platformCode: "doubao", questionText: QUESTIONS[0]! }),
    failed({
      platformCode: "doubao",
      questionText: QUESTIONS[1]!,
      failureCode: "collection_timeout",
    }),
    record({
      platformCode: "doubao",
      questionText: QUESTIONS[1]!,
      deviceId: DEVICE_B,
      accountLabel: "doubao-b",
    }),
    record({
      platformCode: "doubao",
      questionText: QUESTIONS[2]!,
      status: "evidence_incomplete",
      phase: "post_send",
      evidenceIssue: "too_short",
    }),
    // kimi: stopped by a contract change on both devices, nothing valid.
    failed({
      platformCode: "kimi",
      package: "com.moonshot.kimichat",
      appVersion: "3.1.8",
      accountLabel: "kimi-a",
      questionText: QUESTIONS[0]!,
      failureCode: "app_contract_changed",
      phase: "pre_send",
      sentAt: null,
    }),
    failed({
      platformCode: "kimi",
      package: "com.moonshot.kimichat",
      appVersion: "3.1.8",
      deviceId: DEVICE_B,
      accountLabel: "kimi-b",
      questionText: QUESTIONS[0]!,
      failureCode: "app_contract_changed",
      phase: "pre_send",
      sentAt: null,
    }),
  ];
  const report = buildReport({
    batchId: "batch-1",
    batch: batch(["doubao", "kimi"]),
    records,
    bindings: bindings(),
    platformStatus: { version: 1, platforms: {} },
    now: NOW,
  });

  it("counts planned, valid, failed by class, incomplete and not collected per platform", () => {
    expect(report.platforms.map((platform) => platform.platformCode)).toEqual([
      "doubao",
      "kimi",
      "ernie",
    ]);
    expect(report.platforms[0]).toMatchObject({
      platformCode: "doubao",
      package: "com.larus.nova",
      planned: 3,
      attempts: 4,
      valid: 2,
      failed: { collection_timeout: 1 },
      evidenceIncomplete: 1,
      notCollected: 1,
    });
    expect(report.platforms[1]).toMatchObject({
      planned: 3,
      attempts: 2,
      valid: 0,
      failed: { app_contract_changed: 2 },
      evidenceIncomplete: 0,
      notCollected: 3,
    });
    expect(report.platforms[2]).toMatchObject({
      planned: 0,
      attempts: 0,
      valid: 0,
      notCollected: 0,
    });
    expect(report.totals).toEqual({
      planned: 6,
      attempts: 6,
      valid: 2,
      failed: { collection_timeout: 1, app_contract_changed: 2 },
      evidenceIncomplete: 1,
      notCollected: 4,
    });
  });
  // Design §16.2: valid samples by how their sources were read.
  it("counts the valid samples of a platform by the state of their sources", () => {
    const sample = (overrides: Partial<AppRecord>) =>
      record({ platformCode: "doubao", questionText: QUESTIONS[0]!, ...overrides });
    const counted = buildReport({
      batchId: "batch-1",
      batch: batch(["doubao", "kimi"]),
      records: [
        sample({ citationsStatus: "read" }),
        sample({ citationsStatus: "read", video: null }),
        sample({ citations: [], citationsStatus: "absent" }),
        sample({
          citations: [],
          citationsStatus: "unreadable",
          citationsIssue: "source_panel_not_opened",
        }),
        // Written before the status existed: valid, and in none of the four.
        sample({}),
        // Not a valid sample: not counted.
        sample({
          status: "evidence_incomplete",
          phase: "post_send",
          evidenceIssue: "screenshot_failed",
          screenshot: null,
          citationsStatus: "read",
        }),
        sample({
          platformCode: "kimi",
          package: "com.moonshot.kimichat",
          citations: [],
          citationsStatus: "not_declared",
        }),
      ],
      bindings: bindings(),
      platformStatus: { version: 1, platforms: {} },
      now: NOW,
    });
    expect(counted.platforms[0]).toMatchObject({
      platformCode: "doubao",
      valid: 5,
      citations: { read: 2, absent: 1, unreadable: 1, not_declared: 0 },
    });
    expect(counted.platforms[1]).toMatchObject({
      platformCode: "kimi",
      valid: 1,
      citations: { read: 0, absent: 0, unreadable: 0, not_declared: 1 },
    });
    expect(counted.platforms[2]!.citations).toEqual({
      read: 0,
      absent: 0,
      unreadable: 0,
      not_declared: 0,
    });
    // The reason of an unreadable list is in the record, not in the report.
    expect(JSON.stringify(counted)).not.toContain("source_panel_not_opened");
  });
  // Design §16.7: a sample with several source lists counts once, by all of them together.
  it("counts a sample with several source lists by its overall source state", () => {
    const lists = (cited: "read" | "absent", search: "read" | "unreadable"): Partial<AppRecord> => {
      const read = [cited, search].filter((status) => status === "read").length;
      return {
        platformCode: "doubao",
        questionText: QUESTIONS[0]!,
        citations: [
          ...(cited === "read"
            ? [{ title: "合成来源", site: null, url: null, list: "cited" }]
            : []),
          ...(search === "read"
            ? [{ title: "合成来源", site: null, url: null, list: "search" }]
            : []),
        ],
        citationsStatus: read > 0 ? "read" : "unreadable",
        ...(read > 0 ? {} : { citationsIssue: "source_list_empty" }),
        citationLists: [
          { label: "cited", status: cited, count: cited === "read" ? 1 : 0 },
          search === "read"
            ? { label: "search", status: "read", count: 1 }
            : { label: "search", status: "unreadable", issue: "source_list_empty", count: 0 },
        ],
      };
    };
    const counted = buildReport({
      batchId: "batch-1",
      batch: batch(["doubao"]),
      records: [
        record(lists("read", "read")),
        record(lists("read", "unreadable")),
        record(lists("absent", "unreadable")),
      ],
      bindings: bindings(),
      platformStatus: { version: 1, platforms: {} },
      now: NOW,
    });
    expect(counted.platforms[0]).toMatchObject({
      valid: 3,
      citations: { read: 2, absent: 0, unreadable: 1, not_declared: 0 },
    });
    // Neither the lists nor their causes are in the report.
    expect(JSON.stringify(counted)).not.toContain("cited");
    expect(JSON.stringify(counted)).not.toContain("source_list_empty");
  });
  // Task 11-24: links read by opening each source.
  it("counts the links of the sources that were opened, by outcome and cause", () => {
    const linked = (url: string | null, link: AppRecord["citations"][number]["link"]) => ({
      title: "合成来源",
      site: null,
      url,
      ...(link ? { link } : {}),
    });
    const sample = (citations: AppRecord["citations"], overrides: Partial<AppRecord> = {}) =>
      record({
        platformCode: "doubao",
        questionText: QUESTIONS[0]!,
        citations,
        citationsStatus: "read",
        ...overrides,
      });
    const counted = buildReport({
      batchId: "batch-1",
      batch: batch(["doubao"]),
      records: [
        sample([
          linked("https://example.com/1", {
            copied: "https://example.com/1?theme=light",
            restored: true,
          }),
          linked("https://link.example.cn/x", {
            copied: "https://link.example.cn/x",
            restored: false,
          }),
          linked(null, { issue: "page_not_opened" }),
          linked(null, { issue: "time_budget" }),
          // Android's share sheet not declared; another App opened (task 11-24 §11.1).
          linked(null, { issue: "share_external" }),
          linked(null, { issue: "left_app" }),
        ]),
        sample([linked(null, { issue: "time_budget" })]),
        // Read without opening the sources (a contract without links, or an older record).
        sample([linked(null, undefined)]),
        // Not a valid sample: not counted.
        sample([linked(null, { issue: "page_not_closed" })], {
          status: "evidence_incomplete",
          phase: "post_send",
          evidenceIssue: "screenshot_failed",
          screenshot: null,
        }),
      ],
      bindings: bindings(),
      platformStatus: { version: 1, platforms: {} },
      now: NOW,
    });
    expect(counted.platforms[0]!.links).toEqual({
      sources: 7,
      withUrl: 2,
      notRestored: 1,
      issues: {
        page_not_opened: 1,
        share_missing: 0,
        copy_missing: 0,
        clipboard_unchanged: 0,
        not_a_link: 0,
        page_not_closed: 0,
        not_attempted: 0,
        not_reached: 0,
        time_budget: 2,
        row_not_found: 0,
        share_external: 1,
        left_app: 1,
      },
    });
    // A platform without such records counts nothing.
    expect(counted.platforms[1]!.links).toMatchObject({ sources: 0, withUrl: 0, notRestored: 0 });
    // Counts and cause words only: no title and no link.
    const text = JSON.stringify(counted);
    expect(text).not.toContain("合成来源");
    expect(text).not.toContain("example");
  });
  it("counts per binding, including bindings without any attempt", () => {
    const row = (device: string, platform: string) =>
      report.bindings.find((item) => item.device === device && item.platform === platform);
    expect(report.bindings).toHaveLength(6);
    expect(row(DEVICE_A, "doubao")).toEqual({
      device: DEVICE_A,
      platform: "doubao",
      accountLabel: "doubao-a",
      enabled: true,
      attempts: 3,
      valid: 1,
      failed: { collection_timeout: 1 },
      evidenceIncomplete: 1,
    });
    expect(row(DEVICE_B, "doubao")).toMatchObject({ attempts: 1, valid: 1, failed: {} });
    expect(row(DEVICE_B, "ernie")).toEqual({
      device: DEVICE_B,
      platform: "ernie",
      accountLabel: "ernie-b",
      enabled: false,
      disabledReason: "chat content is not readable",
      attempts: 0,
      valid: 0,
      failed: {},
      evidenceIncomplete: 0,
    });
  });
  // AC-A12: a conclusion per platform with App version, image facts and reasons.
  it("concludes usable, unavailable or not verified per platform with version, image and reasons", () => {
    const image = {
      androidVersion: "15",
      abi: "arm64-v8a",
      pageSize: "4k",
      locale: "zh-Hans-CN",
      timezone: "Asia/Shanghai",
    };
    expect(report.platforms[0]).toMatchObject({
      conclusion: "usable",
      appVersions: ["15.7.0"],
      devices: [
        { id: DEVICE_A, ...image },
        { id: DEVICE_B, ...image },
      ],
      reasons: ["collection_timeout", "evidence_incomplete"],
    });
    expect(report.platforms[1]).toMatchObject({
      conclusion: "not_verified",
      appVersions: ["3.1.8"],
      reasons: ["app_contract_changed"],
    });
    // Every binding disabled: unavailable, with the recorded reason and the reviewed version.
    expect(report.platforms[2]).toMatchObject({
      conclusion: "unavailable",
      appVersions: ["5.22.0.10"],
      reasons: ["chat content is not readable"],
    });
  });
  it("concludes unavailable for a platform whose App rejected the emulator", () => {
    const rejected = buildReport({
      batchId: "batch-1",
      batch: batch(["doubao", "kimi"]),
      records: [],
      bindings: bindings(),
      platformStatus: {
        version: 1,
        platforms: {
          kimi: {
            status: "unavailable",
            failureCode: "emulator_rejected",
            since: "2026-11-15T04:10:00.000Z",
            batchId: "batch-0",
            answerId: uuid(999),
          },
        },
      },
      now: NOW,
    });
    expect(rejected.platforms[1]).toMatchObject({
      conclusion: "unavailable",
      reasons: ["emulator_rejected"],
      notCollected: 3,
    });
    expect(rejected.platforms[0]).toMatchObject({
      conclusion: "not_verified",
      reasons: ["no_attempt"],
    });
  });
  it("holds counts, codes and versions only: no question or answer text", () => {
    const text = JSON.stringify(report);
    for (const secret of [...QUESTIONS, "合成回答", "合成来源", "合成前缀"])
      expect(text).not.toContain(secret);
  });
  it("reports an unknown batch as empty instead of failing", () => {
    const empty = buildReport({
      batchId: "batch-9",
      batch: undefined,
      records: [],
      bindings: bindings(),
      platformStatus: { version: 1, platforms: {} },
      now: NOW,
    });
    expect(empty.totals).toMatchObject({ planned: 0, attempts: 0, notCollected: 0 });
  });
});

describe("batch.json and summary.json", () => {
  it("records every run and grows the plan without rewriting earlier runs", async () => {
    const store = await openStore();
    expect(await store.readBatch("batch-1")).toBeUndefined();
    const first = await store.recordBatchRun("batch-1", run, plan(["doubao"]));
    expect(first).toEqual(batch(["doubao"]));
    const later = { ...run, startedAt: "2026-11-15T08:00:00.000Z", scriptRevision: "later" };
    const second = await store.recordBatchRun("batch-1", later, plan(["doubao", "kimi"]));
    expect(second.createdAt).toBe(run.startedAt);
    expect(second.runs).toEqual([run, later]);
    // The doubao cells are not planned twice.
    expect(second.cells).toEqual(plan(["doubao", "kimi"]));
    expect(await store.readBatch("batch-1")).toEqual(second);
    expect(await store.listBatchIds()).toEqual(["batch-1"]);
    // Hashes only: the plan carries no question text.
    const file = await readFile(join(store.root, "batches", "batch-1", "batch.json"), "utf8");
    for (const question of QUESTIONS) expect(file).not.toContain(question);
  });
  it("blocks on a corrupt batch.json instead of replacing it", async () => {
    const store = await openStore();
    await store.recordBatchRun("batch-1", run, plan(["doubao"]));
    await writeFile(join(store.root, "batches", "batch-1", "batch.json"), "{broken");
    await expect(store.recordBatchRun("batch-1", run, [])).rejects.toMatchObject({
      code: "evidence_corrupt",
    });
  });
  it("writes summary.json from the stored batch and records", async () => {
    const store = await openStore();
    await store.recordBatchRun("batch-1", run, plan(["doubao"]));
    const valid = record({ platformCode: "doubao", questionText: QUESTIONS[0]! });
    await store.writeRecord(valid);
    const report = await writeBatchReport(store, "batch-1", bindings(), NOW);
    expect(report.totals).toMatchObject({ planned: 3, valid: 1, notCollected: 2 });
    const path = join(store.root, "batches", "batch-1", "summary.json");
    expect(JSON.parse(await readFile(path, "utf8"))).toEqual(report);
    // Rewritten, not appended, on the next report.
    await writeBatchReport(store, "batch-1", bindings(), NOW);
    expect((await readdir(join(store.root, "batches", "batch-1"))).sort()).toEqual([
      "batch.json",
      "records",
      "summary.json",
    ]);
  });
});

describe("video retention", () => {
  async function seeded() {
    const store = await openStore();
    // A sample with several source lists (design §16.7): the clean-up rewrites its record.
    const old = record({
      video: { ...recordFixture().video!, file: "", stoppedAt: "2026-10-01T00:00:00.000Z" },
      citations: [
        { title: "合成来源", site: null, url: null, list: "cited" },
        { title: "合成来源", site: null, url: null, list: "search" },
      ],
      citationsStatus: "read",
      citationLists: [
        { label: "cited", status: "read", count: 1 },
        { label: "search", status: "read", count: 1 },
      ],
    });
    old.video!.file = `${old.answerId}.mp4`;
    const recent = record({
      batchId: "batch-2",
      video: { ...recordFixture().video!, file: "", stoppedAt: "2026-11-14T00:00:00.000Z" },
    });
    recent.video!.file = `${recent.answerId}.mp4`;
    const withoutVideo = failed({
      failureCode: "recording_failed",
      phase: "pre_send",
      sentAt: null,
      video: null,
      screenshot: null,
      uiDump: null,
    });
    // A valid sample collected with recording switched off: there is nothing to prune.
    const unrecorded = record({ video: null, citationsStatus: "read" });
    for (const item of [old, recent, withoutVideo, unrecorded]) {
      const directory = await store.recordDirectory(item.batchId, item.answerId);
      await store.writeRecord(item);
      if (item.video) await writeFile(join(directory, item.video.file), "synthetic mp4 bytes");
      await writeFile(join(directory, "final.png"), "synthetic png bytes");
    }
    return { store, old, recent, unrecorded };
  }
  const files = async (store: EvidenceStore, item: AppRecord) =>
    (await readdir(join(store.root, "batches", item.batchId, "records", item.answerId))).sort();

  it("deletes videos past the retention period and writes the time into their record", async () => {
    const { store, old, recent, unrecorded } = await seeded();
    const result = await pruneVideos(store, { days: 30, now: NOW });
    expect(result).toEqual({ batches: 2, records: 4, pruned: 1, bytes: old.video!.bytes });
    // A record without a video is left exactly as it was.
    expect(await files(store, unrecorded)).toEqual(["final.png", "record.json"]);
    expect(
      (await store.readRecords("batch-1")).find((item) => item.answerId === unrecorded.answerId),
    ).toEqual(unrecorded);
    // Only the video is gone; record and screenshot stay.
    expect(await files(store, old)).toEqual(["final.png", "record.json"]);
    expect(await files(store, recent)).toEqual([
      `${recent.answerId}.mp4`,
      "final.png",
      "record.json",
    ]);
    const stored = (await store.readRecords("batch-1")).find(
      (item) => item.answerId === old.answerId,
    )!;
    expect(stored.video).toEqual({ ...old.video!, prunedAt: NOW.toISOString() });
    expect(stored.status).toBe("valid");
    // Nothing else of the record changed: its sources still name their lists.
    expect(stored).toEqual({ ...old, video: stored.video });
    expect(stored.citationLists).toHaveLength(2);
    // A second run finds nothing left to do.
    expect(await pruneVideos(store, { days: 30, now: NOW })).toMatchObject({ pruned: 0 });
  });
  it("honours a shorter period and repeats safely after an interruption", async () => {
    const { store, old, recent } = await seeded();
    // An interrupted earlier clean-up removed the file but did not write the record.
    await store.removeVideo(old.batchId, old.answerId);
    const result = await pruneVideos(store, { days: 1, now: NOW });
    expect(result.pruned).toBe(2);
    expect(await files(store, recent)).toEqual(["final.png", "record.json"]);
    for (const batchId of ["batch-1", "batch-2"])
      for (const item of await store.readRecords(batchId))
        if (item.video) expect(item.video.prunedAt).toBe(NOW.toISOString());
  });
});
