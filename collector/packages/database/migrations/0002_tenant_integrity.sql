CREATE OR REPLACE FUNCTION enforce_active_question_limit() RETURNS trigger AS $$
DECLARE
  active_count integer;
BEGIN
  IF NEW.status <> 'active' THEN
    RETURN NEW;
  END IF;

  PERFORM pg_advisory_xact_lock(
    hashtextextended(NEW.tenant_id::text || ':' || NEW.brand_project_id::text, 0)
  );

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

ALTER TABLE users ADD CONSTRAINT users_tenant_id_id_unique UNIQUE (tenant_id, id);
ALTER TABLE brand_projects ADD CONSTRAINT brand_projects_tenant_id_id_unique UNIQUE (tenant_id, id);
ALTER TABLE topic_groups ADD CONSTRAINT topic_groups_tenant_id_id_unique UNIQUE (tenant_id, id);
ALTER TABLE questions ADD CONSTRAINT questions_tenant_id_id_unique UNIQUE (tenant_id, id);
ALTER TABLE question_versions ADD CONSTRAINT question_versions_tenant_id_id_unique UNIQUE (tenant_id, id);
ALTER TABLE monitoring_matrices ADD CONSTRAINT monitoring_matrices_tenant_id_id_unique UNIQUE (tenant_id, id);
ALTER TABLE matrix_cells ADD CONSTRAINT matrix_cells_tenant_id_id_unique UNIQUE (tenant_id, id);
ALTER TABLE collection_tasks ADD CONSTRAINT collection_tasks_tenant_id_id_unique UNIQUE (tenant_id, id);
ALTER TABLE result_submissions ADD CONSTRAINT result_submissions_tenant_id_id_unique UNIQUE (tenant_id, id);
ALTER TABLE answer_samples ADD CONSTRAINT answer_samples_tenant_id_id_unique UNIQUE (tenant_id, id);

ALTER TABLE memberships
  ADD CONSTRAINT memberships_tenant_user_fk
  FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id);

ALTER TABLE brands
  ADD CONSTRAINT brands_tenant_project_fk
  FOREIGN KEY (tenant_id, brand_project_id) REFERENCES brand_projects (tenant_id, id);

ALTER TABLE topic_groups
  ADD CONSTRAINT topic_groups_tenant_project_fk
  FOREIGN KEY (tenant_id, brand_project_id) REFERENCES brand_projects (tenant_id, id);

ALTER TABLE questions
  ADD CONSTRAINT questions_tenant_project_fk
  FOREIGN KEY (tenant_id, brand_project_id) REFERENCES brand_projects (tenant_id, id),
  ADD CONSTRAINT questions_tenant_topic_group_fk
  FOREIGN KEY (tenant_id, topic_group_id) REFERENCES topic_groups (tenant_id, id);

ALTER TABLE question_versions
  ADD CONSTRAINT question_versions_tenant_question_fk
  FOREIGN KEY (tenant_id, question_id) REFERENCES questions (tenant_id, id);

ALTER TABLE monitoring_matrices
  ADD CONSTRAINT monitoring_matrices_tenant_project_fk
  FOREIGN KEY (tenant_id, brand_project_id) REFERENCES brand_projects (tenant_id, id);

ALTER TABLE matrix_cells
  ADD CONSTRAINT matrix_cells_tenant_project_fk
  FOREIGN KEY (tenant_id, brand_project_id) REFERENCES brand_projects (tenant_id, id),
  ADD CONSTRAINT matrix_cells_tenant_matrix_fk
  FOREIGN KEY (tenant_id, matrix_id) REFERENCES monitoring_matrices (tenant_id, id),
  ADD CONSTRAINT matrix_cells_tenant_question_version_fk
  FOREIGN KEY (tenant_id, question_version_id) REFERENCES question_versions (tenant_id, id);

ALTER TABLE collection_tasks
  ADD CONSTRAINT collection_tasks_tenant_project_fk
  FOREIGN KEY (tenant_id, brand_project_id) REFERENCES brand_projects (tenant_id, id),
  ADD CONSTRAINT collection_tasks_tenant_matrix_cell_fk
  FOREIGN KEY (tenant_id, matrix_cell_id) REFERENCES matrix_cells (tenant_id, id),
  ADD CONSTRAINT collection_tasks_tenant_question_version_fk
  FOREIGN KEY (tenant_id, question_version_id) REFERENCES question_versions (tenant_id, id);

ALTER TABLE collection_attempts
  ADD CONSTRAINT collection_attempts_tenant_task_fk
  FOREIGN KEY (tenant_id, task_id) REFERENCES collection_tasks (tenant_id, id);

ALTER TABLE result_submissions
  ADD CONSTRAINT result_submissions_tenant_task_fk
  FOREIGN KEY (tenant_id, task_id) REFERENCES collection_tasks (tenant_id, id);

ALTER TABLE answer_samples
  ADD CONSTRAINT answer_samples_tenant_task_fk
  FOREIGN KEY (tenant_id, task_id) REFERENCES collection_tasks (tenant_id, id),
  ADD CONSTRAINT answer_samples_tenant_submission_fk
  FOREIGN KEY (tenant_id, result_submission_id) REFERENCES result_submissions (tenant_id, id);

ALTER TABLE raw_answers
  ADD CONSTRAINT raw_answers_tenant_sample_fk
  FOREIGN KEY (tenant_id, answer_sample_id) REFERENCES answer_samples (tenant_id, id);

ALTER TABLE citations
  ADD CONSTRAINT citations_tenant_sample_fk
  FOREIGN KEY (tenant_id, answer_sample_id) REFERENCES answer_samples (tenant_id, id);
