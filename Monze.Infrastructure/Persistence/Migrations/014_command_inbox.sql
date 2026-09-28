CREATE TABLE command_inbox (
  clan_id BIGINT NOT NULL,
  channel_id BIGINT NOT NULL,
  message_id BIGINT NOT NULL,
  status TEXT NOT NULL DEFAULT 'processing',
  lease_token TEXT NOT NULL,
  locked_until TIMESTAMPTZ NOT NULL,
  received_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  completed_at TIMESTAMPTZ NULL,
  PRIMARY KEY (clan_id, channel_id, message_id)
);

CREATE INDEX command_inbox_retention
  ON command_inbox (completed_at)
  WHERE completed_at IS NOT NULL;

CREATE INDEX command_inbox_lease
  ON command_inbox (locked_until)
  WHERE status = 'processing';
