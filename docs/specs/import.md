# Import from Echo of Dreams

Status: **M2b, slice 5, built** (2026-11-23). Brings Echo of Dreams' blog over to Agent Blog: every published post, its
pictures and its authors. Code: `Import/Import.cs`, `PostService.ImportAsync`, migration `0020_import.sql`. Owner how-to:
[guides/import.md](../guides/import.md).

## The source

EoD's public blog API, read as a signed-out reader: option A of the two offered (2026-11-23), which the owner's "do
it all" left standing. Published posts are what moves; drafts stay on EoD. A database export (option B) would add them.


- `GET /api/blog/posts?page=&pageSize=50`: the published posts, a page at a time (at most 5,000).
- `GET /api/blog/posts/{slug}`: each post's Markdown, uuid, slug, title, excerpt, cover, dates, tags, category, author.
- `GET /api/blog/categories`: the source's categories, each with its id, slug, name and parent id (`parentId`), so a
  child category can name its parent. A 404 reads as an empty list (a source without the endpoint still imports);
  a 5xx stops the run with its reason.
- `POST /api/pictures/resolve`: each gallery picture (`g://…`, `gi://…`) a signed-out reader may see, with the address
  of its best public variant. A picture only adults or its owner may see isn't admitted, so it doesn't come over.
  EoD refuses more than 100 refs in one call, so a post with more asks in chunks of at most 100.

The address is EoD's ServerAPI (`https://serverapi-staging.up.railway.app`, the default in the app). A `429` waits as
long as EoD asks (at most a minute) and tries again, up to four times. The client is guarded like the media fetch
(agent-kit's `GuardedHttp`): it connects only to public addresses on port 443, checked at connect time, and follows no
redirects — a source inside the server's own network, or one that redirects there, is refused before any answer.

## What comes over

- **Posts**, oldest first: live, with the **same uuid** as their id (EoD's comments are keyed by it, so they stay
  attached through the widget, eod-439), the same slug, the original publication date, the excerpt (or the SEO
  description), tags, and the category (made as terms when new).
- **Term addresses:** a category is made at the **source's own slug** when that is an address here (else its name's
  slug); a tag under its name's slug. EoD's term addresses — `/category/{slug}`, `/tag/{the name as written}` — get
  `old_address` rows whenever they differ from the address here (stored percent-escaped; the old-address lookup tries
  the escaped form of the decoded request path, so `/tag/World%20Building` moves too). A tag whose name has nothing
  that survives into an address (no A–Z/0–9), one whose address another tag on the post already has (a duplicate,
  logged as such), or one over the post's cap, is **left off and logged**, not a failure of the whole post.
- **Category parents:** the source's categories carry a parent (`parentId`, from `GET /api/blog/categories`). Agent
  Blog's categories are **flat** — the `term` table has no parent column — so the hierarchy is kept as a **description
  note**: a child category's description names its parent ("Under “Travel” on the source blog."), filling an empty
  description only, never overwriting one the owner wrote. The parent itself comes over as an ordinary category. When
  categories here gain parents, the import can keep the hierarchy as a link instead.
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
- **The owner's map ("this author is me"):** when starting the import, the owner can name EoD authors who are
  accounts already here (`author_map`: the author's slug or display name → the account's id on this blog; the slug
  is tried first, then the display name, so a map keyed by name still lands an author who has a slug). Their
  posts land on that account — the owner's own posts on the owner, not on a new writer nobody can claim — and their
  EoD author address gets an `old_address` row to their author page here. The mapping is remembered for later runs.
- **The source's feed id:** an imported post keeps the source's Atom/RSS entry id — the `feedId` field the source gives,
  when it is an absolute http(s) URI of at most 2,000 characters with no XML-invalid characters — stored on the post
  (`source_feed_id`) and used as its entry id in this blog's RSS and Atom ([public-site.md](public-site.md)), so a feed
  reader doesn't show the post again after a move. No field, or one that isn't a usable IRI, stores NULL: the post keeps
  this blog's own absolute address as its id, exactly like a post written here.
- **Old addresses:** EoD's `/{slug}` moves to `/posts/{slug}` by itself ([old-addresses.md](old-addresses.md)). A slug
  taken here (the imported post gets `-2`), or an author address taken here, gets an `old_address` row. A slug that
  names one of the site's own top-level pages (`themes`, …) **keeps its slug**: the real route answers when it is a
  page (the themes page with the picker on), and where it is not (the picker off), the address falls through to the
  old-address rule and moves to the post — renaming it would only cost the post its address.

## Running it

- **Account → Import** (owner only): EoD's address and where profiles live, optionally which source authors are accounts
  already here (the author map: each source author's slug or name → a member of this blog), then **Import**. The server
  runs it in the background; the screen shows the counts and a log (what was left out, and why) while it runs.
- **One at a time** per blog. **Running it again** skips every post already here **on this blog** (by its uuid) without
  asking EoD for it again, and brings in only what's new; the same uuid on another blog of the instance is not "already
  here" (a post lives on one blog only, and that post fails with the reason). Both checks run **before** anything is
  fetched or made for the post, so a refused uuid creates no writer, picture or author row; the in-transaction check
  remains as a backstop. Progress and the heartbeat are saved after every post **and, while pictures copy, whenever the last save is more than
a minute old** — on every outcome, copied, missing or failed — so a long
  post's copy never looks stale. An import interrupted by a restart is taken up again after five minutes. By default,
  edits to a post already imported are **not** re-synced: freeze authoring on the source before the final run, or use
  the update mode below.
- **Update changed posts** (`update_changed`, a checkbox on the import screen): a run that, as well as bringing what's
  new, updates a post already imported **from the same source** when the source's `updatedAt` is newer than the copy
  here. The source's `updatedAt` is read with its offset (a `DateTimeOffset`, so a non-UTC time keeps its instant) and
  compared and stored at the precision the database keeps — whole microseconds, truncated: a 7-digit source time never
  looks newer than its own copy, so an unchanged post churns no revisions and copies no pictures again. The update is a **new revision** (the history stays), written by the post's author, with a `post_import_updated`
  event that announcing, email, notifications and search pings walk past — an update is no more news than the import.
  A post someone **edited here after the import** is never overwritten: it is skipped and named in the log. A post the
  owner **trashed** is skipped the same way — a source edit never lands on a post in the trash; restoring it lets a later
  run bring the change over. An unchanged post is left alone. The post remembers its source (`import_source`), the source's `updatedAt` at the last
  import or update (`import_updated_at`), and the revision the import last wrote (`import_revision_id`); a current
  revision beyond that one means a local edit. A post imported before these were tracked is left as it is.
- A failure (EoD down, a bad address) stops the job with the reason; fixed, run it again.

## Claiming

An imported author hasn't signed in: **People** shows them as "Imported: hasn't signed in yet" with **Invite to
claim**. That link is an ordinary invitation (once, 7 days) bound to their account: whoever signs in from it becomes
that account, with every post already under their name, and their sessions, agents and author page from then on. A
claimed account can't be claimed again, unless a move to a new address strands it (passkeys only, made for the old
address; [address-move.md](address-move.md#claiming-a-stranded-account)). Someone who already has an account here (another blog on a hosted instance)
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
- [x] An imported post keeps the source's feed id in RSS and Atom when it is a usable IRI (absolute http(s), at most
      2,000 characters, no XML-invalid characters); a post written here — or an import with no usable source id — keeps
      its own absolute address as its id.
- [x] Public gallery pictures are copied and relinked; others become their description, named in the log.
- [x] Each author becomes one writer at their EoD address, with their EoD profile; an authorless post is the owner's.
- [x] An author the owner maps to an account already here writes under that account (the owner's own posts on the
      owner); their EoD author address moves, and later runs remember the mapping. The PWA's import screen has the
      `author_map` field: a list of source author (slug or name) → a member of this blog, built from the members list.
      A row that names an author but no account blocks the submit ("Choose an account for <key>") instead of being
      dropped without a word.
- [x] Running it again brings nothing twice and asks EoD only about what's new.
- [x] "Update changed posts" brings a source edit as a new revision (history stays); an unchanged post is left alone; a
      post edited here after the import, or one the owner trashed, is skipped and named in the log; nothing about an
      update is announced or sent.
- [x] An invitation to claim binds the sign-in to the imported account; a claimed account can't be claimed again.

**Invariants:**
- [x] Nothing imported is announced, emailed or notified; its history starts at the import.
- [x] Only the owner imports, from an https address, one import at a time.
- [x] The import client connects only to public addresses on 443 and follows no redirects; a source inside the
      network is refused before connecting, and the job's log carries no internal answer.

**Edges:**
- [x] EoD failing stops the job with the reason; it runs again once fixed.
- [x] A categories endpoint that answers 404 reads as an empty list and the run still finishes done; a 5xx there stops
      the job with its reason.
