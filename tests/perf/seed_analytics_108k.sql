-- One-time synthetic workload for the isolated jogeoai_perf_20260927 database.
-- Prepare it with migrations 001-007, a tenant, a brand Site, and 200 Prompts.
-- Keep the database after measurement; project rules require approval to delete it.
-- Do not run against a shared or business database.

SET @tenant = UUID_TO_BIN('971e151d-a2cd-4d18-aab5-aaa900000001');
SET @site = UUID_TO_BIN('d6f815f8-0b88-49c3-a6b3-6781aa1f9e49');
SET @release = (SELECT published_release_id FROM site_analysis_state WHERE site_id = @site);

-- 90 daily Runs × 6 targets × 200 Prompts = 108,000 successful Checks.
INSERT INTO visibility_runs (id, tenant_id, site_id, status, idempotency_key,
    requested_at, started_at, completed_at, total_checks, succeeded_checks,
    created_at, updated_at, subject_config_version, target_config_version)
SELECT UUID_TO_BIN(UUID()), @tenant, @site, 'completed', CONCAT('perf-20260927-', n),
       DATE_SUB(UTC_TIMESTAMP(6), INTERVAL (90 - n) DAY),
       DATE_SUB(UTC_TIMESTAMP(6), INTERVAL (90 - n) DAY),
       DATE_SUB(UTC_TIMESTAMP(6), INTERVAL (90 - n) DAY), 1200, 1200,
       DATE_SUB(UTC_TIMESTAMP(6), INTERVAL (90 - n) DAY), UTC_TIMESTAMP(6), 1, 1
FROM JSON_TABLE(CONCAT('[', REPEAT('0,', 89), '0]'), '$[*]'
    COLUMNS (n FOR ORDINALITY)) AS days;

INSERT INTO run_engines (id, run_id, engine, provider, sort_order, created_at,
    target_key, capture_mode, country, locale)
SELECT UUID_TO_BIN(UUID()), r.id, engines.engine, 'cloro', engines.sort_order,
       r.requested_at, SHA2(CONCAT('perf:', engines.engine), 256), 'scraped', 'US', 'en-US'
FROM visibility_runs AS r
CROSS JOIN (
    SELECT 'chatgpt' AS engine, 1 AS sort_order UNION ALL
    SELECT 'perplexity', 2 UNION ALL SELECT 'copilot', 3 UNION ALL
    SELECT 'gemini', 4 UNION ALL SELECT 'google-ai-mode', 5 UNION ALL
    SELECT 'google-ai-overview', 6
) AS engines
WHERE r.site_id = @site;

INSERT INTO visibility_checks (id, site_id, run_id, prompt_id, engine, provider,
    status, prompt_snapshot, model_version, answer, search_queries, created_at,
    updated_at, tenant_id, captured_at, capture_mode, evidence_class, locale,
    country, answer_present, run_target_id, sample_index, prompt_version_id,
    prompt_tags_snapshot, branded_status_snapshot, branded_source_snapshot,
    subject_config_version, snapshot_missing, query_availability)
SELECT UUID_TO_BIN(UUID()), @site, r.id, p.id, e.engine, 'cloro', 'succeeded',
       p.content, 'perf-v1', 'Performance Fixture is visible in this answer.',
       JSON_ARRAY('performance comparison'), r.requested_at, r.requested_at,
       @tenant, r.requested_at, 'scraped', 'observed_surface', 'en-US', 'US',
       TRUE, e.id, 1, p.current_version_id, JSON_ARRAY(), 'unbranded', 'system',
       1, FALSE, 'available'
FROM visibility_runs AS r JOIN run_engines AS e ON e.run_id = r.id
JOIN prompts AS p ON p.site_id = @site
WHERE r.site_id = @site;

-- Five distinct external URL events per Check: 540,000 citation rows.
INSERT INTO citations (id, check_id, normalized_url, title, source_domain,
    position, created_at)
SELECT UUID_TO_BIN(UUID()), c.id,
       CONCAT('https://perf-', positions.n, '.example.test/article/', HEX(c.id)),
       CONCAT('Performance source ', positions.n),
       CONCAT('perf-', positions.n, '.example.test'), positions.n, c.captured_at
FROM visibility_checks AS c
CROSS JOIN JSON_TABLE('[0,0,0,0,0]', '$[*]'
    COLUMNS (n FOR ORDINALITY)) AS positions
WHERE c.site_id = @site;

INSERT INTO analytics_check_arrivals (check_id, tenant_id, site_id, captured_at,
    arrived_at)
SELECT id, @tenant, @site, captured_at, captured_at
FROM visibility_checks WHERE site_id = @site;

INSERT INTO analysis_check_work (release_id, check_id, tenant_id, site_id,
    status, created_at, updated_at)
SELECT @release, id, @tenant, @site, 'completed', captured_at, UTC_TIMESTAMP(6)
FROM visibility_checks WHERE site_id = @site;

INSERT INTO analysis_events (release_id, check_id, completed_at)
SELECT @release, id, UTC_TIMESTAMP(6)
FROM visibility_checks WHERE site_id = @site;

INSERT INTO check_analysis (release_id, check_id, tenant_id, site_id, status,
    configuration_missing, completed_event_id, completed_at)
SELECT @release, e.check_id, @tenant, @site, 'completed', FALSE, e.id, e.completed_at
FROM analysis_events AS e WHERE e.release_id = @release;

-- The detail-v1 release contains one real expanded query and five external
-- classified URL events per Check. Hashes match the domain rule versions.
INSERT INTO check_query_events (release_id, check_id, query_hash, query_key,
    original_query, first_position, included, normalization_version)
SELECT @release, id, SHA2('PERFORMANCE COMPARISON', 256),
       'PERFORMANCE COMPARISON', 'performance comparison', 1, TRUE, 'query-v1'
FROM visibility_checks WHERE site_id = @site;

INSERT INTO query_term_facts (release_id, check_id, query_hash, term_hash,
    term, relation, tokenizer_version)
SELECT @release, c.id, SHA2('PERFORMANCE COMPARISON', 256),
       SHA2(terms.term, 256), terms.term, terms.relation, 'term-v1'
FROM visibility_checks AS c
CROSS JOIN (
    SELECT 'PERFORMANCE' AS term, 'retained' AS relation UNION ALL
    SELECT 'COMPARISON', 'added' UNION ALL
    SELECT 'PROMPT', 'removed' UNION ALL
    SELECT 'ABOUT', 'removed' UNION ALL
    SELECT 'PRODUCT', 'removed' UNION ALL
    SELECT 'VISIBILITY', 'removed'
) AS terms
WHERE c.site_id = @site;

INSERT INTO query_term_facts (release_id, check_id, query_hash, term_hash,
    term, relation, tokenizer_version)
SELECT @release, c.id, SHA2('PERFORMANCE COMPARISON', 256),
       SHA2(SUBSTRING_INDEX(SUBSTRING_INDEX(c.prompt_snapshot, ' ', 3), ' ', -1), 256),
       SUBSTRING_INDEX(SUBSTRING_INDEX(c.prompt_snapshot, ' ', 3), ' ', -1),
       'removed', 'term-v1'
FROM visibility_checks AS c WHERE c.site_id = @site;

INSERT INTO citation_events (release_id, check_id, url_hash,
    normalized_url, domain, title, first_position, url_version)
SELECT @release, c.check_id, SHA2(c.normalized_url, 256), c.normalized_url,
       c.source_domain, c.title, c.position, 'url-v1'
FROM citations AS c JOIN visibility_checks AS v ON v.id = c.check_id
WHERE v.site_id = @site;

INSERT INTO citation_classifications (release_id, check_id, url_hash,
    source_category, page_type, ownership, rule_version, rule_basis)
SELECT @release, c.check_id, SHA2(c.normalized_url, 256),
       'other', 'article', 'third_party', 'citation-v1', 'url_or_title_pattern'
FROM citations AS c JOIN visibility_checks AS v ON v.id = c.check_id
WHERE v.site_id = @site;


INSERT INTO check_subject_presence (release_id, check_id, subject_id,
    subject_type, first_offset)
SELECT @release, id, @site, 'brand', 0
FROM visibility_checks WHERE site_id = @site;

INSERT INTO mention_evidence (id, release_id, check_id, subject_id,
    subject_type, matched_text, snippet, match_basis, offset_start, offset_end,
    extractor_version, subject_config_version, created_at)
SELECT UUID_TO_BIN(UUID()), @release, id, @site, 'brand',
       'Performance Fixture', 'Performance Fixture is visible in this answer.',
       'name', 0, 19, 'mention-v1', 1, UTC_TIMESTAMP(6)
FROM visibility_checks WHERE site_id = @site;
