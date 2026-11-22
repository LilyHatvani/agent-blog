# Roadmap

Each milestone ships on its own and is **not done** until every box below it is verified against the running product.

## M0 — repository set up (2026-11-22)

- [x] Charter, vision, decisions 0001–0004, tool contract.
- [x] A .NET 10 server that builds, answers `GET /healthz` with `{"status":"ok"}`, and has a passing test.
- [x] Dockerfile, compose (Postgres + server), CI that builds and tests.
- [ ] CI green on the first push (the image build has not run yet: no Docker daemon where M0 was written).

## M1 — write with your agent, publish yourself (Propose)

**Slices** (each lands with its tests; the boxes below are the milestone's, checked when it's done):
1. ✅ Data model and migrations ([specs/data-model.md](specs/data-model.md)): tenant-scoped with composite keys,
   append-only events and revisions, the app role's grants. 19 tests against real Postgres (2026-11-22).
2. Owner sign-in (`AgentKit.ProviderSignIn`), first-run setup that claims the blog.
3. The MCP door (`AgentKit.McpOAuth`): connections with levels, the consent page asks which blog.
4. Posts over MCP: the Propose tools, revisions, approval requests.
5. Public pages in Liquid (Fluid), RSS, Atom, sitemap.
6. The phone app: review, preview, publish, history, levels.
7. Media to the bucket.

**User use cases (driven in the running product):**
- [ ] As a writer, I deploy the blog on Railway with one click and sign in on my phone with Google or Discord.
- [ ] As a writer, I add the blog to claude.ai as a custom connector, and Claude creates a draft with a picture in it.
- [ ] As a writer, I see that draft on my phone with a preview and publish it with one tap.
- [ ] As a reader, I open the post's URL with JavaScript off and read it; it appears in RSS and the sitemap.

**Behaviour:**
- [ ] Every Propose tool in [specs/tools.md](specs/tools.md) works; every tool above Propose returns an approval
      request the owner can approve or decline.
- [ ] Every save is a revision naming its connection; the owner restores any revision in one tap.

**Invariants:**
- [ ] Every table carries `blog_id` and every tool is scoped to the connection's blog: a connection of blog A asking
      for blog B's ids gets "not found" ([0005](decisions/0005-hosted-and-self-hosted.md)).
- [ ] Public pages render through Liquid (Fluid) with a step, recursion and time limit; a broken theme falls back to
      the default and tells the owner ([0006](decisions/0006-themes-in-liquid.md)).
- [ ] MCP OAuth and sign-in are the `AgentKit.McpOAuth` and `AgentKit.ProviderSignIn` packages, not copies
      ([0007](decisions/0007-shared-kernel.md)).
- [ ] A new connection is at Propose.
- [ ] No tool changes a connection's level, empties the trash or touches sign-in (a test calls each with every level).
- [ ] An approval for a post that changed since the request does not apply, and says why.

**Edges:**
- [ ] A revoked connection's next call fails with a clear "connection revoked", even mid-session.
- [ ] A stale revision id is refused with the current one.

## M2 — Publish and Full

- [ ] The owner sets a connection to Publish or Full; Full asks once, in plain words, and says how to undo.
- [ ] At Publish, an agent publishes and schedules; a schedule from a connection lowered to Propose before it fires
      does not fire, and the post stays a draft.
- [ ] At Full, an agent trashes and restores; nothing an agent does empties the trash.
- [ ] History shows every agent action with its connection, and each is reversible from there.

## M3 — the template goes public

- [ ] Image published to GHCR on release; the Railway template deploys the image.
- [ ] A deploy into a fresh Railway account, filling nothing in, goes green and signs in.
- [ ] The repository is public; README carries the Deploy on Railway button.

## M4 — hosted

- [ ] As a visitor, I start a blog on the hosted service without deploying anything and connect Claude to it.
- [ ] As a hosted owner, I export everything and import it into a self-hosted copy; posts, revisions, media, theme and
      connections' history arrive (connections themselves are re-made: tokens never travel).
- [ ] 0005's open points are decided: the trial, the content policy with a report button and takedown, payment.

## Later (not committed)

- Earned autonomy on top of the levels (Relay 0008's idea).
- Import from WordPress (WXR) and Ghost (JSON).
- Several human authors.
