-- 0011 — check_post (docs/specs/check.md): what the last look at an outside link found, per blog and address, so the
-- Inbox can show a post's links without fetching anything. A result is fresh for a day; a later check replaces it.
CREATE TABLE link_check (
    blog_id    uuid NOT NULL REFERENCES blog (id),
    url        text NOT NULL CHECK (length(url) BETWEEN 1 AND 2000),
    outcome    text NOT NULL CHECK (outcome IN ('ok', 'broken', 'unknown')),
    http_status integer,
    detail     text CHECK (length(detail) <= 300),
    checked_at timestamptz NOT NULL,
    PRIMARY KEY (blog_id, url)
);
