ALTER TABLE meeting_session
  ADD COLUMN IF NOT EXISTS direct_agent BOOLEAN NOT NULL DEFAULT FALSE,
  ADD COLUMN IF NOT EXISTS notification_channel_id BIGINT NULL,
  ADD COLUMN IF NOT EXISTS notification_message_id BIGINT NULL,
  ADD COLUMN IF NOT EXISTS source_message_id BIGINT NULL,
  ADD COLUMN IF NOT EXISTS started_at TIMESTAMPTZ NULL;

ALTER TABLE outbox_delivery
  ADD COLUMN IF NOT EXISTS meeting_session_id BIGINT NULL,
  ADD COLUMN IF NOT EXISTS reply_to_message_id BIGINT NULL;

CREATE INDEX IF NOT EXISTS meeting_agent_voice_active
  ON meeting_session (clan_id, voice_channel_id, id DESC)
  WHERE status IN ('agent_pending', 'suggested', 'live', 'summary_pending');

CREATE INDEX IF NOT EXISTS outbox_meeting_session
  ON outbox_delivery (meeting_session_id, status, id)
  WHERE meeting_session_id IS NOT NULL;
