ALTER TABLE clan_settings
  ADD COLUMN IF NOT EXISTS welcome_embed JSONB NULL;
