-- 0013 — announcing new posts (docs/specs/announce.md): the owner's social accounts, their sealed credentials, and one
-- announcement per post per account, which is also AgentKit.Social's ledger for it (the attempt_* columns), so a retry
-- after a lost answer never posts twice where the network can be asked.

CREATE TABLE social_account (
    id            uuid PRIMARY KEY,
    blog_id       uuid NOT NULL REFERENCES blog (id),
    network       text NOT NULL CHECK (network IN ('bluesky', 'mastodon', 'discord', 'slack', 'telegram', 'x')),
    label         text NOT NULL CHECK (length(label) BETWEEN 1 AND 100),
    -- Non-secret settings: a handle, an instance and its limits, a chat. Shown to the owner.
    settings      jsonb NOT NULL DEFAULT '{}'::jsonb,
    auto          boolean NOT NULL DEFAULT true,
    enabled       boolean NOT NULL DEFAULT true,
    last_error    text CHECK (length(last_error) <= 500),
    failing_since timestamptz,
    removed_at    timestamptz,
    created_by    uuid NOT NULL REFERENCES account (id),
    created_at    timestamptz NOT NULL,
    UNIQUE (blog_id, id)
);

-- Sealed like channel_secret; removing an account deletes them.
CREATE TABLE social_secret (
    blog_id    uuid NOT NULL,
    account_id uuid NOT NULL,
    name       text NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,39}$'),
    ciphertext bytea NOT NULL,
    nonce      bytea NOT NULL CHECK (length(nonce) = 12),
    key_id     text NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (account_id, name),
    FOREIGN KEY (blog_id, account_id) REFERENCES social_account (blog_id, id)
);

CREATE TABLE announcement (
    id                 uuid PRIMARY KEY,
    blog_id            uuid NOT NULL,
    post_id            uuid NOT NULL REFERENCES post (id),
    account_id         uuid NOT NULL,
    -- draft: text written before the post went live; waiting: for the owner's tap; scheduled: due at due_at.
    state              text NOT NULL CHECK (state IN ('draft', 'waiting', 'scheduled', 'posted', 'failed', 'unconfirmed', 'skipped', 'cancelled')),
    why_waiting        text CHECK (why_waiting IN ('agent_published', 'agent_text')),
    -- Null: the default text, built when it is sent. text_by names who wrote it (an actor).
    text               text CHECK (length(text) <= 5000),
    text_by            text,
    due_at             timestamptz,
    attempts           integer NOT NULL DEFAULT 0,
    lease_until        timestamptz,
    resend_unconfirmed boolean NOT NULL DEFAULT false,
    -- AgentKit.Social's ledger: the attempt as the publisher keeps it.
    attempt_token      text,
    attempt_started_at timestamptz,
    attempt_state      text CHECK (attempt_state IN ('pending', 'posted', 'failed')),
    attempt_channel    text,
    remote_id          text CHECK (length(remote_id) <= 500),
    remote_url         text CHECK (length(remote_url) <= 2000),
    error              text CHECK (length(error) <= 500),
    notes              text[] NOT NULL DEFAULT '{}',
    created_at         timestamptz NOT NULL,
    updated_at         timestamptz NOT NULL,
    sent_at            timestamptz,
    UNIQUE (post_id, account_id),
    FOREIGN KEY (blog_id, account_id) REFERENCES social_account (blog_id, id)
);
CREATE INDEX announcement_due ON announcement (due_at) WHERE state = 'scheduled';
CREATE INDEX announcement_post ON announcement (post_id);

-- Starts at the newest event: posts published before this never announce on their own.
INSERT INTO notify_cursor (name, last_event_id) SELECT 'announce', coalesce(max(id), 0) FROM event;
