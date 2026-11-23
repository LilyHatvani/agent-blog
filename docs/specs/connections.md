# Agent connections

Status: **M1, slice 3** (2026-11-22); anyone on the blog connects their own agent since **M2b, slice 2** (2026-11-23,
[members.md § Their agents](members.md#their-agents)). Levels: [decision 0001](../decisions/0001-agent-autonomy-levels.md).

## Connecting an agent

The MCP endpoint is `<PublicUrl>/mcp` (Streamable HTTP, stateless). Its OAuth server is `AgentKit.McpOAuth`
([0007](../decisions/0007-shared-kernel.md)): PKCE S256 only, opaque reference tokens stored hashed, audience pinned to
`<PublicUrl>/mcp`, discovery by RFC 9728 from the `/mcp` 401. Seeded clients: claude.ai (`claude-ai`) and Claude Code
(`claude-code`); client metadata documents from `claude.ai` and `chatgpt.com`. No dynamic client registration.

1. The client calls `/mcp`, gets a 401 naming the protected resource metadata, and starts an authorization.
2. The consent page needs someone signed in: the owner, an editor or a writer. Without a session it sends the browser
   to `/sign-in` and back.
3. They pick **which blog** (only blogs they are on are offered) and a label, then Allow. The page says the connection
   starts at **Propose** and what that means; on a blog they don't own, also that it is their agent, held to what they
   may do, with its level the owner's to set.
4. The connection is created at Propose, bound to the grant, the blog and the person who allowed it (`created_by`). A
   forged form naming a blog they aren't on is refused and the grant revoked; no code is issued.

One issuer serves the whole instance, hosted or self-hosted ([0005](../decisions/0005-hosted-and-self-hosted.md)): the
blog comes from the connection, never from the URL.

## Every call

The gate resolves the bearer to its connection on every request, uncached: the grant must be live (the package checks
it), the connection unrevoked and bound to that grant, and its person still on the blog. The tools read the blog, the
person and the level the agent **works at** from that resolution: the lower of the connection's level and its person's
cap (owner Full, editor Publish, writer Propose). A level the owner changes, or a person's new role, applies to the
agent's very next call; a scheduled post is judged the same way when it fires.

## Controls

| Endpoint | Who | Does |
| --- | --- | --- |
| `GET /api/connections` | anyone signed in | every connection on a blog they own; elsewhere only their own agents. Each with blog, label, level and what it means, client, last used, whose agent it is (`person`), its `cap`, the level it `works_at`, and `can_set_level` |
| `PATCH /api/connections/{id}` `{ "level", "confirm_full" }` | the owner | sets the level, never above the person's cap (400 `above_person`). Raising to Full without `confirm_full: true` is refused (409 `confirm_full`): the admin page asks once, in plain words, and says how to undo |
| `PATCH /api/connections/{id}` `{ "byline_name" }` | the owner, or the agent's person | its name on posts ([bylines.md](bylines.md)) |
| `DELETE /api/connections/{id}` | the owner, or the agent's person | revokes it, then revokes the grant and every token under it, so a refresh can't bring it back |

These need a session and the `X-Blog-Request` header ([auth.md](auth.md)). An agent's bearer never reaches them, and no
MCP tool changes a level or a connection. Anyone else gets "not found". Removing a person from the blog revokes their
agents there, with their tokens.

## Events

`connection_created` (actor: the person who allowed it), `connection_level_changed` (`from`, `to`), `connection_revoked`
(`why: person_removed` when it went with its person), each with the blog.
