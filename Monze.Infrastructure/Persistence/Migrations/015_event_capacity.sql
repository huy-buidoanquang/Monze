ALTER TABLE community_event
  ADD COLUMN IF NOT EXISTS capacity INT NULL;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'community_event_capacity_positive'
  ) THEN
    ALTER TABLE community_event
      ADD CONSTRAINT community_event_capacity_positive
      CHECK (capacity IS NULL OR capacity > 0);
  END IF;
END $$;

ALTER TABLE signup_entry
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'confirmed';

ALTER TABLE signup_entry
  ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'signup_entry_status_valid'
  ) THEN
    ALTER TABLE signup_entry
      ADD CONSTRAINT signup_entry_status_valid
      CHECK (status IN ('confirmed', 'waitlisted'));
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS signup_event_status_order
  ON signup_entry (event_id, status, created_at, user_id);
