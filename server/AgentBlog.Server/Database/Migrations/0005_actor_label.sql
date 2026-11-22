-- 0005 — a readable name for an actor (docs/specs/data-model.md § Actors), for the owner's history and revisions:
-- a connection's label, an account's display name, or "System". Read-only, so the app role calls it freely.
CREATE FUNCTION actor_label(a actor) RETURNS text LANGUAGE sql STABLE AS $$
    SELECT CASE
        WHEN a = 'system' THEN 'System'
        WHEN a LIKE 'connection:%' THEN coalesce((SELECT label FROM connection WHERE id = substring(a FROM 12)::uuid), 'A removed agent')
        WHEN a LIKE 'account:%' THEN coalesce((SELECT display_name FROM account WHERE id = substring(a FROM 9)::uuid), 'A removed account')
    END
$$;
