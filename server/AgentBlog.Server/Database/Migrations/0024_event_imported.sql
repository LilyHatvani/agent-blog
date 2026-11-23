-- 0024 — imported history (docs/specs/export.md): events that came over from another instance's export keep their kind
-- and their story (bylines, History) but were news there, not here. The workers that act on new events (notifications,
-- announcing and email, search pings) walk past them.
ALTER TABLE event ADD COLUMN imported boolean NOT NULL DEFAULT false;
