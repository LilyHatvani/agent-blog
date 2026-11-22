# Media

Status: **M1, slice 7** (2026-11-22). Pictures an agent adds to a blog, kept in an S3-compatible bucket and served to
readers from the blog's own address. Code: `Media/`, `Storage/` (lifted from Relay, [REUSE.md](../REUSE.md)).

## What is kept

PNG, JPEG, GIF and WebP, up to `max_bytes` each (10 MiB by default). **No SVG**: it's a document that can carry
script, and media is served from the blog's own origin. The type is decided by the file's first bytes, never by a name
or a header someone sent.

## Two ways in

**Upload (the agent has the bytes).** `upload_media` with the type, size, SHA-256 and alt text returns a slot: a
presigned `PUT` URL and the exact headers to send. The signature covers `Content-Type`, `Content-Length` and
`x-amz-checksum-sha256`, so the bucket takes only those bytes. The agent PUTs, then calls `confirm_media`. The server
reads the bytes back and checks the length, the SHA-256 and that they are the picture type declared. If anything fails,
the bytes are removed and the slot stays open for another PUT until it expires (15 minutes). Only the connection that
asked for the slot (or the owner) confirms it.

**From a URL (the picture is on the web).** `upload_media_from_url` makes the server fetch it, at most `max_bytes`, in
at most 20 seconds, following up to 3 redirects. What arrives must be one of the four types, whatever the response
claims. The fetch can't be turned against the network the server sits on:

- Only `http`/`https` on ports 80 and 443, no user name or password in the URL.
- **The check runs at connect time** (`SocketsHttpHandler.ConnectCallback`) on the addresses DNS gave for that very
  connection, and the socket connects to an address that was checked. A redirect opens a new connection through the
  same check. A name that answers with a private address (DNS rebinding, `localhost`) meets it too.
- Refused: loopback, private (10/8, 172.16/12, 192.168/16), shared (100.64/10, which is where Railway's private network
  lives), link-local (169.254/16, the cloud metadata service), multicast, reserved, documentation and benchmarking
  ranges; for IPv6 anything outside global unicast, plus 6to4, Teredo and documentation. IPv4-mapped IPv6 is judged as
  the IPv4 address. A name with any refused address in its answer is refused whole.
- No proxy: a proxy is what the check would see instead of the target.

## States

| Status | Means | Readers |
| --- | --- | --- |
| `pending` | a slot is open; bytes may or may not have landed | 404 |
| `ready` | checked, in use | served |
| `expired` | the slot ran out unconfirmed; its bytes were removed | 404 |
| `trash` | the owner trashed it; the bytes stay | 404 |

Each change writes an event: `media_upload_started`, `media_added`, `media_upload_expired` (by `system`),
`media_trashed`, `media_restored`. The scheduler's pass (every 30 s) expires slots and removes their bytes; a slot
whose bytes can't be removed yet stays pending and is tried again.

**Quota.** A blog's pictures may take `max_blog_bytes` (1 GiB by default), pending and trash included, since their
bytes are in the bucket. A refusal says how full the blog is and what raises the line. Two uploads at once queue on
the blog's row, so both can't slip under it.

## Serving

`GET /media/{id}/{filename}` on the blog's own host: only `ready` media, only under its own filename. The bytes stream
from the bucket through the server with `Content-Type` from the row, `X-Content-Type-Options: nosniff`,
`Content-Security-Policy: default-src 'none'; sandbox`, an `ETag` of the SHA-256 (a matching `If-None-Match` gets
304), and `Cache-Control: public, max-age=3600`, so a picture the owner trashes stops being served within the hour.
The Markdown an agent embeds is root-relative (`![alt](/media/…)`), so it survives a domain change; the feeds make
such addresses absolute, since a feed reader has no page to resolve them against.

## Who does what

| | Agent (any level) | Owner |
| --- | --- | --- |
| upload, from a URL, confirm, list | yes (Propose, [0001](../decisions/0001-agent-autonomy-levels.md)) | (through an agent) |
| trash, restore | **not yet**: waits for approval requests to cover media | the app's Media screen, `POST /api/media/{id}/trash\|restore` |
| purge (remove bytes for good) | never | later, with emptying the trash |

## Configuration

`Blog__Bucket__endpoint`, `__name`, `__access_key`, `__secret_key` turn media on; `__url_style` (`path`, or `virtual`
for Railway's buckets), `__region` (`us-east-1`; `auto` for R2), `__max_bytes`, `__max_blog_bytes`. Without them every
media tool answers `media_off` with what to set, and the app's Media screen says the same.

## Acceptance criteria

**Use cases (driven in the running product):**
- [ ] As a writer, Claude adds a picture to a draft through the connector, and I see it in the preview on my phone.
- [ ] As a reader, I see the picture in the live post with JavaScript off, and in a feed reader.
- [ ] As a writer, I trash a picture from Media on my phone; readers stop getting it; restore brings it back.

**Behaviour (tests, `MediaTests`, 2026-11-22):**
- [x] Upload → PUT → confirm makes a picture ready at `/media/{id}/{filename}` with its bytes, type, nosniff and an ETag that answers 304.
- [x] Confirm refuses nothing uploaded, other bytes than declared (removed), another type than declared, text named as a picture.
- [x] Refusals name the reason: SVG, too large, empty, bad hash, long alt; base64 hashes work.
- [x] Another connection can't confirm a slot it didn't ask for.
- [x] From a URL: kept with its source and a clean filename; a page, an oversized file, a 404, a non-http scheme, credentials in the URL and a relative URL are refused.
- [x] The real guarded client refuses loopback, `localhost`, metadata, private, shared, mapped and unspecified addresses and other ports, before connecting; `IsPublic` is pinned on 28 addresses.
- [x] Trash stops serving and keeps the bytes; restore serves again; no agent tool trashes media at any level.
- [x] An unconfirmed slot expires on the sweep, its bytes go, confirm then says expired.
- [x] Over the quota, the refusal names how full the blog is.
- [x] Without a bucket, the tools say `media_off` and what to set.
- [x] A picture in a live post is in its page, and absolute in RSS and Atom.

**Invariants:**
- [x] Turning off the address check, the port check, the confirm checks, the creator check, the status check on serving
      or the quota each fails the tests (mutation runs, 2026-11-22).
- [ ] Nothing in a post ever reaches the reader as SVG or HTML from `/media/…`.
