# 0009 — Pictures through Cloudflare, optional

Status: **accepted** (owner, 2026-11-22: "Railway charges for egress … it is better to use Cloudflare … we should also
give the option to connect Cloudflare"; "Echo of Dreams Media Console already has this set up")

## Problem

Every picture a reader sees goes bucket → server → reader. On Railway both legs can cost egress; a popular post with
pictures pays for every view.

## Decision

Keep the blog's own address for pictures (`/media/{id}/{filename}`) and its rules (only checked, in-use pictures are
served; trash stops serving), and make Cloudflare an **optional** layer that removes almost all of that egress:

1. **Cloudflare R2 as the bucket.** S3-compatible with no egress fee; the bucket client already speaks it
   (`Blog__Bucket__endpoint=https://<account>.r2.cloudflarestorage.com`, `region=auto`, path style). To verify on a
   real R2 bucket: that it accepts the signed `x-amz-checksum-sha256` on a presigned PUT (EoD never relied on it). If
   it doesn't, a setting turns that header off; integrity still holds, because confirm reads the bytes back and checks
   length, SHA-256 and type.
2. **Cloudflare in front of the blog, connected.** With the blog's domain proxied by Cloudflare, `/media/…` responses
   carry `Cache-Tag: media:{id}` and, when Cloudflare is connected, a long `s-maxage` (a picture's bytes at an id never
   change). Connecting means giving the blog a zone id and an API token scoped to **Zone → Cache Purge** only, kept in
   the encrypted credential store (M2 slice 3). Trashing a picture purges it at once through an outbox drained in the
   background (EoD's `media_purge_outbox` / `MediaPurgeDrainer` design: batches, a lease, retry on 429). Not connected,
   the edge time stays one hour: the stated bound for a trashed picture to disappear everywhere.
3. **A worker reading R2 directly** (EoD's full design: signed URLs, an R2 binding, zero origin hits) stays a later
   option, if origin load ever matters. It would make Cloudflare a dependency, which the core must not have.

## Lifted from EchoOfDreams

The purge outbox and drainer shape, the `media:{id}` cache tag, the stated takedown bound, the R2 settings (no presigned
POST, region `auto`). Not lifted: audiences, member sessions, the D1 containment list, the Images binding: a blog's
pictures are public once in use, or not served at all.

## Still true

Self-hosting without Cloudflare works exactly as now. Nothing is deleted by inaction: trash purges caches, the bytes stay
until the owner empties the trash.
