import {
  bigint,
  boolean,
  date,
  integer,
  jsonb,
  numeric,
  pgTable,
  text,
  timestamp,
  uuid,
} from "drizzle-orm/pg-core";

export const tenants = pgTable("tenants", {
  id: uuid("id").primaryKey(),
  slug: text("slug").notNull().unique(),
  name: text("name").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const platforms = pgTable("platforms", {
  code: text("code").primaryKey(),
  displayName: text("display_name").notNull(),
  region: text("region").notNull(),
  enabled: boolean("enabled").notNull(),
});

export const platformEntryVersions = pgTable("platform_entry_versions", {
  id: uuid("id").primaryKey(),
  platformEntryCode: text("platform_entry_code").notNull(),
  platformCode: text("platform_code").notNull(),
  version: integer("version").notNull(),
  displayName: text("display_name").notNull(),
  region: text("region").notNull(),
  channel: text("channel").notNull(),
  config: jsonb("config").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const collectionTasks = pgTable("collection_tasks", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  brandProjectId: uuid("brand_project_id").notNull(),
  questionVersionId: uuid("question_version_id").notNull(),
  platformEntryCode: text("platform_entry_code").notNull(),
  platformEntryVersionId: uuid("platform_entry_version_id").notNull(),
  platformEntryVersion: integer("platform_entry_version").notNull(),
  platformCode: text("platform_code").notNull(),
  region: text("region").notNull(),
  language: text("language").notNull(),
  channel: text("channel").notNull(),
  questionText: text("question_text").notNull(),
  scheduledDate: date("scheduled_date").notNull(),
  sampleIndex: integer("sample_index").notNull(),
  runKind: text("run_kind").notNull(),
  businessKey: text("business_key").notNull().unique(),
  status: text("status").notNull(),
  leaseVersion: integer("lease_version").notNull(),
  leaseOwner: text("lease_owner"),
  leaseExpiresAt: timestamp("lease_expires_at", { withTimezone: true }),
  notBefore: timestamp("not_before", { withTimezone: true }),
  failureCount: integer("failure_count").notNull(),
  lastFailureCode: text("last_failure_code"),
  leaseSessionProfileId: uuid("lease_session_profile_id"),
});

export const collectionAttempts = pgTable("collection_attempts", {
  id: bigint("id", { mode: "number" }).primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  taskId: uuid("task_id").notNull(),
  leaseVersion: integer("lease_version").notNull(),
  nodeId: text("node_id").notNull(),
  region: text("region").notNull(),
  eventKind: text("event_kind").notNull(),
  idempotencyKey: text("idempotency_key"),
  payloadHash: text("payload_hash"),
  sessionProfileId: uuid("session_profile_id"),
  failureCode: text("failure_code"),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const answerSamples = pgTable("answer_samples", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  taskId: uuid("task_id").notNull(),
  resultSubmissionId: uuid("result_submission_id").notNull(),
  collectorNodeId: text("collector_node_id").notNull(),
  collectorRegion: text("collector_region").notNull(),
  egressCountry: text("egress_country").notNull(),
  collectorBuildRevision: text("collector_build_revision").notNull(),
  valid: boolean("valid").notNull(),
  sessionProfileId: uuid("session_profile_id"),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const sessionProfiles = pgTable("session_profiles", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  platformCode: text("platform_code").notNull(),
  region: text("region").notNull(),
  label: text("label").notNull(),
  status: text("status").notNull(),
  hourlyQuota: integer("hourly_quota").notNull(),
  dailyQuota: integer("daily_quota").notNull(),
  minIntervalSeconds: integer("min_interval_seconds").notNull(),
  cooldownUntil: timestamp("cooldown_until", { withTimezone: true }),
  consecutiveChallenges: integer("consecutive_challenges").notNull(),
  lastLeasedAt: timestamp("last_leased_at", { withTimezone: true }),
  lastSeenAt: timestamp("last_seen_at", { withTimezone: true }),
  lastProbeFailedAt: timestamp("last_probe_failed_at", { withTimezone: true }),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
  updatedAt: timestamp("updated_at", { withTimezone: true }).notNull(),
});

export const sessionHealthEvents = pgTable("session_health_events", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  region: text("region").notNull(),
  platformCode: text("platform_code").notNull(),
  sessionProfileId: uuid("session_profile_id"),
  eventKind: text("event_kind").notNull(),
  failureCode: text("failure_code"),
  phase: text("phase"),
  taskId: uuid("task_id"),
  leaseVersion: integer("lease_version"),
  nodeId: text("node_id"),
  fromStatus: text("from_status"),
  toStatus: text("to_status"),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const platformDispatchStates = pgTable("platform_dispatch_states", {
  tenantId: uuid("tenant_id").notNull(),
  region: text("region").notNull(),
  platformCode: text("platform_code").notNull(),
  minIntervalSeconds: integer("min_interval_seconds").notNull(),
  dailyCap: integer("daily_cap").notNull(),
  lastDispatchedAt: timestamp("last_dispatched_at", { withTimezone: true }),
  pausedAt: timestamp("paused_at", { withTimezone: true }),
  pauseReason: text("pause_reason"),
  breakerProfileThreshold: integer("breaker_profile_threshold").notNull(),
  breakerWindowMinutes: integer("breaker_window_minutes").notNull(),
  retryBackoffSeconds: integer("retry_backoff_seconds").notNull(),
  maxTaskFailures: integer("max_task_failures").notNull(),
  rateLimitCooldownBaseSeconds: integer("rate_limit_cooldown_base_seconds").notNull(),
  rateLimitCooldownMaxSeconds: integer("rate_limit_cooldown_max_seconds").notNull(),
  probeFailureOfflineMinutes: integer("probe_failure_offline_minutes").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
  updatedAt: timestamp("updated_at", { withTimezone: true }).notNull(),
});

export const resultSubmissions = pgTable("result_submissions", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  taskId: uuid("task_id").notNull(),
  leaseVersion: integer("lease_version").notNull(),
  nodeId: text("node_id").notNull(),
  region: text("region").notNull(),
  idempotencyKey: text("idempotency_key").notNull(),
  payloadHash: text("payload_hash").notNull(),
  disposition: text("disposition").notNull(),
  answerText: text("answer_text").notNull(),
  citations: jsonb("citations").notNull(),
  egressCountry: text("egress_country").notNull(),
  collectorBuildRevision: text("collector_build_revision").notNull(),
  completedAt: timestamp("completed_at", { withTimezone: true }).notNull(),
});

export const brandRuleVersions = pgTable("brand_rule_versions", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  brandId: uuid("brand_id").notNull(),
  version: integer("version").notNull(),
  canonicalName: text("canonical_name").notNull(),
  aliases: text("aliases").array().notNull(),
  exclusionTerms: text("exclusion_terms").array().notNull(),
  ruleHash: text("rule_hash").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const brandMatchRules = pgTable("brand_match_rules", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  brandId: uuid("brand_id").notNull(),
  brandRuleVersionId: uuid("brand_rule_version_id").notNull(),
  ruleKind: text("rule_kind").notNull(),
  matchKind: text("match_kind").notNull(),
  pattern: text("pattern").notNull(),
  normalizedPattern: text("normalized_pattern").notNull(),
  language: text("language"),
  caseSensitive: boolean("case_sensitive").notNull(),
  priority: integer("priority").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const brandDomains = pgTable("brand_domains", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  brandId: uuid("brand_id").notNull(),
  brandRuleVersionId: uuid("brand_rule_version_id").notNull(),
  registrableDomain: text("registrable_domain").notNull(),
  isPrimary: boolean("is_primary").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const parserVersions = pgTable("parser_versions", {
  id: uuid("id").primaryKey(),
  code: text("code").notNull(),
  version: integer("version").notNull(),
  configHash: text("config_hash").notNull(),
  status: text("status").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const answerParses = pgTable("answer_parses", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  answerSampleId: uuid("answer_sample_id").notNull(),
  parserVersionId: uuid("parser_version_id").notNull(),
  brandRuleSetHash: text("brand_rule_set_hash").notNull(),
  status: text("status").notNull(),
  errorCode: text("error_code"),
  parsedAt: timestamp("parsed_at", { withTimezone: true }).notNull(),
});

export const answerBrandFacts = pgTable("answer_brand_facts", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  answerParseId: uuid("answer_parse_id").notNull(),
  answerSampleId: uuid("answer_sample_id").notNull(),
  brandId: uuid("brand_id").notNull(),
  brandRuleVersionId: uuid("brand_rule_version_id").notNull(),
  mentioned: boolean("mentioned").notNull(),
  mentionCount: integer("mention_count").notNull(),
  firstMentionOffset: integer("first_mention_offset"),
  firstMentionRank: integer("first_mention_rank"),
  recommendationRole: text("recommendation_role").notNull(),
  sentiment: text("sentiment").notNull(),
  sentimentConfidence: numeric("sentiment_confidence", { precision: 5, scale: 4 }),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const brandMentionOccurrences = pgTable("brand_mention_occurrences", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  brandFactId: uuid("brand_fact_id").notNull(),
  occurrenceIndex: integer("occurrence_index").notNull(),
  matchedText: text("matched_text").notNull(),
  characterStart: integer("character_start").notNull(),
  characterEnd: integer("character_end").notNull(),
  contextSnippet: text("context_snippet").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const brandFactCorrections = pgTable("brand_fact_corrections", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  brandFactId: uuid("brand_fact_id").notNull(),
  supersedesCorrectionId: uuid("supersedes_correction_id"),
  correctedByUserId: uuid("corrected_by_user_id").notNull(),
  fieldName: text("field_name").notNull(),
  previousValue: jsonb("previous_value").notNull(),
  correctedValue: jsonb("corrected_value").notNull(),
  reason: text("reason").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const brandFactRisks = pgTable("brand_fact_risks", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  brandFactId: uuid("brand_fact_id").notNull(),
  riskCode: text("risk_code").notNull(),
  riskLevel: text("risk_level").notNull(),
  confidence: numeric("confidence", { precision: 5, scale: 4 }),
  contextSnippet: text("context_snippet"),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const brandFactRiskCorrections = pgTable("brand_fact_risk_corrections", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  brandFactRiskId: uuid("brand_fact_risk_id").notNull(),
  supersedesCorrectionId: uuid("supersedes_correction_id"),
  correctedByUserId: uuid("corrected_by_user_id").notNull(),
  previousValue: jsonb("previous_value").notNull(),
  correctedValue: jsonb("corrected_value").notNull(),
  reason: text("reason").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const citationDomains = pgTable("citation_domains", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  canonicalHost: text("canonical_host").notNull(),
  registrableDomain: text("registrable_domain").notNull(),
  sourceCategory: text("source_category").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const normalizedUrls = pgTable("normalized_urls", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  citationDomainId: uuid("citation_domain_id").notNull(),
  urlHash: text("url_hash").notNull(),
  canonicalUrl: text("canonical_url").notNull(),
  scheme: text("scheme").notNull(),
  host: text("host").notNull(),
  registrableDomain: text("registrable_domain").notNull(),
  path: text("path").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const citationArticles = pgTable("citation_articles", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  normalizedUrlId: uuid("normalized_url_id").notNull(),
  citationDomainId: uuid("citation_domain_id").notNull(),
  title: text("title"),
  sourceType: text("source_type").notNull(),
  publishedAt: timestamp("published_at", { withTimezone: true }),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const answerCitations = pgTable("answer_citations", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  answerSampleId: uuid("answer_sample_id").notNull(),
  normalizedUrlId: uuid("normalized_url_id").notNull(),
  citationIndex: integer("citation_index").notNull(),
  originalUrl: text("original_url").notNull(),
  title: text("title"),
  sourceLabel: text("source_label"),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const answerCitationDimensionSnapshots = pgTable("answer_citation_dimension_snapshots", {
  tenantId: uuid("tenant_id").notNull(),
  answerCitationId: uuid("answer_citation_id").notNull(),
  answerSampleId: uuid("answer_sample_id").notNull(),
  normalizedUrlId: uuid("normalized_url_id").notNull(),
  citationDomainId: uuid("citation_domain_id").notNull(),
  canonicalHost: text("canonical_host").notNull(),
  registrableDomain: text("registrable_domain").notNull(),
  sourceCategory: text("source_category").notNull(),
  urlHash: text("url_hash").notNull(),
  canonicalUrl: text("canonical_url").notNull(),
  scheme: text("scheme").notNull(),
  host: text("host").notNull(),
  path: text("path").notNull(),
  citationArticleId: uuid("citation_article_id"),
  articleTitle: text("article_title"),
  articleSourceType: text("article_source_type"),
  articlePublishedAt: timestamp("article_published_at", { withTimezone: true }),
  capturedAt: timestamp("captured_at", { withTimezone: true }).notNull(),
});

export const questionVersions = pgTable("question_versions", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  questionId: uuid("question_id").notNull(),
  brandProjectId: uuid("brand_project_id").notNull(),
  topicGroupId: uuid("topic_group_id").notNull(),
  topicGroupName: text("topic_group_name").notNull(),
  topicGroupSortOrder: integer("topic_group_sort_order").notNull(),
  topicGroupStatus: text("topic_group_status").notNull(),
  version: integer("version").notNull(),
  questionText: text("question_text").notNull(),
  language: text("language").notNull(),
  fixture: boolean("fixture").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const questionTags = pgTable("question_tags", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  brandProjectId: uuid("brand_project_id").notNull(),
  tagType: text("tag_type").notNull(),
  name: text("name").notNull(),
  status: text("status").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const questionTagAssignments = pgTable("question_tag_assignments", {
  tenantId: uuid("tenant_id").notNull(),
  brandProjectId: uuid("brand_project_id").notNull(),
  questionVersionId: uuid("question_version_id").notNull(),
  questionTagId: uuid("question_tag_id").notNull(),
  tagTypeSnapshot: text("tag_type_snapshot").notNull(),
  tagNameSnapshot: text("tag_name_snapshot").notNull(),
  tagStatusSnapshot: text("tag_status_snapshot").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const metricDefinitions = pgTable("metric_definitions", {
  code: text("code").primaryKey(),
  displayName: text("display_name").notNull(),
  valueKind: text("value_kind").notNull(),
  numeratorName: text("numerator_name").notNull(),
  denominatorName: text("denominator_name"),
  description: text("description").notNull(),
});

export const metricVersions = pgTable("metric_versions", {
  id: uuid("id").primaryKey(),
  metricCode: text("metric_code").notNull(),
  version: integer("version").notNull(),
  formulaDefinition: jsonb("formula_definition").notNull(),
  formulaHash: text("formula_hash").notNull(),
  active: boolean("active").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const dailyBrandMetricRollups = pgTable("daily_brand_metric_rollups", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  brandProjectId: uuid("brand_project_id").notNull(),
  scheduledDate: date("scheduled_date").notNull(),
  brandId: uuid("brand_id").notNull(),
  brandRuleVersionId: uuid("brand_rule_version_id").notNull(),
  questionId: uuid("question_id").notNull(),
  questionVersionId: uuid("question_version_id").notNull(),
  topicGroupId: uuid("topic_group_id").notNull(),
  platformEntryCode: text("platform_entry_code").notNull(),
  platformEntryVersionId: uuid("platform_entry_version_id").notNull(),
  platformEntryVersion: integer("platform_entry_version").notNull(),
  platformCode: text("platform_code").notNull(),
  region: text("region").notNull(),
  language: text("language").notNull(),
  channel: text("channel").notNull(),
  parserVersionId: uuid("parser_version_id").notNull(),
  plannedTaskCount: integer("planned_task_count").notNull(),
  completedTaskCount: integer("completed_task_count").notNull(),
  failedTaskCount: integer("failed_task_count").notNull(),
  missingTaskCount: integer("missing_task_count").notNull(),
  validAnswerCount: integer("valid_answer_count").notNull(),
  mentionedAnswerCount: integer("mentioned_answer_count").notNull(),
  totalMentionCount: integer("total_mention_count").notNull(),
  allMonitoredBrandMentionCount: integer("all_monitored_brand_mention_count").notNull(),
  rankSum: integer("rank_sum").notNull(),
  rankCount: integer("rank_count").notNull(),
  firstRankCount: integer("first_rank_count").notNull(),
  top3Count: integer("top3_count").notNull(),
  citedAnswerCount: integer("cited_answer_count").notNull(),
  positiveCount: integer("positive_count").notNull(),
  neutralCount: integer("neutral_count").notNull(),
  negativeCount: integer("negative_count").notNull(),
  inputHash: text("input_hash").notNull(),
  sovLineageVersion: integer("sov_lineage_version").notNull(),
  calculatedAt: timestamp("calculated_at", { withTimezone: true }).notNull(),
});

export const brandMetricObservations = pgTable("brand_metric_observations", {
  id: uuid("id").primaryKey(),
  tenantId: uuid("tenant_id").notNull(),
  rollupId: uuid("rollup_id").notNull(),
  brandId: uuid("brand_id").notNull(),
  metricVersionId: uuid("metric_version_id").notNull(),
  numerator: numeric("numerator", { precision: 24, scale: 8 }).notNull(),
  denominator: numeric("denominator", { precision: 24, scale: 8 }),
  value: numeric("value", { precision: 24, scale: 12 }),
  participatingSampleCount: integer("participating_sample_count").notNull(),
});

export const dailyBrandMetricMemberships = pgTable("daily_brand_metric_memberships", {
  tenantId: uuid("tenant_id").notNull(),
  rollupId: uuid("rollup_id").notNull(),
  brandId: uuid("brand_id").notNull(),
  taskId: uuid("task_id").notNull(),
  answerSampleId: uuid("answer_sample_id"),
  brandFactId: uuid("brand_fact_id"),
  inclusionStatus: text("inclusion_status").notNull(),
  contribution: jsonb("contribution").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const brandMetricMembershipCorrections = pgTable("brand_metric_membership_corrections", {
  tenantId: uuid("tenant_id").notNull(),
  rollupId: uuid("rollup_id").notNull(),
  taskId: uuid("task_id").notNull(),
  brandFactId: uuid("brand_fact_id").notNull(),
  correctionId: uuid("correction_id").notNull(),
  fieldName: text("field_name").notNull(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});

export const brandMetricSovContributions = pgTable("brand_metric_sov_contributions", {
  tenantId: uuid("tenant_id").notNull(),
  rollupId: uuid("rollup_id").notNull(),
  taskId: uuid("task_id").notNull(),
  denominatorBrandId: uuid("denominator_brand_id").notNull(),
  brandFactId: uuid("brand_fact_id").notNull(),
  brandRuleVersionId: uuid("brand_rule_version_id").notNull(),
  mentionCount: integer("mention_count").notNull(),
  appliedMentionCountCorrectionId: uuid("applied_mention_count_correction_id"),
  appliedCorrectionFieldName: text("applied_correction_field_name"),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull(),
});
