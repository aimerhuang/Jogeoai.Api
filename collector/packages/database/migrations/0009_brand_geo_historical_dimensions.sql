ALTER TABLE question_versions
  ADD COLUMN topic_group_name text,
  ADD COLUMN topic_group_sort_order integer,
  ADD COLUMN topic_group_status text;

ALTER TABLE topic_groups NO FORCE ROW LEVEL SECURITY;
ALTER TABLE question_versions NO FORCE ROW LEVEL SECURITY;

UPDATE question_versions AS version
SET
  topic_group_name = topic.name,
  topic_group_sort_order = topic.sort_order,
  topic_group_status = topic.status
FROM topic_groups AS topic
WHERE topic.tenant_id = version.tenant_id
  AND topic.brand_project_id = version.brand_project_id
  AND topic.id = version.topic_group_id;

ALTER TABLE topic_groups FORCE ROW LEVEL SECURITY;
ALTER TABLE question_versions FORCE ROW LEVEL SECURITY;

ALTER TABLE question_versions
  ALTER COLUMN topic_group_name SET NOT NULL,
  ALTER COLUMN topic_group_sort_order SET NOT NULL,
  ALTER COLUMN topic_group_status SET NOT NULL,
  ADD CONSTRAINT question_versions_topic_group_name_check
    CHECK (length(btrim(topic_group_name)) > 0),
  ADD CONSTRAINT question_versions_topic_group_status_check
    CHECK (topic_group_status IN ('active', 'inactive', 'archived'));

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
    OR OLD.topic_group_name IS DISTINCT FROM NEW.topic_group_name
    OR OLD.topic_group_sort_order IS DISTINCT FROM NEW.topic_group_sort_order
    OR OLD.topic_group_status IS DISTINCT FROM NEW.topic_group_status
  THEN
    RAISE EXCEPTION 'Question version snapshots are immutable; create a new version'
      USING ERRCODE = '55000';
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TABLE platform_entry_versions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  platform_entry_code text NOT NULL,
  platform_code text NOT NULL,
  version integer NOT NULL CHECK (version > 0),
  display_name text NOT NULL CHECK (length(btrim(display_name)) > 0),
  region text NOT NULL CHECK (region IN ('cn', 'overseas')),
  channel text NOT NULL CHECK (channel IN ('consumer_web')),
  config jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(config) = 'object'),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (platform_entry_code, version),
  UNIQUE (id, platform_entry_code, platform_code, version, region, channel),
  UNIQUE (platform_entry_code, platform_code, version, display_name, region, channel),
  FOREIGN KEY (platform_code, platform_entry_code)
    REFERENCES platform_entries (platform_code, code)
);

INSERT INTO platform_entry_versions (
  platform_entry_code, platform_code, version, display_name, region, channel
)
SELECT code, platform_code, version, display_name, region, channel
FROM platform_entries
ON CONFLICT (platform_entry_code, version) DO NOTHING;

ALTER TABLE platform_entries
  ADD CONSTRAINT platform_entries_current_version_fk
  FOREIGN KEY (code, platform_code, version, display_name, region, channel)
  REFERENCES platform_entry_versions (
    platform_entry_code, platform_code, version, display_name, region, channel
  ) DEFERRABLE INITIALLY DEFERRED;

ALTER TABLE collection_tasks
  ADD COLUMN platform_entry_version_id uuid,
  ADD COLUMN platform_entry_version integer;

ALTER TABLE collection_tasks NO FORCE ROW LEVEL SECURITY;

UPDATE collection_tasks AS task
SET
  platform_entry_version_id = version.id,
  platform_entry_version = version.version
FROM platform_entry_versions AS version
WHERE version.platform_entry_code = task.platform_entry_code
  AND version.platform_code = task.platform_code
  AND version.region = task.region
  AND version.channel = task.channel;

ALTER TABLE collection_tasks FORCE ROW LEVEL SECURITY;

ALTER TABLE collection_tasks
  ALTER COLUMN platform_entry_version_id SET NOT NULL,
  ALTER COLUMN platform_entry_version SET NOT NULL,
  ADD CONSTRAINT collection_tasks_platform_entry_version_fk
    FOREIGN KEY (
      platform_entry_version_id, platform_entry_code, platform_code,
      platform_entry_version, region, channel
    ) REFERENCES platform_entry_versions (
      id, platform_entry_code, platform_code, version, region, channel
    );

ALTER TABLE daily_brand_metric_rollups
  ADD COLUMN platform_entry_version_id uuid,
  ADD COLUMN platform_entry_version integer;

ALTER TABLE daily_brand_metric_rollups NO FORCE ROW LEVEL SECURITY;

UPDATE daily_brand_metric_rollups AS rollup
SET
  platform_entry_version_id = version.id,
  platform_entry_version = version.version
FROM platform_entry_versions AS version
WHERE version.platform_entry_code = rollup.platform_entry_code
  AND version.platform_code = rollup.platform_code
  AND version.region = rollup.region
  AND version.channel = rollup.channel;

ALTER TABLE daily_brand_metric_rollups FORCE ROW LEVEL SECURITY;

ALTER TABLE daily_brand_metric_rollups
  ALTER COLUMN platform_entry_version_id SET NOT NULL,
  ALTER COLUMN platform_entry_version SET NOT NULL,
  DROP CONSTRAINT daily_brand_metric_rollups_tenant_id_brand_project_id_sched_key,
  ADD CONSTRAINT daily_brand_metric_rollups_platform_entry_version_fk
    FOREIGN KEY (
      platform_entry_version_id, platform_entry_code, platform_code,
      platform_entry_version, region, channel
    ) REFERENCES platform_entry_versions (
      id, platform_entry_code, platform_code, version, region, channel
    ),
  ADD CONSTRAINT daily_brand_metric_rollups_identity_unique UNIQUE (
    tenant_id, brand_project_id, scheduled_date, brand_rule_version_id,
    question_version_id, platform_entry_version_id, region, language, channel,
    parser_version_id, input_hash
  );

CREATE TRIGGER platform_entry_versions_immutable
  BEFORE UPDATE OR DELETE ON platform_entry_versions
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();

ALTER TABLE answer_citations NO FORCE ROW LEVEL SECURITY;
ALTER TABLE answer_citations
  DROP CONSTRAINT answer_citations_normalized_url_id_fkey;

CREATE TEMP TABLE citation_domain_tenant_map ON COMMIT DROP AS
WITH required_domains AS (
  SELECT DISTINCT
    citation.tenant_id,
    normalized.citation_domain_id AS original_domain_id
  FROM answer_citations AS citation
  JOIN normalized_urls AS normalized ON normalized.id = citation.normalized_url_id
), ranked_domains AS (
  SELECT
    tenant_id,
    original_domain_id,
    row_number() OVER (PARTITION BY original_domain_id ORDER BY tenant_id) AS tenant_rank
  FROM required_domains
)
SELECT
  tenant_id,
  original_domain_id,
  CASE WHEN tenant_rank = 1 THEN original_domain_id ELSE gen_random_uuid() END AS scoped_domain_id
FROM ranked_domains;

CREATE TEMP TABLE citation_url_tenant_map ON COMMIT DROP AS
WITH required_urls AS (
  SELECT DISTINCT tenant_id, normalized_url_id AS original_url_id
  FROM answer_citations
), ranked_urls AS (
  SELECT
    tenant_id,
    original_url_id,
    row_number() OVER (PARTITION BY original_url_id ORDER BY tenant_id) AS tenant_rank
  FROM required_urls
)
SELECT
  tenant_id,
  original_url_id,
  CASE WHEN tenant_rank = 1 THEN original_url_id ELSE gen_random_uuid() END AS scoped_url_id
FROM ranked_urls;

-- Legacy rows without answer-citation references have no tenant provenance and are derived caches,
-- so only evidence-reachable dimensions are copied into the tenant-scoped tables.

CREATE TABLE citation_domains_scoped (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  canonical_host text NOT NULL CHECK (canonical_host = lower(canonical_host)),
  registrable_domain text NOT NULL CHECK (registrable_domain = lower(registrable_domain)),
  source_category text NOT NULL DEFAULT 'unknown' CHECK (
    source_category IN ('unknown', 'owned', 'news', 'social', 'ecommerce', 'reference', 'other')
  ),
  created_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO citation_domains_scoped (
  id, tenant_id, canonical_host, registrable_domain, source_category, created_at
)
SELECT
  mapping.scoped_domain_id,
  mapping.tenant_id,
  domain.canonical_host,
  domain.registrable_domain,
  domain.source_category,
  domain.created_at
FROM citation_domain_tenant_map AS mapping
JOIN citation_domains AS domain ON domain.id = mapping.original_domain_id;

CREATE TABLE normalized_urls_scoped (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  citation_domain_id uuid NOT NULL,
  url_hash text NOT NULL,
  canonical_url text NOT NULL,
  scheme text NOT NULL CHECK (scheme IN ('http', 'https')),
  host text NOT NULL CHECK (host = lower(host)),
  registrable_domain text NOT NULL CHECK (registrable_domain = lower(registrable_domain)),
  path text NOT NULL DEFAULT '/',
  created_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO normalized_urls_scoped (
  id, tenant_id, citation_domain_id, url_hash, canonical_url,
  scheme, host, registrable_domain, path, created_at
)
SELECT
  url_mapping.scoped_url_id,
  url_mapping.tenant_id,
  domain_mapping.scoped_domain_id,
  normalized.url_hash,
  normalized.canonical_url,
  normalized.scheme,
  normalized.host,
  normalized.registrable_domain,
  normalized.path,
  normalized.created_at
FROM citation_url_tenant_map AS url_mapping
JOIN normalized_urls AS normalized ON normalized.id = url_mapping.original_url_id
JOIN citation_domain_tenant_map AS domain_mapping
  ON domain_mapping.tenant_id = url_mapping.tenant_id
 AND domain_mapping.original_domain_id = normalized.citation_domain_id;

CREATE TABLE citation_articles_scoped (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  normalized_url_id uuid NOT NULL,
  citation_domain_id uuid NOT NULL,
  title text,
  source_type text NOT NULL DEFAULT 'web_page' CHECK (
    source_type IN ('web_page', 'article', 'product_page', 'social_post', 'other')
  ),
  published_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO citation_articles_scoped (
  id, tenant_id, normalized_url_id, citation_domain_id,
  title, source_type, published_at, created_at
)
SELECT
  CASE
    WHEN url_mapping.scoped_url_id = url_mapping.original_url_id
      THEN article.id
    ELSE gen_random_uuid()
  END,
  url_mapping.tenant_id,
  url_mapping.scoped_url_id,
  domain_mapping.scoped_domain_id,
  article.title,
  article.source_type,
  article.published_at,
  article.created_at
FROM citation_articles AS article
JOIN citation_url_tenant_map AS url_mapping
  ON url_mapping.original_url_id = article.normalized_url_id
JOIN normalized_urls AS normalized ON normalized.id = article.normalized_url_id
JOIN citation_domain_tenant_map AS domain_mapping
  ON domain_mapping.tenant_id = url_mapping.tenant_id
 AND domain_mapping.original_domain_id = normalized.citation_domain_id;

UPDATE answer_citations AS citation
SET normalized_url_id = mapping.scoped_url_id
FROM citation_url_tenant_map AS mapping
WHERE mapping.tenant_id = citation.tenant_id
  AND mapping.original_url_id = citation.normalized_url_id;

DROP TABLE citation_articles;
DROP TABLE normalized_urls;
DROP TABLE citation_domains;

ALTER TABLE citation_domains_scoped RENAME TO citation_domains;
ALTER TABLE normalized_urls_scoped RENAME TO normalized_urls;
ALTER TABLE citation_articles_scoped RENAME TO citation_articles;

ALTER INDEX citation_domains_scoped_pkey RENAME TO citation_domains_pkey;
ALTER INDEX normalized_urls_scoped_pkey RENAME TO normalized_urls_pkey;
ALTER INDEX citation_articles_scoped_pkey RENAME TO citation_articles_pkey;

ALTER TABLE tenants NO FORCE ROW LEVEL SECURITY;

ALTER TABLE citation_domains
  ADD CONSTRAINT citation_domains_tenant_fk FOREIGN KEY (tenant_id) REFERENCES tenants(id),
  ADD CONSTRAINT citation_domains_tenant_id_id_unique UNIQUE (tenant_id, id),
  ADD CONSTRAINT citation_domains_tenant_host_unique UNIQUE (tenant_id, canonical_host),
  ADD CONSTRAINT citation_domains_tenant_identity_unique
    UNIQUE (tenant_id, id, canonical_host, registrable_domain);

ALTER TABLE normalized_urls
  ADD CONSTRAINT normalized_urls_tenant_fk FOREIGN KEY (tenant_id) REFERENCES tenants(id),
  ADD CONSTRAINT normalized_urls_tenant_id_id_unique UNIQUE (tenant_id, id),
  ADD CONSTRAINT normalized_urls_tenant_hash_unique UNIQUE (tenant_id, url_hash),
  ADD CONSTRAINT normalized_urls_tenant_domain_identity_unique
    UNIQUE (tenant_id, id, citation_domain_id),
  ADD CONSTRAINT normalized_urls_tenant_domain_dimensions_fk
    FOREIGN KEY (tenant_id, citation_domain_id, host, registrable_domain)
    REFERENCES citation_domains (tenant_id, id, canonical_host, registrable_domain);

ALTER TABLE citation_articles
  ADD CONSTRAINT citation_articles_tenant_fk FOREIGN KEY (tenant_id) REFERENCES tenants(id),
  ADD CONSTRAINT citation_articles_tenant_id_id_unique UNIQUE (tenant_id, id),
  ADD CONSTRAINT citation_articles_tenant_url_unique UNIQUE (tenant_id, normalized_url_id),
  ADD CONSTRAINT citation_articles_tenant_url_domain_fk
    FOREIGN KEY (tenant_id, normalized_url_id, citation_domain_id)
    REFERENCES normalized_urls (tenant_id, id, citation_domain_id);

CREATE INDEX citation_articles_domain_idx
  ON citation_articles (tenant_id, citation_domain_id, normalized_url_id);

ALTER TABLE answer_citations
  ADD CONSTRAINT answer_citations_tenant_normalized_url_fk
    FOREIGN KEY (tenant_id, normalized_url_id)
    REFERENCES normalized_urls (tenant_id, id);

ALTER TABLE answer_citations FORCE ROW LEVEL SECURITY;

ALTER TABLE tenants FORCE ROW LEVEL SECURITY;

DO $$
DECLARE
  table_name text;
BEGIN
  FOREACH table_name IN ARRAY ARRAY[
    'citation_domains', 'normalized_urls', 'citation_articles'
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

ALTER TABLE brand_fact_corrections
  ADD CONSTRAINT brand_fact_corrections_membership_identity_unique
    UNIQUE (tenant_id, brand_fact_id, id, field_name);

ALTER TABLE daily_brand_metric_memberships
  ADD CONSTRAINT daily_brand_metric_memberships_fact_identity_unique
    UNIQUE (tenant_id, rollup_id, task_id, brand_fact_id);

CREATE TABLE brand_metric_membership_corrections (
  tenant_id uuid NOT NULL,
  rollup_id uuid NOT NULL,
  task_id uuid NOT NULL,
  brand_fact_id uuid NOT NULL,
  correction_id uuid NOT NULL,
  field_name text NOT NULL CHECK (
    field_name IN ('mentioned', 'mention_count', 'first_mention_rank', 'sentiment')
  ),
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, rollup_id, task_id, correction_id),
  UNIQUE (tenant_id, rollup_id, task_id, field_name),
  FOREIGN KEY (tenant_id, rollup_id, task_id, brand_fact_id)
    REFERENCES daily_brand_metric_memberships (tenant_id, rollup_id, task_id, brand_fact_id),
  FOREIGN KEY (tenant_id, brand_fact_id, correction_id, field_name)
    REFERENCES brand_fact_corrections (tenant_id, brand_fact_id, id, field_name)
);

CREATE OR REPLACE FUNCTION enforce_brand_metric_correction_lineage() RETURNS trigger AS $$
DECLARE
  correction brand_fact_corrections%ROWTYPE;
  membership daily_brand_metric_memberships%ROWTYPE;
BEGIN
  SELECT * INTO correction
  FROM brand_fact_corrections
  WHERE tenant_id = NEW.tenant_id
    AND id = NEW.correction_id;

  SELECT * INTO membership
  FROM daily_brand_metric_memberships
  WHERE tenant_id = NEW.tenant_id
    AND rollup_id = NEW.rollup_id
    AND task_id = NEW.task_id;

  IF EXISTS (
    SELECT 1
    FROM brand_fact_corrections AS successor
    WHERE successor.tenant_id = NEW.tenant_id
      AND successor.supersedes_correction_id = NEW.correction_id
  ) THEN
    RAISE EXCEPTION 'Metric memberships must reference the latest correction for a fact field'
      USING ERRCODE = '23514';
  END IF;

  IF (NEW.field_name = 'mentioned'
      AND correction.corrected_value IS DISTINCT FROM
        to_jsonb(membership.inclusion_status = 'valid_mentioned'))
    OR (NEW.field_name = 'mention_count'
      AND correction.corrected_value IS DISTINCT FROM membership.contribution -> 'mentionCount')
    OR (NEW.field_name = 'first_mention_rank'
      AND correction.corrected_value IS DISTINCT FROM membership.contribution -> 'firstRank')
    OR (NEW.field_name = 'sentiment'
      AND correction.corrected_value IS DISTINCT FROM membership.contribution -> 'sentiment')
  THEN
    RAISE EXCEPTION 'Metric membership contribution does not match the applied correction'
      USING ERRCODE = '23514';
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER brand_metric_membership_corrections_lineage_guard
  BEFORE INSERT ON brand_metric_membership_corrections
  FOR EACH ROW EXECUTE FUNCTION enforce_brand_metric_correction_lineage();

CREATE TRIGGER brand_metric_membership_corrections_immutable
  BEFORE UPDATE OR DELETE ON brand_metric_membership_corrections
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();

ALTER TABLE brand_metric_membership_corrections ENABLE ROW LEVEL SECURITY;
ALTER TABLE brand_metric_membership_corrections FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON brand_metric_membership_corrections
  USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

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
    OR task.platform_entry_version_id IS DISTINCT FROM rollup.platform_entry_version_id
    OR task.platform_entry_version IS DISTINCT FROM rollup.platform_entry_version
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
