# Owner sign-in

Status: **M1, slice 2** (2026-11-22). Agents sign in separately, over MCP OAuth ([connections.md](connections.md)).

## Providers

**A passkey**, always on, needing nothing configured (§ Passkeys). A provider the owner trusts, set in the app (§ A trusted provider). And Google and Discord, through `AgentKit.ProviderSignIn` ([0007](../decisions/0007-shared-kernel.md)). The package runs
the round trip (state, PKCE, nonce, the code exchange, Google's ID token) and hands over a verified
**(provider, provider account id)**. That pair is the only key: never an email. Each provider is offered when its client
id and secret are both set:

| Variable | |
| --- | --- |
| `Blog__PublicUrl` | the origin the browser sees; the redirects are built from it, never from the request |
| `Blog__Auth__Google__ClientId`, `__ClientSecret` | Google Cloud → Credentials → OAuth client ID (Web), redirect `<PublicUrl>/api/auth/google/callback` |
| `Blog__Auth__Discord__ClientId`, `__ClientSecret` | Discord Developer Portal → OAuth2, redirect `<PublicUrl>/api/auth/discord/callback` |

## A trusted provider (OpenID Connect)

Status: **built** (2026-11-24). Code: `Auth/Oidc.cs`, migration `0026_oidc_sign_in.sql`; the app's **Account → Sign-in
and comments**. Owner how-to: [guides/sign-in-provider.md](../guides/sign-in-provider.md). Tests: `OidcTests` (15) against
`OidcStub`, an OpenID Connect provider in the test process.

The owner lets people sign in with an account they have somewhere else: any OpenID Connect issuer (Echo of Dreams' for
its blog, an Authentik or Keycloak at home, a company's). Google and Discord stay environment settings; this one is the
owner's, set in the app.

### The setting

`PUT /api/blogs/{blog}/sign-in/oidc` `{ issuer, client_id, client_secret, display_name }`, `GET`, `DELETE`. Owner only:
an editor or writer finds nothing (404), and no agent tool or approval request reaches it.

- **The issuer** is an https address with no query, fragment or credentials. Saving fetches its discovery document
  (`{issuer}/.well-known/openid-configuration`) and refuses, with the reason, unless the document's `issuer` is
  **exactly** the one given (a trailing slash counts) and its authorization, token and keys addresses are https.
- **The client secret** is sealed like every other secret here (`AgentKit.Secrets`, `blog_secret`, associated data
  naming the blog and `oidc_client_secret`), so it needs `Blog__Secrets__MasterKey` (409 `unavailable` without it). It
  is write-only: `GET` answers `secret_set`, never the value, and a `PUT` without one keeps the stored one. The
  `blog_settings_changed` event records the issuer, client id and name before and after, and `secret_changed`, never the
  secret.
- **The name** goes on the button: "Sign in with Echo of Dreams".
- `GET` also answers `redirect_uri`, the address to register at the provider: `<Blog__PublicUrl>/api/auth/oidc/callback`.
- **Hosted instances** (`Blog__Hosting__Domain`) refuse it (409): the sign-in page is shared by every blog there, and one
  blog's owner must not decide who signs in on another's. Self-hosted, the instance's blog's provider is the one offered.

### The round trip

The authorization code flow with PKCE S256, the same shape as `AgentKit.ProviderSignIn`'s Google one, which can't take a
provider added at run time (decision 0007's ledger lists the copy).

- `GET /api/auth/oidc/start` begins a plain sign-in. Setup, invitations (`POST /api/auth/invite` with
  `provider: "oidc"`) and linking from Account (`POST /api/auth/identities/oidc`) begin theirs the same way the other
  providers do. These literal routes answer before the package's `/api/auth/{provider}/…`, which 404 an unknown provider.
- Each sign-in gets a random state, PKCE verifier and nonce, kept in the package's `provider_signin_state` (only the
  state's SHA-256 is the key) for 10 minutes and taken once. The state is bound to the browser by a `__Host-blog_oidc`
  cookie (HttpOnly, Secure, SameSite=Lax, one copy, always deleted at the callback): another browser's callback (login
  CSRF) is refused without spending the real sign-in.
- The state row records the provider as configured when the sign-in began (a fingerprint of the blog, issuer and client
  id). A sign-in begun before the owner changed the provider, or begun for Google or Discord, is refused at this callback.
- **The exchange** sends the code, the redirect URI and the **PKCE verifier**, with the client secret as HTTP Basic
  (`client_secret_basic`, the default), or in the form when discovery offers only `client_secret_post`. A code brought
  from another sign-in fails the provider's PKCE check, and the sign-in is refused.
- **The ID token** is checked against the issuer's JWKS (cached an hour; fetched again for an unknown key id, at most
  once a minute): the signature, by a public-key algorithm only (RS, PS or ES; never `none`, never an HMAC keyed by the
  client secret), the issuer (exactly the configured one), the audience (the client id; with an `azp`, that too),
  the expiry (two minutes' skew), and the nonce of this very sign-in.
- Every call to the issuer goes through a client that reaches only public addresses and follows no redirects, with a
  10-second timeout and a 1 MB answer cap (`Blog__Auth__Oidc__AllowPrivate=true` for a provider on the self-hoster's own
  network).

### Accounts

The identity is provider `oidc`, its key the SHA-256 of the issuer and the token's `sub` (base64url; joined by a byte no
URL carries). **Never an email**: the email and name are display hints. The same `sub` at another issuer is someone
else. From there it is the existing paths: a verified identity goes to the same completion as Google and Discord, so
sign-in finds only a linked account on a blog, a signed-in member links it from Account → Ways to sign in, an invitation
is claimed with it, a re-opened setup binds it to the same owner, and the last way in can't be removed.

Removing the provider removes its button and the sealed secret. Accounts keep their `oidc` identity: it is unusable while
no provider is set, and works again if the same issuer is set again.

### Refusals

The provider being down signs nobody out: sessions are this blog's own, and a failed sign-in, link or callback never
touches the cookie of a session that exists. A failure goes back to the page it began from (sign-in, setup, or Account
for a link) with its reason, shown as a fixed sentence:

| Code | When |
| --- | --- |
| `provider_unavailable` | the issuer, its keys or its token endpoint didn't answer, or answered 5xx/429 |
| `state_invalid`, `state_expired`, `denied` | as for Google and Discord |
| `exchange_failed` | the provider refused the code (a code from another sign-in, a wrong PKCE verifier, a wrong secret) |
| `oidc_signature` | not signed by a key in the issuer's JWKS, or not by an allowed algorithm |
| `oidc_issuer`, `oidc_audience`, `oidc_expired`, `oidc_nonce` | the token's issuer, audience (client id), expiry or nonce |
| `oidc_rejected` | no ID token, or no usable `sub` |
| `oidc_off` | no provider is set (any more) |

The log says more (the token library's exception type only, never token content).

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
