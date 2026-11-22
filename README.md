# Agent Blog

> Working name. Rename before the repository goes public.

A self-hosted blog you write **with** your AI. Connect claude.ai, ChatGPT, Claude Code or any MCP client; the agent
drafts, edits and files posts; you read them on your phone and press publish. Or, if you want, you let it publish too.

- **Your AI is a writer on the blog, not a plugin bolted on.** The MCP door is the product's front door, next to the
  editor, not an afterthought.
- **You choose how much it may do, per connection.** *Propose* (it drafts, you publish), *Publish* (it ships its own
  posts) or *Full* (it runs the blog's content). See [decision 0001](docs/decisions/0001-agent-autonomy-levels.md).
- **Nothing an agent does is unrecoverable.** Every save is a revision with the agent's name on it, trash is a bin,
  and one click puts back what an agent changed. At every level.
- **One service, Postgres and an S3-compatible bucket.** Runs on Railway with one click, or anywhere Docker runs.

**Status: pre-alpha.** M1 is done: owner sign-in, the MCP door (claude.ai, ChatGPT, Claude Code) with per-connection
levels, posts over MCP with approval requests, the public pages with RSS, Atom and a sitemap, and the owner's phone app
at `/admin`. M2 is built (the brief and feedback, editing on the phone, notifications, pictures through Cloudflare,
`check_post`, social cards, announcing, honest bylines, subscribe by email), and M2b has begun: writers and editors by
invitation. Owner how-tos: [docs/guides/](docs/guides/README.md). What's next: [docs/ROADMAP.md](docs/ROADMAP.md).

## Read first

| | |
| --- | --- |
| [docs/VISION.md](docs/VISION.md) | what this is, who it's for, the principles |
| [docs/decisions/](docs/decisions/README.md) | every design decision, with the question it answered |
| [docs/specs/tools.md](docs/specs/tools.md) | the MCP tools and which autonomy level each needs |
| [docs/ROADMAP.md](docs/ROADMAP.md) | milestones with acceptance criteria |
| [CLAUDE.md](CLAUDE.md) | the project charter, for people and for AI contributors |

## Run it locally

```bash
docker compose up --build      # Postgres + the server on http://localhost:8080
curl localhost:8080/healthz
```

Or without Docker: `dotnet run --project server/AgentBlog.Server` (.NET 10 SDK).

## Licence

MIT. See [LICENSE](LICENSE).
