CREATE UNIQUE INDEX metric_versions_one_active_per_code_idx
  ON metric_versions (metric_code)
  WHERE active = true;

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
    FROM answer_citations AS citation
    JOIN normalized_urls AS normalized
      ON normalized.tenant_id = citation.tenant_id
     AND normalized.id = citation.normalized_url_id
    JOIN brand_domains AS brand_domain
      ON brand_domain.tenant_id = membership.tenant_id
     AND brand_domain.brand_rule_version_id = rollup.brand_rule_version_id
     AND brand_domain.registrable_domain = normalized.registrable_domain
    WHERE citation.tenant_id = membership.tenant_id
      AND citation.answer_sample_id = membership.answer_sample_id
  ) INTO expected_cited;

  IF membership.contribution -> 'cited' IS DISTINCT FROM to_jsonb(expected_cited) THEN
    RAISE EXCEPTION 'Metric membership citation contribution does not match normalized brand-domain evidence'
      USING ERRCODE = '23514';
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;
