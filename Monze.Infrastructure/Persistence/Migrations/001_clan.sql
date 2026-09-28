CREATE TABLE clan_registry (
  clan_id BIGINT PRIMARY KEY,
  owner_id BIGINT NOT NULL,
  inactive_reason TEXT NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE clan_settings (
  clan_id BIGINT PRIMARY KEY REFERENCES clan_registry(clan_id),
  version BIGINT NOT NULL DEFAULT 1,
  welcome_enabled BOOLEAN NOT NULL DEFAULT FALSE,
  welcome_text TEXT NULL,
  timezone TEXT NOT NULL DEFAULT 'Asia/Ho_Chi_Minh'
);

CREATE TABLE clan_admin (
  clan_id BIGINT NOT NULL REFERENCES clan_registry(clan_id),
  user_id BIGINT NOT NULL,
  PRIMARY KEY (clan_id, user_id)
);

CREATE TABLE channel_policy (
  clan_id BIGINT NOT NULL,
  channel_id BIGINT NOT NULL,
  persist_messages BOOLEAN NOT NULL DEFAULT FALSE,
  last_message_id BIGINT NULL,
  has_gap BOOLEAN NOT NULL DEFAULT FALSE,
  PRIMARY KEY (clan_id, channel_id)
);

CREATE TABLE bot_flags (
  id INT PRIMARY KEY DEFAULT 1,
  discovery_incomplete BOOLEAN NOT NULL DEFAULT FALSE,
  CONSTRAINT bot_flags_singleton CHECK (id = 1)
);

INSERT INTO bot_flags (id) VALUES (1);
