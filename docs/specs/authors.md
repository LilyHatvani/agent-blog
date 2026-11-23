# Authors on the page

Status: **M2b, slice 3, built** (2026-11-23). With several people writing ([members.md](members.md)), readers see who
wrote what: every name in a byline ([bylines.md](bylines.md)) links to that person's author page, and each author page
lists what they wrote. Code: `Public/PublicSite.cs` (`/author/{slug}`), `Posts/Bylines.cs`, migration `0017_authors.sql`.

## Author pages

- **Address:** `/author/{slug}`. The slug is made from the person's name once, when they join the blog, the way a post
  title becomes a slug: accents dropped, letters and digits kept (`Anna Nováková` → `anna-novakova`), `-2`, `-3` when
  the name is taken, `author` for a name in another script. It never changes, so links keep working: removing someone
  keeps their page, and someone invited again gets the same address. The owner's name on it is their **Your name on
  posts**; the slug comes from their account name.
- **What it shows:** the person's name, how many live posts they wrote and which of their agents wrote with them
  ("3 posts, written with Claude"), then those posts, newest first, ten to a page, each with its byline.
- **Which posts:** the live posts the person or one of their agents wrote, exactly as the posts' bylines say "wrote":
  approving, publishing, putting an old revision back or deciding a suggested tag writes nothing. Drafts, scheduled
  posts and the trash never show.
- **When there is none:** the page answers 404, like a slug that never existed, while the person has no live post of
  their own, and for everyone while the blog's bylines are off (readers see no names then).
- **In every theme:** the page is Paper's `author.liquid`, which every theme without its own uses, with each theme's
  styles (the same markup as a tag page). `og:type` is `profile`.

## Bylines link

- Every name in a byline is a link (`rel="author"`) to its person's author page. An agent's name links to the page of
  the person whose agent it is.
- **An agent another person connected** goes by that person's name too: "Written by Anna's Claude · approved by Lenka".
  The owner's own agents keep their plain names on the owner's blog, as before. Two people's "Claude" are never one
  name.
- Themes get the byline as `post.byline` (the sentence) and `post.byline_parts` (the same sentence in parts, each with
  its `url` or none), and `post.credits` with each name's `url`.
- schema.org `BlogPosting`: each `Person` author carries its author page as `url`.

## In the app

- **Account** shows your author page's address.
- **People** links each name to their author page.

## The author's link

Each person can give one link of their own, where else readers find them: their Echo of Dreams profile, a site.
**Account** → **Your link**; `PATCH /api/blogs/{id}/me/author` `{ "profile_url" }`, for the caller only (nobody sets
someone else's), empty to clear. An `https://` address with a host and nothing hidden in it (no `user:password@`), at
most 300 characters; the table enforces the same. It shows on the author page in every theme, and on Midnight's
creator card under each post, with `rel="me noopener"`. The import from Echo of Dreams fills it with each author's EoD
profile.

## Acceptance criteria

Behaviour, invariants and edges are checked by `AuthorsTests` (5, the theme one in every theme), each rule turned off
once to see a test fail; the use cases wait for the live pass.

**User use cases:**
- [ ] As a reader, I tap a name under a post and see everything that person wrote here.
- [ ] As a writer, I find my author page from my account and share it.

**Behaviour:**
- [x] Each name in a byline links to its person's page; an agent's links to its person's.
- [x] Another person's agent is named with its person ("Anna's Claude"); the owner's agents keep their names.
- [x] An author page lists the live posts the person and their agents wrote, and nothing else.
- [x] It renders in every theme.

**Invariants:**
- [x] A slug never changes: removal keeps it, joining again finds it.
- [x] A page and its posts' bylines agree on who wrote what.

**The author's link (`MidnightThemeTests`):**
- [x] A person sets, changes and clears their own link; it shows on their page and on Midnight's card.
- [x] Only a safe https address is kept: no http, script, credentials, spaces or bare host.
- [ ] As an author, I add my Echo of Dreams profile and readers reach it from my posts (live pass).

**Edges:**
- [x] 404 for an unknown slug, a person with nothing live, and every author while bylines are off.
- [x] Names with accents, letters that don't decompose (Ł, ß) and other scripts all get a valid slug.
