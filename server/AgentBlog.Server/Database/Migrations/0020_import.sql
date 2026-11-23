-- M2b slice 5 (docs/specs/import.md): bringing a blog over from Echo of Dreams. An import is a job the owner starts and
-- the server runs in the background, resumable: running it again skips what is already here.

CREATE TABLE import_job (
    id           uuid PRIMARY KEY,
    blog_id      uuid NOT NULL REFERENCES blog (id),
    source       text NOT NULL CHECK (source IN ('eod')),
    source_url   text NOT NULL CHECK (source_url ~ '^https://' AND length(source_url) <= 300),
    profile_base text CHECK (profile_base ~ '^https://' AND length(profile_base) <= 300),
    state        text NOT NULL DEFAULT 'queued' CHECK (state IN ('queued', 'running', 'done', 'failed')),
    counts       jsonb NOT NULL DEFAULT '{}',
    log          jsonb NOT NULL DEFAULT '[]',
    error        text,
    created_by   uuid NOT NULL REFERENCES account (id),
    created_at   timestamptz NOT NULL,
    started_at   timestamptz,
    heartbeat_at timestamptz,
    finished_at  timestamptz
);
CREATE INDEX import_job_blog ON import_job (blog_id, created_at DESC);

-- Who an imported author is here: one account per author of the source, made once, so a second run (or a resumed one)
-- finds the same person and every post keeps one name.
CREATE TABLE import_author (
    blog_id    uuid NOT NULL REFERENCES blog (id),
    source     text NOT NULL,
    source_key text NOT NULL CHECK (length(source_key) BETWEEN 1 AND 200),
    account_id uuid NOT NULL REFERENCES account (id),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (blog_id, source, source_key)
);

-- An invitation can be for an account that already exists here (an imported author): accepting it binds the sign-in to
-- that account, so the posts already under their name stay theirs.
ALTER TABLE invitation ADD COLUMN account_id uuid REFERENCES account (id);
