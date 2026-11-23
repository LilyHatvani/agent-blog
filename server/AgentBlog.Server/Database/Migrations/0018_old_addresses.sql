-- M2b slice 6 (docs/specs/old-addresses.md): addresses a blog had before it moved here keep working. Each row answers a
-- GET for an exact old path with a 301 to where that page lives now. The import from Echo of Dreams (slice 5) fills it;
-- a live post's old /{slug} needs no row (the site checks that by itself).

CREATE TABLE old_address (
    blog_id    uuid NOT NULL REFERENCES blog (id),
    path       text NOT NULL CHECK (path ~ '^/[^/]' AND length(path) <= 500 AND path !~ '\s'),
    -- A path on this blog, never another site: no scheme, no protocol-relative //.
    target     text NOT NULL CHECK (target ~ '^/([^/]|$)' AND length(target) <= 500 AND target !~ '\s'),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (blog_id, path),
    CHECK (path <> target)
);
