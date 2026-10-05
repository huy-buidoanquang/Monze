-- Keep one durable conversation context while allowing every Agent room to
-- produce its own meeting_session and meeting_summary row.
ALTER TABLE meeting_session
  ADD COLUMN IF NOT EXISTS root_session_id BIGINT NULL
    REFERENCES meeting_session(id) ON DELETE CASCADE,
  ADD COLUMN IF NOT EXISTS context_closed_at TIMESTAMPTZ NULL;

-- Historical completed rows cannot safely be attached to a future Agent room.
-- Active requested/suggested/live rows remain open across this migration.
UPDATE meeting_session
SET context_closed_at = COALESCE(ended_at, created_at, now())
WHERE root_session_id IS NULL
  AND context_closed_at IS NULL
  AND status NOT IN ('requested', 'suggested', 'live');

CREATE INDEX IF NOT EXISTS meeting_open_context
  ON meeting_session (clan_id, voice_channel_id, id DESC)
  WHERE root_session_id IS NULL
    AND context_closed_at IS NULL
    AND voice_channel_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS meeting_context_cycles
  ON meeting_session (root_session_id, id)
  WHERE root_session_id IS NOT NULL;
