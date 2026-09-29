-- Meeting invitations are delivered to the selected voice channel. The
-- structured content is kept in the outbox so retries preserve the channel
-- link and embed instead of rebuilding a text-only card.
ALTER TABLE outbox_delivery
  ADD COLUMN IF NOT EXISTS mention_everyone BOOLEAN NOT NULL DEFAULT FALSE,
  ADD COLUMN IF NOT EXISTS content_json TEXT NULL;

-- The repeat command was removed from the product. Keep the historical column
-- from migration 017 for checksum compatibility, but prevent old repeat rows
-- from being claimed by the new scheduler.
UPDATE meeting_schedule
SET status = 'cancelled',
    locked_until = NULL,
    lease_token = NULL,
    last_error = 'repeat schedules are no longer supported'
WHERE status IN ('active', 'running')
  AND (kind ILIKE 'repeat' OR repeat_minutes IS NOT NULL);
