# Old addresses

Status: **M2b, slice 6, built** (2026-11-23). A blog that moves here (Echo of Dreams' first: [research](../research/2026-11-22-eod-blog.md))
keeps its addresses: links in old posts, other sites, feed readers and search engines land on the page that lives here
now, with a `301 Moved Permanently`. Code: `Public/PublicSite.cs` (`OldAddressAsync`), migration `0018_old_addresses.sql`.

## What moves

Checked only when a public page would otherwise be "not found", so no page that exists ever moves:

1. **A recorded old path** (`old_address`: exact, or the same with a trailing slash). The import from Echo of Dreams
   (slice 5) fills it: EoD author pages (`/author/{eod-username}`, which our own author slugs don't match), anything
   renamed on the way.
2. **A live post's old `/{slug}`**, Echo of Dreams' post address, moves to `/posts/{slug}` without a row. A draft's or a
   trashed post's slug doesn't move: it answers "not found", as the post itself does.
3. **`/login`** moves to `/sign-in`.
4. **`/api/blog/feed.xml`** and **`/api/blog/sitemap.xml`**, Echo of Dreams' feed and sitemap, move to `/feed.xml` and
   `/sitemap.xml`.

Tag and category addresses (`/tag/{slug}`, `/category/{slug}`) are the same on both, and need nothing.

The query string comes along (`?utm_source=…`). A target is always a path on this blog: the table refuses a URL, a
protocol-relative `//host` and anything not starting with `/`, so no row can send readers elsewhere.

## Acceptance criteria

Checked by `OldAddressTests` (3), each rule turned off once to see a test fail.

- [x] A live post's `/{slug}` answers 301 to `/posts/{slug}`, with its query; a draft's, an unknown and a deeper path don't.
- [x] A recorded path moves, with or without its trailing slash, even where a route of ours (`/author/…`) would answer.
- [x] EoD's feed and sitemap addresses and `/login` move to theirs.
- [x] No row can point off the blog.
- [ ] As a reader with an old EoD link, I land on the same post here (live pass, after the switch).
