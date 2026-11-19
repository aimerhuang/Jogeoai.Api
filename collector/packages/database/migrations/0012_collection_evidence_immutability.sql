CREATE TRIGGER collection_attempts_immutable
  BEFORE UPDATE OR DELETE ON collection_attempts
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();

CREATE TRIGGER result_submissions_immutable
  BEFORE UPDATE OR DELETE ON result_submissions
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();

CREATE TRIGGER answer_samples_immutable
  BEFORE UPDATE OR DELETE ON answer_samples
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();

CREATE TRIGGER raw_answers_immutable
  BEFORE UPDATE OR DELETE ON raw_answers
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();

CREATE TRIGGER citations_immutable
  BEFORE UPDATE OR DELETE ON citations
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();

CREATE TRIGGER answer_citations_immutable
  BEFORE UPDATE OR DELETE ON answer_citations
  FOR EACH ROW EXECUTE FUNCTION prevent_immutable_analytics_mutation();
