-- An author's own link (docs/specs/authors.md § The author's link): where else readers find them (their profile on
-- Echo of Dreams, a site of their own). Each person sets theirs; themes show it on the author page and, in Midnight,
-- on the creator card under each post. https only, an address and nothing else.

ALTER TABLE author ADD COLUMN profile_url text
    CHECK (profile_url ~ '^https://[^\s/?#@]+(/[^\s]*)?$' AND length(profile_url) <= 300);
