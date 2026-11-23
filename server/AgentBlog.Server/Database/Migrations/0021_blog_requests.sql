-- M3 (docs/specs/requests.md § Beyond posts): an agent below Full asks before trashing a picture or changing the blog's
-- title or description; the owner decides in the Inbox, as for posts. Pinned to what the agent saw: a picture no longer
-- in use, or settings changed since, make the request stale instead of applying over someone else's change.

CREATE TABLE blog_request (
    id            uuid PRIMARY KEY,
    blog_id       uuid NOT NULL REFERENCES blog (id),
    connection_id uuid NOT NULL,
    action        text NOT NULL CHECK (action IN ('trash_media', 'edit_settings')),
    media_id      uuid REFERENCES media (id),
    payload       jsonb NOT NULL DEFAULT '{}',
    pinned        jsonb NOT NULL DEFAULT '{}',
    state         text NOT NULL DEFAULT 'waiting' CHECK (state IN ('waiting', 'approved', 'declined', 'stale')),
    created_at    timestamptz NOT NULL,
    decided_at    timestamptz,
    decided_by    uuid REFERENCES account (id),
    decision_note text CHECK (length(decision_note) BETWEEN 1 AND 2000),
    UNIQUE (blog_id, id),
    FOREIGN KEY (blog_id, connection_id) REFERENCES connection (blog_id, id),
    CHECK ((action = 'trash_media') = (media_id IS NOT NULL)),
    CHECK ((state = 'waiting') = (decided_at IS NULL))
);
CREATE INDEX blog_request_waiting ON blog_request (blog_id, created_at) WHERE state = 'waiting';
