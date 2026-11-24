# Categories, tags and the blog's settings

Status: **M1, slice 8** (2026-11-22). Code: `Posts/Taxonomy.cs`, `Posts/Posts.cs`, `Blogs/BlogSettings.cs`,
migration `0007_terms.sql`.

## Terms

One table, `term`, for both kinds: `category` and `tag`. A term has a name (1–100 characters), a slug made from it
(accents dropped: "Čeština" → `cestina`), and an optional description. The same name twice, in any case or spacing, is
the same term. A name with no letter or digit from A–Z or 0–9 has no address and is refused with that reason, rather
than collapsing into a shared fallback.

A post has at most 5 categories and 20 tags.

### Terms ride in revisions

Which terms a post carries is part of each revision (`post_revision.term_ids`), not a side table. So:

- putting an old revision back puts its terms back;
- an edit that changes terms is a revision like any other, named after whoever made it;
- an edit that waits for the owner carries its terms, and the Inbox shows what it adds and removes.

An array can't hold a foreign key, so a trigger checks every insert: a revision can only carry its own blog's terms
(the tenant rule of [0005](../decisions/0005-hosted-and-self-hosted.md), enforced by the database).

### Who creates terms

| | Propose | Publish | Full | Owner |
| --- | --- | --- | --- | --- |
| Use existing terms on a post (`create_draft`, `edit_post`) | ✓ | ✓ | ✓ | ✓ |
| Create a term (by name on a post, or `create_category` / `create_tag`) | suggests | ✓ | ✓ | ✓ |
| Edit, merge, delete terms | | | M3 | M3 |

**Suggests:** at Propose, a name that doesn't exist yet isn't created. It travels with the revision as a suggestion
(`suggested_terms`). The agent's result lists it under `suggested_to_owner`, and the post screen in the app shows each
with **Add** (creates it and puts it on the post) and **Dismiss**. Both save a new revision by the owner.

`edit_post` replaces only the kinds it names: tags given, categories kept; neither given, both kept.

## Readers

- A post's page ends with its categories ("Filed under") and tags.
- `/category/{slug}` and `/tag/{slug}` list the live posts carrying that term, newest first, ten a page.
- A term that no live post carries has no page (404), so a draft's tags don't leak. Term pages with live posts are in
  the sitemap.

## Sections

A **section** is a category the owner marks as a section of the blog — a personal section (their own articles) and a
publication section (SEO-targeted articles about the product), each browsed on its own. The mark and its fields live in
one `term.section` jsonb column, null for every term that isn't a section; only a category can be one (the database
checks it). A section has:

- a **title** (defaults to the category's name),
- an **intro** (Markdown, rendered on its landing page),
- an **SEO title** and a **meta description** (for search engines),
- a **nav order** (lower first; ties by name), and
- **show_on_home** (bool, default true): whether its posts stay on the home page.

The owner marks and edits a category in settings (`PATCH /api/blogs/{id}/categories/{slug}`, owner only) and over MCP
with `edit_category` at **Full** ([tools.md](tools.md)); below Full it becomes an approval request pinned to the category
as the agent saw it ([requests.md](requests.md)), stale if the category changed since. A field left out stays;
`is_section: false` unmarks it. A rename keeps the slug, so addresses never move. Every change writes `term_changed`;
a save that changes nothing writes nothing.

Readers get, for each section:

- a place in the **site nav**, in every theme through the shared nav partial, in nav order;
- a **landing page** at `/category/{slug}`: the intro, then its live posts paginated. It exists while the section does,
  even before any post is live (the intro is the owner's public words). A non-section category page is unchanged;
- its **own feeds**, `/category/{slug}/feed.xml` and `/category/{slug}/atom.xml`, advertised with `<link rel="alternate">`
  on the section page;
- a place in the **sitemap**.

The **home page** lists posts from sections with `show_on_home`, plus posts in no section (a post in a section kept off
the home page is hidden from it). With no section at all the page is byte-identical to before; a test pins that.

## The blog's settings

The owner edits the blog's **title** and **description** under Account → This blog in the app
(`GET`/`PATCH /api/blogs/{id}`). Only the fields sent change; an empty title is refused; an empty description
clears it; a change writes `blog_settings_changed` with before and after, and a save that changes nothing writes
nothing. Readers see the new title at once: header, feeds, link previews, and `get_blog`.

An agent changes the title and description with `edit_blog_details`: at once at Full, an approval request below
([requests.md](requests.md)).

## Display settings (the app)

Account → Display: theme (Auto, Light, Dark) and text size (Standard, Larger), for this device. Stored in the
browser, applied before the first paint, so nothing flashes; without storage the defaults apply.

## Acceptance criteria

**Use cases:**
- [x] As an owner, I rename my blog from my phone and readers see the new name (driven in Chromium, 2026-11-22).
- [x] As an owner, I see the tags my agent suggested on a post and add or dismiss each (driven in Chromium).
- [x] As an owner, I pick dark and larger text for the app on my phone, and it stays after a reload.
- [x] As a reader, I open a tag from a post and see every live post with it.

**Behaviour (tests, `TaxonomyTests`, 7):**
- [x] At Propose new names become suggestions; `create_tag` is refused with the reason; the owner's add and dismiss each make a revision.
- [x] At Publish new names are created (event by the connection); the same name in another case is the same term; an edit replaces only the kinds it names.
- [x] Names without an address and too many terms are refused, naming why; accents stay in the name, not the slug.
- [x] Restoring an old revision brings its terms back; an edit waiting for approval shows its terms and the approval applies them.
- [x] Category and tag pages list live posts only; a term only drafts carry has no page; term pages are in the sitemap.
- [x] The database refuses a revision carrying another blog's term.
- [x] Renaming shows everywhere at once; only sent fields change; an empty title is refused; a no-op writes no event; CSRF and ownership hold.

**Invariants:** each of these, turned off, fails the tests (mutation runs, 2026-11-22): Propose can't create terms; the
cross-blog trigger; term pages only for live posts; an edit replacing only named kinds; approvals carrying terms; no
event for a no-op save.
