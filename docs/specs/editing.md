# The owner edits in the app

Status: **M2, slice 2** (2026-11-22). Code: `Posts/PostsApi.cs` (`PATCH /api/posts/{id}`, `/api/preview`, decisions with
`edits`), `Posts/Posts.cs` (`OwnerEdits`), `pwa/src/components/Editor.tsx`.

## Editing a post

Posts → a post → **Edit** (any status but trash): title, summary and the Markdown body, with **Write** and **Preview**
tabs; the preview is the server's own sanitizer, exactly what readers get. Cancel and Save are always shown; Save is
off until something changed, and only the changed fields are sent. A live post says so: saving changes it for readers
at once.

The save is a revision **named after the owner**, made against the revision they opened (`base_revision`), under the
same rule as agents: if an agent saved in the meantime, the save is refused (409) and the owner's text stays in the
editor to copy.

## Approve with edits

On an Inbox card for an **edit**, a **publish** or a **schedule** that still applies: **Edit before approving** opens
the editor on the agent's proposal (an edit) or on the post as it is (a publish or schedule).

- An edit: the agent's revision is saved, named after the agent; the owner's changes are a second revision on top,
  named after the owner. Each keeps their own words; History and the revisions show both.
- A publish or schedule: the owner's changes are a revision, then the post goes live (or is scheduled) at that revision.
- Not for unpublish, trash or restore: there's no text to change. Not with Decline or Ask for changes either.
- The edits are checked before anything is written, so a bad edit leaves the request waiting and nothing half-applied.

## Acceptance criteria

**Use cases (driven in Chromium against a running server, 2026-11-22):**
- [x] As an owner, I fix a live post on my phone, preview it as readers will see it, and save; it's my revision.
- [x] As an owner, I change the title of an agent's proposed edit and approve it with my edits; the post goes live with my title.

**Behaviour (tests, `EditingTests`, 5):**
- [x] An owner edit is a revision of theirs against the one they read; a moved post is refused and nothing is overwritten; a missing base, an empty title, no CSRF, another account's post are refused.
- [x] Approving an edit with edits keeps the agent's revision (its exact words) and puts the owner's on top.
- [x] Approving a publish with edits publishes the owner's fix.
- [x] Edits on a trash request, or with a decline, are refused; an invalid edit writes nothing and the request stays waiting.
- [x] The preview is sanitized like the public pages and needs a session.

**Invariants:** each of these, turned off, fails the tests (2026-11-22): the agent's revision kept under the owner's;
edits checked before anything is written.
