-- The owner's "this EoD author is an account already here" map (at least "this author is me"), given when the import
-- starts: the EoD author key (their slug, else their display name) to the account's id on this blog. The importer
-- writes the post under that account instead of making a new writer, so the owner's own posts land on the owner.

ALTER TABLE import_job ADD COLUMN author_map jsonb NOT NULL DEFAULT '{}'::jsonb
    CHECK (jsonb_typeof(author_map) = 'object');
