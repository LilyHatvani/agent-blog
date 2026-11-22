# 0008 — Write together: the brief, the owner's feedback, and the next milestone

Status: **accepted** (owner, 2026-11-22: "go with the review's order as the next milestone", on
[reviews/2026-11-22-product.md](../reviews/2026-11-22-product.md))

## Decision

M2 is **Write together**: the channel between owner and agent becomes two-way, in the review's order. First the brief
and the feedback loop, with the owner's quick edit and push; then a pre-flight check and social cards; then bylines and
subscribe. The old M2 (Publish and Full), mostly built during M1, becomes M3; the template and hosted move down one.

## The brief is the one blog text an agent follows

The trust boundary in [CLAUDE.md](../../CLAUDE.md) stays: text from the blog that the owner didn't write is data. The
**brief** ("how this blog sounds": who it's for, the voice, what never to do, posts that show it) and the owner's
**notes** (a reason on a decision, a note to every agent) are written by the owner, so they are the owner's instructions
to the agent, and every tool result marks them so: `"author": "owner"`. Nothing an agent writes can become part of
either: only the owner's API writes them, and an agent's text quoted in a note stays a quote.

## Feedback is kept, and every session starts from it

- Deciding a request may carry a note. **Ask for changes** is a third decision beside Approve and Decline: it needs a
  note, leaves the post as it was, and tells the agent what to change.
- Every note the owner writes (on a decision, or to all agents) is kept in an append-only log. `get_blog` returns the
  brief and the most recent notes, so a new session, any agent, starts where the last was corrected.
- The owner can retire a note that no longer holds; it stays in the log, marked retired, and stops being sent.

## Still true

- The server calls no model (CLAUDE.md): the brief and notes are text the agent reads; nothing scores or rewrites them.
- The owner decides; feedback never changes a level or approves anything by itself.

## Considered

- **The brief as a post or a page.** Rejected: readers would see it, and an agent could edit it at Full, which would let
  an agent write its own instructions.
- **Feedback per connection.** Rejected for now: the owner's taste is about the blog, not about one agent; every
  agent gets the same notes. A note can name a post.
