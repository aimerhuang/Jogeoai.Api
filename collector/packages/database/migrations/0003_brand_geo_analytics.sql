ALTER TABLE brands
  ADD CONSTRAINT brands_tenant_id_unique UNIQUE (tenant_id, id),
  ADD CONSTRAINT brands_tenant_project_id_unique UNIQUE (tenant_id, brand_project_id, id);

ALTER TABLE platform_entries
  ADD CONSTRAINT platform_entries_platform_code_code_unique UNIQUE (platform_code, code);

ALTER TABLE answer_samples
  ADD CONSTRAINT answer_samples_tenant_task_id_unique UNIQUE (tenant_id, task_id, id);

CREATE TABLE brand_rule_versions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  brand_id uuid NOT NULL,
  version integer NOT NULL CHECK (version > 0),
  canonical_name text NOT NULL CHECK (length(btrim(canonical_name)) > 0),
  aliases text[] NOT NULL DEFAULT ARRAY[]::text[],
  exclusion_terms text[] NOT NULL DEFAULT ARRAY[]::text[],
  rule_hash text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, brand_id, version),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, brand_id, id),
  UNIQUE (tenant_id, brand_id, version, id),
  FOREIGN KEY (tenant_id, brand_id) REFERENCES brands (tenant_id, id)
);

CREATE TABLE brand_match_rules (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  brand_id uuid NOT NULL,
  brand_rule_version_id uuid NOT NULL,
  rule_kind text NOT NULL CHECK (
    rule_kind IN ('canonical_name', 'alias', 'product_name', 'exclusion')
  ),
  match_kind text NOT NULL CHECK (match_kind IN ('exact', 'token', 'regex')),
  pattern text NOT NULL CHECK (length(btrim(pattern)) > 0),
  normalized_pattern text NOT NULL CHECK (length(btrim(normalized_pattern)) > 0),
  language text,
  case_sensitive boolean NOT NULL DEFAULT false,
  priority integer NOT NULL DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  UNIQUE (
    tenant_id, brand_rule_version_id, rule_kind,
    match_kind, normalized_pattern, language
  ),
  FOREIGN KEY (tenant_id, brand_id, brand_rule_version_id)
    REFERENCES brand_rule_versions (tenant_id, brand_id, id)
);

CREATE TABLE brand_domains (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  brand_id uuid NOT NULL,
  brand_rule_version_id uuid NOT NULL,
  registrable_domain text NOT NULL CHECK (registrable_domain = lower(registrable_domain)),
  is_primary boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT brand_domains_tenant_rule_domain_unique
    UNIQUE (tenant_id, brand_rule_version_id, registrable_domain),
  UNIQUE (tenant_id, id),
  CONSTRAINT brand_domains_tenant_brand_rule_fk
    FOREIGN KEY (tenant_id, brand_id, brand_rule_version_id)
    REFERENCES brand_rule_versions (tenant_id, brand_id, id)
);

ALTER TABLE brands NO FORCE ROW LEVEL SECURITY;

INSERT INTO brand_rule_versions (
  tenant_id, brand_id, version, canonical_name, aliases, exclusion_terms, rule_hash
)
SELECT
  brand.tenant_id,
  brand.id,
  brand.rule_version,
  brand.canonical_name,
  ARRAY(SELECT jsonb_array_elements_text(brand.aliases)),
  ARRAY(SELECT jsonb_array_elements_text(brand.exclusion_terms)),
  md5(
    jsonb_build_object(
      'canonicalName', brand.canonical_name,
      'aliases', brand.aliases,
      'exclusionTerms', brand.exclusion_terms,
      'version', brand.rule_version
    )::text
  )
FROM brands AS brand
ON CONFLICT (tenant_id, brand_id, version) DO NOTHING;

INSERT INTO brand_match_rules (
  tenant_id, brand_id, brand_rule_version_id, rule_kind,
  match_kind, pattern, normalized_pattern, priority
)
SELECT
  version.tenant_id,
  version.brand_id,
  version.id,
  'canonical_name',
  'exact',
  version.canonical_name,
  lower(btrim(version.canonical_name)),
  100
FROM brand_rule_versions AS version
ON CONFLICT DO NOTHING;

INSERT INTO brand_match_rules (
  tenant_id, brand_id, brand_rule_version_id, rule_kind,
  match_kind, pattern, normalized_pattern, priority
)
SELECT
  version.tenant_id,
  version.brand_id,
  version.id,
  'alias',
  'exact',
  alias.pattern,
  lower(btrim(alias.pattern)),
  90
FROM brand_rule_versions AS version
CROSS JOIN LATERAL unnest(version.aliases) AS alias(pattern)
WHERE length(btrim(alias.pattern)) > 0
ON CONFLICT DO NOTHING;

INSERT INTO brand_match_rules (
  tenant_id, brand_id, brand_rule_version_id, rule_kind,
  match_kind, pattern, normalized_pattern, priority
)
SELECT
  version.tenant_id,
  version.brand_id,
  version.id,
  'exclusion',
  'exact',
  exclusion.pattern,
  lower(btrim(exclusion.pattern)),
  1000
FROM brand_rule_versions AS version
CROSS JOIN LATERAL unnest(version.exclusion_terms) AS exclusion(pattern)
WHERE length(btrim(exclusion.pattern)) > 0
ON CONFLICT DO NOTHING;

ALTER TABLE brands FORCE ROW LEVEL SECURITY;

CREATE TABLE parser_versions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  code text NOT NULL,
  version integer NOT NULL CHECK (version > 0),
  config_hash text NOT NULL,
  status text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'retired')),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (code, version),
  UNIQUE (code, config_hash)
);

CREATE TABLE answer_parses (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  answer_sample_id uuid NOT NULL,
  parser_version_id uuid NOT NULL REFERENCES parser_versions(id),
  brand_rule_set_hash text NOT NULL,
  status text NOT NULL CHECK (status IN ('completed', 'failed')),
  error_code text,
  parsed_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, answer_sample_id, parser_version_id, brand_rule_set_hash),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, answer_sample_id, id),
  FOREIGN KEY (tenant_id, answer_sample_id) REFERENCES answer_samples (tenant_id, id),
  CHECK ((status = 'completed' AND error_code IS NULL) OR status = 'failed')
);

CREATE TABLE answer_brand_facts (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  answer_parse_id uuid NOT NULL,
  answer_sample_id uuid NOT NULL,
  brand_id uuid NOT NULL,
  brand_rule_version_id uuid NOT NULL,
  mentioned boolean NOT NULL,
  mention_count integer NOT NULL CHECK (mention_count >= 0),
  first_mention_offset integer CHECK (first_mention_offset >= 0),
  first_mention_rank integer CHECK (first_mention_rank > 0),
  recommendation_role text NOT NULL CHECK (
    recommendation_role IN (
      'recommended', 'alternative', 'comparison', 'warning', 'neutral', 'unknown', 'not_mentioned'
    )
  ),
  sentiment text NOT NULL CHECK (
    sentiment IN ('positive', 'neutral', 'negative', 'not_applicable')
  ),
  sentiment_confidence numeric(5,4) CHECK (
    sentiment_confidence IS NULL OR sentiment_confidence BETWEEN 0 AND 1
  ),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, answer_parse_id, brand_id),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, answer_sample_id, id),
  UNIQUE (tenant_id, brand_id, id),
  FOREIGN KEY (tenant_id, answer_sample_id, answer_parse_id)
    REFERENCES answer_parses (tenant_id, answer_sample_id, id),
  FOREIGN KEY (tenant_id, answer_sample_id) REFERENCES answer_samples (tenant_id, id),
  FOREIGN KEY (tenant_id, brand_id) REFERENCES brands (tenant_id, id),
  FOREIGN KEY (tenant_id, brand_id, brand_rule_version_id)
    REFERENCES brand_rule_versions (tenant_id, brand_id, id),
  CHECK (
    (
      mentioned = true
      AND mention_count > 0
      AND first_mention_offset IS NOT NULL
      AND first_mention_rank IS NOT NULL
      AND recommendation_role <> 'not_mentioned'
      AND sentiment <> 'not_applicable'
    )
    OR (
      mentioned = false
      AND mention_count = 0
      AND first_mention_offset IS NULL
      AND first_mention_rank IS NULL
      AND recommendation_role = 'not_mentioned'
      AND sentiment = 'not_applicable'
      AND sentiment_confidence IS NULL
    )
  )
);

CREATE OR REPLACE FUNCTION enforce_answer_brand_fact_completed_parse() RETURNS trigger AS $$
DECLARE
  parse answer_parses%ROWTYPE;
BEGIN
  SELECT * INTO parse
  FROM answer_parses
  WHERE tenant_id = NEW.tenant_id
    AND id = NEW.answer_parse_id;

  IF parse.status IS DISTINCT FROM 'completed'
    OR parse.answer_sample_id IS DISTINCT FROM NEW.answer_sample_id
  THEN
    RAISE EXCEPTION 'Brand facts require a completed parse for the same answer sample'
      USING ERRCODE = '23514';
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER answer_brand_facts_completed_parse_guard
  BEFORE INSERT ON answer_brand_facts
  FOR EACH ROW EXECUTE FUNCTION enforce_answer_brand_fact_completed_parse();

CREATE TABLE brand_mention_occurrences (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  brand_fact_id uuid NOT NULL,
  occurrence_index integer NOT NULL CHECK (occurrence_index > 0),
  matched_text text NOT NULL CHECK (length(matched_text) > 0),
  character_start integer NOT NULL CHECK (character_start >= 0),
  character_end integer NOT NULL,
  context_snippet text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, brand_fact_id, occurrence_index),
  FOREIGN KEY (tenant_id, brand_fact_id) REFERENCES answer_brand_facts (tenant_id, id),
  CHECK (character_end > character_start)
);

CREATE TABLE brand_fact_corrections (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  brand_fact_id uuid NOT NULL,
  supersedes_correction_id uuid,
  corrected_by_user_id uuid NOT NULL,
  field_name text NOT NULL CHECK (
    field_name IN (
      'mentioned', 'mention_count', 'first_mention_rank', 'recommendation_role', 'sentiment'
    )
  ),
  previous_value jsonb NOT NULL,
  corrected_value jsonb NOT NULL,
  reason text NOT NULL CHECK (length(btrim(reason)) > 0),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, brand_fact_id) REFERENCES answer_brand_facts (tenant_id, id),
  FOREIGN KEY (tenant_id, supersedes_correction_id)
    REFERENCES brand_fact_corrections (tenant_id, id),
  FOREIGN KEY (tenant_id, corrected_by_user_id) REFERENCES users (tenant_id, id)
);

CREATE UNIQUE INDEX brand_fact_corrections_linear_lineage_idx
  ON brand_fact_corrections (tenant_id, supersedes_correction_id)
  WHERE supersedes_correction_id IS NOT NULL;

CREATE UNIQUE INDEX brand_fact_corrections_one_lineage_root_idx
  ON brand_fact_corrections (tenant_id, brand_fact_id, field_name)
  WHERE supersedes_correction_id IS NULL;

CREATE OR REPLACE FUNCTION enforce_brand_fact_correction_lineage() RETURNS trigger AS $$
DECLARE
  previous_correction brand_fact_corrections%ROWTYPE;
BEGIN
  IF NEW.supersedes_correction_id IS NULL THEN
    RETURN NEW;
  END IF;

  IF NEW.supersedes_correction_id = NEW.id THEN
    RAISE EXCEPTION 'A brand fact correction cannot supersede itself'
      USING ERRCODE = '23514';
  END IF;

  SELECT * INTO previous_correction
  FROM brand_fact_corrections
  WHERE tenant_id = NEW.tenant_id
    AND id = NEW.supersedes_correction_id;

  IF FOUND AND (
    previous_correction.brand_fact_id IS DISTINCT FROM NEW.brand_fact_id
    OR previous_correction.field_name IS DISTINCT FROM NEW.field_name
    OR previous_correction.corrected_value IS DISTINCT FROM NEW.previous_value
  ) THEN
    RAISE EXCEPTION 'A brand fact correction must continue the same fact field and value chain'
      USING ERRCODE = '23514';
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER brand_fact_correction_lineage_guard
  BEFORE INSERT ON brand_fact_corrections
  FOR EACH ROW EXECUTE FUNCTION enforce_brand_fact_correction_lineage();

CREATE TABLE brand_fact_risks (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  brand_fact_id uuid NOT NULL,
  risk_code text NOT NULL CHECK (length(btrim(risk_code)) > 0),
  risk_level text NOT NULL CHECK (risk_level IN ('low', 'medium', 'high')),
  confidence numeric(5,4) CHECK (confidence IS NULL OR confidence BETWEEN 0 AND 1),
  context_snippet text,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, brand_fact_id, risk_code),
  FOREIGN KEY (tenant_id, brand_fact_id) REFERENCES answer_brand_facts (tenant_id, id)
);

CREATE TABLE brand_fact_risk_corrections (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  brand_fact_risk_id uuid NOT NULL,
  supersedes_correction_id uuid,
  corrected_by_user_id uuid NOT NULL,
  previous_value jsonb NOT NULL CHECK (jsonb_typeof(previous_value) = 'object'),
  corrected_value jsonb NOT NULL CHECK (jsonb_typeof(corrected_value) = 'object'),
  reason text NOT NULL CHECK (length(btrim(reason)) > 0),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, brand_fact_risk_id)
    REFERENCES brand_fact_risks (tenant_id, id),
  FOREIGN KEY (tenant_id, supersedes_correction_id)
    REFERENCES brand_fact_risk_corrections (tenant_id, id),
  FOREIGN KEY (tenant_id, corrected_by_user_id) REFERENCES users (tenant_id, id)
);

CREATE UNIQUE INDEX brand_fact_risk_corrections_linear_lineage_idx
  ON brand_fact_risk_corrections (tenant_id, supersedes_correction_id)
  WHERE supersedes_correction_id IS NOT NULL;

CREATE UNIQUE INDEX brand_fact_risk_corrections_one_lineage_root_idx
  ON brand_fact_risk_corrections (tenant_id, brand_fact_risk_id)
  WHERE supersedes_correction_id IS NULL;

CREATE OR REPLACE FUNCTION enforce_brand_fact_risk_correction_lineage() RETURNS trigger AS $$
DECLARE
  previous_correction brand_fact_risk_corrections%ROWTYPE;
BEGIN
  IF NEW.supersedes_correction_id IS NULL THEN
    RETURN NEW;
  END IF;

  IF NEW.supersedes_correction_id = NEW.id THEN
    RAISE EXCEPTION 'A risk correction cannot supersede itself'
      USING ERRCODE = '23514';
  END IF;

  SELECT * INTO previous_correction
  FROM brand_fact_risk_corrections
  WHERE tenant_id = NEW.tenant_id
    AND id = NEW.supersedes_correction_id;

  IF FOUND AND (
    previous_correction.brand_fact_risk_id IS DISTINCT FROM NEW.brand_fact_risk_id
    OR previous_correction.corrected_value IS DISTINCT FROM NEW.previous_value
  ) THEN
    RAISE EXCEPTION 'A risk correction must continue the same risk fact and value chain'
      USING ERRCODE = '23514';
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER brand_fact_risk_correction_lineage_guard
  BEFORE INSERT ON brand_fact_risk_corrections
  FOR EACH ROW EXECUTE FUNCTION enforce_brand_fact_risk_correction_lineage();

CREATE TABLE citation_domains (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  canonical_host text NOT NULL UNIQUE CHECK (canonical_host = lower(canonical_host)),
  registrable_domain text NOT NULL CHECK (registrable_domain = lower(registrable_domain)),
  source_category text NOT NULL DEFAULT 'unknown' CHECK (
    source_category IN ('unknown', 'owned', 'news', 'social', 'ecommerce', 'reference', 'other')
  ),
  created_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT citation_domains_identity_unique
    UNIQUE (id, canonical_host, registrable_domain)
);

CREATE TABLE normalized_urls (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  citation_domain_id uuid NOT NULL REFERENCES citation_domains(id),
  url_hash text NOT NULL UNIQUE,
  canonical_url text NOT NULL,
  scheme text NOT NULL CHECK (scheme IN ('http', 'https')),
  host text NOT NULL CHECK (host = lower(host)),
  registrable_domain text NOT NULL CHECK (registrable_domain = lower(registrable_domain)),
  path text NOT NULL DEFAULT '/',
  created_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT normalized_urls_domain_identity_unique UNIQUE (id, citation_domain_id),
  CONSTRAINT normalized_urls_domain_dimensions_fk
    FOREIGN KEY (citation_domain_id, host, registrable_domain)
    REFERENCES citation_domains (id, canonical_host, registrable_domain)
);

CREATE TABLE citation_articles (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  normalized_url_id uuid NOT NULL UNIQUE REFERENCES normalized_urls(id),
  citation_domain_id uuid NOT NULL REFERENCES citation_domains(id),
  title text,
  source_type text NOT NULL DEFAULT 'web_page' CHECK (
    source_type IN ('web_page', 'article', 'product_page', 'social_post', 'other')
  ),
  published_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT citation_articles_url_domain_fk
    FOREIGN KEY (normalized_url_id, citation_domain_id)
    REFERENCES normalized_urls (id, citation_domain_id)
);

CREATE TABLE answer_citations (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  answer_sample_id uuid NOT NULL,
  normalized_url_id uuid NOT NULL REFERENCES normalized_urls(id),
  citation_index integer NOT NULL CHECK (citation_index > 0),
  original_url text NOT NULL,
  title text,
  source_label text,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, answer_sample_id, normalized_url_id),
  UNIQUE (tenant_id, answer_sample_id, citation_index),
  FOREIGN KEY (tenant_id, answer_sample_id) REFERENCES answer_samples (tenant_id, id)
);

CREATE TABLE question_tags (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  brand_project_id uuid NOT NULL,
  tag_type text NOT NULL CHECK (tag_type IN ('intent', 'journey_stage', 'region', 'language', 'custom')),
  name text NOT NULL CHECK (length(btrim(name)) > 0),
  status text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive', 'archived')),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, brand_project_id, tag_type, name),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, brand_project_id, id),
  FOREIGN KEY (tenant_id, brand_project_id) REFERENCES brand_projects (tenant_id, id)
);

ALTER TABLE questions
  ADD CONSTRAINT questions_tenant_project_id_unique UNIQUE (tenant_id, brand_project_id, id);

ALTER TABLE topic_groups
  ADD CONSTRAINT topic_groups_tenant_project_id_unique UNIQUE (tenant_id, brand_project_id, id);

ALTER TABLE question_versions
  ADD COLUMN brand_project_id uuid,
  ADD COLUMN topic_group_id uuid;

ALTER TABLE questions NO FORCE ROW LEVEL SECURITY;
ALTER TABLE question_versions NO FORCE ROW LEVEL SECURITY;

UPDATE question_versions AS version
SET
  brand_project_id = question.brand_project_id,
  topic_group_id = question.topic_group_id
FROM questions AS question
WHERE question.tenant_id = version.tenant_id
  AND question.id = version.question_id;

ALTER TABLE questions FORCE ROW LEVEL SECURITY;
ALTER TABLE question_versions FORCE ROW LEVEL SECURITY;

ALTER TABLE question_versions
  ALTER COLUMN brand_project_id SET NOT NULL,
  ALTER COLUMN topic_group_id SET NOT NULL,
  ADD CONSTRAINT question_versions_tenant_question_id_unique
    UNIQUE (tenant_id, question_id, id),
  ADD CONSTRAINT question_versions_tenant_project_id_unique
    UNIQUE (tenant_id, brand_project_id, id),
  ADD CONSTRAINT question_versions_dimension_snapshot_unique
    UNIQUE (tenant_id, brand_project_id, question_id, topic_group_id, id),
  ADD CONSTRAINT question_versions_project_question_fk
    FOREIGN KEY (tenant_id, brand_project_id, question_id)
    REFERENCES questions (tenant_id, brand_project_id, id),
  ADD CONSTRAINT question_versions_project_topic_group_fk
    FOREIGN KEY (tenant_id, brand_project_id, topic_group_id)
    REFERENCES topic_groups (tenant_id, brand_project_id, id);

ALTER TABLE matrix_cells
  ADD CONSTRAINT matrix_cells_project_question_version_fk
    FOREIGN KEY (tenant_id, brand_project_id, question_version_id)
    REFERENCES question_versions (tenant_id, brand_project_id, id);

ALTER TABLE collection_tasks
  ADD CONSTRAINT collection_tasks_project_question_version_fk
    FOREIGN KEY (tenant_id, brand_project_id, question_version_id)
    REFERENCES question_versions (tenant_id, brand_project_id, id);

CREATE TABLE question_tag_assignments (
  tenant_id uuid NOT NULL,
  brand_project_id uuid NOT NULL,
  question_version_id uuid NOT NULL,
  question_tag_id uuid NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, question_version_id, question_tag_id),
  FOREIGN KEY (tenant_id, brand_project_id, question_version_id)
    REFERENCES question_versions (tenant_id, brand_project_id, id),
  FOREIGN KEY (tenant_id, brand_project_id, question_tag_id)
    REFERENCES question_tags (tenant_id, brand_project_id, id)
);

CREATE TABLE metric_definitions (
  code text PRIMARY KEY,
  display_name text NOT NULL,
  value_kind text NOT NULL CHECK (value_kind IN ('count', 'rate', 'average')),
  numerator_name text NOT NULL,
  denominator_name text,
  description text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE metric_versions (
  id uuid PRIMARY KEY,
  metric_code text NOT NULL REFERENCES metric_definitions(code),
  version integer NOT NULL CHECK (version > 0),
  formula_definition jsonb NOT NULL CHECK (jsonb_typeof(formula_definition) = 'object'),
  formula_hash text NOT NULL,
  active boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (metric_code, version),
  UNIQUE (metric_code, formula_hash)
);

CREATE UNIQUE INDEX metric_versions_one_active_idx
  ON metric_versions (metric_code) WHERE active = true;

INSERT INTO metric_definitions (
  code, display_name, value_kind, numerator_name, denominator_name, description
) VALUES
  ('valid_sample_count', 'Valid sample count', 'count', 'valid_answer_count', 'planned_task_count', 'Committed valid answers; planned tasks remain available as completeness context'),
  ('mention_rate', 'Mention rate', 'rate', 'mentioned_answer_count', 'valid_answer_count', 'Valid answers mentioning the brand divided by all valid answers'),
  ('average_first_rank', 'Average first rank', 'average', 'rank_sum', 'rank_count', 'Sum of first mention ranks divided by ranked mentioned answers'),
  ('first_place_rate', 'First-place rate', 'rate', 'first_rank_count', 'valid_answer_count', 'Valid answers with first mention rank one divided by all valid answers'),
  ('top3_rate', 'Top-3 rate', 'rate', 'top3_count', 'valid_answer_count', 'Valid answers with first mention rank at most three divided by all valid answers'),
  ('share_of_voice', 'Share of voice', 'rate', 'total_mention_count', 'all_monitored_brand_mention_count', 'Brand mentions divided by mentions across all monitored brands'),
  ('citation_rate', 'Citation rate', 'rate', 'cited_answer_count', 'valid_answer_count', 'Valid answers citing a configured brand domain divided by all valid answers'),
  ('sentiment_positive_rate', 'Positive sentiment rate', 'rate', 'positive_count', 'mentioned_answer_count', 'Positive mentioned answers divided by all mentioned answers'),
  ('sentiment_neutral_rate', 'Neutral sentiment rate', 'rate', 'neutral_count', 'mentioned_answer_count', 'Neutral mentioned answers divided by all mentioned answers'),
  ('sentiment_negative_rate', 'Negative sentiment rate', 'rate', 'negative_count', 'mentioned_answer_count', 'Negative mentioned answers divided by all mentioned answers');

INSERT INTO metric_versions (
  id, metric_code, version, formula_definition, formula_hash, active
) VALUES
  ('70000000-0000-4000-8000-000000000001', 'valid_sample_count', 1, '{"expression":"valid_answer_count","zeroDenominator":"not_applicable"}'::jsonb, 'valid-sample-count-v1', true),
  ('70000000-0000-4000-8000-000000000002', 'mention_rate', 1, '{"expression":"mentioned_answer_count / valid_answer_count","zeroDenominator":"null"}'::jsonb, 'mention-rate-v1', true),
  ('70000000-0000-4000-8000-000000000003', 'average_first_rank', 1, '{"expression":"rank_sum / rank_count","zeroDenominator":"null"}'::jsonb, 'average-first-rank-v1', true),
  ('70000000-0000-4000-8000-000000000004', 'first_place_rate', 1, '{"expression":"first_rank_count / valid_answer_count","zeroDenominator":"null"}'::jsonb, 'first-place-rate-v1', true),
  ('70000000-0000-4000-8000-000000000005', 'top3_rate', 1, '{"expression":"top3_count / valid_answer_count","zeroDenominator":"null"}'::jsonb, 'top3-rate-v1', true),
  ('70000000-0000-4000-8000-000000000006', 'share_of_voice', 1, '{"expression":"total_mention_count / all_monitored_brand_mention_count","zeroDenominator":"null"}'::jsonb, 'share-of-voice-v1', true),
  ('70000000-0000-4000-8000-000000000007', 'citation_rate', 1, '{"expression":"cited_answer_count / valid_answer_count","zeroDenominator":"null"}'::jsonb, 'citation-rate-v1', true),
  ('70000000-0000-4000-8000-000000000008', 'sentiment_positive_rate', 1, '{"expression":"positive_count / mentioned_answer_count","zeroDenominator":"null"}'::jsonb, 'sentiment-positive-rate-v1', true),
  ('70000000-0000-4000-8000-000000000009', 'sentiment_neutral_rate', 1, '{"expression":"neutral_count / mentioned_answer_count","zeroDenominator":"null"}'::jsonb, 'sentiment-neutral-rate-v1', true),
  ('70000000-0000-4000-8000-000000000010', 'sentiment_negative_rate', 1, '{"expression":"negative_count / mentioned_answer_count","zeroDenominator":"null"}'::jsonb, 'sentiment-negative-rate-v1', true);

CREATE TABLE daily_brand_metric_rollups (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  brand_project_id uuid NOT NULL,
  scheduled_date date NOT NULL,
  brand_id uuid NOT NULL,
  brand_rule_version_id uuid NOT NULL,
  question_id uuid NOT NULL,
  question_version_id uuid NOT NULL,
  topic_group_id uuid NOT NULL,
  platform_entry_code text NOT NULL REFERENCES platform_entries(code),
  platform_code text NOT NULL REFERENCES platforms(code),
  region text NOT NULL CHECK (region IN ('cn', 'overseas')),
  language text NOT NULL,
  channel text NOT NULL CHECK (channel IN ('consumer_web')),
  parser_version_id uuid NOT NULL REFERENCES parser_versions(id),
  planned_task_count integer NOT NULL CHECK (planned_task_count >= 0),
  completed_task_count integer NOT NULL CHECK (completed_task_count >= 0),
  failed_task_count integer NOT NULL CHECK (failed_task_count >= 0),
  missing_task_count integer NOT NULL CHECK (missing_task_count >= 0),
  valid_answer_count integer NOT NULL CHECK (valid_answer_count >= 0),
  mentioned_answer_count integer NOT NULL CHECK (mentioned_answer_count >= 0),
  total_mention_count integer NOT NULL CHECK (total_mention_count >= 0),
  all_monitored_brand_mention_count integer NOT NULL CHECK (all_monitored_brand_mention_count >= 0),
  rank_sum integer NOT NULL CHECK (rank_sum >= 0),
  rank_count integer NOT NULL CHECK (rank_count >= 0),
  first_rank_count integer NOT NULL CHECK (first_rank_count >= 0),
  top3_count integer NOT NULL CHECK (top3_count >= 0),
  cited_answer_count integer NOT NULL CHECK (cited_answer_count >= 0),
  positive_count integer NOT NULL CHECK (positive_count >= 0),
  neutral_count integer NOT NULL CHECK (neutral_count >= 0),
  negative_count integer NOT NULL CHECK (negative_count >= 0),
  input_hash text NOT NULL CHECK (length(btrim(input_hash)) > 0),
  calculated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (
    tenant_id, brand_project_id, scheduled_date, brand_rule_version_id,
    question_version_id, platform_entry_code, region, language, channel,
    parser_version_id, input_hash
  ),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, brand_id, id),
  FOREIGN KEY (tenant_id, brand_project_id) REFERENCES brand_projects (tenant_id, id),
  FOREIGN KEY (tenant_id, brand_project_id, brand_id)
    REFERENCES brands (tenant_id, brand_project_id, id),
  FOREIGN KEY (tenant_id, brand_id, brand_rule_version_id)
    REFERENCES brand_rule_versions (tenant_id, brand_id, id),
  CONSTRAINT daily_brand_metric_rollups_question_dimension_fk FOREIGN KEY (
    tenant_id, brand_project_id, question_id, topic_group_id, question_version_id
  ) REFERENCES question_versions (
    tenant_id, brand_project_id, question_id, topic_group_id, id
  ),
  FOREIGN KEY (platform_code, platform_entry_code)
    REFERENCES platform_entries (platform_code, code),
  CHECK (planned_task_count = completed_task_count + failed_task_count + missing_task_count),
  CHECK (valid_answer_count <= completed_task_count),
  CHECK (mentioned_answer_count <= valid_answer_count),
  CHECK (rank_count = mentioned_answer_count),
  CHECK (first_rank_count <= rank_count),
  CHECK (top3_count <= rank_count),
  CHECK (cited_answer_count <= valid_answer_count),
  CHECK (total_mention_count <= all_monitored_brand_mention_count),
  CHECK (positive_count + neutral_count + negative_count = mentioned_answer_count)
);

CREATE TABLE brand_metric_observations (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  rollup_id uuid NOT NULL,
  brand_id uuid NOT NULL,
  metric_version_id uuid NOT NULL REFERENCES metric_versions(id),
  numerator numeric(24,8) NOT NULL,
  denominator numeric(24,8),
  value numeric(24,12),
  participating_sample_count integer NOT NULL CHECK (participating_sample_count >= 0),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, rollup_id, metric_version_id),
  FOREIGN KEY (tenant_id, brand_id, rollup_id)
    REFERENCES daily_brand_metric_rollups (tenant_id, brand_id, id)
);

CREATE TABLE daily_brand_metric_memberships (
  tenant_id uuid NOT NULL,
  rollup_id uuid NOT NULL,
  brand_id uuid NOT NULL,
  task_id uuid NOT NULL,
  answer_sample_id uuid,
  brand_fact_id uuid,
  inclusion_status text NOT NULL CHECK (
    inclusion_status IN ('valid_mentioned', 'valid_unmentioned', 'invalid', 'failed', 'missing')
  ),
  contribution jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, rollup_id, task_id),
  FOREIGN KEY (tenant_id, brand_id, rollup_id)
    REFERENCES daily_brand_metric_rollups (tenant_id, brand_id, id),
  FOREIGN KEY (tenant_id, task_id) REFERENCES collection_tasks (tenant_id, id),
  CONSTRAINT daily_brand_metric_memberships_tenant_task_sample_fk
    FOREIGN KEY (tenant_id, task_id, answer_sample_id)
    REFERENCES answer_samples (tenant_id, task_id, id),
  FOREIGN KEY (tenant_id, answer_sample_id, brand_fact_id)
    REFERENCES answer_brand_facts (tenant_id, answer_sample_id, id),
  FOREIGN KEY (tenant_id, brand_id, brand_fact_id)
    REFERENCES answer_brand_facts (tenant_id, brand_id, id),
  CONSTRAINT daily_brand_metric_memberships_contribution_object_check
    CHECK (jsonb_typeof(contribution) = 'object'),
  CHECK (
    (inclusion_status = 'valid_mentioned' AND answer_sample_id IS NOT NULL AND brand_fact_id IS NOT NULL)
    OR (inclusion_status = 'valid_unmentioned' AND answer_sample_id IS NOT NULL AND brand_fact_id IS NOT NULL)
    OR (inclusion_status = 'invalid' AND answer_sample_id IS NOT NULL AND brand_fact_id IS NULL)
    OR (inclusion_status IN ('failed', 'missing') AND answer_sample_id IS NULL AND brand_fact_id IS NULL)
  )
);

CREATE OR REPLACE FUNCTION enforce_brand_metric_membership_consistency() RETURNS trigger AS $$
DECLARE
  rollup daily_brand_metric_rollups%ROWTYPE;
  task collection_tasks%ROWTYPE;
  sample answer_samples%ROWTYPE;
  fact answer_brand_facts%ROWTYPE;
  parse answer_parses%ROWTYPE;
BEGIN
  SELECT * INTO rollup
  FROM daily_brand_metric_rollups
  WHERE tenant_id = NEW.tenant_id
    AND id = NEW.rollup_id;

  SELECT * INTO task
  FROM collection_tasks
  WHERE tenant_id = NEW.tenant_id
    AND id = NEW.task_id;

  IF rollup.brand_id IS DISTINCT FROM NEW.brand_id
    OR task.brand_project_id IS DISTINCT FROM rollup.brand_project_id
    OR task.scheduled_date IS DISTINCT FROM rollup.scheduled_date
    OR task.question_version_id IS DISTINCT FROM rollup.question_version_id
    OR task.platform_entry_code IS DISTINCT FROM rollup.platform_entry_code
    OR task.platform_code IS DISTINCT FROM rollup.platform_code
    OR task.region IS DISTINCT FROM rollup.region
    OR task.language IS DISTINCT FROM rollup.language
    OR task.channel IS DISTINCT FROM rollup.channel
  THEN
    RAISE EXCEPTION 'Metric membership task dimensions do not match the rollup'
      USING ERRCODE = '23514';
  END IF;

  IF NEW.inclusion_status IN ('valid_mentioned', 'valid_unmentioned', 'invalid')
    AND task.status IS DISTINCT FROM 'completed'
  THEN
    RAISE EXCEPTION 'Completed metric memberships require a completed task'
      USING ERRCODE = '23514';
  END IF;

  IF NEW.inclusion_status = 'failed' AND task.status IS DISTINCT FROM 'failed' THEN
    RAISE EXCEPTION 'Failed metric memberships require a failed task'
      USING ERRCODE = '23514';
  END IF;

  IF NEW.inclusion_status = 'missing' AND task.status IN ('completed', 'failed') THEN
    RAISE EXCEPTION 'Missing metric memberships require an unfinished task'
      USING ERRCODE = '23514';
  END IF;

  IF NEW.answer_sample_id IS NOT NULL THEN
    SELECT * INTO sample
    FROM answer_samples
    WHERE tenant_id = NEW.tenant_id
      AND id = NEW.answer_sample_id;

    IF sample.task_id IS DISTINCT FROM NEW.task_id THEN
      RAISE EXCEPTION 'Metric membership sample does not belong to its task'
        USING ERRCODE = '23514';
    END IF;
  END IF;

  IF NEW.brand_fact_id IS NOT NULL THEN
    SELECT * INTO fact
    FROM answer_brand_facts
    WHERE tenant_id = NEW.tenant_id
      AND id = NEW.brand_fact_id;

    SELECT * INTO parse
    FROM answer_parses
    WHERE tenant_id = NEW.tenant_id
      AND id = fact.answer_parse_id;

    IF fact.answer_sample_id IS DISTINCT FROM NEW.answer_sample_id
      OR fact.brand_id IS DISTINCT FROM NEW.brand_id
      OR fact.brand_rule_version_id IS DISTINCT FROM rollup.brand_rule_version_id
      OR parse.parser_version_id IS DISTINCT FROM rollup.parser_version_id
      OR parse.status IS DISTINCT FROM 'completed'
      OR (NEW.inclusion_status = 'valid_mentioned' AND fact.mentioned IS DISTINCT FROM true)
      OR (NEW.inclusion_status = 'valid_unmentioned' AND fact.mentioned IS DISTINCT FROM false)
    THEN
      RAISE EXCEPTION 'Metric membership fact does not match the rollup lineage'
        USING ERRCODE = '23514';
    END IF;
  END IF;

  IF NEW.inclusion_status IN ('valid_mentioned', 'valid_unmentioned')
    AND sample.valid IS DISTINCT FROM true
  THEN
    RAISE EXCEPTION 'Valid metric memberships require a valid answer sample'
      USING ERRCODE = '23514';
  END IF;

  IF NEW.inclusion_status = 'invalid' AND sample.valid IS DISTINCT FROM false THEN
    RAISE EXCEPTION 'Invalid metric memberships require an invalid answer sample'
      USING ERRCODE = '23514';
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER daily_brand_metric_membership_consistency_guard
  BEFORE INSERT ON daily_brand_metric_memberships
  FOR EACH ROW EXECUTE FUNCTION enforce_brand_metric_membership_consistency();

CREATE INDEX answer_brand_facts_analysis_idx
  ON answer_brand_facts (tenant_id, brand_id, mentioned, first_mention_rank, sentiment);

CREATE INDEX answer_citations_domain_idx
  ON answer_citations (tenant_id, normalized_url_id, answer_sample_id);

CREATE INDEX citation_articles_domain_idx
  ON citation_articles (citation_domain_id, normalized_url_id);

CREATE INDEX daily_brand_metric_rollups_filter_idx
  ON daily_brand_metric_rollups (
    tenant_id, brand_project_id, scheduled_date, platform_code, brand_id, question_version_id
  );

CREATE OR REPLACE FUNCTION protect_parser_version_definition() RETURNS trigger AS $$
BEGIN
  IF OLD.code IS DISTINCT FROM NEW.code
    OR OLD.version IS DISTINCT FROM NEW.version
    OR OLD.config_hash IS DISTINCT FROM NEW.config_hash
  THEN
    RAISE EXCEPTION 'Parser version definitions are immutable; create a new version'
      USING ERRCODE = '55000';
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER parser_versions_definition_immutable
  BEFORE UPDATE ON parser_versions
  FOR EACH ROW EXECUTE FUNCTION protect_parser_version_definition();

CREATE OR REPLACE FUNCTION protect_metric_version_definition() RETURNS trigger AS $$
BEGIN
  IF OLD.metric_code IS DISTINCT FROM NEW.metric_code
    OR OLD.version IS DISTINCT FROM NEW.version
    OR OLD.formula_definition IS DISTINCT FROM NEW.formula_definition
    OR OLD.formula_hash IS DISTINCT FROM NEW.formula_hash
  THEN
    RAISE EXCEPTION 'Metric version definitions are immutable; create a new version'
      USING ERRCODE = '55000';
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION protect_question_version_snapshot() RETURNS trigger AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'Question version snapshots are immutable; archive the question instead'
      USING ERRCODE = '55000';
  END IF;

  IF OLD.tenant_id IS DISTINCT FROM NEW.tenant_id
    OR OLD.question_id IS DISTINCT FROM NEW.question_id
    OR OLD.version IS DISTINCT FROM NEW.version
    OR OLD.question_text IS DISTINCT FROM NEW.question_text
    OR OLD.language IS DISTINCT FROM NEW.language
    OR OLD.brand_project_id IS DISTINCT FROM NEW.brand_project_id
    OR OLD.topic_group_id IS DISTINCT FROM NEW.topic_group_id
  THEN
    RAISE EXCEPTION 'Question version snapshots are immutable; create a new version'
      USING ERRCODE = '55000';
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER metric_versions_definition_immutable
  BEFORE UPDATE ON metric_versions
  FOR EACH ROW EXECUTE FUNCTION protect_metric_version_definition();
CREATE TRIGGER question_versions_snapshot_immutable
  BEFORE UPDATE OR DELETE ON question_versions
  FOR EACH ROW EXECUTE FUNCTION protect_question_version_snapshot();

CREATE OR REPLACE FUNCTION prevent_immutable_analytics_mutation() RETURNS trigger AS $$
BEGIN
  RAISE EXCEPTION '% rows are immutable; create a new version or lineage record', TG_TABLE_NAME
    USING ERRCODE = '55000';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER brand_rule_versions_immutable
  BEFORE UPDATE OR DELETE ON brand_rule_versions
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER parser_versions_delete_immutable
  BEFORE DELETE ON parser_versions
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER metric_versions_delete_immutable
  BEFORE DELETE ON metric_versions
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER brand_match_rules_immutable
  BEFORE UPDATE OR DELETE ON brand_match_rules
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER brand_domains_immutable
  BEFORE UPDATE OR DELETE ON brand_domains
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER answer_parses_immutable
  BEFORE UPDATE OR DELETE ON answer_parses
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER answer_brand_facts_immutable
  BEFORE UPDATE OR DELETE ON answer_brand_facts
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER brand_mention_occurrences_immutable
  BEFORE UPDATE OR DELETE ON brand_mention_occurrences
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER brand_fact_corrections_immutable
  BEFORE UPDATE OR DELETE ON brand_fact_corrections
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER brand_fact_risks_immutable
  BEFORE UPDATE OR DELETE ON brand_fact_risks
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER brand_fact_risk_corrections_immutable
  BEFORE UPDATE OR DELETE ON brand_fact_risk_corrections
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER daily_brand_metric_rollups_immutable
  BEFORE UPDATE OR DELETE ON daily_brand_metric_rollups
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER brand_metric_observations_immutable
  BEFORE UPDATE OR DELETE ON brand_metric_observations
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER daily_brand_metric_memberships_immutable
  BEFORE UPDATE OR DELETE ON daily_brand_metric_memberships
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
CREATE TRIGGER question_tag_assignments_immutable
  BEFORE UPDATE OR DELETE ON question_tag_assignments
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();

DO $$
DECLARE
  table_name text;
BEGIN
  FOREACH table_name IN ARRAY ARRAY[
    'brand_rule_versions', 'brand_match_rules', 'brand_domains', 'answer_parses',
    'answer_brand_facts', 'brand_mention_occurrences', 'brand_fact_corrections',
    'brand_fact_risks', 'brand_fact_risk_corrections', 'answer_citations',
    'question_tags', 'question_tag_assignments', 'daily_brand_metric_rollups',
    'brand_metric_observations', 'daily_brand_metric_memberships'
  ]
  LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', table_name);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', table_name);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %I USING (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid) WITH CHECK (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid)',
      table_name
    );
  END LOOP;
END $$;
