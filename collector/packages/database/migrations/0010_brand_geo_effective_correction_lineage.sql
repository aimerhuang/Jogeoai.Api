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

CREATE OR REPLACE FUNCTION enforce_brand_metric_effective_fact_values() RETURNS trigger AS $$
DECLARE
  membership daily_brand_metric_memberships%ROWTYPE;
  fact answer_brand_facts%ROWTYPE;
  correction brand_fact_corrections%ROWTYPE;
  metric_field_name text;
  actual_value jsonb;
  expected_value jsonb;
BEGIN
  SELECT * INTO membership
  FROM daily_brand_metric_memberships
  WHERE tenant_id = NEW.tenant_id
    AND rollup_id = NEW.rollup_id
    AND task_id = NEW.task_id;

  IF membership.brand_fact_id IS NULL THEN
    RETURN NEW;
  END IF;

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

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER daily_brand_metric_memberships_effective_fact_guard
  AFTER INSERT ON daily_brand_metric_memberships
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION enforce_brand_metric_effective_fact_values();

CREATE CONSTRAINT TRIGGER brand_metric_membership_corrections_effective_fact_guard
  AFTER INSERT ON brand_metric_membership_corrections
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION enforce_brand_metric_effective_fact_values();
