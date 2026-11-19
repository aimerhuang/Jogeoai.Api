CREATE TABLE IF NOT EXISTS schema_migrations (
  version text PRIMARY KEY,
  applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tenants (
  id uuid PRIMARY KEY,
  slug text NOT NULL UNIQUE,
  name text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE users (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  email text NOT NULL,
  display_name text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, email)
);

CREATE TABLE memberships (
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  user_id uuid NOT NULL REFERENCES users(id),
  role text NOT NULL CHECK (role IN ('admin', 'member')),
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, user_id)
);

CREATE TABLE brand_projects (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  name text NOT NULL,
  fixture boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, name)
);

CREATE TABLE brands (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  brand_project_id uuid NOT NULL REFERENCES brand_projects(id),
  canonical_name text NOT NULL,
  kind text NOT NULL CHECK (kind IN ('primary', 'competitor')),
  aliases jsonb NOT NULL DEFAULT '[]'::jsonb,
  exclusion_terms jsonb NOT NULL DEFAULT '[]'::jsonb,
  rule_version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, brand_project_id, canonical_name)
);

CREATE TABLE topic_groups (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  brand_project_id uuid NOT NULL REFERENCES brand_projects(id),
  name text NOT NULL,
  sort_order integer NOT NULL DEFAULT 0,
  status text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive', 'archived')),
  fixture boolean NOT NULL DEFAULT false,
  UNIQUE (tenant_id, brand_project_id, name)
);

CREATE TABLE questions (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  brand_project_id uuid NOT NULL REFERENCES brand_projects(id),
  topic_group_id uuid NOT NULL REFERENCES topic_groups(id),
  status text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive', 'archived')),
  fixture boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE question_versions (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  question_id uuid NOT NULL REFERENCES questions(id),
  version integer NOT NULL,
  question_text text NOT NULL CHECK (length(btrim(question_text)) > 0),
  language text NOT NULL DEFAULT 'zh-CN',
  fixture boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, question_id, version)
);

CREATE TABLE platforms (
  code text PRIMARY KEY,
  display_name text NOT NULL,
  region text NOT NULL CHECK (region IN ('cn', 'overseas')),
  enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE platform_entries (
  code text PRIMARY KEY,
  platform_code text NOT NULL REFERENCES platforms(code),
  display_name text NOT NULL,
  region text NOT NULL CHECK (region IN ('cn', 'overseas')),
  channel text NOT NULL CHECK (channel IN ('consumer_web')),
  version integer NOT NULL DEFAULT 1,
  enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE monitoring_matrices (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  brand_project_id uuid NOT NULL REFERENCES brand_projects(id),
  name text NOT NULL,
  frequency text NOT NULL DEFAULT 'daily' CHECK (frequency IN ('daily')),
  samples_per_period integer NOT NULL DEFAULT 1 CHECK (samples_per_period BETWEEN 1 AND 10),
  enabled boolean NOT NULL DEFAULT true,
  fixture boolean NOT NULL DEFAULT false,
  UNIQUE (tenant_id, brand_project_id, name)
);

CREATE TABLE matrix_cells (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  brand_project_id uuid NOT NULL REFERENCES brand_projects(id),
  matrix_id uuid NOT NULL REFERENCES monitoring_matrices(id),
  question_version_id uuid NOT NULL REFERENCES question_versions(id),
  platform_entry_code text NOT NULL REFERENCES platform_entries(code),
  region text NOT NULL CHECK (region IN ('cn', 'overseas')),
  language text NOT NULL,
  channel text NOT NULL CHECK (channel IN ('consumer_web')),
  enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, matrix_id, question_version_id, platform_entry_code)
);

CREATE TABLE collection_tasks (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  brand_project_id uuid NOT NULL REFERENCES brand_projects(id),
  matrix_cell_id uuid NOT NULL REFERENCES matrix_cells(id),
  question_version_id uuid NOT NULL REFERENCES question_versions(id),
  platform_entry_code text NOT NULL REFERENCES platform_entries(code),
  platform_code text NOT NULL REFERENCES platforms(code),
  region text NOT NULL CHECK (region IN ('cn', 'overseas')),
  language text NOT NULL,
  channel text NOT NULL CHECK (channel IN ('consumer_web')),
  question_text text NOT NULL,
  scheduled_date date NOT NULL,
  sample_index integer NOT NULL CHECK (sample_index > 0),
  run_kind text NOT NULL CHECK (run_kind IN ('scheduled', 'backfill', 'manual')),
  business_key text NOT NULL UNIQUE,
  status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'leased', 'completed', 'failed')),
  lease_version integer NOT NULL DEFAULT 0 CHECK (lease_version >= 0),
  lease_owner text,
  lease_expires_at timestamptz,
  queued_at timestamptz NOT NULL DEFAULT now(),
  started_at timestamptz,
  answer_completed_at timestamptz,
  parsed_at timestamptz,
  completed_at timestamptz,
  CHECK ((status = 'leased') = (lease_owner IS NOT NULL AND lease_expires_at IS NOT NULL))
);

CREATE INDEX collection_tasks_claim_idx
  ON collection_tasks (tenant_id, region, status, scheduled_date, lease_expires_at);

CREATE TABLE collection_attempts (
  id bigserial PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  task_id uuid NOT NULL REFERENCES collection_tasks(id),
  lease_version integer NOT NULL,
  node_id text NOT NULL,
  region text NOT NULL CHECK (region IN ('cn', 'overseas')),
  event_kind text NOT NULL CHECK (event_kind IN ('leased', 'heartbeat', 'accepted', 'duplicate', 'stale')),
  idempotency_key text,
  payload_hash text,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE result_submissions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  task_id uuid NOT NULL REFERENCES collection_tasks(id),
  lease_version integer NOT NULL,
  node_id text NOT NULL,
  region text NOT NULL CHECK (region IN ('cn', 'overseas')),
  idempotency_key text NOT NULL,
  payload_hash text NOT NULL,
  disposition text NOT NULL CHECK (disposition IN ('accepted', 'duplicate', 'stale')),
  answer_text text NOT NULL,
  citations jsonb NOT NULL DEFAULT '[]'::jsonb,
  egress_country text NOT NULL CHECK (length(egress_country) = 2),
  collector_build_revision text NOT NULL,
  completed_at timestamptz NOT NULL,
  received_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (task_id, idempotency_key)
);

CREATE TABLE answer_samples (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  task_id uuid NOT NULL UNIQUE REFERENCES collection_tasks(id),
  result_submission_id uuid NOT NULL UNIQUE REFERENCES result_submissions(id),
  collector_node_id text NOT NULL,
  collector_region text NOT NULL CHECK (collector_region IN ('cn', 'overseas')),
  egress_country text NOT NULL,
  collector_build_revision text NOT NULL,
  valid boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE raw_answers (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  answer_sample_id uuid NOT NULL UNIQUE REFERENCES answer_samples(id),
  body text NOT NULL,
  content_hash text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE citations (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  answer_sample_id uuid NOT NULL REFERENCES answer_samples(id),
  url text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (answer_sample_id, url)
);

CREATE OR REPLACE FUNCTION enforce_active_question_limit() RETURNS trigger AS $$
DECLARE
  active_count integer;
BEGIN
  IF NEW.status <> 'active' THEN
    RETURN NEW;
  END IF;

  SELECT count(*) INTO active_count
  FROM questions
  WHERE tenant_id = NEW.tenant_id
    AND brand_project_id = NEW.brand_project_id
    AND status = 'active'
    AND id <> NEW.id;

  IF active_count >= 30 THEN
    RAISE EXCEPTION 'A brand project may have at most 30 active questions'
      USING ERRCODE = 'P0001';
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER questions_active_limit
  BEFORE INSERT OR UPDATE OF status, brand_project_id ON questions
  FOR EACH ROW EXECUTE FUNCTION enforce_active_question_limit();

DO $$
DECLARE
  table_name text;
BEGIN
  FOREACH table_name IN ARRAY ARRAY[
    'users', 'memberships', 'brand_projects', 'brands', 'topic_groups',
    'questions', 'question_versions', 'monitoring_matrices', 'matrix_cells',
    'collection_tasks', 'collection_attempts', 'result_submissions', 'answer_samples',
    'raw_answers', 'citations'
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

ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenants
  USING (id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (id = nullif(current_setting('app.tenant_id', true), '')::uuid);
