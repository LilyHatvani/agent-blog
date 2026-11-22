# The brief and the owner's feedback

Status: **M2, slice 1** (2026-11-22). Decision: [0008](../decisions/0008-write-together.md). Code: `Feedback/`,
`Posts/Posts.cs` (decisions), `Mcp/BlogTools.cs`, migration `0008_feedback.sql`.

## The brief

Account → **How this blog sounds** in the app, one form:

| Field | Up to | Sent to agents as |
| --- | --- | --- |
| Who it's for | 500 characters | `who_it_is_for` |
| The voice | 2,000 | `voice` |
| Never | 2,000 | `never` |
| Posts that sound right | 3 of the blog's posts (not in the trash) | `example_posts` (id, slug, title) |

`get_blog` returns it as `brief`, with `"author": "owner"`, or `null` while it's empty. Saving the same brief writes
nothing; a change writes `brief_changed` naming the fields.

## Deciding with a reason

The Inbox card has three decisions:

| Decision | Note | What happens |
| --- | --- | --- |
| **Approve** | optional | the pinned change applies (if it still can) |
| **Ask for changes** | required | nothing applies; the request is `changes_requested`; the agent is told to change the post and ask again |
| **Decline** | optional | nothing applies; the request is `declined` |

`get_request` returns the state, the note as `owner_note` (`"author": "owner"`), and `next`: what to do now, in words.

## Notes to every agent

Agents → **Tell your agents**: a note to all of them, optionally about one post. Every note the owner writes (here, or
as the reason for a decision) is kept in `owner_note`:

- `get_blog` sends the **10 newest notes in force** to every session, each marked `"author": "owner"`, with the post
  or request it's about.
- `read_post` sends the notes about that post.
- A note that no longer holds is **retired**: it stops being sent, and stays in the log (Retired notes). The database
  refuses any other change to a note, and any delete, for every role.

## Trust

The brief and notes are the one blog text an agent follows as instruction, because the owner wrote them (0008). Only
the owner's API writes them, behind a session and the CSRF header; no tool does, and an agent's bearer never reaches
that API. The server instructions tell the agent which fields are the owner's.

## Acceptance criteria

**Use cases (driven in Chromium against a running server, 2026-11-22):**
- [x] As an owner, I write how my blog sounds on my phone and pick an example post; Save is off until something changes.
- [x] As an owner, I ask for changes from the Inbox with a note (Send is off while it's empty), and the card leaves the Inbox.
- [x] As an owner, I send a note to every agent and retire an old one; it moves to Retired notes.
- [ ] As an owner, a new agent session writes in the brief's voice without me repeating it (live, with a real agent).

**Behaviour (tests, `FeedbackTests`, 5):**
- [x] The brief reaches `get_blog` marked as the owner's, with its examples; an empty brief sends nothing; a no-op save writes nothing; limits and foreign posts are refused; CSRF holds.
- [x] Ask for changes needs a note, changes nothing, and the agent reads why on the request (with `next`), on the post and on the blog; the next attempt can be approved with a note, which is kept.
- [x] A decline may carry a reason or none.
- [x] A note is sent until retired, kept after, and the database refuses rewriting, deleting or un-retiring it.
- [x] An agent's bearer can't write the brief.

**Invariants:** each of these, turned off, fails the tests (mutation runs, 2026-11-22): changes needing a note;
decision notes being kept; retired notes not being sent; notes written once.
