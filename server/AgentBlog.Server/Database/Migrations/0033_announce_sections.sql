-- 0033 — announcing by section, and Patreon. An announcing account can be kept to some of the blog's sections (docs/specs/announce.md § Sections).
-- section_ids: the sections (term ids, categories marked as sections) whose new posts this account announces; empty is
-- every post, as before. by_owner: the owner tapped Send (or Send again) for this announcement, so the account's
-- sections don't hold it back: only what goes out on its own is judged against them.
ALTER TABLE social_account ADD COLUMN IF NOT EXISTS section_ids uuid[] NOT NULL DEFAULT '{}';
ALTER TABLE announcement ADD COLUMN IF NOT EXISTS by_owner boolean NOT NULL DEFAULT false;

-- Patreon (docs/specs/announce.md § Patreon): its API has no way to post, so it is a manual account. Its announcements
-- wait for the owner (why_waiting 'manual'): they copy the words, post them on Patreon, and tap It's there.
ALTER TABLE social_account DROP CONSTRAINT IF EXISTS social_account_network_check;
ALTER TABLE social_account ADD CONSTRAINT social_account_network_check
    CHECK (network IN ('bluesky', 'mastodon', 'discord', 'slack', 'telegram', 'x', 'email', 'patreon'));
ALTER TABLE announcement DROP CONSTRAINT IF EXISTS announcement_why_waiting_check;
ALTER TABLE announcement ADD CONSTRAINT announcement_why_waiting_check
    CHECK (why_waiting IN ('agent_published', 'agent_text', 'manual'));
