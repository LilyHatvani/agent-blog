-- 0014 — honest bylines (docs/specs/bylines.md): the name an agent goes by on posts, set by the owner. Until it is set,
-- the name its client gave when it connected (the label's first part, "Claude · 2026-11-22") stands. The owner's own
-- name on posts and the switch live in blog.settings ('author_name', 'bylines').
ALTER TABLE connection ADD COLUMN byline_name text CHECK (length(byline_name) BETWEEN 1 AND 60);
