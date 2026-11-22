-- 0009 — notifications (docs/specs/notifications.md, research/2026-11-22-integrations.md): the owner's channels, their
-- sealed secrets, the phones that turned push on, and one delivery row per event per channel (per device), sent and
-- retried by a worker. The event log is the only source: a cursor walks it.

CREATE TABLE channel (
    id              uuid PRIMARY KEY,
    blog_id         uuid NOT NULL REFERENCES blog (id),
    kind            text NOT NULL CHECK (kind IN ('webpush', 'discord', 'slack', 'ntfy', 'webhook')),
    label           text NOT NULL CHECK (length(label) BETWEEN 1 AND 100),
    events          text[] NOT NULL CHECK (cardinality(events) BETWEEN 1 AND 20),
    detail          text NOT NULL DEFAULT 'full' CHECK (detail IN ('full', 'minimal')),
    enabled         boolean NOT NULL DEFAULT true,
    failing_since   timestamptz,
    last_error      text CHECK (length(last_error) <= 500),
    disabled_reason text CHECK (length(disabled_reason) <= 500),
    removed_at      timestamptz,
    created_by      uuid NOT NULL REFERENCES account (id),
    created_at      timestamptz NOT NULL,
    UNIQUE (blog_id, id)
);
-- One Web Push channel per blog: every phone that turned push on is a device under it.
CREATE UNIQUE INDEX channel_one_webpush ON channel (blog_id) WHERE kind = 'webpush' AND removed_at IS NULL;

-- A channel's secrets (a webhook URL, a token, a signing secret), sealed with AES-256-GCM under the master key; the
-- associated data names the row, so a value copied to another row doesn't open. Never returned by any API. Removing a
-- channel deletes its secrets: what can be used to post somewhere doesn't outlive the owner's wish.
CREATE TABLE channel_secret (
    blog_id    uuid NOT NULL,
    channel_id uuid NOT NULL,
    name       text NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,39}$'),
    ciphertext bytea NOT NULL,
    nonce      bytea NOT NULL CHECK (length(nonce) = 12),
    key_id     text NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (channel_id, name),
    FOREIGN KEY (blog_id, channel_id) REFERENCES channel (blog_id, id)
);

-- Instance-wide secrets: the VAPID key pair push messages are signed with (the private half sealed, the public half in
-- `public`). Made on first use; changing it would orphan every phone that turned push on.
CREATE TABLE instance_secret (
    name       text PRIMARY KEY CHECK (name ~ '^[a-z][a-z0-9_]{0,39}$'),
    public     text,
    ciphertext bytea NOT NULL,
    nonce      bytea NOT NULL CHECK (length(nonce) = 12),
    key_id     text NOT NULL,
    created_at timestamptz NOT NULL
);

CREATE TABLE push_device (
    id         uuid PRIMARY KEY,
    blog_id    uuid NOT NULL,
    channel_id uuid NOT NULL,
    endpoint   text NOT NULL CHECK (endpoint ~ '^https://' AND length(endpoint) <= 2000),
    p256dh     bytea NOT NULL CHECK (length(p256dh) = 65),
    auth       bytea NOT NULL CHECK (length(auth) = 16),
    label      text NOT NULL DEFAULT '' CHECK (length(label) <= 100),
    created_at timestamptz NOT NULL,
    gone_at    timestamptz,
    gone_reason text CHECK (length(gone_reason) <= 300),
    UNIQUE (blog_id, id),
    FOREIGN KEY (blog_id, channel_id) REFERENCES channel (blog_id, id)
);
CREATE UNIQUE INDEX push_device_endpoint ON push_device (channel_id, endpoint) WHERE gone_at IS NULL;

-- One row per event per channel (per device for push); a test send has no event.
CREATE TABLE delivery (
    id          uuid PRIMARY KEY,
    blog_id     uuid NOT NULL,
    channel_id  uuid NOT NULL,
    device_id   uuid,
    event_id    bigint REFERENCES event (id),
    status      text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'sent', 'failed', 'dead', 'skipped')),
    attempts    integer NOT NULL DEFAULT 0,
    next_at     timestamptz NOT NULL,
    http_status integer,
    error       text CHECK (length(error) <= 500),
    created_at  timestamptz NOT NULL,
    sent_at     timestamptz,
    FOREIGN KEY (blog_id, channel_id) REFERENCES channel (blog_id, id),
    FOREIGN KEY (blog_id, device_id) REFERENCES push_device (blog_id, id)
);
CREATE UNIQUE INDEX delivery_once ON delivery (channel_id, device_id, event_id) NULLS NOT DISTINCT WHERE event_id IS NOT NULL;
CREATE INDEX delivery_due ON delivery (next_at) WHERE status IN ('pending', 'failed');
CREATE INDEX delivery_recent ON delivery (channel_id, created_at DESC);

-- Where the notifier has read the event log up to. Starts at the newest event: nothing from before is sent.
CREATE TABLE notify_cursor (
    name          text PRIMARY KEY,
    last_event_id bigint NOT NULL
);
INSERT INTO notify_cursor (name, last_event_id) SELECT 'notifier', coalesce(max(id), 0) FROM event;
