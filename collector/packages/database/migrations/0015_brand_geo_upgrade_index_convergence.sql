CREATE UNIQUE INDEX IF NOT EXISTS brand_fact_corrections_one_lineage_root_idx
  ON brand_fact_corrections (tenant_id, brand_fact_id, field_name)
  WHERE supersedes_correction_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS brand_fact_risk_corrections_one_lineage_root_idx
  ON brand_fact_risk_corrections (tenant_id, brand_fact_risk_id)
  WHERE supersedes_correction_id IS NULL;

DROP INDEX IF EXISTS citation_articles_domain_idx;
CREATE INDEX citation_articles_domain_idx
  ON citation_articles (tenant_id, citation_domain_id, normalized_url_id);
