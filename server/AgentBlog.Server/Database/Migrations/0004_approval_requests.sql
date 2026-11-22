-- 0004 — approval requests (decision 0001, docs/specs/posts.md): what an agent asked for above its level, pinned to the
-- exact change and the revision it was asked against. The owner approves or declines; a request whose post moved on
-- is "stale" and never applies.

CREATE TABLE approval_request (
    id                 uuid PRIMARY KEY,
    blog_id            uuid NOT NULL REFERENCES blog (id),
    connection_id      uuid NOT NULL,
    action             text NOT NULL CHECK (action IN ('edit', 'publish', 'schedule', 'unpublish', 'trash', 'restore')),
    post_id            uuid NOT NULL,
    pinned_revision_id uuid NOT NULL,
    payload            jsonb NOT NULL DEFAULT '{}'::jsonb,
    state              text NOT NULL DEFAULT 'waiting' CHECK (state IN ('waiting', 'approved', 'declined', 'stale')),
    created_at         timestamptz NOT NULL,
    decided_at         timestamptz,
    decided_by         uuid REFERENCES account (id),
    UNIQUE (blog_id, id),
    FOREIGN KEY (blog_id, connection_id) REFERENCES connection (blog_id, id),
    FOREIGN KEY (blog_id, post_id) REFERENCES post (blog_id, id),
    FOREIGN KEY (blog_id, pinned_revision_id) REFERENCES post_revision (blog_id, id),
    CHECK ((state = 'waiting') = (decided_at IS NULL))
);

CREATE INDEX approval_request_waiting ON approval_request (blog_id, created_at) WHERE state = 'waiting';

-- Who scheduled a post is a connection or an account; when the schedule fires it is judged again (decision 0001).
CREATE INDEX post_scheduled_by ON post (scheduled_by) WHERE status = 'scheduled';
