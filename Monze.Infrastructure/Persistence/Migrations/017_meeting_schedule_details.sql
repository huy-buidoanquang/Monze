ALTER TABLE meeting_schedule
  ADD COLUMN IF NOT EXISTS title TEXT NOT NULL DEFAULT 'Cuộc họp',
  ADD COLUMN IF NOT EXISTS repeat_minutes INT NULL;

ALTER TABLE meeting_schedule
  DROP CONSTRAINT IF EXISTS meeting_schedule_repeat_minutes_check;

ALTER TABLE meeting_schedule
  ADD CONSTRAINT meeting_schedule_repeat_minutes_check
  CHECK (repeat_minutes IS NULL OR repeat_minutes BETWEEN 1 AND 10080);

CREATE INDEX IF NOT EXISTS meeting_schedule_channel_pending
  ON meeting_schedule (clan_id, channel_id, status, next_run_at, id)
  WHERE status IN ('active', 'running');
