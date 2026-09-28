CREATE TABLE ai_usage (
  clan_id BIGINT NOT NULL,
  user_id BIGINT NOT NULL,
  usage_day DATE NOT NULL,
  tokens INT NOT NULL,
  PRIMARY KEY (clan_id, user_id, usage_day)
);
