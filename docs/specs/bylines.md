# Honest bylines

Status: **M2, slice 8, built** (2026-11-22). Every post says who wrote it and who let it out: "Written by Claude · approved by
Lenka". It is derived from what the blog already records (the revisions, the publication, the approvals), never typed
in, so it can't say more than happened. Code: `Posts/Bylines.cs`; shown by the themes, the feeds and the structured
data.

## What it says

From the post's history:

- **Writers**: everyone who saved a revision with new words, in the order they first did. Not a writer for: putting an
  old revision back, accepting or dismissing a suggested tag. Approving an agent's edit with changes of your own is
  writing.
- **The publication**: the post's first `post_published`. The owner let it out when they published it, approved the
  request, or set the schedule; otherwise the agent published it on its own (at Publish or Full, or on a schedule it
  set).
- **Changed since**: an agent's revisions after the publication that the owner didn't approve. Approved edits and the
  owner's own changes aren't mentioned.

| History | Byline |
| --- | --- |
| Only the owner wrote | By Lenka |
| An agent wrote, the owner let it out | Written by Claude · approved by Lenka |
| An agent and the owner wrote, the owner let it out | Written by Claude and Lenka · approved by Lenka |
| An agent wrote and published it on its own | Written and published by Claude |
| Writers include others, an agent published it on its own | Written by Lenka and Claude · published by Claude |
| Then an agent changed the live post without approval | … · changed since by Claude |
| Not live yet (the app's preview) | Written by Claude · not published yet |

Names: the owner's **Your name on posts** (Account → This blog; their account name until set), and each agent's **Name
on posts** (Agents; the name its client gave when it connected, such as "Claude", until set). Three or more: "A, B and
C".

## Where it shows

- **The post page**, under the date, in every theme (`post.byline`; `post.credits` lists each name with `kind`
  `person` or `agent` and `role` `wrote`, `approved`, `published`, `changed`, for themes that want more). Since M2b
  slice 3 each name links to its person's author page, and another person's agent goes by their name too ("Anna's
  Claude"): [authors.md](authors.md).
- **Feeds**: RSS `dc:creator` (each writer) and Atom `author` (each writer) with `contributor` for an approver who
  didn't write.
- **Structured data**: `author` is the people (a `Person` each with its author page as `url`, the approver included); agents are `contributor`, a
  `SoftwareApplication` each. A post no person wrote or approved has the blog as its `author`.
- **The app**: a post shows "Readers see: …". **get_blog** tells an agent its own name on posts.

**Show who wrote each post** (Account → This blog, on by default) turns all of this off for the blog: the page shows no
byline, feeds name the blog, and structured data goes back to the blog as author. Turning it off is the owner's call,
said plainly in the app.

## Acceptance criteria

Behaviour, invariants and edges are checked by `BylineTests` (8), each rule turned off once to see a test fail; the use
cases wait for the live pass.

**User use cases:**
- [ ] As a reader, I see under each post who wrote it and whether a person approved it.
- [ ] As an owner, I choose my name and each agent's name on posts, or turn bylines off.

**Behaviour:**
- [x] Each row of the table above comes out as written, from the history alone.
- [x] Putting a revision back and accepting a suggested tag don't make the owner a writer.
- [x] An approved edit after publication adds no "changed since"; an unapproved agent edit does.
- [x] Feeds and structured data name the same people and agents as the page.

**Invariants:**
- [x] No agent path sets a byline, a name on posts, or the switch.
- [x] Names are escaped wherever they appear (HTML, XML, JSON-LD).

**Edges:**
- [x] Bylines off: no byline on the page, the blog as feed author and structured-data author.
- [x] A revoked agent keeps its name on what it wrote.
