# Export everything, and import it elsewhere

Status: **built** (2026-11-23). ROADMAP M5's "export everything and import it into a self-hosted copy"; the hosted
service itself waits on 0005's open points. Code: `Export/BlogExport.cs` on
[AgentKit.Export](https://github.com/LilyHatvani/agent-kit/tree/develop/src/AgentKit.Export).

## The export

`GET /api/blogs/{id}/export`, the owner only (Account → **Export everything**): one zip, `{slug}-{date}.agent-blog.zip`.

- **`export.json`**: format `agent-blog-export/1`, a header (the blog, its owner, when, from where), each table's row
  count, then the rows, table by table, from **one read-only snapshot**.
- **`subscribers.json`**: confirmed subscribers, addresses opened (they're sealed under this instance's key).
- **`media/{id}`**: each picture's file, as the bucket holds it.

**What's in it** is an allowlist (`BlogExport.Plan`): the blog, its people and authors, tags and categories, posts and
every revision, pictures, agents' connections (as history), approval requests (posts' and the blog's), owner notes, old
addresses, the EoD import's author map, and the whole event log. **What isn't**, each with its reason in the plan:
sign-ins (identities, passkeys, sessions) and agents' tokens (per instance), every sealed secret and what needs one
(notification channels, announcing accounts, Cloudflare), open invitations, and work queues. `BlogExportTests` checks
the plan against the live schema: a migration that adds a column or table fails the test until someone decides,
so nothing new leaks, or goes missing, by default.

## The import

`POST /api/blogs/{id}/import-export` with the zip as the body (Account → Import → **Import an Agent Blog export**), the
owner only, up to 2 GB.

- **Into a blog with nothing in it yet**: no posts, pictures, tags or people besides the owner (an agent connection is
  fine). Typical: set up the new instance with a passkey, then import.
- **The exported owner becomes this owner**, everywhere they're named (revisions, history, notes). Everyone else
  arrives as an account with their name and author page, waiting to be claimed: People → **Invite to claim**.
- **Ids are kept**, so every post's, author's and picture's address is the same as before.
- **Agents' connections arrive revoked** (their tokens didn't come): History shows what they did, and an agent connects
  again.
- **History comes over marked imported** (`event.imported`): it reads as before (bylines, History), but the workers
  that act on new events walk past it, so nothing is notified, announced, emailed or pinged again.
- **It says what's wrong before changing anything**: not a zip, another format, a cut or altered document (the counts
  don't match), a blog that isn't empty, subscribers without a master key here (set `Blog__Secrets__MasterKey` first),
  pictures without a bucket here.
- Pictures go to the bucket first; if the database write then fails, importing again writes them again.

## Acceptance criteria

Checked by `BlogExportTests` (4), each rule turned off once to see a test fail, and `AgentKit.Export`'s own tests (3,
including an exact round trip of every column type).

- [x] The plan decides about every table and column of the schema.
- [x] A blog with posts, revisions, a picture, a writer, an agent's request, a note and a subscriber arrives whole on
      another instance: same ids and addresses, the picture served, the writer waiting to be claimed, the subscriber kept.
- [x] The exported owner is the new owner everywhere; the agent's connection is revoked with no grant.
- [x] Nothing is announced, notified, emailed or pinged for imported history, on an instance set up to do all four;
      something new afterwards is announced as ever.
- [x] No sign-in, token or secret is in the export; only the owner exports or imports.
- [x] A wrong file, another format, a non-empty blog and subscribers without a master key are refused before anything changes.
