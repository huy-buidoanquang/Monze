ALTER TABLE meeting_session
  ADD COLUMN IF NOT EXISTS summary_locked_until TIMESTAMPTZ NULL,
  ADD COLUMN IF NOT EXISTS summary_lease_token TEXT NULL;

CREATE INDEX IF NOT EXISTS meeting_summary_lease
  ON meeting_session (summary_locked_until, summary_next_attempt_at, id)
  WHERE status = 'summary_pending' AND room_id IS NOT NULL;

UPDATE voice_claim AS claim
SET expires_at = 'infinity'::timestamptz
FROM meeting_session AS session
WHERE session.id = claim.session_id
  AND session.status = 'live';
