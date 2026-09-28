ALTER TABLE clan_registry
  ADD COLUMN IF NOT EXISTS inactive_reason TEXT NULL;

ALTER TABLE outbox_delivery
  ADD COLUMN IF NOT EXISTS lease_token TEXT NULL,
  ADD COLUMN IF NOT EXISTS last_error TEXT NULL;

CREATE TABLE IF NOT EXISTS role_grant (
  clan_id BIGINT NOT NULL,
  role_id BIGINT NOT NULL,
  user_id BIGINT NOT NULL,
  granted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (clan_id, role_id, user_id)
);

CREATE TABLE IF NOT EXISTS welcome_delivery (
  clan_id BIGINT NOT NULL,
  user_id BIGINT NOT NULL,
  claimed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (clan_id, user_id)
);

CREATE TABLE IF NOT EXISTS meeting_schedule (
  id BIGSERIAL PRIMARY KEY,
  clan_id BIGINT NOT NULL,
  channel_id BIGINT NOT NULL,
  requester_id BIGINT NOT NULL,
  kind TEXT NOT NULL,
  when_text TEXT NOT NULL,
  timezone TEXT NOT NULL,
  next_run_at TIMESTAMPTZ NOT NULL,
  status TEXT NOT NULL DEFAULT 'active',
  locked_until TIMESTAMPTZ NULL,
  lease_token TEXT NULL,
  last_error TEXT NULL
);

CREATE INDEX IF NOT EXISTS meeting_schedule_due
  ON meeting_schedule (status, next_run_at, id)
  WHERE status IN ('active', 'running');

CREATE INDEX IF NOT EXISTS outbox_due_ready
  ON outbox_delivery (status, due_at, id)
  WHERE status IN ('pending', 'sending');

CREATE INDEX IF NOT EXISTS outbox_lease_expiry
  ON outbox_delivery (locked_until)
  WHERE status = 'sending';

CREATE INDEX IF NOT EXISTS meeting_voice_lookup
  ON meeting_session (clan_id, voice_channel_id, status);

CREATE INDEX IF NOT EXISTS channel_policy_persist
  ON channel_policy (clan_id, channel_id)
  WHERE persist_messages;

CREATE INDEX IF NOT EXISTS topic_prompt_rotation
  ON topic_prompt (clan_id, last_used_at, id);

CREATE INDEX IF NOT EXISTS agent_event_dedupe
  ON agent_event (room_id, event_type);

CREATE INDEX IF NOT EXISTS knowledge_search
  ON knowledge_entry
  USING GIN (to_tsvector('simple', normalized));
