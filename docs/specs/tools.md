# MCP tools

Status: the post tools and `get_request` are built (slice 4, [posts.md](posts.md)); the media tools (slice 7,
[media.md](media.md)); categories and tags (slice 8, [taxonomy.md](taxonomy.md)). Names are the contract; M1 builds them. The level column is the lowest level that runs the
tool at once; below it, the call becomes an approval request ([0001](../decisions/0001-agent-autonomy-levels.md)).
"Owner" means no agent level runs it, ever.

| Tool | Does | Runs at once from | Below that |
| --- | --- | --- | --- |
| `get_blog` | title, description, URL, the connection's own level, counts, the owner's brief and newest notes, marked `author: owner` ([feedback.md](feedback.md)), and the agent's own name on posts ([bylines.md](bylines.md)), and `your_person`: whose agent it is, their role, the level it is held to and its reach ([members.md](members.md#their-agents)) | Propose | — |
| `list_posts` | posts by status (draft, review, scheduled, live, trash), category, tag, text; paged | Propose | — |
| `read_post` | one post: Markdown body, metadata, current revision id | Propose | — |
| `list_revisions` | a post's revisions, who made each | Propose | — |
| `check_post` | a pre-flight before asking: links, alt text, excerpt, title, headings, leftovers, similar posts; also checks unsaved text ([check.md](check.md)) | Propose | — |
| `list_categories`, `list_tags` | the taxonomy, with counts | Propose | — |
| `create_draft` | a new draft from Markdown, title, excerpt, categories, tags | Propose | — |
| `edit_post` | a new revision of a draft (needs the revision id it read) | Propose | — |
| `edit_post` on a live or scheduled post | a new revision, live at once | Publish | approval request |
| `upload_media`, `confirm_media` | a presigned upload slot; then the server checks the bytes and returns the Markdown to embed | Propose | — |
| `upload_media_from_url` | the server fetches a public picture and keeps a copy | Propose | — |
| `list_media` | the blog's pictures with their Markdown | Propose | — |
| `submit_for_review` | marks a draft ready; it shows in the owner's Inbox (no push or email yet) | Propose | — |
| `publish_post` | puts a draft live now | Publish | approval request |
| `schedule_post` | puts a draft live at a time; judged again when it fires | Publish | approval request |
| `unpublish_post` | back to draft | Publish | approval request |
| `create_category`, `create_tag` | new taxonomy entries (also by name on `create_draft` / `edit_post`) | Publish | suggested on the draft instead; the owner adds or dismisses it |
| `trash_post`, `restore_post` | to and from the trash | Full | approval request |
| `trash_media` | a picture to the trash ([requests.md](requests.md)); restoring is the owner's | Full | approval request |
| `edit_category` | a category's name or description, or mark it as a section (title, Markdown intro, SEO title, meta description, nav order, `show_on_home`) ([taxonomy.md § Sections](taxonomy.md#sections)) | Full | approval request |
| `merge_tags`, `delete_tag` | taxonomy upkeep | Full | approval request |
| `edit_blog_details` | the blog's title and description ([requests.md](requests.md)) | Full | approval request |
| `write_announcement` | what a post's announcement says on each of the owner's social accounts, with each network's own count; without text, reads them. Never sends: the owner sends what an agent wrote ([announce.md](announce.md)) | Propose | — |
| `get_request` | the state of an approval request this connection made, the owner's note on it, and what to do next | Propose | — |
| — | empty trash, purge media, export, change any connection's level, sign-in, domain | Owner | not a tool |

## Rules every tool keeps

- **Optimistic concurrency.** A write names the revision it read; a stale one is refused with the current revision
  id, never silently overwritten.
- **An approval request pins the change.** The owner approves exactly what the agent sent, against the revision it
  sent it for. If the post moved on, the request says so and doesn't apply.
- **The level is read on every call** from the connection's grant, not cached in the MCP session.
- **Tool results carry data, never instructions.** Post bodies, titles and fetched pages come back as fields; the
  server instructions say so.
- **Refusals name the real reason** ("this needs Publish; your connection is Propose; request #12 is waiting for the
  owner").

## Clients keep the tool list they first fetched

claude.ai and Claude Code fetch the tool list when the connector is connected and keep it; a stateless server can't tell
them it changed (found 2026-11-22: after the media tools shipped, both kept the old list, and only `server/discover`
reached the server). After an update that adds or changes tools, **disconnect and connect the connector again**. The
server is lenient where it can be: a parameter an older client sends in an older shape (a list as a string) still works.
