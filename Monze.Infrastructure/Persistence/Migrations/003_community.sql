CREATE TABLE activity_ledger (
  id BIGSERIAL PRIMARY KEY,
  clan_id BIGINT NOT NULL,
  user_id BIGINT NOT NULL,
  source_type TEXT NOT NULL,
  source_id TEXT NOT NULL,
  delta BIGINT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (clan_id, user_id, source_type, source_id)
);

CREATE TABLE activity_balance (
  clan_id BIGINT NOT NULL,
  user_id BIGINT NOT NULL,
  points BIGINT NOT NULL,
  awarded_today INT NOT NULL DEFAULT 0,
  award_day DATE NOT NULL DEFAULT CURRENT_DATE,
  PRIMARY KEY (clan_id, user_id)
);

CREATE INDEX balance_rank ON activity_balance (clan_id, points DESC);

CREATE TABLE game_attempt (
  id BIGSERIAL PRIMARY KEY,
  clan_id BIGINT NOT NULL,
  user_id BIGINT NOT NULL,
  prize_index INT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE topic_prompt (
  id BIGSERIAL PRIMARY KEY,
  clan_id BIGINT NOT NULL,
  text TEXT NOT NULL,
  last_used_at TIMESTAMPTZ NULL
);
