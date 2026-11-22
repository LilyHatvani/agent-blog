-- 0008 — write together (decision 0008, docs/specs/feedback.md): the brief, the owner's notes, and Ask for changes.
-- The brief and notes are the one blog text an agent follows as instruction, so only the owner's API writes them.

-- The brief: who the blog is for, its voice, what never to do, posts that show it. One per blog, the owner's.
ALTER TABLE blog ADD COLUMN brief jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(brief) = 'object');

-- A third decision on a request, and the owner's reason for any decision.
ALTER TABLE approval_request DROP CONSTRAINT approval_request_state_check;
ALTER TABLE approval_request ADD CONSTRAINT approval_request_state_check
    CHECK (state IN ('waiting', 'approved', 'declined', 'changes_requested', 'stale'));
ALTER TABLE approval_request ADD COLUMN decision_note text CHECK (length(decision_note) BETWEEN 1 AND 2000);

-- Every note the owner writes to agents: on a decision, or to all of them. Kept: a note that no longer holds is
-- retired (it stops being sent), never rewritten or removed.
CREATE TABLE owner_note (
    id          uuid PRIMARY KEY,
    blog_id     uuid NOT NULL REFERENCES blog (id),
    kind        text NOT NULL CHECK (kind IN ('general', 'approved', 'declined', 'changes_requested')),
    note        text NOT NULL CHECK (length(note) BETWEEN 1 AND 2000),
    post_id     uuid,
    request_id  uuid,
    written_by  uuid NOT NULL REFERENCES account (id),
    created_at  timestamptz NOT NULL,
    retired_at  timestamptz,
    UNIQUE (blog_id, id),
    FOREIGN KEY (blog_id, post_id) REFERENCES post (blog_id, id),
    FOREIGN KEY (blog_id, request_id) REFERENCES approval_request (blog_id, id),
    CHECK ((kind = 'general') = (request_id IS NULL))
);

CREATE INDEX owner_note_current ON owner_note (blog_id, created_at DESC) WHERE retired_at IS NULL;
CREATE INDEX owner_note_post ON owner_note (post_id, created_at DESC) WHERE post_id IS NOT NULL;

-- A note is written once. The only change it takes is being retired, once; nothing removes it.
CREATE FUNCTION owner_note_only_retire() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'owner_note is kept: retire a note instead' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.retired_at IS NOT NULL OR NEW.retired_at IS NULL
       OR (NEW.id, NEW.blog_id, NEW.kind, NEW.note, NEW.post_id, NEW.request_id, NEW.written_by, NEW.created_at)
          IS DISTINCT FROM (OLD.id, OLD.blog_id, OLD.kind, OLD.note, OLD.post_id, OLD.request_id, OLD.written_by, OLD.created_at) THEN
        RAISE EXCEPTION 'owner_note is written once; it can only be retired' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER owner_note_written_once BEFORE UPDATE OR DELETE ON owner_note
    FOR EACH ROW EXECUTE FUNCTION owner_note_only_retire();
