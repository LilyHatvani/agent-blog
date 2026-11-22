# Railway template: what "Deploy on Railway" needs

Status: draft · **Waits on:** M1 and a published image (0004)

## Services

| Service | Source | Settings |
| --- | --- | --- |
| `Postgres` | Railway's Postgres | defaults, with a volume |
| `media` | a Railway bucket, or none when the owner brings Cloudflare R2 (recommended for price: no egress) | defaults; pictures are off without one |
| `Blog` | image `ghcr.io/<owner>/agent-blog:latest` | healthcheck `/healthz`; a generated public domain; 1 replica |

## Variables on `Blog`

| Variable | Template value | Why |
| --- | --- | --- |
| `DATABASE_URL` | `${{Postgres.DATABASE_URL}}` | the schema owner's connection; the server migrates with it |
| `Blog__PublicUrl` | `https://${{RAILWAY_PUBLIC_DOMAIN}}` | canonical links, feeds, the OAuth issuer |
| `Blog__SetupToken` | `${{secret(64, "abcdef0123456789")}}` | claims the blog for its owner on first sign-in |
| `Blog__Secrets__MasterKey` | `${{secret(64, "abcdef0123456789")}}` | encrypts stored credentials; not in any backup |
| `Blog__Bucket__endpoint`, `__name`, `__access_key`, `__secret_key`, `__region` | references to the bucket's variables | media, by reference so a credential reset needs no edit |
| `Blog__Bucket__url_style` | `virtual` | Railway's buckets answer only at `<bucket>.<endpoint host>` |
| `Blog__Auth__Google__*`, `Blog__Auth__Discord__*` | empty, described as optional | owner sign-in providers |

**Never** hard-code a credential in the template. Every secret comes from `secret()`.

## Listing

| Field | Text |
| --- | --- |
| Name | Agent Blog (working name) |
| Short description | A blog you write with your AI: it drafts over MCP, you publish from your phone. |
| Category | Blogs |
