# The owner's app

Status: **M1, slice 6** (2026-11-22).

A phone-first PWA at `/admin` (Preact + Vite + TypeScript in `pwa/`, no component library; Relay's stack). The server
builds it into the image and serves it (`Auth/AdminApp.cs`): its static files are public, every app screen needs a
session and otherwise goes to sign-in and back. Setup and sign-in stay server-rendered pages before it.

## Screens

| Screen | For |
| --- | --- |
| **Inbox** | agents' requests, each saying who asks what of which post. An edit shows **what changes**: title and summary before → after, and a line diff of the body with context, then the whole new version on demand. A request whose post moved on says so and can't be approved. Below: drafts submitted for review. The tab carries a count. |
| **Posts** | the blog's posts by status, then one post: a preview in the readers' HTML, what can be done now (publish, schedule, back to draft, trash with a confirmation, restore), and its revisions with who made each; any revision opens and can be put back as a new one. |
| **Agents** | the MCP address to connect, and each connection's level as a three-way choice. **Full asks once, in plain words**, what it means and what stays undoable; Cancel changes nothing. Revoke with a confirmation. |
| **History** | everything that happened on the blog, newest first, in words: who (the agent's label, the owner's name, System), what, and the post. |
| **Account** | ways to sign in (add the other provider; the last one can't be removed), sign out, sign out everywhere. |

## Rules it follows

- **The server decides, the app shows.** Every state comes from the API; a 401 sends the browser to sign-in and back;
  a failed call says why and leaves the screen as it was. The service worker caches the app shell only, never the API.
- **Every write sends `X-Blog-Request`** (auth.md § CSRF).
- **No inline script or style**: CSP `script-src 'self'; style-src 'self'`. Post previews are the server's sanitized HTML.
- **Colours are tokens**, light and dark by the device's setting.
- Dialogs start on Cancel, close on Escape and the backdrop, and are bottom sheets on a phone.

## API it reads

`/api/me`, `/api/blogs/{id}/posts`, `/api/posts/{id}` (with `html` and revisions with `by_label`),
`/api/posts/{id}/revisions/{revision}`, `/api/requests` (with `proposal`, `current`, `still_applies`),
`/api/blogs/{id}/history`, `/api/connections`, and the writes in posts.md, connections.md and auth.md.

## Verified (2026-11-22)

Against a running server with a seeded blog, in Chromium at 390 × 844 (light and dark) and 1366 × 900: every screen
renders with no console errors; approving an edit from the Inbox makes revision 3 named after the agent and updates the
preview; the Full dialog's Cancel keeps the level and its confirm sets Full; the history lists both, by name.
