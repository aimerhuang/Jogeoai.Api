ALTER TABLE question_tag_assignments
  ADD COLUMN tag_type_snapshot text,
  ADD COLUMN tag_name_snapshot text,
  ADD COLUMN tag_status_snapshot text;

ALTER TABLE question_tag_assignments NO FORCE ROW LEVEL SECURITY;
ALTER TABLE question_tag_assignments DISABLE TRIGGER question_tag_assignments_immutable;

UPDATE question_tag_assignments AS assignment
SET
  tag_type_snapshot = tag.tag_type,
  tag_name_snapshot = tag.name,
  tag_status_snapshot = tag.status
FROM question_tags AS tag
WHERE tag.tenant_id = assignment.tenant_id
  AND tag.brand_project_id = assignment.brand_project_id
  AND tag.id = assignment.question_tag_id;

ALTER TABLE question_tag_assignments ENABLE TRIGGER question_tag_assignments_immutable;
ALTER TABLE question_tag_assignments FORCE ROW LEVEL SECURITY;

ALTER TABLE question_tag_assignments
  ALTER COLUMN tag_type_snapshot SET NOT NULL,
  ALTER COLUMN tag_name_snapshot SET NOT NULL,
  ALTER COLUMN tag_status_snapshot SET NOT NULL,
  ADD CONSTRAINT question_tag_assignments_type_snapshot_check CHECK (
    tag_type_snapshot IN ('intent', 'journey_stage', 'region', 'language', 'custom')
  ),
  ADD CONSTRAINT question_tag_assignments_name_snapshot_check CHECK (
    length(btrim(tag_name_snapshot)) > 0
  ),
  ADD CONSTRAINT question_tag_assignments_status_snapshot_check CHECK (
    tag_status_snapshot IN ('active', 'inactive', 'archived')
  );

CREATE OR REPLACE FUNCTION snapshot_question_tag_assignment() RETURNS trigger AS $$
DECLARE
  tag question_tags%ROWTYPE;
BEGIN
  SELECT * INTO tag
  FROM question_tags
  WHERE tenant_id = NEW.tenant_id
    AND brand_project_id = NEW.brand_project_id
    AND id = NEW.question_tag_id;

  IF tag.id IS NULL THEN
    RAISE EXCEPTION 'Question tag assignment requires a tag in the same tenant and project'
      USING ERRCODE = '23503';
  END IF;

  IF NEW.tag_type_snapshot IS NOT NULL
    AND NEW.tag_type_snapshot IS DISTINCT FROM tag.tag_type
  THEN
    RAISE EXCEPTION 'Question tag type snapshot does not match the assigned tag'
      USING ERRCODE = '23514';
  END IF;
  IF NEW.tag_name_snapshot IS NOT NULL
    AND NEW.tag_name_snapshot IS DISTINCT FROM tag.name
  THEN
    RAISE EXCEPTION 'Question tag name snapshot does not match the assigned tag'
      USING ERRCODE = '23514';
  END IF;
  IF NEW.tag_status_snapshot IS NOT NULL
    AND NEW.tag_status_snapshot IS DISTINCT FROM tag.status
  THEN
    RAISE EXCEPTION 'Question tag status snapshot does not match the assigned tag'
      USING ERRCODE = '23514';
  END IF;

  NEW.tag_type_snapshot := tag.tag_type;
  NEW.tag_name_snapshot := tag.name;
  NEW.tag_status_snapshot := tag.status;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER question_tag_assignments_snapshot_guard
  BEFORE INSERT ON question_tag_assignments
  FOR EACH ROW EXECUTE FUNCTION snapshot_question_tag_assignment();

ALTER TABLE answer_citations
  ADD CONSTRAINT answer_citations_snapshot_identity_unique
    UNIQUE (tenant_id, id, answer_sample_id, normalized_url_id);

CREATE TABLE answer_citation_dimension_snapshots (
  tenant_id uuid NOT NULL,
  answer_citation_id uuid NOT NULL,
  answer_sample_id uuid NOT NULL,
  normalized_url_id uuid NOT NULL,
  citation_domain_id uuid NOT NULL,
  canonical_host text NOT NULL,
  registrable_domain text NOT NULL,
  source_category text NOT NULL,
  url_hash text NOT NULL,
  canonical_url text NOT NULL,
  scheme text NOT NULL,
  host text NOT NULL,
  path text NOT NULL,
  citation_article_id uuid,
  article_title text,
  article_source_type text,
  article_published_at timestamptz,
  captured_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, answer_citation_id),
  FOREIGN KEY (tenant_id, answer_citation_id, answer_sample_id, normalized_url_id)
    REFERENCES answer_citations (tenant_id, id, answer_sample_id, normalized_url_id),
  FOREIGN KEY (tenant_id, normalized_url_id)
    REFERENCES normalized_urls (tenant_id, id),
  FOREIGN KEY (tenant_id, citation_domain_id)
    REFERENCES citation_domains (tenant_id, id),
  FOREIGN KEY (tenant_id, citation_article_id)
    REFERENCES citation_articles (tenant_id, id),
  CHECK (canonical_host = lower(canonical_host)),
  CHECK (registrable_domain = lower(registrable_domain)),
  CHECK (host = lower(host)),
  CHECK (scheme IN ('http', 'https'))
);

CREATE OR REPLACE FUNCTION capture_answer_citation_dimensions() RETURNS trigger AS $$
BEGIN
  INSERT INTO answer_citation_dimension_snapshots (
    tenant_id, answer_citation_id, answer_sample_id, normalized_url_id,
    citation_domain_id, canonical_host, registrable_domain, source_category,
    url_hash, canonical_url, scheme, host, path, citation_article_id,
    article_title, article_source_type, article_published_at
  )
  SELECT
    NEW.tenant_id, NEW.id, NEW.answer_sample_id, NEW.normalized_url_id,
    domain.id, domain.canonical_host, domain.registrable_domain, domain.source_category,
    normalized.url_hash, normalized.canonical_url, normalized.scheme, normalized.host,
    normalized.path, article.id, article.title, article.source_type, article.published_at
  FROM normalized_urls AS normalized
  JOIN citation_domains AS domain
    ON domain.tenant_id = normalized.tenant_id
   AND domain.id = normalized.citation_domain_id
  LEFT JOIN citation_articles AS article
    ON article.tenant_id = normalized.tenant_id
   AND article.normalized_url_id = normalized.id
  WHERE normalized.tenant_id = NEW.tenant_id
    AND normalized.id = NEW.normalized_url_id;

  IF NOT FOUND THEN
    RAISE EXCEPTION 'Answer citation dimensions could not be snapshotted'
      USING ERRCODE = '23514';
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

ALTER TABLE answer_citations NO FORCE ROW LEVEL SECURITY;
ALTER TABLE normalized_urls NO FORCE ROW LEVEL SECURITY;
ALTER TABLE citation_domains NO FORCE ROW LEVEL SECURITY;
ALTER TABLE citation_articles NO FORCE ROW LEVEL SECURITY;

INSERT INTO answer_citation_dimension_snapshots (
  tenant_id, answer_citation_id, answer_sample_id, normalized_url_id,
  citation_domain_id, canonical_host, registrable_domain, source_category,
  url_hash, canonical_url, scheme, host, path, citation_article_id,
  article_title, article_source_type, article_published_at, captured_at
)
SELECT
  citation.tenant_id, citation.id, citation.answer_sample_id, citation.normalized_url_id,
  domain.id, domain.canonical_host, domain.registrable_domain, domain.source_category,
  normalized.url_hash, normalized.canonical_url, normalized.scheme, normalized.host,
  normalized.path, article.id, article.title, article.source_type, article.published_at,
  citation.created_at
FROM answer_citations AS citation
JOIN normalized_urls AS normalized
  ON normalized.tenant_id = citation.tenant_id
 AND normalized.id = citation.normalized_url_id
JOIN citation_domains AS domain
  ON domain.tenant_id = normalized.tenant_id
 AND domain.id = normalized.citation_domain_id
LEFT JOIN citation_articles AS article
  ON article.tenant_id = normalized.tenant_id
 AND article.normalized_url_id = normalized.id;

ALTER TABLE answer_citations FORCE ROW LEVEL SECURITY;
ALTER TABLE normalized_urls FORCE ROW LEVEL SECURITY;
ALTER TABLE citation_domains FORCE ROW LEVEL SECURITY;
ALTER TABLE citation_articles FORCE ROW LEVEL SECURITY;

CREATE TRIGGER answer_citations_dimension_snapshot
  AFTER INSERT ON answer_citations
  FOR EACH ROW EXECUTE FUNCTION capture_answer_citation_dimensions();

ALTER TABLE daily_brand_metric_rollups
  ADD COLUMN sov_lineage_version smallint;

ALTER TABLE daily_brand_metric_rollups NO FORCE ROW LEVEL SECURITY;
ALTER TABLE daily_brand_metric_rollups DISABLE TRIGGER daily_brand_metric_rollups_immutable;
UPDATE daily_brand_metric_rollups SET sov_lineage_version = 0;
ALTER TABLE daily_brand_metric_rollups ENABLE TRIGGER daily_brand_metric_rollups_immutable;
ALTER TABLE daily_brand_metric_rollups FORCE ROW LEVEL SECURITY;

ALTER TABLE daily_brand_metric_rollups
  ALTER COLUMN sov_lineage_version SET DEFAULT 1,
  ALTER COLUMN sov_lineage_version SET NOT NULL,
  ADD CONSTRAINT daily_brand_metric_rollups_sov_lineage_version_check
    CHECK (sov_lineage_version IN (0, 1));

CREATE TABLE brand_metric_sov_contributions (
  tenant_id uuid NOT NULL,
  rollup_id uuid NOT NULL,
  task_id uuid NOT NULL,
  denominator_brand_id uuid NOT NULL,
  brand_fact_id uuid NOT NULL,
  brand_rule_version_id uuid NOT NULL,
  mention_count integer NOT NULL CHECK (mention_count >= 0),
  applied_mention_count_correction_id uuid,
  applied_correction_field_name text,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, rollup_id, task_id, brand_fact_id),
  UNIQUE (tenant_id, rollup_id, task_id, denominator_brand_id),
  FOREIGN KEY (tenant_id, rollup_id, task_id)
    REFERENCES daily_brand_metric_memberships (tenant_id, rollup_id, task_id),
  FOREIGN KEY (tenant_id, denominator_brand_id, brand_fact_id)
    REFERENCES answer_brand_facts (tenant_id, brand_id, id),
  FOREIGN KEY (tenant_id, denominator_brand_id, brand_rule_version_id)
    REFERENCES brand_rule_versions (tenant_id, brand_id, id),
  FOREIGN KEY (
    tenant_id, brand_fact_id, applied_mention_count_correction_id,
    applied_correction_field_name
  ) REFERENCES brand_fact_corrections (tenant_id, brand_fact_id, id, field_name),
  CHECK (
    (applied_mention_count_correction_id IS NULL AND applied_correction_field_name IS NULL)
    OR (
      applied_mention_count_correction_id IS NOT NULL
      AND applied_correction_field_name = 'mention_count'
    )
  )
);

CREATE OR REPLACE FUNCTION enforce_brand_metric_sov_lineage() RETURNS trigger AS $$
DECLARE
  membership daily_brand_metric_memberships%ROWTYPE;
  rollup daily_brand_metric_rollups%ROWTYPE;
  target_fact answer_brand_facts%ROWTYPE;
  expected_fact_count integer;
  actual_fact_count integer;
  actual_mention_count integer;
BEGIN
  SELECT * INTO membership
  FROM daily_brand_metric_memberships
  WHERE tenant_id = NEW.tenant_id
    AND rollup_id = NEW.rollup_id
    AND task_id = NEW.task_id;

  SELECT * INTO rollup
  FROM daily_brand_metric_rollups
  WHERE tenant_id = NEW.tenant_id
    AND id = NEW.rollup_id;

  IF rollup.sov_lineage_version = 0 THEN
    IF TG_TABLE_NAME = 'brand_metric_sov_contributions' THEN
      RAISE EXCEPTION 'Legacy metric rollups cannot be extended with partial share-of-voice lineage'
        USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
  END IF;

  IF membership.inclusion_status NOT IN ('valid_mentioned', 'valid_unmentioned') THEN
    IF EXISTS (
      SELECT 1
      FROM brand_metric_sov_contributions AS contribution
      WHERE contribution.tenant_id = membership.tenant_id
        AND contribution.rollup_id = membership.rollup_id
        AND contribution.task_id = membership.task_id
    ) OR membership.contribution -> 'monitoredBrandMentionCount' IS DISTINCT FROM '0'::jsonb
    THEN
      RAISE EXCEPTION 'Non-valid metric memberships cannot carry share-of-voice facts'
        USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
  END IF;

  SELECT * INTO target_fact
  FROM answer_brand_facts
  WHERE tenant_id = membership.tenant_id
    AND id = membership.brand_fact_id;

  SELECT count(*)::integer INTO expected_fact_count
  FROM answer_brand_facts
  WHERE tenant_id = membership.tenant_id
    AND answer_parse_id = target_fact.answer_parse_id;

  SELECT count(*)::integer, coalesce(sum(mention_count), 0)::integer
  INTO actual_fact_count, actual_mention_count
  FROM brand_metric_sov_contributions
  WHERE tenant_id = membership.tenant_id
    AND rollup_id = membership.rollup_id
    AND task_id = membership.task_id;

  IF expected_fact_count IS DISTINCT FROM actual_fact_count
    OR EXISTS (
      SELECT 1
      FROM answer_brand_facts AS expected
      WHERE expected.tenant_id = membership.tenant_id
        AND expected.answer_parse_id = target_fact.answer_parse_id
        AND NOT EXISTS (
          SELECT 1
          FROM brand_metric_sov_contributions AS contribution
          WHERE contribution.tenant_id = membership.tenant_id
            AND contribution.rollup_id = membership.rollup_id
            AND contribution.task_id = membership.task_id
            AND contribution.brand_fact_id = expected.id
        )
    )
  THEN
    RAISE EXCEPTION 'Share-of-voice lineage must contain every brand fact from the target parse'
      USING ERRCODE = '23514';
  END IF;

  IF actual_mention_count IS DISTINCT FROM
    (membership.contribution ->> 'monitoredBrandMentionCount')::integer
  THEN
    RAISE EXCEPTION 'Share-of-voice fact sum does not match the metric membership denominator'
      USING ERRCODE = '23514';
  END IF;

  IF NOT EXISTS (
    SELECT 1
    FROM brand_metric_sov_contributions AS contribution
    WHERE contribution.tenant_id = membership.tenant_id
      AND contribution.rollup_id = membership.rollup_id
      AND contribution.task_id = membership.task_id
      AND contribution.denominator_brand_id = membership.brand_id
      AND contribution.brand_fact_id = membership.brand_fact_id
      AND contribution.mention_count = (membership.contribution ->> 'mentionCount')::integer
  ) THEN
    RAISE EXCEPTION 'Share-of-voice lineage must include the target brand contribution'
      USING ERRCODE = '23514';
  END IF;

  IF EXISTS (
    SELECT 1
    FROM brand_metric_sov_contributions AS contribution
    JOIN answer_brand_facts AS fact
      ON fact.tenant_id = contribution.tenant_id
     AND fact.id = contribution.brand_fact_id
    JOIN answer_parses AS parse
      ON parse.tenant_id = fact.tenant_id
     AND parse.id = fact.answer_parse_id
    JOIN brands AS brand
      ON brand.tenant_id = fact.tenant_id
     AND brand.id = fact.brand_id
    LEFT JOIN brand_fact_corrections AS correction
      ON correction.tenant_id = contribution.tenant_id
     AND correction.id = contribution.applied_mention_count_correction_id
    WHERE contribution.tenant_id = membership.tenant_id
      AND contribution.rollup_id = membership.rollup_id
      AND contribution.task_id = membership.task_id
      AND (
        fact.answer_parse_id IS DISTINCT FROM target_fact.answer_parse_id
        OR fact.answer_sample_id IS DISTINCT FROM membership.answer_sample_id
        OR fact.brand_id IS DISTINCT FROM contribution.denominator_brand_id
        OR fact.brand_rule_version_id IS DISTINCT FROM contribution.brand_rule_version_id
        OR parse.parser_version_id IS DISTINCT FROM rollup.parser_version_id
        OR parse.status IS DISTINCT FROM 'completed'
        OR brand.brand_project_id IS DISTINCT FROM rollup.brand_project_id
        OR contribution.mention_count IS DISTINCT FROM CASE
          WHEN contribution.applied_mention_count_correction_id IS NULL THEN fact.mention_count
          ELSE (correction.corrected_value #>> '{}')::integer
        END
        OR (
          contribution.applied_mention_count_correction_id IS NOT NULL
          AND EXISTS (
            SELECT 1
            FROM brand_fact_corrections AS successor
            WHERE successor.tenant_id = contribution.tenant_id
              AND successor.supersedes_correction_id =
                contribution.applied_mention_count_correction_id
          )
        )
      )
  ) THEN
    RAISE EXCEPTION 'Share-of-voice contribution does not match its parse, brand, or effective fact'
      USING ERRCODE = '23514';
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER daily_brand_metric_memberships_sov_lineage_guard
  AFTER INSERT ON daily_brand_metric_memberships
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION enforce_brand_metric_sov_lineage();

CREATE CONSTRAINT TRIGGER brand_metric_sov_contributions_lineage_guard
  AFTER INSERT ON brand_metric_sov_contributions
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION enforce_brand_metric_sov_lineage();

CREATE TRIGGER brand_metric_sov_contributions_immutable
  BEFORE UPDATE OR DELETE ON brand_metric_sov_contributions
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();

CREATE TRIGGER answer_citation_dimension_snapshots_immutable
  BEFORE UPDATE OR DELETE ON answer_citation_dimension_snapshots
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();

DO $$
DECLARE
  table_name text;
BEGIN
  FOREACH table_name IN ARRAY ARRAY[
    'answer_citation_dimension_snapshots', 'brand_metric_sov_contributions'
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

CREATE OR REPLACE FUNCTION enforce_brand_metric_effective_fact_values() RETURNS trigger AS $$
DECLARE
  membership daily_brand_metric_memberships%ROWTYPE;
  rollup daily_brand_metric_rollups%ROWTYPE;
  fact answer_brand_facts%ROWTYPE;
  correction brand_fact_corrections%ROWTYPE;
  metric_field_name text;
  actual_value jsonb;
  expected_value jsonb;
  expected_cited boolean;
BEGIN
  SELECT * INTO membership
  FROM daily_brand_metric_memberships
  WHERE tenant_id = NEW.tenant_id
    AND rollup_id = NEW.rollup_id
    AND task_id = NEW.task_id;

  IF membership.brand_fact_id IS NULL THEN
    RETURN NEW;
  END IF;

  SELECT * INTO rollup
  FROM daily_brand_metric_rollups
  WHERE tenant_id = membership.tenant_id
    AND id = membership.rollup_id;

  SELECT * INTO fact
  FROM answer_brand_facts
  WHERE tenant_id = membership.tenant_id
    AND id = membership.brand_fact_id;

  FOREACH metric_field_name IN ARRAY ARRAY[
    'mentioned', 'mention_count', 'first_mention_rank', 'sentiment'
  ]
  LOOP
    SELECT fact_correction.* INTO correction
    FROM brand_metric_membership_corrections AS membership_correction
    JOIN brand_fact_corrections AS fact_correction
      ON fact_correction.tenant_id = membership_correction.tenant_id
     AND fact_correction.id = membership_correction.correction_id
    WHERE membership_correction.tenant_id = membership.tenant_id
      AND membership_correction.rollup_id = membership.rollup_id
      AND membership_correction.task_id = membership.task_id
      AND membership_correction.field_name = metric_field_name;

    CASE metric_field_name
      WHEN 'mentioned' THEN
        actual_value := to_jsonb(membership.inclusion_status = 'valid_mentioned');
        expected_value := to_jsonb(fact.mentioned);
      WHEN 'mention_count' THEN
        actual_value := membership.contribution -> 'mentionCount';
        expected_value := to_jsonb(fact.mention_count);
      WHEN 'first_mention_rank' THEN
        actual_value := membership.contribution -> 'firstRank';
        expected_value := to_jsonb(fact.first_mention_rank);
      WHEN 'sentiment' THEN
        actual_value := membership.contribution -> 'sentiment';
        expected_value := CASE
          WHEN fact.sentiment = 'not_applicable' THEN 'null'::jsonb
          ELSE to_jsonb(fact.sentiment)
        END;
    END CASE;

    IF correction.id IS NOT NULL THEN
      expected_value := correction.corrected_value;
    END IF;

    IF actual_value IS DISTINCT FROM expected_value THEN
      RAISE EXCEPTION 'Metric membership fact contribution does not match machine fact or applied correction for %', metric_field_name
        USING ERRCODE = '23514';
    END IF;
  END LOOP;

  SELECT EXISTS (
    SELECT 1
    FROM answer_citation_dimension_snapshots AS citation_snapshot
    JOIN brand_domains AS brand_domain
      ON brand_domain.tenant_id = membership.tenant_id
     AND brand_domain.brand_rule_version_id = rollup.brand_rule_version_id
     AND brand_domain.registrable_domain = citation_snapshot.registrable_domain
    WHERE citation_snapshot.tenant_id = membership.tenant_id
      AND citation_snapshot.answer_sample_id = membership.answer_sample_id
  ) INTO expected_cited;

  IF membership.contribution -> 'cited' IS DISTINCT FROM to_jsonb(expected_cited) THEN
    RAISE EXCEPTION 'Metric membership citation contribution does not match snapshotted brand-domain evidence'
      USING ERRCODE = '23514';
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;
