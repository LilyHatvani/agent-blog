-- 0010 — pictures through Cloudflare (decision 0009, docs/specs/media.md § Cloudflare): a blog may connect its
-- Cloudflare zone with a purge-only token. Connected, pictures carry a long edge time, and trashing one purges it from
-- the edge at once through an outbox drained in the background (EchoOfDreams' media_purge_outbox shape).

-- Secrets that belong to a blog rather than to one of its channels: the Cloudflare purge token. Sealed like
-- channel_secret (AgentKit.Secrets, associated data naming the row); deleted when the owner disconnects.
CREATE TABLE blog_secret (
    blog_id    uuid NOT NULL REFERENCES blog (id),
    name       text NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,39}$'),
    ciphertext bytea NOT NULL,
    nonce      bytea NOT NULL CHECK (length(nonce) = 12),
    key_id     text NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (blog_id, name)
);

-- One link per blog. Disconnecting keeps the row (who connected which zone, when) and clears connected_at.
CREATE TABLE cdn_link (
    blog_id         uuid PRIMARY KEY REFERENCES blog (id),
    provider        text NOT NULL DEFAULT 'cloudflare' CHECK (provider = 'cloudflare'),
    zone_id         text NOT NULL CHECK (zone_id ~ '^[0-9a-f]{32}$'),
    edge_seconds    integer NOT NULL CHECK (edge_seconds BETWEEN 3600 AND 2592000),
    connected_by    uuid NOT NULL REFERENCES account (id),
    connected_at    timestamptz,
    disconnected_at timestamptz,
    last_purge_at   timestamptz,
    failing_since   timestamptz,
    last_error      text CHECK (length(last_error) <= 500),
    refused         boolean NOT NULL DEFAULT false
);

-- The pictures whose edge copies must go. One row per picture: trashing it again before the drainer came round moves
-- enqueued_at forward instead of adding a row. A claim is a lease (claimed_until, claim_id), never a lock held across
-- the call to Cloudflare. A row older than the blog's edge time is moot (every edge copy has expired) and is dropped.
CREATE TABLE media_purge (
    media_id      uuid PRIMARY KEY,
    blog_id       uuid NOT NULL REFERENCES blog (id),
    enqueued_at   timestamptz NOT NULL,
    claimed_until timestamptz,
    claim_id      uuid,
    attempts      integer NOT NULL DEFAULT 0,
    FOREIGN KEY (blog_id, media_id) REFERENCES media (blog_id, id)
);
CREATE INDEX media_purge_due ON media_purge (blog_id, enqueued_at);
