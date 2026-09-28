ALTER TABLE role_rule
  ADD COLUMN IF NOT EXISTS condition_value TEXT NULL;

CREATE INDEX IF NOT EXISTS role_rule_enabled_condition
  ON role_rule (rule_kind, clan_id, role_id)
  WHERE enabled;
