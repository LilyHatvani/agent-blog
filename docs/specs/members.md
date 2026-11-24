# Writers by invitation

Status: **M2b, slice 1, built** (2026-11-22); **slice 2, their agents, built** (2026-11-23). A blog has one owner and, by the owner's invitation, people who write with them:
**writers** and **editors**. Nobody joins any other way: there is no sign-up. Code: `Auth/Members.cs`; the rules for
what each may do with posts live in the core (`Posts/Posts.cs`), the same for the app and anything after it.

## Roles

| | Writer | Editor | Owner |
| --- | --- | --- | --- |
| Write a new post, edit their own drafts, submit for review | ✓ | ✓ | ✓ |
| Edit any post, publish, schedule, take down, put an old revision back, decide suggested tags | | ✓ | ✓ |
| Trash and restore posts, trash pictures | | | ✓ |
| Decide agents' requests (the Inbox), connect agents, set their levels | | | ✓ |
| Settings, the brief and notes, notifications, Cloudflare, announcing, subscribers | | | ✓ |
| Invite, change a role, remove someone | | | ✓ |

- A writer's own post is one whose first revision they saved. Once it is live or scheduled, a change to it is an
  editor's or the owner's: a writer who tries is told so, plainly.
- Everyone reads every post in the app (the team's work is shared); only the actions above differ.
- Every write is a revision or an event naming the person who made it, by their own name.

## Inviting

1. The owner opens **Account → People**, picks **writer** or **editor**, optionally a note on who it's for, and taps
   **Invite**. The link is shown **once** (`/invite/<token>`; the server keeps only its hash), to send however they
   like.
2. The link works **once**, for **7 days**, until the owner withdraws it. It opens a page saying who invites them to
   which blog, as what, with **Sign in with Discord / Google**.
3. Signing in there accepts it: the account the person signs in with joins the blog in that role. A new person gets a
   new account named from the provider; someone already known here (the same provider account) keeps theirs. The link
   is then spent: nobody else can use it, even if it was forwarded.
4. The owner sees who accepted, when, and pending invitations with **Withdraw**.

Refused, with the reason on the page: an expired, spent or withdrawn link, judged again when the person comes back from
the provider (a link withdrawn or run out meanwhile lets nobody in). The owner's own account, or someone already on the
blog, just signs in: the link stays unspent for whoever it was meant for. Accepting needs nothing but the link: no
email, no form, no account beforehand. Owner how-to: [guides/people.md](../guides/people.md).

## People

- **Account → People** lists the owner and everyone else with their role, when they joined, and (for the owner) **Make
  editor / Make writer** and **Remove**.
- **Removing** someone takes their access away at once: their sessions end (unless they are on another blog of this
  server), and signing in again is refused, because an account on no blog doesn't sign in. Everything they wrote
  stays: posts, revisions and history under their name. They can be invited again, and come back with the same account.
- **A schedule is judged when it fires** for people too: a post an editor scheduled goes live only if they are still an
  editor (or the owner) at that moment; otherwise it becomes a draft, with the reason in History.
- Bylines name each person by their account's name; the owner's **Your name on posts** is the owner's alone.
- Notifications tell the owner what others do (a writer submitting for review, an editor publishing, someone accepting
  an invitation: `invitation_accepted`) as they tell what agents do; the owner's own doings are still never sent back.
- In the app, a writer or an editor sees Posts (with **New post**), History (editors), and Account (their name, ways in,
  sign out). An action that isn't theirs stays on screen, turned off, with the reason beside it.

## Their agents

Slice 2. A writer or an editor connects their own agent the way the owner does ([connections.md](connections.md)): the
same `/mcp` address, the consent page, starting at Propose. It is **their** agent, and never does more than they may.

| | The owner's agent | An editor's agent | A writer's agent |
| --- | --- | --- | --- |
| The highest level it works at | Full | Publish | Propose |
| Posts it changes | as its level allows | as its level allows | its writer's own: ones the writer started, or it or another of their agents started |

- **The level is the owner's to set**, as for every agent, and never above the person's cap (refused, with the reason).
  The person sees their agent on **Agents**, renames it and revokes it; they don't set its level.
- **Judged on every call:** the agent works at the lower of its level and its person's cap at that moment. An editor
  made a writer has an agent that works at Propose from its next call, and a schedule it set doesn't fire; made an
  editor again, the level the owner set comes back. `get_blog` tells the agent whose it is, its role, and its reach.
- **A person removed** takes their agents with them: revoked with their tokens. Everything the agents wrote stays, under
  the agents' names.
- A post an agent started counts as its person's: the writer edits it in the app as their own.
- The writer's-own rule covers every post change, writing its announcements included (`write_announcement`): a writer's
  agent refused on someone else's post leaves that post's announcement exactly as it was.

### Who decides a request

A request goes to whoever may do what it asks: the owner decides every one; an editor decides every one but trashing
and restoring, which stay the owner's; a writer decides none, not even their own agent's. The Inbox shows each person
exactly the requests they may decide, with whose agent asked ("Claude (Anna's agent) asks to publish…").

The owner's words are the one blog text an agent follows. A note an editor leaves when deciding reaches the agent marked
`"author": "editor"` with their name, in `get_request` and among `get_blog`'s notes: feedback on its request, never an
instruction over the owner's.

## Acceptance criteria

Behaviour, invariants and edges are checked by `MembersTests` (12), each rule turned off once to see a test fail; the
use cases wait for the live pass.

**User use cases:**
- [ ] As an owner, I invite someone to write; they sign in from the link and write drafts; nobody else can join.
- [ ] As an editor, I publish a writer's post; as a writer, I draft and submit but never publish.
- [ ] As an owner, I remove someone, and everything they wrote stays under their name.

**Behaviour:**
- [x] An invitation works once, for one account, for 7 days, and not after it is withdrawn.
- [x] A writer edits only their own drafts and submits them; publishing, scheduling, editing a live post or someone
      else's post is refused with the reason.
- [x] An editor edits any post and publishes, schedules, takes down; trashing is the owner's.
- [x] Owner-only screens and routes answer "not found" to an editor or a writer.
- [x] A writer's submit for review reaches the owner's notifications.

**Invariants:**
- [x] No way in without an invitation the owner made: an unknown account that signs in gets no blog.
- [x] Only the owner invites, changes roles and removes; no role reaches the owner's own.
- [x] Removing someone removes nothing they wrote; revisions and events keep naming them.

**Their agents (slice 2, `PeoplesAgentsTests`):**
- [x] A writer's agent drafts, edits and submits only the writer's posts; its publish waits for the owner or an editor.
- [x] A writer's agent writes announcements only on the writer's posts; a refusal stops or changes nothing (`AnnounceTests`).
- [x] The owner sets agents' levels, never above the person's cap; the person renames and revokes their own.
- [x] An editor decides agents' requests except trash and restore; a writer decides none.
- [x] An editor's note reaches the agent as an editor's, never as the owner's.
- [x] A new role applies from the agent's next call and at its schedule; removal revokes the person's agents.
- [ ] As a writer, I connect my agent from my phone and it drafts for me (live pass).

**Edges:**
- [x] A link that ran out, was used or withdrawn says so; the owner's own account and an existing member can't use one.
- [x] A removed person's open session finds no blog on its next request.
