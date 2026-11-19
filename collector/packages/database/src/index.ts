import { readFile, readdir } from "node:fs/promises";
import { resolve } from "node:path";

import type {
  LeaseRequest,
  LeasedTask,
  ResultSubmission,
  ResultSubmissionResponse,
} from "@aivc/contracts";
import {
  BRAND_GEO_METRIC_CODES,
  buildTaskBusinessKey,
  calculateBrandGeoMetrics,
  deriveBrandGeoMetricInputs,
  DomainError,
  hashBrandMetricMemberships,
  hashResultPayload,
  type BrandGeoMetricCode,
  type BrandMetricMembership,
  type SubmissionDisposition,
} from "@aivc/domain";
import postgres from "postgres";

import {
  FIXTURE_IDS,
  PLATFORM_FIXTURES,
  SYNTHETIC_QUESTIONS,
  fixtureQuestionId,
  fixtureQuestionVersionId,
  fixtureTopicGroupId,
} from "./fixtures.ts";
import { leaseTaskIdsForSessionProfile, tenantUsesSessionProfiles } from "./session-profiles.ts";

export * from "./fixtures.ts";
export * from "./schema.ts";
export {
  checkSessionProfileEligibility,
  configurePlatformDispatch,
  ELIGIBILITY_RETRY_MAX_SECONDS,
  ELIGIBILITY_RETRY_MIN_SECONDS,
  listPlatformDispatchStates,
  listSessionProfileMetrics,
  recordSessionEvent,
  registerSessionProfile,
  reportCollectorFailure,
  resumePlatformDispatch,
  setSessionProfileStatus,
  SESSION_PROFILE_ONLINE_WINDOW_SECONDS,
  type ConfigurePlatformDispatchInput,
  type PlatformDispatchStateView,
  type RegisterSessionProfileInput,
  type SessionProfileMetrics,
} from "./session-profiles.ts";

export type Database = ReturnType<typeof postgres>;
export type DatabaseTransaction = postgres.TransactionSql;

export interface PersistDailyBrandMetricRollupInput {
  tenantId: string;
  brandProjectId: string;
  scheduledDate: string;
  brandId: string;
  brandRuleVersionId: string;
  questionId: string;
  questionVersionId: string;
  topicGroupId: string;
  platformEntryCode: string;
  platformEntryVersionId: string;
  platformEntryVersion: number;
  platformCode: string;
  region: "cn" | "overseas";
  language: string;
  channel: "consumer_web";
  parserVersionId: string;
  memberships: readonly BrandMetricMembership[];
}

interface MatrixCellRow {
  matrix_cell_id: string;
  tenant_id: string;
  brand_project_id: string;
  question_version_id: string;
  question_text: string;
  platform_entry_code: string;
  platform_entry_version_id: string;
  platform_entry_version: number;
  platform_code: string;
  region: "cn" | "overseas";
  language: string;
  channel: string;
  samples_per_period: number;
}

interface LeasedTaskRow {
  id: string;
  tenant_id: string;
  brand_project_id: string;
  platform_code: LeasedTask["platformCode"];
  platform_entry_code: string;
  platform_entry_version_id: string;
  platform_entry_version: number;
  question_version_id: string;
  question_text: string;
  region: LeasedTask["region"];
  language: string;
  channel: "consumer_web";
  lease_version: number;
  lease_expires_at: Date;
  lease_session_profile_id: string | null;
}

interface TaskSubmissionRow {
  id: string;
  status: "pending" | "leased" | "completed" | "failed";
  region: "cn" | "overseas";
  lease_version: number;
  lease_owner: string | null;
  lease_expires_at: Date | null;
  lease_session_profile_id: string | null;
}

interface ActiveMetricVersionRow {
  id: string;
  metric_code: BrandGeoMetricCode;
}

export function createDatabase(databaseUrl: string, maxConnections = 10): Database {
  return postgres(databaseUrl, {
    max: maxConnections,
    prepare: false,
    onnotice: () => undefined,
    idle_timeout: 20,
    connect_timeout: 10,
  });
}

export async function migrateDatabase(database: Database): Promise<void> {
  const migrationsDirectory = resolve(process.cwd(), "packages/database/migrations");
  const migrationFiles = (await readdir(migrationsDirectory))
    .filter((file) => file.endsWith(".sql"))
    .sort();
  const migrations = await Promise.all(
    migrationFiles.map(async (file) => ({
      file,
      sql: await readFile(resolve(migrationsDirectory, file), "utf8"),
    })),
  );

  await database.begin(async (transaction) => {
    await transaction`SELECT pg_advisory_xact_lock(hashtextextended('aivc:schema-migrations', 0))`;
    await transaction`
      CREATE TABLE IF NOT EXISTS schema_migrations (
        version text PRIMARY KEY,
        applied_at timestamptz NOT NULL DEFAULT now()
      )
    `;

    for (const migration of migrations) {
      const alreadyApplied = await transaction<{ version: string }[]>`
        SELECT version FROM schema_migrations WHERE version = ${migration.file}
      `;
      if (alreadyApplied.length > 0) {
        continue;
      }

      await transaction.unsafe(migration.sql);
      await transaction`INSERT INTO schema_migrations (version) VALUES (${migration.file})`;
    }
  });
}

export async function seedFixture(database: Database): Promise<void> {
  await database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${FIXTURE_IDS.tenantId}, true)`;
    await transaction`
      INSERT INTO tenants (id, slug, name)
      VALUES (${FIXTURE_IDS.tenantId}, 'synthetic-fixture', 'Synthetic Fixture Tenant')
      ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name
    `;
    await transaction`
      INSERT INTO users (id, tenant_id, email, display_name)
      VALUES (${FIXTURE_IDS.adminUserId}, ${FIXTURE_IDS.tenantId}, 'fixture-admin@example.invalid', 'Fixture Admin')
      ON CONFLICT (id) DO UPDATE SET display_name = EXCLUDED.display_name
    `;
    await transaction`
      INSERT INTO memberships (tenant_id, user_id, role)
      VALUES (${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.adminUserId}, 'admin')
      ON CONFLICT (tenant_id, user_id) DO UPDATE SET role = EXCLUDED.role
    `;
    await transaction`
      INSERT INTO brand_projects (id, tenant_id, name, fixture)
      VALUES (${FIXTURE_IDS.brandProjectId}, ${FIXTURE_IDS.tenantId}, 'Synthetic Monitoring Project', true)
      ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, fixture = true
    `;
    await transaction`
      INSERT INTO brands (id, tenant_id, brand_project_id, canonical_name, kind, aliases, exclusion_terms)
      VALUES
        (${FIXTURE_IDS.primaryBrandId}, ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.brandProjectId}, '合成主品牌', 'primary', '["合成品牌"]'::jsonb, '[]'::jsonb),
        (${FIXTURE_IDS.competitorBrandId}, ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.brandProjectId}, '合成竞品', 'competitor', '[]'::jsonb, '[]'::jsonb)
      ON CONFLICT (id) DO UPDATE SET canonical_name = EXCLUDED.canonical_name
    `;
    await transaction`
      INSERT INTO brand_rule_versions (
        id, tenant_id, brand_id, version, canonical_name,
        aliases, exclusion_terms, rule_hash
      ) VALUES
        (
          ${FIXTURE_IDS.primaryBrandRuleVersionId}, ${FIXTURE_IDS.tenantId},
          ${FIXTURE_IDS.primaryBrandId}, 1, '合成主品牌', ARRAY['合成品牌'],
          ARRAY[]::text[], 'fixture-primary-brand-rules-v1'
        ),
        (
          ${FIXTURE_IDS.competitorBrandRuleVersionId}, ${FIXTURE_IDS.tenantId},
          ${FIXTURE_IDS.competitorBrandId}, 1, '合成竞品', ARRAY[]::text[],
          ARRAY[]::text[], 'fixture-competitor-brand-rules-v1'
        )
      ON CONFLICT (tenant_id, brand_id, version) DO NOTHING
    `;
    const fixtureRuleVersions = await transaction<{ brand_id: string; id: string }[]>`
      SELECT brand_id, id
      FROM brand_rule_versions
      WHERE tenant_id = ${FIXTURE_IDS.tenantId}
        AND brand_id IN (${FIXTURE_IDS.primaryBrandId}, ${FIXTURE_IDS.competitorBrandId})
        AND version = 1
    `;
    const primaryBrandRuleVersionId = fixtureRuleVersions.find(
      (ruleVersion) => ruleVersion.brand_id === FIXTURE_IDS.primaryBrandId,
    )?.id;
    const competitorBrandRuleVersionId = fixtureRuleVersions.find(
      (ruleVersion) => ruleVersion.brand_id === FIXTURE_IDS.competitorBrandId,
    )?.id;
    if (!primaryBrandRuleVersionId || !competitorBrandRuleVersionId) {
      throw new DomainError(
        "FIXTURE_RULE_VERSION_NOT_FOUND",
        "Synthetic brand rule versions were not persisted",
        500,
      );
    }
    await transaction`
      INSERT INTO brand_match_rules (
        tenant_id, brand_id, brand_rule_version_id, rule_kind,
        match_kind, pattern, normalized_pattern, language, priority
      ) VALUES
        (
          ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.primaryBrandId},
          ${primaryBrandRuleVersionId}, 'canonical_name',
          'exact', '合成主品牌', '合成主品牌', 'zh-CN', 100
        ),
        (
          ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.primaryBrandId},
          ${primaryBrandRuleVersionId}, 'alias',
          'exact', '合成品牌', '合成品牌', 'zh-CN', 90
        ),
        (
          ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.competitorBrandId},
          ${competitorBrandRuleVersionId}, 'canonical_name',
          'exact', '合成竞品', '合成竞品', 'zh-CN', 100
        )
      ON CONFLICT DO NOTHING
    `;
    await transaction`
      INSERT INTO brand_domains (
        tenant_id, brand_id, brand_rule_version_id, registrable_domain, is_primary
      )
      VALUES
        (
          ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.primaryBrandId},
          ${primaryBrandRuleVersionId}, 'primary-brand.invalid', true
        ),
        (
          ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.competitorBrandId},
          ${competitorBrandRuleVersionId}, 'competitor-brand.invalid', true
        )
      ON CONFLICT (tenant_id, brand_rule_version_id, registrable_domain) DO NOTHING
    `;
    await transaction`
      INSERT INTO parser_versions (id, code, version, config_hash, status)
      VALUES (${FIXTURE_IDS.parserVersionId}, 'fixture-brand-parser', 1, 'fixture-parser-v1', 'active')
      ON CONFLICT (code, version) DO NOTHING
    `;

    const topicGroupNames = ["协作工具", "品牌监测", "采集可靠性"] as const;
    for (const [index, name] of topicGroupNames.entries()) {
      await transaction`
        INSERT INTO topic_groups (id, tenant_id, brand_project_id, name, sort_order, fixture)
        VALUES (${fixtureTopicGroupId(index)}, ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.brandProjectId}, ${`【合成测试】${name}`}, ${index}, true)
        ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, sort_order = EXCLUDED.sort_order
      `;
    }

    for (const [index, questionText] of SYNTHETIC_QUESTIONS.entries()) {
      const topicIndex = Math.floor(index / 10);
      await transaction`
        INSERT INTO questions (id, tenant_id, brand_project_id, topic_group_id, status, fixture)
        VALUES (${fixtureQuestionId(index)}, ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.brandProjectId}, ${fixtureTopicGroupId(topicIndex)}, 'active', true)
        ON CONFLICT (id) DO UPDATE SET topic_group_id = EXCLUDED.topic_group_id, status = 'active'
      `;
      await transaction`
        INSERT INTO question_versions (
          id, tenant_id, brand_project_id, question_id, topic_group_id,
          topic_group_name, topic_group_sort_order, topic_group_status,
          version, question_text, language, fixture
        ) VALUES (
          ${fixtureQuestionVersionId(index)}, ${FIXTURE_IDS.tenantId},
          ${FIXTURE_IDS.brandProjectId}, ${fixtureQuestionId(index)},
          ${fixtureTopicGroupId(topicIndex)}, ${`【合成测试】${topicGroupNames[topicIndex]}`},
          ${topicIndex}, 'active', 1, ${questionText}, 'zh-CN', true
        )
        ON CONFLICT (id) DO UPDATE SET
          brand_project_id = EXCLUDED.brand_project_id,
          topic_group_id = EXCLUDED.topic_group_id,
          topic_group_name = EXCLUDED.topic_group_name,
          topic_group_sort_order = EXCLUDED.topic_group_sort_order,
          topic_group_status = EXCLUDED.topic_group_status,
          question_text = EXCLUDED.question_text
      `;
    }

    for (const platform of PLATFORM_FIXTURES) {
      const entryCode = `${platform.code}.consumer_web`;
      await transaction`
        INSERT INTO platforms (code, display_name, region, enabled)
        VALUES (${platform.code}, ${platform.name}, ${platform.region}, true)
        ON CONFLICT (code) DO UPDATE
          SET display_name = EXCLUDED.display_name, region = EXCLUDED.region, enabled = true
      `;
      await transaction`
        INSERT INTO platform_entries (code, platform_code, display_name, region, channel, version, enabled)
        VALUES (${entryCode}, ${platform.code}, ${`${platform.name} 消费者 Web`}, ${platform.region}, 'consumer_web', 1, true)
        ON CONFLICT (code) DO UPDATE
          SET enabled = true
      `;
      await transaction`
        INSERT INTO platform_entry_versions (
          platform_entry_code, platform_code, version, display_name, region, channel
        ) VALUES (
          ${entryCode}, ${platform.code}, 1, ${`${platform.name} 消费者 Web`},
          ${platform.region}, 'consumer_web'
        )
        ON CONFLICT (platform_entry_code, version) DO NOTHING
      `;
    }

    await transaction`
      INSERT INTO monitoring_matrices (id, tenant_id, brand_project_id, name, frequency, samples_per_period, enabled, fixture)
      VALUES (${FIXTURE_IDS.matrixId}, ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.brandProjectId}, 'Synthetic Daily Matrix', 'daily', 1, true, true)
      ON CONFLICT (id) DO UPDATE SET enabled = true, samples_per_period = 1
    `;
    await transaction`
      INSERT INTO matrix_cells (
        tenant_id, brand_project_id, matrix_id, question_version_id,
        platform_entry_code, region, language, channel, enabled
      )
      SELECT
        ${FIXTURE_IDS.tenantId}, ${FIXTURE_IDS.brandProjectId}, ${FIXTURE_IDS.matrixId},
        qv.id, pe.code, pe.region, qv.language, pe.channel, true
      FROM question_versions qv
      CROSS JOIN platform_entries pe
      WHERE qv.tenant_id = ${FIXTURE_IDS.tenantId}
        AND qv.fixture = true
        AND pe.enabled = true
      ON CONFLICT (tenant_id, matrix_id, question_version_id, platform_entry_code)
      DO UPDATE SET enabled = true, region = EXCLUDED.region
    `;
  });
}

export function currentShanghaiDate(now = new Date()): string {
  return new Intl.DateTimeFormat("en-CA", {
    timeZone: "Asia/Shanghai",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(now);
}

export async function generateDailyTasks(
  database: Database,
  input: { tenantId: string; brandProjectId: string; scheduledDate: string },
): Promise<{ planned: number; inserted: number }> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${input.tenantId}, true)`;
    const cells = await transaction<MatrixCellRow[]>`
      SELECT
        mc.id AS matrix_cell_id,
        mc.tenant_id,
        mc.brand_project_id,
        mc.question_version_id,
        qv.question_text,
        mc.platform_entry_code,
        pev.id AS platform_entry_version_id,
        pev.version AS platform_entry_version,
        pe.platform_code,
        mc.region,
        mc.language,
        mc.channel,
        mm.samples_per_period
      FROM matrix_cells mc
      JOIN monitoring_matrices mm ON mm.id = mc.matrix_id AND mm.tenant_id = mc.tenant_id
      JOIN question_versions qv ON qv.id = mc.question_version_id AND qv.tenant_id = mc.tenant_id
      JOIN questions q ON q.id = qv.question_id AND q.tenant_id = mc.tenant_id
      JOIN platform_entries pe ON pe.code = mc.platform_entry_code
      JOIN platform_entry_versions pev
        ON pev.platform_entry_code = pe.code
       AND pev.platform_code = pe.platform_code
       AND pev.version = pe.version
       AND pev.region = pe.region
       AND pev.channel = pe.channel
      WHERE mc.tenant_id = ${input.tenantId}
        AND mc.brand_project_id = ${input.brandProjectId}
        AND mc.enabled = true
        AND mm.enabled = true
        AND q.status = 'active'
        AND pe.enabled = true
      ORDER BY qv.id, pe.code
    `;

    let inserted = 0;
    let planned = 0;
    for (const cell of cells) {
      for (let sampleIndex = 1; sampleIndex <= cell.samples_per_period; sampleIndex += 1) {
        planned += 1;
        const businessKey = buildTaskBusinessKey({
          tenantId: cell.tenant_id,
          brandProjectId: cell.brand_project_id,
          questionVersionId: cell.question_version_id,
          platformEntryCode: cell.platform_entry_code,
          region: cell.region,
          language: cell.language,
          channel: cell.channel,
          scheduledDate: input.scheduledDate,
          sampleIndex,
          runKind: "scheduled",
        });
        const created = await transaction<{ id: string }[]>`
          INSERT INTO collection_tasks (
            tenant_id, brand_project_id, matrix_cell_id, question_version_id,
            platform_entry_code, platform_entry_version_id, platform_entry_version,
            platform_code, region, language, channel, question_text,
            scheduled_date, sample_index, run_kind, business_key
          ) VALUES (
            ${cell.tenant_id}, ${cell.brand_project_id}, ${cell.matrix_cell_id}, ${cell.question_version_id},
            ${cell.platform_entry_code}, ${cell.platform_entry_version_id},
            ${cell.platform_entry_version}, ${cell.platform_code}, ${cell.region},
            ${cell.language}, ${cell.channel}, ${cell.question_text},
            ${input.scheduledDate}, ${sampleIndex}, 'scheduled', ${businessKey}
          )
          ON CONFLICT (business_key) DO NOTHING
          RETURNING id
        `;
        inserted += created.length;
      }
    }

    return { planned, inserted };
  });
}

const LEASED_TASK_COLUMNS = [
  "task.id",
  "task.tenant_id",
  "task.brand_project_id",
  "task.platform_code",
  "task.platform_entry_code",
  "task.platform_entry_version_id",
  "task.platform_entry_version",
  "task.question_version_id",
  "task.question_text",
  "task.region",
  "task.language",
  "task.channel",
  "task.lease_version",
  "task.lease_expires_at",
  "task.lease_session_profile_id",
];

/**
 * Leases tasks for one collector. With `sessionProfileId` the account-pool path applies
 * (profile, platform gate, fairness; capacity 1). Without it the legacy path is unchanged, but
 * only for tenants that registered no profile for a requested platform in this region.
 */
export async function leaseTasks(database: Database, request: LeaseRequest): Promise<LeasedTask[]> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${request.tenantId}, true)`;
    const sessionProfileId = request.sessionProfileId;
    let tasks: LeasedTaskRow[];
    if (sessionProfileId) {
      const granted = await leaseTaskIdsForSessionProfile(transaction, {
        ...request,
        sessionProfileId,
      });
      tasks =
        granted.length === 0
          ? []
          : await transaction<LeasedTaskRow[]>`
              SELECT ${transaction(LEASED_TASK_COLUMNS)}
              FROM collection_tasks AS task
              WHERE task.tenant_id = ${request.tenantId}
                AND task.id = ANY(${transaction.array(granted.map((row) => row.id))}::uuid[])
            `;
    } else if (await tenantUsesSessionProfiles(transaction, request)) {
      return [];
    } else {
      tasks = await transaction<LeasedTaskRow[]>`
        WITH candidates AS (
          SELECT id
          FROM collection_tasks
          WHERE tenant_id = ${request.tenantId}
            AND region = ${request.region}
            AND platform_code = ANY(${transaction.array(request.supportedPlatformCodes)})
            AND scheduled_date <= current_date
            AND (not_before IS NULL OR not_before <= now())
            AND (
              status = 'pending'
              OR (status = 'leased' AND lease_expires_at <= now())
            )
          ORDER BY scheduled_date, queued_at, id
          FOR UPDATE SKIP LOCKED
          LIMIT ${request.capacity}
        )
        UPDATE collection_tasks AS task
        SET
          status = 'leased',
          lease_version = task.lease_version + 1,
          lease_owner = ${request.nodeId},
          lease_expires_at = now() + make_interval(secs => ${request.leaseSeconds}),
          lease_session_profile_id = NULL,
          started_at = COALESCE(task.started_at, now())
        FROM candidates
        WHERE task.id = candidates.id
        RETURNING ${transaction(LEASED_TASK_COLUMNS)}
      `;
    }

    for (const task of tasks) {
      await transaction`
        INSERT INTO collection_attempts (
          tenant_id, task_id, lease_version, node_id, region, event_kind, session_profile_id
        ) VALUES (
          ${task.tenant_id}, ${task.id}, ${task.lease_version}, ${request.nodeId}, ${task.region},
          'leased', ${task.lease_session_profile_id}
        )
      `;
    }

    return tasks.map((task) => ({
      id: task.id,
      tenantId: task.tenant_id,
      brandProjectId: task.brand_project_id,
      platformCode: task.platform_code,
      platformEntryCode: task.platform_entry_code,
      platformEntryVersionId: task.platform_entry_version_id,
      platformEntryVersion: task.platform_entry_version,
      questionVersionId: task.question_version_id,
      questionText: task.question_text,
      region: task.region,
      language: task.language,
      channel: task.channel,
      leaseVersion: task.lease_version,
      leaseExpiresAt: task.lease_expires_at.toISOString(),
      sessionProfileId: task.lease_session_profile_id,
    }));
  });
}

export async function heartbeatLease(
  database: Database,
  input: {
    tenantId: string;
    taskId: string;
    nodeId: string;
    region: "cn" | "overseas";
    leaseVersion: number;
    extendSeconds: number;
  },
): Promise<string> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${input.tenantId}, true)`;
    const renewed = await transaction<{ lease_expires_at: Date; region: "cn" | "overseas" }[]>`
      UPDATE collection_tasks
      SET lease_expires_at = now() + make_interval(secs => ${input.extendSeconds})
      WHERE tenant_id = ${input.tenantId}
        AND id = ${input.taskId}
        AND status = 'leased'
        AND lease_owner = ${input.nodeId}
        AND region = ${input.region}
        AND lease_version = ${input.leaseVersion}
        AND lease_expires_at > now()
      RETURNING lease_expires_at, region
    `;
    const lease = renewed[0];
    if (!lease) {
      throw new DomainError("STALE_LEASE", "The task lease is no longer current", 409);
    }
    await transaction`
      INSERT INTO collection_attempts (tenant_id, task_id, lease_version, node_id, region, event_kind)
      VALUES (${input.tenantId}, ${input.taskId}, ${input.leaseVersion}, ${input.nodeId}, ${lease.region}, 'heartbeat')
    `;
    return lease.lease_expires_at.toISOString();
  });
}

export async function submitTaskResult(
  database: Database,
  submission: ResultSubmission,
): Promise<ResultSubmissionResponse> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${submission.tenantId}, true)`;
    const tasks = await transaction<TaskSubmissionRow[]>`
      SELECT id, status, region, lease_version, lease_owner, lease_expires_at, lease_session_profile_id
      FROM collection_tasks
      WHERE tenant_id = ${submission.tenantId} AND id = ${submission.taskId}
      FOR UPDATE
    `;
    const task = tasks[0];
    if (!task) {
      throw new DomainError("TASK_NOT_FOUND", "Task was not found in the current tenant", 404);
    }
    if (task.region !== submission.region) {
      throw new DomainError("REGION_MISMATCH", "Collector region does not match task region", 409);
    }

    const priorSubmission = await transaction<{ disposition: SubmissionDisposition }[]>`
      SELECT disposition
      FROM result_submissions
      WHERE task_id = ${submission.taskId} AND idempotency_key = ${submission.idempotencyKey}
    `;
    if (priorSubmission.length > 0) {
      const sample = await transaction<{ id: string }[]>`
        SELECT id FROM answer_samples WHERE task_id = ${submission.taskId}
      `;
      await transaction`
        INSERT INTO collection_attempts (
          tenant_id, task_id, lease_version, node_id, region, event_kind, idempotency_key
        ) VALUES (
          ${submission.tenantId}, ${submission.taskId}, ${submission.leaseVersion},
          ${submission.nodeId}, ${submission.region}, 'duplicate', ${submission.idempotencyKey}
        )
      `;
      return { disposition: "duplicate", sampleId: sample[0]?.id ?? null };
    }

    const payloadHash = hashResultPayload(submission.answerText, submission.citations);
    const leaseIsStale =
      task.status !== "leased" ||
      task.lease_version !== submission.leaseVersion ||
      task.lease_owner !== submission.nodeId ||
      task.lease_expires_at === null ||
      task.lease_expires_at.getTime() <= Date.now();

    if (leaseIsStale) {
      await recordRejectedSubmission(transaction, submission, payloadHash, "stale");
      return { disposition: "stale", sampleId: null };
    }

    const submissions = await transaction<{ id: string }[]>`
      INSERT INTO result_submissions (
        tenant_id, task_id, lease_version, node_id, region, idempotency_key, payload_hash,
        disposition, answer_text, citations, egress_country, collector_build_revision, completed_at
      ) VALUES (
        ${submission.tenantId}, ${submission.taskId}, ${submission.leaseVersion}, ${submission.nodeId},
        ${submission.region}, ${submission.idempotencyKey}, ${payloadHash}, 'accepted', ${submission.answerText},
        ${transaction.json(submission.citations)}, ${submission.egressCountry},
        ${submission.collectorBuildRevision}, ${submission.completedAt}
      )
      RETURNING id
    `;
    const resultSubmissionId = submissions[0]?.id;
    if (!resultSubmissionId) {
      throw new DomainError("RESULT_WRITE_FAILED", "Result submission was not persisted", 500);
    }

    // The profile comes from the control-plane lease record, never from the request body.
    const samples = await transaction<{ id: string }[]>`
      INSERT INTO answer_samples (
        tenant_id, task_id, result_submission_id, collector_node_id,
        collector_region, egress_country, collector_build_revision, valid, session_profile_id
      ) VALUES (
        ${submission.tenantId}, ${submission.taskId}, ${resultSubmissionId}, ${submission.nodeId},
        ${submission.region}, ${submission.egressCountry}, ${submission.collectorBuildRevision}, true,
        ${task.lease_session_profile_id}
      )
      RETURNING id
    `;
    const sampleId = samples[0]?.id;
    if (!sampleId) {
      throw new DomainError("SAMPLE_WRITE_FAILED", "Answer sample was not persisted", 500);
    }

    await transaction`
      INSERT INTO raw_answers (tenant_id, answer_sample_id, body, content_hash)
      VALUES (${submission.tenantId}, ${sampleId}, ${submission.answerText}, ${payloadHash})
    `;
    for (const citation of new Set(submission.citations)) {
      await transaction`
        INSERT INTO citations (tenant_id, answer_sample_id, url)
        VALUES (${submission.tenantId}, ${sampleId}, ${citation})
        ON CONFLICT (answer_sample_id, url) DO NOTHING
      `;
    }
    await transaction`
      UPDATE collection_tasks
      SET
        status = 'completed',
        lease_owner = NULL,
        lease_expires_at = NULL,
        answer_completed_at = ${submission.completedAt},
        completed_at = now()
      WHERE id = ${submission.taskId} AND tenant_id = ${submission.tenantId}
    `;
    await transaction`
      INSERT INTO collection_attempts (
        tenant_id, task_id, lease_version, node_id, region, event_kind, idempotency_key,
        payload_hash, session_profile_id
      ) VALUES (
        ${submission.tenantId}, ${submission.taskId}, ${submission.leaseVersion}, ${submission.nodeId},
        ${submission.region}, 'accepted', ${submission.idempotencyKey}, ${payloadHash},
        ${task.lease_session_profile_id}
      )
    `;

    return { disposition: "accepted", sampleId };
  });
}

async function recordRejectedSubmission(
  transaction: postgres.TransactionSql,
  submission: ResultSubmission,
  payloadHash: string,
  disposition: Exclude<SubmissionDisposition, "accepted">,
): Promise<void> {
  await transaction`
    INSERT INTO result_submissions (
      tenant_id, task_id, lease_version, node_id, region, idempotency_key, payload_hash,
      disposition, answer_text, citations, egress_country, collector_build_revision, completed_at
    ) VALUES (
      ${submission.tenantId}, ${submission.taskId}, ${submission.leaseVersion}, ${submission.nodeId},
      ${submission.region}, ${submission.idempotencyKey}, ${payloadHash}, ${disposition}, ${submission.answerText},
      ${transaction.json(submission.citations)}, ${submission.egressCountry},
      ${submission.collectorBuildRevision}, ${submission.completedAt}
    )
  `;
  await transaction`
    INSERT INTO collection_attempts (
      tenant_id, task_id, lease_version, node_id, region, event_kind, idempotency_key, payload_hash
    ) VALUES (
      ${submission.tenantId}, ${submission.taskId}, ${submission.leaseVersion}, ${submission.nodeId},
      ${submission.region}, ${disposition}, ${submission.idempotencyKey}, ${payloadHash}
    )
  `;
}

export async function persistDailyBrandMetricRollup(
  database: Database,
  input: PersistDailyBrandMetricRollupInput,
): Promise<{ rollupId: string; observationCount: number }> {
  const metrics = deriveBrandGeoMetricInputs(input.memberships);
  const observations = calculateBrandGeoMetrics(metrics);
  const inputHash = hashBrandMetricMemberships(input.memberships);

  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${input.tenantId}, true)`;
    const insertedRollups = await transaction<{ id: string }[]>`
      INSERT INTO daily_brand_metric_rollups (
        tenant_id, brand_project_id, scheduled_date, brand_id, brand_rule_version_id,
        question_id, question_version_id, topic_group_id, platform_entry_code,
        platform_entry_version_id, platform_entry_version, platform_code,
        region, language, channel, parser_version_id,
        planned_task_count, completed_task_count, failed_task_count, missing_task_count,
        valid_answer_count, mentioned_answer_count, total_mention_count,
        all_monitored_brand_mention_count, rank_sum, rank_count, first_rank_count,
        top3_count, cited_answer_count, positive_count, neutral_count, negative_count,
        input_hash, sov_lineage_version
      ) VALUES (
        ${input.tenantId}, ${input.brandProjectId}, ${input.scheduledDate},
        ${input.brandId}, ${input.brandRuleVersionId}, ${input.questionId},
        ${input.questionVersionId}, ${input.topicGroupId}, ${input.platformEntryCode},
        ${input.platformEntryVersionId}, ${input.platformEntryVersion},
        ${input.platformCode}, ${input.region}, ${input.language}, ${input.channel},
        ${input.parserVersionId}, ${metrics.plannedTaskCount},
        ${metrics.completedTaskCount}, ${metrics.failedTaskCount},
        ${metrics.missingTaskCount}, ${metrics.validAnswerCount},
        ${metrics.mentionedAnswerCount}, ${metrics.totalMentionCount},
        ${metrics.allMonitoredBrandMentionCount}, ${metrics.rankSum},
        ${metrics.rankCount}, ${metrics.firstRankCount},
        ${metrics.top3Count}, ${metrics.citedAnswerCount},
        ${metrics.positiveCount}, ${metrics.neutralCount},
        ${metrics.negativeCount}, ${inputHash}, 1
      )
      ON CONFLICT (
        tenant_id, brand_project_id, scheduled_date, brand_rule_version_id,
        question_version_id, platform_entry_version_id, region, language, channel,
        parser_version_id, input_hash
      ) DO NOTHING
      RETURNING id
    `;

    let rollupId = insertedRollups[0]?.id;
    if (!rollupId) {
      const existing = await transaction<{ id: string }[]>`
        SELECT id
        FROM daily_brand_metric_rollups
        WHERE tenant_id = ${input.tenantId}
          AND brand_project_id = ${input.brandProjectId}
          AND scheduled_date = ${input.scheduledDate}
          AND brand_rule_version_id = ${input.brandRuleVersionId}
          AND question_version_id = ${input.questionVersionId}
          AND platform_entry_version_id = ${input.platformEntryVersionId}
          AND region = ${input.region}
          AND language = ${input.language}
          AND channel = ${input.channel}
          AND parser_version_id = ${input.parserVersionId}
          AND input_hash = ${inputHash}
      `;
      rollupId = existing[0]?.id;
    }
    if (!rollupId) {
      throw new DomainError("METRIC_ROLLUP_WRITE_FAILED", "Metric rollup was not persisted", 500);
    }

    const metricVersions = await transaction<ActiveMetricVersionRow[]>`
      SELECT id, metric_code
      FROM metric_versions
      WHERE active = true
        AND metric_code = ANY(${transaction.array([...BRAND_GEO_METRIC_CODES])})
    `;
    const metricVersionByCode = new Map(
      metricVersions.map((metricVersion) => [metricVersion.metric_code, metricVersion.id]),
    );
    const missingMetricCode = BRAND_GEO_METRIC_CODES.find(
      (metricCode) => !metricVersionByCode.has(metricCode),
    );
    if (missingMetricCode) {
      throw new DomainError(
        "METRIC_VERSION_NOT_FOUND",
        `No active metric version exists for ${missingMetricCode}`,
        500,
      );
    }

    for (const observation of observations) {
      await transaction`
        INSERT INTO brand_metric_observations (
          tenant_id, rollup_id, brand_id, metric_version_id,
          numerator, denominator, value, participating_sample_count
        ) VALUES (
          ${input.tenantId}, ${rollupId}, ${input.brandId},
          ${metricVersionByCode.get(observation.code)!}, ${observation.numerator},
          ${observation.denominator}, ${observation.value},
          ${observation.participatingSampleCount}
        )
        ON CONFLICT (tenant_id, rollup_id, metric_version_id) DO NOTHING
      `;
    }

    for (const membership of input.memberships) {
      const monitoredBrandMentionCount = (membership.shareOfVoiceFacts ?? []).reduce(
        (sum, fact) => sum + fact.mentionCount,
        0,
      );
      await transaction`
        INSERT INTO daily_brand_metric_memberships (
          tenant_id, rollup_id, brand_id, task_id, answer_sample_id,
          brand_fact_id, inclusion_status, contribution
        ) VALUES (
          ${input.tenantId}, ${rollupId}, ${input.brandId}, ${membership.taskId},
          ${membership.answerSampleId}, ${membership.brandFactId},
          ${membership.inclusionStatus}, ${transaction.json({
            mentionCount: membership.contribution.mentionCount,
            monitoredBrandMentionCount,
            firstRank: membership.contribution.firstRank,
            cited: membership.contribution.cited,
            sentiment: membership.contribution.sentiment,
          })}
        )
        ON CONFLICT (tenant_id, rollup_id, task_id) DO NOTHING
      `;
      for (const correctionId of membership.appliedCorrectionIds ?? []) {
        await transaction`
          INSERT INTO brand_metric_membership_corrections (
            tenant_id, rollup_id, task_id, brand_fact_id, correction_id, field_name
          )
          SELECT
            ${input.tenantId}, ${rollupId}, ${membership.taskId},
            correction.brand_fact_id, correction.id, correction.field_name
          FROM brand_fact_corrections AS correction
          WHERE correction.tenant_id = ${input.tenantId}
            AND correction.id = ${correctionId}
          ON CONFLICT (tenant_id, rollup_id, task_id, correction_id) DO NOTHING
        `;
        const persistedCorrections = await transaction<{ correction_id: string }[]>`
          SELECT correction_id
          FROM brand_metric_membership_corrections
          WHERE tenant_id = ${input.tenantId}
            AND rollup_id = ${rollupId}
            AND task_id = ${membership.taskId}
            AND correction_id = ${correctionId}
        `;
        if (persistedCorrections.length === 0) {
          throw new DomainError(
            "METRIC_CORRECTION_NOT_FOUND",
            `Metric correction was not persisted: ${correctionId}`,
            400,
          );
        }
      }
      for (const fact of membership.shareOfVoiceFacts ?? []) {
        await transaction`
          INSERT INTO brand_metric_sov_contributions (
            tenant_id, rollup_id, task_id, denominator_brand_id, brand_fact_id,
            brand_rule_version_id, mention_count, applied_mention_count_correction_id,
            applied_correction_field_name
          ) VALUES (
            ${input.tenantId}, ${rollupId}, ${membership.taskId}, ${fact.brandId},
            ${fact.brandFactId}, ${fact.brandRuleVersionId}, ${fact.mentionCount},
            ${fact.appliedMentionCountCorrectionId ?? null},
            ${fact.appliedMentionCountCorrectionId ? "mention_count" : null}
          )
          ON CONFLICT (tenant_id, rollup_id, task_id, brand_fact_id) DO NOTHING
        `;
      }
    }

    return { rollupId, observationCount: observations.length };
  });
}

export async function countTenantRows(
  database: Database,
  tenantId: string,
): Promise<{ tasks: number; samples: number; staleSubmissions: number }> {
  return database.begin(async (transaction) => {
    await transaction`SELECT set_config('app.tenant_id', ${tenantId}, true)`;
    const [tasks, samples, stale] = await Promise.all([
      transaction<
        { count: number }[]
      >`SELECT count(*)::integer AS count FROM collection_tasks WHERE tenant_id = ${tenantId}`,
      transaction<
        { count: number }[]
      >`SELECT count(*)::integer AS count FROM answer_samples WHERE tenant_id = ${tenantId}`,
      transaction<
        { count: number }[]
      >`SELECT count(*)::integer AS count FROM result_submissions WHERE tenant_id = ${tenantId} AND disposition = 'stale'`,
    ]);
    return {
      tasks: tasks[0]?.count ?? 0,
      samples: samples[0]?.count ?? 0,
      staleSubmissions: stale[0]?.count ?? 0,
    };
  });
}

export async function resetDatabaseForTests(database: Database): Promise<void> {
  await database.unsafe(`
    TRUNCATE TABLE
      brand_metric_sov_contributions, brand_metric_membership_corrections,
      answer_citation_dimension_snapshots, citation_articles, normalized_urls,
      citation_domains, parser_versions,
      citations, raw_answers, answer_samples, result_submissions, collection_attempts,
      session_health_events, platform_dispatch_states, session_profiles,
      collection_tasks, matrix_cells, monitoring_matrices, platform_entry_versions,
      platform_entries, platforms,
      question_versions, questions, topic_groups, brands, brand_projects,
      memberships, users, tenants
    RESTART IDENTITY CASCADE
  `);
}
