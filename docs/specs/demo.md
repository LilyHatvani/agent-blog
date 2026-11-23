# The demo

Status: **built** (2026-11-23). Off unless `Blog__Demo__Enabled=true`.

A visitor sees what running a blog with an agent looks like before deploying one: `/demo` (also linked from the
sign-in page as **Try the demo**) opens the owner's app as a guest, on a sample blog with two agents, a writer, posts
in every state and two requests waiting in the Inbox. Everything shows; nothing changes.

## What keeps it harmless

- **Its own blog, hidden.** The sample is a second blog on the instance (`Demo/Demo.cs`), made the first time anyone
  opens the demo, with `hidden_at` set: the public site never serves it, and instance-wide questions look past it. The
  owner of the instance is the owner of its first blog that isn't hidden (`Accounts.OwnerSql`), so setup, recovery and
  the sign-in page's "no owner yet" never pick the guest. The guest is on no other blog, so the real blog, its posts
  and its people are not in reach (the owner API is scoped by membership).
- **Every change is refused, with the reason.** A demo session's writes to the owner API answer 403 `demo`:
  *"This is a demo: look around as much as you like, but nothing here can be changed. To write with your own AI,
  deploy your own blog."* The app shows it where the action was tried. Signing out is the one write it keeps.
  Connecting an AI from the consent page is refused the same way, so no agent ever reaches the sample.
- **One guest for everyone.** Each visit is a session of the same guest account (identity kind `demo`, which no
  sign-in page offers and nothing else binds); sign-in's rate limits apply to `/demo`.
- **Off ends it.** With the setting off, `/demo` is not found and a demo session's next request is signed out.

In the app: a **Demo** banner on every screen, and **View site** is off (the sample isn't public).

## Acceptance criteria

Checked by `DemoTests` (5), each rule turned off once to see a test fail, and in Chromium (Try the demo → Inbox →
Approve, refused beside the button).

- [x] Off by default: no `/demo`, no link.
- [x] A visitor lands in the app as the sample blog's owner: posts, requests, agents, people and history all show.
- [x] Every write is refused with the reason; the sample is unchanged afterwards; signing out works.
- [x] The guest can't connect an AI.
- [x] Opened before setup, the instance still has no owner; setup and recovery find the real owner; the public site is
      the real blog's; the guest never sees the real blog.
- [x] Turning it off ends demo sessions.
