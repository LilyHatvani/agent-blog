# 0005 — Hosted by us, self-hosted when you want it your own

Status: **accepted** in direction (owner, 2026-11-22: "The blog version can be theoretically us hosted too, with a
preview and try out, with the self hosting option as 'if you want to make it your own'"). The open points below are
the owner's.

## Decision

**One codebase, two ways to run it.**

- **Hosted:** we run one service with many blogs. People try it without deploying anything and stay if they like it.
- **Self-hosted:** the same image with one blog in it, on Railway (the template) or anywhere Docker runs. "Make it
  your own."
- **Moving between them is one button each way.** Export everything from hosted, import into a self-hosted copy, and
  back. That's what makes "try it on ours" trustworthy: leaving is never a project.

## What this changes now, before M1

- **Every table carries `blog_id` from the first migration.** A self-hosted instance simply has one row in `blogs`.
  Adding a tenant column later means migrating everyone's data; adding it to an empty schema costs nothing.
- **Every query and every tool is scoped to one blog**, resolved from the connection (MCP) or the request's host
  (public pages). No code path reads across blogs. A test calls each tool as a connection of blog A against blog B's
  ids and expects "not found".
- **One MCP endpoint for the whole hosted service.** `AgentKit.McpOAuth` has one issuer (`PublicUrl`) and the MCP
  endpoint at `/mcp`. That fits: the agent connects to the service, and the consent page asks which blog the
  connection is for (`IMcpConnectionStore.RenderChoicesAsync`). The connection is bound to that blog and its level.
  No package change needed ([0007](0007-shared-kernel.md)).
- **People, not just one owner.** Hosted needs accounts: each blog has an owner, signed in with
  `AgentKit.ProviderSignIn` (Google, Discord). Several humans per blog stays "later"; the seam is a blog's members
  table with one row.

## Public addresses

- **Hosted blogs** start at `<name>.<our blog domain>`, with custom domains as the way to make one fully yours
  (Cloudflare's custom hostnames for SaaS, or Railway custom domains).
- **Why custom domains matter early:** blogs on a shared domain share its reputation. Relay was flagged as phishing
  by Chrome on `*.up.railway.app` and had to move (2026-11-11). One spam blog on our subdomain can mark them all.
  The shared blog domain is therefore a separate registered domain, never the product's own.

## Open points (the owner's)

1. **The try-out and "nothing deleted by inaction" pull against each other.** A trial blog nobody comes back to
   either stays forever or gets removed. Options: trials are said to be temporary up front, with export offered before
   anything goes; or trials never expire but stop being public after a while (hidden, not deleted); or there is no
   anonymous trial, only a free tier. **Default if no answer:** hidden after 30 days without a visit from its owner,
   never deleted; the owner gets it back by signing in.
2. **Hosting other people's writing is a content and abuse question.** A content policy, a report button on public
   pages, and a takedown path are needed before hosted opens to strangers. **Default:** hosted opens by invitation
   until those exist.
3. **How hosted is paid for.** Unlike EoD, this product hosts no adult catalog by design, but the content policy
   decides what processors will say. **Default:** decided with point 2, before hosted opens to strangers.

## Not changed

The principles hold in both ways of running it: levels per connection, every agent change recoverable, public pages
as plain HTML, no AI model calls in the server.
