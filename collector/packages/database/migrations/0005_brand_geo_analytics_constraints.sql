ALTER TABLE brand_match_rules
  DROP CONSTRAINT IF EXISTS brand_match_rules_rule_kind_check;

ALTER TABLE brand_match_rules
  ADD CONSTRAINT brand_match_rules_rule_kind_check CHECK (
    rule_kind IN ('canonical_name', 'alias', 'product_name', 'exclusion')
  );
