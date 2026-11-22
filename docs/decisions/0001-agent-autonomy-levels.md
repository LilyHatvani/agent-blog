# 0001 — Agent autonomy levels: Propose, Publish, Full

Status: **accepted** (owner, 2026-11-22: "I want the option to have the AI have full power (some people want it)")

## Question

How much may an agent do on the blog without the owner's click? Some writers want their hand on every publish. Some
want the agent to run the blog. Both are real uses.

## Decision

**Three levels, set per agent connection by the owner. Propose is the default.**

| Level | The agent can | Waits for the owner's click |
| --- | --- | --- |
| **Propose** (default) | read everything on the blog; create drafts; edit any draft; upload media; suggest categories and tags; submit a draft for review | publish, schedule, unpublish, edit a live or scheduled post, move to trash |
| **Publish** | everything in Propose; publish and schedule; edit live posts; unpublish; create categories and tags | move to trash |
| **Full** | everything in Publish; move posts and media to trash and restore them; edit, merge and delete categories and tags; edit the blog's title, description and navigation | nothing |

A request above the connection's level is never refused outright. It becomes an **approval request** with the exact
change pinned (the post's revision), which the owner approves or declines on the phone. The agent hears "waiting for
the owner", with the request id, not "forbidden". (EchoOfDreams' blog tool pack works this way: public changes are
judged now, ledgered, and run as the owner's click.)

## What stays the owner's at every level, Full included

- Emptying the trash, purging media, deleting the blog's data, exporting it.
- Connections: creating, revoking, or changing any connection's level. **No agent raises its own level.**
- The owner's sign-in and the instance's settings (domain, providers, bucket).

These are not caps on use: an agent at Full can do everything the blog's content needs. They are the lines that keep
"undo" true.

## What holds at every level

- **Every save is a revision** naming the connection that made it. The owner's history view shows who did what and
  restores any revision in one click.
- **Trash is a bin.** Posts and media in the trash stay restorable until the owner empties it. No rule empties it on
  its own (nothing is deleted by inaction).
- **A schedule is judged when it fires.** A post an agent scheduled goes live only if that connection still holds
  Publish or Full at that moment. Revoke the connection or lower it to Propose, and its pending schedules stop; the
  posts stay as drafts.
- **Every agent action writes an event** (append-only), whether it ran or waited.

## How Full is offered

The level picker shows all three. Choosing Full asks once, in plain words: "This agent will publish, change and trash
posts without asking you. You can see and undo everything it does in History." The owner confirms; nothing more. This
is the acknowledged compromise: the agent gets the power, the owner keeps the undo.

## Per connection, not per blog

A writer can let Claude Code run at Full for a changelog workflow while claude.ai on the phone stays at Propose.
The level belongs to the connection (its OAuth client and grant), and an MCP session reads it fresh on every call, so
lowering a level takes effect on the agent's next tool call, not at the next sign-in.

## Same words on WordPress

The sibling repository, Agent Ready (WordPress), uses the same three names. There they map to WordPress roles the agent's
user holds: Propose = Contributor, Publish = Author, Full = Editor. WordPress's own permission checks enforce them.

## Alternatives considered

- **Propose only.** The most principled, and it turns away the people who want an agent to run a changelog or a niche
  site. They would go elsewhere and lose the undo too.
- **A free-form permission matrix.** Every capability as a toggle. Precise, and a settings page nobody reads. Three
  levels with a clear story cover the real uses; the matrix can come later if a fourth use appears.
- **Earned autonomy** (Relay's 0008: the level rises as the owner accepts the agent's work). Interesting, and a
  natural next step on top of these levels, not instead of them. Not in v1.
