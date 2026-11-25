# Moving the blog to a new address

Status: **built** (2026-11-24). Code: `Auth/AddressMove.cs` (the old address's redirect, where each passkey was made),
`Auth/Members.cs` (claiming a stranded account), `/api/me` (`passkey_here`), the app's prompt
(`pwa/src/components/PasskeyHere.tsx`) and People. Owner how-to: [guides/moving-your-blog.md](../guides/moving-your-blog.md).
Tests: `AddressMoveTests` (6).

The owner moves a self-hosted blog from `https://old.example` to `https://new.example`, and everyone who writes for it
can still sign in afterwards.

## The choice: `Blog__PublicUrl` stays an environment setting

The brief allowed either a move from the app or a documented procedure with a recovery. **This ships the procedure.**
`Blog__PublicUrl` is read once, when the server starts, by three things that can't change their mind at run time:

- **The MCP door** (`AgentKit.McpOAuth`): it is the OAuth issuer, and every agent's token is pinned to `<PublicUrl>/mcp`.
- **Google and Discord** (`AgentKit.ProviderSignIn`): the redirect address each provider has registered, and the
  `__Host-` state cookie.
- **Passkeys**: the relying party is `PublicUrl`'s host; a passkey is bound to it by the browser and the device.

A move changes all three whatever the app does, and the first two need the operator anyway (registering new redirect
addresses at Google and Discord, pointing the domain at the service). A move button would only hide that. So the
address is the operator's, changed with a redeploy, and the code covers what a move breaks: old links, passkeys, and
the people who only had passkeys.

## What the code does

### The old address redirects pages

`Blog__PreviousUrls`: the old addresses, comma-separated (`https://old.example, https://older.example`). A request whose
`Host` is one of them (and the domain still points at the service) gets:

- **GET or HEAD for a page** (anything but `/api`, `/mcp`, `/oauth`, `/.well-known`): `301` to the **same path and
  query on `Blog__PublicUrl`**. Posts, feeds, author pages, pictures, `/sign-in`, `/admin`, the unsubscribe page: links
  people saved keep working, and search engines move their index. The 301 carries `Cache-Control: public,
  max-age=86400`: a browser keeps it a day, never for good, so a move undone (a wrong `Blog__PreviousUrls`) heals for
  every visitor within a day.
- **Everything else is served as before**, on the old host:
  - a `POST`: the one-click unsubscribe in an email sent before the move is a `POST` to the old address, and a `301`
    would turn it into a `GET` that unsubscribes nobody;
  - `/api`: a cross-site redirect breaks a JSON call; an app still open at the old address keeps its session until it
    reloads (the reload is a page, and goes to the new address);
  - `/mcp`, `/oauth`, `/.well-known`: the MCP issuer is the new address now, so agents connected at the old one
    reconnect: an access token issued before the move is refused at either address, "This access token isn't valid
    here. Connect again." (tested).
- Only the **listed** hosts redirect, never "any host but `PublicUrl`" (a hosted instance answers many). The target is
  built from `Blog__PublicUrl` alone, never from the request, so the redirect can't be pointed anywhere else.

### Passkeys say where they were made

A passkey works only at the host it was made for. WebAuthn's client data records the origin it was made at; `/api/me`
reads it back for each passkey (`made_at`), says which host passkeys are made for now (`passkey_host`), and answers
**`passkey_here: false`** when every passkey of this account was made for another host. The app then shows, on every
screen until it's done: *"This blog moved to new.example. Your passkeys were made for old.example, so they don't work
here."* with **Add a passkey here**. Account → Ways to sign in marks each old passkey "for old.example: it doesn't work
at this address".

### Who can still sign in, and how

| Who | After the move |
| --- | --- |
| Anyone with Google, Discord or the trusted provider ([auth.md](auth.md#a-trusted-provider-openid-connect)) | signs in with it at the new address (once the operator registered the new redirect address at that provider), then is asked for a passkey here |
| The owner, passkeys only | a new `Blog__SetupToken` re-opens setup at the new address; a passkey made there binds to **the same owner**, blog and posts untouched, and ends the owner's old sessions (the existing recovery, [auth.md](auth.md#setup-claiming-the-blog)) |
| A writer or editor, passkeys only | **stranded**: People shows "Can't sign in here" and **Invite to claim**. Their link signs whoever accepts it in **as that account**, posts and all, with a new passkey (or a provider) |

### Claiming a stranded account

The claim invitation already existed for imported authors nobody has signed in as ([import.md](import.md#claiming)).
It now also claims an account stranded by a move. Because it lets whoever holds the link become that account, it is
bounded; an account is **claimable** only when all of these hold, checked when the owner makes the link and again when
it is accepted (the account row locked):

- it is on this blog, and **not its owner** (the owner recovers through setup, which needs the server's token);
- it is on **no other blog** of the instance (claiming it would reach that blog too);
- **nobody has a way in to it at this address**: no identity at all (an imported author), or every identity a passkey
  whose recorded origin is another host. A Google, Discord or trusted-provider identity, a passkey made here, or a
  passkey whose origin can't be read, is a way in: that account is not claimable.

Accepting it: the new passkey is made under **the account's own passkey handle** (so it signs in as that account by
itself), **every session of the account ends** (a stale tab at the old address may not be the person claiming it),
**its agent connections are revoked** (`connection_revoked`, why `account_reclaimed`: a connection made by whoever had
the account isn't the claimer's to keep), and the `invitation_accepted` event carries `reclaimed: true`. The old passkeys stay, unusable; they work again if the blog
moves back.

## The procedure

The guide has it in the owner's words: [guides/moving-your-blog.md](../guides/moving-your-blog.md). In short:

1. **Before:** in People, see who signs in only with passkeys; ask them to add Google, Discord or the trusted provider
   (or plan to send them a claim link afterwards). Add one yourself, or have a setup token ready.
2. Register the new redirect addresses (`<new>/api/auth/google/callback`, `/discord/callback`, `/oidc/callback`) at
   each provider, keeping the old ones until the move is done.
3. Point the new domain at the service, and keep the old one pointing at it too.
4. Set `Blog__PublicUrl=<new>` and `Blog__PreviousUrls=<old>`, and redeploy.
5. Sign in at the new address; add a passkey when asked. Send stranded people their claim links.
6. Reconnect each AI (its connector's address is now `<new>/mcp`).

## Acceptance criteria

- [x] GET and HEAD for a page on a listed old host answer 301 to the same path and query on `Blog__PublicUrl`;
      `/api`, `/mcp`, `/oauth`, `/.well-known`, any POST, and unlisted hosts are not redirected (`AddressMoveTests`).
- [ ] That 301 says how long to keep it (`max-age=86400`); no browser keeps it for good.
- [x] After a move, Discord still signs the owner in; `/api/me` says the passkeys are for the old host and asks for
      one here; a passkey made here clears it and signs in by itself.
- [x] A passkey-only owner recovers at the new address with a new setup token, as the same owner of the same blog.
- [x] A passkey-only writer is shown as stranded, and the owner's claim link makes them the same account again, with
      their posts; their old sessions end and their agents are revoked; the new passkey signs them in as that account.
- [x] An agent connected before the move is refused after it (its token was issued for the old issuer).
- [x] Claiming is refused for an account with a way in here (Discord), the owner, an account on another blog, and a
      link accepted after the account stopped being claimable.
- [ ] As an owner, I move blog-ai.echoofdreams.eu to another address and sign in afterwards (the live pass).
