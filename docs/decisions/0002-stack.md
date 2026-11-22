# 0002 — Stack: Relay's skeleton and the agent-kit packages

Status: **accepted** as the default (owner set up the repository on Relay's stack, 2026-11-22). Versions are checked
again before M1 code.

## Decision

| Part | Choice | Source |
| --- | --- | --- |
| Service | .NET 10, ASP.NET Core minimal APIs, one process (public site, owner API, MCP door, scheduler) | Relay 0005 |
| MCP | `ModelContextProtocol.AspNetCore`, Streamable HTTP, stateless sessions | Relay 0005 |
| OAuth for MCP clients | NuGet `AgentKit.McpOAuth` (OpenIddict, PKCE S256, hashed reference tokens, RFC 9728 discovery, per-call revocation) | agent-kit, extracted from Relay |
| Owner sign-in | NuGet `AgentKit.ProviderSignIn` (Google, Discord) | agent-kit |
| Data | Npgsql + Dapper, hand-written SQL, numbered run-once migrations | Relay |
| Database | Postgres 18 | |
| Uploads | S3-compatible bucket (Railway buckets, R2, MinIO), presigned uploads | Relay's evidence storage |
| Public pages | Server-rendered HTML from the service, RSS, Atom, sitemap | EoD's `BlogSeoEndpoints` (feeds) |
| Owner app | Preact + Vite PWA served by the service | Relay's PWA |
| Deployment | One Docker image; Railway template; compose for everyone else | Relay |

## Why

- The two hardest parts, an MCP door that claude.ai and ChatGPT accept and safe owner sign-in, are already
  published packages. M1 consumes them; it doesn't rebuild them.
- The owner runs this stack every day; nothing here is new to operate.
- Public pages are server-rendered because a blog's readers come from search and link previews.

## Verify before M1 code

- Current `ModelContextProtocol.AspNetCore` (nuget.org shows 2.4.0 on 2026-11-22; Relay pins 2.2.0) and its changelog.
- `AgentKit.McpOAuth` / `AgentKit.ProviderSignIn` latest (0.1.5 on 2026-11-22) and whether they assume Relay's tables.
