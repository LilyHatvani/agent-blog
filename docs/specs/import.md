# Import from Echo of Dreams

Status: **M2b, slice 5, built** (2026-11-23). Brings Echo of Dreams' blog over to Agent Blog: every published post, its
pictures and its authors. Code: `Import/Import.cs`, `PostService.ImportAsync`, migration `0020_import.sql`. Owner how-to:
[guides/import.md](../guides/import.md).

## The source

EoD's public blog API, read as a signed-out reader: option A of the two offered (2026-11-23), which the owner's "do
it all" left standing. Published posts are what moves; drafts stay on EoD. A database export (option B) would add them.


- `GET /api/blog/posts?page=&pageSize=50`: the published posts, a page at a time (at most 5,000).
- `GET /api/blog/posts/{slug}`: each post's Markdown, uuid, slug, title, excerpt, cover, dates, tags, category, author.
- `POST /api/pictures/resolve`: each gallery picture (`g://…`, `gi://…`) a signed-out reader may see, with the address
  of its best public variant. A picture only adults or its owner may see isn't admitted, so it doesn't come over.

The address is EoD's ServerAPI (`https://serverapi-staging.up.railway.app`, the default in the app). A `429` waits as
long as EoD asks (at most a minute) and tries again, up to four times.

## What comes over

- **Posts**, oldest first: live, with the **same uuid** as their id (EoD's comments are keyed by it, so they stay
  attached through the widget, eod-439), the same slug, the original publication date, the excerpt (or the SEO
  description), tags, and the category (made as terms when new).
- **History starts at the import:** one revision, written by the post's author, and a `post_imported` event. Bylines
  count it as the publication ("By Anna"); announcing, subscriber email, notifications and search pings don't react to
  it, so nothing old is sent anywhere as new. A post imported and later taken down and put back isn't announced either.
- **Pictures:** each admitted gallery picture is copied into this blog's bucket (the same guarded fetch as
  `upload_media_from_url`), and its link rewritten to the copy, title kept. The cover, when the body doesn't show it,
  goes first in the body, so it's the post's picture on shares. A picture that isn't admitted, or fails to copy,
  becomes its description in italics, and the log names it.
- **Authors:** each EoD author becomes a **writer** here, once (found again on every later run), under their EoD
  display name, at **their EoD author address** (`/author/{eod slug}`) when it's free here, with their EoD profile
  (`{profiles}/creator/{slug}`) as their link. A post with no author is the owner's.
- **Old addresses:** EoD's `/{slug}` moves to `/posts/{slug}` by itself ([old-addresses.md](old-addresses.md)). A slug
  taken here (the imported post gets `-2`), or an author address taken here, gets an `old_address` row.

## Running it

- **Account → Import** (owner only): EoD's address and where profiles live, then **Import**. The server runs it in the
  background; the screen shows the counts and a log (what was left out, and why) while it runs.
- **One at a time** per blog. **Running it again** skips every post already here (by its uuid) without asking EoD for
  it again, and brings in only what's new. An import interrupted by a restart is taken up again after five minutes.
- A failure (EoD down, a bad address) stops the job with the reason; fixed, run it again.

## Claiming

An imported author hasn't signed in: **People** shows them as "Imported: hasn't signed in yet" with **Invite to
claim**. That link is an ordinary invitation (once, 7 days) bound to their account: whoever signs in from it becomes
that account, with every post already under their name, and their sessions, agents and author page from then on. A
claimed account can't be claimed again. Someone who already has an account here (another blog on a hosted instance)
joins as themselves instead.

## Acceptance criteria

Checked by `ImportTests` (5), each rule turned off once to see a test fail; the use cases wait for the live pass on
EoD's instance.

**User use cases:**
- [ ] As the owner, I import EoD's blog in one go and read it here, pictures and all.
- [ ] As an EoD author, I sign in from the link and find my posts under my name.
- [ ] As a reader, an old EoD link lands on the same post.

**Behaviour:**
- [x] Posts keep their uuid, slug, date, excerpt, tags and category; a taken slug moves aside with an old address.
- [x] Public gallery pictures are copied and relinked; others become their description, named in the log.
- [x] Each author becomes one writer at their EoD address, with their EoD profile; an authorless post is the owner's.
- [x] Running it again brings nothing twice and asks EoD only about what's new.
- [x] An invitation to claim binds the sign-in to the imported account; a claimed account can't be claimed again.

**Invariants:**
- [x] Nothing imported is announced, emailed or notified; its history starts at the import.
- [x] Only the owner imports, from an https address, one import at a time.

**Edges:**
- [x] EoD failing stops the job with the reason; it runs again once fixed.
