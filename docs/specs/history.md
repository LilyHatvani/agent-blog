# History and undo

Status: **M3, built** (2026-11-23). History is the blog's event log in words, newest first (`GET /api/blogs/{id}/history`,
the app's **History** tab, for the owner and editors). Every action an agent took on posts and pictures is reversible
from there, by the owner, in one tap. Code: `Posts/Undo.cs`, `Posts/PostsApi.cs`.

## What each undo does

| The agent | Undo |
| --- | --- |
| wrote a draft (`post_created`) | moves it to the trash (from where it restores) |
| saved revision *n* (`revision_saved`, its own or an approved edit) | puts revision *n − 1* back, as a new revision |
| published or scheduled a post | back to draft |
| took a post back to draft | publishes it again |
| trashed a post | restores it |
| restored a post | moves it back to the trash |
| added a picture (`media_added`) | moves the picture to the trash |

Each event in History carries its undo for the owner (`undo: { label, why_not, done }`); `POST
/api/blogs/{id}/history/{event}/undo` does it.

## The rules

- **Judged now, against the post as it is**, the same rule as approvals: an edit undoes only while the post is still at
  that revision (otherwise someone's later words would be thrown away; open the post and pick a version instead); a
  publish undoes only while the post is live, and so on. When an undo no longer applies, the button stays, turned
  off, with the reason.
- **An undo is an ordinary owner action** through the same core: a new revision or a state change, with its own event,
  under the owner's name. Nothing is deleted; undoing an edit keeps the agent's revision in the post's history.
- **Once:** an undo is recorded (`action_undone`, naming the event it undid), and History shows that action as
  **Undone**.
- **The owner's alone.** Editors see History without undo buttons; a person's own actions have no undo here (they're
  theirs to change by hand).

## Acceptance criteria

Checked by `UndoTests` (5), each rule turned off once to see a test fail.

- [x] Undoing an agent's edit puts the earlier words back as a new revision, once.
- [x] An undo that no longer applies says why and changes nothing.
- [x] Publishing, scheduling, taking down, trashing, restoring and a new draft each have their inverse.
- [x] A picture an agent added goes to the trash.
- [x] Only the owner undoes, and only what an agent did.
- [ ] As an owner, I undo an agent's change from History on my phone (live pass).
