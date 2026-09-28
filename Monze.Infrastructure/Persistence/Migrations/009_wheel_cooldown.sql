CREATE TABLE wheel_cooldown (
  clan_id BIGINT NOT NULL,
  user_id BIGINT NOT NULL,
  next_allowed_at TIMESTAMPTZ NOT NULL,
  PRIMARY KEY (clan_id, user_id)
);

CREATE INDEX wheel_cooldown_expiry
  ON wheel_cooldown (next_allowed_at);
