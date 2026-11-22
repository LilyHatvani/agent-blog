# Agent connections

Status: **M1, slice 3** (2026-11-22). Levels: [decision 0001](../decisions/0001-agent-autonomy-levels.md).

## Connecting an agent

The MCP endpoint is `<PublicUrl>/mcp` (Streamable HTTP, stateless). Its OAuth server is `AgentKit.McpOAuth`
([0007](../decisions/0007-shared-kernel.md)): PKCE S256 only, opaque reference tokens stored hashed, audience pinned to
`<PublicUrl>/mcp`, discovery by RFC 9728 from the `/mcp` 401. Seeded clients: claude.ai (`claude-ai`) and Claude Code
(`claude-code`); client metadata documents from `claude.ai` and `chatgpt.com`. No dynamic client registration.

1. The client calls `/mcp`, gets a 401 naming the protected resource metadata, and starts an authorization.
2. The consent page needs the owner signed in. Without a session it sends the browser to `/sign-in` and back.
3. The owner picks **which blog** (only blogs they own are offered) and a label, then Allow. The page says the
   connection starts at **Propose** and what that means.
4. The connection is created at Propose, bound to the grant and the blog. A forged form naming another blog is refused
   and the grant revoked; no code is issued.

One issuer serves the whole instance, hosted or self-hosted ([0005](../decisions/0005-hosted-and-self-hosted.md)): the
blog comes from the connection, never from the URL.

## Every call

The gate resolves the bearer to its connection on every request, uncached: the grant must be live (the package checks
it) and the connection unrevoked and bound to that grant. The tools read the connection's blog and level from that
resolution, so a level the owner changes applies to the agent's very next call.

## The owner's controls

| Endpoint | Does |
| --- | --- |
| `GET /api/connections` | the owner's connections, with blog, label, level and what it means, client, last used |
| `PATCH /api/connections/{id}` `{ "level", "confirm_full" }` | sets the level. Raising to Full without `confirm_full: true` is refused (409 `confirm_full`): the admin page asks once, in plain words, and says how to undo |
| `DELETE /api/connections/{id}` | revokes it, then revokes the grant and every token under it, so a refresh can't bring it back |

These need the owner's session and the `X-Blog-Request` header ([auth.md](auth.md)). An agent's bearer never reaches
them, and no MCP tool changes a level or a connection. Another account on the instance gets "not found" for a
connection on a blog it doesn't own.

## Events

`connection_created`, `connection_level_changed` (`from`, `to`), `connection_revoked`, each with the blog.
