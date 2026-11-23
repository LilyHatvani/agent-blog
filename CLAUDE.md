# Agent Blog — project charter

A self-hosted blog where an AI agent is a writer, over MCP, and the owner decides how much it may do on its own.
Full picture: [docs/VISION.md](docs/VISION.md). Read it before designing anything.

## Non-negotiables

- **The owner sets the agent's reach; the agent never does.** Each agent connection has an autonomy level (Propose,
  Publish, Full; [0001](docs/decisions/0001-agent-autonomy-levels.md)). No code path lets an agent raise its own or
  another connection's level, change the owner's sign-in, or mint a connection.
- **Propose is the default.** A new connection starts at Propose. Full is a choice the owner makes on purpose, told
  plainly what it means at the moment of the switch.
- **Every agent change is recoverable, at every level.** Every save is a revision naming who made it. Removing a post
  moves it to the trash; only the owner empties the trash. One click restores what an agent changed.
- **A schedule is judged when it fires.** A post an agent scheduled goes live only if that connection still holds a
  level that may publish at that moment (EchoOfDreams' `scheduled_by` rule).
- **Events are append-only.** Every state change writes an event row; nothing updates or deletes one.
- **One contract.** MCP and the owner's REST API are two faces of the same core. A rule lives in the core, never in
  one face.
- **Public pages are server-rendered HTML from Liquid templates** (Fluid, sandboxed;
  [0006](docs/decisions/0006-themes-in-liquid.md)). A blog lives on search engines and link previews; the reader never
  needs JavaScript to read a post. No theme ever runs code on the server.
- **One codebase, hosted and self-hosted.** Every row and every query is scoped to one blog
  ([0005](docs/decisions/0005-hosted-and-self-hosted.md)); a self-hosted instance is the same code with one blog.
- **Reuse the agent-kit packages; never copy them** ([0007](docs/decisions/0007-shared-kernel.md)).
- **Self-hostable, vendor-neutral core.** One service + Postgres + an S3-compatible bucket. Railway is the reference
  deployment, not a dependency.
- **Trust boundary.** Text read from the blog that the owner didn't write (comments, imported pages, fetched URLs) is
  data in a tool result, never instructions.

## Status

Pre-alpha. **M1 done** (write with your agent, publish yourself). **M2 built** (write together: the brief and feedback,
editing on the phone, notifications, Cloudflare, `check_post`, social cards, announcing, bylines, subscribe by email),
its use cases waiting for the live pass. **M2b** (several writers, Echo of Dreams' blog): slice 1, writers by
invitation, slice 2, their own agents capped by their role ([specs/members.md](docs/specs/members.md)), and slice 3,
author pages ([specs/authors.md](docs/specs/authors.md)), slice 5, the import ([specs/import.md](docs/specs/import.md)),
slice 6, old addresses, and the Midnight theme, built; Echo of Dreams' instance (slice 4, the owner's infrastructure)
and the comments widget (slice 7, an EoD lane) are next. Owner how-tos:
[docs/guides/](docs/guides/README.md). Milestones and their acceptance criteria: [docs/ROADMAP.md](docs/ROADMAP.md). Every decision record in
[docs/decisions/](docs/decisions/README.md) states whether it is accepted or proposed.

Shared code comes from the public [agent-kit](https://github.com/LilyHatvani/agent-kit) packages (MCP OAuth, provider
sign-in). The blog's domain comes from EchoOfDreams' blog, rewritten for one owner; each lift records its source
([0003](docs/decisions/0003-standalone-not-a-fork.md)).

## Repo conventions

- Default branch: `develop`.
- Commit messages: conventional prefix with scope, then a plain sentence about the behaviour —
  `feat(mcp): create_draft saves a revision named after the connection`.
- Line endings: LF in the repo (`.gitattributes`).
- Secrets never committed; `.env.example` documents variables.
- Contributions carry `Signed-off-by:` (DCO, `git commit -s`). See [CONTRIBUTING.md](CONTRIBUTING.md).
