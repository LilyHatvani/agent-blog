# Writers by invitation

Status: **M2b, slice 1, built** (2026-11-22). A blog has one owner and, by the owner's invitation, people who write with them:
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

**Edges:**
- [x] A link that ran out, was used or withdrawn says so; the owner's own account and an existing member can't use one.
- [x] A removed person's open session finds no blog on its next request.
