# Your own sign-in provider

Besides a passkey, Google and Discord, the people on your blog can sign in with an account they have somewhere else:
any service that speaks OpenID Connect (Echo of Dreams, an Authentik or Keycloak you run, your company's). Spec:
[../specs/auth.md § A trusted provider](../specs/auth.md#a-trusted-provider-openid-connect).

## Before you start

- The server needs `Blog__Secrets__MasterKey` (the provider's secret is stored sealed). Without it, the screen says so.
  See [self-host-secrets-and-storage.md](self-host-secrets-and-storage.md).
- On a hosted blog this isn't offered: the sign-in page there is shared by every blog.

## Setting it up

1. In the app, **Account → Sign-in and comments**. The form shows a **redirect address**, ending in
   `/api/auth/oidc/callback`.
2. At the provider, register a new app (it may say "client" or "application"): a web app, with that redirect address,
   asking for the `openid` and `profile` scopes. It gives you a **client id** and a **client secret**, and documents its
   **issuer** (an https address; for some, the one that has `/.well-known/openid-configuration` under it).
3. Back in the app, fill in the issuer, client id, client secret and the **name on the button** ("Echo of Dreams").
   **Save.** The blog asks the provider about itself first; if something doesn't match, it says what.
4. Under **Ways to sign in**, tap **Add Echo of Dreams** (your name for it) and sign in there. From now on you can sign
   in with it.

Your writers can add it the same way, and an invitation can be accepted with it: the invitation page shows its button.

## What to expect

- **An account is the provider's account, not an email.** Someone with the same email at the provider is someone else.
  If you change the issuer to another provider, links made with the old one stop working until you set it back.
- **The secret never comes back.** The form shows "Saved"; type a new one only to replace it.
- **If the provider is down,** nobody is signed out. Signing in with it fails with "The provider didn't answer", and
  every other way in still works.
- **Removing it** takes the button off the sign-in page. Anyone who signed in only with it needs another way in (a
  passkey, or a new invitation). Their link comes back if you set the same issuer again.

## When it says no

| What the app says | What to check |
| --- | --- |
| The issuer's discovery document answered 404 | the issuer address: exactly what the provider documents |
| The provider's discovery document names another issuer | a trailing slash, `http` vs `https`, or a path that differs |
| The issuer isn't reachable from here | the provider must be on the public internet (a self-hoster can set `Blog__Auth__Oidc__AllowPrivate=true`) |
| The provider didn't confirm the sign-in | usually the client secret, or the redirect address registered at the provider |
| The provider's answer was meant for another app | the client id |
| The provider's answer had expired | the server's clock |
| That account isn't on this blog | the account isn't linked yet: add it under Ways to sign in, or accept an invitation with it |
