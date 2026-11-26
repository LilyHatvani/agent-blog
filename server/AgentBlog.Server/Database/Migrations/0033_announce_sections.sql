-- 0033 — an announcing account can be kept to some of the blog's sections (docs/specs/announce.md § Sections).
-- section_ids: the sections (term ids, categories marked as sections) whose new posts this account announces; empty is
-- every post, as before. by_owner: the owner tapped Send (or Send again) for this announcement, so the account's
-- sections don't hold it back: only what goes out on its own is judged against them.
ALTER TABLE social_account ADD COLUMN IF NOT EXISTS section_ids uuid[] NOT NULL DEFAULT '{}';
ALTER TABLE announcement ADD COLUMN IF NOT EXISTS by_owner boolean NOT NULL DEFAULT false;
