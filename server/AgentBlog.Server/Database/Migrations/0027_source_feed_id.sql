-- Keep the source's feed id (docs/specs/import.md, docs/specs/public-site.md): an imported post can carry the source's
-- Atom/RSS entry id — the feedId field the source gives, when it is a usable IRI — so a feed reader doesn't show the
-- post again after a move. The feeds use it as the entry id when it is present. A post written here, or an import with
-- no usable source id, has none and keeps its own id.
ALTER TABLE post ADD COLUMN source_feed_id text;
