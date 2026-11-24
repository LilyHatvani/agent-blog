# Honest bylines

Status: **M2, slice 8, built** (2026-11-22); **the person, through MCP** (2026-11-24). Every post says who wrote it and
who let it out, derived from what the blog already records (the revisions, the publication, the approvals), never typed
in, so it can't say more than happened. Readers see the **person**, with the **channel**: an agent's words are credited
to its person, "through MCP". The agent's own name stays in structured data and the app's History, never on the public
page. Code: `Posts/Bylines.cs`; shown by the themes, the feeds and the structured data.

## What it says

From the post's history:

- **Writers**: everyone who saved a revision with new words, in the order they first did — an agent's words counting as
  its **person's**. Not a writer for: putting an old revision back, accepting or dismissing a suggested tag. Approving an
  agent's edit with changes of your own is writing.
- **The channel**: words written through an agent are "through MCP"; a person writing some and their agent the rest is
  "partly through MCP"; a person writing alone has no channel clause.
- **The publication**: the post's first `post_published`. A person let it out when they published it, approved the
  request, or set the schedule; otherwise an agent published it on its own — "published through MCP", credited to its
  person.
- **Changed since**: an agent's revisions after the publication that the owner didn't approve — "changed since through
  MCP". Approved edits and the owner's own changes aren't mentioned.

| History | Byline |
| --- | --- |
| Only the owner wrote | By Lenka |
| The owner's agent wrote, the owner approved | By Lenka · through MCP |
| The owner wrote part, their agent the rest | By Lenka · partly through MCP |
| Another person's agent wrote, the owner approved | Written by Anna · through MCP · approved by Lenka |
| An agent published it on its own | By Lenka · published through MCP |
| Another person wrote, the owner's agent published | Written by Anna · published by Lenka through MCP |
| Then an agent changed the live post without approval | … · changed since through MCP |
| Not live yet (the app's preview), an agent drafted | By Lenka · through MCP · not published yet |

A person who let a post out is named as approving it only when they aren't one of its writers ("Written by Anna · …
approved by Lenka"); when the writer and the person who let it out are the same, it's simply theirs ("By Lenka …").
The same when an agent let it out on its own: its person is named only when they aren't one of the writers ("Written by
Anna · published by Lenka through MCP"); when they wrote it too, it's simply "published through MCP".
Several people: "A, B and C".

Names: the owner's **Your name on posts** (Account → This blog; their account name until set), and each person's account
name. An agent's **Name on posts** (Agents) is what structured data and the app's History call it; readers never see it on
the page.

## Where it shows

- **The post page**, under the date, in every theme (`post.byline`; `post.credits` lists each **person** with `kind`
  `person`, `role` `wrote`/`approved`/`published`/`changed` and `via: "mcp"` when what they did came through an agent).
  Each name links to its person's author page ([authors.md](authors.md)). The agent is not named on the page, nor on the
  author page or Midnight's creator card.
- **Feeds**: RSS `dc:creator` and Atom `author` are the **people** who wrote; Atom `contributor` is a person who let it
  out without writing. No agent is named in a feed.
- **Structured data**: `author` is the people who **wrote** (a `Person` each with its author page as `url`); everyone
  else is `contributor` — a person who let it out without writing (a `Person`), and the agents (a `SoftwareApplication`
  each) — structured data only, never the visible author. The page, the feeds and the structured data name the same
  people. A post with bylines off has the blog as its `author`.
- **The app**: a post shows "Readers see: …" (the person and the channel). **get_blog** tells an agent its own name on
  posts, which History uses.

**Show who wrote each post** (Account → This blog, on by default) turns all of this off for the blog: the page shows no
byline, feeds name the blog, and structured data goes back to the blog as author. Turning it off is the owner's call,
said plainly in the app. With bylines off, nothing else changes.

## Acceptance criteria

Behaviour, invariants and edges are checked by `BylineTests` (a test for every row of the table, on the page, in the
feeds and in the structured data), `AuthorsTests` and `MidnightThemeTests`; the use cases wait for the live pass.

**User use cases:**
- [ ] As a reader, I see under each post who wrote it and whether a person approved it.
- [ ] As an owner, I choose my name and each agent's name on posts, or turn bylines off.

**Behaviour:**
- [x] Each row of the table above comes out as written, from the history alone, on the page, in the feeds and in JSON-LD.
- [x] Putting a revision back and accepting a suggested tag don't make the owner a writer.
- [x] An approved edit after publication adds no "changed since"; an unapproved agent edit does.
- [x] Feeds and structured data name the same people as the page; the agent is a SoftwareApplication contributor only.

**Invariants:**
- [x] No agent path sets a byline, a name on posts, or the switch.
- [x] Names are escaped wherever they appear (HTML, XML, JSON-LD); the agent's name never reaches the visible page.

**Edges:**
- [x] Bylines off: no byline on the page, the blog as feed author and structured-data author.
- [x] A revoked agent keeps its person on what it wrote.
