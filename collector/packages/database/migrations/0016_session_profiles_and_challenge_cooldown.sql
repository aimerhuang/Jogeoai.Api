-- Account pool and challenge cooldown (task 10-28-account-pool-challenge-cooldown).
-- Additive and forward-only: existing rows keep NULL / default values and the
-- legacy lease path keeps working for tenants without registered session profiles.

-- A platform has exactly one region; expose (code, region) so profiles and dispatch
-- states can reference both and cannot claim a platform in the wrong region.
ALTER TABLE platforms
  ADD CONSTRAINT platforms_code_region_unique UNIQUE (code, region);

CREATE TABLE session_profiles (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  platform_code text NOT NULL,
  region text NOT NULL CHECK (region IN ('cn', 'overseas')),
  label text NOT NULL CHECK (length(btrim(label)) BETWEEN 1 AND 128),
  status text NOT NULL DEFAULT 'active' CHECK (
    status IN ('active', 'cooling', 'needs_verification', 'expired', 'disabled')
  ),
  hourly_quota integer NOT NULL DEFAULT 4 CHECK (hourly_quota > 0),
  daily_quota integer NOT NULL DEFAULT 30 CHECK (daily_quota > 0),
  min_interval_seconds integer NOT NULL DEFAULT 600 CHECK (min_interval_seconds >= 0),
  cooldown_until timestamptz,
  consecutive_challenges integer NOT NULL DEFAULT 0 CHECK (consecutive_challenges >= 0),
  last_leased_at timestamptz,
  last_seen_at timestamptz,
  -- Last failure reported during probe (design §12.4); a recent probe failure without a later
  -- lease removes the profile from the fairness queue of other profiles.
  last_probe_failed_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT session_profiles_tenant_id_id_unique UNIQUE (tenant_id, id),
  CONSTRAINT session_profiles_tenant_platform_label_unique UNIQUE (tenant_id, platform_code, label),
  CONSTRAINT session_profiles_platform_region_fk
    FOREIGN KEY (platform_code, region) REFERENCES platforms (code, region),
  CONSTRAINT session_profiles_cooling_has_deadline
    CHECK (status <> 'cooling' OR cooldown_until IS NOT NULL)
);

CREATE INDEX session_profiles_dispatch_idx
  ON session_profiles (tenant_id, region, platform_code, status);

CREATE TABLE platform_dispatch_states (
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  region text NOT NULL CHECK (region IN ('cn', 'overseas')),
  platform_code text NOT NULL,
  min_interval_seconds integer NOT NULL DEFAULT 120 CHECK (min_interval_seconds >= 0),
  daily_cap integer NOT NULL DEFAULT 120 CHECK (daily_cap > 0),
  last_dispatched_at timestamptz,
  paused_at timestamptz,
  pause_reason text,
  breaker_profile_threshold integer NOT NULL DEFAULT 2 CHECK (breaker_profile_threshold > 0),
  breaker_window_minutes integer NOT NULL DEFAULT 60 CHECK (breaker_window_minutes > 0),
  retry_backoff_seconds integer NOT NULL DEFAULT 1800 CHECK (retry_backoff_seconds >= 0),
  max_task_failures integer NOT NULL DEFAULT 3 CHECK (max_task_failures > 0),
  rate_limit_cooldown_base_seconds integer NOT NULL DEFAULT 1800
    CHECK (rate_limit_cooldown_base_seconds > 0),
  rate_limit_cooldown_max_seconds integer NOT NULL DEFAULT 28800
    CHECK (rate_limit_cooldown_max_seconds > 0),
  probe_failure_offline_minutes integer NOT NULL DEFAULT 15
    CHECK (probe_failure_offline_minutes BETWEEN 1 AND 1440),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, region, platform_code),
  CONSTRAINT platform_dispatch_states_platform_region_fk
    FOREIGN KEY (platform_code, region) REFERENCES platforms (code, region),
  CONSTRAINT platform_dispatch_states_pause_reason
    CHECK ((paused_at IS NULL) = (pause_reason IS NULL)),
  CONSTRAINT platform_dispatch_states_cooldown_bounds
    CHECK (rate_limit_cooldown_max_seconds >= rate_limit_cooldown_base_seconds)
);

-- Collector-reportable failure codes. Kept in one place so tasks, attempts and
-- health events agree on the vocabulary.
CREATE OR REPLACE FUNCTION is_reportable_collector_failure(code text) RETURNS boolean AS $$
  SELECT code IN (
    'login_required', 'region_restricted', 'challenge_required', 'rate_limited',
    'page_contract_changed', 'page_contract_unverified', 'collection_timeout', 'browser_failed'
  );
$$ LANGUAGE sql IMMUTABLE;

ALTER TABLE collection_tasks
  ADD COLUMN not_before timestamptz,
  ADD COLUMN failure_count integer NOT NULL DEFAULT 0,
  ADD COLUMN last_failure_code text,
  ADD COLUMN lease_session_profile_id uuid,
  ADD CONSTRAINT collection_tasks_failure_count_nonnegative CHECK (failure_count >= 0),
  ADD CONSTRAINT collection_tasks_last_failure_code_check
    CHECK (last_failure_code IS NULL OR is_reportable_collector_failure(last_failure_code)),
  ADD CONSTRAINT collection_tasks_lease_session_profile_fk
    FOREIGN KEY (tenant_id, lease_session_profile_id) REFERENCES session_profiles (tenant_id, id);

ALTER TABLE collection_attempts
  DROP CONSTRAINT IF EXISTS collection_attempts_event_kind_check;

ALTER TABLE collection_attempts
  ADD COLUMN session_profile_id uuid,
  ADD COLUMN failure_code text,
  ADD CONSTRAINT collection_attempts_event_kind_check CHECK (
    event_kind IN ('leased', 'heartbeat', 'accepted', 'duplicate', 'stale', 'failed')
  ),
  ADD CONSTRAINT collection_attempts_failure_code_check CHECK (
    (event_kind = 'failed') = (failure_code IS NOT NULL)
    AND (failure_code IS NULL OR is_reportable_collector_failure(failure_code))
  ),
  ADD CONSTRAINT collection_attempts_session_profile_fk
    FOREIGN KEY (tenant_id, session_profile_id) REFERENCES session_profiles (tenant_id, id);

-- Quotas are derived from leased attempts per profile and per platform day.
CREATE INDEX collection_attempts_profile_quota_idx
  ON collection_attempts (tenant_id, session_profile_id, event_kind, created_at)
  WHERE session_profile_id IS NOT NULL;

CREATE INDEX collection_attempts_leased_created_idx
  ON collection_attempts (tenant_id, region, created_at)
  WHERE event_kind = 'leased';

CREATE INDEX collection_tasks_platform_claim_idx
  ON collection_tasks (tenant_id, region, platform_code, status, not_before);

ALTER TABLE answer_samples
  ADD COLUMN session_profile_id uuid,
  ADD CONSTRAINT answer_samples_session_profile_fk
    FOREIGN KEY (tenant_id, session_profile_id) REFERENCES session_profiles (tenant_id, id);

-- The accepted sample's profile comes from the control plane lease record, never
-- from the collector request body.
CREATE OR REPLACE FUNCTION enforce_answer_sample_session_profile() RETURNS trigger AS $$
DECLARE
  leased_profile_id uuid;
BEGIN
  SELECT lease_session_profile_id INTO leased_profile_id
  FROM collection_tasks
  WHERE tenant_id = NEW.tenant_id AND id = NEW.task_id;

  IF NEW.session_profile_id IS DISTINCT FROM leased_profile_id THEN
    RAISE EXCEPTION 'Answer sample session profile must equal the task lease session profile'
      USING ERRCODE = '23514';
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER answer_samples_session_profile_lineage
  BEFORE INSERT ON answer_samples
  FOR EACH ROW EXECUTE FUNCTION enforce_answer_sample_session_profile();

CREATE TABLE session_health_events (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  region text NOT NULL CHECK (region IN ('cn', 'overseas')),
  platform_code text NOT NULL,
  session_profile_id uuid,
  event_kind text NOT NULL CHECK (
    event_kind IN (
      'profile_registered', 'challenge_wait_started', 'challenge_cleared',
      'challenge_timeout', 'failure_reported', 'verified', 'cooldown_expired',
      'status_changed_by_operator', 'breaker_tripped', 'breaker_reset',
      'platform_settings_changed'
    )
  ),
  failure_code text CHECK (failure_code IS NULL OR is_reportable_collector_failure(failure_code)),
  phase text CHECK (phase IS NULL OR phase IN ('probe', 'pre_send', 'post_send')),
  task_id uuid,
  lease_version integer CHECK (lease_version IS NULL OR lease_version > 0),
  node_id text,
  from_status text CHECK (
    from_status IS NULL
    OR from_status IN ('active', 'cooling', 'needs_verification', 'expired', 'disabled')
  ),
  to_status text CHECK (
    to_status IS NULL
    OR to_status IN ('active', 'cooling', 'needs_verification', 'expired', 'disabled')
  ),
  created_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT session_health_events_platform_region_fk
    FOREIGN KEY (platform_code, region) REFERENCES platforms (code, region),
  CONSTRAINT session_health_events_profile_fk
    FOREIGN KEY (tenant_id, session_profile_id) REFERENCES session_profiles (tenant_id, id),
  CONSTRAINT session_health_events_task_fk
    FOREIGN KEY (tenant_id, task_id) REFERENCES collection_tasks (tenant_id, id),
  CONSTRAINT session_health_events_profile_scope CHECK (
    (session_profile_id IS NULL) = (event_kind IN ('breaker_reset', 'platform_settings_changed'))
  ),
  CONSTRAINT session_health_events_status_pair CHECK (
    (session_profile_id IS NULL AND from_status IS NULL AND to_status IS NULL)
    OR (session_profile_id IS NOT NULL AND from_status IS NOT NULL AND to_status IS NOT NULL)
  ),
  CONSTRAINT session_health_events_failure_scope CHECK (
    (failure_code IS NOT NULL) = (event_kind IN ('failure_reported', 'challenge_timeout'))
  ),
  CONSTRAINT session_health_events_task_scope CHECK (task_id IS NOT NULL OR lease_version IS NULL)
);

CREATE INDEX session_health_events_breaker_idx
  ON session_health_events (tenant_id, region, platform_code, event_kind, created_at);

CREATE INDEX session_health_events_profile_idx
  ON session_health_events (tenant_id, session_profile_id, created_at);

CREATE TRIGGER session_health_events_immutable
  BEFORE UPDATE OR DELETE ON session_health_events
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();

DO $$
DECLARE
  table_name text;
BEGIN
  FOREACH table_name IN ARRAY ARRAY[
    'session_profiles', 'session_health_events', 'platform_dispatch_states'
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
