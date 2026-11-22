# Subscribe by email

Status: **M2, slice 9, built** (2026-11-22). Readers leave their address under a post, confirm it from their inbox, and get an
email when a new post goes live, sent through the owner's own mail service: Resend's API, or any SMTP server. Code:
`Mail/` here. The new-post email is an announcement like the others ([announce.md](announce.md)), so the same rules
apply: the wait, judged when sent, and a post an agent published on its own (or an email an agent wrote) waits for the
owner's tap.

## Setting it up

The owner adds **Email** under **Account → Announce**: the provider (**Resend**: an API key; **SMTP**: host, port,
user, password), the **From** address (on a domain the provider has verified) and a **From name**. Credentials are
sealed under `Blog__Secrets__MasterKey`, as every other secret. **Send a test** sends one email to an address the owner
types, at once, and says what the provider answered.

On Railway, outbound SMTP is open only on the Pro plan and above (docs.railway.com, Outbound Networking, 2026-11-22):
use Resend there, or another provider's HTTP API when one is added.

Only once an Email account exists (and is on) does the blog show the form.

## Readers

- **The form**: under every post, in every theme, and at `/subscribe`. An address, a field people don't see (a bot
  that fills it is told it worked and nothing happens), and **Subscribe**.
- **Double opt-in**: the answer is always the same, "Check your inbox", whether the address is new, waiting or
  already subscribed, so the form tells nobody who is on the list. A confirmation email goes to a new or waiting
  address; the link in it expires after 48 hours. The confirmation page has a **Confirm** button (a `POST`), so a
  mail scanner that opens links doesn't confirm anyone.
- **Limits**: 5 tries a minute from one address, and one confirmation email per address per hour, so the form can't be
  used to flood someone's inbox from the owner's domain.
- **Unsubscribe**: every email carries a link (a page with a button) and the `List-Unsubscribe` and
  `List-Unsubscribe-Post: List-Unsubscribe=One-Click` headers (RFC 8058), so a mail client's own button works.
  Unsubscribing erases the address.

## What the blog keeps

- The address sealed under the master key (associated data naming the subscriber), and a keyed hash of it (HMAC-SHA256
  with a per-blog secret) to find duplicates. A database copy without the key holds no address.
- The state (`pending`, `confirmed`, `unsubscribed`), when it changed, and nothing else: no IP address, no opens, no
  clicks. Emails carry no tracking pixel and no rewritten links.
- The unsubscribe link's token is derived from the subscriber's id with the same per-blog secret: stored nowhere.

## The new-post email

- One per confirmed subscriber, from the announcement's own row: `Email` is an announcement account, so the post's
  **Announce** section shows it, with the wait, **Edit text**, **Announce** and **Don't announce**.
- Subject: the post's title. Body: the announcement's text (by default the title and excerpt), a **Read it** link to
  the post, the byline when the blog shows bylines, and the footer: why the reader gets it, and the unsubscribe link.
  HTML and plain text.
- Sent by a worker from a queue, one email per subscriber, each with its own retries (1 m, 5 m, 30 m, 2 h, 12 h, or
  the provider's wait), and an idempotency key on Resend so a retry never sends twice. Judged when sent: a post taken
  down mid-send stops; an address unsubscribed meanwhile is skipped.
- A credential refused stops the queue for the account and says so on it.

## The owner

- **Subscribers** on the Email account: how many are confirmed and waiting, each address, when, and **Remove**.
- Agents never see subscribers, addresses or counts.

## Acceptance criteria

Behaviour, invariants and edges are checked by `SubscribeTests` (12), each rule turned off once to see a test fail; the
use cases wait for the live pass.

**User use cases:**
- [ ] As a reader, I subscribe under a post, confirm from my inbox, and get the next post by email.
- [ ] As a reader, I unsubscribe with one click from any email, and my address is gone.
- [ ] As an owner, I connect my mail service, send myself a test, and see who subscribed.

**Behaviour:**
- [x] The form's answer is the same for a new, waiting and confirmed address.
- [x] Nobody is subscribed without confirming; opening the confirmation link without pressing Confirm subscribes no one.
- [x] A new post goes to every confirmed subscriber once, after the wait; an agent's post waits for the owner's tap.
- [x] A post taken down before or during sending stops the rest; an unsubscribed address gets nothing more.
- [x] One-click unsubscribe (RFC 8058) works without a page.

**Invariants:**
- [x] Addresses are sealed at rest and never reach an agent.
- [x] No IP, open or click is recorded; links in emails aren't rewritten.
- [x] No agent path adds, changes or reads the Email account or its subscribers.

**Edges:**
- [x] Without an Email account the form isn't shown and `/subscribe` says the blog doesn't send email.
- [x] Too many tries from one address are refused, and a second confirmation within the hour isn't sent.
- [x] A refused credential stops the queue and shows on the account; a provider's rate limit waits its time.
