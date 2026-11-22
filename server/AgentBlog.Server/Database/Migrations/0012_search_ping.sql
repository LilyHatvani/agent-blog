-- 0012 — telling search engines and feed readers (docs/specs/social.md § Found): IndexNow and WebSub pings, sent
-- from the event log by a worker with its own cursor, the last answer of each kept per blog for the owner to see.
CREATE TABLE search_ping (
    blog_id     uuid NOT NULL REFERENCES blog (id),
    service     text NOT NULL CHECK (service IN ('indexnow', 'websub')),
    last_at     timestamptz NOT NULL,
    last_status integer,
    last_error  text CHECK (length(last_error) <= 300),
    last_urls   integer NOT NULL DEFAULT 0,
    PRIMARY KEY (blog_id, service)
);

-- Starts at the newest event, like the notifier's: nothing from before is announced.
INSERT INTO notify_cursor (name, last_event_id) SELECT 'search', coalesce(max(id), 0) FROM event;
