# Integrations: telling the owner, announcing posts

Researched 2026-11-22 (asked by the owner: "research integration options: like discord webhooks etc."). Facts from the
vendors' official docs as found by search that day; **unverified** marks what couldn't be confirmed. Recommendation,
not decision: the build order is folded into the [roadmap](../ROADMAP.md) M2.

## Telling the owner

| Option | Owner pastes | Limits | Cost | No extra account? | Notes |
| --- | --- | --- | --- | --- | --- |
| **Web Push to /admin** | nothing ("Turn on") | ~4 KB payload | free | yes | iOS 16.4+ only when installed to the Home Screen; action buttons don't work in Safari. Relay has a vendor-free implementation (`Relay.Server/Push/`) |
| **Discord webhook** | webhook URL (it is the secret) | 2,000 chars, 10 embeds; 429 with `retry_after` | free | Discord account | an un-owned webhook can only send link buttons, with `?with_components=true` |
| **Slack incoming webhook** | webhook URL | ~1 msg/s, 429 + Retry-After | free | Slack app | removed webhook → 404 `no_service` |
| **Telegram bot** | bot token + chat (pairing) | 4,096 chars; ~1 msg/s per chat | free | Telegram | inline buttons; webhook secret header |
| **ntfy** (ntfy.sh or own) | topic URL (+ token) | ~4 KB; ntfy.sh daily limits unverified | free | yes | on public ntfy.sh the topic name is the password; content is plaintext to the server |
| **Pushover** | user key + app token | 1,024 chars; 10,000/month per account | $4.99 one-time per platform | no | covered by ntfy and Web Push |
| **Matrix** | homeserver, room, token | — | free | Matrix | encrypted rooms need E2EE; little demand |
| **Email** (SMTP, Resend, Postmark) | SMTP login or API key | Resend free 3,000/month; Postmark free 100/month | free tiers | provider | arrives with M2 slice 7 |

## Announcing what's published

| Option | Verdict | Why |
| --- | --- | --- |
| Discord / Slack / Telegram channel | **build** | same senders as owner channels |
| **Bluesky** | **build** | app password now (OAuth later); 300 graphemes; links need facets; link card with our own thumbnail; generous limits |
| **Mastodon** | **build** | instance + `write:statuses` token; 500 chars default, URL = 23; `Idempotency-Key`; the card comes from our og tags (slice 5) |
| Threads | skip | needs the owner's own Meta business app |
| LinkedIn | skip | tokens die every 60 days without partner-only refresh |
| X | skip | pay-per-use; a post with a URL costs $0.20 (since 2026-04-20) |
| **WebSub** | **build** (tiny) | `rel="hub"` in feeds + a ping |
| **IndexNow** | **build** (tiny) | server-made key; one POST reaches Bing, Yandex, Seznam, Naver, Yep; Google doesn't take part |
| Google | — | the sitemap ping is retired; robots.txt's sitemap line is all there is |
| **Outgoing webhooks** | **build** | [Standard Webhooks](https://github.com/standard-webhooks/standard-webhooks) signing: covers Zapier, Make, n8n, the owner's own code |
| Inbound webhooks | skip | a second way in beside MCP, against "one contract" |

## Acting from a notification

Tapping a notification **opens the request in the app**: one more tap, and the owner sees the diff before deciding.
Chat buttons (Discord interactions, Telegram callbacks, Slack interactivity) are possible with the owner's identity
bound (we already store their Discord identity), but they approve without the diff in view; later, opt-in, Approve and
Decline only. **Never one-time approve links**: any leak (forwarded mail, a public ntfy topic) becomes the power to
approve.

## Recommended design: one model

The append-only `event` table is the only source. A notifier reads it with a cursor (event ids commit in order, Db.cs)
and fans each event out to the owner's **channels** as **deliveries**; a worker sends them with retries; messages come
from fixed templates, never a model.

```
channel        (blog_id, id, kind, audience owner|public, label, config jsonb, events text[], detail full|minimal,
                enabled, failing_since, last_error, created_at, disabled_at)
channel_device (blog_id, channel_id, id, endpoint, p256dh, auth, gone_at, gone_reason)        -- Web Push
credential     (blog_id, channel_id, name, ciphertext, nonce, key_id, created_at, rotated_at)  -- Relay 0024's store
delivery       (blog_id, id, channel_id, device_id?, event_id, status pending|sent|failed|dead|skipped, attempts,
                next_at, http_status, error, external_ref, created_at, sent_at, UNIQUE (channel_id, device_id, event_id))
notify_cursor  (name, last_event_id)
```

- **To the owner** by default: `approval_requested`, `post_submitted`, `schedule_dropped`, `connection_created`. **To
  the public:** `post_published`.
- **Judged when it sends**: the request still waiting, the post still live at that revision; otherwise `skipped` with
  the reason. Announcements wait a short delay (2 min) so an unpublish cancels them.
- **The owner sets the reach**: a post an agent published on its own isn't announced by default; it becomes an Inbox
  card, "Announce on Bluesky?".
- **Trust boundary**: titles are agent-written: escape per target, Discord `allowed_mentions: {parse: []}`, truncate;
  `detail: minimal` keeps draft text off third parties.
- **Retries** 1 m, 5 m, 30 m, 2 h, 12 h, honouring 429; a 401/404/410 disables the channel with the reason and an
  event; a failing channel shows a banner in the app. Never silent.
- **Outgoing requests** go through `SafeFetch` (a self-hoster may allow their LAN for a local ntfy or n8n).
- **Secrets** encrypted at rest (Relay 0024: AES-256-GCM, one master key), never returned.
- **Notifications screen**: add a channel, choose events, **Send test**, the last 20 deliveries, disable.
- **Undo an announcement**: `external_ref` keeps the Bluesky URI, Mastodon id or Discord message id, so unpublishing
  can offer "Remove the announcement".

## Build order

1. **M2 slice 3, Notifications**: the channel core (channels, deliveries, cursor, worker, credential store, screen
   with test and log) with Web Push, Discord, Slack, ntfy and signed outgoing webhooks.
2. **Slice 5, social cards**, plus IndexNow and WebSub.
3. **A new slice, Announce**: Bluesky, Mastodon, public Discord/Slack/Telegram channels, delayed and judged when sent.
4. **Slice 7, email**: one more channel kind, sharing the transport with subscribe.
5. Later if asked: Pushover, Matrix, Bluesky OAuth, chat buttons with identity bound. Skip: X, LinkedIn, Threads,
   approve-by-link, inbound webhooks.

## Risks

The hosted service as a spam relay (agent-direct announcements wait for a tap; a per-blog rate that queues); a leaked
webhook URL (stored encrypted, never shown again); draft text leaving the server (`detail: minimal`); platform drift
(small isolated formatters); iOS Web Push needing the Home Screen install (say so on the "Turn on" screen).

## Sources (searched 2026-11-22)

Discord [webhooks](https://docs.discord.com/developers/resources/webhook), [rate limits](https://docs.discord.com/developers/topics/rate-limits),
[interactions](https://docs.discord.com/developers/interactions/overview) · Slack [incoming webhooks](https://docs.slack.dev/messaging/sending-messages-using-incoming-webhooks/),
[rate limits](https://docs.slack.dev/apis/web-api/rate-limits/) · Telegram [Bot API](https://core.telegram.org/bots/api), [FAQ](https://core.telegram.org/bots/faq) ·
ntfy [publishing](https://docs.ntfy.sh/publish/) · Pushover [API](https://pushover.net/api), [pricing](https://pushover.net/pricing) ·
Web Push [RFC 8292](https://datatracker.ietf.org/doc/html/rfc8292), [RFC 8291](https://rfc-editor.org/rfc/rfc8291.html),
[Apple](https://developer.apple.com/documentation/usernotifications/sending-web-push-notifications-in-web-apps-and-browsers) ·
Bluesky [rate limits](https://docs.bsky.app/docs/advanced-guides/rate-limits), [creating a post](https://docs.bsky.app/docs/tutorials/creating-a-post) ·
Mastodon [statuses](https://docs.joinmastodon.org/methods/statuses/), [rate limits](https://docs.joinmastodon.org/api/rate-limits/) ·
X [pricing update](https://devcommunity.x.com/t/x-api-pricing-update-owned-reads-now-0-001-other-changes-effective-april-20-2026/263025) ·
[IndexNow](https://www.indexnow.org/documentation) · [WebSub](https://www.w3.org/TR/websub/) ·
[Google: sitemap ping retired](https://developers.google.com/search/blog/2023/06/sitemaps-lastmod-ping) ·
[Standard Webhooks](https://github.com/standard-webhooks/standard-webhooks) · Resend [pricing](https://resend.com/pricing), Postmark [pricing](https://postmarkapp.com/pricing).
