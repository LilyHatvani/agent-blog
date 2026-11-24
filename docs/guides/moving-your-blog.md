# Moving your blog to a new address

Your blog's address is a server setting, `Blog__PublicUrl`. Moving it is a redeploy with two settings, done in an order
that keeps everyone able to sign in. Spec: [../specs/address-move.md](../specs/address-move.md). For a hosted blog, the
address is the platform's; this guide is for a blog you run yourself.

## What changes, and what doesn't

- **Links keep working.** Every page at the old address answers with a permanent redirect to the same page at the new
  one: posts, feeds, author pages, pictures. Search engines follow it.
- **Google, Discord and your own provider keep working**, once each knows the new address (step 2).
- **Passkeys don't move.** A passkey belongs to the address it was made at. After the move, the app asks everyone to
  make one at the new address; it takes a few seconds once you're signed in some other way.
- **Your AI reconnects.** Its connector's address becomes `<new address>/mcp`.

## Before you move

1. **Account → People.** For each person, think: do they have a way in that isn't a passkey (Google, Discord, your own
   provider)? Ask the ones who don't to add one under Account → Ways to sign in. Anyone you miss can still get back:
   see "Someone can't sign in" below.
2. **Yourself:** add Google, Discord or your provider too, or be ready to set a new setup token (your way back in if
   you only have passkeys).
3. **At each provider you use**, add the new redirect address next to the old one:
   - Google: `<new address>/api/auth/google/callback`
   - Discord: `<new address>/api/auth/discord/callback`
   - your own provider: `<new address>/api/auth/oidc/callback` (Account → Sign-in and comments shows it after the move)

## The move

4. Point the new domain at the service (on Railway: add it as a custom domain of the blog's service). **Keep the old
   domain pointing at it too**: that's what answers the old links with redirects.
5. In the service's variables:
   - `Blog__PublicUrl` = the new address, `https://new.example`
   - `Blog__PreviousUrls` = the old one, `https://old.example` (several, comma-separated, if it moved before)
6. Redeploy.

## After

7. Open the new address and sign in with Google, Discord or your provider. The app says your passkeys were made for the
   old address: tap **Add a passkey here**.
   - **Only had passkeys?** Set a new `Blog__SetupToken` (32+ characters), redeploy, open `/setup` at the new address,
     paste it and make a passkey. You're back as the same owner, with everything as it was. Remove the token afterwards
     if you like; it can't be used twice.
8. Reconnect your AI: in claude.ai (or your client), change the connector's address to `<new address>/mcp` and connect
   again.
9. When everything works, you can remove the old redirect addresses from your providers.

## Someone can't sign in

People shows them with **"Can't sign in here: they only have passkeys made for the blog's old address"** and an **Invite
to claim** button. Send them the link. Whoever opens it and makes a passkey (or signs in with a provider) **becomes that
account**: same name, same posts, same role. Their old sessions end, and so do the AI connections the account had (they
connect their AI again). The link works once, for a week.

It's offered only for someone who has no way in at the new address and writes for no other blog on this server. For
anyone else, invite them as usual.

## Moving back

Set `Blog__PublicUrl` back and swap the two addresses in `Blog__PreviousUrls`. Old passkeys work again at their old
address.
