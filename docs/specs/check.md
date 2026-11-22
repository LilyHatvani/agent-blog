# check_post

Status: **M2, slice 5** (2026-11-22). The pre-flight an agent runs before it asks, and the owner sees on the request.
Code: `Posts/PostCheck.cs`. Deterministic: rules and counts, never a model (VISION: the server calls no AI).

## Who runs it

- **An agent**, with `check_post` (any level, read-only): on a post as saved, or on new `title`, `excerpt` or
  `body_md` it is about to save (a dry run: nothing is written). The server's instructions ask agents to run it before
  asking to publish, schedule, submit or change a live post, fix every problem, and say why for a warning they leave.
- **The owner**: every waiting edit, publish or schedule request in the Inbox carries the check of exactly what it asks
  (an unpublish, trash or restore has nothing to check; a request that no longer applies shows none) (an edit's new words over
  the post; the post as it stands for a publish or schedule), collapsed to one line. A post's screen has **Check**.
- Outside links are fetched only by `check_post` and the post screen's Check, never by the Inbox; what each answered is
  kept a day per blog and address (`link_check`), so the Inbox shows the last answers without fetching.

## What it finds

**Problem** (readers would hit it; `ready` is false while any is left):

| Check | When |
| --- | --- |
| `body_empty` | no text |
| `alt_missing` | a picture with no description |
| `media_missing` | a picture not in this blog's media, in the trash, not ready, or under another filename |
| `link_internal` | a link to `/posts/{slug}` that isn't a post here (or is in the trash) |
| `link_unsafe` | an address the blog won't keep (`javascript:`, `data:`…): readers get a dead link |
| `link_broken` | an outside link answering 404 or 410, or pointing inside the server's network (refused by the guard) |
| `placeholder` | `TODO`, `FIXME`, `TBD`, `XXX` or lorem ipsum |

**Warning** (worth a look): `title_length` (over 70), `excerpt_missing`, `excerpt_length` (over 200), `heading_h1` (the
title is the page's heading), `heading_skip`, `heading_empty`, `alt_weak` (a filename, "image", …), `alt_long` (over 250),
`link_text` ("here", "click here", none), `link_insecure` (http), `image_external` (a picture hot-linked from another
site), `raw_html` (readers see it as text: the pipeline turns HTML off; code is left alone), `link_internal` to a post
that isn't live yet, `link_unknown` (an outside link that answered something else, or nothing within 8 s),
`similar_post`.

**Note**: `body_short` (under 100 words), `no_terms`, `links_not_checked`.

**Similar posts**: the 1000 most recently changed posts (not in the trash, never itself) are compared by title (same
title, or 60% of its words) and by text (25% of three-word runs shared; texts too short to have 8 runs aren't
compared). At most three are named, most alike first.

**Outside links**: at most 25 fetched per check (5 at a time, 8 s each), through the guarded client (public addresses
only, ports 80 and 443, up to three redirects). `HEAD`, then `GET` when a site refuses `HEAD`. 2xx and 3xx are fine;
404 and 410 broken; anything else, or no answer, "couldn't confirm" (sites refuse robots or sit behind a login).

## Acceptance criteria

**Use cases:**
- [x] As an agent, I check a draft (or my next edit before saving it) and get what to fix (MCP, `CheckTests`).
- [ ] As an owner, the request card in my Inbox tells me in one line whether readers would hit anything, and why on a
      tap (driven in the app 2026-11-22 at 390 and 1366 px; to confirm on the live blog).

**Behaviour (tests, `CheckTests`, 2026-11-22):**
- [x] A clean post with pictures, headings, code holding HTML and a `<` comparison is ready with no warnings.
- [x] Missing alt, three kinds of missing media, internal links to nothing and to a draft, a broken and an unsafe link, and a TODO are problems.
- [x] A long title, no excerpt, an H1, a skipped level, weak alt, "here", http, raw HTML and a hot-linked picture are warnings.
- [x] A repeat is named, an unrelated post isn't, a post is never its own repeat, two short texts aren't repeats.
- [x] New text is checked without being saved.
- [x] Outside links are fetched only when asked, at most 25 a check, remembered a day, fetched again after.
- [x] The real guarded client counts links into the server's network as broken without connecting.
- [x] The Inbox card carries the check of what is asked (an edit's new words), fetching nothing; the owner checks any post.

**Invariants:**
- [x] No model call (the checker has no client for one).
- [x] Turning off the alt rule, the filename and status checks on media, the missing-post rule, the unsafe-link rule, the
      25-link cap, the day's freshness, fetching only when asked, excluding the post itself, the HTML rule, the 404 and
      guard outcomes, checking an edit's new words, or the short-text guard each fails the tests (mutation runs, 2026-11-22).
