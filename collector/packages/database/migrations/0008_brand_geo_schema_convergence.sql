DO $$
BEGIN
  IF EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'question_tag_assignments'
      AND column_name = 'question_id'
  ) AND NOT EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'question_tag_assignments'
      AND column_name = 'question_version_id'
  ) THEN
    ALTER TABLE question_versions NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE question_tags NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE question_tag_assignments NO FORCE ROW LEVEL SECURITY;

    IF EXISTS (
      SELECT 1
      FROM question_tag_assignments AS assignment
      LEFT JOIN question_versions AS version
        ON version.tenant_id = assignment.tenant_id
       AND version.question_id = assignment.question_id
      WHERE version.id IS NULL
    ) THEN
      RAISE EXCEPTION 'Cannot migrate a question tag assignment without a question version';
    END IF;

    ALTER TABLE question_tag_assignments RENAME TO question_tag_assignments_legacy;

    CREATE TABLE question_tag_assignments (
      tenant_id uuid NOT NULL,
      brand_project_id uuid NOT NULL,
      question_version_id uuid NOT NULL,
      question_tag_id uuid NOT NULL,
      created_at timestamptz NOT NULL DEFAULT now()
    );

    INSERT INTO question_tag_assignments (
      tenant_id, brand_project_id, question_version_id, question_tag_id, created_at
    )
    SELECT
      assignment.tenant_id,
      assignment.brand_project_id,
      version.id,
      assignment.question_tag_id,
      assignment.created_at
    FROM question_tag_assignments_legacy AS assignment
    JOIN question_versions AS version
      ON version.tenant_id = assignment.tenant_id
     AND version.question_id = assignment.question_id;

    DROP TABLE question_tag_assignments_legacy;

    ALTER TABLE question_tag_assignments
      ADD PRIMARY KEY (tenant_id, question_version_id, question_tag_id),
      ADD FOREIGN KEY (tenant_id, brand_project_id, question_version_id)
        REFERENCES question_versions (tenant_id, brand_project_id, id),
      ADD FOREIGN KEY (tenant_id, brand_project_id, question_tag_id)
        REFERENCES question_tags (tenant_id, brand_project_id, id);

    ALTER TABLE question_versions FORCE ROW LEVEL SECURITY;
    ALTER TABLE question_tags FORCE ROW LEVEL SECURITY;
  END IF;
END $$;

ALTER TABLE brand_domains
  DROP CONSTRAINT IF EXISTS brand_domains_tenant_id_brand_id_fkey;

ALTER TABLE daily_brand_metric_memberships
  DROP CONSTRAINT IF EXISTS daily_brand_metric_memberships_tenant_id_answer_sample_id_fkey;

ALTER TABLE daily_brand_metric_rollups
  DROP CONSTRAINT IF EXISTS daily_brand_metric_rollups_tenant_id_brand_project_id_ques_fkey,
  DROP CONSTRAINT IF EXISTS daily_brand_metric_rollups_tenant_id_brand_project_id_topi_fkey,
  DROP CONSTRAINT IF EXISTS daily_brand_metric_rollups_tenant_id_question_id_question__fkey;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'daily_brand_metric_rollups'::regclass
      AND conname = 'daily_brand_metric_rollups_question_dimension_fk'
  ) THEN
    ALTER TABLE daily_brand_metric_rollups
      ADD CONSTRAINT daily_brand_metric_rollups_question_dimension_fk
      FOREIGN KEY (
        tenant_id, brand_project_id, question_id, topic_group_id, question_version_id
      ) REFERENCES question_versions (
        tenant_id, brand_project_id, question_id, topic_group_id, id
      );
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'daily_brand_metric_memberships'::regclass
      AND conname = 'daily_brand_metric_memberships_contribution_object_check'
  ) THEN
    ALTER TABLE daily_brand_metric_memberships
      ADD CONSTRAINT daily_brand_metric_memberships_contribution_object_check
      CHECK (jsonb_typeof(contribution) = 'object');
  END IF;
END $$;

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
    IF NOT EXISTS (
      SELECT 1
      FROM pg_policies
      WHERE schemaname = 'public'
        AND tablename = table_name
        AND policyname = 'tenant_isolation'
    ) THEN
      EXECUTE format(
        'CREATE POLICY tenant_isolation ON %I USING (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid) WITH CHECK (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid)',
        table_name
      );
    END IF;
  END LOOP;
END $$;

DROP TRIGGER IF EXISTS question_tag_assignments_immutable ON question_tag_assignments;
CREATE TRIGGER question_tag_assignments_immutable
  BEFORE UPDATE OR DELETE ON question_tag_assignments
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
