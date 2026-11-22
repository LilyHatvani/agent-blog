# Data model

Status: **M1, slice 1** (2026-11-22). The canon for tables, states and actors. Migrations implement it; a change here
comes before a change there.

## Tenancy: every row belongs to one blog

Every table but `account`, `account_identity` and `schema_migrations` has `blog_id`
([0005](../decisions/0005-hosted-and-self-hosted.md)). **The database enforces it, not only the code:** each
blog-scoped table has `UNIQUE (blog_id, id)`, and every reference between blog-scoped rows is a composite foreign key
`(blog_id, x_id) → (blog_id, id)`. A revision of blog A can't point at a post of blog B even if the code asks it to.

## Actors

Who did something is a text value, the same in every table and in events:

| Actor | Form | Who |
| --- | --- | --- |
| account | `account:<uuid>` | a person signed in (the blog's owner) |
| connection | `connection:<uuid>` | an agent, through one connection |
| system | `system` | the server itself (a schedule firing) |

## Tables (slice 1)

| Table | What | Notes |
| --- | --- | --- |
| `account` | a person | display name; no email kept |
| `account_identity` | a sign-in method | `(provider, provider_account_id)` unique (`AgentKit.ProviderSignIn`: matched by account id, never email) |
| `blog` | one blog | `slug` (the hosted subdomain) unique; `hidden_at` for 0005's hidden trials; never deleted by a rule |
| `blog_member` | who runs a blog | `role` is `owner` in v1 (the seam for several authors) |
| `connection` | one agent connection | `level` `propose` \| `publish` \| `full`, default `propose` ([0001](../decisions/0001-agent-autonomy-levels.md)); `oauth_authorization_id` links the `AgentKit.McpOAuth` grant; `revoked_at` |
| `post` | a post's current state | `status` `draft` \| `review` \| `scheduled` \| `live` \| `trash`; `current_revision_id`; `publish_at` and `scheduled_by` (an actor) for schedules; `trashed_at`, `trashed_by`; `slug` unique per blog |
| `post_revision` | one save | number per post, title, Markdown body, excerpt, `actor`; **never updated or deleted** by the app role |
| `event` | append-only log | `blog_id`, target, kind, actor, payload; **never updated or deleted** by the app role |

Coming in later slices: `category`, `tag`, `post_category`, `post_tag`, `media`, `approval_request`, `theme`.

## Roles

The schema is migrated by the database owner. The app serves as its own role with `SELECT, INSERT, UPDATE` and no
`UPDATE, DELETE, TRUNCATE` on `event` and `post_revision` (Relay's pattern). The few tables that hold transient rows
get `DELETE` by name. Nothing else is deleted by the app: a post leaves through the trash, and only the owner's
"empty trash" (a later slice) removes it, through a function that writes the event first.
