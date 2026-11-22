-- 0015 — subscribe by email (docs/specs/subscribe.md): readers' addresses, sealed, with a keyed hash to find duplicates;
-- and one queue of outgoing mail (confirmations and new-post emails), sent and retried by a worker. Email is an
-- announcement account like the social ones, so a new-post email is an announcement row first.

ALTER TABLE social_account DROP CONSTRAINT IF EXISTS social_account_network_check;
ALTER TABLE social_account ADD CONSTRAINT social_account_network_check
    CHECK (network IN ('bluesky', 'mastodon', 'discord', 'slack', 'telegram', 'x', 'email'));

CREATE TABLE subscriber (
    id                   uuid PRIMARY KEY,
    blog_id              uuid NOT NULL REFERENCES blog (id),
    -- HMAC-SHA256 of the normalized address under the blog's own secret: finds a duplicate without the address.
    email_hash           bytea NOT NULL CHECK (length(email_hash) = 32),
    -- The address, sealed under the master key; NULL once unsubscribed (erased).
    email_ciphertext     bytea,
    email_nonce          bytea CHECK (length(email_nonce) = 12),
    email_key_id         text,
    state                text NOT NULL CHECK (state IN ('pending', 'confirmed', 'unsubscribed')),
    confirm_expires_at   timestamptz,
    confirm_sent_at      timestamptz,
    created_at           timestamptz NOT NULL,
    confirmed_at         timestamptz,
    unsubscribed_at      timestamptz,
    UNIQUE (blog_id, email_hash),
    UNIQUE (blog_id, id),
    CHECK (state = 'unsubscribed' OR email_ciphertext IS NOT NULL)
);
CREATE INDEX subscriber_confirmed ON subscriber (blog_id) WHERE state = 'confirmed';

CREATE TABLE mail_out (
    id              uuid PRIMARY KEY,
    blog_id         uuid NOT NULL,
    subscriber_id   uuid NOT NULL,
    kind            text NOT NULL CHECK (kind IN ('confirm', 'post')),
    announcement_id uuid REFERENCES announcement (id),
    status          text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'sent', 'failed', 'skipped')),
    attempts        integer NOT NULL DEFAULT 0,
    next_at         timestamptz NOT NULL,
    lease_until     timestamptz,
    error           text CHECK (length(error) <= 500),
    created_at      timestamptz NOT NULL,
    sent_at         timestamptz,
    FOREIGN KEY (blog_id, subscriber_id) REFERENCES subscriber (blog_id, id),
    CHECK ((kind = 'post') = (announcement_id IS NOT NULL))
);
-- A post goes to a subscriber once, whatever retries the announcement goes through.
CREATE UNIQUE INDEX mail_out_once ON mail_out (announcement_id, subscriber_id) WHERE announcement_id IS NOT NULL;
CREATE INDEX mail_out_due ON mail_out (next_at) WHERE status = 'pending';

-- What an announcement said when it went out (the email's body is read from here for every subscriber).
ALTER TABLE announcement ADD COLUMN sent_text text CHECK (length(sent_text) <= 5000);
