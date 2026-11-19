ALTER TABLE brand_domains
  ADD COLUMN IF NOT EXISTS brand_rule_version_id uuid;

UPDATE brand_domains AS domain
SET brand_rule_version_id = (
  SELECT version.id
  FROM brand_rule_versions AS version
  JOIN brands AS brand
    ON brand.tenant_id = version.tenant_id
   AND brand.id = version.brand_id
  WHERE version.tenant_id = domain.tenant_id
    AND version.brand_id = domain.brand_id
  ORDER BY
    (version.version = brand.rule_version) DESC,
    version.version DESC
  LIMIT 1
)
WHERE domain.brand_rule_version_id IS NULL;

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM brand_domains WHERE brand_rule_version_id IS NULL) THEN
    RAISE EXCEPTION 'Cannot assign a brand rule version to every existing brand domain';
  END IF;
END $$;

ALTER TABLE brand_domains
  ALTER COLUMN brand_rule_version_id SET NOT NULL,
  DROP CONSTRAINT IF EXISTS brand_domains_tenant_id_brand_id_registrable_domain_key;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'brand_domains'::regclass
      AND conname = 'brand_domains_tenant_rule_domain_unique'
  ) THEN
    ALTER TABLE brand_domains
      ADD CONSTRAINT brand_domains_tenant_rule_domain_unique
      UNIQUE (tenant_id, brand_rule_version_id, registrable_domain);
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'brand_domains'::regclass
      AND conname = 'brand_domains_tenant_brand_rule_fk'
  ) THEN
    ALTER TABLE brand_domains
      ADD CONSTRAINT brand_domains_tenant_brand_rule_fk
      FOREIGN KEY (tenant_id, brand_id, brand_rule_version_id)
      REFERENCES brand_rule_versions (tenant_id, brand_id, id);
  END IF;
END $$;

ALTER TABLE question_versions
  ADD COLUMN IF NOT EXISTS brand_project_id uuid,
  ADD COLUMN IF NOT EXISTS topic_group_id uuid;

ALTER TABLE questions NO FORCE ROW LEVEL SECURITY;
ALTER TABLE question_versions NO FORCE ROW LEVEL SECURITY;

UPDATE question_versions AS version
SET
  brand_project_id = question.brand_project_id,
  topic_group_id = question.topic_group_id
FROM questions AS question
WHERE question.tenant_id = version.tenant_id
  AND question.id = version.question_id
  AND (
    version.brand_project_id IS NULL
    OR version.topic_group_id IS NULL
  );

ALTER TABLE questions FORCE ROW LEVEL SECURITY;
ALTER TABLE question_versions FORCE ROW LEVEL SECURITY;

ALTER TABLE question_versions
  ALTER COLUMN brand_project_id SET NOT NULL,
  ALTER COLUMN topic_group_id SET NOT NULL;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'question_versions'::regclass
      AND conname = 'question_versions_tenant_project_id_unique'
  ) THEN
    ALTER TABLE question_versions
      ADD CONSTRAINT question_versions_tenant_project_id_unique
      UNIQUE (tenant_id, brand_project_id, id);
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'question_versions'::regclass
      AND conname = 'question_versions_dimension_snapshot_unique'
  ) THEN
    ALTER TABLE question_versions
      ADD CONSTRAINT question_versions_dimension_snapshot_unique
      UNIQUE (tenant_id, brand_project_id, question_id, topic_group_id, id);
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'question_versions'::regclass
      AND conname = 'question_versions_project_question_fk'
  ) THEN
    ALTER TABLE question_versions
      ADD CONSTRAINT question_versions_project_question_fk
      FOREIGN KEY (tenant_id, brand_project_id, question_id)
      REFERENCES questions (tenant_id, brand_project_id, id);
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'question_versions'::regclass
      AND conname = 'question_versions_project_topic_group_fk'
  ) THEN
    ALTER TABLE question_versions
      ADD CONSTRAINT question_versions_project_topic_group_fk
      FOREIGN KEY (tenant_id, brand_project_id, topic_group_id)
      REFERENCES topic_groups (tenant_id, brand_project_id, id);
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'matrix_cells'::regclass
      AND conname = 'matrix_cells_project_question_version_fk'
  ) THEN
    ALTER TABLE matrix_cells
      ADD CONSTRAINT matrix_cells_project_question_version_fk
      FOREIGN KEY (tenant_id, brand_project_id, question_version_id)
      REFERENCES question_versions (tenant_id, brand_project_id, id);
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'collection_tasks'::regclass
      AND conname = 'collection_tasks_project_question_version_fk'
  ) THEN
    ALTER TABLE collection_tasks
      ADD CONSTRAINT collection_tasks_project_question_version_fk
      FOREIGN KEY (tenant_id, brand_project_id, question_version_id)
      REFERENCES question_versions (tenant_id, brand_project_id, id);
  END IF;
END $$;

ALTER TABLE daily_brand_metric_memberships
  ADD COLUMN IF NOT EXISTS contribution jsonb NOT NULL DEFAULT '{}'::jsonb;

UPDATE daily_brand_metric_memberships
SET contribution = jsonb_build_object(
  'mentionCount', 0,
  'monitoredBrandMentionCount', 0,
  'firstRank', NULL,
  'cited', false,
  'sentiment', NULL
)
WHERE contribution = '{}'::jsonb;

ALTER TABLE daily_brand_metric_memberships
  DROP CONSTRAINT IF EXISTS daily_brand_metric_memberships_check;

ALTER TABLE daily_brand_metric_memberships
  ADD CONSTRAINT daily_brand_metric_memberships_check CHECK (
    (inclusion_status = 'valid_mentioned' AND answer_sample_id IS NOT NULL AND brand_fact_id IS NOT NULL)
    OR (inclusion_status = 'valid_unmentioned' AND answer_sample_id IS NOT NULL AND brand_fact_id IS NOT NULL)
    OR (inclusion_status = 'invalid' AND answer_sample_id IS NOT NULL AND brand_fact_id IS NULL)
    OR (inclusion_status IN ('failed', 'missing') AND answer_sample_id IS NULL AND brand_fact_id IS NULL)
  );

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'answer_samples'::regclass
      AND conname = 'answer_samples_tenant_task_id_unique'
  ) THEN
    ALTER TABLE answer_samples
      ADD CONSTRAINT answer_samples_tenant_task_id_unique
      UNIQUE (tenant_id, task_id, id);
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'daily_brand_metric_memberships'::regclass
      AND conname = 'daily_brand_metric_memberships_tenant_task_sample_fk'
  ) THEN
    ALTER TABLE daily_brand_metric_memberships
      ADD CONSTRAINT daily_brand_metric_memberships_tenant_task_sample_fk
      FOREIGN KEY (tenant_id, task_id, answer_sample_id)
      REFERENCES answer_samples (tenant_id, task_id, id);
  END IF;
END $$;

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

DROP TRIGGER IF EXISTS daily_brand_metric_membership_consistency_guard
  ON daily_brand_metric_memberships;
CREATE TRIGGER daily_brand_metric_membership_consistency_guard
  BEFORE INSERT ON daily_brand_metric_memberships
  FOR EACH ROW EXECUTE FUNCTION enforce_brand_metric_membership_consistency();

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

CREATE OR REPLACE FUNCTION prevent_immutable_analytics_mutation() RETURNS trigger AS $$
BEGIN
  RAISE EXCEPTION '% rows are immutable; create a new version or lineage record', TG_TABLE_NAME
    USING ERRCODE = '55000';
END;
$$ LANGUAGE plpgsql;

DO $$
DECLARE
  trigger_definition record;
BEGIN
  FOR trigger_definition IN
    SELECT * FROM (VALUES
      ('parser_versions', 'parser_versions_definition_immutable', 'BEFORE UPDATE', 'protect_parser_version_definition'),
      ('metric_versions', 'metric_versions_definition_immutable', 'BEFORE UPDATE', 'protect_metric_version_definition'),
      ('brand_rule_versions', 'brand_rule_versions_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation'),
      ('parser_versions', 'parser_versions_delete_immutable', 'BEFORE DELETE', 'prevent_immutable_analytics_mutation'),
      ('metric_versions', 'metric_versions_delete_immutable', 'BEFORE DELETE', 'prevent_immutable_analytics_mutation'),
      ('brand_match_rules', 'brand_match_rules_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation'),
      ('brand_domains', 'brand_domains_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation'),
      ('answer_parses', 'answer_parses_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation'),
      ('answer_brand_facts', 'answer_brand_facts_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation'),
      ('brand_mention_occurrences', 'brand_mention_occurrences_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation'),
      ('brand_fact_corrections', 'brand_fact_corrections_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation'),
      ('brand_fact_risks', 'brand_fact_risks_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation'),
      ('brand_fact_risk_corrections', 'brand_fact_risk_corrections_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation'),
      ('daily_brand_metric_rollups', 'daily_brand_metric_rollups_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation'),
      ('brand_metric_observations', 'brand_metric_observations_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation'),
      ('daily_brand_metric_memberships', 'daily_brand_metric_memberships_immutable', 'BEFORE UPDATE OR DELETE', 'prevent_immutable_analytics_mutation')
    ) AS definitions(table_name, trigger_name, timing, function_name)
  LOOP
    EXECUTE format(
      'DROP TRIGGER IF EXISTS %I ON %I',
      trigger_definition.trigger_name,
      trigger_definition.table_name
    );
    EXECUTE format(
      'CREATE TRIGGER %I %s ON %I FOR EACH ROW EXECUTE FUNCTION %I()',
      trigger_definition.trigger_name,
      trigger_definition.timing,
      trigger_definition.table_name,
      trigger_definition.function_name
    );
  END LOOP;
END $$;
