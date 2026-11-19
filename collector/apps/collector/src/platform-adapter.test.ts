import { describe, expect, it } from "vitest";
import { createSubmission, mockAdapter } from "./platform-adapter.ts";
import { fixtureConfig, fixtureTask } from "./test-fixtures.ts";
import { buildWebQuestion } from "./web-question.ts";

const base = {
  answerText: "fixture answer",
  citations: ["https://example.com/a", "https://example.com/b"],
  completedAt: "2026-10-21T06:00:00.000Z",
};

describe("result submission metadata", () => {
  it("rejects the question coming back as the answer at the real submission boundary", () => {
    const task = fixtureTask();
    expect(() =>
      createSubmission(
        task,
        { ...base, answerText: ` ${buildWebQuestion(task.questionText)}\n ` },
        { ...fixtureConfig(), COLLECTOR_MODE: "real" },
      ),
    ).toThrow(expect.objectContaining({ code: "page_contract_changed" }));
  });
  it("preserves the original task and answer metadata in real mode", () => {
    const task = Object.freeze(fixtureTask());
    const original = { ...task };
    const submission = createSubmission(
      task,
      { ...base, citationTitles: ["标题 A", null], searchQueries: ["关键词"] },
      { ...fixtureConfig(), COLLECTOR_MODE: "real" },
    );
    expect(task).toEqual(original);
    expect(submission.taskId).toBe(original.id);
    expect(submission.answerText).toBe(base.answerText);
    expect(submission.citations).toEqual(base.citations);
    expect(submission.citationTitles).toEqual(["标题 A", null]);
    expect(submission.searchQueries).toEqual(["关键词"]);
  });
  it("keeps Mock collection on the original question", async () => {
    const task = Object.freeze(fixtureTask());
    const result = await mockAdapter(task.platformCode).collect(task, new AbortController().signal);
    expect(result.answerText).toBe(
      `合成采集回答：${task.questionText}\n平台：${task.platformCode}`,
    );
  });
  it("omits titles and keywords when the adapter does not provide them", () => {
    const submission = createSubmission(fixtureTask(), base, fixtureConfig());
    expect(submission.citations).toEqual(base.citations);
    expect(submission).not.toHaveProperty("citationTitles");
    expect(submission).not.toHaveProperty("searchQueries");
  });
  it("passes aligned titles and keywords through", () => {
    const submission = createSubmission(
      fixtureTask(),
      { ...base, citationTitles: ["标题 A", null], searchQueries: ["关键词一", "关键词二"] },
      fixtureConfig(),
    );
    expect(submission.citationTitles).toEqual(["标题 A", null]);
    expect(submission.searchQueries).toEqual(["关键词一", "关键词二"]);
  });
  it("keeps titles aligned while deduplicating URLs and keeps the first readable title", () => {
    const submission = createSubmission(
      fixtureTask(),
      {
        ...base,
        citations: [
          "https://example.com/a",
          "https://example.com/b",
          "https://example.com/a",
          "https://example.com/c",
          "https://example.com/b",
        ],
        citationTitles: [null, "标题 B", "标题 A", "标题 C", "重复 B"],
      },
      fixtureConfig(),
    );
    expect(submission.citations).toEqual([
      "https://example.com/a",
      "https://example.com/b",
      "https://example.com/c",
    ]);
    expect(submission.citationTitles).toEqual(["标题 A", "标题 B", "标题 C"]);
  });
  it("drops misaligned titles and out-of-range metadata instead of failing the answer", () => {
    const misaligned = createSubmission(
      fixtureTask(),
      { ...base, citationTitles: ["only one"], searchQueries: Array(33).fill("q") },
      fixtureConfig(),
    );
    expect(misaligned.citations).toEqual(base.citations);
    expect(misaligned).not.toHaveProperty("citationTitles");
    expect(misaligned).not.toHaveProperty("searchQueries");
    const invalid = createSubmission(
      fixtureTask(),
      { ...base, citationTitles: ["", "题".repeat(513)], searchQueries: ["ok", ""] },
      fixtureConfig(),
    );
    expect(invalid.citationTitles).toEqual([null, null]);
    expect(invalid).not.toHaveProperty("searchQueries");
  });
  it.each([
    // .NET Trim() also strips U+0085 (JS trim does not); Jogeoai rejects blank keywords with 400.
    ["blank under .NET whitespace rules", ["关键词", "\u0085"]],
    ["only whitespace", ["关键词", " 　\t"]],
    // System.Text.Json fails to bind a lone surrogate escape, rejecting the whole request.
    ["lone surrogate", ["关键词", "坏\ud83d"]],
    // Jogeoai's 64 KB estimate can count up to 6 bytes per UTF-16 unit.
    ["total size beyond the guaranteed 64 KB bound", Array(21).fill("😀".repeat(250))],
  ])("drops keywords Jogeoai could reject but keeps the answer: %s", (_label, searchQueries) => {
    const submission = createSubmission(
      fixtureTask(),
      { ...base, citationTitles: ["标题 A", "标题 B"], searchQueries },
      fixtureConfig(),
    );
    expect(submission).not.toHaveProperty("searchQueries");
    expect(submission.citations).toEqual(base.citations);
    expect(submission.citationTitles).toEqual(["标题 A", "标题 B"]);
  });
  it("keeps realistic keyword lists up to the guaranteed size bound", () => {
    const widest = Array(20).fill("😀".repeat(250));
    expect(
      createSubmission(fixtureTask(), { ...base, searchQueries: widest }, fixtureConfig())
        .searchQueries,
    ).toEqual(widest);
  });
  it("nulls a title with a lone surrogate instead of sending an unbindable payload", () => {
    const submission = createSubmission(
      fixtureTask(),
      { ...base, citationTitles: ["坏\udc00", "  "] },
      fixtureConfig(),
    );
    // Whitespace-only titles are accepted by Jogeoai and stored as null.
    expect(submission.citationTitles).toEqual([null, "  "]);
  });
  it("keeps an empty keyword list (explicitly no keywords)", () => {
    expect(
      createSubmission(fixtureTask(), { ...base, searchQueries: [] }, fixtureConfig())
        .searchQueries,
    ).toEqual([]);
  });
  it("does not change the idempotency key when only titles or keywords differ", () => {
    const plain = createSubmission(fixtureTask(), base, fixtureConfig());
    const enriched = createSubmission(
      fixtureTask(),
      { ...base, citationTitles: ["标题 A", "标题 B"], searchQueries: ["关键词"] },
      fixtureConfig(),
    );
    expect(enriched.idempotencyKey).toBe(plain.idempotencyKey);
  });
});
