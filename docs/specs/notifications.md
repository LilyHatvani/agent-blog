# Notifications

Status: **M2, slice 3** (2026-11-22). The blog tells its owner what needs them, where they'll see it: this app on their
phones, Discord, Slack, ntfy, or a signed webhook. Code: `Notify/` here; the senders are agent-kit's
`AgentKit.Channels` (Discord, Slack, ntfy, Standard Webhooks), `AgentKit.Push` (Web Push), `AgentKit.Fetch` (the address
guard) and `AgentKit.Secrets` (sealing). Design: [research/2026-11-22-integrations.md](../research/2026-11-22-integrations.md).

## One model

The **event log is the only source.** A cursor (`notify_cursor`) walks it; event ids commit in order under the event
lock, so none is skipped. Each event the owner can be told about becomes one **delivery** per channel that wants it (one
per phone for push). A worker (`Blog__Notify__IntervalSeconds`, 10 s) sends what is due. Messages are fixed templates,
never a model.

- **A channel hears what happens after it is made**, never older news.
- **The owner's own doings are never sent back to them** (they just did it), except a new connection, which changes who
  can write.
- **Judged when sent:** a request decided before the worker came round is skipped with the reason ("The request was
  approved before this went out"), not sent. The same for a post that left review, or isn't live any more.

## Events

| Event | Default | Says |
| --- | --- | --- |
| `approval_requested` | on | "Claude asks to publish “Title”", opens the Inbox |
| `post_submitted` | on | "Claude submitted “Title” for review" |
| `schedule_dropped` | on | "“Title” didn't go live" and why |
| `connection_created` | on | "A new agent is connected: Claude", at which level |
| `channel_disabled` | on | "Notifications to My server stopped" and why, on the owner's **other** channels |
| `cdn_purge_refused` | on | "Cloudflare stopped taking purges" and how long trashed pictures may stay at its edge |
| `post_published` | off | "“Title” is live", only when an agent or the schedule did it |

**Detail.** `full` names the post and who did it; `minimal` says what happened and no more, for a channel others can
read. A post title is agent-written: Discord gets it with mentions switched off and markdown escaped, Slack with
`& < >` escaped. ntfy on a topic without a token defaults to `minimal`.

## Channels

| Kind | Set up with | Sealed |
| --- | --- | --- |
| `webpush` | the device itself: **Turn on for this device** (one channel per blog, a row per phone or browser) | the instance's VAPID private key |
| `discord` | a channel webhook URL (`https://discord.com/api/webhooks/…`) | the URL |
| `slack` | an incoming webhook URL (`https://hooks.slack.com/services/…`) | the URL |
| `ntfy` | server (default ntfy.sh), topic, optional access token | all three |
| `webhook` | an https URL; signed per [Standard Webhooks](https://www.standardwebhooks.com/) | the URL and the signing secret |

- **Secrets never come back** from any API. A webhook's signing secret is shown **once**, when the channel is made. A
  channel's card shows a hint (`discord.com · …abcd`).
- **Where a channel sends can't be changed**: remove it and add a new one. Removing deletes its secrets (the one
  delete in the app's grants besides sign-in state), lets its phones go, skips what waited; what it sent stays in the
  log.
- **Outgoing requests** go through the guard (`AgentKit.Fetch`): public addresses only, checked at connect time, no
  redirects. A literal private address or `localhost` is refused when the channel is made.
  `Blog__Notify__AllowPrivate=true` lets a self-hoster reach their own network (a LAN ntfy or n8n).
- At most 20 channels and 20 phones per blog.

## When it doesn't get through

Read with `AgentKit.Channels.SendOutcome`:

- **2xx:** sent; the channel's trouble clears.
- **429, 408, 425, 5xx, no answer:** retried after 1 m, 5 m, 30 m, 2 h, 12 h, or the destination's `Retry-After` when
  that is longer; then the delivery is dead. Meanwhile the channel shows "Failing since … " with the error.
- **401, 403, 404, 410:** for a webhook channel, it is **turned off** with the reason ("Discord answered 404: the
  webhook was deleted…"), what waited is skipped, and `channel_disabled` goes to the owner's other channels. **Turn
  back on** clears it. For a phone, 404 and 410 mean its subscription ended: that phone is let go
  (`push_device_gone`), the others keep getting notifications.
- **Other 4xx, a refused address, a secret that doesn't open:** dead, and the channel shows why.
- **Never silent:** a channel that stopped or keeps failing shows a banner on every screen of the app until it's fixed.

## The app

Account → **Notifications** (`#/notifications`): turn this device on or off; add Discord, Slack, ntfy or a webhook;
per channel, the events, the detail, **Send test** (sent at once, never retried later; the result shows on the card), the phones, and the
last 20 deliveries (sent, will retry, not delivered, not sent, each with why). An iPhone shows notifications from the
app only once it is added to the Home Screen; the screen says so where push isn't available.

## Configuration

`Blog__Secrets__MasterKey` (32 random bytes: `openssl rand -hex 32`) turns notifications on; without it the screen says
what to set, and everything else runs. Keep a copy: what is sealed under it needs it. `Blog__Secrets__PreviousKeys`
while rotating. `Blog__Notify__Subject` (the VAPID contact; default `Blog__PublicUrl`), `Blog__Notify__AllowPrivate`,
`Blog__Notify__IntervalSeconds`.

## API

`GET /api/blogs/{id}/notifications` (channels with hints, phones and recent deliveries; the event catalogue; the VAPID
public key); `POST /api/blogs/{id}/channels`; `PATCH`, `DELETE /api/blogs/{id}/channels/{channel}`;
`POST …/channels/{channel}/test`; `POST /api/blogs/{id}/push/devices` (the browser's `PushSubscription.toJSON()`),
`POST …/push/devices/remove` (by endpoint, from the phone), `DELETE …/push/devices/{device}`. Owner session only; every
change writes an event (`channel_created`, `_changed`, `_removed`, `_disabled`, `push_device_added`, `_removed`,
`_gone`).

## Acceptance criteria

**Use cases (driven in the running product):**
- [ ] As an owner, I turn notifications on for my phone from Account → Notifications, and my phone shows "Claude asks
      to publish …" when an agent asks; tapping it opens the Inbox.
- [ ] As an owner, I paste a Discord webhook, press Send test, and see the test in my Discord channel.
- [ ] As an owner, when I delete that webhook in Discord, the app tells me notifications to it stopped, and why.

**Behaviour (tests, `NotificationTests`, 2026-11-22):**
- [x] An agent's request reaches Discord once, mentioning nobody, with the admin link; the URL is sealed and never returned.
- [x] A request decided before sending is skipped with the reason; the owner's own publish isn't sent back; an agent's is.
- [x] A 404 turns the channel off with the reason, the other channel says so, the stopped one isn't told; turning it on clears it.
- [x] 429 honours a longer Retry-After, the schedule wins over a shorter one, success clears the failure.
- [x] A webhook is signed (verifies with the secret shown once) and keeps its id across retries; http, a private IP and localhost are refused.
- [x] ntfy without a token sends no title; with a token, the bearer and the title.
- [x] A phone gets an encrypted push that decrypts as a browser does, signed with the instance's VAPID key; a 410 lets that phone go and not the others; bad subscriptions are refused.
- [x] Send test reports sent or why not, once (never retried); removing a channel deletes its secrets and skips what waited.
- [x] Without a master key, the screen says what to set and the rest of the blog runs.
- [x] Bad events, labels, kinds, a changed destination, no CSRF header, no session and another blog are refused.

**Invariants:**
- [x] Turning off the send-time judgement, the owner's-own-doings rule, the after-creation rule, the not-to-itself
      rule, the turn-off on gone, letting a gone phone go, deleting secrets on removal, Retry-After, the address check
      or the cursor each fails the tests (mutation runs, 2026-11-22).
- [x] No agent tool reads or changes a channel (the tool list is pinned in `M1AcceptanceTests`, unchanged).
