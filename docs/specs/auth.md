# Owner sign-in

Status: **M1, slice 2** (2026-11-22). Agents sign in separately, over MCP OAuth ([connections.md](connections.md)).

## Providers

**A passkey**, always on, needing nothing configured (§ Passkeys). And Google and Discord, through `AgentKit.ProviderSignIn` ([0007](../decisions/0007-shared-kernel.md)). The package runs
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
2. `/setup`: paste the token, then either type your name and create a passkey (§ Passkeys), or pick a provider
   (`POST /api/auth/setup`, JSON only) and sign in there.
3. On the way back, setup creates the owner's account and the blog, binds the identity, starts a session, and **burns
   the token**: its hash is stored, and setup answers "done" for it from then on.

**Recovery:** a new `Blog__SetupToken` re-opens setup. Completing it binds the new identity to the **same** owner (the
blog and its posts are untouched) and ends that owner's other sessions, because whoever had them may be who lost the
account. A token changed or removed while a setup sign-in is at the provider binds nothing.

## Passkeys

Status: **built** (2026-11-23). What lets a deploy with nothing filled in be claimed: the setup token and a passkey.
WebAuthn is ASP.NET Core 10's own `PasskeyHandler` (in the shared framework, no package to audit) over a small store
(`Auth/Passkeys.cs`); no Identity tables, no passwords.

- **A passkey is one more identity**, provider `passkey`, its key the SHA-256 of the credential id (base64url, so it
  fits `provider_account_id` whatever the authenticator's id length). Setup, recovery, invitations, binding, removing,
  sessions and "removed people sign in to nothing" are the same paths as Google and Discord. The public key and
  counters are in `account_passkey`, removed with the identity.
- **Two calls per ceremony:** `begin` answers the WebAuthn options and a ceremony id; `finish` takes the browser's
  answer. The ceremony is kept server-side (`passkey_ceremony`, only its id's hash) for **one answer within five
  minutes**, and what a valid answer does is fixed at `begin` and re-checked at `finish`: the setup token still the
  configured one, the invitation still open, the session that began an "add" still live and the same one.
  - Before a session: `POST /api/auth/passkey/begin` `{ purpose: "setup" | "invite" | "sign-in", token?, name?,
    return_to? }`, `POST /api/auth/passkey/finish` `{ ceremony, credential }` → `{ redirect }` and the session cookie.
  - Signed in: `POST /api/auth/passkeys/begin` `{}`, `/finish`: add a passkey (Account → Ways to sign in).
- **The relying party is `Blog__PublicUrl`'s host**, and an answer counts only from that origin: a passkey made for
  this blog doesn't sign in anywhere else, and one signed for another site doesn't sign in here, whatever `Host` a
  request names.
- **Discoverable, no username:** sign-in asks for any of this blog's passkeys and the device offers them. The device
  knows the account by a random handle (`account.passkey_handle`), never its id. User verification is preferred.
- **Names:** a passkey has none to give, so setup and invitations ask "Your name, as readers see it on posts".
- **Losing it:** add a second way in (another passkey, or Google or Discord). With none left, a new setup token
  re-opens setup, which adds a new passkey to the same owner.
- Checked by `PasskeyTests` (9) with a software authenticator signing real ES256 answers, each rule turned off once to
  see a test fail, and in Chromium with its virtual authenticator: setup, adding a second device, signing out and back in.

## Sessions

A random 256-bit cookie, `blog_session`: HttpOnly, Secure (unless the public origin is plain http, i.e. local
development), SameSite=Lax, 30 days, renewed at most once a day while used. Only its SHA-256 is stored, with the
identity that began it, so removing that identity ends its sessions. Sign out ends one; sign out everywhere ends all of
the account's.

## Ways in

A signed-in owner adds a passkey (§ Passkeys) or the other provider (`POST /api/auth/identities/{provider}`), which is
also the way back in if one is lost. An identity bound to someone else is refused. The last identity can't be removed.

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
