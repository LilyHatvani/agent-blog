# Requests beyond posts

Status: **M3, built** (2026-11-23). An agent below its level's reach asks instead of acting, and the owner decides in
the Inbox ([decision 0001](../decisions/0001-agent-autonomy-levels.md)). Posts' requests are in [posts.md](posts.md);
this page is the rest: **pictures** and **the blog's title and description**. Code: `Blogs/BlogRequests.cs`,
migration `0021_blog_requests.sql`.

## The tools

| Tool | At Full | Below Full |
| --- | --- | --- |
| `trash_media` `{ media_id }` | the picture goes to the trash at once (readers stop seeing it in every post) | a request |
| `edit_blog_details` `{ title?, description? }` | changed at once, under the agent's name | a request |

Restoring a picture and emptying the trash stay the owner's: no tool does either. An agent whose person is an editor or
a writer is held below Full ([members.md](members.md#their-agents)), so theirs are always requests.

## The request

- **Pinned to what the agent saw:** a picture's request pins it in use; a settings request pins the title and
  description as they were. If that changed before the owner decides (the picture went to the trash some other way,
  someone renamed the blog), the request is **stale**: the Inbox says so, Approve is off, and approving anyway applies
  nothing. The same rule as posts: an approval never overwrites a change made after the agent asked.
- **Only the owner decides** (the Inbox's "The blog" section, `GET /api/blog-requests`, `POST
  /api/blog-requests/{id}/approve|decline` with an optional note). Editors decide posts' requests, not these: trashing
  and the blog's identity are the owner's.
- Approving a settings change applies it **under the agent's name** (`blog_settings_changed`, actor the connection);
  the decision is its own event naming the owner. A decline can carry a note, which reaches the agent marked
  `"author": "owner"` in `get_request`.
- Notifications tell the owner, as for posts ("Claude asks to trash a picture").

## Undo

Both are reversible from History ([history.md](history.md)): a picture an agent trashed is restored; a title or
description an agent changed is put back, while it still stands as the agent left it.

## Acceptance criteria

Checked by `BlogRequestTests` (5), each rule turned off once to see a test fail.

- [x] Below Full, trashing a picture waits for the owner, and approving trashes it; once.
- [x] At Full, the agent trashes a picture, and History restores it.
- [x] A settings request applies what the agent asked, or turns stale when they changed since; a decline's note reaches the agent.
- [x] At Full, settings change at once, and History puts them back while they still stand.
- [x] Only the owner decides; empty or oversized changes are refused.
