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
at most 20 seconds — the whole fetch, headers and body together: a server that answers headers and then drips the body
fails at the deadline instead of holding the fetch open — following up to 3 redirects. What arrives must be one of the
four types, whatever the response claims. The fetch can't be turned against the network the server sits on:

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
304), `Cache-Control: public, max-age=3600`, so a picture the owner trashes stops being served within the hour, and
`Cache-Tag: media:{id},blog-media:{blog}` for Cloudflare (below). A picture that isn't served answers 404 with
`Cache-Control: no-store`, so restoring it shows it again at once.

## Cloudflare

Status: **M2, slice 4** (2026-11-22, decision [0009](../decisions/0009-media-through-cloudflare.md)). Optional. With the
blog's domain proxied by Cloudflare, every picture is cached at its edge for the hour browsers keep it. **Connecting**
the blog's zone (Media → Cloudflare: the zone id and an API token with only **Zone → Cache Purge** on that zone) does
two things:

- **A long edge time.** Pictures carry `Cloudflare-CDN-Cache-Control: max-age=…` (1, 7 or 30 days; 7 by default).
  Only Cloudflare reads it; browsers and any other cache keep the hour. A picture's bytes at its id never change, so
  readers stop costing bucket egress after the first view per edge.
- **Trash purges at once.** Trashing a picture queues its tag (`media_purge`, in the trash's own transaction). The
  scheduler's pass (every 30 s) sends at most 100 tags per blog in one call to
  `POST /zones/{zone}/purge_cache`, which is well inside Cloudflare's purge limit, and the rows go. A purge older than
  the edge time is moot and dropped. Claims are leases, so two replicas never send the same row.

When it doesn't work:

- **Not now** (429, 5xx, no answer, a 200 without `success`): freed for the next pass; Media shows "Purging is failing".
- **Refused** (401, 403, 404, another 4xx: the token was revoked or lost its permission): the link is marked refused,
  the owner is told once (`cdn_purge_refused`, a notification event, on by default), new copies get only the hour, and
  the waiting purges stay until the token works again or they're moot. The first purge that works clears it all.
- **Connecting tries the token first** with a real purge of a tag nothing carries, so a token that can't purge this
  zone is refused at once with Cloudflare's reason. It is sealed (AgentKit.Secrets, `Blog__Secrets__MasterKey`) and
  never returned.
- **Disconnecting** purges every picture of the blog first (its `blog-media:{blog}` tag), then deletes the token. A
  purge that fails still disconnects, and the answer says how long pictures may stay at the edge.

Not built (0009 § 3): a worker reading R2 directly. The origin still serves each picture once per edge.
The Markdown an agent embeds is root-relative (`![alt](/media/…)`), so it survives a domain change; the feeds make
such addresses absolute, since a feed reader has no page to resolve them against.

## Who does what

| | Agent (any level) | Owner |
| --- | --- | --- |
| upload, from a URL, confirm, list | yes (Propose, [0001](../decisions/0001-agent-autonomy-levels.md)) | (through an agent) |
| trash, restore | **not yet**: waits for approval requests to cover media | the app's Media screen, `POST /api/media/{id}/trash\|restore` |
| purge (remove bytes for good) | never | later, with emptying the trash |

## Configuration

**Which bucket.** Any S3-compatible bucket works. **Cloudflare R2 is the recommendation**, for price: it charges no
egress, so pictures leave the bucket for free. A Railway bucket works too and stays a documented option (`url_style`
`virtual`), but its egress is billed; with it, put Cloudflare in front of the blog so readers' requests are served
from Cloudflare's cache ([0009](../decisions/0009-media-through-cloudflare.md)).

| | Cloudflare R2 (recommended) | Railway bucket | Any other S3 (MinIO, AWS) |
| --- | --- | --- | --- |
| `Blog__Bucket__endpoint` | `https://<account id>.r2.cloudflarestorage.com` | `${{bucket.ENDPOINT}}` | the store's origin |
| `Blog__Bucket__region` | `auto` | `${{bucket.REGION}}` | the store's |
| `Blog__Bucket__url_style` | `path` | `virtual` | usually `path` |
| keys | an R2 API token scoped to the one bucket (Object Read & Write) | `${{bucket.ACCESS_KEY_ID}}`, `${{bucket.SECRET_ACCESS_KEY}}` | keys for that bucket |
| egress | free | billed | depends |


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

**Cloudflare (tests, `CdnTests`, 2026-11-22):**
- [x] Every picture carries its two tags; only a connected, working blog sends the long edge time; browsers keep the hour; a 404 says no-store.
- [x] Connecting tries the token (a wrong one is refused with the reason), checks the zone id and edge time, seals the token; without a master key it says what to set.
- [x] Trash purges the picture's tag with the zone's token; restore doesn't; a blog not connected queues nothing.
- [x] 150 trashed pictures go in calls of 100 and 50.
- [x] 429 and a 200 without success are retried; a 403 is refused, told once, shortens new copies' edge time, and the first working purge clears it.
- [x] A purge older than the edge time is dropped without a call.
- [x] Disconnecting purges the blog's tag, deletes the token, stops queueing; a refused last purge still disconnects and says what stays.

**Use case (driven in the running product):**
- [ ] As an owner with the blog's domain on Cloudflare, I connect it from Media, trash a picture, and it stops loading
      at once (not after the hour).

**Invariants:**
- [x] Turning off the address check, the port check, the confirm checks, the creator check, the status check on serving
      or the quota each fails the tests (mutation runs, 2026-11-22).
- [x] Turning off the queueing on trash, the connected check when queueing, the token check on connect, the refused
      check on the edge time, the purge on disconnect, the 100-tag limit, dropping moot rows, telling a refusal once,
      deleting the token, no-store on 404, reading Cloudflare's success, or freeing a failed claim each fails the tests
      (mutation runs, 2026-11-22).
- [ ] Nothing in a post ever reaches the reader as SVG or HTML from `/media/…`.
