-- 0001 — accounts, blogs, connections, posts with revisions, events (docs/specs/data-model.md).
-- Every blog-scoped table has UNIQUE (blog_id, id), and references between blog-scoped rows are composite foreign keys,
-- so the database itself refuses a row of one blog pointing at another blog's row.
-- Enums are text + CHECK: an addition is one ALTER. Runs once, in one transaction (Database/Migrator.cs).

-- An actor: account:<uuid>, connection:<uuid>, or system.
CREATE DOMAIN actor AS text
    CHECK (VALUE ~ '^(account|connection):[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' OR VALUE = 'system');

CREATE DOMAIN slug AS text
    CHECK (VALUE ~ '^[a-z0-9](?:[a-z0-9-]{0,98}[a-z0-9])?$');

-- ---------------------------------------------------------------------------------------------
-- People
CREATE TABLE account (
    id           uuid PRIMARY KEY,
    display_name text NOT NULL CHECK (length(display_name) BETWEEN 1 AND 100),
    created_at   timestamptz NOT NULL
);

CREATE TABLE account_identity (
    provider            text NOT NULL CHECK (provider IN ('google', 'discord')),
    provider_account_id text NOT NULL CHECK (length(provider_account_id) BETWEEN 1 AND 200),
    account_id          uuid NOT NULL REFERENCES account (id),
    created_at          timestamptz NOT NULL,
    PRIMARY KEY (provider, provider_account_id)
);

-- ---------------------------------------------------------------------------------------------
-- Blogs
CREATE TABLE blog (
    id          uuid PRIMARY KEY,
    slug        slug NOT NULL UNIQUE,
    title       text NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    description text NOT NULL DEFAULT '' CHECK (length(description) <= 1000),
    settings    jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at  timestamptz NOT NULL,
    hidden_at   timestamptz
);

CREATE TABLE blog_member (
    blog_id    uuid NOT NULL REFERENCES blog (id),
    account_id uuid NOT NULL REFERENCES account (id),
    role       text NOT NULL DEFAULT 'owner' CHECK (role IN ('owner')),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (blog_id, account_id)
);

-- ---------------------------------------------------------------------------------------------
-- Agent connections (decision 0001)
CREATE TABLE connection (
    id                     uuid PRIMARY KEY,
    blog_id                uuid NOT NULL REFERENCES blog (id),
    label                  text NOT NULL CHECK (length(label) BETWEEN 1 AND 100),
    level                  text NOT NULL DEFAULT 'propose' CHECK (level IN ('propose', 'publish', 'full')),
    oauth_authorization_id text UNIQUE,
    created_by             uuid NOT NULL REFERENCES account (id),
    created_at             timestamptz NOT NULL,
    last_used_at           timestamptz,
    revoked_at             timestamptz,
    UNIQUE (blog_id, id)
);

-- ---------------------------------------------------------------------------------------------
-- Posts and revisions
CREATE TABLE post (
    id                  uuid PRIMARY KEY,
    blog_id             uuid NOT NULL REFERENCES blog (id),
    slug                slug NOT NULL,
    status              text NOT NULL DEFAULT 'draft'
                        CHECK (status IN ('draft', 'review', 'scheduled', 'live', 'trash')),
    current_revision_id uuid,
    publish_at          timestamptz,
    scheduled_by        actor,
    published_at        timestamptz,
    trashed_at          timestamptz,
    trashed_by          actor,
    status_before_trash text CHECK (status_before_trash IN ('draft', 'review', 'scheduled', 'live')),
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL,
    UNIQUE (blog_id, id),
    UNIQUE (blog_id, slug),
    -- a schedule has a time and someone who set it; nothing else has either
    CHECK ((status = 'scheduled') = (publish_at IS NOT NULL AND scheduled_by IS NOT NULL)),
    -- the trash records when, by whom and what to restore to; nothing else does
    CHECK ((status = 'trash') = (trashed_at IS NOT NULL AND trashed_by IS NOT NULL AND status_before_trash IS NOT NULL)),
    CHECK (status <> 'live' OR published_at IS NOT NULL)
);

CREATE TABLE post_revision (
    id         uuid PRIMARY KEY,
    blog_id    uuid NOT NULL,
    post_id    uuid NOT NULL,
    number     integer NOT NULL CHECK (number >= 1),
    title      text NOT NULL CHECK (length(title) BETWEEN 1 AND 300),
    body_md    text NOT NULL CHECK (length(body_md) <= 500000),
    excerpt    text NOT NULL DEFAULT '' CHECK (length(excerpt) <= 1000),
    actor      actor NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (blog_id, id),
    UNIQUE (post_id, number),
    FOREIGN KEY (blog_id, post_id) REFERENCES post (blog_id, id)
);

-- The post's current revision is one of its own (and so of its own blog).
ALTER TABLE post
    ADD CONSTRAINT post_current_revision_fk
    FOREIGN KEY (blog_id, current_revision_id) REFERENCES post_revision (blog_id, id)
    DEFERRABLE INITIALLY DEFERRED;

CREATE INDEX post_blog_status ON post (blog_id, status, updated_at DESC);
CREATE INDEX post_due ON post (publish_at) WHERE status = 'scheduled';

-- ---------------------------------------------------------------------------------------------
-- Events: append-only (CLAUDE.md). The app role has no UPDATE or DELETE on it (Migrator grants), and the triggers below
-- refuse them for every role, the owner's included, so neither the app nor a migration rewrites history by accident.
CREATE TABLE event (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    blog_id     uuid REFERENCES blog (id),
    target_type text NOT NULL CHECK (target_type ~ '^[a-z_]{1,40}$'),
    target_id   uuid NOT NULL,
    kind        text NOT NULL CHECK (kind ~ '^[a-z_]{1,60}$'),
    actor       actor NOT NULL,
    payload     jsonb NOT NULL DEFAULT '{}'::jsonb,
    at          timestamptz NOT NULL
);

CREATE INDEX event_blog ON event (blog_id, id);
CREATE INDEX event_target ON event (target_type, target_id, id);

CREATE FUNCTION refuse_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END $$;

CREATE TRIGGER event_append_only BEFORE UPDATE OR DELETE OR TRUNCATE ON event
    FOR EACH STATEMENT EXECUTE FUNCTION refuse_change();
-- Revisions are never edited. They are removed only with their post, when the owner empties the trash: that function
-- (a later slice) sets agentblog.purging for its transaction, writes its event first, then deletes.
CREATE FUNCTION refuse_revision_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' AND current_setting('agentblog.purging', true) = 'on' THEN
        RETURN NULL;
    END IF;
    RAISE EXCEPTION 'post_revision is append-only' USING ERRCODE = 'insufficient_privilege';
END $$;

CREATE TRIGGER post_revision_append_only BEFORE UPDATE OR DELETE OR TRUNCATE ON post_revision
    FOR EACH STATEMENT EXECUTE FUNCTION refuse_revision_change();
