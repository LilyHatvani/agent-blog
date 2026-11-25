-- "This is me" (docs/specs/import.md § This is me): an author the import made a writer, who is really someone already on
-- the blog (usually the owner), is folded into that account. Revisions and events are append-only and keep naming who
-- saved them, so the fold is this row, and every reader of a post's people (bylines, author pages, feeds, a writer's
-- own posts) reads the folded account as the one it was folded into. Folds never chain: folding an account others were
-- folded into moves theirs along.
CREATE TABLE author_fold (
    blog_id         uuid NOT NULL REFERENCES blog (id),
    account_id      uuid NOT NULL REFERENCES account (id),
    into_account_id uuid NOT NULL REFERENCES account (id),
    created_at      timestamptz NOT NULL,
    PRIMARY KEY (blog_id, account_id),
    CHECK (account_id <> into_account_id)
);
CREATE INDEX author_fold_into ON author_fold (blog_id, into_account_id);
