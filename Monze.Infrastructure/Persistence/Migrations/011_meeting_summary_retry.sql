ALTER TABLE meeting_session
  ADD COLUMN IF NOT EXISTS summary_attempts INT NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS summary_next_attempt_at TIMESTAMPTZ NULL,
  ADD COLUMN IF NOT EXISTS summary_last_error TEXT NULL;

CREATE INDEX IF NOT EXISTS meeting_summary_retry
  ON meeting_session (summary_next_attempt_at, id)
  WHERE status = 'summary_pending' AND room_id IS NOT NULL;
