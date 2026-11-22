# Roadmap

Each milestone ships on its own and is **not done** until every box below it is verified against the running product.

## M0 — repository set up ✅ (2026-11-22)

- [x] Charter, vision, decisions 0001–0004, tool contract.
- [x] A .NET 10 server that builds, answers `GET /healthz` with `{"status":"ok"}`, and has a passing test.
- [x] Dockerfile, compose (Postgres + server), CI that builds and tests.
- [x] CI green on the first push, image included (2026-11-22, run 1; with Postgres 18 from run 2).

## M1 — write with your agent, publish yourself (Propose)

**Slices** (each lands with its tests; the boxes below are the milestone's, checked when it's done):
1. ✅ Data model and migrations ([specs/data-model.md](specs/data-model.md)): tenant-scoped with composite keys,
   append-only events and revisions, the app role's grants. 19 tests against real Postgres (2026-11-22).
2. ✅ Owner sign-in (`AgentKit.ProviderSignIn`), first-run setup that claims the blog, recovery by a new setup token,
   a second way in, sign out (everywhere), CSRF header, rate limits ([specs/auth.md](specs/auth.md)). 15 tests with
   stub Google and Discord; breaking the CSRF or owner check fails them (2026-11-22).
3. ✅ The MCP door (`AgentKit.McpOAuth`): the consent page asks which blog, connections start at Propose, the owner
   sets levels (Full confirmed) and revokes, `get_blog` reads the level fresh on every call
   ([specs/connections.md](specs/connections.md)). 8 tests driving the claude.ai flow end to end (2026-11-22).
4. ✅ Posts over MCP: drafts, edits pinned to a revision, review, publish, schedule (judged when it fires), unpublish,
   trash and restore, approval requests that apply only if the post didn't move on, the owner restoring any revision
   ([specs/posts.md](specs/posts.md)). 15 tests; opening every level, ignoring the pinned revision or skipping the
   schedule check each fails them (2026-11-22).
5. ✅ Public pages in Liquid (Fluid), RSS, Atom, sitemap, robots.txt; only live posts, Markdown made safe, hosted
   subdomains ([specs/public-site.md](specs/public-site.md)). 18 tests; passing raw HTML, skipping link filtering or
   showing drafts each fails them (2026-11-22). Per-blog themes come later.
6. ✅ The phone app at `/admin`: Inbox with a line diff of every proposed edit, posts with preview, actions and
   revision restore, agents with levels (Full confirmed in words), history in words, account
   ([specs/app.md](specs/app.md)). Checked in Chromium at phone and desktop widths, light and dark, by clicking through
   approve and Full against a running server (2026-11-22).
7. ✅ Media to the bucket: upload by presigned PUT then confirm (bytes read back and checked), or fetched from a URL
   through a connect-time guard against private addresses; served from `/media/…` on the blog's own host; the owner's
   Media screen with trash and restore ([specs/media.md](specs/media.md)). 53 new tests (131 in all); turning off any of the six guards
   fails them (2026-11-22). Agent media trash waits for approval requests to cover media.
8. ✅ Categories and tags, riding in revisions (restore and approvals carry them), suggested to the owner below Publish;
   category and tag pages for readers; the owner renames the blog; the app's display settings (theme, text size);
   tables readable in the app and the theme ([specs/taxonomy.md](specs/taxonomy.md)). 7 new tests (139 in all); six
   rules turned off each fail them (2026-11-22).
9. ✅ Themes you can try on the site: Paper, Ledger, Mono and Studio, each its own Liquid layout; a bar on the public
   pages (the owner's switch) lets anyone, signed in or not, see the blog in each, and the owner picks the blog's own
   ([specs/themes.md](specs/themes.md)). 4 new tests (143 in all); five rules turned off each fail them (2026-11-22).
10. ✅ M1 acceptance pass (2026-11-22): every box below checked against tests and the live blog. Closed on the way:
    a revoked connection's 401 now says why (`error_description`); a request whose post changed state shows why it no
    longer applies and can't be approved into the trash; History says what a settings change did; tool inputs for
    terms survive older clients. Product review by a second model: [reviews/2026-11-22-product.md](reviews/2026-11-22-product.md).

**User use cases (driven in the running product):**
- [x] As a writer, I deploy the blog on Railway and sign in on my phone with Discord: done on blog-ai.echoofdreams.eu
      (Discord, setup token, 2026-11-22). **One click** is the template, which is M3.
- [ ] As a writer, I add the blog to claude.ai as a custom connector, and Claude creates a draft with a picture in it.
      The connector and drafts are proven live (eight guides written through it); a **picture** through it is still
      to be done live in a fresh chat (the media tools shipped after this session's connector was loaded).
- [x] As a writer, I see that draft on my phone with a preview and publish it with one tap: the eight guides were
      reviewed and published from the app (2026-11-22).
- [x] As a reader, I open the post's URL with JavaScript off and read it; it appears in RSS and the sitemap: the live
      HTTP log shows every post, `/feed.xml`, `/atom.xml`, `/sitemap.xml` and `/category/guides` served 200; pages
      carry no script (CSP `default-src 'none'`), tested.

**Behaviour:**
- [x] Every Propose tool in [specs/tools.md](specs/tools.md) works; every tool above Propose returns an approval
      request the owner can approve or decline (`M1AcceptanceTests`: the whole matrix, six requests decided).
- [x] Every save is a revision naming its connection; the owner restores any revision in one tap (PostTests, AppApiTests,
      driven in the app).

**Invariants:**
- [x] Every table carries `blog_id` and every tool is scoped to the connection's blog: a connection of blog A asking
      for blog B's ids gets "not found" from all twelve tools that take an id, and lists never show B
      (`M1AcceptanceTests`; composite keys and the term trigger in the database).
- [x] Public pages render through Liquid (Fluid) with a step, recursion and time limit; a broken theme falls back to
      the default (mutation-tested). *Telling the owner* is the server log for now: the four built-in themes are each
      tested on every page, so a failure is a bug, not a user's theme. It moves to the uploaded-themes milestone, where
      a failure can come from the owner's own template.
- [x] MCP OAuth and sign-in are the `AgentKit.McpOAuth` and `AgentKit.ProviderSignIn` packages, not copies.
- [x] A new connection is at Propose (SchemaTests, ConnectionTests).
- [x] No tool changes a connection's level, empties the trash or touches sign-in: the tool list is pinned at every
      level, and an unknown tool is refused (`M1AcceptanceTests`).
- [x] An approval for a post that changed since the request does not apply, and says why: a moved revision, a state
      that no longer allows it, or a schedule time passed.

**Edges:**
- [x] A revoked connection's next call fails with a clear reason, even mid-session: `401` with
      `error_description="The blog's owner revoked this connection…"`.
- [x] A stale revision id is refused with the current one.

## M2 — Write together ([0008](decisions/0008-write-together.md))

The channel between owner and agent becomes two-way, in the order of the
[product review](reviews/2026-11-22-product.md).

**Slices:**
1. ✅ The brief and the feedback loop: "how this blog sounds" in the app, returned by `get_blog` as the owner's own
   words; Decline with a reason and **Ask for changes**; a note to every agent; the most recent notes in `get_blog`
   and the reason in `get_request` ([specs/feedback.md](specs/feedback.md)). 5 new tests (156 in all); four rules
   turned off each fail them (2026-11-22).
2. ✅ The owner edits in the app: a Markdown editor with a preview on a post, and **Approve with edits** on a request,
   each author keeping their words as their own revision ([specs/editing.md](specs/editing.md)). 5 new tests (161 in
   all); two rules turned off each fail them (2026-11-22).
3. ✅ Notifications: one channel model over the event log (deliveries judged when sent, retries, Send test, failures
   never silent), with Web Push to the app, Discord, Slack, ntfy and signed outgoing webhooks
   ([specs/notifications.md](specs/notifications.md)), on agent-kit's Channels, Push, Fetch and Secrets. 11 new tests
   (144 in all, the address table now in agent-kit); ten rules turned off each fail them (2026-11-22). Live once
   `Blog__Secrets__MasterKey` is set.
4. ✅ Pictures through Cloudflare, optional ([0009](decisions/0009-media-through-cloudflare.md),
   [media.md § Cloudflare](specs/media.md#cloudflare)): cache tags on every picture; connected with a purge-only token, a
   long edge time (`Cloudflare-CDN-Cache-Control`) and trash purged at once through an outbox; refusals told once;
   disconnecting purges everything. 8 new tests; twelve rules turned off each fail them (2026-11-22).
5. ✅ `check_post` ([specs/check.md](specs/check.md)): a pre-flight an agent runs before asking (links, alt text, excerpt, title, headings, similar posts),
   its result shown on the Inbox card. Deterministic: no model. Outside links fetched only when asked, 25 at most,
   remembered a day. 8 new tests; fifteen rules turned off each fail them (2026-11-22).
6. ✅ Social cards and structured data ([specs/social.md](specs/social.md)): every shared link shows the post's own picture or
   a card drawn in the blog's theme, JSON-LD, one shared head for every theme; IndexNow and WebSub tell search engines and
   feed readers when a post changes, never naming what wasn't public. 8 new tests (168 in all); ten rules turned off
   each fail them (2026-11-22).
7. ✅ Announce ([specs/announce.md](specs/announce.md)): new posts to Bluesky, Mastodon, Discord, Slack, Telegram and X, after
   the blog's wait and judged when sent, through agent-kit's `AgentKit.Social` with the announcement as its ledger (the
   Slack and Telegram adapters added there for this). What an agent published on its own, or wrote with
   `write_announcement`, waits for the owner's tap; lost answers are unconfirmed until the owner looks. 15 new tests (183
   in all); twenty-two rules turned off each fail them (2026-11-22).
8. ✅ Honest bylines ([specs/bylines.md](specs/bylines.md)): "Written by Claude · approved by Lenka", worked out from the
   revisions, the first publication and the approvals; on the page in every theme, in RSS and Atom, and as JSON-LD
   (people `author`, agents `contributor`). The owner names themself and each agent, or turns bylines off. 8 new tests;
   thirteen rules turned off each fail them (2026-11-22).
9. ✅ Subscribe by email ([specs/subscribe.md](specs/subscribe.md)): a form under every post, double opt-in with a
   button (scanners confirm nobody), the new-post email as an announcement (the wait, judged when sent, the owner's
   tap for an agent's post), through Resend or SMTP with the owner's own key; RFC 8058 one-click unsubscribe that erases
   the address; addresses sealed, no tracking. 12 new tests; seventeen rules turned off each fail them (2026-11-22).

**Alongside:** extract the agent-kit packages from the copies in [0007's ledger](decisions/0007-shared-kernel.md#copies-waiting-for-a-package-ledger-2026-11-22),
switching Relay and Agent Blog to them in the same change. Done: Secrets, Push, Fetch (0.1.6) and Channels (0.1.7).
Social (0.1.9, with the EoD director; Slack and Telegram added for slice 7). Next: Storage, Postgres.

**User use cases:**
- [ ] As an owner, I write how my blog sounds once, and a new agent session writes in that voice without me repeating it.
- [ ] As an owner, I decline with a reason or ask for changes from my phone, and the agent's next attempt shows it read them.
- [ ] As an owner, I fix one sentence of a draft on my phone and publish it, without going back to the chat.
- [ ] As an owner, my phone tells me when an agent asks for something.
- [x] As an agent, I check a draft before asking, and fix what the check finds (slice 5, `CheckTests`).
- [ ] As an owner, a post I publish appears on my Bluesky after the wait, and one an agent published on its own waits for
      my tap.
- [ ] As a reader, I see under each post who wrote it, people and agents, and whether a person approved it.
- [ ] As a reader, a shared link shows a picture and the post's title; I can subscribe and get the next post by email.

**Invariants:**
- [ ] Only the owner writes the brief and notes; every tool result marks them `author: owner`; no agent text becomes either.
- [ ] Feedback never approves, publishes or changes a level by itself.
- [ ] The server still calls no model.

## M2b — Several writers, and Echo of Dreams' blog ([research](research/2026-11-22-eod-blog.md))

Owner, 2026-11-22: *"EoD blog should be writable by multiple users. The same the blog-ai version should have an invite
only access option."* Comments: *"Keep EoD's comments via the widget."* History: *"start each post's history at the
import."* Agent Blog becomes Echo of Dreams' blog; blog.echoofdreams.eu keeps its addresses, its comments and its
shared EoD session.

**Slices:**
1. Writers by invitation: the owner invites a person with a single-use, expiring link bound to the account they sign
   in with (Discord or Google); no open sign-up, ever. Roles: **writer** (drafts, edits their own, submits for review,
   like Propose) and **editor** (publishes and edits any post); the owner stays the one who invites, removes and sets
   levels. Removing a writer keeps their posts and revisions under their name. Every write names its person.
2. Writers' agents: a writer connects their own agent, and its level is capped by the writer's role (a writer's agent
   never publishes). The Inbox shows requests to whoever may decide them.
3. Authors on the page: a byline per post from its people and agents, author pages (`/author/{slug}`) in every theme.
4. Echo of Dreams' instance: its own Railway service, R2 bucket and Cloudflare zone; a theme in EoD's look with a
   creator card linking to the author's EoD profile.
5. Import from EoD: `named_pages` blog posts with their uuids, slugs, dates, tags and categories; pictures re-hosted
   from EoD's gallery into the bucket and their `g://` links rewritten; each post's history starts at the import;
   EoD authors become invited writers.
6. Old addresses: `/{slug}` answers 301 to `/posts/{slug}`, `/api/blog/feed.xml` to `/feed.xml`; tag and category
   addresses already match.
7. Comments through EoD's widget: a theme slot keyed by the post's uuid, so every existing comment and reaction stays
   attached; EoD serves the widget (an EoD lane: Relay `eod`).
8. The switch: the cf-worker's `blog.echoofdreams.eu` route points at the new service (and back the same way); then EoD
   retires `blogConsole`, the `blog_authoring` pack (11 tools off its MCP door), the admin blog tabs and the review
   queue widget (EoD lanes).

**User use cases:**
- [ ] As an owner, I invite someone to write; they sign in from the link and write drafts; nobody else can join.
- [ ] As an editor, I publish a writer's post; as a writer, my agent can draft and submit but never publish.
- [ ] As a reader of blog.echoofdreams.eu, old links and the feed still work, and the comments are still there.

**Invariants:**
- [ ] No way in without an invitation the owner made; an invitation works once, for one account, and expires.
- [ ] A writer's agent never holds more than its writer may do.
- [ ] Removing a writer removes nothing they wrote.

## M3 — Publish and Full

- [x] The owner sets a connection to Publish or Full; Full asks once, in plain words, and says how to undo (M1, slice 6).
- [x] At Publish, an agent publishes and schedules; a schedule from a connection lowered to Propose before it fires
      does not fire, and the post stays a draft (M1, slice 4, tested).
- [x] At Full, an agent trashes and restores; nothing an agent does empties the trash (M1, slices 4 and 10).
- [ ] History shows every agent action with its connection, and each is reversible from there.
- [ ] Agent media trash and `edit_blog_settings`, as approval requests below Full (approval requests beyond posts).

## M4 — the template goes public

- [ ] Image published to GHCR on release; the Railway template deploys the image.
- [ ] A deploy into a fresh Railway account, filling nothing in, goes green and signs in.
- [ ] The repository is public; README carries the Deploy on Railway button.

## M5 — hosted

- [ ] As a visitor, I start a blog on the hosted service without deploying anything and connect Claude to it.
- [ ] As a hosted owner, I export everything and import it into a self-hosted copy; posts, revisions, media, theme and
      connections' history arrive (connections themselves are re-made: tokens never travel).
- [ ] 0005's open points are decided: the trial, the content policy with a report button and takedown, payment.

## Later (not committed)

- Earned autonomy, as a prompt and never automatic: "You approved its last 12 unchanged. Let it publish?" (review #10).
- An idea backlog: `propose_idea`, the owner taps "Write this" or "No" (review #8).
- Series and internal links (review #9).
- A per-post timeline with a word-level diff for prose (review #7).
- Import from WordPress (WXR) and Ghost (JSON); imported posts become the brief's examples (review #12).
- Several human authors.
