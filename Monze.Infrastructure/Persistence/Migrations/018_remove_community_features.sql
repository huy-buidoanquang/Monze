-- Monze no longer exposes community events, FAQ, activity points,
-- games, or topic prompts. Existing migrations remain immutable for
-- checksum validation; this migration removes their obsolete schema.

DELETE FROM role_rule
WHERE rule_kind IN ('min_points', 'points');

DELETE FROM outbox_delivery
WHERE kind IN ('Reminder', 'EventPost', 'CommandReply')
   OR dedupe_key LIKE 'announce:%';

DROP INDEX IF EXISTS topic_prompt_clan_text;
DROP INDEX IF EXISTS topic_prompt_rotation;
DROP INDEX IF EXISTS knowledge_clan;
DROP INDEX IF EXISTS knowledge_search;
DROP INDEX IF EXISTS balance_rank;
DROP INDEX IF EXISTS wheel_cooldown_expiry;
DROP INDEX IF EXISTS signup_event_status_order;

DROP TABLE IF EXISTS signup_entry;
DROP TABLE IF EXISTS community_event;
DROP TABLE IF EXISTS knowledge_entry;
DROP TABLE IF EXISTS activity_ledger;
DROP TABLE IF EXISTS activity_balance;
DROP TABLE IF EXISTS game_attempt;
DROP TABLE IF EXISTS wheel_cooldown;
DROP TABLE IF EXISTS topic_prompt;
