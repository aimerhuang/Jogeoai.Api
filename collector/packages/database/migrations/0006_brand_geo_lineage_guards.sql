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

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'citation_domains'::regclass
      AND conname = 'citation_domains_identity_unique'
  ) THEN
    ALTER TABLE citation_domains
      ADD CONSTRAINT citation_domains_identity_unique
      UNIQUE (id, canonical_host, registrable_domain);
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'normalized_urls'::regclass
      AND conname = 'normalized_urls_domain_identity_unique'
  ) THEN
    ALTER TABLE normalized_urls
      ADD CONSTRAINT normalized_urls_domain_identity_unique
      UNIQUE (id, citation_domain_id);
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'normalized_urls'::regclass
      AND conname = 'normalized_urls_domain_dimensions_fk'
  ) THEN
    ALTER TABLE normalized_urls
      ADD CONSTRAINT normalized_urls_domain_dimensions_fk
      FOREIGN KEY (citation_domain_id, host, registrable_domain)
      REFERENCES citation_domains (id, canonical_host, registrable_domain);
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conrelid = 'citation_articles'::regclass
      AND conname = 'citation_articles_url_domain_fk'
  ) THEN
    ALTER TABLE citation_articles
      ADD CONSTRAINT citation_articles_url_domain_fk
      FOREIGN KEY (normalized_url_id, citation_domain_id)
      REFERENCES normalized_urls (id, citation_domain_id);
  END IF;
END $$;

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

DROP TRIGGER IF EXISTS question_versions_snapshot_immutable ON question_versions;
CREATE TRIGGER question_versions_snapshot_immutable
  BEFORE UPDATE OR DELETE ON question_versions
  FOR EACH ROW EXECUTE FUNCTION protect_question_version_snapshot();

DROP TRIGGER IF EXISTS question_tag_assignments_immutable ON question_tag_assignments;
CREATE TRIGGER question_tag_assignments_immutable
  BEFORE UPDATE OR DELETE ON question_tag_assignments
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
