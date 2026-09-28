CREATE TABLE community_event (
  id BIGSERIAL PRIMARY KEY,
  clan_id BIGINT NOT NULL,
  channel_id BIGINT NOT NULL,
  owner_id BIGINT NOT NULL,
  title TEXT NOT NULL,
  starts_at TIMESTAMPTZ NOT NULL,
  status TEXT NOT NULL DEFAULT 'open'
);

CREATE TABLE signup_entry (
  event_id BIGINT NOT NULL REFERENCES community_event(id),
  user_id BIGINT NOT NULL,
  PRIMARY KEY (event_id, user_id)
);

CREATE TABLE outbox_delivery (
  id BIGSERIAL PRIMARY KEY,
  clan_id BIGINT NOT NULL,
  channel_id BIGINT NOT NULL,
  kind TEXT NOT NULL,
  dedupe_key TEXT NOT NULL UNIQUE,
  body TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending',
  attempts INT NOT NULL DEFAULT 0,
  external_message_id BIGINT NULL,
  locked_until TIMESTAMPTZ NULL,
  due_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX outbox_due ON outbox_delivery (status, id);

CREATE TABLE meeting_session (
  id BIGSERIAL PRIMARY KEY,
  clan_id BIGINT NOT NULL,
  text_channel_id BIGINT NOT NULL,
  voice_channel_id BIGINT NULL,
  requester_id BIGINT NOT NULL,
  status TEXT NOT NULL,
  room_id TEXT NULL,
  claim_until TIMESTAMPTZ NULL,
  ended_at TIMESTAMPTZ NULL
);

CREATE UNIQUE INDEX meeting_room ON meeting_session (room_id) WHERE room_id IS NOT NULL;

CREATE TABLE voice_claim (
  clan_id BIGINT NOT NULL,
  voice_channel_id BIGINT NOT NULL,
  session_id BIGINT NOT NULL,
  expires_at TIMESTAMPTZ NOT NULL,
  PRIMARY KEY (clan_id, voice_channel_id)
);

CREATE TABLE meeting_summary (
  session_id BIGINT PRIMARY KEY REFERENCES meeting_session(id),
  summary_text TEXT NOT NULL,
  transcript TEXT NULL,
  posted BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE agent_event (
  id BIGSERIAL PRIMARY KEY,
  room_id TEXT NOT NULL,
  event_type TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE knowledge_entry (
  id BIGSERIAL PRIMARY KEY,
  clan_id BIGINT NOT NULL,
  version BIGINT NOT NULL DEFAULT 1,
  question TEXT NOT NULL,
  answer TEXT NOT NULL,
  normalized TEXT NOT NULL
);

CREATE INDEX knowledge_clan ON knowledge_entry (clan_id, normalized);
