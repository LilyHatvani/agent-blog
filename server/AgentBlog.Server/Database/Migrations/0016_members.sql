-- 0016 — writers by invitation (docs/specs/members.md): editors and writers beside the owner, and the owner's
-- invitations. A membership row is deleted when the owner removes someone; what they wrote keeps naming their account.

ALTER TABLE blog_member DROP CONSTRAINT IF EXISTS blog_member_role_check;
ALTER TABLE blog_member ADD CONSTRAINT blog_member_role_check CHECK (role IN ('owner', 'editor', 'writer'));
-- One owner per blog: the seam for several is a later decision (0001's single owner).
CREATE UNIQUE INDEX blog_member_one_owner ON blog_member (blog_id) WHERE role = 'owner';

CREATE TABLE invitation (
    id          uuid PRIMARY KEY,
    blog_id     uuid NOT NULL REFERENCES blog (id),
    role        text NOT NULL CHECK (role IN ('editor', 'writer')),
    -- SHA-256 of the link's token; the token itself is shown to the owner once and kept nowhere.
    token_hash  bytea NOT NULL UNIQUE CHECK (length(token_hash) = 32),
    note        text NOT NULL DEFAULT '' CHECK (length(note) <= 200),
    created_by  uuid NOT NULL REFERENCES account (id),
    created_at  timestamptz NOT NULL,
    expires_at  timestamptz NOT NULL,
    used_at     timestamptz,
    used_by     uuid REFERENCES account (id),
    withdrawn_at timestamptz
);
CREATE INDEX invitation_blog ON invitation (blog_id, created_at DESC);
