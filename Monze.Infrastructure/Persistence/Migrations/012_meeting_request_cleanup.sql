ALTER TABLE meeting_session
  ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();

CREATE INDEX IF NOT EXISTS meeting_requested_cleanup
  ON meeting_session (created_at, id)
  WHERE status = 'requested';
