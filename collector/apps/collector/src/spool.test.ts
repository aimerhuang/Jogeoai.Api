import { createCipheriv, randomBytes } from "node:crypto";
import { mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { flushSpool, spoolSubmission } from "./spool.ts";
import { createSubmission } from "./platform-adapter.ts";
import { fixtureConfig, fixtureTask } from "./test-fixtures.ts";

const directories: string[] = [];

afterEach(async () => {
  await Promise.all(
    directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })),
  );
});

describe("encrypted collector spool", () => {
  it("retains ciphertext after failed replay and rejects corruption or a wrong key", async () => {
    const directory = await mkdtemp(join(tmpdir(), "aivc-spool-retry-"));
    directories.push(directory);
    const key = Buffer.alloc(32, 7);
    const submission = createSubmission(
      fixtureTask(),
      {
        answerText: "synthetic private fixture",
        citations: [],
        completedAt: new Date().toISOString(),
      },
      fixtureConfig(),
    );
    const path = await spoolSubmission(directory, key, submission);
    await expect(
      flushSpool(directory, key, async () => {
        throw new Error("offline");
      }),
    ).rejects.toThrow("offline");
    expect(await readdir(directory)).toHaveLength(1);
    await expect(flushSpool(directory, Buffer.alloc(32, 8), async () => {})).rejects.toThrow();
    await writeFile(path, "{}");
    await expect(flushSpool(directory, key, async () => {})).rejects.toThrow();
    expect(await readdir(directory)).toHaveLength(1);
  });
  it("does not store answer plaintext and replays the validated submission", async () => {
    const directory = await mkdtemp(join(tmpdir(), "aivc-spool-"));
    directories.push(directory);
    const key = Buffer.alloc(32, 7);
    const submission = {
      tenantId: "11111111-1111-4111-8111-111111111111",
      taskId: "77777777-7777-4777-8777-777777777777",
      nodeId: "test-node",
      region: "cn" as const,
      leaseVersion: 1,
      idempotencyKey: "test-idempotency-key",
      egressCountry: "CN",
      collectorBuildRevision: "test",
      answerText: "sensitive synthetic answer",
      citations: ["https://example.invalid/source"],
      completedAt: new Date().toISOString(),
    };
    await spoolSubmission(directory, key, submission);
    const [file] = await import("node:fs/promises").then(({ readdir }) => readdir(directory));
    expect(await readFile(join(directory, file!), "utf8")).not.toContain(submission.answerText);

    const replayed: unknown[] = [];
    expect(await flushSpool(directory, key, async (value) => void replayed.push(value))).toBe(1);
    expect(replayed).toEqual([submission]);
  });
  it("replays a legacy envelope written before citation titles and search queries existed", async () => {
    const directory = await mkdtemp(join(tmpdir(), "aivc-spool-legacy-"));
    directories.push(directory);
    const key = Buffer.alloc(32, 7);
    // Exact pre-change field set, encrypted the same way the previous build wrote it.
    const legacy = {
      tenantId: "11111111-1111-4111-8111-111111111111",
      taskId: "77777777-7777-4777-8777-777777777777",
      nodeId: "test-node",
      region: "cn",
      leaseVersion: 2,
      idempotencyKey: "legacy-idempotency-key",
      egressCountry: "CN",
      collectorBuildRevision: "legacy-build",
      answerText: "legacy synthetic answer",
      citations: ["https://example.com/legacy"],
      completedAt: "2026-10-20T13:47:00.000Z",
    };
    const iv = randomBytes(12);
    const cipher = createCipheriv("aes-256-gcm", key, iv);
    const ciphertext = Buffer.concat([
      cipher.update(JSON.stringify(legacy), "utf8"),
      cipher.final(),
    ]);
    await writeFile(
      join(directory, `${legacy.taskId}-2-legacy.json.enc`),
      JSON.stringify({
        version: 1,
        iv: iv.toString("base64"),
        authTag: cipher.getAuthTag().toString("base64"),
        ciphertext: ciphertext.toString("base64"),
      }),
      { mode: 0o600 },
    );
    const replayed: unknown[] = [];
    expect(await flushSpool(directory, key, async (value) => void replayed.push(value))).toBe(1);
    expect(replayed).toEqual([legacy]);
    expect(replayed[0]).not.toHaveProperty("citationTitles");
    expect(replayed[0]).not.toHaveProperty("searchQueries");
    expect(await readdir(directory)).toEqual([]);
  });
  it("round-trips citation titles and search queries through the encrypted spool", async () => {
    const directory = await mkdtemp(join(tmpdir(), "aivc-spool-metadata-"));
    directories.push(directory);
    const key = Buffer.alloc(32, 7);
    const submission = createSubmission(
      fixtureTask(),
      {
        answerText: "synthetic answer with sources",
        citations: ["https://example.com/a", "https://example.com/b"],
        citationTitles: ["来源标题 A", null],
        searchQueries: ["合成关键词"],
        completedAt: new Date().toISOString(),
      },
      fixtureConfig(),
    );
    await spoolSubmission(directory, key, submission);
    const replayed: unknown[] = [];
    expect(await flushSpool(directory, key, async (value) => void replayed.push(value))).toBe(1);
    expect(replayed).toEqual([submission]);
  });
});
