# Who wrote each post: bylines

Under every post the blog says who wrote it and who let it out, such as "By Lenka · through MCP". Readers see the
**person**, with the **channel**: an agent's words are credited to you (or to whoever owns the agent), "through MCP".
The agent's own name never shows on the page. You don't type the byline: it is worked out from the post's history (who
saved its revisions, who published it, who approved later changes), so it can't claim more than happened. The rules:
[specs/bylines.md](../specs/bylines.md).

## What readers see

| What happened | Under the post |
| --- | --- |
| You wrote it | By Lenka |
| Your agent wrote it and you published it, approved its request, or scheduled it | By Lenka · through MCP |
| You wrote part and your agent the rest | By Lenka · partly through MCP |
| Another person's agent wrote it and you approved | Written by Anna · through MCP · approved by Lenka |
| An agent published it on its own (at Publish or Full) | By Lenka · published through MCP |
| An agent then changed the live post without asking you | … · changed since through MCP |

Some things you do aren't writing, so they don't put your name among the writers: putting an old revision back, and
adding or dismissing a tag an agent suggested. Approving an agent's request with your own edits is writing.

The same people go into the feeds (RSS `dc:creator`, Atom `author`). To search engines the people are `author` (a
`Person` each) and the agents are `contributor` (a `SoftwareApplication` each) — structured data only, never the visible
author.

## Check what a post will say

Open the post in the app. Under its title: **Readers see: …**. A draft says "not published yet" at the end.

## Names

- **Your name**: **Account**, **This blog**, **Your name on posts**. Empty, it's your account's name. Tap **Save**.
- **An agent's name**: **Agents**, on the agent's card, **Name on posts**. Until you set one, it's the name its app gave
  when it connected (such as "Claude"). Tap **Save**. This name shows only in the app's **History** and in structured
  data — readers see the person, "through MCP", never the agent's name. An agent you revoke keeps its person on what it
  wrote.

Both are up to 60 characters on one line. An agent can read its own name (`get_blog` says `your_name_on_posts`) but
can't change it, or yours.

## Author pages

Each name under a post links to that person's author page (`/author/anna`): everything they and their agents wrote
that is live, newest first. With other people writing on your blog ([people.md](people.md)), their agents' words are
credited to them: "Written by Anna · through MCP · approved by you". The agent's name is not on the page or the author
page. **Account** shows your own page's address. More: [specs/authors.md](../specs/authors.md).

**Your link:** under **Account**, **Your link** takes one address, such as your Echo of Dreams profile. It shows on
your author page and, in the Midnight theme, on the card under each of your posts. Each person sets their own.

## Turning bylines off

**Account**, **This blog**, untick **Show who wrote each post**, **Save**. Posts then show no names, the feeds name
the blog, and search engines get the blog as the author. Nothing is lost: tick it again and every post's byline is
back, worked out from the same history.
