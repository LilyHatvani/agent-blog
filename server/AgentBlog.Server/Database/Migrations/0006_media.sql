-- 0006 — media (docs/specs/media.md): pictures in the bucket, one row each. A row is 'pending' from the moment an
-- upload is asked for until the server has read the bytes back and checked them (length, SHA-256, that they are the
-- picture type they claim); only 'ready' media is served. Trash is a bin, as for posts: only the owner empties it.
-- An upload never confirmed is 'expired' once its slot runs out, and its bytes (if any landed) are removed: nothing
-- was ever shown, so nothing is lost.

CREATE TABLE media (
    id                uuid PRIMARY KEY,
    blog_id           uuid NOT NULL REFERENCES blog (id),
    status            text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'ready', 'expired', 'trash')),
    content_type      text NOT NULL CHECK (content_type IN ('image/png', 'image/jpeg', 'image/gif', 'image/webp')),
    size_bytes        bigint NOT NULL CHECK (size_bytes > 0),
    sha256            bytea NOT NULL CHECK (length(sha256) = 32),
    -- the last segment of the reader's address (/media/{id}/{filename}); safe in a URL and a header as it is
    filename          text NOT NULL CHECK (filename ~ '^[a-z0-9][a-z0-9-]{0,79}\.(png|jpg|gif|webp)$'),
    alt               text NOT NULL DEFAULT '' CHECK (length(alt) <= 500),
    source_url        text CHECK (length(source_url) <= 2000),
    upload_expires_at timestamptz,
    created_by        actor NOT NULL,
    created_at        timestamptz NOT NULL,
    ready_at          timestamptz,
    trashed_at        timestamptz,
    trashed_by        actor,
    UNIQUE (blog_id, id),
    CHECK ((status = 'pending') = (upload_expires_at IS NOT NULL)),
    CHECK ((status IN ('ready', 'trash')) = (ready_at IS NOT NULL)),
    CHECK ((status = 'trash') = (trashed_at IS NOT NULL AND trashed_by IS NOT NULL))
);

CREATE INDEX media_blog ON media (blog_id, status, created_at DESC);
CREATE INDEX media_pending ON media (upload_expires_at) WHERE status = 'pending';
