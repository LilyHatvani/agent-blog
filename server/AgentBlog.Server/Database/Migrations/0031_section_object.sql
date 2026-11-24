-- 0031 — a section mark is always a JSON object (docs/specs/taxonomy.md § Sections). A malformed mark (a scalar, an
-- array, a nav_order that isn't a whole number) once took every public page down with it: each page reads the sections,
-- and the read cast nav_order to int without a check. The read is tolerant now (Section.Parse, and an order that casts
-- only a number); the database refuses a mark that isn't an object at all. Rows that would break the new rule are
-- normalized first, to the defaults Section.Parse gives a non-object, so the constraint can be added.
UPDATE term SET section = jsonb_build_object('title', '', 'intro', '', 'seo_title', '', 'meta_description', '', 'nav_order', 0, 'show_on_home', true)
WHERE section IS NOT NULL AND jsonb_typeof(section) <> 'object';

ALTER TABLE term ADD CONSTRAINT term_section_object_check
    CHECK (section IS NULL OR jsonb_typeof(section) = 'object');
