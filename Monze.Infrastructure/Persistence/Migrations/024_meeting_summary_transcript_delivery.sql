-- Preserve the complete Agent response for later participant and action-item
-- calculations, and serialize the two summary messages as ordered outbox rows.
ALTER TABLE meeting_summary
  ADD COLUMN IF NOT EXISTS full_transcript JSONB NULL;

ALTER TABLE meeting_session
  ADD COLUMN IF NOT EXISTS meeting_title TEXT NULL,
  ADD COLUMN IF NOT EXISTS voice_channel_label TEXT NULL;

ALTER TABLE outbox_delivery
  ADD COLUMN IF NOT EXISTS depends_on_id BIGINT NULL;

CREATE INDEX IF NOT EXISTS outbox_dependency_status
  ON outbox_delivery (depends_on_id, status)
  WHERE depends_on_id IS NOT NULL;
