-- 0028 — sections (docs/specs/taxonomy.md § Sections, public-site.md): a category the owner marks as a section of the
-- blog (a personal section, a publication section). The mark and its fields live in one jsonb column, null for every
-- term that isn't a section; only a category can be one. A section has a title, a Markdown intro, an SEO title and meta
-- description, a nav order and show_on_home. Readers browse each section on its own landing page and feeds; the home
-- page lists the sections the owner keeps on it, plus posts in no section.
ALTER TABLE term ADD COLUMN section jsonb
    CHECK (section IS NULL OR kind = 'category');

-- An agent below Full asks before editing a category (docs/specs/tools.md: edit_category at Full), as it asks before
-- trashing a picture or changing the blog's details. The request's pinned state is the category as the agent saw it.
ALTER TABLE blog_request DROP CONSTRAINT blog_request_action_check;
ALTER TABLE blog_request ADD CONSTRAINT blog_request_action_check
    CHECK (action IN ('trash_media', 'edit_settings', 'edit_category'));
