# Agent Support (working name) — the sibling product

Status: **idea, recorded** (owner, 2026-11-22: "I realized we can do the same for support specifically"). Kept here
until it has its own repository; then this file moves there.

## The idea

A helpdesk where your AI works the tickets over MCP, with the reach you give it, the same way the blog works: you bring
your own agent (Claude, ChatGPT), the server never calls a model, and every agent action is visible and attributed.
Hosted by us to try, self-hosted to make it your own ([0005](../decisions/0005-hosted-and-self-hosted.md)).

Helpdesks that "add AI" put a model inside the product (Intercom's Fin, Zendesk's agents) and bill per resolution.
This is the other way round: the business's own AI, at a level the business picked.

## Where it starts: EchoOfDreams' support engine

EoD's support system was designed to leave EoD. Checked 2026-11-22 (EoD `8c3312ca`):

- `Support.Core` is **multi-tenant already**: `SupportTenantKind` has `EodReserved` (EoD's own support) and
  `Community` (Discord communities, for the planned Ticket Bot). Each tenant chooses where staff capabilities come
  from: an adapter (EoD's live answer) or the tenant's own members table (`SupportCapabilitySource`).
- `SupportAPI` is its own host, with an `EodAdapterClient` for everything EoD-specific (who a user is, their
  standing). Its plan's first design goal: "One engine, two clients later … ticketbot. adds rows and routes, never a
  second engine" (SUPPORT_CONSOLE_DELIVERY.md §2).
- It lives in the community database, schema `support`, every function scoped by `p_tenant_id`; it has a status
  machine, audience projection (what a requester sees vs staff), attachments, erasure and legal holds.
- Channels today: the web and Discord. **No email**: the plan lists email notifications as a non-goal.

A standalone product is a **third tenant kind** (a business) with its own members, not a rewrite. What it needs that
EoD's doesn't: email in and out, and a public, MIT-licensable home outside EoD's private repository.

## Levels, and where support differs from the blog

| Level | The agent can | Waits for a person |
| --- | --- | --- |
| **Propose** | read tickets, search past ones, draft replies, suggest tags, category and priority, write internal notes | sending anything to a customer |
| **Publish** | send replies | closing, merging, anything account-level |
| **Full** | send, close, merge, tag, assign | refunds and account actions (never an agent's in v1) |

**The blog's promise doesn't carry over unchanged.** On the blog, everything an agent does can be undone. A reply sent
to a customer can't be unsent. Publish and Full need their own safeguard; options to decide between (or combine):

1. **An undo-send window:** a sent reply waits N seconds or minutes, visible to staff with a "stop" button, then goes.
2. **Scope by category:** an agent sends on its own only in categories the owner chose (billing questions no,
   "how do I" yes).
3. **Approved templates:** an agent sends on its own only replies built from templates a person approved, Propose for
   everything else.

**Default if no answer:** 1 and 2 together.

## Shared with the blog

Through agent-kit ([0007](../decisions/0007-shared-kernel.md)): MCP OAuth, provider sign-in, the autonomy levels and
approval requests, Liquid (the help-centre pages, reply and email templates), secrets, export. Tenancy follows the
same rule: a column and a scope, owned by the product's schema.

## Open questions (the owner's)

1. **How does the code leave EoD's private repository?** Extract `Support.Core` and `SupportAPI` into a public
   repository and have EoD consume it (one engine), or start the standalone product fresh with EoD's design as the
   source. **Default:** decided when SUPPORT_CONSOLE_DELIVERY.md reaches M3 (the API and adapter exist), so the
   extraction follows the built shape, not a guess.
2. **Email:** inbound by forwarding to an address we host, or by connecting the business's own mailbox? **Default:**
   forwarding (no mailbox credentials held).
3. **The send safeguard** above.
4. **Name.**
