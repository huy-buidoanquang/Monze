CREATE TABLE interaction_inbox (
  clan_id BIGINT NOT NULL,
  channel_id BIGINT NOT NULL,
  message_id BIGINT NOT NULL,
  action TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'processing'
    CHECK (status IN ('processing', 'completed', 'uncertain')),
  lease_token TEXT NOT NULL,
  locked_until TIMESTAMPTZ NOT NULL,
  received_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  completed_at TIMESTAMPTZ NULL,
  PRIMARY KEY (clan_id, channel_id, message_id, action)
);

CREATE INDEX interaction_inbox_retention
  ON interaction_inbox (completed_at)
  WHERE completed_at IS NOT NULL;

CREATE INDEX interaction_inbox_lease
  ON interaction_inbox (locked_until)
  WHERE status = 'processing';
