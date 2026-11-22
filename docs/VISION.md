# Vision

## The problem

People already write with AI. Then they copy the result into Ghost or WordPress, fix the formatting, re-upload the
pictures, and press publish. The AI did the writing and the human does the clerical work.

Blog engines that "add AI" add a text box that calls a model. That is the wrong way round: the writer's AI already
exists (claude.ai, ChatGPT, Claude Code), it already knows the writer's voice, and it has no way in.

## What this is

A blog whose front door for AI is MCP. The writer connects the AI they already use. From there the agent can:

- read the blog: posts, drafts, categories, tags, the media library;
- write: create drafts, edit them, attach pictures, suggest categories and tags;
- ship, if the owner allows it: submit for review, publish, schedule, update a live post.

The owner reads and decides on a phone. A draft from an agent shows up like one from a co-writer: what changed, a
preview, and one button.

## Who it's for

- **Writers who already draft with an AI** and want the last mile gone, with their hand on the publish button.
- **People who want an AI to run a blog end to end** (a changelog, a devlog, a niche site) and accept that trade.
  They get it, said plainly, with the undo kept.
- **Self-hosters** who want their writing in their own Postgres, not on a platform.

## Principles

1. **The owner sets the reach.** Three autonomy levels per connection: Propose, Publish, Full
   ([0001](decisions/0001-agent-autonomy-levels.md)). Propose is the default; Full is offered, never pushed.
2. **Nothing an agent does is unrecoverable.** Revisions, a trash bin, a one-click restore. That's what makes Full
   safe to offer.
3. **Readers get plain HTML.** Server-rendered pages, RSS, Atom, a sitemap. No JavaScript needed to read.
4. **One small service.** One process, Postgres, a bucket. A self-hoster's bill is a few dollars a month.
5. **Open source, MIT.** The value is the writer's own instance, not a moat around the code.

## Not in v1

- Several human authors with roles. One owner, any number of agent connections. (The seam stays open: a post's
  author is a principal, not a hard-coded owner.)
- Comments. Reading them is a moderation problem this project doesn't want on day one.
- Newsletters and memberships.
- A model call inside the server. The server never calls an AI; agents come to it.
- Themes beyond one good default and CSS variables.

## How it relates to its siblings

- **Relay** (the owner's AI session hub) gives the skeleton: a .NET service with an MCP door that claude.ai and
  ChatGPT accept, owner sign-in, a phone PWA, Railway as code.
- **EchoOfDreams' blog** gives the domain: posts with revisions, a category tree, tags, schedules judged when they
  fire, feeds and a sitemap, and the blog authoring tool pack whose "public changes wait for a click" rule is the
  Propose level.
- **Agent Ready for WP** (the sibling repository) offers the same three levels on WordPress, for people who are
  staying on WordPress.
