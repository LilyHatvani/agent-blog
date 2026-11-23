# Railway template: what "Deploy on Railway" needs

Status: **ready to build** (2026-11-23) · **Waits on:** the first release and a public package
([release.md](release.md)), then the owner builds it in Railway's template composer (no API makes one).

## Services

| Service | Source | Settings |
| --- | --- | --- |
| `Postgres` | Railway's Postgres | defaults, with a volume |
| `media` | a Railway bucket, or none when the owner brings Cloudflare R2 (recommended for price: no egress) | defaults; pictures are off without one |
| `Blog` | image `ghcr.io/lilyhatvani/agent-blog:latest` | healthcheck `/healthz`; a generated public domain; 1 replica |

## Variables on `Blog`

| Variable | Template value | Why |
| --- | --- | --- |
| `DATABASE_URL` | `${{Postgres.DATABASE_URL}}` | the schema owner's connection; the server migrates with it |
| `Blog__PublicUrl` | `https://${{RAILWAY_PUBLIC_DOMAIN}}` | canonical links, feeds, the OAuth issuer |
| `Blog__SetupToken` | `${{secret(64, "abcdef0123456789")}}` | claims the blog for its owner on first sign-in |
| `Blog__Secrets__MasterKey` | `${{secret(64, "abcdef0123456789")}}` | encrypts stored credentials; not in any backup |
| `Blog__Bucket__endpoint`, `__name`, `__access_key`, `__secret_key`, `__region` | references to the bucket's variables | media, by reference so a credential reset needs no edit |
| `Blog__Bucket__url_style` | `virtual` | Railway's buckets answer only at `<bucket>.<endpoint host>` |
| `Blog__Auth__Google__*`, `Blog__Auth__Discord__*` | empty; the deployer fills one pair | owner sign-in providers (setup needs one) |

**Never** hard-code a credential in the template. Every secret comes from `secret()`.

## Listing

| Field | Text |
| --- | --- |
| Name | Agent Blog (working name) |
| Short description | A blog you write with your AI: it drafts over MCP, you publish from your phone. |
| Category | Blogs |

## Building it (the owner, once)

1. In Railway: **New → Template** (the template composer).
2. Add **Postgres** (Database → PostgreSQL). Keep its volume.
3. Optionally add a **Bucket**; leave it out when the template recommends bringing Cloudflare R2.
4. Add **Docker image** `ghcr.io/lilyhatvani/agent-blog:latest`, name it `Blog`. In its settings: healthcheck path
   `/healthz`, **Generate domain** on port 8080, 1 replica.
5. On `Blog`, set the variables from the table above, exactly as written: the `${{…}}` references are filled at deploy,
   so nothing is typed by the person deploying. Describe the `Blog__Auth__*` ones as "Google or Discord sign-in for
   you, the owner: fill in one pair" (setup needs one, see the gap below).
6. Fill the listing, **Create**, then **Publish** it to the marketplace if you want it listed (the button works either
   way). Copy the template's code for the README button.
7. Test from a fresh Railway account: deploy, wait for green, open `/setup`, paste the setup token from the `Blog`
   service's variables, sign in (M4's second line).

## The gap: "filling nothing in"

Setup is the setup token **plus** a Google or Discord sign-in (`Auth/SignIn.cs`), and each needs an OAuth client the
deployer makes at Google or Discord and pastes in. So today a deploy goes green with nothing filled in, but nobody can
claim it until one pair is set, and M4's "fills nothing in and signs in" doesn't hold. Closing it needs an owner
sign-in that depends on no third party, a passkey (WebAuthn) being the candidate: the setup token, then a passkey on
the phone. That is a change to how the owner signs in, so it's the owner's call before it's built.

## The README button

```markdown
[![Deploy on Railway](https://railway.com/button.svg)](https://railway.com/deploy/<template code>)
```
