-- M2b slice 3 (docs/specs/authors.md): every person who writes on a blog has an author page at /author/{slug}. The slug
-- is made once, when they join, and kept: removing someone keeps their row, so links to their page and their posts'
-- bylines keep working, and someone invited again comes back to the same address.

CREATE TABLE author (
    blog_id    uuid NOT NULL REFERENCES blog (id),
    account_id uuid NOT NULL REFERENCES account (id),
    slug       slug NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (blog_id, account_id),
    UNIQUE (blog_id, slug)
);

-- A name as a slug, the way post titles become one (Posts.Slugify): accents dropped, letters and digits kept, the rest
-- one hyphen; 'author' when nothing is left (a name in another script).
CREATE FUNCTION author_slug_base(name text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT coalesce(nullif(trim(BOTH '-' FROM left(trim(BOTH '-' FROM regexp_replace(
               replace(replace(replace(translate(lower(regexp_replace(normalize(coalesce(name, ''), NFD), '[̀-ͯ]', '', 'g')),
                   'łøđħıþ', 'lodhit'), 'ß', 'ss'), 'æ', 'ae'), 'œ', 'oe'),
               '[^a-z0-9]+', '-', 'g')), 80)), ''), 'author')
$$;

-- Gives the account an author row on the blog, if it has none: its name's slug, or with -2, -3… when taken.
CREATE FUNCTION author_add(b uuid, a uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    base text;
    candidate text;
    n int := 1;
BEGIN
    IF EXISTS (SELECT 1 FROM author WHERE blog_id = b AND account_id = a) THEN RETURN; END IF;
    base := author_slug_base((SELECT display_name FROM account WHERE id = a));
    candidate := base;
    WHILE EXISTS (SELECT 1 FROM author WHERE blog_id = b AND slug = candidate) LOOP
        n := n + 1;
        candidate := left(base, 90) || '-' || n;
    END LOOP;
    INSERT INTO author (blog_id, account_id, slug) VALUES (b, a, candidate);
END
$$;

CREATE FUNCTION author_on_join() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM author_add(NEW.blog_id, NEW.account_id);
    RETURN NEW;
END
$$;

CREATE TRIGGER blog_member_author AFTER INSERT ON blog_member FOR EACH ROW EXECUTE FUNCTION author_on_join();

-- Everyone already on a blog (oldest first: the first to join keeps the plain slug), then anyone who wrote on one or
-- connected an agent to it and has since been removed.
SELECT author_add(m.blog_id, m.account_id) FROM blog_member m ORDER BY m.created_at, m.account_id;
SELECT author_add(r.blog_id, substring(r.actor FROM 9)::uuid)
FROM (SELECT DISTINCT blog_id, actor FROM post_revision WHERE actor LIKE 'account:%') r;
SELECT author_add(c.blog_id, c.created_by) FROM (SELECT DISTINCT blog_id, created_by FROM connection) c;
