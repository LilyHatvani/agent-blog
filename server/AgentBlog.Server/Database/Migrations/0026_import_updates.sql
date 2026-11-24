-- Re-import updates (docs/specs/import.md § Running it again): a run in "update changed posts" mode brings a source
-- edit into a post already imported from the same source, as a new revision, unless someone edited it here first.

-- What an imported post remembers of its source: which source, the source's updatedAt at the last import or update
-- (a newer one at the source means a change to bring), and the revision the import last wrote — a current revision
-- beyond it means someone edited here after the import, so the source's change is left alone.
ALTER TABLE post ADD COLUMN import_source text;
ALTER TABLE post ADD COLUMN import_updated_at timestamptz;
ALTER TABLE post ADD COLUMN import_revision_id uuid;

-- The owner's choice for a run: bring only new posts (the default), or also update changed ones.
ALTER TABLE import_job ADD COLUMN update_changed boolean NOT NULL DEFAULT false;
