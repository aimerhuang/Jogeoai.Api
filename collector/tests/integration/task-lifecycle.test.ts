import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

import type { LeaseRequest, ResultSubmission } from "@aivc/contracts";
import {
  FIXTURE_IDS,
  PLATFORM_FIXTURES,
  createDatabase,
  countTenantRows,
  generateDailyTasks,
  heartbeatLease,
  leaseTasks,
  migrateDatabase,
  persistDailyBrandMetricRollup,
  resetDatabaseForTests,
  seedFixture,
  submitTaskResult,
} from "@aivc/database";
import type { DomainError } from "@aivc/domain";
import { afterAll, beforeAll, beforeEach, describe, expect, it } from "vitest";

const databaseUrl =
  process.env.DATABASE_URL ??
  "postgres://ai_visibility:local-development-only@localhost:55432/ai_visibility";
const database = createDatabase(databaseUrl);
const scheduledDate = "2026-09-01";

function leaseRequest(overrides: Partial<LeaseRequest> = {}): LeaseRequest {
  return {
    tenantId: FIXTURE_IDS.tenantId,
    nodeId: "integration-cn",
    region: "cn",
    supportedPlatformCodes: PLATFORM_FIXTURES.map((platform) => platform.code),
    capacity: 1,
    leaseSeconds: 60,
    ...overrides,
  };
}

function submissionFor(
  task: Awaited<ReturnType<typeof leaseTasks>>[number],
  overrides: Partial<ResultSubmission> = {},
): ResultSubmission {
  return {
    tenantId: task.tenantId,
    taskId: task.id,
    nodeId: task.region === "cn" ? "integration-cn" : "integration-overseas",
    region: task.region,
    leaseVersion: task.leaseVersion,
    idempotencyKey: `result-${task.id}-${task.leaseVersion}`,
    egressCountry: task.region === "cn" ? "CN" : "JP",
    collectorBuildRevision: "integration-test",
    answerText: `Synthetic answer for ${task.questionText}`,
    citations: ["https://example.invalid/source"],
    completedAt: new Date().toISOString(),
    ...overrides,
  };
}

async function createParsedPrimaryBrandFact(): Promise<{
  task: Awaited<ReturnType<typeof leaseTasks>>[number];
  sampleId: string;
  parseId: string;
  brandFactId: string;
  competitorBrandFactId: string;
  questionId: string;
  topicGroupId: string;
}> {
  const [task] = await leaseTasks(database, leaseRequest());
  if (!task) {
    throw new Error("Expected a task lease for analytics fixture setup");
  }
  const submission = await submitTaskResult(
    database,
    submissionFor(task, {
      answerText: "合成竞品也可使用，但合成主品牌是第二个推荐品牌。",
      citations: ["https://primary-brand.invalid/article"],
    }),
  );
  if (!submission.sampleId) {
    throw new Error("Expected an accepted sample for analytics fixture setup");
  }
  const sampleId = submission.sampleId;

  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
    const dimensions = await transaction<{ question_id: string; topic_group_id: string }[]>`
      SELECT qv.question_id, qv.topic_group_id
      FROM question_versions qv
      WHERE qv.tenant_id = ${FIXTURE_IDS.tenantId}
        AND qv.id = ${task.questionVersionId}
    `;
    const parses = await transaction<{ id: string }[]>`
      INSERT INTO answer_parses (
        tenant_id, answer_sample_id, parser_version_id,
        brand_rule_set_hash, status
      ) VALUES (
        ${FIXTURE_IDS.tenantId}, ${sampleId}, ${FIXTURE_IDS.parserVersionId},
        'fixture-rule-set-v1', 'completed'
      )
      RETURNING id
    `;
    const parseId = parses[0]?.id;
    if (!parseId) {
      throw new Error("Expected an answer parse");
    }
    const facts = await transaction<{ brand_id: string; id: string }[]>`
      INSERT INTO answer_brand_facts (
        tenant_id, answer_parse_id, answer_sample_id, brand_id, brand_rule_version_id,
        mentioned, mention_count, first_mention_offset, first_mention_rank,
        recommendation_role, sentiment, sentiment_confidence
      ) VALUES
        (
          ${FIXTURE_IDS.tenantId}, ${parseId}, ${sampleId},
          ${FIXTURE_IDS.primaryBrandId}, ${FIXTURE_IDS.primaryBrandRuleVersionId},
          true, 2, 8, 2, 'recommended', 'positive', 0.95
        ),
        (
          ${FIXTURE_IDS.tenantId}, ${parseId}, ${sampleId},
          ${FIXTURE_IDS.competitorBrandId}, ${FIXTURE_IDS.competitorBrandRuleVersionId},
          true, 3, 0, 1, 'alternative', 'neutral', 0.90
        )
      RETURNING id, brand_id
    `;
    const brandFactId = facts.find((fact) => fact.brand_id === FIXTURE_IDS.primaryBrandId)?.id;
    const competitorBrandFactId = facts.find(
      (fact) => fact.brand_id === FIXTURE_IDS.competitorBrandId,
    )?.id;
    const dimension = dimensions[0];
    if (!brandFactId || !competitorBrandFactId || !dimension) {
      throw new Error("Expected brand fact dimensions");
    }

    await transaction`
      INSERT INTO brand_mention_occurrences (
        tenant_id, brand_fact_id, occurrence_index, matched_text,
        character_start, character_end, context_snippet
      ) VALUES (
        ${FIXTURE_IDS.tenantId}, ${brandFactId}, 1, '合成主品牌', 8, 13,
        '合成主品牌是第二个推荐品牌'
      )
    `;

    return {
      task,
      sampleId,
      parseId,
      brandFactId,
      competitorBrandFactId,
      questionId: dimension.question_id,
      topicGroupId: dimension.topic_group_id,
    };
  });
}

async function createPrimaryBrandCitation(sampleId: string): Promise<void> {
  await database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
    const domains = await transaction<{ id: string }[]>`
      INSERT INTO citation_domains (
        tenant_id, canonical_host, registrable_domain, source_category
      ) VALUES (
        ${FIXTURE_IDS.tenantId}, 'primary-brand.invalid',
        'primary-brand.invalid', 'owned'
      )
      RETURNING id
    `;
    const urls = await transaction<{ id: string }[]>`
      INSERT INTO normalized_urls (
        tenant_id, citation_domain_id, url_hash, canonical_url, scheme, host,
        registrable_domain, path
      ) VALUES (
        ${FIXTURE_IDS.tenantId}, ${domains[0]!.id}, 'fixture-primary-citation-url-v1',
        'https://primary-brand.invalid/article', 'https',
        'primary-brand.invalid', 'primary-brand.invalid', '/article'
      )
      RETURNING id
    `;
    await transaction`
      INSERT INTO citation_articles (
        tenant_id, normalized_url_id, citation_domain_id, title, source_type
      ) VALUES (
        ${FIXTURE_IDS.tenantId}, ${urls[0]!.id}, ${domains[0]!.id},
        '合成品牌资料', 'article'
      )
    `;
    await transaction`
      INSERT INTO answer_citations (
        tenant_id, answer_sample_id, normalized_url_id, citation_index,
        original_url, title, source_label
      ) VALUES (
        ${FIXTURE_IDS.tenantId}, ${sampleId}, ${urls[0]!.id}, 1,
        'https://primary-brand.invalid/article', '合成品牌资料', '品牌官网'
      )
    `;
  });
}

beforeAll(async () => {
  const concurrentMigrator = createDatabase(databaseUrl, 1);
  try {
    await Promise.all([migrateDatabase(database), migrateDatabase(concurrentMigrator)]);
  } finally {
    await concurrentMigrator.end();
  }
});

beforeEach(async () => {
  await resetDatabaseForTests(database);
  await seedFixture(database);
  await generateDailyTasks(database, {
    tenantId: FIXTURE_IDS.tenantId,
    brandProjectId: FIXTURE_IDS.brandProjectId,
    scheduledDate,
  });
});

afterAll(async () => {
  await database.end();
});

describe("database migration compatibility", () => {
  it("orders data backfills before immutable and not-null guards", async () => {
    const migrationDirectory = resolve(process.cwd(), "packages/database/migrations");
    const [analyticsMigration, compatibilityMigration] = await Promise.all([
      readFile(resolve(migrationDirectory, "0003_brand_geo_analytics.sql"), "utf8"),
      readFile(resolve(migrationDirectory, "0004_brand_geo_analytics_upgrade.sql"), "utf8"),
    ]);

    expect(analyticsMigration.indexOf("INSERT INTO brand_rule_versions")).toBeGreaterThan(-1);
    expect(
      analyticsMigration.indexOf("ALTER TABLE brands FORCE ROW LEVEL SECURITY"),
    ).toBeGreaterThan(analyticsMigration.indexOf("INSERT INTO brand_match_rules"));
    expect(analyticsMigration.indexOf("UPDATE question_versions AS version")).toBeLessThan(
      analyticsMigration.indexOf("ALTER COLUMN brand_project_id SET NOT NULL"),
    );
    expect(compatibilityMigration.indexOf("UPDATE brand_domains AS domain")).toBeLessThan(
      compatibilityMigration.indexOf("ALTER COLUMN brand_rule_version_id SET NOT NULL"),
    );
  });
});

describe("central task lifecycle", () => {
  it("serializes concurrent migration runners", async () => {
    const migrations = await database<{ version: string }[]>`
      SELECT version FROM schema_migrations ORDER BY version
    `;
    expect(migrations.map((migration) => migration.version)).toEqual([
      "0001_initial.sql",
      "0002_tenant_integrity.sql",
      "0003_brand_geo_analytics.sql",
      "0004_brand_geo_analytics_upgrade.sql",
      "0005_brand_geo_analytics_constraints.sql",
      "0006_brand_geo_lineage_guards.sql",
      "0007_brand_geo_task_fact_guards.sql",
      "0008_brand_geo_schema_convergence.sql",
      "0009_brand_geo_historical_dimensions.sql",
      "0010_brand_geo_effective_correction_lineage.sql",
      "0011_brand_geo_citation_and_metric_version_guards.sql",
      "0012_collection_evidence_immutability.sql",
      "0013_remove_duplicate_metric_active_index.sql",
      "0014_brand_geo_audit_completion.sql",
      "0015_brand_geo_upgrade_index_convergence.sql",
      "0016_session_profiles_and_challenge_cooldown.sql",
    ]);
  });

  it("seeds ten stable platforms and creates exactly 300 idempotent tasks", async () => {
    const repeated = await generateDailyTasks(database, {
      tenantId: FIXTURE_IDS.tenantId,
      brandProjectId: FIXTURE_IDS.brandProjectId,
      scheduledDate,
    });
    expect(repeated).toEqual({ planned: 300, inserted: 0 });
    expect((await countTenantRows(database, FIXTURE_IDS.tenantId)).tasks).toBe(300);

    const platformCounts = await database<{ platforms: number; entry_versions: number }[]>`
      SELECT
        (SELECT count(*)::integer FROM platforms WHERE enabled = true) AS platforms,
        (SELECT count(*)::integer FROM platform_entry_versions) AS entry_versions
    `;
    const questionAudit = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<
        {
          question_version_id: string;
          text_count: number;
          platform_count: number;
          question_text: string;
        }[]
      >`
        SELECT
          question_version_id,
          count(DISTINCT question_text)::integer AS text_count,
          count(DISTINCT platform_code)::integer AS platform_count,
          min(question_text) AS question_text
        FROM collection_tasks
        GROUP BY question_version_id
        ORDER BY question_version_id
      `;
    });
    expect(platformCounts[0]).toEqual({ platforms: 10, entry_versions: 10 });
    expect(questionAudit).toHaveLength(30);
    expect(questionAudit.every((row) => row.text_count === 1 && row.platform_count === 10)).toBe(
      true,
    );
    expect(questionAudit.every((row) => row.question_text.startsWith("【合成测试】"))).toBe(true);

    const taskVersionAudit = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<{ count: number }[]>`
        SELECT count(*)::integer AS count
        FROM collection_tasks AS task
        JOIN platform_entry_versions AS version
          ON version.id = task.platform_entry_version_id
         AND version.platform_entry_code = task.platform_entry_code
         AND version.version = task.platform_entry_version
        WHERE task.tenant_id = ${FIXTURE_IDS.tenantId}
      `;
    });
    expect(taskVersionAudit[0]?.count).toBe(300);
  });

  it("rejects a 31st active question for the same brand project", async () => {
    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        await transaction`
          INSERT INTO questions (id, tenant_id, brand_project_id, topic_group_id, status, fixture)
          VALUES (
            ${randomUUID()}, ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.brandProjectId},
            '30000000-0000-4000-8000-000000000001', 'active', true
          )
        `;
      }),
    ).rejects.toThrow("at most 30 active questions");
  });

  it("keeps question version text and dimension snapshots immutable", async () => {
    const snapshotBeforeRename = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<{ id: string; topic_group_id: string; topic_group_name: string }[]>`
        SELECT id, topic_group_id, topic_group_name
        FROM question_versions
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
        ORDER BY id
        LIMIT 1
      `;
    });
    await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      await transaction`
        UPDATE topic_groups
        SET name = 'Renamed live topic group'
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
          AND id = ${snapshotBeforeRename[0]!.topic_group_id}
      `;
    });
    const snapshotAfterRename = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<{ topic_group_name: string }[]>`
        SELECT topic_group_name
        FROM question_versions
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
          AND id = ${snapshotBeforeRename[0]!.id}
      `;
    });
    expect(snapshotAfterRename[0]?.topic_group_name).toBe(
      snapshotBeforeRename[0]?.topic_group_name,
    );

    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        await transaction`
          UPDATE question_versions
          SET question_text = 'Mutated historical question'
          WHERE tenant_id = ${FIXTURE_IDS.tenantId}
            AND id = (
              SELECT id FROM question_versions
              WHERE tenant_id = ${FIXTURE_IDS.tenantId}
              ORDER BY id
              LIMIT 1
            )
        `;
      }),
    ).rejects.toThrow("Question version snapshots are immutable");
  });

  it("snapshots auxiliary tag labels on each question version assignment", async () => {
    const tagId = randomUUID();
    const questionVersionId = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      const versions = await transaction<{ id: string }[]>`
        SELECT id
        FROM question_versions
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
        ORDER BY id
        LIMIT 1
      `;
      await transaction`
        INSERT INTO question_tags (
          id, tenant_id, brand_project_id, tag_type, name, status
        ) VALUES (
          ${tagId}, ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.brandProjectId},
          'intent', '购买决策', 'active'
        )
      `;
      await transaction`
        INSERT INTO question_tag_assignments (
          tenant_id, brand_project_id, question_version_id, question_tag_id
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.brandProjectId},
          ${versions[0]!.id}, ${tagId}
        )
      `;
      return versions[0]!.id;
    });

    await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      await transaction`
        UPDATE question_tags
        SET name = '已改名购买意图', status = 'archived'
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
          AND id = ${tagId}
      `;
    });

    const snapshots = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<
        { tag_name_snapshot: string; tag_status_snapshot: string; tag_type_snapshot: string }[]
      >`
        SELECT tag_type_snapshot, tag_name_snapshot, tag_status_snapshot
        FROM question_tag_assignments
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
          AND question_version_id = ${questionVersionId}
          AND question_tag_id = ${tagId}
      `;
    });
    expect(snapshots[0]).toEqual({
      tag_type_snapshot: "intent",
      tag_name_snapshot: "购买决策",
      tag_status_snapshot: "active",
    });
  });

  it("pins each task to an immutable platform entry version", async () => {
    const entryCode = "doubao.consumer_web";
    const oldTasks = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<{ version: number; version_id: string }[]>`
        SELECT DISTINCT
          platform_entry_version AS version,
          platform_entry_version_id AS version_id
        FROM collection_tasks
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
          AND platform_entry_code = ${entryCode}
      `;
    });
    expect(oldTasks).toHaveLength(1);
    expect(oldTasks[0]?.version).toBe(1);

    await database.begin(async (transaction) => {
      await transaction`
        INSERT INTO platform_entry_versions (
          platform_entry_code, platform_code, version, display_name, region, channel,
          config
        ) VALUES (
          ${entryCode}, 'doubao', 2, '豆包消费者 Web v2', 'cn', 'consumer_web',
          '{"mode":"web-search"}'::jsonb
        )
      `;
      await transaction`
        UPDATE platform_entries
        SET version = 2, display_name = '豆包消费者 Web v2'
        WHERE code = ${entryCode}
      `;
    });

    await generateDailyTasks(database, {
      tenantId: FIXTURE_IDS.tenantId,
      brandProjectId: FIXTURE_IDS.brandProjectId,
      scheduledDate: "2026-09-02",
    });
    const versionAudit = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<{ scheduled_date: string; version: number; version_count: number }[]>`
        SELECT
          scheduled_date::text,
          min(platform_entry_version)::integer AS version,
          count(DISTINCT platform_entry_version_id)::integer AS version_count
        FROM collection_tasks
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
          AND platform_entry_code = ${entryCode}
        GROUP BY scheduled_date
        ORDER BY scheduled_date
      `;
    });
    expect(versionAudit).toEqual([
      { scheduled_date: scheduledDate, version: 1, version_count: 1 },
      { scheduled_date: "2026-09-02", version: 2, version_count: 1 },
    ]);

    await seedFixture(database);
    const currentEntry = await database<{ display_name: string; version: number }[]>`
      SELECT display_name, version
      FROM platform_entries
      WHERE code = ${entryCode}
    `;
    expect(currentEntry[0]).toEqual({ display_name: "豆包消费者 Web v2", version: 2 });

    await expect(
      database`UPDATE platform_entry_versions SET display_name = 'mutated' WHERE id = ${oldTasks[0]!.version_id}`,
    ).rejects.toThrow("immutable");
  });

  it("serializes concurrent inserts at the active question limit", async () => {
    const projectId = "88888888-8888-4888-8888-888888888881";
    const topicGroupId = "88888888-8888-4888-8888-888888888882";
    await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      await transaction`
        INSERT INTO brand_projects (id, tenant_id, name, fixture)
        VALUES (${projectId}, ${FIXTURE_IDS.tenantId}, 'Concurrent Limit Project', true)
      `;
      await transaction`
        INSERT INTO topic_groups (id, tenant_id, brand_project_id, name, fixture)
        VALUES (${topicGroupId}, ${FIXTURE_IDS.tenantId}, ${projectId}, 'Concurrent Limit Group', true)
      `;
      for (let index = 0; index < 29; index += 1) {
        await transaction`
          INSERT INTO questions (id, tenant_id, brand_project_id, topic_group_id, status, fixture)
          VALUES (${randomUUID()}, ${FIXTURE_IDS.tenantId}, ${projectId}, ${topicGroupId}, 'active', true)
        `;
      }
    });

    const insertQuestion = () =>
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        await transaction`
          INSERT INTO questions (id, tenant_id, brand_project_id, topic_group_id, status, fixture)
          VALUES (${randomUUID()}, ${FIXTURE_IDS.tenantId}, ${projectId}, ${topicGroupId}, 'active', true)
        `;
      });
    const results = await Promise.allSettled([insertQuestion(), insertQuestion()]);
    expect(results.filter((result) => result.status === "fulfilled")).toHaveLength(1);
    expect(results.filter((result) => result.status === "rejected")).toHaveLength(1);

    const activeQuestions = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<{ count: number }[]>`
        SELECT count(*)::integer AS count
        FROM questions
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
          AND brand_project_id = ${projectId}
          AND status = 'active'
      `;
    });
    expect(activeQuestions[0]?.count).toBe(30);
  });

  it("routes leases strictly by collector region and supported platform", async () => {
    const impossibleCrossRegion = await leaseTasks(
      database,
      leaseRequest({ supportedPlatformCodes: ["chatgpt"] }),
    );
    expect(impossibleCrossRegion).toEqual([]);

    const cnTasks = await leaseTasks(database, leaseRequest({ capacity: 20 }));
    expect(cnTasks).toHaveLength(20);
    expect(cnTasks.every((task) => task.region === "cn")).toBe(true);

    const overseasTasks = await leaseTasks(
      database,
      leaseRequest({
        nodeId: "integration-overseas",
        region: "overseas",
        supportedPlatformCodes: ["chatgpt", "claude", "gemini"],
        capacity: 20,
      }),
    );
    expect(overseasTasks).toHaveLength(20);
    expect(overseasTasks.every((task) => task.region === "overseas")).toBe(true);
  });

  it("reclaims an expired lease with a monotonic version", async () => {
    const [first] = await leaseTasks(database, leaseRequest());
    expect(first).toBeDefined();
    await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      await transaction`
        UPDATE collection_tasks
        SET lease_expires_at = now() - interval '1 second', queued_at = timestamptz '2000-01-01 00:00:00Z'
        WHERE id = ${first!.id}
      `;
    });
    const [reclaimed] = await leaseTasks(
      database,
      leaseRequest({ nodeId: "integration-cn-recovery" }),
    );
    expect(reclaimed?.id).toBe(first!.id);
    expect(reclaimed?.leaseVersion).toBe(first!.leaseVersion + 1);
  });

  it("does not renew a lease through credentials bound to the other region", async () => {
    const [task] = await leaseTasks(database, leaseRequest());
    await expect(
      heartbeatLease(database, {
        tenantId: task!.tenantId,
        taskId: task!.id,
        nodeId: "integration-cn",
        region: "overseas",
        leaseVersion: task!.leaseVersion,
        extendSeconds: 60,
      }),
    ).rejects.toMatchObject({ code: "STALE_LEASE" } satisfies Partial<DomainError>);
  });

  it("keeps one formal sample across idempotent duplicate submissions", async () => {
    const [task] = await leaseTasks(database, leaseRequest());
    const submission = submissionFor(task!);
    const accepted = await submitTaskResult(database, submission);
    expect(accepted).toMatchObject({ disposition: "accepted" });
    expect(await submitTaskResult(database, submission)).toMatchObject({
      disposition: "duplicate",
    });
    expect((await countTenantRows(database, FIXTURE_IDS.tenantId)).samples).toBe(1);

    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        await transaction`
          UPDATE raw_answers
          SET body = 'Mutated accepted answer'
          WHERE tenant_id = ${FIXTURE_IDS.tenantId}
            AND answer_sample_id = ${accepted.sampleId}
        `;
      }),
    ).rejects.toThrow("immutable");

    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        await transaction`
          UPDATE answer_samples
          SET collector_build_revision = 'mutated'
          WHERE tenant_id = ${FIXTURE_IDS.tenantId}
            AND id = ${accepted.sampleId}
        `;
      }),
    ).rejects.toThrow("immutable");
  });

  it("audits an old lease as stale without letting it overwrite the reclaimed result", async () => {
    const [oldLease] = await leaseTasks(database, leaseRequest());
    await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      await transaction`
        UPDATE collection_tasks
        SET lease_expires_at = now() - interval '1 second', queued_at = timestamptz '2000-01-01 00:00:00Z'
        WHERE id = ${oldLease!.id}
      `;
    });
    const [newLease] = await leaseTasks(
      database,
      leaseRequest({ nodeId: "integration-cn-recovery" }),
    );
    expect(newLease?.id).toBe(oldLease!.id);

    expect(await submitTaskResult(database, submissionFor(oldLease!))).toEqual({
      disposition: "stale",
      sampleId: null,
    });
    expect(
      await submitTaskResult(
        database,
        submissionFor(newLease!, { nodeId: "integration-cn-recovery" }),
      ),
    ).toMatchObject({ disposition: "accepted" });
    expect(await countTenantRows(database, FIXTURE_IDS.tenantId)).toEqual({
      tasks: 300,
      samples: 1,
      staleSubmissions: 1,
    });
  });

  it("hides task rows and submissions from a different tenant context", async () => {
    const [task] = await leaseTasks(database, leaseRequest());
    const otherTenantId = "99999999-9999-4999-8999-999999999999";
    const invisibleRows = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${otherTenantId}, true)`;
      await transaction`
        INSERT INTO tenants (id, slug, name)
        VALUES (${otherTenantId}, 'other-tenant', 'Other Tenant')
      `;
      return transaction<{ count: number }[]>`
        SELECT count(*)::integer AS count FROM collection_tasks
      `;
    });
    expect(invisibleRows[0]?.count).toBe(0);

    await expect(
      submitTaskResult(database, submissionFor(task!, { tenantId: otherTenantId })),
    ).rejects.toMatchObject({ code: "TASK_NOT_FOUND" } satisfies Partial<DomainError>);
  });

  it("rejects tenant-scoped rows that reference another tenant's project", async () => {
    const otherTenantId = "99999999-9999-4999-8999-999999999998";
    const otherProjectId = "99999999-9999-4999-8999-999999999997";
    await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${otherTenantId}, true)`;
      await transaction`
        INSERT INTO tenants (id, slug, name)
        VALUES (${otherTenantId}, 'foreign-reference-tenant', 'Foreign Reference Tenant')
      `;
      await transaction`
        INSERT INTO brand_projects (id, tenant_id, name, fixture)
        VALUES (${otherProjectId}, ${otherTenantId}, 'Foreign Reference Project', true)
      `;
    });

    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        await transaction`
          INSERT INTO brands (
            id, tenant_id, brand_project_id, canonical_name, kind, aliases, exclusion_terms
          ) VALUES (
            ${randomUUID()}, ${FIXTURE_IDS.tenantId}, ${otherProjectId},
            'Cross Tenant Brand', 'competitor', '[]'::jsonb, '[]'::jsonb
          )
        `;
      }),
    ).rejects.toThrow();
  });
});

describe("brand GEO analytics lineage", () => {
  it("persists formula-versioned observations and sample memberships idempotently", async () => {
    const fixture = await createParsedPrimaryBrandFact();
    await createPrimaryBrandCitation(fixture.sampleId);
    const appliedCorrections = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<{ id: string }[]>`
        INSERT INTO brand_fact_corrections (
          tenant_id, brand_fact_id, corrected_by_user_id, field_name,
          previous_value, corrected_value, reason
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${fixture.brandFactId}, ${FIXTURE_IDS.adminUserId},
          'sentiment', '"positive"'::jsonb, '"neutral"'::jsonb,
          '指标计算前人工复核'
        )
        RETURNING id
      `;
    });
    const input = {
      tenantId: FIXTURE_IDS.tenantId,
      brandProjectId: FIXTURE_IDS.brandProjectId,
      scheduledDate,
      brandId: FIXTURE_IDS.primaryBrandId,
      brandRuleVersionId: FIXTURE_IDS.primaryBrandRuleVersionId,
      questionId: fixture.questionId,
      questionVersionId: fixture.task.questionVersionId,
      topicGroupId: fixture.topicGroupId,
      platformEntryCode: fixture.task.platformEntryCode,
      platformEntryVersionId: fixture.task.platformEntryVersionId,
      platformEntryVersion: fixture.task.platformEntryVersion,
      platformCode: fixture.task.platformCode,
      region: fixture.task.region,
      language: fixture.task.language,
      channel: fixture.task.channel,
      parserVersionId: FIXTURE_IDS.parserVersionId,
      memberships: [
        {
          taskId: fixture.task.id,
          answerSampleId: fixture.sampleId,
          brandFactId: fixture.brandFactId,
          appliedCorrectionIds: [appliedCorrections[0]!.id],
          shareOfVoiceFacts: [
            {
              brandId: FIXTURE_IDS.primaryBrandId,
              brandFactId: fixture.brandFactId,
              brandRuleVersionId: FIXTURE_IDS.primaryBrandRuleVersionId,
              mentionCount: 2,
            },
            {
              brandId: FIXTURE_IDS.competitorBrandId,
              brandFactId: fixture.competitorBrandFactId,
              brandRuleVersionId: FIXTURE_IDS.competitorBrandRuleVersionId,
              mentionCount: 3,
            },
          ],
          inclusionStatus: "valid_mentioned" as const,
          contribution: {
            mentionCount: 2,
            firstRank: 2,
            cited: true,
            sentiment: "neutral" as const,
          },
        },
      ],
    };

    const first = await persistDailyBrandMetricRollup(database, input);
    const repeated = await persistDailyBrandMetricRollup(database, input);
    expect(repeated).toEqual(first);
    expect(first.observationCount).toBe(10);

    const observations = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<
        {
          metric_code: string;
          numerator: number;
          denominator: number | null;
          value: number | null;
          version: number;
        }[]
      >`
        SELECT
          mv.metric_code,
          observation.numerator::double precision AS numerator,
          observation.denominator::double precision AS denominator,
          observation.value::double precision AS value,
          mv.version
        FROM brand_metric_observations observation
        JOIN metric_versions mv ON mv.id = observation.metric_version_id
        WHERE observation.tenant_id = ${FIXTURE_IDS.tenantId}
          AND observation.rollup_id = ${first.rollupId}
        ORDER BY mv.metric_code
      `;
    });
    expect(observations).toHaveLength(10);
    expect(observations.every((observation) => observation.version === 1)).toBe(true);
    expect(observations.find((row) => row.metric_code === "mention_rate")).toMatchObject({
      numerator: 1,
      denominator: 1,
      value: 1,
    });
    expect(observations.find((row) => row.metric_code === "average_first_rank")?.value).toBe(2);
    expect(observations.find((row) => row.metric_code === "share_of_voice")?.value).toBe(0.4);

    const persistedCounts = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<
        {
          rollups: number;
          observations: number;
          memberships: number;
          corrections: number;
          sovFacts: number;
        }[]
      >`
        SELECT
          (SELECT count(*)::integer FROM daily_brand_metric_rollups) AS rollups,
          (SELECT count(*)::integer FROM brand_metric_observations) AS observations,
          (SELECT count(*)::integer FROM daily_brand_metric_memberships) AS memberships,
          (SELECT count(*)::integer FROM brand_metric_membership_corrections) AS corrections,
          (SELECT count(*)::integer FROM brand_metric_sov_contributions) AS "sovFacts"
      `;
    });
    expect(persistedCounts[0]).toEqual({
      rollups: 1,
      observations: 10,
      memberships: 1,
      corrections: 1,
      sovFacts: 2,
    });

    await expect(
      persistDailyBrandMetricRollup(database, {
        ...input,
        memberships: [
          {
            ...input.memberships[0]!,
            shareOfVoiceFacts: [input.memberships[0]!.shareOfVoiceFacts[0]!],
          },
        ],
      }),
    ).rejects.toThrow("must contain every brand fact");

    await expect(
      persistDailyBrandMetricRollup(database, {
        ...input,
        memberships: [
          {
            ...input.memberships[0]!,
            shareOfVoiceFacts: input.memberships[0]!.shareOfVoiceFacts.map((fact) =>
              fact.brandId === FIXTURE_IDS.competitorBrandId ? { ...fact, mentionCount: 4 } : fact,
            ),
          },
        ],
      }),
    ).rejects.toThrow("does not match its parse, brand, or effective fact");

    await expect(
      persistDailyBrandMetricRollup(database, {
        ...input,
        memberships: [
          {
            ...input.memberships[0]!,
            appliedCorrectionIds: [],
            shareOfVoiceFacts: input.memberships[0]!.shareOfVoiceFacts.map((fact) =>
              fact.brandId === FIXTURE_IDS.primaryBrandId ? { ...fact, mentionCount: 1 } : fact,
            ),
            contribution: {
              ...input.memberships[0]!.contribution,
              mentionCount: 1,
              sentiment: "positive",
            },
          },
        ],
      }),
    ).rejects.toThrow("does not match machine fact or applied correction");

    await expect(
      persistDailyBrandMetricRollup(database, {
        ...input,
        memberships: [
          {
            ...input.memberships[0]!,
            appliedCorrectionIds: [],
            contribution: {
              ...input.memberships[0]!.contribution,
              cited: false,
              sentiment: "positive",
            },
          },
        ],
      }),
    ).rejects.toThrow("citation contribution does not match");

    await expect(
      persistDailyBrandMetricRollup(database, {
        ...input,
        memberships: [
          {
            taskId: fixture.task.id,
            answerSampleId: null,
            brandFactId: null,
            inclusionStatus: "failed",
            contribution: {
              mentionCount: 0,
              firstRank: null,
              cited: false,
              sentiment: null,
            },
          },
        ],
      }),
    ).rejects.toThrow("Failed metric memberships require a failed task");

    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        const mismatchedTasks = await transaction<{ id: string }[]>`
          SELECT id
          FROM collection_tasks
          WHERE tenant_id = ${FIXTURE_IDS.tenantId}
            AND platform_entry_code <> ${fixture.task.platformEntryCode}
          LIMIT 1
        `;
        await transaction`
          INSERT INTO daily_brand_metric_memberships (
            tenant_id, rollup_id, brand_id, task_id, inclusion_status, contribution
          ) VALUES (
            ${FIXTURE_IDS.tenantId}, ${first.rollupId}, ${FIXTURE_IDS.primaryBrandId},
            ${mismatchedTasks[0]!.id}, 'missing',
            '{
              "mentionCount": 0,
              "monitoredBrandMentionCount": 0,
              "firstRank": null,
              "cited": false,
              "sentiment": null
            }'::jsonb
          )
        `;
      }),
    ).rejects.toThrow("task dimensions do not match");
  });

  it("applies a complete manual correction set to metric membership values", async () => {
    const fixture = await createParsedPrimaryBrandFact();
    const corrections = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<{ field_name: string; id: string }[]>`
        INSERT INTO brand_fact_corrections (
          tenant_id, brand_fact_id, corrected_by_user_id, field_name,
          previous_value, corrected_value, reason
        ) VALUES
          (
            ${FIXTURE_IDS.tenantId}, ${fixture.brandFactId}, ${FIXTURE_IDS.adminUserId},
            'mentioned', 'true'::jsonb, 'false'::jsonb, '人工确认未形成品牌提及'
          ),
          (
            ${FIXTURE_IDS.tenantId}, ${fixture.brandFactId}, ${FIXTURE_IDS.adminUserId},
            'mention_count', '2'::jsonb, '0'::jsonb, '人工确认未形成品牌提及'
          ),
          (
            ${FIXTURE_IDS.tenantId}, ${fixture.brandFactId}, ${FIXTURE_IDS.adminUserId},
            'first_mention_rank', '2'::jsonb, 'null'::jsonb, '人工确认未形成品牌提及'
          ),
          (
            ${FIXTURE_IDS.tenantId}, ${fixture.brandFactId}, ${FIXTURE_IDS.adminUserId},
            'sentiment', '"positive"'::jsonb, 'null'::jsonb, '人工确认未形成品牌提及'
          )
        RETURNING id, field_name
      `;
    });

    const result = await persistDailyBrandMetricRollup(database, {
      tenantId: FIXTURE_IDS.tenantId,
      brandProjectId: FIXTURE_IDS.brandProjectId,
      scheduledDate,
      brandId: FIXTURE_IDS.primaryBrandId,
      brandRuleVersionId: FIXTURE_IDS.primaryBrandRuleVersionId,
      questionId: fixture.questionId,
      questionVersionId: fixture.task.questionVersionId,
      topicGroupId: fixture.topicGroupId,
      platformEntryCode: fixture.task.platformEntryCode,
      platformEntryVersionId: fixture.task.platformEntryVersionId,
      platformEntryVersion: fixture.task.platformEntryVersion,
      platformCode: fixture.task.platformCode,
      region: fixture.task.region,
      language: fixture.task.language,
      channel: fixture.task.channel,
      parserVersionId: FIXTURE_IDS.parserVersionId,
      memberships: [
        {
          taskId: fixture.task.id,
          answerSampleId: fixture.sampleId,
          brandFactId: fixture.brandFactId,
          appliedCorrectionIds: corrections.map((correction) => correction.id),
          shareOfVoiceFacts: [
            {
              brandId: FIXTURE_IDS.primaryBrandId,
              brandFactId: fixture.brandFactId,
              brandRuleVersionId: FIXTURE_IDS.primaryBrandRuleVersionId,
              mentionCount: 0,
              appliedMentionCountCorrectionId: corrections.find(
                (correction) => correction.field_name === "mention_count",
              )!.id,
            },
            {
              brandId: FIXTURE_IDS.competitorBrandId,
              brandFactId: fixture.competitorBrandFactId,
              brandRuleVersionId: FIXTURE_IDS.competitorBrandRuleVersionId,
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
        },
      ],
    });

    const membership = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<
        {
          correction_count: number;
          inclusion_status: string;
          sov_correction_id: string | null;
          sov_denominator: number;
        }[]
      >`
        SELECT
          membership.inclusion_status,
          (
            SELECT count(*)::integer
            FROM brand_metric_membership_corrections AS correction
            WHERE correction.tenant_id = membership.tenant_id
              AND correction.rollup_id = membership.rollup_id
              AND correction.task_id = membership.task_id
          ) AS correction_count,
          (
            SELECT max(sov.applied_mention_count_correction_id::text)
            FROM brand_metric_sov_contributions AS sov
            WHERE sov.tenant_id = membership.tenant_id
              AND sov.rollup_id = membership.rollup_id
              AND sov.task_id = membership.task_id
          ) AS sov_correction_id,
          (
            SELECT sum(sov.mention_count)::integer
            FROM brand_metric_sov_contributions AS sov
            WHERE sov.tenant_id = membership.tenant_id
              AND sov.rollup_id = membership.rollup_id
              AND sov.task_id = membership.task_id
          ) AS sov_denominator
        FROM daily_brand_metric_memberships AS membership
        WHERE membership.tenant_id = ${FIXTURE_IDS.tenantId}
          AND membership.rollup_id = ${result.rollupId}
      `;
    });
    expect(membership[0]).toEqual({
      inclusion_status: "valid_unmentioned",
      correction_count: 4,
      sov_correction_id: corrections.find(
        (correction) => correction.field_name === "mention_count",
      )!.id,
      sov_denominator: 3,
    });
  });

  it("uses immutable citation snapshots for historical source dimensions", async () => {
    const fixture = await createParsedPrimaryBrandFact();
    await createPrimaryBrandCitation(fixture.sampleId);

    await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      await transaction`
        UPDATE citation_domains
        SET source_category = 'news'
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
          AND canonical_host = 'primary-brand.invalid'
      `;
      await transaction`
        UPDATE normalized_urls
        SET canonical_url = 'https://primary-brand.invalid/article?reclassified=1'
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
          AND url_hash = 'fixture-primary-citation-url-v1'
      `;
      await transaction`
        UPDATE citation_articles
        SET title = '后续补充的文章标题'
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
          AND normalized_url_id = (
            SELECT id FROM normalized_urls
            WHERE tenant_id = ${FIXTURE_IDS.tenantId}
              AND url_hash = 'fixture-primary-citation-url-v1'
          )
      `;
    });

    const snapshots = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<
        { article_title: string | null; canonical_url: string; source_category: string }[]
      >`
        SELECT source_category, canonical_url, article_title
        FROM answer_citation_dimension_snapshots
        WHERE tenant_id = ${FIXTURE_IDS.tenantId}
          AND answer_sample_id = ${fixture.sampleId}
      `;
    });
    expect(snapshots[0]).toEqual({
      source_category: "owned",
      canonical_url: "https://primary-brand.invalid/article",
      article_title: "合成品牌资料",
    });

    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        await transaction`
          UPDATE answer_citation_dimension_snapshots
          SET source_category = 'other'
          WHERE tenant_id = ${FIXTURE_IDS.tenantId}
            AND answer_sample_id = ${fixture.sampleId}
        `;
      }),
    ).rejects.toThrow("immutable");
  });

  it("keeps parser, rule, correction, citation, and fact-risk lineage tenant-scoped", async () => {
    const fixture = await createParsedPrimaryBrandFact();

    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        const failedParses = await transaction<{ id: string }[]>`
          INSERT INTO answer_parses (
            tenant_id, answer_sample_id, parser_version_id,
            brand_rule_set_hash, status, error_code
          ) VALUES (
            ${FIXTURE_IDS.tenantId}, ${fixture.sampleId}, ${FIXTURE_IDS.parserVersionId},
            'fixture-failed-rule-set-v1', 'failed', 'synthetic_parse_failure'
          )
          RETURNING id
        `;
        await transaction`
          INSERT INTO answer_brand_facts (
            tenant_id, answer_parse_id, answer_sample_id, brand_id, brand_rule_version_id,
            mentioned, mention_count, first_mention_offset, first_mention_rank,
            recommendation_role, sentiment
          ) VALUES (
            ${FIXTURE_IDS.tenantId}, ${failedParses[0]!.id}, ${fixture.sampleId},
            ${FIXTURE_IDS.competitorBrandId}, ${FIXTURE_IDS.competitorBrandRuleVersionId},
            false, 0, NULL, NULL, 'not_mentioned', 'not_applicable'
          )
        `;
      }),
    ).rejects.toThrow("Brand facts require a completed parse");

    await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      const secondRuleVersions = await transaction<{ id: string }[]>`
        INSERT INTO brand_rule_versions (
          tenant_id, brand_id, version, canonical_name,
          aliases, exclusion_terms, rule_hash
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.primaryBrandId}, 2, '合成主品牌',
          ARRAY['合成品牌', '合成品牌二代'], ARRAY[]::text[], 'fixture-primary-brand-rules-v2'
        )
        RETURNING id
      `;
      await transaction`
        INSERT INTO brand_match_rules (
          tenant_id, brand_id, brand_rule_version_id, rule_kind,
          match_kind, pattern, normalized_pattern, language, priority
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.primaryBrandId},
          ${secondRuleVersions[0]!.id}, 'product_name', 'exact',
          '合成品牌专业版', '合成品牌专业版', 'zh-CN', 80
        )
      `;
      await transaction`
        INSERT INTO brand_domains (
          tenant_id, brand_id, brand_rule_version_id, registrable_domain, is_primary
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.primaryBrandId},
          ${secondRuleVersions[0]!.id}, 'v2-primary-brand.invalid', true
        )
      `;
      const corrections = await transaction<{ id: string }[]>`
        INSERT INTO brand_fact_corrections (
          tenant_id, brand_fact_id, corrected_by_user_id, field_name,
          previous_value, corrected_value, reason
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${fixture.brandFactId}, ${FIXTURE_IDS.adminUserId},
          'sentiment', '"positive"'::jsonb, '"neutral"'::jsonb, '人工复核上下文'
        )
        RETURNING id
      `;
      await transaction`
        INSERT INTO brand_fact_corrections (
          tenant_id, brand_fact_id, supersedes_correction_id, corrected_by_user_id,
          field_name, previous_value, corrected_value, reason
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${fixture.brandFactId}, ${corrections[0]!.id},
          ${FIXTURE_IDS.adminUserId}, 'sentiment', '"neutral"'::jsonb,
          '"negative"'::jsonb, '二次人工复核'
        )
      `;
      const risks = await transaction<{ id: string }[]>`
        INSERT INTO brand_fact_risks (
          tenant_id, brand_fact_id, risk_code, risk_level, confidence, context_snippet
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${fixture.brandFactId}, 'unsupported_claim',
          'medium', 0.8, '合成主品牌是第二个推荐品牌'
        )
        RETURNING id
      `;
      await transaction`
        INSERT INTO brand_fact_risk_corrections (
          tenant_id, brand_fact_risk_id, corrected_by_user_id,
          previous_value, corrected_value, reason
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${risks[0]!.id}, ${FIXTURE_IDS.adminUserId},
          '{"riskLevel":"medium"}'::jsonb, '{"riskLevel":"low"}'::jsonb,
          '证据复核后降低风险等级'
        )
      `;

      const domains = await transaction<{ id: string }[]>`
        INSERT INTO citation_domains (
          tenant_id, canonical_host, registrable_domain, source_category
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, 'primary-brand.invalid',
          'primary-brand.invalid', 'owned'
        )
        RETURNING id
      `;
      const urls = await transaction<{ id: string }[]>`
        INSERT INTO normalized_urls (
          tenant_id, citation_domain_id, url_hash, canonical_url, scheme, host,
          registrable_domain, path
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${domains[0]!.id}, 'fixture-citation-url-v1',
          'https://primary-brand.invalid/article', 'https',
          'primary-brand.invalid', 'primary-brand.invalid', '/article'
        )
        RETURNING id
      `;
      await transaction`
        INSERT INTO citation_articles (
          tenant_id, normalized_url_id, citation_domain_id, title, source_type
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${urls[0]!.id}, ${domains[0]!.id},
          '合成品牌资料', 'article'
        )
      `;
      await transaction`
        INSERT INTO answer_citations (
          tenant_id, answer_sample_id, normalized_url_id, citation_index,
          original_url, title, source_label
        ) VALUES (
          ${FIXTURE_IDS.tenantId}, ${fixture.sampleId}, ${urls[0]!.id}, 1,
          'https://primary-brand.invalid/article', '合成品牌资料', '品牌官网'
        )
      `;
    });

    const lineage = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      return transaction<
        {
          rule_versions: number;
          product_name_rules: number;
          versioned_domains: number;
          corrections: number;
          risks: number;
          articles: number;
          old_fact_rule_version: string;
        }[]
      >`
        SELECT
          (SELECT count(*)::integer FROM brand_rule_versions WHERE brand_id = ${FIXTURE_IDS.primaryBrandId}) AS rule_versions,
          (SELECT count(*)::integer FROM brand_match_rules WHERE brand_id = ${FIXTURE_IDS.primaryBrandId} AND rule_kind = 'product_name') AS product_name_rules,
          (SELECT count(*)::integer FROM brand_domains WHERE brand_id = ${FIXTURE_IDS.primaryBrandId}) AS versioned_domains,
          (SELECT count(*)::integer FROM brand_fact_corrections WHERE brand_fact_id = ${fixture.brandFactId}) AS corrections,
          (SELECT count(*)::integer FROM brand_fact_risks WHERE brand_fact_id = ${fixture.brandFactId}) AS risks,
          (SELECT count(*)::integer FROM citation_articles) AS articles,
          (SELECT brand_rule_version_id::text FROM answer_brand_facts WHERE id = ${fixture.brandFactId}) AS old_fact_rule_version
      `;
    });
    expect(lineage[0]).toEqual({
      rule_versions: 2,
      product_name_rules: 1,
      versioned_domains: 2,
      corrections: 2,
      risks: 1,
      articles: 1,
      old_fact_rule_version: FIXTURE_IDS.primaryBrandRuleVersionId,
    });

    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        await transaction`
          UPDATE answer_brand_facts SET sentiment = 'negative'
          WHERE id = ${fixture.brandFactId}
        `;
      }),
    ).rejects.toThrow("immutable");

    const correctionTargets = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
      const priorCorrections = await transaction<{ id: string }[]>`
        SELECT id
        FROM brand_fact_corrections
        WHERE brand_fact_id = ${fixture.brandFactId}
        ORDER BY created_at
        LIMIT 1
      `;
      return {
        priorCorrectionId: priorCorrections[0]!.id,
        competitorFactId: fixture.competitorBrandFactId,
      };
    });
    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        await transaction`
          INSERT INTO brand_fact_corrections (
            tenant_id, brand_fact_id, supersedes_correction_id, corrected_by_user_id,
            field_name, previous_value, corrected_value, reason
          ) VALUES (
            ${FIXTURE_IDS.tenantId}, ${correctionTargets.competitorFactId},
            ${correctionTargets.priorCorrectionId}, ${FIXTURE_IDS.adminUserId},
            'sentiment', '"positive"'::jsonb, '"neutral"'::jsonb,
            '不允许跨事实串联'
          )
        `;
      }),
    ).rejects.toThrow("same fact field and value chain");

    const selfReferencingCorrectionId = randomUUID();
    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        await transaction`
          INSERT INTO brand_fact_corrections (
            id, tenant_id, brand_fact_id, supersedes_correction_id, corrected_by_user_id,
            field_name, previous_value, corrected_value, reason
          ) VALUES (
            ${selfReferencingCorrectionId}, ${FIXTURE_IDS.tenantId}, ${fixture.brandFactId},
            ${selfReferencingCorrectionId}, ${FIXTURE_IDS.adminUserId},
            'mention_count', '2'::jsonb, '1'::jsonb, '不允许自引用修正'
          )
        `;
      }),
    ).rejects.toThrow("cannot supersede itself");

    await expect(
      database`UPDATE metric_versions SET formula_hash = 'mutated' WHERE metric_code = 'mention_rate'`,
    ).rejects.toThrow("immutable");

    await expect(
      database`
        INSERT INTO metric_versions (
          metric_code, version, formula_definition, formula_hash, active
        ) VALUES (
          'mention_rate', 2,
          '{"expression":"mentioned_answer_count / valid_answer_count"}'::jsonb,
          'mention-rate-v2', true
        )
      `,
    ).rejects.toThrow();

    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        const firstDomains = await transaction<{ id: string }[]>`
          INSERT INTO citation_domains (
            tenant_id, canonical_host, registrable_domain, source_category
          ) VALUES (
            ${FIXTURE_IDS.tenantId}, 'source-one.example.invalid',
            'example.invalid', 'reference'
          )
          RETURNING id
        `;
        const secondDomains = await transaction<{ id: string }[]>`
          INSERT INTO citation_domains (
            tenant_id, canonical_host, registrable_domain, source_category
          ) VALUES (
            ${FIXTURE_IDS.tenantId}, 'source-two.example.invalid',
            'example.invalid', 'reference'
          )
          RETURNING id
        `;
        const mismatchedUrls = await transaction<{ id: string }[]>`
          INSERT INTO normalized_urls (
            tenant_id, citation_domain_id, url_hash, canonical_url, scheme, host,
            registrable_domain, path
          ) VALUES (
            ${FIXTURE_IDS.tenantId}, ${firstDomains[0]!.id},
            'mismatched-citation-url-v1',
            'https://source-one.example.invalid/mismatch', 'https',
            'source-one.example.invalid', 'example.invalid', '/mismatch'
          )
          RETURNING id
        `;
        await transaction`
          INSERT INTO citation_articles (
            tenant_id, normalized_url_id, citation_domain_id, source_type
          ) VALUES (
            ${FIXTURE_IDS.tenantId}, ${mismatchedUrls[0]!.id},
            ${secondDomains[0]!.id}, 'article'
          )
        `;
      }),
    ).rejects.toThrow();

    const otherTenantId = "99999999-9999-4999-8999-999999999996";
    const otherProjectId = "99999999-9999-4999-8999-999999999995";
    const otherBrandId = "99999999-9999-4999-8999-999999999994";
    const invisibleAnalytics = await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${otherTenantId}, true)`;
      await transaction`
        INSERT INTO tenants (id, slug, name)
        VALUES (${otherTenantId}, 'analytics-other-tenant', 'Analytics Other Tenant')
      `;
      await transaction`
        INSERT INTO brand_projects (id, tenant_id, name, fixture)
        VALUES (${otherProjectId}, ${otherTenantId}, 'Other Analytics Project', true)
      `;
      await transaction`
        INSERT INTO brands (
          id, tenant_id, brand_project_id, canonical_name, kind, aliases, exclusion_terms
        ) VALUES (
          ${otherBrandId}, ${otherTenantId}, ${otherProjectId},
          'Other Analytics Brand', 'primary', '[]'::jsonb, '[]'::jsonb
        )
      `;
      return transaction<
        {
          facts: number;
          corrections: number;
          citations: number;
          domains: number;
          urls: number;
          articles: number;
        }[]
      >`
        SELECT
          (SELECT count(*)::integer FROM answer_brand_facts) AS facts,
          (SELECT count(*)::integer FROM brand_fact_corrections) AS corrections,
          (SELECT count(*)::integer FROM answer_citations) AS citations,
          (SELECT count(*)::integer FROM citation_domains) AS domains,
          (SELECT count(*)::integer FROM normalized_urls) AS urls,
          (SELECT count(*)::integer FROM citation_articles) AS articles
      `;
    });
    expect(invisibleAnalytics[0]).toEqual({
      facts: 0,
      corrections: 0,
      citations: 0,
      domains: 0,
      urls: 0,
      articles: 0,
    });

    await database.begin(async (transaction) => {
      await transaction`SELECT set_config('app.tenant_id', ${otherTenantId}, true)`;
      const domains = await transaction<{ id: string }[]>`
        INSERT INTO citation_domains (
          tenant_id, canonical_host, registrable_domain, source_category
        ) VALUES (
          ${otherTenantId}, 'primary-brand.invalid',
          'primary-brand.invalid', 'owned'
        )
        RETURNING id
      `;
      await transaction`
        INSERT INTO normalized_urls (
          tenant_id, citation_domain_id, url_hash, canonical_url, scheme, host,
          registrable_domain, path
        ) VALUES (
          ${otherTenantId}, ${domains[0]!.id}, 'fixture-citation-url-v1',
          'https://primary-brand.invalid/article', 'https',
          'primary-brand.invalid', 'primary-brand.invalid', '/article'
        )
      `;
    });

    await expect(
      database.begin(async (transaction) => {
        await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
        await transaction`
          INSERT INTO brand_rule_versions (
            tenant_id, brand_id, version, canonical_name,
            aliases, exclusion_terms, rule_hash
          ) VALUES (
            ${FIXTURE_IDS.tenantId}, ${otherBrandId}, 1, 'Cross Tenant Rule',
            ARRAY[]::text[], ARRAY[]::text[], 'cross-tenant-rule'
          )
        `;
      }),
    ).rejects.toThrow();
  });
});
