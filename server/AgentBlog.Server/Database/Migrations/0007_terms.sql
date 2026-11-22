-- 0007 — categories and tags (docs/specs/taxonomy.md). One table for both, by kind. Which terms a post carries is part
-- of each revision (term_ids), so putting an old revision back puts its terms back, and an edit that changes them is
-- reviewed and approved like any other change. Names an agent used that don't exist yet, at a level that can't create
-- terms, travel with the revision as suggestions for the owner (suggested_terms).

CREATE TABLE term (
    id          uuid PRIMARY KEY,
    blog_id     uuid NOT NULL REFERENCES blog (id),
    kind        text NOT NULL CHECK (kind IN ('category', 'tag')),
    slug        slug NOT NULL,
    name        text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    description text NOT NULL DEFAULT '' CHECK (length(description) <= 1000),
    created_by  actor NOT NULL,
    created_at  timestamptz NOT NULL,
    UNIQUE (blog_id, id),
    UNIQUE (blog_id, kind, slug)
);

ALTER TABLE post_revision ADD COLUMN term_ids uuid[] NOT NULL DEFAULT '{}';
ALTER TABLE post_revision ADD COLUMN suggested_terms jsonb NOT NULL DEFAULT '[]'::jsonb
    CHECK (jsonb_typeof(suggested_terms) = 'array' AND jsonb_array_length(suggested_terms) <= 50);
CREATE INDEX post_revision_terms ON post_revision USING gin (term_ids);

-- An array can't carry a foreign key, so the database checks it here: every term on a revision is one of the same blog's
-- terms, so no revision can point at another blog's term (the composite-key rule of 0001, for arrays).
CREATE FUNCTION check_revision_terms() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF cardinality(NEW.term_ids) > 0 AND EXISTS (
        SELECT 1 FROM unnest(NEW.term_ids) AS t(id)
        WHERE NOT EXISTS (SELECT 1 FROM term WHERE term.id = t.id AND term.blog_id = NEW.blog_id)
    ) THEN
        RAISE EXCEPTION 'post_revision.term_ids names a term that is not this blog''s' USING ERRCODE = 'foreign_key_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER post_revision_terms BEFORE INSERT ON post_revision
    FOR EACH ROW EXECUTE FUNCTION check_revision_terms();
