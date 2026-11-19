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

DROP TRIGGER IF EXISTS answer_brand_facts_completed_parse_guard ON answer_brand_facts;
CREATE TRIGGER answer_brand_facts_completed_parse_guard
  BEFORE INSERT ON answer_brand_facts
  FOR EACH ROW EXECUTE FUNCTION enforce_answer_brand_fact_completed_parse();

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
