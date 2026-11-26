# Sections: a personal part and a publication part

A **section** is a category you mark as a part of the blog — say a *personal* section for your own articles and a
*publication* section for articles about the product. Readers browse each on its own page, and search engines see each
as a proper section. The contract is in [../specs/taxonomy.md § Sections](../specs/taxonomy.md#sections).

## Marking a category as a section

A section starts as an ordinary category. In the app, **Account → Sections** lists every category: tick **A section**
on one, or add a new one there with **As a section** ticked. If you let an agent do it, it's `edit_category` over MCP at
**Full** (below Full it asks you first). A section has:

- a **title** (defaults to the category's name),
- an **intro** — Markdown, shown at the top of its page,
- an **SEO title** and a **meta description** for search engines,
- a **nav order** (lower shows first), and
- **show on home** — whether its posts stay on the home page (on by default).

Leave a field out and it stays as it was; `is_section: false` turns the section back into a plain category. Renaming a
section keeps its address, so links never break.

## What readers get

- The section appears in the **site nav**, in every theme, in the order you set.
- Its page at `/category/{slug}` shows the intro, then its posts, newest first, ten a page. The page exists as soon as
  you mark the section, even before any post is in it.
- Its **own feeds** — `/category/{slug}/feed.xml` and `/category/{slug}/atom.xml` — linked from the page, so a reader can
  subscribe to just that section.
- A place in the **sitemap**.

The **home page** lists posts from sections you keep on it, plus posts in no section. Turn *show on home* off for a
section and its posts leave the home page (they stay on the section's own page). With no section at all, the home page
is exactly as it was before.

## Putting a post in a section

A section is a category, so you put a post in it the same way as any category:

- **On the post's page**, pick it under **Section** (or **No section**). It saves at once as a new revision; the post's
  other categories and tags stay.

- **Through MCP**, `create_draft` with `categories: ["Guides"]` (the section's category name) puts the new post in that
  section; `edit_post` with `categories` replaces the post's categories, so naming the section's category moves an
  existing post into it. At Propose a name that doesn't exist yet is suggested to you, as ever; a section you already
  marked exists, so your agent uses it directly.

The post then shows on the section's page and in its feeds, and — unless you turned *show on home* off — on the home
page too.
