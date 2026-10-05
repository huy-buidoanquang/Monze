DELETE FROM agent_event
WHERE created_at < now() - interval '1 day';

DELETE FROM inbox_event
WHERE source = 'agent'
  AND received_at < now() - interval '1 day';

DELETE FROM agent_event AS duplicate
USING agent_event AS keeper
WHERE duplicate.room_id = keeper.room_id
  AND duplicate.event_type = keeper.event_type
  AND duplicate.id > keeper.id;

CREATE UNIQUE INDEX IF NOT EXISTS agent_event_room_type_unique
  ON agent_event (room_id, event_type);
