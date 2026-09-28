CREATE TABLE IF NOT EXISTS inbox_event (
  source TEXT NOT NULL,
  event_key TEXT NOT NULL,
  received_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (source, event_key)
);

CREATE INDEX IF NOT EXISTS inbox_event_retention
  ON inbox_event (received_at);
