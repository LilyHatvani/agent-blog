# Owner sign-in

Status: **M1, slice 2** (2026-11-22). Agents sign in separately, over MCP OAuth (slice 3).

## Providers

Google and Discord, through `AgentKit.ProviderSignIn` ([0007](../decisions/0007-shared-kernel.md)). The package runs
the round trip (state, PKCE, nonce, the code exchange, Google's ID token) and hands over a verified
**(provider, provider account id)**. That pair is the only key: never an email. Each provider is offered when its client
id and secret are both set:

| Variable | |
| --- | --- |
| `Blog__PublicUrl` | the origin the browser sees; the redirects are built from it, never from the request |
| `Blog__Auth__Google__ClientId`, `__ClientSecret` | Google Cloud → Credentials → OAuth client ID (Web), redirect `<PublicUrl>/api/auth/google/callback` |
| `Blog__Auth__Discord__ClientId`, `__ClientSecret` | Discord Developer Portal → OAuth2, redirect `<PublicUrl>/api/auth/discord/callback` |

## Setup: claiming the blog

1. The operator sets `Blog__SetupToken` (32+ characters) and, optionally, `Blog__Slug` and `Blog__Title`.
2. `/setup`: paste the token, pick a provider (`POST /api/auth/setup`, JSON only), sign in there.
3. On the way back, setup creates the owner's account and the blog, binds the identity, starts a session, and **burns
   the token**: its hash is stored, and setup answers "done" for it from then on.

**Recovery:** a new `Blog__SetupToken` re-opens setup. Completing it binds the new identity to the **same** owner (the
blog and its posts are untouched) and ends that owner's other sessions, because whoever had them may be who lost the
account. A token changed or removed while a setup sign-in is at the provider binds nothing.

## Sessions

A random 256-bit cookie, `blog_session`: HttpOnly, Secure (unless the public origin is plain http, i.e. local
development), SameSite=Lax, 30 days, renewed at most once a day while used. Only its SHA-256 is stored, with the
identity that began it, so removing that identity ends its sessions. Sign out ends one; sign out everywhere ends all of
the account's.

## Ways in

A signed-in owner adds the other provider (`POST /api/auth/identities/{provider}`), which is also the way back in if
one account is lost. An identity bound to someone else is refused. The last identity can't be removed.

## Writes and CSRF

Every `/api` endpoint but the sign-in ones needs a session. Every write also needs the `X-Blog-Request` header: a
cross-site form can't set it, and a cross-site `fetch` with it needs a CORS preflight this server never grants.
SameSite=Lax is the second layer. Setup takes JSON only, for the same reason.

## Rate limits

The sign-in endpoints allow 10 requests a minute per client and 60 a minute overall (`Blog__Auth__RateLimitPerMinute`,
`__GlobalRateLimitPerMinute`). Behind a proxy the socket's address is the proxy's: set `Blog__Proxy__ForwardedForHops`
to the number of trusted proxies (Railway: 1) and the client is the entry that many places from the right of
`X-Forwarded-For`, the one the proxy appended. Entries further left are the client's own claims and are ignored.

## Pages

`/setup`, `/sign-in` and `/admin` are server-rendered, with a CSP that allows only this server's own script and style.
Error codes in their URLs are shown as fixed sentences, never echoed. The phone app (slice 6) replaces `/admin`.

## Events

`account_created`, `blog_created`, `identity_bound`, `identity_unbound`, `setup_completed`,
`setup_reopened_and_completed`. Sessions are operational state, not events.
