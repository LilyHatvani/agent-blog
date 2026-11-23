# Who wrote each post: bylines

Under every post the blog says who wrote it and who let it out, such as "Written by Claude · approved by Lenka". You
don't type it: it is worked out from the post's history (who saved its revisions, who published it, who approved
later changes), so it can't claim more than happened. The rules: [specs/bylines.md](../specs/bylines.md).

## What readers see

| What happened | Under the post |
| --- | --- |
| You wrote it | By Lenka |
| An agent wrote it and you published it, approved its request, or scheduled it | Written by Claude · approved by Lenka |
| An agent and you both wrote it | Written by Claude and Lenka · approved by Lenka |
| An agent published it on its own (at Publish or Full) | Written and published by Claude |
| An agent then changed the live post without asking you | … · changed since by Claude |

Some things you do aren't writing, so they don't put your name among the writers: putting an old revision back, and
adding or dismissing a tag an agent suggested. Approving an agent's request with your own edits is writing.

The same names go into the feeds (RSS `dc:creator`, Atom `author`) and to search engines (people as `author`, agents
as `contributor`, a `SoftwareApplication`).

## Check what a post will say

Open the post in the app. Under its title: **Readers see: …**. A draft says "not published yet" at the end.

## Names

- **Your name**: **Account**, **This blog**, **Your name on posts**. Empty, it's your account's name. Tap **Save**.
- **An agent's name**: **Agents**, on the agent's card, **Name on posts**. Until you set one, it's the name its app gave
  when it connected (such as "Claude"). Tap **Save**. An agent you revoke keeps its name on what it wrote.

Both are up to 60 characters on one line. An agent can read its own name (`get_blog` says `your_name_on_posts`) but
can't change it, or yours.

## Author pages

Each name under a post links to that person's author page (`/author/anna`): everything they and their agents wrote
that is live, newest first. An agent's name links to the page of the person whose agent it is. With other people
writing on your blog ([people.md](people.md)), their agents carry their name: "Written by Anna's Claude · approved by
you". Your own agents keep their plain names. **Account** shows your own page's address. More:
[specs/authors.md](../specs/authors.md).

**Your link:** under **Account**, **Your link** takes one address, such as your Echo of Dreams profile. It shows on
your author page and, in the Midnight theme, on the card under each of your posts. Each person sets their own.

## Turning bylines off

**Account**, **This blog**, untick **Show who wrote each post**, **Save**. Posts then show no names, the feeds name
the blog, and search engines get the blog as the author. Nothing is lost: tick it again and every post's byline is
back, worked out from the same history.
