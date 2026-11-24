-- Keep the source's feed id (docs/specs/import.md, docs/specs/public-site.md): an imported post can carry the source's
-- Atom/RSS entry id — a field the source gives, or {source host}/{slug} — so a feed reader doesn't show the post again
-- after a move. The feeds use it as the entry id when it is present. A post written here has none and keeps its own id.
ALTER TABLE post ADD COLUMN source_feed_id text;
