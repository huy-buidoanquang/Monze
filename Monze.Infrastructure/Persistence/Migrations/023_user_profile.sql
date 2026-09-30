CREATE TABLE IF NOT EXISTS clan_user_profile (
  clan_id BIGINT NOT NULL,
  user_id BIGINT NOT NULL,
  clan_nick TEXT NULL,
  display_name TEXT NULL,
  username TEXT NULL,
  avatar_url TEXT NULL,
  version BIGINT NOT NULL DEFAULT 1,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (clan_id, user_id)
);

CREATE INDEX IF NOT EXISTS ix_clan_user_profile_username
  ON clan_user_profile(clan_id, lower(username))
  WHERE username IS NOT NULL;
