-- 0034 — Threads and Facebook Pages as announcing accounts (docs/specs/announce.md § Threads and Facebook), through
-- AgentKit.Social. Their credentials are sealed like the other networks'; a Threads account keeps when its token was
-- last renewed in its settings (token_refreshed_at).
ALTER TABLE social_account DROP CONSTRAINT IF EXISTS social_account_network_check;
ALTER TABLE social_account ADD CONSTRAINT social_account_network_check
    CHECK (network IN ('bluesky', 'mastodon', 'discord', 'slack', 'telegram', 'x', 'threads', 'facebook', 'email', 'patreon'));
