# People: writers and editors

Your blog can have people who write with you. Nobody joins on their own: there is no sign-up. You invite each person
with a link, and they sign in from it with Discord or Google. The rules behind it: [specs/members.md](../specs/members.md).

## Who may do what

| | Writer | Editor | You (the owner) |
| --- | --- | --- | --- |
| Write a new post, edit their own drafts, submit for review | ✓ | ✓ | ✓ |
| Edit any post, publish, schedule, take down, put an old revision back | | ✓ | ✓ |
| Trash and restore posts | | | ✓ |
| Decide agents' requests to publish, schedule, take down or edit a live post (the Inbox) | | ✓ | ✓ |
| Decide agents' requests to trash or restore | | | ✓ |
| Connect their own agent, rename it, revoke it | ✓ | ✓ | ✓ |
| Set any agent's level | | | ✓ |
| Settings, notifications, announcing, notes to agents | | | ✓ |
| Invite, change roles, remove | | | ✓ |

Everyone on the blog can read every post in the app. A writer's own post is one they started. Once it is live or
scheduled, only an editor or you change it.

## Invite someone

1. Open **Account**, then **Who writes for this blog** (People).
2. Pick **Writer** or **Editor**. Under **Who it's for**, you can add a note only you see, such as "Anna, for the travel
   posts".
3. Tap **Invite**. The link shows **once**: tap **Copy** and send it however you like (a message, an email).

The link works **once** and for **7 days**. Whoever signs in from it first joins, so send it only to the person it is
for. If it went to the wrong place, tap **Withdraw** under **Waiting**. A link that was used, ran out or was withdrawn
says so, and lets nobody in.

## What the person you invited sees

They open the link and see who invites them, to which blog, and as what. They tap **Sign in with Discord** or **Google**.
That's all: no form, no email address, no password. The app opens on **Posts**, where **New post** starts a draft.

- A writer writes, then taps **Submit for review**. You (or an editor) publish it. Your notifications tell you when a
  writer submits ("An agent or a writer submits a draft for review", on by default).
- An editor publishes, schedules and edits any post. **Move to trash** shows to them, turned off, with the reason.
- Readers see each person by their account's name (their Discord or Google name), in bylines and feeds. Each name
  links to the person's author page, `/author/their-name`, with everything they wrote ([bylines.md](bylines.md)).

## Their agents

A writer or an editor can connect their own agent (Claude, ChatGPT…) the same way you do: **Agents** shows the address
to add, and they approve it on the page that opens. It's **their** agent:

- It starts at **Propose**, and you set its level on **Agents**, never higher than its person may go: a writer's agent
  stays at Propose, an editor's goes at most to **Publish**. Full is only for your own agents. A level above that is
  turned off, with the reason on it.
- A writer's agent changes only that writer's posts (the ones they or their agents started). Asking to publish one
  becomes a request in the Inbox.
- They see their own agents on **Agents**, rename them and revoke them. You see every agent, with whose it is.
- If you make an editor a writer, their agent drops to Propose from its very next call (the card says "It works at
  Propose while Ben is a writer"), and a post it scheduled doesn't go live. Make them an editor again and its level
  comes back.

**Who decides a request:** you decide every one. An editor decides every request except trashing and restoring, which
stay yours. A writer decides none, not even their own agent's. The Inbox shows each request with whose agent asked:
"Claude (Anna's agent) asks to publish…".

**Notes:** when an editor declines or asks for changes with a reason, the agent gets it marked as the editor's words,
with their name. Only your words (the brief, your notes, your reasons) are instructions an agent follows.

## Change a role, remove someone

On **People**, each person has **Make editor** (or **Make writer**) and **Remove**.

- **Removing** someone ends their access at once: their open app goes to the sign-in page, and signing in again finds no
  blog. Their agents are revoked too. Everything they and their agents wrote stays, under their names: posts,
  revisions, history.
- A post an editor scheduled goes live only if they may still publish when it is due. Make them a writer or remove them,
  and the post becomes a draft again, with the reason in History.
- You can invite someone again after removing them. They come back with the same account, and their old posts are still
  theirs.

## If something goes wrong

- "That invitation has run out, was used, or was withdrawn": make a new one.
- "That account isn't on this blog": they signed in with an account that wasn't invited, or with a different one than
  they joined with. They sign in with the one they used on the link, or you invite them again.
- You open your own invitation link: you just sign in as yourself, and the link stays unused for the person it is for.
