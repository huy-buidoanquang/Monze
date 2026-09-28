CREATE TABLE IF NOT EXISTS role_rule (
  clan_id BIGINT NOT NULL,
  role_id BIGINT NOT NULL,
  rule_kind TEXT NOT NULL,
  enabled BOOLEAN NOT NULL DEFAULT FALSE,
  version BIGINT NOT NULL DEFAULT 1,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (clan_id, role_id, rule_kind)
);

CREATE INDEX IF NOT EXISTS role_rule_enabled
  ON role_rule (clan_id, rule_kind, role_id)
  WHERE enabled;
